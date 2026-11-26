using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using A = SolidEdgeAssembly;
using D = SolidEdgeDraft;
using F = SolidEdgeFramework;
using G = SolidEdgeGeometry;
using P = SolidEdgePart;

namespace TianGongCadSuite {
    // 快速出清单的 CAD 交互层：
    //   1. 从当前装配 / 工程图 / 零件或指定文件夹收集加工件，识别焊接件子装配，配对同名 .dft；
    //   2. 进程内经 SolidEdge.FileProperties 毫秒级读取属性卡（兼容 .asm 的「零件名」「材料」与 .par 的「零件名称」「Material」）；
    //   3. 导出每个零件的 6 种格式：
    //      · 原三维（.par/.psm/.asm）与 .dft 经非白名单进程 SuiteRenameWorker.exe --copy 复制，保持公司透明加密头不解密；
    //      · .step 经三维文档 SaveCopyAs 导出；
    //      · .dwg / .pdf 经工程图 SaveCopyAs 导出；
    //      · .jpg 经工程图 SheetWindow.SaveAsImage 导出（实测 DraftDocument.SaveCopyAs(".jpg") 写出的是 OLE 复合文档而非真 JPEG）。
    //   4. 可选离屏渲染三维等轴测缩略图 PNG（复用 PreviewMesh + MeshView），写进《加工件清单.xlsx》。
    public sealed class BomCad {
        readonly F.Application app;
        public readonly List<string> SkippedSubs = new List<string>();

        public BomCad(F.Application application) {
            app = application;
        }

        // ---------- 收集 ----------

        public List<BomItem> CollectFromActive(out string source, out string sourcePath, Action<string> pulse) {
            source = "没有打开的文档";
            sourcePath = "";
            SkippedSubs.Clear();
            var items = new List<BomItem>();
            if (app == null) return items;

            object doc = null;
            try { doc = app.ActiveDocument; } catch { }
            if (doc == null) return items;

            string rootFile = SafeFullName(doc);
            sourcePath = rootFile;

            var asm = doc as A.AssemblyDocument;
            if (asm != null) {
                source = "装配「" + Path.GetFileName(rootFile) + "」";
                return CollectAssemblyTree(asm, rootFile, pulse);
            }

            var dft = doc as D.DraftDocument;
            if (dft != null) {
                source = "工程图「" + Path.GetFileName(rootFile) + "」";
                var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try {
                    var links = dft.ModelLinks;
                    for (int i = 1; i <= links.Count; i++) {
                        var link = links.Item(i);
                        string mf = ""; try { mf = link.FileName; } catch { }
                        if (string.IsNullOrEmpty(mf)) continue;
                        try { mf = Path.GetFullPath(mf); } catch { }
                        if (!seenLinks.Add(mf)) continue;
                        A.AssemblyDocument subAsm = null;
                        try { subAsm = link.ModelDocument as A.AssemblyDocument; } catch { }
                        string draftForModel = BomRules.PairDraftPath(mf);
                        if (string.IsNullOrEmpty(draftForModel)) draftForModel = rootFile;
                        if (subAsm != null) {
                            // 统一走递归装配树收集：既能逐层展开中文命名子装配里的全部下层加工件，又能准确识别真正的叶级焊接件并关联当前工程图
                            var subItems = CollectAssemblyTree(subAsm, mf, pulse, draftForModel);
                            foreach (var si in subItems) if (seenModels.Add(si.ModelPath)) items.Add(si);
                        } else if (seenModels.Add(mf)) {
                            object mdoc = null; try { mdoc = link.ModelDocument; } catch { }
                            var it = BuildItem(mf, draftForModel, 1, true, "", mdoc);
                            if (mdoc != null) TryReadOpenMetrics(mdoc, it);
                            ApplyAutoCategory(it);
                            items.Add(it);
                        }
                    }
                } catch (Exception e) { Log.Write("BomCollectDraft", e); }
                return items;
            }

            if (BomRules.IsModelExtension(rootFile)) {
                source = "当前模型「" + Path.GetFileName(rootFile) + "」";
                string dftPath = BomRules.PairDraftPath(rootFile);
                var it = BuildItem(rootFile, dftPath, 1, true, "", doc);
                TryReadOpenMetrics(doc, it);
                ApplyAutoCategory(it);
                items.Add(it);
            }
            return items;
        }

        List<BomItem> CollectAssemblyTree(A.AssemblyDocument rootAsm, string rootFile, Action<string> pulse, string rootDraftOverride = "") {
            try { if (!string.IsNullOrEmpty(rootFile)) rootFile = Path.GetFullPath(rootFile); } catch { }
            var order = new List<string>();
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var walkedAsms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(rootFile)) walkedAsms.Add(rootFile);
            var childrenOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var loadedDocs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            loadedDocs[rootFile] = rootAsm;

            WalkAssembly(rootAsm, rootFile, 0, discovered, walkedAsms, order, childrenOf, loadedDocs, pulse);
            // 按 DAG 递归累乘父级实例数，保证多实例子装配下层零件数量 = Σ(父级倍率 × 局部实例数)
            var counts = BomRules.ComputeAssemblyCounts(rootFile, childrenOf);

            // 识别叶级焊接件（本身有同名 .dft，且下面没有带公司图号/工程图的独立加工件）
            var topProject = BomRules.ExtractProjectCode(Path.GetFileNameWithoutExtension(rootFile));
            if (topProject.Length == 0) topProject = BomRules.PickTopProject(order);

