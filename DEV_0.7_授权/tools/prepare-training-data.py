"""Validate exported CAD samples and derive reviewable training labels.

Never removes CAD watermarks, edits source documents, or admits unreviewed data.
Requires numpy and pypdf; --poppler adds full-page PNG / vector SVG companions.
"""
import argparse
import hashlib
import json
import math
import re
from pathlib import Path
import subprocess
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]/"build"/"training-python"))
import numpy as np
from pypdf import PdfReader


def validate_contract(sample):
    """Independent schema, privacy, topology and native-ID checks for 0.2."""
    errors = []
    if sample.get("schema_version") != "0.2.0":
        return errors
    import jsonschema
    schema = json.loads((Path(__file__).resolve().parents[1]/"training-sample.schema.json").read_text(encoding="utf-8"))
    for error in jsonschema.Draft202012Validator(schema).iter_errors(sample):
        errors.append("schema: " + "/".join(map(str, error.absolute_path)) + ": " + error.validator)
    views={v["id"] for s in sample.get("drawing", {}).get("sheets", []) for v in s.get("views", [])}
    curves={c["id"] for s in sample.get("drawing", {}).get("sheets", []) for v in s.get("views", []) for kind in ("lines","arcs","circles") for c in (v.get(kind) or [])}
    geometry={e["id"] for m in sample.get("models", []) for b in m.get("bodies", []) for kind in ("faces","edges") for e in b.get(kind, [])}
    def walk(value, path=""):
        if isinstance(value, dict):
            for key, child in value.items():
                if isinstance(child, str) and re.search(r"[A-Za-z]:[\\/]|\\\\[^\\]+\\", child):
                    errors.append("privacy absolute path: " + path + "/" + key)
                if key in ("view_id", "parent_view", "child_view") and child is not None and child not in views:
                    errors.append("dangling native view ID: " + path)
                if key == "drawing_geometry_id" and child is not None and child not in curves:
                    errors.append("dangling drawing curve ID: " + path)
                if key == "native_geometry" and child is not None and child.get("id") not in geometry:
                    errors.append("dangling native geometry ID: " + path)
                walk(child, path+"/"+key)
        elif isinstance(value, list):
            for child in value: walk(child, path)
        elif isinstance(value, float) and not math.isfinite(value):
            errors.append("non-finite native number: " + path)
    walk(sample)
    for model in sample.get("models", []):
        for body in model.get("bodies", []):
            fs={f["id"]: f for f in body["faces"]}; es={e["id"]: e for e in body["edges"]}
            if len(fs)!=len(body["faces"]) or len(es)!=len(body["edges"]): errors.append("duplicate topology ID: "+body["id"])
            for fid, face in fs.items():
                for eid in face.get("edge_ids") or []:
                    if eid not in es or fid not in (es[eid].get("adjacent_face_ids") or []): errors.append("asymmetric topology: "+fid)
            for eid, edge in es.items():
                for fid in edge.get("adjacent_face_ids") or []:
                    if fid not in fs or eid not in (fs[fid].get("edge_ids") or []): errors.append("asymmetric topology: "+eid)
            mesh=body.get("mesh") or {}; mapping=mesh.get("triangle_face_ids") or []
            if len(mapping)!=mesh.get("triangle_count") or any(fid not in fs for fid in mapping): errors.append("invalid native triangle map: "+body["id"])
            if mesh.get("failed_face_ids"): errors.append("native face tessellation incomplete: "+body["id"])
    return errors


