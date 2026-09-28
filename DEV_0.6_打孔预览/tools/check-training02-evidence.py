"""Check real exported evidence plus rejection of corrupted supervision IDs."""
import argparse, copy, importlib.util, json, math
from pathlib import Path

p=argparse.ArgumentParser(); p.add_argument("directories", nargs="+", type=Path); p.add_argument("--output", type=Path, required=True); args=p.parse_args()
spec=importlib.util.spec_from_file_location("training_prepare", Path(__file__).with_name("prepare-training-data.py")); module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
samples=[]
for directory in args.directories:
    for file in directory.glob("sample-*/sample.json"):
        sample=json.loads(file.read_text(encoding="utf-8-sig"))
        if sample.get("models") and sample.get("extraction_status")!="failed": samples.append((file, sample))
checks=[]
def check(ok, name):
    checks.append({"check":name,"passed":bool(ok)})
    if not ok: raise AssertionError(name)
for file,sample in samples:
    check(not module.validate_contract(sample), "schema_privacy_native_ids:"+file.parent.name)
    check(sample["source_integrity_verified"], "source_hash:"+file.parent.name)
    for model in sample["models"]:
        for body in model.get("bodies", []):
            check(len(body["mesh"]["triangle_face_ids"])==body["mesh"]["triangle_count"] and not body["mesh"]["failed_face_ids"], "native_mesh:"+file.parent.name+":"+body["id"])
        for feature in (model.get("feature_tree") or {}).get("features", []):
            for collection in ("faces","edges"):
                allowed={x["id"] for body in model.get("bodies", []) for x in body[collection]}
                check(set(feature.get(collection[:-1]+"_ids") or []) <= allowed, "feature_"+collection+":"+feature["id"])
base=next(s for _,s in samples if s.get("drawing") and any(m.get("bodies") for m in s["models"]))
bad=copy.deepcopy(base); next(b for m in bad["models"] for b in m.get("bodies", []))["mesh"]["triangle_face_ids"][0]="foreign-face"
check(any("triangle map" in e for e in module.validate_contract(bad)), "reject_foreign_triangle_face")
bad=copy.deepcopy(base); bad["private_debug_author"]="C:\\Users\\TEST_PRIVATE\\project"
check(any("privacy absolute path" in e for e in module.validate_contract(bad)), "reject_absolute_private_path")
bad=copy.deepcopy(base); bad["injected_reference"]={"view_id":"sheet-999/view-999"}
check(any("dangling native view" in e for e in module.validate_contract(bad)), "reject_internal_sheet_as_working_sheet")
bad=copy.deepcopy(base); bad["injected_reference"]={"drawing_geometry_id":"missing-curve"}
check(any("dangling drawing curve" in e for e in module.validate_contract(bad)), "reject_foreign_drawing_curve")
holes=[f["hole_parameters"] for _,s in samples for m in s["models"] for f in (m.get("feature_tree") or {}).get("features", []) if f.get("hole_parameters")]
regular=next(h for h in holes if h["hole_type"]=="igRegularHole")
check(math.isclose(regular["diameter_m"],.008,abs_tol=1e-12), "native_fixture_8mm_hole")
check(regular["counterbore_diameter_m"] is None and regular["countersink_angle_rad"] is None, "inactive_hole_defaults_not_training_targets")
sink=next(h for h in holes if h["hole_type"]=="igCountersinkHole")
check(math.isclose(sink["countersink_angle_rad"],math.pi/2,abs_tol=1e-12), "native_fixture_90deg_to_rad")
bore=next(h for h in holes if h["hole_type"]=="igCounterboreHole")
check(math.isclose(bore["counterbore_depth_m"],.004,abs_tol=1e-12), "native_fixture_4mm_counterbore_depth")
section_pairs=[v for _,s in samples for sheet in s.get("drawing",{}).get("sheets",[]) for v in sheet.get("views",[]) if (v.get("section_data") or {}).get("type") in ("section","detail")]
check(len(section_pairs)>=2 and all(v["section_data"]["parent_view"] for v in section_pairs), "native_section_and_detail_parent_links")
args.output.parent.mkdir(parents=True,exist_ok=True)
args.output.write_text(json.dumps({"status":"passed","samples":len(samples),"checks":checks},ensure_ascii=False,indent=2),encoding="utf-8")
print(json.dumps({"samples":len(samples),"checks":len(checks),"status":"passed"}))
