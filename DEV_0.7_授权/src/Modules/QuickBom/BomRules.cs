using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    // 快速出清单的核心纯逻辑：八大类自动分类、加工件筛选、表头推断、标准命名、导出前校验。
    // 不依赖 CAD / WinForms，全部在 tests/pure 里覆盖。
    public static class BomRules {
        // 对应《编码申请-加工件.xlsx》的「八大类」sheet（严格一致，确保 VLOOKUP 命中）
        public static readonly string[] CategoryErpNames = {
            "",
            "大焊接件",
            "焊接小件",
            "机加大板",
            "机加小件",
            "车床件",
            "钣金件",
            "非金属件",
            "铝型材"
        };

        public static readonly string[] CategoryLabels = {
            "1-大焊接件",
            "2-焊接小件",
            "3-机加大板",
            "4-机加小件",
            "5-车床件",
            "6-钣金件",
            "7-非金属件",
            "8-铝型材"
        };

        public static string ErpCategoryName(int category) {
            if (category >= 1 && category <= 8) return CategoryErpNames[category];
            return CategoryErpNames[4];
        }

        public static string CategoryLabel(int category) {
            if (category >= 1 && category <= 8) return CategoryLabels[category - 1];
            return CategoryLabels[3];
        }

        public static int ParseCategory(string text) {
            string s = (text ?? "").Trim();
            if (s.Length == 0) return 0;
            int n;
            if (int.TryParse(s, out n) && n >= 1 && n <= 8) return n;
            if (s[0] >= '1' && s[0] <= '8') return s[0] - '0';
            for (int i = 1; i <= 8; i++) {
                if (s.IndexOf(CategoryErpNames[i], StringComparison.OrdinalIgnoreCase) >= 0) return i;
            }
            if (s.IndexOf("大焊", StringComparison.Ordinal) >= 0) return 1;
            if (s.IndexOf("小焊", StringComparison.Ordinal) >= 0 || s.IndexOf("焊接", StringComparison.Ordinal) >= 0) return 2;
            if (s.IndexOf("大板", StringComparison.Ordinal) >= 0) return 3;
            if (s.IndexOf("机加", StringComparison.Ordinal) >= 0) return 4;
            if (s.IndexOf("车床", StringComparison.Ordinal) >= 0) return 5;
            if (s.IndexOf("钣金", StringComparison.Ordinal) >= 0) return 6;
            if (s.IndexOf("非金属", StringComparison.Ordinal) >= 0) return 7;
            if (s.IndexOf("型材", StringComparison.Ordinal) >= 0 || s.IndexOf("PC", StringComparison.OrdinalIgnoreCase) >= 0) return 8;
            return 0;
        }

        // ---- 八大类自动判别（严格遵循公司清单《八大类区分规则》） ----
        // 1类大焊接件：长度 > 1200mm 或 重量 > 100kg
        // 2类小焊接件：除 1 类以外的所有焊接件
        // 3类大板：长度 > 800mm 的大机加件（厚度 6mm 以上并且有表面处理要求的属于机加件）
        // 4类小机加件：800mm 以下的所有机加件
        // 5类车床件：车床加工零件类
        // 6类钣金：钣金件零件类
        // 7类非金属件：非金属零件类
        // 8类型材：型材类、PC板类
        static readonly Regex ProfileMaterial = new Regex(@"(?:6063|铝型材|型材|\bPC\b|PC板|聚碳酸酯|耐力板|防静电PC)", RegexOptions.IgnoreCase);
        static readonly Regex ProfileName = new Regex(@"(?:铝型材|型材(?!框架|支架)|PC板|聚碳酸酯|防尘板|封板PC|有机玻璃PC)", RegexOptions.IgnoreCase);
        static readonly Regex NonMetalMaterial = new Regex(@"(?:POM|赛钢|尼龙|PA6|PA66|MC尼龙|电木|胶木|FR4|玻纤|环氧|PEEK|PTFE|四氟|特氟龙|优力胶|聚氨酯|\bPU\b|橡胶|硅胶|氟胶|PVC|\bPP\b|\bPE\b|HDPE|UHMW|ABS|亚克力|PMMA|有机玻璃|防静电板|合成石|绝缘板|电木板|红胶木|聚甲醛)", RegexOptions.IgnoreCase);
        static readonly Regex WeldName = new Regex(@"(?:焊接|焊件|焊合)", RegexOptions.IgnoreCase);
        static readonly Regex SheetName = new Regex(@"(?:钣金|折弯|护罩|罩壳|防尘罩|护板|折边)", RegexOptions.IgnoreCase);
        static readonly Regex SheetMaterial = new Regex(@"(?:SPCC|SGCC|SECC|ST12|DC01|冷轧板|镀锌板|薄板)", RegexOptions.IgnoreCase);
        static readonly Regex LatheStrongName = new Regex(@"(?:光轴|转轴|主轴|芯轴|心轴|销轴|定位销|衬套|轴套|隔套|垫套|滚轮|导向柱|导柱|导套|螺柱|柱塞|顶针|法兰盘|法兰轴|转接套)", RegexOptions.IgnoreCase);
        static readonly Regex LatheWeakName = new Regex(@"(?:轴|销|套|辊|柱|法兰)$", RegexOptions.IgnoreCase);

        public static int ClassifyCategory(BomItem item, out string reason) {
            reason = "";
            if (item == null) return 4;
            string mat = (item.Material ?? "").Trim();
            string name = (item.PartName ?? "").Trim();
            bool hasMetrics = item.MetricsLoaded && (item.MaxLengthMm > 0 || item.MassKg > 0);
            string metricTag = BuildMetricTag(item);

            // 1. 焊接件（.asm 装配体，或名字明确标为焊接件）
            if (item.IsAssembly || WeldName.IsMatch(name)) {
                // 铝型材框架本身如果不是焊件而是型材单根，通常不会是 .asm；如果是型材单根 .par 下面判为 8
                if (hasMetrics && (item.MaxLengthMm > 1200.0 || item.MassKg > 100.0)) {
                    reason = "大焊接件" + metricTag;
                    return 1;
                }
                reason = "小焊接件" + (metricTag.Length > 0 ? metricTag : "（装配体）");
                return 2;
            }

            // 2. 型材类 / PC板类 -> 8
            if (ProfileMaterial.IsMatch(mat) || ProfileName.IsMatch(name)) {
                reason = "型材/PC板（按材料或名称）";
                return 8;
            }

            // 3. 非金属件 -> 7
            if (NonMetalMaterial.IsMatch(mat)) {
                reason = "非金属（材料 " + mat + "）";
                return 7;
            }

            // 4. 钣金件 -> 6（.psm 文件明确是钣金）
            if (item.IsSheetMetal) {
                reason = "钣金件（.psm）" + metricTag;
                return 6;
            }

            // 5. 车床件 -> 5（回转体特征或明确车床件名称）
            if (LatheStrongName.IsMatch(name)) {
                reason = "车床件（按名称）" + metricTag;
                return 5;
            }
            if (LatheWeakName.IsMatch(name) && hasMetrics && IsCylindricalBox(item)) {
                reason = "车床件（回转外形）" + metricTag;
                return 5;
            }

            // 6. 薄板/折弯件 -> 6（厚度 < 6mm 且名字/材料像钣金，或厚度 < 6mm 无表面处理要求）
            bool hasSurface = !string.IsNullOrWhiteSpace(item.SurfaceTreatment)
                && !string.Equals(item.SurfaceTreatment.Trim(), "无", StringComparison.Ordinal)
                && !string.Equals(item.SurfaceTreatment.Trim(), "/", StringComparison.Ordinal)
                && !string.Equals(item.SurfaceTreatment.Trim(), "-", StringComparison.Ordinal);
            if (SheetName.IsMatch(name) && (!hasMetrics || item.MinThickMm <= 6.0 || item.MinThickMm == 0)) {
                reason = "钣金件（按名称）" + metricTag;
                return 6;
            }
            if (hasMetrics && item.MinThickMm > 0 && item.MinThickMm < 6.0 && (SheetMaterial.IsMatch(mat) || !hasSurface)) {
                reason = "钣金件（薄板 " + item.MinThickMm.ToString("0.#", CultureInfo.InvariantCulture) + "mm）";
                return 6;
            }

            // 7. 机加件：长度 > 800mm -> 3类大板；否则 -> 4类小机加件
            if (hasMetrics && item.MaxLengthMm > 800.0) {
                reason = "机加大板" + metricTag;
                return 3;
            }
            reason = "机加小件" + metricTag;
            return 4;
        }

        static bool IsCylindricalBox(BomItem item) {
            if (item.MinThickMm <= 0 || item.MidDimMm <= 0 || item.MaxLengthMm <= 0) return false;
            // 回转体至少有两个包围盒方向尺寸近似相等（误差 <= 5%）
            double d1 = Math.Abs(item.MidDimMm - item.MinThickMm) / Math.Max(1.0, item.MidDimMm);
            double d2 = Math.Abs(item.MaxLengthMm - item.MidDimMm) / Math.Max(1.0, item.MaxLengthMm);
            return d1 <= 0.05 || d2 <= 0.05;
        }

        static string BuildMetricTag(BomItem item) {
            if (!item.MetricsLoaded) return "";
            var parts = new List<string>();
            if (item.MaxLengthMm > 0) parts.Add(Math.Round(item.MaxLengthMm).ToString("0", CultureInfo.InvariantCulture) + "mm");
            if (item.MassKg > 0) parts.Add(item.MassKg.ToString("0.0", CultureInfo.InvariantCulture) + "kg");
            return parts.Count > 0 ? "（" + string.Join(" / ", parts.ToArray()) + "）" : "";
        }

        // ---- 加工件筛选与工程图配对 ----
        static readonly Regex VendorPrefix = new Regex(@"^(MISUMI|OMRON|SMC|CKD|FESTO|AIRTAC|THK|HIWIN|NSK|SKF|KEYENCE|SICK|IGUS|MV-|MVL-|PISCO|KOGANEI|NBK|GB|GB╱T|GB/T|ISO|DIN|JIS|ANSI|IDEC|HW1|BM-)", RegexOptions.IgnoreCase);
        static readonly Regex StdPartWords = new Regex(@"螺钉|螺栓|螺母|垫圈|垫片|弹垫|平垫|销钉|圆柱销|轴承|弹簧|卡簧|挡圈|铆钉|拉铆|铰链|把手|脚杯|万向轮|气缸|电机|传感器|接头|堵头|导轨|滑块|丝杆|联轴器|蜂鸣器|指示灯|钥匙开关|平头按钮|Bearing|Screw|Bolt|Nut|Washer", RegexOptions.IgnoreCase);
        static readonly Regex BearingCode = new Regex(@"^B?\d{3,5}(ZZ|2RS|RS)?$", RegexOptions.IgnoreCase);
        static readonly Regex CompanyDrawingPattern = new Regex(@"^[A-Za-z]{2,6}\d{2}\.\d{2}[A-Za-z0-9]*-[A-Za-z0-9]+", RegexOptions.IgnoreCase);
        static readonly Regex ProjectPrefixPattern = new Regex(@"^([A-Za-z]{1,6}\d{2}\.\d{2}[A-Za-z0-9]*)", RegexOptions.IgnoreCase);

        public static string ExtractProjectCode(string name) {
            var m = ProjectPrefixPattern.Match((name ?? "").Trim());
            return m.Success ? m.Groups[1].Value : "";
        }

        public static bool HasCompanyDrawingNumber(string name) {
            return CompanyDrawingPattern.IsMatch((name ?? "").Trim());
        }

        public static bool IsCustomBomPart(string filePath, string topProject, bool hasDraft, out string reason) {
            reason = "";
            string name = Path.GetFileNameWithoutExtension(filePath ?? "");
            string dir = Path.GetDirectoryName(filePath ?? "") ?? "";
            if (dir.IndexOf("Standard Parts", StringComparison.OrdinalIgnoreCase) >= 0 || dir.IndexOf("标准件", StringComparison.Ordinal) >= 0) {
                reason = "标准件库";
                return false;
            }
            if (VendorPrefix.IsMatch(name) || BearingCode.IsMatch(name)) {
                reason = "外购件 / 标准件";
                return false;
            }
            // 有同名工程图的一律默认是待出清单的加工件（放在外购词正则之前，避免「气缸支架 / 电机安装板 / 轴承座 / 传感器支架 / 调整垫片」等自制件误判为外购件）
            if (hasDraft) return true;
            // 有公司图号（如 SK26.01-SD-15-009 气缸支架）默认视为加工件
            string p = ExtractProjectCode(name);
            if (HasCompanyDrawingNumber(name)) {
                if (!string.IsNullOrEmpty(topProject) && !string.Equals(p, topProject, StringComparison.OrdinalIgnoreCase)) {
                    reason = "别的项目（" + p + "）";
                    return false;
                }
                return true;
            }
            if (StdPartWords.IsMatch(name)) {
                reason = "外购件 / 标准件";
                return false;
            }
            if (!string.IsNullOrEmpty(topProject) && p.Length == 0) {
                reason = "非公司图号";
                return false;
            }
            reason = "未配对工程图且非标准图号";
            return false;
        }

        // 从装配直接子项映射（父装配路径 -> 直接可见子实例路径列表，含同层重复实例）按 DAG 递归累乘全树真实总数量。
        // 例：总装含 4 个子装配 A，每个 A 含 2 个零件 P → P 的总数量 = 4 × 2 = 8。
        // 同层先汇总局部计数再递归，并用路径 visiting 集防异常循环引用，既保持每个子装配只做一次 COM 遍历又保证数量准确。
        internal static Dictionary<string, int> ComputeAssemblyCounts(string rootPath, IDictionary<string, List<string>> childrenOf) {
            var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(rootPath) || childrenOf == null) return total;
            AccumulateAssemblyCounts(rootPath, 1, childrenOf, total, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return total;
        }

        static void AccumulateAssemblyCounts(string asmPath, int multiplier, IDictionary<string, List<string>> childrenOf,
            Dictionary<string, int> total, HashSet<string> visiting) {
            if (multiplier <= 0 || !visiting.Add(asmPath)) return;
            try {
                List<string> kids;
                if (!childrenOf.TryGetValue(asmPath, out kids) || kids == null || kids.Count == 0) return;
                var local = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < kids.Count; i++) {
                    string k = kids[i];
                    if (string.IsNullOrEmpty(k)) continue;
                    int c;
                    local.TryGetValue(k, out c);
                    local[k] = c + 1;
                }
                foreach (var kv in local) {
                    long prod = (long)multiplier * kv.Value;
                    int added = prod > int.MaxValue ? int.MaxValue : (int)prod;
                    int prev;
                    total.TryGetValue(kv.Key, out prev);
                    long sum = (long)prev + added;
                    total[kv.Key] = sum > int.MaxValue ? int.MaxValue : (int)sum;
                    if (childrenOf.ContainsKey(kv.Key)) {
                        AccumulateAssemblyCounts(kv.Key, added, childrenOf, total, visiting);
                    }
                }
            } finally {
                visiting.Remove(asmPath);
            }
        }

        public static string PairDraftPath(string modelPath) {
            if (string.IsNullOrEmpty(modelPath)) return "";
            try {
                string dft = Path.ChangeExtension(modelPath, ".dft");
                if (File.Exists(dft)) return dft;
            } catch { }
            return "";
        }

        public static string PairModelPathForDraft(string draftPath) {
            if (string.IsNullOrEmpty(draftPath)) return "";
            foreach (string ext in new[] { ".par", ".psm", ".asm" }) {
                try {
                    string candidate = Path.ChangeExtension(draftPath, ext);
                    if (File.Exists(candidate)) return candidate;
                } catch { }
            }
            return "";
        }

        public static bool IsModelExtension(string path) {
            string ext = (Path.GetExtension(path ?? "") ?? "").ToLowerInvariant();
            return ext == ".par" || ext == ".psm" || ext == ".asm";
        }

        // 安全遍历目录内的三维模型（支持可选递归子文件夹）：
        // 跳过隐藏/系统/符号链接目录、临时锁文件（~*），并自动跳过已导出的《加工件清单》成品目录防止重复扫入。
        public static List<string> ScanFolderModels(string rootFolder, bool recursive) {
            var results = new List<string>();
            if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder)) return results;

            string rootFull;
            try { rootFull = Path.GetFullPath(rootFolder.TrimEnd('\\', '/')); }
            catch { rootFull = rootFolder; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(rootFull);

            while (stack.Count > 0) {
                string dir = stack.Pop();
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (string f in files) {
                    if (!IsModelExtension(f)) continue;
                    string fn = Path.GetFileName(f) ?? "";
                    if (fn.Length == 0 || fn[0] == '~') continue;
                    string full = f;
                    try { full = Path.GetFullPath(f); } catch { }
                    if (seen.Add(full)) results.Add(full);
                }

                if (!recursive) continue;

                string[] subdirs;
                try { subdirs = Directory.GetDirectories(dir); }
                catch { continue; }
                Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
                for (int i = subdirs.Length - 1; i >= 0; i--) {
                    string sub = subdirs[i];
                    if (ShouldSkipScanSubdir(sub)) continue;
                    stack.Push(sub);
                }
            }

            results.Sort((a, b) => {
                string ra = RelativeSubFolder(rootFull, a);
                string rb = RelativeSubFolder(rootFull, b);
                int c = StringComparer.OrdinalIgnoreCase.Compare(ra, rb);
                if (c != 0) return c;
                return StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a), Path.GetFileName(b));
            });
            return results;
        }

        static bool ShouldSkipScanSubdir(string sub) {
            try {
                var attr = File.GetAttributes(sub);
                if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) return true;
                string name = Path.GetFileName(sub.TrimEnd('\\', '/')) ?? "";
                if (name.Length == 0 || name[0] == '.') return true;
                // 跳过同目录下之前导出的清单成品目录（目录名含「加工件清单」且里面已有清单 .xlsx），避免重复扫出同一批零件
                if (name.IndexOf("加工件清单", StringComparison.Ordinal) >= 0) {
                    try {
                        if (Directory.GetFiles(sub, "*加工件清单*.xlsx").Length > 0) return true;
                    } catch { }
                }
            } catch {
                return true;
            }
            return false;
        }

        public static string RelativeSubFolder(string rootFolder, string filePath) {
            if (string.IsNullOrEmpty(rootFolder) || string.IsNullOrEmpty(filePath)) return "";
            try {
                string root = Path.GetFullPath(rootFolder.TrimEnd('\\', '/'));
                string dir = Path.GetFullPath((Path.GetDirectoryName(filePath) ?? "").TrimEnd('\\', '/'));
                if (string.Equals(root, dir, StringComparison.OrdinalIgnoreCase)) return "";
                string prefix = root + Path.DirectorySeparatorChar;
                if (dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return dir.Substring(prefix.Length);
            } catch { }
            return "";
        }

        // 从候选模型列表里推断主项目号：优先按「有同名 .dft 的文件」里出现最多的项目号，避免深层参考件带偏主项目
        public static string PickTopProject(IEnumerable<string> modelFiles) {
            if (modelFiles == null) return "";
            var all = modelFiles.ToList();
            var withDft = MostCommon(all.Where(f => PairDraftPath(f).Length > 0).Select(f => ExtractProjectCode(Path.GetFileNameWithoutExtension(f))));
            if (withDft.Length > 0) return withDft;
            return MostCommon(all.Select(f => ExtractProjectCode(Path.GetFileNameWithoutExtension(f))));
        }

        // ---- 表头自动推断 ----
        public static void InferHeader(IList<BomItem> items, string sourcePath, BomHeader header) {
            if (header == null) return;
            var active = items == null ? new List<BomItem>() : items.Where(i => i.Selected).ToList();
            if (active.Count == 0 && items != null) active = items.ToList();

            // 1. 项目编号（从图号提取，取出现最多的）
            var proj = MostCommon(active.Select(i => ExtractProjectCode(i.DrawingNo)));
            if (proj.Length > 0) header.ProjectCode = proj;

            // 2. 机构号（图号去掉最后一段序号，例如 SK26.01-SD-15-009 -> SK26.01-SD-15）
            var mech = MostCommon(active.Select(i => ExtractMechanismNo(i.DrawingNo)));
            if (mech.Length > 0) header.MechanismNo = mech;

            // 3. 项目名称（从零件属性「项目名称」提取）
            var rawProjName = MostCommon(active.Select(i => i.ProjectNameHint));
            if (rawProjName.Length > 0) {
                string cleaned = rawProjName;
                if (header.ProjectCode.Length > 0 && cleaned.StartsWith(header.ProjectCode, StringComparison.OrdinalIgnoreCase)) {
                    cleaned = cleaned.Substring(header.ProjectCode.Length).TrimStart('-', '_', ' ', '—');
                }
                if (cleaned.Length > 0) header.ProjectName = cleaned;
            }

            // 4. 工程师姓名（从零件属性「设计」/「作者」提取）
            var eng = MostCommon(active.Select(i => i.DesignerHint));
            if (eng.Length > 0) header.EngineerName = eng;

            // 5. 清单名称（来自源装配名或源文件夹名）
            if (!string.IsNullOrEmpty(sourcePath)) {
                string label = "";
                try {
                    if (Directory.Exists(sourcePath)) label = Path.GetFileName(sourcePath.TrimEnd('\\', '/'));
                    else label = Path.GetFileNameWithoutExtension(sourcePath);
                } catch { }
                if (!string.IsNullOrEmpty(label) && !HasCompanyDrawingNumber(label)) {
                    header.ListTitle = label;
                } else if (string.IsNullOrEmpty(header.ListTitle) && !string.IsNullOrEmpty(label)) {
                    header.ListTitle = label;
                }
            }
        }

        public static string ExtractMechanismNo(string drawingNo) {
            string s = (drawingNo ?? "").Trim();
            if (s.Length == 0) return "";
            int lastDash = s.LastIndexOf('-');
            if (lastDash <= 0) return "";
            string prefix = s.Substring(0, lastDash);
            // 至少包含一个 '-'（如 SK26.01-SD-15）
            return prefix.IndexOf('-') > 0 ? prefix : "";
        }

        static string MostCommon(IEnumerable<string> values) {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string best = ""; int bestCount = 0;
            foreach (string raw in values) {
                string s = (raw ?? "").Trim();
                if (s.Length == 0) continue;
                int c; counts.TryGetValue(s, out c); c++; counts[s] = c;
                if (c > bestCount) { bestCount = c; best = s; }
            }
            return best;
        }

        // ---- 标准命名 ----
        // 文件夹：SK26.01-C10-01-加工件清单（按钮盒支架）-苏英治-20261121
        public static string SuggestFolderName(BomHeader h) {
            if (h == null) return "加工件清单-" + DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var parts = new List<string>();
            AddIfNonEmpty(parts, h.ProjectCode);
            AddIfNonEmpty(parts, h.StationNo);
            AddIfNonEmpty(parts, h.SequenceNo);
            string title = "加工件清单" + (string.IsNullOrWhiteSpace(h.ListTitle) ? "" : "（" + h.ListTitle.Trim() + "）");
            parts.Add(title);
            AddIfNonEmpty(parts, h.EngineerName);
            parts.Add(h.IssueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            return SanitizeFileName(string.Join("-", parts.ToArray()));
        }

        // 加工件清单表格：SK26.01-C10-01-按钮盒支架加工件清单-苏英治-20261121.xlsx
        public static string SuggestListFileName(BomHeader h) {
            if (h == null) return "加工件清单-" + DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".xlsx";
            var parts = new List<string>();
            AddIfNonEmpty(parts, h.ProjectCode);
            AddIfNonEmpty(parts, h.StationNo);
            AddIfNonEmpty(parts, h.SequenceNo);
            string mid = (string.IsNullOrWhiteSpace(h.ListTitle) ? "" : h.ListTitle.Trim()) + "加工件清单";
            parts.Add(mid);
            AddIfNonEmpty(parts, h.EngineerName);
            parts.Add(h.IssueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            return SanitizeFileName(string.Join("-", parts.ToArray())) + ".xlsx";
        }

        public static string SuggestCodeFileName() {
            return "编码申请-加工件.xlsx";
        }

        public static string SuggestDefaultOutputFolder(string sourceDir, BomHeader h) {
            string folderName = SuggestFolderName(h);
            string parent = "";
            try {
                if (!string.IsNullOrEmpty(sourceDir)) {
                    string full = Path.GetFullPath(sourceDir.TrimEnd('\\', '/'));
                    string up = Path.GetDirectoryName(full);
                    parent = !string.IsNullOrEmpty(up) ? up : full;
                }
            } catch { }
            if (string.IsNullOrEmpty(parent)) {
                parent = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            return Path.Combine(parent, folderName);
        }

        static void AddIfNonEmpty(List<string> list, string value) {
            string s = (value ?? "").Trim();
            if (s.Length > 0) list.Add(s);
        }

        public static string SanitizeFileName(string name) {
            string s = (name ?? "").Trim();
            if (s.Length == 0) return "未命名";
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            }
            return sb.ToString().Trim().TrimEnd('.');
        }

        // ---- 导出前校验 ----
        public static List<BomIssue> Validate(IList<BomItem> items, BomOptions options) {
            var issues = new List<BomIssue>();
            var selected = items == null ? new List<BomItem>() : items.Where(i => i.Selected).ToList();
            if (selected.Count == 0) {
                issues.Add(new BomIssue { Blocking = true, Message = "没有勾选要出清单的零件。" });
                return issues;
            }
            var seenDrawings = new Dictionary<string, BomItem>(StringComparer.OrdinalIgnoreCase);
            bool needDraft = options != null && (options.IncludeDraft || options.IncludeDwg || options.IncludeJpg || options.IncludePdf);
            foreach (var item in selected) {
                string dno = (item.DrawingNo ?? "").Trim();
                if (dno.Length == 0) {
                    issues.Add(new BomIssue { Blocking = true, Item = item, Message = item.ModelFileName + "：图号为空（无法命名导出的文件）" });
                } else {
                    string safe = SanitizeFileName(dno);
                    if (seenDrawings.ContainsKey(safe)) {
                        issues.Add(new BomIssue { Blocking = true, Item = item, Message = "图号重复：" + dno + "（导出文件会互相覆盖）" });
                    } else {
                        seenDrawings[safe] = item;
                    }
                }
                if (string.IsNullOrWhiteSpace(item.PartName)) {
                    issues.Add(new BomIssue { Blocking = false, Item = item, Message = (dno.Length > 0 ? dno : item.ModelFileName) + "：零件名称为空" });
                }
                if (string.IsNullOrWhiteSpace(item.Material)) {
                    issues.Add(new BomIssue { Blocking = false, Item = item, Message = (dno.Length > 0 ? dno : item.ModelFileName) + "：材料为空" });
                }
                if (item.Quantity <= 0) {
                    issues.Add(new BomIssue { Blocking = true, Item = item, Message = (dno.Length > 0 ? dno : item.ModelFileName) + "：数量必须大于 0" });
                }
                if (item.Category < 1 || item.Category > 8) {
                    issues.Add(new BomIssue { Blocking = true, Item = item, Message = (dno.Length > 0 ? dno : item.ModelFileName) + "：八大类类别必须是 1～8" });
                }
                if (needDraft && !item.HasDraft) {
                    issues.Add(new BomIssue { Blocking = false, Item = item, Message = (dno.Length > 0 ? dno : item.ModelFileName) + "：缺少同名工程图（.dft），将跳过 .dft/.dwg/.jpg/.pdf" });
                }
            }
            return issues;
        }
    }
}
