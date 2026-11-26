using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TianGongCadSuite {
    // 出图清单工作台（0.13.0，属性卡填写 + 批量改名 + 快速出清单三合一）的统一行模型与纯逻辑。
    // 不依赖 CAD / WinForms，全部在 tests/pure 与 PanelTests.exe 里覆盖。
    public sealed class WorkbenchRow {
        public readonly BomItem Bom;
        public readonly PropCardRow Prop;
        public readonly RenameRow Rename;
        public bool HasCfgFollower;
        public string CompanionProblem = "";

        public WorkbenchRow(BomItem bom, PropCardRow prop, RenameRow rename) {
            Bom = bom ?? new BomItem();
            Prop = prop ?? new PropCardRow();
            Rename = rename ?? new RenameRow();
        }

        public bool IsDraftOnly {
            get { return string.Equals(Bom.ModelExt, ".dft", StringComparison.OrdinalIgnoreCase); }
        }

        public bool CanWriteProps {
            get { return !IsDraftOnly && Prop.Locked.Length == 0 && Prop.Problem.Length == 0; }
        }

        public bool RenameChanged {
            get { return Rename.Changed; }
        }

        public bool PropDirty {
            get { return CanWriteProps && Prop.Dirty; }
        }

        public string RenameProblem {
            get {
                if (Rename.Problem.Length > 0) return Rename.Problem;
                return CompanionProblem ?? "";
            }
        }

        public string DraftDisplay {
            get {
                if (IsDraftOnly) return "本文件";
                if (Bom.HasDraft) return HasCfgFollower ? "有+.cfg" : "有";
                return HasCfgFollower ? "缺 .dft (+.cfg)" : "缺 .dft";
            }
        }

        // 文件里实际对应的 Custom 属性键名（兼容 .asm 常用的「零件名」与 .par 的「零件名称」等）
        public string PropKeyFor(string canonicalField) {
            if (canonicalField == "零件名称") {
                if (Prop.Has("零件名称") || Prop.IsEdited("零件名称")) return "零件名称";
                if (Prop.Has("零件名") || Prop.IsEdited("零件名")) return "零件名";
                return "零件名称";
            }
            if (canonicalField == "图号") {
                if (Prop.Has("图号") || Prop.IsEdited("图号")) return "图号";
                if (Prop.Has("零件图号") || Prop.IsEdited("零件图号")) return "零件图号";
                return "图号";
            }
            if (canonicalField == "表面处理") {
                if (Prop.Has("表面处理") || Prop.IsEdited("表面处理")) return "表面处理";
                if (Prop.Has("材料处理") || Prop.IsEdited("材料处理")) return "材料处理";
                return "表面处理";
            }
            if (canonicalField == "优先等级" || canonicalField == "加工优先等级") {
                if (Prop.Has("优先等级") || Prop.IsEdited("优先等级")) return "优先等级";
                if (Prop.Has("加工优先等级") || Prop.IsEdited("加工优先等级")) return "加工优先等级";
                return canonicalField;
            }
            return canonicalField;
        }

        public bool IsPropMissing(string canonicalField) {
            if (IsDraftOnly || Prop.Problem.Length > 0) return false;
            string key = PropKeyFor(canonicalField);
            return !Prop.Has(key) && !Prop.IsEdited(key);
        }

        public bool IsPropEdited(string canonicalField) {
            if (IsDraftOnly) return false;
            return Prop.IsEdited(PropKeyFor(canonicalField));
        }

        public string GetPropValue(string canonicalField) {
            // 锁定 / 读取失败 / 仅工程图行不能写回文件属性卡，但允许在表里修改核心 6 列供清单导出：
            // 此时直接读 Bom.*，避免改完马上弹回 Prop.Original 旧值造成屏幕显示与导出 Excel 脑裂。
            if (!CanWriteProps) {
                if (canonicalField == "零件名称") return Bom.PartName ?? "";
                if (canonicalField == "图号") return Bom.DrawingNo ?? "";
                if (canonicalField == "数量") return Bom.Quantity.ToString();
                if (canonicalField == "表面处理") return Bom.SurfaceTreatment ?? "";
                if (canonicalField == "批次") return Bom.Batch ?? "";
                if (canonicalField == "优先等级" || canonicalField == "加工优先等级") return Bom.Priority ?? "";
            }
            string key = PropKeyFor(canonicalField);
            string v = Prop.Value(key);
            if (canonicalField == "零件名称") return v.Length > 0 || Prop.IsEdited(key) ? v : Bom.PartName;
            if (canonicalField == "图号") return v.Length > 0 || Prop.IsEdited(key) ? v : Bom.DrawingNo;
            if (canonicalField == "数量") return v.Length > 0 || Prop.IsEdited(key) ? v : Bom.Quantity.ToString();
            if (canonicalField == "表面处理") return v.Length > 0 || Prop.IsEdited(key) ? v : Bom.SurfaceTreatment;
            if (canonicalField == "批次") return v.Length > 0 || Prop.IsEdited(key) ? v : Bom.Batch;
            if (canonicalField == "优先等级" || canonicalField == "加工优先等级") return v.Length > 0 || Prop.IsEdited(key) ? v : Bom.Priority;
            return v;
        }

        // 修改任一属性格：同时记录到属性卡待写入队列 + 实时同步到清单项（并按需重算八大类）
        public void SetPropValue(string canonicalField, string value) {
            string clean = PropCardRow.Clean(value);
            string key = PropKeyFor(canonicalField);
            if (CanWriteProps) Prop.Set(key, clean);

            if (canonicalField == "零件名称") {
                Bom.PartName = clean;
                ReclassifyIfAuto();
            } else if (canonicalField == "图号") {
                Bom.DrawingNo = clean;
            } else if (canonicalField == "数量") {
                int q;
                Bom.Quantity = int.TryParse(clean, out q) ? q : 0;
            } else if (canonicalField == "表面处理") {
                Bom.SurfaceTreatment = clean;
                ReclassifyIfAuto();
            } else if (canonicalField == "批次") {
                Bom.Batch = clean;
            } else if (canonicalField == "优先等级" || canonicalField == "加工优先等级") {
                Bom.Priority = clean;
            } else if (canonicalField == "设计") {
                Bom.DesignerHint = clean;
            } else if (canonicalField == "项目名称") {
                Bom.ProjectNameHint = clean;
            }
        }

        public void SetMaterial(string value) {
            Bom.Material = PropCardRow.Clean(value);
            ReclassifyIfAuto();
        }

        public void ReclassifyIfAuto() {
            if (IsDraftOnly || Bom.CategoryManual) return;
            string reason;
            Bom.Category = BomRules.ClassifyCategory(Bom, out reason);
            Bom.CategoryReason = reason;
        }

        // 批量改名按模板生成时用的实时属性字典（包含当前表里改过但尚未落盘的值！）
        public Dictionary<string, string> EffectiveTemplateProps() {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Prop.Original) d[kv.Key] = kv.Value ?? "";
            foreach (var kv in Prop.Edited) d[kv.Key] = kv.Value ?? "";
            string dno = GetPropValue("图号");
            string pName = GetPropValue("零件名称");
            PutIfNonEmpty(d, "图号", dno);
            PutIfNonEmpty(d, "零件名称", pName);
            PutIfNonEmpty(d, "零件名", pName);
            PutIfNonEmpty(d, "材料", Bom.Material);
            PutIfNonEmpty(d, "表面处理", GetPropValue("表面处理"));
            PutIfNonEmpty(d, "批次", GetPropValue("批次"));
            PutIfNonEmpty(d, "优先等级", GetPropValue("优先等级"));
            if (Bom.Quantity > 0) d["数量"] = Bom.Quantity.ToString();
            return d;
        }

        static void PutIfNonEmpty(Dictionary<string, string> d, string key, string value) {
            if (!string.IsNullOrWhiteSpace(value)) d[key] = value.Trim();
        }

        public void RevertAllEdits() {
            Rename.Revert();
            CompanionProblem = "";
            if (Prop.Dirty) {
                Prop.Edited.Clear();
                SyncBomFromPropOriginal();
            }
        }

        public void SyncBomFromPropOriginal() {
            string pName = Prop.Value(PropKeyFor("零件名称"));
            if (pName.Length > 0) Bom.PartName = pName;
            string dno = Prop.Value(PropKeyFor("图号"));
            Bom.DrawingNo = dno.Length > 0 ? dno : Path.GetFileNameWithoutExtension(Bom.ModelPath ?? "");
            string qty = Prop.Value("数量");
            int q;
            if (int.TryParse(qty, out q) && q > 0) Bom.Quantity = q;
            else Bom.Quantity = Math.Max(1, Bom.OccCount);
            Bom.SurfaceTreatment = Prop.Value(PropKeyFor("表面处理"));
            string batch = Prop.Value("批次");
            Bom.Batch = batch.Length > 0 ? batch : "第一批";
            string prio = Prop.Value(PropKeyFor("优先等级"));
            Bom.Priority = prio.Length > 0 ? prio : "A";
            ReclassifyIfAuto();
        }

        public void RefreshCompanions(Func<string, bool> exists) {
            if (exists == null) exists = File.Exists;
            if (IsDraftOnly) {
                Bom.DraftPath = Bom.ModelPath;
                HasCfgFollower = false;
                return;
            }
            string dft = "";
            try {
                string cand = Path.ChangeExtension(Bom.ModelPath, ".dft");
                if (exists(cand)) dft = cand;
            } catch { }
            Bom.DraftPath = dft;

            HasCfgFollower = false;
            if (Bom.IsAssembly) {
                try {
                    string cfg = Path.ChangeExtension(Bom.ModelPath, ".cfg");
                    if (exists(cfg)) HasCfgFollower = true;
                } catch { }
            }
        }

        public WorkbenchRowSnapshot CaptureSnapshot() {
            return new WorkbenchRowSnapshot {
                Selected = Bom.Selected,
                NewBase = Rename.NewBase,
                EditedProps = new Dictionary<string, string>(Prop.Edited, StringComparer.Ordinal),
                PartName = Bom.PartName,
                DrawingNo = Bom.DrawingNo,
                Material = Bom.Material,
                Quantity = Bom.Quantity,
                SurfaceTreatment = Bom.SurfaceTreatment,
                Batch = Bom.Batch,
                Priority = Bom.Priority,
                Category = Bom.Category,
                CategoryManual = Bom.CategoryManual,
                CategoryReason = Bom.CategoryReason,
                ChangeReason = Bom.ChangeReason,
                Remark = Bom.Remark,
                Code = Bom.Code,
                LookupDrawingNo = Bom.LookupDrawingNo,
                LookupPartName = Bom.LookupPartName,
                DesignerHint = Bom.DesignerHint,
                ProjectNameHint = Bom.ProjectNameHint
            };
        }

        public void RestoreSnapshot(WorkbenchRowSnapshot s) {
            if (s == null) return;
            Bom.Selected = s.Selected;
            Rename.SetNewBase(s.NewBase);
            Prop.Edited.Clear();
            if (s.EditedProps != null) {
                foreach (var kv in s.EditedProps) Prop.Edited[kv.Key] = kv.Value;
            }
            Bom.PartName = s.PartName;
            Bom.DrawingNo = s.DrawingNo;
            Bom.Material = s.Material;
            Bom.Quantity = s.Quantity;
            Bom.SurfaceTreatment = s.SurfaceTreatment;
            Bom.Batch = s.Batch;
            Bom.Priority = s.Priority;
            Bom.Category = s.Category;
            Bom.CategoryManual = s.CategoryManual;
            Bom.CategoryReason = s.CategoryReason;
            Bom.ChangeReason = s.ChangeReason;
            Bom.Remark = s.Remark;
            Bom.Code = s.Code;
            Bom.LookupDrawingNo = s.LookupDrawingNo;
            Bom.LookupPartName = s.LookupPartName;
            Bom.DesignerHint = s.DesignerHint;
            Bom.ProjectNameHint = s.ProjectNameHint;
        }
    }

    public sealed class WorkbenchRowSnapshot {
        public bool Selected;
        public string NewBase = "";
        public Dictionary<string, string> EditedProps;
        public string PartName = "";
        public string DrawingNo = "";
        public string Material = "";
        public int Quantity;
        public string SurfaceTreatment = "";
        public string Batch = "";
        public string Priority = "";
        public int Category;
        public bool CategoryManual;
        public string CategoryReason = "";
        public string ChangeReason = "";
        public string Remark = "";
        public string Code = "";
        public string LookupDrawingNo = "";
        public string LookupPartName = "";
        public string DesignerHint = "";
        public string ProjectNameHint = "";
    }

    public static class WorkbenchRules {
        // 已经放在表格固定核心区的 6 个共用属性（属性卡扩展区自动去重，不出现重复列）
        public static readonly string[] CoreSharedPropFields = {
            "零件名称", "图号", "数量", "表面处理", "批次", "优先等级"
        };
        // 已被固定核心列按别名承接的同类属性名（避免默认同时出现「优先等级」与「加工优先等级」两列互相覆盖 Bom.Priority）
        static readonly string[] CoreSharedPropAliases = {
            "零件名", "零件图号", "材料处理", "加工优先等级"
        };

        // 常用机械工程字段下拉联想词库（用于单元格编辑自动补全，仍支持自由输入任意值）
        public static readonly string[] SuggestedMaterials = {
            "45#", "45#钢", "Q235-B", "Q235A", "AL6061", "AL6061-T6", "AL5052", "AL7075",
            "SUS304", "SUS316", "Cr12MoV", "SKD11", "40Cr", "SPCC", "SGCC", "SECC",
            "POM", "白色POM", "黑色POM", "PC", "防静电PC", "电木", "尼龙", "MC尼龙", "FR4", "PEEK", "6063-T5"
        };
        public static readonly string[] SuggestedSurfaceTreatments = {
            "无", "本色阳极氧化", "黑色阳极氧化", "硬质阳极氧化", "镀镍", "镀化学镍",
            "镀铬", "镀硬铬", "发黑", "镀锌", "镀蓝白锌", "哑光喷塑RAL9003", "喷粉",
            "喷塑", "烤漆", "拉丝", "抛光", "调质", "高频淬火", "真空淬火", "氮化"
        };
        public static readonly string[] SuggestedBatches = {
            "第一批", "第二批", "第三批", "第四批", "补单", "变更"
        };
        public static readonly string[] SuggestedPriorities = {
            "A", "B", "C", "紧急", "加急", "正常"
        };
        public static readonly string[] SuggestedChangeReasons = {
            "新增", "设变", "尺寸变更", "材质变更", "补单", "加急"
        };

        public static string[] BuildSuggestions(IEnumerable<string> presets, IEnumerable<string> existingValues) {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (presets != null) {
                foreach (var s in presets) {
                    string t = (s ?? "").Trim();
                    if (t.Length > 0 && seen.Add(t)) list.Add(t);
                }
            }
            if (existingValues != null) {
                foreach (var s in existingValues) {
                    string t = (s ?? "").Trim();
                    if (t.Length > 0 && seen.Add(t)) list.Add(t);
                }
            }
            return list.ToArray();
        }

        public static bool IsCoreSharedField(string field) {
            for (int i = 0; i < CoreSharedPropFields.Length; i++) {
                if (string.Equals(CoreSharedPropFields[i], field, StringComparison.Ordinal)) return true;
            }
            for (int i = 0; i < CoreSharedPropAliases.Length; i++) {
                if (string.Equals(CoreSharedPropAliases[i], field, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public static List<string> ExtraPropFields(IEnumerable<string> configuredFields) {
            var list = new List<string>();
            if (configuredFields == null) return list;
            foreach (string raw in configuredFields) {
                string f = (raw ?? "").Trim();
                if (f.Length == 0 || IsCoreSharedField(f) || list.Contains(f)) continue;
                list.Add(f);
            }
            return list;
        }

        public static WorkbenchRow CreateRow(BomItem bom, Func<string, bool> exists) {
            if (exists == null) exists = File.Exists;
            string path = bom.ModelPath ?? "";
            var prop = new PropCardRow {
                Path = path,
                Material = bom.Material ?? "",
                Problem = bom.ReadProblem ?? "",
                Locked = LockReason(path, false)
            };
            foreach (var kv in bom.RawCustomProps) {
                if (!prop.Original.ContainsKey(kv.Key)) prop.Original[kv.Key] = kv.Value ?? "";
            }
            var ren = new RenameRow {
                Path = path,
                Locked = LockReason(path, true)
            };
            var row = new WorkbenchRow(bom, prop, ren);
            row.RefreshCompanions(exists);
            return row;
        }

        public static string LockReason(string path, bool checkReadOnly) {
            if (string.IsNullOrEmpty(path)) return "";
            try {
                string full = Path.GetFullPath(path);
                if (full.IndexOf("Standard Parts", StringComparison.OrdinalIgnoreCase) >= 0
                    || full.IndexOf("标准件", StringComparison.Ordinal) >= 0) {
                    return "标准件库里的共用文件，不修改";
                }
                foreach (var root in new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                }) {
                    if (string.IsNullOrEmpty(root)) continue;
                    string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                        return "程序目录里的共用文件，不修改";
                    }
                }
                if (checkReadOnly && File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) {
                    return "文件是只读的，不能改名";
                }
            } catch { }
            return "";
        }

        // 构造完整的批量改名批次（主文件 + 可选跟随的同名 .dft / .cfg）并做重名与冲突校验
        public static List<RenameRow> BuildAndValidateRenameBatch(IList<WorkbenchRow> rows, bool followCompanions, Func<string, bool> exists, out int problemCount) {
            if (exists == null) exists = File.Exists;
            var batch = new List<RenameRow>();
            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var companionMap = new Dictionary<RenameRow, WorkbenchRow>();

            foreach (var r in rows) {
                r.CompanionProblem = "";
                batch.Add(r.Rename);
                existingPaths.Add(r.Rename.Path);
            }

            if (followCompanions) {
                foreach (var r in rows) {
                    if (r.IsDraftOnly) continue;
                    var exts = r.Bom.IsAssembly ? new[] { ".dft", ".cfg" } : new[] { ".dft" };
                    foreach (string ext in exts) {
                        string compPath = "";
                        try { compPath = Path.ChangeExtension(r.Rename.Path, ext); } catch { }
                        if (string.IsNullOrEmpty(compPath) || existingPaths.Contains(compPath) || !exists(compPath)) continue;
                        existingPaths.Add(compPath);
                        var comp = new RenameRow {
                            Path = compPath,
                            Master = r.Rename,
                            Locked = LockReason(compPath, true)
                        };
                        batch.Add(comp);
                        companionMap[comp] = r;
                    }
                }
            }

            problemCount = RenameRules.Validate(batch, exists);
            foreach (var kv in companionMap) {
                if (kv.Key.Problem.Length > 0) {
                    var master = kv.Value;
                    string msg = "跟随" + kv.Key.Extension + "：" + kv.Key.Problem;
                    master.CompanionProblem = master.CompanionProblem.Length == 0 ? msg : master.CompanionProblem + "；" + msg;
                }
            }
            return batch;
        }

        // 改名落盘成功后同步所有行的路径（模型路径、配对 .dft 路径、属性卡路径），保留尚未写入的属性卡修改！
        public static void ApplyRenamedPaths(IList<WorkbenchRow> rows, IDictionary<string, string> moved, Func<string, bool> exists) {
            if (moved == null || moved.Count == 0) return;
            if (exists == null) exists = File.Exists;
            foreach (var r in rows) {
                string to;
                if (moved.TryGetValue(r.Rename.Path, out to)) {
                    string oldBase = Path.GetFileNameWithoutExtension(r.Rename.Path);
                    string newBase = Path.GetFileNameWithoutExtension(to);
                    r.Rename.Path = to;
                    r.Rename.Revert();
                    r.Prop.Path = to;
                    r.Bom.ModelPath = to;
                    // 仅当文件本身没有非空自定义「图号」属性（清单图号原本只是回退自旧文件名）时，改名后才自动跟着变成新文件名，
                    // 防止原文件已有 Prop.Original["图号"] == oldBase 时表格仍显旧图号而导出 Bom.DrawingNo 偷偷变成新文件名
                    string dnoKey = r.PropKeyFor("图号");
                    bool hasCustomDno = r.CanWriteProps && !string.IsNullOrEmpty(r.Prop.Value(dnoKey));
                    if (string.Equals(r.Bom.DrawingNo, oldBase, StringComparison.OrdinalIgnoreCase) && !r.IsPropEdited("图号") && !hasCustomDno) {
                        r.Bom.DrawingNo = newBase;
                    }
                }
                string newDraft;
                if (!string.IsNullOrEmpty(r.Bom.DraftPath) && moved.TryGetValue(r.Bom.DraftPath, out newDraft)) {
                    r.Bom.DraftPath = newDraft;
                } else {
                    r.RefreshCompanions(exists);
                }
                r.CompanionProblem = "";
            }
        }

        // 计算某一行写入属性卡时需要写的字段（自动映射到该文件实际属性名，避免 .asm 重复建「零件名」与「零件名称」）
        public static List<KeyValuePair<string, string>> CollectPropWrites(WorkbenchRow row, IList<string> configuredFields, bool fillMissing) {
            if (row == null || !row.PropDirty) return new List<KeyValuePair<string, string>>();
            var mappedFields = new List<string>();
            if (configuredFields != null) {
                foreach (var f in configuredFields) {
                    string key = row.PropKeyFor(f);
                    if (!mappedFields.Contains(key)) mappedFields.Add(key);
                }
            }
            return row.Prop.Writes(mappedFields, fillMissing);
        }

        // 一键联动：用图号填入新文件名
        public static int SyncDrawingNoToNewName(IEnumerable<WorkbenchRow> targets) {
            int n = 0;
            foreach (var r in targets) {
                if (r.Rename.Locked.Length > 0) continue;
                string dno = (r.GetPropValue("图号") ?? "").Trim();
                if (dno.Length == 0) continue;
                r.Rename.SetNewBase(dno);
                n++;
            }
            return n;
        }

        // 一键联动：用当前文件名（优先用待改的新文件名）同步写入图号
        public static int SyncFileNameToDrawingNo(IEnumerable<WorkbenchRow> targets) {
            int n = 0;
            foreach (var r in targets) {
                if (r.IsDraftOnly) continue;
                string baseName = r.Rename.NewBase;
                if (string.IsNullOrWhiteSpace(baseName)) continue;
                r.SetPropValue("图号", baseName);
                n++;
            }
            return n;
        }

        // ---- 类 Excel 表格排序与列筛选纯逻辑 ----

        public const string BlankToken = "(空白)";

        // 取出行在指定列键（稳定键，不受自定义列增删影响）上的展示文本
        public static string CellTextByKey(WorkbenchRow r, string colKey, string statusText) {
            if (r == null || string.IsNullOrEmpty(colKey)) return "";
            if (colKey.StartsWith("prop:", StringComparison.Ordinal)) {
                return r.GetPropValue(colKey.Substring(5)) ?? "";
            }
            switch (colKey) {
                case "col:sel": return r.Bom.Selected ? "已勾选" : "未勾选";
                case "col:kind": return r.Rename.Kind ?? "";
                case "col:old": return r.Rename.FileName ?? "";
                case "col:new": return r.Rename.NewBase ?? "";
                case "col:mat": return r.Bom.Material ?? "";
                case "col:cat": return BomRules.CategoryLabel(r.Bom.Category);
                case "col:change": return r.Bom.ChangeReason ?? "";
                case "col:remark": return r.Bom.Remark ?? "";
                case "col:code": return r.Bom.Code ?? "";
                case "col:ldno": return r.Bom.LookupDrawingNo ?? "";
                case "col:lname": return r.Bom.LookupPartName ?? "";
                case "col:draft": return r.DraftDisplay ?? "";
                case "col:status": return statusText ?? "";
                default: return "";
            }
        }

        public static string FilterToken(string raw) {
            string s = (raw ?? "").Trim();
            return s.Length == 0 ? BlankToken : s;
        }

        // 判断某行是否通过全部列筛选（可跳过 excludeColKey，用于生成该列筛选菜单的级联候选项）
        public static bool PassesColumnFilters(WorkbenchRow r, string statusText, IDictionary<string, HashSet<string>> activeFilters, string excludeColKey) {
            if (activeFilters == null || activeFilters.Count == 0) return true;
            foreach (var kv in activeFilters) {
                if (excludeColKey != null && string.Equals(kv.Key, excludeColKey, StringComparison.Ordinal)) continue;
                if (kv.Value == null) continue;
                string tok = FilterToken(CellTextByKey(r, kv.Key, statusText));
                if (!kv.Value.Contains(tok)) return false;
            }
            return true;
        }

        // 统计某列的所有唯一值及出现次数（自然排序，(空白) 放最前方便一键找空）
        public static List<KeyValuePair<string, int>> DistinctColumnValues(IEnumerable<WorkbenchRow> candidateRows, string colKey, Func<WorkbenchRow, string> statusOf) {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in candidateRows) {
                string tok = FilterToken(CellTextByKey(r, colKey, statusOf != null ? statusOf(r) : ""));
                int c;
                counts.TryGetValue(tok, out c);
                counts[tok] = c + 1;
            }
            var list = counts.ToList();
            list.Sort((a, b) => {
                if (a.Key == BlankToken && b.Key != BlankToken) return -1;
                if (b.Key == BlankToken && a.Key != BlankToken) return 1;
                return NaturalCompare(a.Key, b.Key);
            });
            return list;
        }

        // 工程自然排序：SK26.01-SD-15-2 排在 SK26.01-SD-15-10 前面，数字按数值比、其余不分大小写
        public static int NaturalCompare(string a, string b) {
            a = a ?? "";
            b = b ?? "";
            int ia = 0, ib = 0, na = a.Length, nb = b.Length;
            while (ia < na && ib < nb) {
                char ca = a[ia], cb = b[ib];
                if (ca >= '0' && ca <= '9' && cb >= '0' && cb <= '9') {
                    int sa = ia; while (ia < na && a[ia] >= '0' && a[ia] <= '9') ia++;
                    int sb = ib; while (ib < nb && b[ib] >= '0' && b[ib] <= '9') ib++;
                    // 去前导零比较数值大小
                    int za = sa; while (za < ia - 1 && a[za] == '0') za++;
                    int zb = sb; while (zb < ib - 1 && b[zb] == '0') zb++;
                    int la = ia - za, lb = ib - zb;
                    if (la != lb) return la.CompareTo(lb);
                    for (int k = 0; k < la; k++) {
                        if (a[za + k] != b[zb + k]) return a[za + k].CompareTo(b[zb + k]);
                    }
                    // 数值相同则短的在前（如 "2" vs "02"）
                    if ((ia - sa) != (ib - sb)) return (ia - sa).CompareTo(ib - sb);
                } else {
                    int cmp = StringComparer.OrdinalIgnoreCase.Compare(ca.ToString(), cb.ToString());
                    if (cmp != 0) return cmp;
                    ia++; ib++;
                }
            }
            return (na - ia).CompareTo(nb - ib);
        }

        // 对可见行排序（sortDir: 1 升序，-1 降序，0 保持默认；文本空白项始终置底，与 Excel/WPS 一致）
        public static void SortRows(List<WorkbenchRow> list, string colKey, int sortDir, Func<WorkbenchRow, string> statusOf) {
            if (list == null || list.Count <= 1 || sortDir == 0 || string.IsNullOrEmpty(colKey)) return;
            // 稳定排序：带原始下标兜底，相等元素不抖动
            var indexed = new List<KeyValuePair<int, WorkbenchRow>>(list.Count);
            for (int i = 0; i < list.Count; i++) indexed.Add(new KeyValuePair<int, WorkbenchRow>(i, list[i]));
            indexed.Sort((x, y) => {
                int c = CompareRowsByColumn(x.Value, y.Value, colKey, sortDir, statusOf);
                if (c != 0) return c;
                return x.Key.CompareTo(y.Key);
            });
            list.Clear();
            for (int i = 0; i < indexed.Count; i++) list.Add(indexed[i].Value);
        }

        static int CompareRowsByColumn(WorkbenchRow a, WorkbenchRow b, string colKey, int sortDir, Func<WorkbenchRow, string> statusOf) {
            if (colKey == "col:sel") {
                // 升序：已勾选排前面；降序：未勾选排前面
                int c = b.Bom.Selected.CompareTo(a.Bom.Selected);
                return sortDir > 0 ? c : -c;
            }
            if (colKey == "prop:数量") {
                int c = a.Bom.Quantity.CompareTo(b.Bom.Quantity);
                return sortDir > 0 ? c : -c;
            }
            if (colKey == "col:cat") {
                int c = a.Bom.Category.CompareTo(b.Bom.Category);
                return sortDir > 0 ? c : -c;
            }
            string sa = CellTextByKey(a, colKey, statusOf != null ? statusOf(a) : "");
            string sb = CellTextByKey(b, colKey, statusOf != null ? statusOf(b) : "");
            // 空白值无论升序还是降序都固定排在底部（Excel / WPS 标准习惯）
            bool ea = string.IsNullOrWhiteSpace(sa), eb = string.IsNullOrWhiteSpace(sb);
            if (ea && eb) return 0;
            if (ea) return 1;
            if (eb) return -1;
            int cmp = NaturalCompare(sa, sb);
            return sortDir > 0 ? cmp : -cmp;
        }

        // 把选中格子格式化为干净的 Excel / WPS 制表符文本（过滤换行符/制表符，避免粘贴错列）
        public static string FormatClipboardBlock(IList<IList<string>> rows) {
            if (rows == null || rows.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < rows.Count; r++) {
                if (r > 0) sb.Append("\r\n");
                var cols = rows[r];
                if (cols == null) continue;
                for (int c = 0; c < cols.Count; c++) {
                    if (c > 0) sb.Append('\t');
                    string v = cols[c] ?? "";
                    if (v.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0) {
                        v = v.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                    }
                    sb.Append(v);
                }
            }
            return sb.ToString();
        }
    }
}