def dump(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")


def contained(root, relative):
    target = (root / relative).resolve()
    if not target.is_relative_to(root.resolve()):
        raise ValueError(f"Unsafe artifact path: {relative}")
    return target


def derive_view(view, models):
    result = {"id": view["id"], "model_id": view.get("model_id"), "matrix_status": "unavailable"}
    model = models.get(view.get("model_id"))
    if not model or model.get("kind") not in ("part", "sheet_metal") or not view.get("orientation_native"):
        return result
    ori = view["orientation_native"]
    x = np.asarray(ori["local_x_direction"], dtype=float)
    n = np.asarray(ori["view_direction"], dtype=float)
    y = np.cross(n, x)
    # DV geometry lives in unscaled view coordinates, not paper coordinates.
    linear = np.stack([x, y])
    if not np.allclose([np.linalg.norm(x), np.linalg.norm(n), np.dot(x, n)], [1, 1, 0], atol=1e-7):
        return result
    edges = {e.get("reference_key"): e for b in model.get("bodies", []) for e in b.get("edges", [])}
    pairs, links = [], []
    for line in view.get("lines") or []:
        ref = line.get("native_reference") or {}
        edge = edges.get((ref.get("bound_edge") or {}).get("canonical_reference_key", ref.get("reference_key")))
        if not edge or not edge.get("endpoints_m") or edge.get("curve_type") not in ("igLine", "167551109"):
            continue
        p = np.asarray(edge["endpoints_m"])
        q = np.asarray([line["start_native_m"], line["end_native_m"]])
        if np.linalg.norm(p[0] - p[1]) < 1e-9:
            continue
        pairs.append((p, q))
        links.append({"drawing_curve_id": line["id"], "model_edge_id": edge["id"]})
    result["model_edge_links"] = links
    if len(pairs) < 3:
        return result
    offsets = np.array([q.mean(axis=0) - linear @ p.mean(axis=0) for p, q in pairs])
    offset = np.median(offsets, axis=0)
    errors = []
    for p, q in pairs:
        projected = p @ linear.T + offset
        errors.append(min(np.max(np.linalg.norm(projected-q, axis=1)), np.max(np.linalg.norm(projected-q[::-1], axis=1))))
    max_error = float(max(errors))
    result.update(verified_line_count=len(pairs), max_endpoint_error_m=max_error)
    # A single affine map cannot describe a broken view. Require every matched
    # model line to agree, plus two-dimensional spread; do not discard outliers.
    spread = np.concatenate([q for p, q in pairs])
    if max_error <= 1e-6 and np.linalg.matrix_rank(spread-spread.mean(axis=0), tol=1e-8) == 2:
        model_to_view = np.column_stack([linear, offset])
        result.update(matrix_status="verified_against_model_edges", model_to_view_affine=model_to_view.tolist())
        if view.get("view_to_sheet_affine"):
            paper = np.asarray(view["view_to_sheet_affine"])
            result["model_to_sheet_affine"] = (paper @ np.vstack([model_to_view, [0, 0, 0, 1]])).tolist()
    else:
        result["matrix_status"] = "rejected_geometry_mismatch"
    return result


def process(manifest, poppler):
    root = manifest.parent
    sample = json.loads(manifest.read_text(encoding="utf-8-sig"))
    errors, warnings = validate_contract(sample), []
    for item in sample.get("files", []):
        path = contained(root, item["path"])
        if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]:
            errors.append("Artifact missing or hash mismatch: " + item["path"])
    models = {m["id"]: m for m in sample.get("models", [])}
    mesh_count = triangle_count = 0
    for m in models.values():
        if m.get("kind") not in ("part", "sheet_metal", "assembly") or not m.get("native_file"):
            errors.append("Incomplete model extraction: " + m["id"])
        if m.get("kind") in ("part", "sheet_metal") and not m.get("bodies"):
            errors.append("Model contains no extracted body: " + m["id"])
        for body in m.get("bodies", []):
            mesh = body.get("mesh")
            if not mesh:
                errors.append("Missing mesh: " + body["id"])
                continue
            path = contained(root, mesh["file"])
            vertices, faces, obj_groups = [], [], []
            group = None
            for line in path.read_text().splitlines():
                fields = line.split()
                if fields and fields[0] == "v": vertices.append([float(v) for v in fields[1:]])
                if fields and fields[0] == "g": group = fields[1]
                if fields and fields[0] == "f":
                    faces.append([int(v) for v in fields[1:]])
                    obj_groups.append(group)
            if mesh.get("triangle_face_ids") is not None and obj_groups != [fid.replace("/", "_") for fid in mesh["triangle_face_ids"]]:
                errors.append("OBJ group differs from native triangle map: " + body["id"])
            points = np.array(vertices)
            valid = (len(faces) == mesh["triangle_count"] and points.ndim == 2 and points.shape[1] == 3
                     and np.isfinite(points).all() and all(len(f) == 3 and min(f) >= 1 and max(f) <= len(points) for f in faces))
            if not valid:
                errors.append("Invalid mesh: " + body["id"])
                continue
            if body.get("aabb_m") and not np.allclose([points.min(axis=0), points.max(axis=0)], body["aabb_m"], atol=mesh["chord_tolerance_m"]*2, rtol=0):
                errors.append("Mesh bounds disagree with native CAD: " + body["id"])
            mesh_count += 1
            triangle_count += len(faces)
    sheets = sample.get("drawing", {}).get("sheets", [])
    working = [s for s in sheets if s.get("section_type") == "igWorkingSection"]
    derived = [derive_view(v, models) for s in working for v in s.get("views", [])]
    pdf_relative = sample.get("drawing", {}).get("pdf_file")
    pages = []
    if pdf_relative:
        pdf = contained(root, pdf_relative)
        reader = PdfReader(pdf)
        if len(reader.pages) != len(working):
            errors.append("PDF page count differs from working sheet count; page-to-sheet mapping requires review")
        for i, page in enumerate(reader.pages, 1):
            text = page.extract_text() or ""
            info = {"page": i, "media_box_pt": list(map(float, page.mediabox)), "rotation_deg": page.rotation,
                    "text": None, "text_status": "redacted", "text_sha256": hashlib.sha256(text.encode('utf-8')).hexdigest()}
            if poppler:
                prefix = root / "derived" / f"page-{i:03d}"
                prefix.parent.mkdir(exist_ok=True)
                subprocess.run([str(poppler/"pdftoppm.exe"), "-f", str(i), "-l", str(i), "-r", "150", "-singlefile", "-png", str(pdf), str(prefix)], check=True, capture_output=True)
                info.update(png=f"derived/page-{i:03d}.png", dpi=150)
                try:
                    import pymupdf
                    with pymupdf.open(pdf) as vector_doc:
                        prefix.with_suffix('.svg').write_text(vector_doc[i-1].get_svg_image(text_as_path=True), encoding="utf-8")
                    info["svg"] = f"derived/page-{i:03d}.svg"
                except ImportError:
                    warnings.append("PyMuPDF unavailable; SVG companion omitted")
            pages.append(info)
        warnings.append("Review PDF watermarks, title block property errors, and page-to-sheet order; no image cleaning applied")
    elif sample.get("task") != "model_only":
        errors.append("No standard PDF companion")
    if not models:
        errors.append("No paired model exported")
    if mesh_count == 0:
        errors.append("No usable 3D mesh exported")
    if sample.get("source_integrity_verified") is not True:
        errors.append("Source integrity did not pass")
    result = {"schema": "tiangong.training-derived", "schema_version": sample.get("schema_version"), "manifest": "sample.json", "training_ready": False,
              "errors": errors, "warnings": warnings, "mesh_count": mesh_count, "triangle_count": triangle_count,
              "views": derived, "pdf_pages": pages, "native_issue_count": len(sample.get("issues", [])),
              "review_required": ["missing annotations and primitive classes", "watermarks and property errors", "model family / near-duplicate grouping", "geometric and dimension correctness"]}
    dump(root/"validation.json", result)
    dump(root/"training-labels.json", {"schema": "tiangong.training-labels", "schema_version": sample.get("schema_version"), "views": derived, "sheets": working, "models": list(models.values()), "length_unit": "m", "angle_unit": "rad", "training_ready": False})
    summary_path = root / "export_summary.json"
    if summary_path.exists():
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
        summary["valid_transforms"] = sum(v["matrix_status"] == "verified_against_model_edges" for v in derived)
        summary["artifact_validation_errors"] = errors
        dump(summary_path, summary)
    return {"sample": root.name, "source": sample.get("source_name") or sample["source_sha256"][:16], "source_sha256": sample["source_sha256"],
            "geometry_split_descriptors": sorted({m["dataset_metadata"]["normalized_geometry_hash"] for m in models.values() if m.get("dataset_metadata", {}).get("normalized_geometry_hash")}),
            "model_hashes": sample.get("split_group_model_hashes", []), "training_ready": False,
            "errors": errors, "mesh_count": mesh_count, "triangle_count": triangle_count,
            "pdf_pages": len(pages), "views": len(derived),
            "verified_projections": sum(v["matrix_status"] == "verified_against_model_edges" for v in derived),
            "dimensions": sum(len(s.get("dimensions", [])) for s in working), "native_issue_count": result["native_issue_count"]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--poppler", type=Path)
    args = parser.parse_args()
    manifests = sorted(args.directory.glob("sample-*/sample.json"))
    if not manifests:
        raise SystemExit("No sample-*/sample.json found")
    results = []
    for manifest in manifests:
        try:
            row = process(manifest, args.poppler)
        except Exception as e:
            row = {"sample": manifest.parent.name, "training_ready": False, "errors": [type(e).__name__], "model_hashes": []}
        results.append(row)
    # Connected components prevent a shared part leaking through two assemblies.
    parents = list(range(len(results)))
    def find(i):
        while parents[i] != i:
            parents[i] = parents[parents[i]]
            i = parents[i]
        return i
    seen = {}
    for i, row in enumerate(results):
        for key in row["model_hashes"] + row.get("geometry_split_descriptors", []) + [row.get("source_sha256", "missing:"+str(i))]:
            if key in seen: parents[find(i)] = find(seen[key])
            seen[key] = i
    for i, row in enumerate(results): row["split_group"] = f"group-{find(i):05d}"
    dump(args.directory/"dataset-validation.json", results)
    with (args.directory/"index.jsonl").open("w", encoding="utf-8") as f:
        for row in results: f.write(json.dumps(row, ensure_ascii=False, allow_nan=False)+"\n")
    print(json.dumps(results, ensure_ascii=False, indent=2))
    return 1 if any(r["errors"] for r in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