            var weldmentRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var insideWeldment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in order) {
                if (!f.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) continue;
                string dft = BomRules.PairDraftPath(f);
                if (dft.Length == 0 && !BomRules.HasCompanyDrawingNumber(Path.GetFileNameWithoutExtension(f))) continue;
                if (!HasCustomChild(f, childrenOf, new HashSet<string>(StringComparer.OrdinalIgnoreCase))) {
                    weldmentRoots.Add(f);
                    MarkDescendants(f, Path.GetFileNameWithoutExtension(f), childrenOf, insideWeldment, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
            }

            var items = new List<BomItem>();
            // 根装配本身：若本身就是焊接件（无独立子加工件且有图号/工程图）则默认勾选；
            // 若是总装配（下面有独立子加工件）也收进表里（默认不勾），方便在三合一工作台里同时改总装配的属性卡或文件名。
            bool rootHasCustomChild = HasCustomChild(rootFile, childrenOf, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string rootDraft = BomRules.PairDraftPath(rootFile);
            if (rootDraft.Length == 0 && !string.IsNullOrEmpty(rootDraftOverride)) rootDraft = rootDraftOverride;
            bool rootIsWeld = !rootHasCustomChild && (rootDraft.Length > 0 || BomRules.HasCompanyDrawingNumber(Path.GetFileNameWithoutExtension(rootFile)));
            if (rootIsWeld) {
                // 根装配本身就是叶级焊接件时，把其下属无图号子零件也标为属于该焊接件，避免重复勾选
                MarkDescendants(rootFile, Path.GetFileNameWithoutExtension(rootFile), childrenOf, insideWeldment, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(rootFile) && File.Exists(rootFile)) {
                var rootItem = BuildItem(rootFile, rootDraft, 1, rootIsWeld, rootIsWeld ? "" : "顶层总装配", rootAsm);
                TryReadOpenMetrics(rootAsm, rootItem);
                ApplyAutoCategory(rootItem);
                items.Add(rootItem);
            }

            for (int i = 0; i < order.Count; i++) {
                string f = order[i];
                if (pulse != null) pulse("读取属性 " + (i + 1) + "/" + order.Count + "：" + Path.GetFileName(f));
                string dft = BomRules.PairDraftPath(f);
                int occCount = counts.ContainsKey(f) ? counts[f] : 1;
                bool selected;
                string skipReason = "";

                if (weldmentRoots.Contains(f)) {
                    selected = true;
                } else if (f.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) {
                    selected = false;
                    skipReason = "父级机构装配";
                } else if (insideWeldment.ContainsKey(f)) {
                    // 焊接件内部的虚拟/无磁盘文件子件（如天工自动生成的角撑板_1..6）不混进表里
                    if (!File.Exists(f)) continue;
                    selected = false;
                    skipReason = "属于焊接件「" + insideWeldment[f] + "」";
                } else {
                    selected = BomRules.IsCustomBomPart(f, topProject, dft.Length > 0, out skipReason);
                }

                object loaded;
                loadedDocs.TryGetValue(f, out loaded);
                var item = BuildItem(f, dft, occCount, selected, skipReason, loaded);
                if (loaded != null) TryReadOpenMetrics(loaded, item);
                ApplyAutoCategory(item);
                items.Add(item);
            }

            // 已勾选的排前面，同组按图号排序
            return items
                .OrderByDescending(x => x.Selected)
                .ThenBy(x => x.DrawingNo, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        void WalkAssembly(A.AssemblyDocument asm, string asmPath, int depth,
            HashSet<string> discovered, HashSet<string> walkedAsms, List<string> order,
            Dictionary<string, List<string>> childrenOf, Dictionary<string, object> loadedDocs, Action<string> pulse) {
            if (asm == null || depth > 8) return;
            List<string> kids;
            if (!childrenOf.TryGetValue(asmPath, out kids)) { kids = new List<string>(); childrenOf[asmPath] = kids; }

            A.Occurrences occs = null;
            try { occs = Attempt("读装配实例", () => asm.Occurrences); } catch (Exception e) { Log.Write("BomWalkOccs " + asmPath, e); return; }
            int n = 0; try { n = occs.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                A.Occurrence occ = null;
                try { occ = occs.Item(i); } catch { continue; }
                bool hidden = false;
                try { if (!occ.Visible || occ.DisplayAsReference) hidden = true; } catch { }
                if (hidden) continue;

                string f = "";
                try { f = occ.OccurrenceFileName; } catch { }
                if (string.IsNullOrEmpty(f)) continue;
                try { f = Path.GetFullPath(f); } catch { }

                kids.Add(f);
                if (discovered.Add(f)) order.Add(f);

                bool sub = false;
                try { sub = occ.Subassembly; } catch { }
                if (sub && walkedAsms.Add(f)) {
                    if (pulse != null) pulse("展开子装配：" + Path.GetFileName(f));
                    try {
                        var oc = occ;
                        var childDoc = Attempt("读子装配文档", () => oc.OccurrenceDocument) as A.AssemblyDocument;
                        if (childDoc != null) {
                            loadedDocs[f] = childDoc;
                            WalkAssembly(childDoc, f, depth + 1, discovered, walkedAsms, order, childrenOf, loadedDocs, pulse);
                        }
                    } catch (Exception e) {
                        Log.Write("BomWalkSub " + f, e);
                        SkippedSubs.Add(Path.GetFileName(f));
                    }
                }
            }
        }

        static bool HasCustomChild(string asmPath, Dictionary<string, List<string>> childrenOf, HashSet<string> visited) {
            if (!visited.Add(asmPath)) return false;
            List<string> kids;
            if (!childrenOf.TryGetValue(asmPath, out kids)) return false;
            foreach (var k in kids) {
                string dft = BomRules.PairDraftPath(k);
                string name = Path.GetFileNameWithoutExtension(k);
                if (dft.Length > 0 || BomRules.HasCompanyDrawingNumber(name)) return true;
                if (k.EndsWith(".asm", StringComparison.OrdinalIgnoreCase) && HasCustomChild(k, childrenOf, visited)) return true;
            }
            return false;
        }

        static void MarkDescendants(string asmPath, string weldmentName, Dictionary<string, List<string>> childrenOf,
            Dictionary<string, string> insideWeldment, HashSet<string> visited) {
            if (!visited.Add(asmPath)) return;
            List<string> kids;
            if (!childrenOf.TryGetValue(asmPath, out kids)) return;
            foreach (var k in kids) {
                if (!insideWeldment.ContainsKey(k)) insideWeldment[k] = weldmentName;
                if (k.EndsWith(".asm", StringComparison.OrdinalIgnoreCase)) MarkDescendants(k, weldmentName, childrenOf, insideWeldment, visited);
            }
        }

        // 从文件夹扫描模型（自动配对同名 .dft，支持递归遍历子文件夹）
        public List<BomItem> CollectFromFolder(string folder, bool recursive, Action<string> pulse) {
            var items = new List<BomItem>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return items;

            var modelFiles = BomRules.ScanFolderModels(folder, recursive);
            string topProject = BomRules.PickTopProject(modelFiles);

            for (int i = 0; i < modelFiles.Count; i++) {
                string f = modelFiles[i];
                if (pulse != null) pulse("扫描文件夹 " + (i + 1) + "/" + modelFiles.Count + "：" + Path.GetFileName(f));
                string dft = BomRules.PairDraftPath(f);
                string why;
                bool selected = BomRules.IsCustomBomPart(f, topProject, dft.Length > 0, out why);
                var it = BuildItem(f, dft, 1, selected, why, null);
                it.SubFolder = BomRules.RelativeSubFolder(folder, f);
                ApplyAutoCategory(it);
                items.Add(it);
            }

            return items
                .OrderByDescending(x => x.Selected)
                .ThenBy(x => x.DrawingNo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SubFolder, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 手动添加文件（允许直接挑 .par/.psm/.asm 或 .dft）
        public List<BomItem> CollectFromFiles(IEnumerable<string> paths, Action<string> pulse) {
            var items = new List<BomItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = (paths ?? new string[0]).ToList();
            for (int i = 0; i < list.Count; i++) {
                string raw = list[i];
                if (string.IsNullOrEmpty(raw) || !File.Exists(raw)) continue;
                string model = raw;
                string dft = "";
                bool draftOnly = false;
                if (raw.EndsWith(".dft", StringComparison.OrdinalIgnoreCase)) {
                    dft = raw;
                    model = BomRules.PairModelPathForDraft(raw);
                    if (string.IsNullOrEmpty(model)) { model = raw; draftOnly = true; }
                } else if (BomRules.IsModelExtension(raw)) {
                    dft = BomRules.PairDraftPath(raw);
                } else {
                    continue;
                }
                try { model = Path.GetFullPath(model); } catch { }
                if (!seen.Add(model)) continue;
                if (pulse != null) pulse("读取文件 " + (i + 1) + "/" + list.Count + "：" + Path.GetFileName(model));
                var it = BuildItem(model, dft, 1, !draftOnly, draftOnly ? "独立工程图（仅改名）" : "", null);
                if (!draftOnly) ApplyAutoCategory(it);
                items.Add(it);
            }
            return items;
        }

        // ---------- 读属性与尺寸/质量 ----------

        BomItem BuildItem(string modelPath, string draftPath, int occCount, bool selected, string skipReason, object loadedDoc) {
            var item = new BomItem {
                Selected = selected,
                ModelPath = modelPath,
                DraftPath = draftPath ?? "",
                OccCount = Math.Max(1, occCount),
                Quantity = Math.Max(1, occCount),
                SkipReason = skipReason ?? "",
                DrawingNo = Path.GetFileNameWithoutExtension(modelPath ?? "")
            };
            ReadProperties(item, loadedDoc);
            if (string.IsNullOrWhiteSpace(item.DrawingNo)) item.DrawingNo = Path.GetFileNameWithoutExtension(modelPath ?? "");
            if (string.IsNullOrWhiteSpace(item.PartName)) item.PartName = item.DrawingNo;
            if (item.PropQuantity > 0) item.Quantity = item.PropQuantity;
            return item;
        }

        public void ReadProperties(BomItem item, object loadedDoc) {
            if (item == null || string.IsNullOrEmpty(item.ModelPath)) return;
            var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string material = "", summaryAuthor = "";

            object sets = null;
            bool ok = false;
            // 若该文件已作为打开文档或已加载子装配在 CAD 内存中，优先读内存实时属性，避免读到未 Ctrl+S 保存的旧磁盘值
            object openDoc = loadedDoc ?? FindOpenDoc(item.ModelPath);
            if (openDoc != null) {
                try {
                    string m = "", auth = "";
                    Attempt("读文档属性", () => { custom.Clear(); m = ""; auth = ""; ReadDocProps(openDoc, custom, ref m, ref auth); return true; });
                    material = m;
                    summaryAuthor = auth;
                    ok = true;
                } catch {
                    custom.Clear();
                    material = "";
                    summaryAuthor = "";
                }
            }
            if (!ok) {
                try {
                    sets = Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.FileProperties"));
                    LateCall(sets, "Open", item.ModelPath, true);
                    ReadFilePropsSet(sets, "Custom", custom);
                    material = ReadFilePropSingle(sets, "MechanicalModeling", "Material");
                    if (material.Length == 0) material = ReadFilePropSingle(sets, "MechanicalModeling", "材料");
                    summaryAuthor = ReadFilePropSingle(sets, "SummaryInformation", "作者");
                    if (summaryAuthor.Length == 0) summaryAuthor = ReadFilePropSingle(sets, "SummaryInformation", "Author");
                    ok = true;
                } catch (Exception e) {
                    item.ReadProblem = "读属性失败：" + UnwrapMsg(e);
                    Log.Write("BomReadFileProps " + item.ModelPath, e);
                } finally {
                    CloseFileProperties(sets);
                }
            }

            if (!ok) return;

            item.RawCustomProps.Clear();
            foreach (var kv in custom) item.RawCustomProps[kv.Key] = kv.Value ?? "";

            string partName = FirstNonEmpty(custom, "零件名称", "零件名", "名称", "标题", "Title");
            if (partName.Length > 0) item.PartName = partName;

            string dno = FirstNonEmpty(custom, "图号", "零件图号", "代号");
            if (dno.Length > 0) item.DrawingNo = dno;

            string mat = material.Length > 0 ? material : FirstNonEmpty(custom, "材料", "Material");
            if (mat.Length > 0) item.Material = mat;

            string qtyText = FirstNonEmpty(custom, "数量");
            int q;
            if (int.TryParse(qtyText, out q) && q > 0) item.PropQuantity = q;

            string surf = FirstNonEmpty(custom, "表面处理", "材料处理");
            if (surf.Length > 0) item.SurfaceTreatment = surf;

            string batch = FirstNonEmpty(custom, "批次");
            if (batch.Length > 0) item.Batch = batch;

            string prio = FirstNonEmpty(custom, "加工优先等级", "优先等级");
            if (prio.Length > 0) item.Priority = prio;

            string remark = FirstNonEmpty(custom, "备注");
            if (remark.Length > 0) item.Remark = remark;

            string change = FirstNonEmpty(custom, "变更原因");
            if (change.Length > 0) item.ChangeReason = change;

            string code = FirstNonEmpty(custom, "编码", "物料编码", "ERP编码");
            if (code.Length > 0) item.Code = code;

            string catText = FirstNonEmpty(custom, "八大类类别", "八大类");
            int cat = BomRules.ParseCategory(catText);
            if (cat >= 1 && cat <= 8) {
                item.Category = cat;
                item.CategoryManual = true;
                item.CategoryReason = "来自属性卡";
            }

            item.ProjectNameHint = FirstNonEmpty(custom, "项目名称");
            string designer = FirstNonEmpty(custom, "设计", "作者", "设计者");
            if (designer.Length == 0 && summaryAuthor.Length > 0 && !IsGenericUser(summaryAuthor)) designer = summaryAuthor;
            item.DesignerHint = designer;
        }

        // 批量读取选中零件的真实外形尺寸和质量（用于八大类准确判别）
        public int LoadMetricsForItems(IList<BomItem> items, Action<string> pulse, Func<bool> shouldStop) {
            if (app == null || items == null) return 0;
            int loaded = 0;
            bool oldAlerts = true;
            try { oldAlerts = app.DisplayAlerts; } catch { }
            try {
                try { app.DisplayAlerts = false; } catch { }
                for (int i = 0; i < items.Count; i++) {
                    if (shouldStop != null && shouldStop()) break;
                    var item = items[i];
                    if (item.MetricsLoaded || !File.Exists(item.ModelPath)) continue;
                    if (pulse != null) pulse("读取尺寸与重量 " + (i + 1) + "/" + items.Count + "：" + item.ModelFileName);

                    object existing = FindOpenDoc(item.ModelPath);
                    object doc = existing;
                    bool opened = false;
                    try {
                        if (doc == null) {
                            doc = Attempt("打开模型读尺寸", () => app.Documents.Open(item.ModelPath));
                            Idle();
                            opened = doc != null;
                        }
                        if (doc != null) {
                            // 顺便补读之前可能读不到的属性
                            if (string.IsNullOrEmpty(item.Material) || item.ReadProblem.Length > 0) {
                                var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                string m = "", auth = "";
                                try {
                                    ReadDocProps(doc, custom, ref m, ref auth);
                                    if (string.IsNullOrEmpty(item.Material) && m.Length > 0) item.Material = m;
                                    string pn = FirstNonEmpty(custom, "零件名称", "零件名");
                                    if (pn.Length > 0 && item.PartName == item.DrawingNo) item.PartName = pn;
                                    item.ReadProblem = "";
                                } catch { }
                            }
                            if (TryReadOpenMetrics(doc, item)) loaded++;
                            ApplyAutoCategory(item);
                        }
                    } catch (Exception e) {
                        Log.Write("BomMetrics " + item.ModelPath, e);
                    } finally {
                        if (opened && doc != null) CloseDocSafe(doc);
                    }
                }
            } finally {
                try { app.DisplayAlerts = oldAlerts; } catch { }
            }
            return loaded;
        }

        static bool TryReadOpenMetrics(object doc, BomItem item) {
            if (doc == null || item == null) return false;
            bool gotAny = false;
            double dx = 0, dy = 0, dz = 0, mass = 0;

            var asm = doc as A.AssemblyDocument;
            if (asm != null) {
                try {
                    double m = 0, vol = 0, area = 0;
                    Array com = new double[0], cov = new double[0], gm = new double[0], pa1 = new double[0], pa2 = new double[0], pa3 = new double[0], pm = new double[0], rog = new double[0];
                    bool isSick = false, upd = false;
                    asm.PhysicalProperties.GetAssemblyPhysicalProperties(out m, out vol, out area, ref com, ref cov, ref gm, ref pa1, ref pa2, ref pa3, ref pm, ref rog, out isSick, out upd);
                    if (m > 0 && !double.IsNaN(m) && !double.IsInfinity(m)) { mass = m; gotAny = true; }
                } catch { }
                try {
                    double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
                    double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
                    int cnt = 0;
                    foreach (A.Occurrence occ in asm.Occurrences) {
                        try {
                            if (!occ.Visible || occ.DisplayAsReference) continue;
                            double lx, ly, lz, hx, hy, hz;
                            occ.Range(out lx, out ly, out lz, out hx, out hy, out hz);
                            if (lx < x0) x0 = lx; if (ly < y0) y0 = ly; if (lz < z0) z0 = lz;
                            if (hx > x1) x1 = hx; if (hy > y1) y1 = hy; if (hz > z1) z1 = hz;
                            cnt++;
                        } catch { }
                    }
                    if (cnt > 0 && x1 >= x0) {
                        dx = (x1 - x0) * 1000.0; dy = (y1 - y0) * 1000.0; dz = (z1 - z0) * 1000.0;
                        gotAny = true;
                    }
                } catch { }
            } else {
                // PartDocument / SheetMetalDocument
                var bodies = new List<G.Body>();
                try {
                    var part = doc as P.PartDocument;
                    if (part != null) {
                        bodies = AssemblyPart.BodiesOf(part, "", null);
                        if (part.Models.Count > 0) mass = ReadModelMass(part.Models.Item(1));
                    } else if (doc is P.SheetMetalDocument) {
                        bodies = AssemblyPart.SheetBodies(doc, "", null);
                        var sm = (P.SheetMetalDocument)doc;
                        if (sm.Models.Count > 0) mass = ReadModelMass(sm.Models.Item(1));
                    }
                } catch { }
                if (mass > 0) gotAny = true;
                double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
                double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
                int bcnt = 0;
                foreach (var b in bodies) {
                    try {
                        Array lo = new double[3], hi = new double[3];
                        b.GetExactRange(ref lo, ref hi);
                        double lx = Convert.ToDouble(lo.GetValue(0)), ly = Convert.ToDouble(lo.GetValue(1)), lz = Convert.ToDouble(lo.GetValue(2));
                        double hx = Convert.ToDouble(hi.GetValue(0)), hy = Convert.ToDouble(hi.GetValue(1)), hz = Convert.ToDouble(hi.GetValue(2));
                        if (lx < x0) x0 = lx; if (ly < y0) y0 = ly; if (lz < z0) z0 = lz;
                        if (hx > x1) x1 = hx; if (hy > y1) y1 = hy; if (hz > z1) z1 = hz;
                        bcnt++;
                    } catch { }
                }
                if (bcnt > 0 && x1 >= x0) {
                    dx = (x1 - x0) * 1000.0; dy = (y1 - y0) * 1000.0; dz = (z1 - z0) * 1000.0;
                    gotAny = true;
                }
            }

            if (gotAny) {
                var dims = new[] { Math.Abs(dx), Math.Abs(dy), Math.Abs(dz) };
                Array.Sort(dims);
                item.MinThickMm = dims[0];
                item.MidDimMm = dims[1];
                item.MaxLengthMm = dims[2];
                if (mass > 0) item.MassKg = mass;
                item.MetricsLoaded = true;
            }
            return gotAny;
        }

        static double ReadModelMass(P.Model model) {
            if (model == null) return 0;
            try {
                double density = 0, accuracy = 0, vol = 0, area = 0, mass = 0, relAcc = 0;
                Array cog = new double[0], cov = new double[0], moi = new double[0], pmoi = new double[0], paxes = new double[0], rog = new double[0];
                int status = 0;
                model.GetPhysicalProperties(out status, out density, out accuracy, out vol, out area, out mass, ref cog, ref cov, ref moi, ref pmoi, ref paxes, ref rog, out relAcc);
                if (mass > 0 && !double.IsNaN(mass) && !double.IsInfinity(mass)) return mass;
            } catch { }
            return 0;
        }

        public static void ApplyAutoCategory(BomItem item) {
            if (item == null || item.CategoryManual) return;
            string reason;
            item.Category = BomRules.ClassifyCategory(item, out reason);
            item.CategoryReason = reason;
        }

        // ---------- 导出全部文件（6 种格式 + 表格 + 可选 ZIP） ----------

        public BomExportSummary Export(BomHeader header, IList<BomItem> selectedItems, BomOptions options,
            Action<int, int, string> onProgress, Action<BomItem> onItemDone, Func<bool> shouldStop) {
            var sw = Stopwatch.StartNew();
            var summary = new BomExportSummary {
                OutputFolder = options.OutputFolder,
                ItemsTotal = selectedItems.Count
            };

            Directory.CreateDirectory(options.OutputFolder);

            // 记下用户原本的活动文档，做完切回去
            object originalActive = null;
            try { originalActive = app != null ? app.ActiveDocument : null; } catch { }
            bool oldAlerts = true;
            try { if (app != null) oldAlerts = app.DisplayAlerts; } catch { }

            try {
                if (app != null) { try { app.DisplayAlerts = false; } catch { } }

                // 1. 先批量复制原三维（.par/.psm/.asm）与 .dft（走非白名单工作器，保持加密头不解密）
                var copyPairs = new List<KeyValuePair<string, string>>();
                var copyItemMap = new Dictionary<string, BomItem>(StringComparer.OrdinalIgnoreCase);
                var skippedExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in selectedItems) {
                    string safeBase = BomRules.SanitizeFileName(item.DrawingNo);
                    if (options.IncludeNative3d && File.Exists(item.ModelPath)) {
                        string dst3d = Path.Combine(options.OutputFolder, safeBase + item.ModelExt);
                        if (options.OverwriteExisting || !File.Exists(dst3d)) {
                            copyPairs.Add(new KeyValuePair<string, string>(item.ModelPath, dst3d));
                            copyItemMap[dst3d] = item;
                        } else {
                            skippedExisting.Add(dst3d);
                            summary.FilesSkipped++;
                        }
                    }
                    if (options.IncludeDraft && item.HasDraft) {
                        string dstDft = Path.Combine(options.OutputFolder, safeBase + ".dft");
                        if (options.OverwriteExisting || !File.Exists(dstDft)) {
                            copyPairs.Add(new KeyValuePair<string, string>(item.DraftPath, dstDft));
                            copyItemMap[dstDft] = item;
                        } else {
                            skippedExisting.Add(dstDft);
                            summary.FilesSkipped++;
                        }
                    }
                }

                var copiedOk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (copyPairs.Count > 0) {
                    if (onProgress != null) onProgress(0, selectedItems.Count, "复制原三维与工程图（保持加密状态）…");
                    string copyErr;
                    int done = CopyFilesSafe(copyPairs, out copyErr);
                    for (int k = 0; k < done && k < copyPairs.Count; k++) {
                        copiedOk.Add(copyPairs[k].Value);
                        summary.FilesExported++;
                    }
                    if (copyErr.Length > 0) {
                        summary.Errors.Add(copyErr);
                        summary.FilesFailed += Math.Max(1, copyPairs.Count - done);
                    }
                }

                // 2. 逐个零件导出 .step / 缩略图 / .dwg / .pdf / .jpg
                for (int i = 0; i < selectedItems.Count; i++) {
                    if (shouldStop != null && shouldStop()) { summary.Cancelled = true; break; }
                    var item = selectedItems[i];
                    string safeBase = BomRules.SanitizeFileName(item.DrawingNo);
                    int okCount = 0;
                    int expectCount = 0;
                    var itemNotes = new List<string>();

                    if (options.IncludeNative3d) {
                        expectCount++;
                        string dst3d = Path.Combine(options.OutputFolder, safeBase + item.ModelExt);
                        // 必须以本次真实复制成功（copiedOk）或明确跳过已存在（skippedExisting）为准，不能靠 File.Exists 把失败残留的旧文件误判为完成
                        if (copiedOk.Contains(dst3d) || skippedExisting.Contains(dst3d)) okCount++;
                        else itemNotes.Add(File.Exists(item.ModelPath) ? "复制原三维失败" : "缺原三维");
                    }
                    if (options.IncludeDraft) {
                        expectCount++;
                        string dstDft = Path.Combine(options.OutputFolder, safeBase + ".dft");
                        if (!item.HasDraft) itemNotes.Add("无 .dft");
                        else if (copiedOk.Contains(dstDft) || skippedExisting.Contains(dstDft)) okCount++;
                        else itemNotes.Add("复制 .dft 失败");
                    }

                    // 2a. 三维模型：STEP + 缩略图 + 尺寸重量（只需打开一次）
                    string dstStep = Path.Combine(options.OutputFolder, safeBase + ".step");
                    bool needStep = options.IncludeStep && (options.OverwriteExisting || !File.Exists(dstStep));
                    if (options.IncludeStep) {
                        expectCount++;
                        if (!needStep && File.Exists(dstStep)) { okCount++; summary.FilesSkipped++; }
                    }
                    bool needThumb = options.IncludeThumbnails && (item.ThumbnailPng == null || item.ThumbnailPng.Length == 0);
                    bool needMetrics = !item.MetricsLoaded && !item.CategoryManual;

                    if ((needStep || needThumb || needMetrics) && File.Exists(item.ModelPath) && app != null) {
                        if (onProgress != null) onProgress(i, selectedItems.Count, "(" + (i + 1) + "/" + selectedItems.Count + ") 导出三维：" + safeBase + ".step");
                        object existing = FindOpenDoc(item.ModelPath);
                        object doc = existing;
                        bool opened = false;
                        try {
                            if (doc == null) {
                                doc = Attempt("打开三维模型", () => app.Documents.Open(item.ModelPath));
                                Idle();
                                opened = doc != null;
                            }
                            if (doc != null) {
                                if (needMetrics) {
                                    TryReadOpenMetrics(doc, item);
                                    ApplyAutoCategory(item);
                                }
                                if (needStep) {
                                    string stepLog = Path.ChangeExtension(dstStep, ".log");
                                    bool hadStepLog = File.Exists(stepLog);
                                    try {
                                        DateTime prevUtc;
                                        bool hadOld = PrepareOverwriteTarget(dstStep, out prevUtc);
                                        var d3 = (F.SolidEdgeDocument)doc;
                                        Attempt("导出 STEP", () => { d3.SaveCopyAs(dstStep); return true; });
                                        if (VerifyExportWritten(dstStep, hadOld, prevUtc)) { okCount++; summary.FilesExported++; }
                                        else { summary.FilesFailed++; itemNotes.Add("STEP 未写出"); }
                                    } catch (Exception e) {
                                        summary.FilesFailed++;
                                        itemNotes.Add("STEP 失败");
                                        summary.Errors.Add(safeBase + ".step：" + UnwrapMsg(e));
                                        Log.Write("BomStep " + item.ModelPath, e);
                                    } finally {
                                        // 天工导出 STEP 会在旁边自动生成一个同名 .log，不是清单交付物，清理掉
                                        if (!hadStepLog && File.Exists(stepLog)) { try { File.Delete(stepLog); } catch { } }
                                    }
                                }
                                if (needThumb) {
                                    try {
                                        PreviewMesh mesh = doc is A.AssemblyDocument
                                            ? PreviewMesh.Build((A.AssemblyDocument)doc, null)
                                            : PreviewMesh.BuildSingle(doc);
                                        item.ThumbnailPng = MeshView.RenderPng(mesh, 260, 195);
                                        if (item.ThumbnailPng != null && item.ThumbnailPng.Length > 0) summary.ThumbnailsRendered++;
                                    } catch (Exception e) {
                                        Log.Write("BomThumb " + item.ModelPath, e);
                                    }
                                }
                            }
                        } catch (Exception e) {
                            if (needStep) { summary.FilesFailed++; itemNotes.Add("打开三维失败"); }
                            summary.Errors.Add(safeBase + " 打开三维失败：" + UnwrapMsg(e));
                            Log.Write("BomOpen3d " + item.ModelPath, e);
                        } finally {
                            if (opened && doc != null) {
                                CloseDocSafe(doc);
                                Idle();
                            }
                        }
                    }

                    // 2b. 二维工程图：DWG / PDF / JPG（只需打开一次 .dft）
                    string dstDwg = Path.Combine(options.OutputFolder, safeBase + ".dwg");
                    string dstPdf = Path.Combine(options.OutputFolder, safeBase + ".pdf");
                    string dstJpg = Path.Combine(options.OutputFolder, safeBase + ".jpg");

                    bool needDwg = options.IncludeDwg && item.HasDraft && (options.OverwriteExisting || !File.Exists(dstDwg));
                    bool needPdf = options.IncludePdf && item.HasDraft && (options.OverwriteExisting || !File.Exists(dstPdf));
                    bool needJpg = options.IncludeJpg && item.HasDraft && (options.OverwriteExisting || !File.Exists(dstJpg));

                    if (options.IncludeDwg) { expectCount++; if (!needDwg && item.HasDraft && File.Exists(dstDwg)) { okCount++; summary.FilesSkipped++; } }
                    if (options.IncludePdf) { expectCount++; if (!needPdf && item.HasDraft && File.Exists(dstPdf)) { okCount++; summary.FilesSkipped++; } }
                    if (options.IncludeJpg) { expectCount++; if (!needJpg && item.HasDraft && File.Exists(dstJpg)) { okCount++; summary.FilesSkipped++; } }

                    if ((needDwg || needPdf || needJpg) && item.HasDraft && app != null) {
                        if (onProgress != null) onProgress(i, selectedItems.Count, "(" + (i + 1) + "/" + selectedItems.Count + ") 导出工程图：" + safeBase + " (DWG/PDF/JPG)");
                        object existing = FindOpenDoc(item.DraftPath);
                        object doc = existing;
                        bool opened = false;
                        try {
                            if (doc == null) {
                                doc = Attempt("打开工程图", () => app.Documents.Open(item.DraftPath));
                                Idle();
                                opened = doc != null;
                            }
                            var dft = doc as D.DraftDocument;
                            if (dft != null) {
                                if (needDwg) {
                                    try {
                                        DateTime prevUtc;
                                        bool hadOld = PrepareOverwriteTarget(dstDwg, out prevUtc);
                                        Attempt("导出 DWG", () => { dft.SaveCopyAs(dstDwg); return true; });
                                        if (VerifyExportWritten(dstDwg, hadOld, prevUtc)) { okCount++; summary.FilesExported++; }
                                        else { summary.FilesFailed++; itemNotes.Add("DWG 未写出"); }
                                    } catch (Exception e) {
                                        summary.FilesFailed++; itemNotes.Add("DWG 失败");
                                        summary.Errors.Add(safeBase + ".dwg：" + UnwrapMsg(e));
                                        Log.Write("BomDwg " + item.DraftPath, e);
                                    }
                                }
                                if (needPdf) {
                                    try {
                                        DateTime prevUtc;
                                        bool hadOld = PrepareOverwriteTarget(dstPdf, out prevUtc);
                                        Attempt("导出 PDF", () => { dft.SaveCopyAs(dstPdf); return true; });
                                        if (VerifyExportWritten(dstPdf, hadOld, prevUtc)) { okCount++; summary.FilesExported++; }
                                        else { summary.FilesFailed++; itemNotes.Add("PDF 未写出"); }
                                    } catch (Exception e) {
                                        summary.FilesFailed++; itemNotes.Add("PDF 失败");
                                        summary.Errors.Add(safeBase + ".pdf：" + UnwrapMsg(e));
                                        Log.Write("BomPdf " + item.DraftPath, e);
                                    }
                                }
                                if (needJpg) {
                                    try {
                                        DateTime prevUtc;
                                        bool hadOld = PrepareOverwriteTarget(dstJpg, out prevUtc);
                                        if (ExportDraftJpg(dft, dstJpg) && VerifyExportWritten(dstJpg, hadOld, prevUtc)) { okCount++; summary.FilesExported++; }
                                        else { summary.FilesFailed++; itemNotes.Add("JPG 未写出"); }
                                    } catch (Exception e) {
                                        summary.FilesFailed++; itemNotes.Add("JPG 失败");
                                        summary.Errors.Add(safeBase + ".jpg：" + UnwrapMsg(e));
                                        Log.Write("BomJpg " + item.DraftPath, e);
                                    }
                                }
                            }
                        } catch (Exception e) {
                            int missed = (needDwg ? 1 : 0) + (needPdf ? 1 : 0) + (needJpg ? 1 : 0);
                            summary.FilesFailed += missed;
                            itemNotes.Add("打开 .dft 失败");
                            summary.Errors.Add(safeBase + ".dft 打开失败：" + UnwrapMsg(e));
                            Log.Write("BomOpenDft " + item.DraftPath, e);
                        } finally {
                            if (opened && doc != null) {
                                CloseDocSafe(doc);
                                Idle();
                            }
                        }
                    } else if (!item.HasDraft && (options.IncludeDwg || options.IncludePdf || options.IncludeJpg)) {
                        if (!itemNotes.Contains("无 .dft")) itemNotes.Add("无 .dft（已跳过二维导出）");
                        summary.Warnings.Add(safeBase + " 缺少同名 .dft，跳过二维格式导出");
                    }

                    if (okCount == expectCount && expectCount > 0) summary.ItemsComplete++;
                    item.ExportStatus = okCount + "/" + expectCount + (itemNotes.Count > 0 ? "（" + string.Join("，", itemNotes.ToArray()) + "）" : " 完成");
                    if (onItemDone != null) onItemDone(item);
                    if (onProgress != null) onProgress(i + 1, selectedItems.Count, "已完成 " + (i + 1) + "/" + selectedItems.Count + "：" + safeBase);
                }

                // 3. 生成《加工件清单.xlsx》
                if (!summary.Cancelled && options.IncludeProcessingList) {
                    if (onProgress != null) onProgress(selectedItems.Count, selectedItems.Count, "正在生成《加工件清单》表格…");
                    string xlsxPath = Path.Combine(options.OutputFolder, BomRules.SuggestListFileName(header));
                    try {
                        BomXlsxWriter.WriteProcessingList(xlsxPath, header, selectedItems, options);
                        summary.ListXlsxPath = xlsxPath;
                    } catch (Exception e) {
                        summary.Errors.Add("生成加工件清单失败：" + e.Message);
                        Log.Write("BomWriteList", e);
                    }
                }

                // 4. 可选生成《编码申请-加工件.xlsx》
                if (!summary.Cancelled && options.IncludeCodeRequest) {
                    if (onProgress != null) onProgress(selectedItems.Count, selectedItems.Count, "正在生成《编码申请-加工件》表格…");
                    string codePath = Path.Combine(options.OutputFolder, BomRules.SuggestCodeFileName());
                    try {
                        BomXlsxWriter.WriteCodeRequest(codePath, selectedItems, options.CustomCodeTemplate);
                        summary.CodeXlsxPath = codePath;
                    } catch (Exception e) {
                        summary.Errors.Add("生成编码申请表失败：" + e.Message);
                        Log.Write("BomWriteCode", e);
                    }
                }

                // 5. 可选打包同名 .zip
                if (!summary.Cancelled && options.CreateZip) {
                    if (onProgress != null) onProgress(selectedItems.Count, selectedItems.Count, "正在打包 .zip 压缩包…");
                    try {
                        string trimmed = options.OutputFolder.TrimEnd('\\', '/');
                        string zipPath = trimmed + ".zip";
                        CreateFolderZip(trimmed, zipPath);
                        summary.ZipPath = zipPath;
                    } catch (Exception e) {
                        summary.Errors.Add("打包 ZIP 失败：" + e.Message);
                        Log.Write("BomWriteZip", e);
                    }
                }
            } finally {
                if (app != null) {
                    try { app.DisplayAlerts = oldAlerts; } catch { }
                    if (originalActive != null) {
                        try { ((dynamic)originalActive).Activate(); } catch { }
                    }
                }
                sw.Stop();
                summary.ElapsedMs = sw.ElapsedMilliseconds;
            }

            Log.Write("QuickBomExport", summary.ShortSummary());
            return summary;
        }

        // 用 SheetWindow.SaveAsImage 导出清晰的工程图 JPG（多工作页时第 2 页起另存为 _2.jpg）
        bool ExportDraftJpg(D.DraftDocument dft, string jpgPath) {
            try { dft.Activate(); } catch { }
            Idle();

            var workingSheets = new List<D.Sheet>();
            try {
                var ws = dft.Sections.WorkingSection.Sheets;
                for (int s = 1; s <= ws.Count; s++) workingSheets.Add(ws.Item(s));
            } catch {
                try { if (dft.ActiveSheet != null) workingSheets.Add(dft.ActiveSheet); } catch { }
            }
            if (workingSheets.Count == 0) return false;

            bool firstOk = false;
            string dir = Path.GetDirectoryName(jpgPath);
            string stem = Path.GetFileNameWithoutExtension(jpgPath);

            for (int idx = 0; idx < workingSheets.Count; idx++) {
                var sheet = workingSheets[idx];
                try { sheet.Activate(); } catch { }
                Idle();
                D.SheetWindow win = null;
                try { if (dft.Windows.Count > 0) win = dft.Windows.Item(1) as D.SheetWindow; } catch { }
                if (win == null) { try { win = app.ActiveWindow as D.SheetWindow; } catch { } }
                if (win == null) throw new InvalidOperationException("拿不到工程图窗口（SheetWindow）");

                int imgW = 1920, imgH = 1080;
                try {
                    double sw = sheet.SheetSetup.SheetWidth;
                    double sh = sheet.SheetSetup.SheetHeight;
                    if (sw > 0 && sh > 0) {
                        if (sh > sw) { imgW = 1358; imgH = 1920; }
                        else { imgW = 1920; imgH = Math.Max(1080, (int)Math.Round(1920.0 * sh / sw)); }
                    }
                } catch { }

                string target = idx == 0 ? jpgPath : Path.Combine(dir, stem + "_" + (idx + 1) + ".jpg");
                var w = win;
                Attempt("导出 JPG", () => {
                    try { w.FitEx(D.SheetFitConstants.igFitSheet); } catch { }
                    Idle();
                    w.SaveAsImage(target, imgW, imgH, 1.0, 24, F.SeImageQualityType.seImageQualityHigh, false);
                    return true;
                });
                if (idx == 0 && IsValidJpeg(target)) firstOk = true;
            }
            return firstOk;
        }

        static bool IsValidJpeg(string path) {
            try {
                if (!File.Exists(path)) return false;
                var b = new byte[2];
                using (var fs = File.OpenRead(path)) return fs.Read(b, 0, 2) == 2 && b[0] == 0xFF && b[1] == 0xD8;
            } catch { return false; }
        }

        // ---------- DLP 安全复制（非白名单进程 SuiteRenameWorker.exe --copy） ----------

        static int CopyFilesSafe(List<KeyValuePair<string, string>> pairs, out string why) {
            why = "";
            if (pairs.Count == 0) return 0;
            string exe = Path.Combine(Path.GetDirectoryName(typeof(BomCad).Assembly.Location), "SuiteRenameWorker.exe");
            if (File.Exists(exe)) {
                string tmp = Path.Combine(Path.GetTempPath(), "tg-bomcopy-" + Guid.NewGuid().ToString("N"));
                string list = tmp + ".txt", answer = tmp + ".out";
                try {
                    File.WriteAllLines(list, pairs.Select(p => p.Key + "\t" + p.Value).ToArray(), new UTF8Encoding(false));
                    var psi = new ProcessStartInfo(exe, "--copy \"" + list + "\" \"" + answer + "\"") { UseShellExecute = false, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) {
                        var limit = Stopwatch.StartNew();
                        while (!p.WaitForExit(100)) {
                            try { System.Windows.Forms.Application.DoEvents(); } catch { }
                            if (limit.Elapsed.TotalMinutes > 5) { try { p.Kill(); } catch { } break; }
                        }
                    }
                    var lines = File.Exists(answer) ? File.ReadAllLines(answer, Encoding.UTF8) : new string[0];
                    int done = 0;
                    var head = lines.Length > 0 ? lines[0].Split(' ') : new string[0];
                    if (head.Length == 2) int.TryParse(head[1], out done);
                    if (head.Length != 2 || head[0] != "OK") {
                        why = "复制原模型/工程图失败：" + (lines.Length > 1 ? lines[1] : "工作器未回报结果");
                    }
                    return Math.Min(done, pairs.Count);
                } catch (Exception e) {
                    Log.Write("BomCopyWorker", e);
                    why = "复制原模型/工程图失败：" + e.Message;
                    return 0;
                } finally {
                    try { File.Delete(list); } catch { }
                    try { File.Delete(answer); } catch { }
                }
            }

            // 找不到工作器（极端环境）：仅在源文件不是加密文件时退回直接复制；加密文件拒绝在白名单进程里复制
            int ok = 0;
            foreach (var pair in pairs) {
                if (HasTsdHeader(pair.Key)) {
                    why = "找不到 SuiteRenameWorker.exe，为防止加密文件被解密，已停止复制原模型/工程图。";
                    return ok;
                }
                try {
                    string d = Path.GetDirectoryName(pair.Value);
                    if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                    File.Copy(pair.Key, pair.Value, true);
                    ok++;
                } catch (Exception e) {
                    why = "复制失败（" + Path.GetFileName(pair.Key) + "）：" + e.Message;
                    return ok;
                }
            }
            return ok;
        }

        static bool HasTsdHeader(string path) {
            try {
                var b = new byte[11];
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    if (s.Read(b, 0, b.Length) < b.Length) return false;
                }
                return Encoding.ASCII.GetString(b) == "%TSD-Header";
            } catch { return false; }
        }

        static void CreateFolderZip(string folder, string zipPath) {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create)) {
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) {
                    string rel = file.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                    z.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                }
            }
        }

        // ---------- 辅助 COM / 属性 ----------

        // 覆盖导出前尝试移除旧目标文件；若删不掉则记录旧时间戳，导出后校验确实写出了新文件，避免残留旧文件造成误报完成
        static bool PrepareOverwriteTarget(string dst, out DateTime prevUtc) {
            prevUtc = DateTime.MinValue;
            if (!File.Exists(dst)) return false;
            try { prevUtc = File.GetLastWriteTimeUtc(dst); } catch { }
            try { File.Delete(dst); return false; }
            catch { return true; }
        }

        static bool VerifyExportWritten(string dst, bool hadUnremovedOld, DateTime prevUtc) {
            if (!File.Exists(dst)) return false;
            if (!hadUnremovedOld) return true;
            try { return File.GetLastWriteTimeUtc(dst) != prevUtc; }
            catch { return false; }
        }

        // 关闭批量打开的临时文档时带 COM 忙碌重试并记日志，防止大批量导出遇瞬时忙泄漏文档窗口
        void CloseDocSafe(object doc) {
            if (doc == null) return;
            try {
                Attempt("关闭临时文档", () => { ((dynamic)doc).Close(false); return true; });
            } catch (Exception e) {
                Log.Write("BomCloseDoc " + SafeFullName(doc), e);
            }
        }

        object FindOpenDoc(string path) {
            if (app == null || string.IsNullOrEmpty(path)) return null;
            try {
                var docs = app.Documents;
                for (int i = 1; i <= docs.Count; i++) {
                    object d = docs.Item(i);
                    if (string.Equals(SafeFullName(d), path, StringComparison.OrdinalIgnoreCase)) return d;
                }
            } catch { }
            return null;
        }

        static void ReadFilePropsSet(object sets, string setName, Dictionary<string, string> target) {
            try {
                object set = LateGet(sets, "Item", setName);
                int count = Convert.ToInt32(LateGet(set, "Count"));
                for (int i = 0; i < count; i++) {
                    try {
                        object p = LateGet(set, "Item", i);
                        string name = Convert.ToString(LateGet(p, "Name"));
                        string val = Convert.ToString(LateGet(p, "Value"));
                        // 保留值为空字符串的已有属性（例如「补齐缺的属性」写入的空值），否则工作台重载后会误判为「文件里没有该属性」
                        if (!string.IsNullOrEmpty(name) && !target.ContainsKey(name)) target[name] = (val ?? "").Trim();
                    } catch { }
                }
            } catch { }
        }

        static string ReadFilePropSingle(object sets, string setName, string propName) {
            try {
                object set = LateGet(sets, "Item", setName);
                int count = Convert.ToInt32(LateGet(set, "Count"));
                for (int i = 0; i < count; i++) {
                    try {
                        object p = LateGet(set, "Item", i);
                        string name = Convert.ToString(LateGet(p, "Name"));
                        if (string.Equals(name, propName, StringComparison.OrdinalIgnoreCase)) {
                            return (Convert.ToString(LateGet(p, "Value")) ?? "").Trim();
                        }
                    } catch { }
                }
            } catch { }
            return "";
        }

        static void ReadDocProps(object doc, Dictionary<string, string> custom, ref string material, ref string summaryAuthor) {
            var sets = (F.PropertySets)doc.GetType().InvokeMember("Properties", BindingFlags.GetProperty, null, doc, null);
            for (int s = 1; s <= sets.Count; s++) {
                var set = sets.Item(s);
                string sname = ""; try { sname = set.Name; } catch { }
                if (string.Equals(sname, "Custom", StringComparison.OrdinalIgnoreCase)) {
                    for (int j = 1; j <= set.Count; j++) {
                        try {
                            var p = set.Item(j);
                            string val = Convert.ToString(p.get_Value());
                            if (!string.IsNullOrEmpty(p.Name) && !custom.ContainsKey(p.Name)) custom[p.Name] = (val ?? "").Trim();
                        } catch { }
                    }
                } else if (string.Equals(sname, "MechanicalModeling", StringComparison.OrdinalIgnoreCase)) {
                    for (int j = 1; j <= set.Count; j++) {
                        try {
                            var p = set.Item(j);
                            if (string.Equals(p.Name, "Material", StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, "材料", StringComparison.Ordinal)) {
                                string val = (Convert.ToString(p.get_Value()) ?? "").Trim();
                                if (val.Length > 0) material = val;
                            }
                        } catch { }
                    }
                } else if (string.Equals(sname, "SummaryInformation", StringComparison.OrdinalIgnoreCase)) {
                    for (int j = 1; j <= set.Count; j++) {
                        try {
                            var p = set.Item(j);
                            if (string.Equals(p.Name, "作者", StringComparison.Ordinal) || string.Equals(p.Name, "Author", StringComparison.OrdinalIgnoreCase)) {
                                string val = (Convert.ToString(p.get_Value()) ?? "").Trim();
                                if (val.Length > 0) summaryAuthor = val;
                            }
                        } catch { }
                    }
                }
            }
        }

        static string FirstNonEmpty(Dictionary<string, string> map, params string[] keys) {
            for (int i = 0; i < keys.Length; i++) {
                string v;
                if (map.TryGetValue(keys[i], out v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
            return "";
        }

        static bool IsGenericUser(string name) {
            string s = (name ?? "").Trim();
            return string.Equals(s, "admin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "Administrator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "user", StringComparison.OrdinalIgnoreCase);
        }

        static void CloseFileProperties(object sets) {
            if (sets == null) return;
            try { LateCall(sets, "Close"); } catch { }
            try { Marshal.FinalReleaseComObject(sets); } catch { }
        }

        static object LateGet(object o, string name, params object[] args) {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);
        }
        static void LateCall(object o, string name, params object[] args) {
            o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }

        static string SafeFullName(object doc) {
            if (doc == null) return "";
            try { return Convert.ToString(doc.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, doc, null)); }
            catch { return ""; }
        }

        static string UnwrapMsg(Exception e) {
            if (e == null) return "";
            if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        void Idle() {
            for (int k = 0; k < 4; k++) { try { if (app != null) app.DoIdle(); } catch { } }
        }

        T Attempt<T>(string what, Func<T> action) {
            for (int i = 0; ; i++) {
                try { return action(); }
                catch (Exception e) {
                    var ce = e as COMException ?? (e.InnerException as COMException);
                    if (ce == null) throw;
                    int hr = ce.ErrorCode;
                    bool retryable = hr == unchecked((int)0x80010001) || hr == unchecked((int)0x8001010A) || hr == unchecked((int)0x80010100);
                    if (!retryable || i >= 12) throw;
                    Idle();
                    Thread.Sleep(250);
                }
            }
        }
    }
}
