using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    // 阀岛/阀组摘要：供方案 A 左侧阀岛树展示，实时聚合显式配置与记录推导
    public sealed class LineupIslandSummary {
        public string Key = "";
        public string Prefix = "";
        public int Number;
        public string Kind = "阀岛";
        public string Model = "";
        public string IslandRecordId = "";
        public bool Has3DBind;
        public string BindLabel = "";
        public int ValveCount;
        public int SpareCount;
        public int CylinderCount;
        public int SensorCount;
        public int MissingValveCylCount;
        public int MissingSensorCylCount;
    }

    // 阀路总览行：把「阀片 ↔ 气缸 ↔ H/W 磁开」按物理阀路自然配对，支持 1阀1缸、1阀多缸、预留盖板与待配阀气缸
    public sealed class LineupChannelRow {
        public LineupRecord Valve;
        public LineupRecord Cylinder;
        public List<LineupRecord> Sensors = new List<LineupRecord>();
        public bool IsContinuation; // 一阀控多缸时，第 2 个及后续气缸行标记为同阀续行
        public bool IsSpare;        // 预留 / 盖板阀片
        public bool IsBlowOrVacuum; // 吹气 / 破真空（无需 H/W 磁开）

        public string PrimaryRecordId {
            get { return Cylinder != null ? Cylinder.Id : (Valve != null ? Valve.Id : ""); }
        }
    }

    public sealed class LineupBundleResult {
        public string ValveId = "";
        public string CylinderId = "";
        public List<string> SensorIds = new List<string>();
    }

    // 方案 A 气动主线纯逻辑层（零 UI / CAD 依赖，可直接由单元测试全覆盖验证）：
    //   · 显式管理 阀岛(Y*) → 阀片(V**) → 气缸(C**) → 气缸传感器(H/W) 四级联动；
    //   · 严格向后兼容旧 .lineup.xml、LineupXlsxWriter 5-sheet 导出与既有自动化测试约定。
    public static class LineupPneumaticOps {
        static readonly Regex IslandKeyRegex = new Regex(@"^(.+)-Y([0-9]+)$", RegexOptions.IgnoreCase);
        static readonly Regex SlotRegex = new Regex(@"-Y[0-9]+[CV]([0-9]+)", RegexOptions.IgnoreCase);
        static readonly Regex BlowRegex = new Regex(@"C[0-9]+[a-z]$", RegexOptions.IgnoreCase);

        public static bool ParseIslandKey(string key, out string prefix, out int num) {
            prefix = "";
            num = 0;
            if (string.IsNullOrWhiteSpace(key)) return false;
            var m = IslandKeyRegex.Match(key.Trim());
            if (!m.Success) return false;
            int parsed;
            if (!int.TryParse(m.Groups[2].Value, out parsed) || parsed < 1) return false;
            prefix = m.Groups[1].Value.Trim();
            num = parsed;
            return prefix.Length > 0;
        }

        public static string DefaultPrefix(LineupProject p) {
            if (p == null) return "OK170";
            if (!string.IsNullOrWhiteSpace(p.NumberPrefix)) return p.NumberPrefix.Trim();
            if (!string.IsNullOrWhiteSpace(p.StationCode)) return p.StationCode.Trim();
            foreach (var isl in p.Islands ?? new List<LineupIsland>()) {
                string pre; int n;
                if (ParseIslandKey(isl.Key, out pre, out n)) return pre;
            }
            return "ST01";
        }

        public static int NextIslandNumber(LineupProject p, string prefix) {
            prefix = (prefix ?? "").Trim();
            int max = 0;
            if (p != null) {
                foreach (var isl in p.Islands ?? new List<LineupIsland>()) {
                    string pre; int n;
                    if (ParseIslandKey(isl.Key, out pre, out n) && (prefix.Length == 0 || string.Equals(pre, prefix, StringComparison.OrdinalIgnoreCase)))
                        if (n > max) max = n;
                }
                foreach (var r in p.Records ?? new List<LineupRecord>()) {
                    string key = r.Category == "阀岛" ? (r.Number ?? "").Trim() : LineupNumbering.IslandKey(r.Number);
                    string pre; int n;
                    if (ParseIslandKey(key, out pre, out n) && (prefix.Length == 0 || string.Equals(pre, prefix, StringComparison.OrdinalIgnoreCase)))
                        if (n > max) max = n;
                }
            }
            return max + 1;
        }

        public static bool IsSpareRecord(LineupRecord r) {
            if (r == null) return false;
            string s = (r.Description ?? "") + " " + (r.Parameters ?? "") + " " + (r.Model ?? "") + " " + (r.Notes ?? "");
            return s.Contains("预留") || s.Contains("备用") || s.Contains("盖板") || s.Contains("空位");
        }

        public static bool IsBlowOrVacuum(LineupRecord r) {
            if (r == null) return false;
            string d = (r.Description ?? "") + " " + (r.Number ?? "") + " " + (r.Home ?? "");
            return d.Contains("吹气") || d.Contains("破真空") || d.Contains("吸盘") || d.Contains("减压阀")
                || d.Contains("非真空") || d.Contains("真空发生") || BlowRegex.IsMatch((r.Number ?? "").Trim());
        }

        // 自然提取当前阀岛下的最大工位序号（同时考察 V** 与 C**，防止预留阀片与新增气缸撞号）
        public static int NextSlotNumber(LineupProject p, string islandKey) {
            int max = 0;
            if (p == null || string.IsNullOrWhiteSpace(islandKey)) return 1;
            var pat = new Regex("^" + Regex.Escape(islandKey.Trim()) + "[CV]([0-9]+)(?:-[0-9]+|[a-z])?$", RegexOptions.IgnoreCase);
            foreach (var r in p.Records ?? new List<LineupRecord>()) {
                string cat = LineupTableData.Category(r.Category);
                if (cat != "气缸" && cat != "阀片") continue;
                var m = pat.Match((r.Number ?? "").Trim());
                int v;
                if (m.Success && int.TryParse(m.Groups[1].Value, out v) && v > max) max = v;
            }
            return max + 1;
        }

        static int ExtractSlotSortKey(string number) {
            var m = SlotRegex.Match((number ?? "").Trim());
            int v;
            return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : 999999;
        }

        // 汇总项目内全部阀岛（显式 Islands 配置 + 记录中推断出的 Y* 键），按工位号与 Y 序号自然排序
        public static List<LineupIslandSummary> ListIslands(LineupProject p) {
            var list = new List<LineupIslandSummary>();
            if (p == null) return list;

            var byKey = new Dictionary<string, LineupIslandSummary>(StringComparer.OrdinalIgnoreCase);
            Func<string, LineupIslandSummary> ensure = key => {
                key = (key ?? "").Trim();
                string pre; int num;
                if (!ParseIslandKey(key, out pre, out num)) return null;
                LineupIslandSummary sum;
                if (!byKey.TryGetValue(key, out sum)) {
                    sum = new LineupIslandSummary { Key = key, Prefix = pre, Number = num, Kind = "阀岛" };
                    byKey[key] = sum;
                }
                return sum;
            };

            foreach (var isl in p.Islands ?? new List<LineupIsland>()) {
                var s = ensure(isl.Key);
                if (s == null) continue;
                s.Kind = string.IsNullOrWhiteSpace(isl.Kind) ? "阀岛" : isl.Kind.Trim();
                s.Model = (isl.Model ?? "").Trim();
            }

            var islandRecById = new Dictionary<string, LineupRecord>(StringComparer.Ordinal);
            foreach (var r in p.Records ?? new List<LineupRecord>()) {
                if (r.Category == "阀岛") {
                    islandRecById[r.Id] = r;
                    var s = ensure(r.Number);
                    if (s != null) {
                        s.IslandRecordId = r.Id;
                        if (string.IsNullOrWhiteSpace(s.Model) && !string.IsNullOrWhiteSpace(r.Model)) s.Model = r.Model.Trim();
                        if (!string.IsNullOrEmpty(r.ReferenceKey)) {
                            s.Has3DBind = true;
                            string path = string.Join(" / ", r.InstancePath ?? new string[0]);
                            int slash = path.LastIndexOf('/');
                            s.BindLabel = slash >= 0 ? path.Substring(slash + 1).Trim() : path;
                        }
                    }
                }
            }

            // 从阀片与气缸中补全未显式配置的 Y* 阀岛
            foreach (var r in p.Records ?? new List<LineupRecord>()) {
                string cat = LineupTableData.Category(r.Category);
                if (cat != "阀片" && cat != "气缸") continue;
                string key = ResolveIslandKey(p, r, islandRecById);
                if (key.Length > 0) ensure(key);
            }

            foreach (var sum in byKey.Values) {
                var channels = BuildChannelRows(p, sum.Key);
                var countedValves = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ch in channels) {
                    if (ch.Valve != null && countedValves.Add(ch.Valve.Id)) {
                        sum.ValveCount++;
                        if (ch.IsSpare) sum.SpareCount++;
                    }
                    if (ch.Cylinder != null) {
                        sum.CylinderCount++;
                        sum.SensorCount += ch.Sensors.Count;
                        if (ch.Valve == null && !IsSpareRecord(ch.Cylinder)) sum.MissingValveCylCount++;
                        if (!ch.IsBlowOrVacuum && !IsSpareRecord(ch.Cylinder) && ch.Sensors.Count < 2) sum.MissingSensorCylCount++;
                    }
                }
            }

            return byKey.Values
                .OrderBy(x => x.Prefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Number)
                .ToList();
        }

        static string ResolveIslandKey(LineupProject p, LineupRecord r, Dictionary<string, LineupRecord> islandRecById) {
            string key = LineupNumbering.IslandKey(r.Number);
            if (key.Length > 0) return key;
            LineupRecord owner;
            if (!string.IsNullOrEmpty(r.IslandId) && islandRecById.TryGetValue(r.IslandId, out owner) && !string.IsNullOrWhiteSpace(owner.Number))
                return owner.Number.Trim();
            return "";
        }

        // 构建指定阀岛的完整「阀路总览」行列表（无损覆盖 1阀1缸、1阀多缸、预留盖板与无阀气缸）
        public static List<LineupChannelRow> BuildChannelRows(LineupProject p, string islandKey) {
            var result = new List<LineupChannelRow>();
            if (p == null || string.IsNullOrWhiteSpace(islandKey)) return result;

            var records = p.Records ?? new List<LineupRecord>();
            var islandRec = records.FirstOrDefault(r => r.Category == "阀岛" && string.Equals((r.Number ?? "").Trim(), islandKey.Trim(), StringComparison.OrdinalIgnoreCase));
            string islandRecId = islandRec == null ? "" : islandRec.Id;

            var valves = records
                .Where(r => LineupTableData.Category(r.Category) == "阀片" && BelongsToIsland(r, islandKey, islandRecId))
                .OrderBy(r => ExtractSlotSortKey(r.Number))
                .ThenBy(r => r.Number ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            var valveIds = new HashSet<string>(valves.Select(v => v.Id), StringComparer.Ordinal);
            var valveByNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in valves) {
                string vn = (v.Number ?? "").Trim();
                if (vn.Length > 0 && !valveByNum.ContainsKey(vn)) valveByNum[vn] = v;
            }

            var cyls = records
                .Where(r => LineupTableData.Category(r.Category) == "气缸"
                    && (BelongsToIsland(r, islandKey, islandRecId)
                        // 若气缸自身编号已明确归属其他阀岛，绝不再被旧 ValveId 拉回本岛造成跨岛双倍统计
                        || (LineupNumbering.IslandKey(r.Number).Length == 0 && !string.IsNullOrEmpty(r.ValveId) && valveIds.Contains(r.ValveId))))
                .OrderBy(r => ExtractSlotSortKey(r.Number))
                .ThenBy(r => r.Number ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sensorsByCyl = GroupSensorsByCylinder(records, cyls);

            // 把气缸按所属阀片分组（优先 ValveId，其次按编号 V** ↔ C** 映射）
            var cylsByValve = new Dictionary<string, List<LineupRecord>>(StringComparer.Ordinal);
            var unattachedCyls = new List<LineupRecord>();
            foreach (var c in cyls) {
                LineupRecord ownerValve = null;
                if (!string.IsNullOrEmpty(c.ValveId) && valveIds.Contains(c.ValveId))
                    ownerValve = valves.FirstOrDefault(v => v.Id == c.ValveId);
                if (ownerValve == null) {
                    string vn = LineupNumbering.ValveNumberForCylinder(c.Number);
                    if (vn.Length > 0) valveByNum.TryGetValue(vn, out ownerValve);
                }
                if (ownerValve != null) {
                    List<LineupRecord> bucket;
                    if (!cylsByValve.TryGetValue(ownerValve.Id, out bucket)) { bucket = new List<LineupRecord>(); cylsByValve[ownerValve.Id] = bucket; }
                    bucket.Add(c);
                } else {
                    unattachedCyls.Add(c);
                }
            }

            foreach (var v in valves) {
                List<LineupRecord> bound;
                if (!cylsByValve.TryGetValue(v.Id, out bound) || bound.Count == 0) {
                    result.Add(new LineupChannelRow {
                        Valve = v,
                        Cylinder = null,
                        IsContinuation = false,
                        IsSpare = IsSpareRecord(v)
                    });
                } else {
                    for (int i = 0; i < bound.Count; i++) {
                        var c = bound[i];
                        List<LineupRecord> slist;
                        sensorsByCyl.TryGetValue(c.Id, out slist);
                        result.Add(new LineupChannelRow {
                            Valve = v,
                            Cylinder = c,
                            Sensors = slist ?? new List<LineupRecord>(),
                            IsContinuation = i > 0,
                            IsSpare = false,
                            IsBlowOrVacuum = IsBlowOrVacuum(c)
                        });
                    }
                }
            }

            foreach (var c in unattachedCyls) {
                List<LineupRecord> slist;
                sensorsByCyl.TryGetValue(c.Id, out slist);
                result.Add(new LineupChannelRow {
                    Valve = null,
                    Cylinder = c,
                    Sensors = slist ?? new List<LineupRecord>(),
                    IsContinuation = false,
                    IsSpare = IsSpareRecord(c),
                    IsBlowOrVacuum = IsBlowOrVacuum(c)
                });
            }

            return result;
        }

        // 查找未归属到任何 Y* 阀岛的孤立气缸/阀片（如手工自定义编号或早期导入件）
        public static List<LineupRecord> FindOrphanPneumaticRecords(LineupProject p) {
            var list = new List<LineupRecord>();
            if (p == null || p.Records == null) return list;
            var islandIds = new HashSet<string>(p.Records.Where(r => r.Category == "阀岛").Select(r => r.Id), StringComparer.Ordinal);
            var valveIds = new HashSet<string>(p.Records.Where(r => LineupTableData.Category(r.Category) == "阀片" && (LineupNumbering.IslandKey(r.Number).Length > 0 || islandIds.Contains(r.IslandId))).Select(r => r.Id), StringComparer.Ordinal);
            foreach (var r in p.Records) {
                string cat = LineupTableData.Category(r.Category);
                if (cat != "气缸" && cat != "阀片") continue;
                bool hasIsland = LineupNumbering.IslandKey(r.Number).Length > 0
                    || (!string.IsNullOrEmpty(r.IslandId) && islandIds.Contains(r.IslandId))
                    || (cat == "气缸" && !string.IsNullOrEmpty(r.ValveId) && valveIds.Contains(r.ValveId));
                if (!hasIsland) list.Add(r);
            }
            return list;
        }

        static bool BelongsToIsland(LineupRecord r, string islandKey, string islandRecId) {
            string k = LineupNumbering.IslandKey(r.Number);
            if (k.Length > 0) return string.Equals(k, islandKey, StringComparison.OrdinalIgnoreCase);
            return islandRecId.Length > 0 && string.Equals(r.IslandId, islandRecId, StringComparison.Ordinal);
        }

        static Dictionary<string, List<LineupRecord>> GroupSensorsByCylinder(List<LineupRecord> all, List<LineupRecord> cyls) {
            var map = new Dictionary<string, List<LineupRecord>>(StringComparer.Ordinal);
            var cylByNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            var cylIdSet = new HashSet<string>(cyls.Select(c => c.Id), StringComparer.Ordinal);
            var allCylIdSet = new HashSet<string>(all.Where(r => LineupTableData.Category(r.Category) == "气缸").Select(c => c.Id), StringComparer.Ordinal);
            foreach (var c in cyls) {
                map[c.Id] = new List<LineupRecord>();
                string cn = (c.Number ?? "").Trim();
                if (cn.Length > 0 && !cylByNum.ContainsKey(cn)) cylByNum[cn] = c;
            }
            foreach (var s in all.Where(r => LineupTableData.Category(r.Category) == "气缸传感器")) {
                string targetId = "";
                // 若磁开已显式指向项目内某支真实气缸（例如在另一阀岛），则只归属该气缸，不再按编号兜底导致跨岛重复计入
                if (!string.IsNullOrEmpty(s.CylinderId) && allCylIdSet.Contains(s.CylinderId)) {
                    if (cylIdSet.Contains(s.CylinderId)) targetId = s.CylinderId;
                } else {
                    var m = Regex.Match((s.Number ?? "").Trim(), @"^(.+?)([HWM][0-9]*)$", RegexOptions.IgnoreCase);
                    LineupRecord c;
                    if (m.Success && cylByNum.TryGetValue(m.Groups[1].Value.Trim(), out c)) targetId = c.Id;
                }
                if (targetId.Length > 0) map[targetId].Add(s);
            }
            return map;
        }

        // 确保显式“阀岛”记录存在（用于挂载 IslandId 与 3D 模型绑定）
        static LineupRecord EnsureIslandRecord(LineupProject p, string islandKey) {
            var rec = p.Records.FirstOrDefault(r => r.Category == "阀岛" && string.Equals((r.Number ?? "").Trim(), islandKey, StringComparison.OrdinalIgnoreCase));
            var cfg = LineupNumbering.IslandEntry(p, islandKey);
            if (rec == null) {
                rec = new LineupRecord {
                    Category = "阀岛",
                    Number = islandKey,
                    Model = cfg == null ? "" : (cfg.Model ?? ""),
                    Description = (cfg == null || string.IsNullOrWhiteSpace(cfg.Kind) ? "阀岛" : cfg.Kind.Trim()) + " " + islandKey
                };
                p.Records.Add(rec);
            } else if (cfg != null && !string.IsNullOrWhiteSpace(cfg.Model)) {
                rec.Model = cfg.Model.Trim();
            }
            return rec;
        }

        // 1. 新建阀岛 / 汇流排 / 阀组 / 单片阀
        public static string CreateIsland(LineupProject p, string prefix, int islandNum, string kind, string model, LineupRecord cadBind = null) {
            prefix = (prefix ?? "").Trim();
            if (prefix.Length == 0 || islandNum < 1) throw new InvalidOperationException("工位前缀不能为空，阀岛编号必须大于 0。");
            string key = prefix + "-Y" + islandNum;
            if (p.Islands != null && p.Islands.Any(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("阀岛 " + key + " 已存在，请修改编号或直接编辑原阀岛。");

            LineupNumbering.SetIsland(p, prefix, islandNum, (model ?? "").Trim(), string.IsNullOrWhiteSpace(kind) ? "阀岛" : kind.Trim());
            if (string.IsNullOrWhiteSpace(p.StationCode)) p.StationCode = prefix;

            if (cadBind != null && !string.IsNullOrEmpty(cadBind.ReferenceKey)) {
                var rec = EnsureIslandRecord(p, key);
                Apply3DBind(p, rec, cadBind);
            }
            return key;
        }

        // 2. 编辑已有阀岛的形态、型号或 3D 绑定
        public static void UpdateIsland(LineupProject p, string islandKey, string kind, string model, LineupRecord cadBind = null) {
            string pre; int num;
            if (!ParseIslandKey(islandKey, out pre, out num)) throw new InvalidOperationException("无效的阀岛编号：" + islandKey);
            string k = string.IsNullOrWhiteSpace(kind) ? "阀岛" : kind.Trim();
            string m = (model ?? "").Trim();
            LineupNumbering.SetIsland(p, pre, num, m, k);

            var rec = p.Records.FirstOrDefault(r => r.Category == "阀岛" && string.Equals((r.Number ?? "").Trim(), islandKey, StringComparison.OrdinalIgnoreCase));
            if (rec != null) {
                rec.Model = m;
                rec.Description = k + " " + islandKey;
            }
            if (cadBind != null && !string.IsNullOrEmpty(cadBind.ReferenceKey)) {
                if (rec == null) rec = EnsureIslandRecord(p, islandKey);
                Apply3DBind(p, rec, cadBind);
            }
        }

        // 3. 删除阀岛（支持带子件级联删除或先保护提示；删掉中间阀岛后同前缀后续阀岛 Y* 自动顺延重排）
        public static int DeleteIsland(LineupProject p, string islandKey, bool deleteChildren) {
            string delPre; int delNum;
            ParseIslandKey(islandKey, out delPre, out delNum);

            var islandRecIds = new HashSet<string>(
                p.Records.Where(r => r.Category == "阀岛" && string.Equals((r.Number ?? "").Trim(), islandKey, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id),
                StringComparer.Ordinal);

            var channels = BuildChannelRows(p, islandKey);
            var childIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ch in channels) {
                if (ch.Valve != null) childIds.Add(ch.Valve.Id);
                if (ch.Cylinder != null) childIds.Add(ch.Cylinder.Id);
                foreach (var s in ch.Sensors) childIds.Add(s.Id);
            }
            // 一并清理仍挂在该阀岛下（编号或 IslandId 指向本岛）的无主气缸传感器，避免顺排后悬空关联或撞号
            foreach (var r in p.Records) {
                if (LineupTableData.Category(r.Category) != "气缸传感器") continue;
                if (string.Equals(LineupNumbering.IslandKey(r.Number), islandKey, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(r.IslandId) && islandRecIds.Contains(r.IslandId)))
                    childIds.Add(r.Id);
            }
            if (childIds.Count > 0 && !deleteChildren)
                throw new InvalidOperationException("阀岛 " + islandKey + " 下仍有 " + childIds.Count + " 条关联记录，请确认是否一并删除。");

            var removedIds = new HashSet<string>(childIds, StringComparer.Ordinal);
            foreach (string id in islandRecIds) removedIds.Add(id);

            // 同步删除曾由本阀岛同步到「其他电气件 E**」的本体记录，防止顺排后续阀岛时出现两条同称 Y* 阀岛本体的幽灵电气件
            bool removedSyncedElec = false;
            foreach (var r in p.Records) {
                if (LineupTableData.Category(r.Category) == "其他电气件"
                    && string.Equals((r.Notes ?? "").Trim(), (islandKey ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) {
                    removedIds.Add(r.Id);
                    removedSyncedElec = true;
                }
            }

            if (removedIds.Count > 0) p.Records.RemoveAll(r => removedIds.Contains(r.Id));
            if (p.Islands != null) p.Islands.RemoveAll(i => string.Equals(i.Key, islandKey, StringComparison.OrdinalIgnoreCase));

            if (delPre.Length > 0) LineupNumbering.CompactIslands(p, delPre);
            if (removedSyncedElec) LineupNumbering.CompactLinearCategory(p, "其他电气件");
            LineupNumbering.SyncCadPageNumbers(p, removedIds);

            var remaining = ListIslands(p);
            if (remaining.Count > 0) {
                p.NumberPrefix = remaining[0].Prefix;
                p.ActiveIsland = remaining[0].Number;
            }
            return childIds.Count;
        }

        // 4. 在指定阀岛下新增一条气缸（默认一键配齐 阀片 + H/W 磁开，并沿用本岛常用阀片/磁开规格）
        public static LineupBundleResult AddCylinderBundle(
            LineupProject p, string islandKey, bool withValve, int sensorPairs, bool isBlowOrVacuum,
            LineupRecord template = null, LineupRecord cadBind = null) {

            string pre; int num;
            if (!ParseIslandKey(islandKey, out pre, out num)) throw new InvalidOperationException("请先新建或选择一个阀岛。");
            var cfg = LineupNumbering.IslandEntry(p, islandKey);
            if (cfg == null) LineupNumbering.SetIsland(p, pre, num, "", "阀岛");
            else { p.NumberPrefix = pre; p.ActiveIsland = num; }

            if (cadBind != null && !string.IsNullOrEmpty(cadBind.ReferenceKey)) {
                var conflict = p.Records.FirstOrDefault(r => string.Equals(r.ReferenceKey, cadBind.ReferenceKey, StringComparison.Ordinal));
                if (conflict != null) throw new InvalidOperationException("所选三维模型已绑定到 " + conflict.Number + "，请点选其他零件。");
            }

            int slot = NextSlotNumber(p, islandKey);
            string slotStr = slot.ToString("D2");
            var islandRec = EnsureIslandRecord(p, islandKey);
            var res = new LineupBundleResult();

            LineupRecord valve = null;
            if (withValve) {
                var valveSample = FindDefaultValveSpec(p, islandKey);
                valve = new LineupRecord {
                    Category = "阀片",
                    Number = islandKey + "V" + slotStr,
                    Parameters = template != null ? (template.Description ?? "") : (isBlowOrVacuum ? "吹气/破真空" : ""),
                    Description = valveSample.Description,
                    Model = valveSample.Model,
                    Brand = valveSample.Brand,
                    IslandId = islandRec.Id,
                    Mechanism = template != null ? (template.Mechanism ?? "") : "",
                    Notes = template != null ? (template.Notes ?? "") : ""
                };
                p.Records.Add(valve);
                res.ValveId = valve.Id;
            }

            var cyl = new LineupRecord {
                Category = "气缸",
                Number = islandKey + "C" + slotStr,
                Description = template != null ? (template.Description ?? "") : (isBlowOrVacuum ? "吹气/破真空口" : ""),
                Model = template != null ? (template.Model ?? "") : "",
                Brand = template != null ? (template.Brand ?? "") : "",
                Home = template != null && !string.IsNullOrWhiteSpace(template.Home) ? template.Home : (isBlowOrVacuum ? "非真空" : "缩回"),
                Mounting = template != null && !string.IsNullOrWhiteSpace(template.Mounting) ? template.Mounting : (isBlowOrVacuum ? "" : "水平安装"),
                Mechanism = template != null ? (template.Mechanism ?? "") : "",
                Notes = template != null ? (template.Notes ?? "") : "",
                ValveId = valve == null ? "" : valve.Id,
                IslandId = islandRec.Id
            };
            if (cadBind != null) {
                if (string.IsNullOrWhiteSpace(cyl.Model) && !string.IsNullOrWhiteSpace(cadBind.Model)) cyl.Model = cadBind.Model;
                if (string.IsNullOrWhiteSpace(cyl.Brand) && !string.IsNullOrWhiteSpace(cadBind.Brand)) cyl.Brand = cadBind.Brand;
                if (string.IsNullOrWhiteSpace(cyl.Description) && !string.IsNullOrWhiteSpace(cadBind.Description)) cyl.Description = cadBind.Description;
                if (string.IsNullOrWhiteSpace(cyl.Mechanism) && !string.IsNullOrWhiteSpace(cadBind.Mechanism)) cyl.Mechanism = cadBind.Mechanism;
                if (string.IsNullOrWhiteSpace(cyl.Notes) && !string.IsNullOrWhiteSpace(cadBind.Notes)) cyl.Notes = cadBind.Notes;
                Apply3DBind(p, cyl, cadBind);
                if (valve != null && string.IsNullOrWhiteSpace(valve.Parameters) && !string.IsNullOrWhiteSpace(cyl.Description))
                    valve.Parameters = cyl.Description;
            }
            p.Records.Add(cyl);
            res.CylinderId = cyl.Id;

            if (!isBlowOrVacuum && sensorPairs > 0) {
                var beforeIds = new HashSet<string>(p.Records.Select(r => r.Id), StringComparer.Ordinal);
                LineupNumbering.SensorPairsMulti(p, new[] { cyl.Id }, sensorPairs);
                var sensorSample = FindDefaultSensorSpec(p);
                foreach (var s in p.Records.Where(r => !beforeIds.Contains(r.Id))) {
                    if (!string.IsNullOrWhiteSpace(sensorSample.Model)) s.Model = sensorSample.Model;
                    if (!string.IsNullOrWhiteSpace(sensorSample.Brand)) s.Brand = sensorSample.Brand;
                    if (!string.IsNullOrWhiteSpace(sensorSample.Connector)) s.Connector = sensorSample.Connector;
                    res.SensorIds.Add(s.Id);
                }
            }

            LineupTableData.RejectDuplicates(p.Records);
            return res;
        }

        // 5. 直接向阀岛添加 1~N 片阀片（正常阀片或预留/盖板位）
        public static List<string> AddValveSlices(LineupProject p, string islandKey, bool isSpareCover, int count) {
            if (count < 1 || count > 64) throw new InvalidOperationException("单次添加阀片数量需在 1～64 之间。");
            string pre; int num;
            if (!ParseIslandKey(islandKey, out pre, out num)) throw new InvalidOperationException("请先新建或选择一个阀岛。");
            var cfg = LineupNumbering.IslandEntry(p, islandKey);
            if (cfg == null) LineupNumbering.SetIsland(p, pre, num, "", "阀岛");
            else { p.NumberPrefix = pre; p.ActiveIsland = num; }

            var islandRec = EnsureIslandRecord(p, islandKey);
            var valveSample = FindDefaultValveSpec(p, islandKey);
            var added = new List<string>();
            int nextSlot = NextSlotNumber(p, islandKey);
            for (int i = 0; i < count; i++) {
                var v = new LineupRecord {
                    Category = "阀片",
                    Number = islandKey + "V" + (nextSlot + i).ToString("D2"),
                    Parameters = isSpareCover ? "预留" : "",
                    Description = isSpareCover ? "盖板" : valveSample.Description,
                    Model = isSpareCover ? "" : valveSample.Model,
                    Brand = isSpareCover ? "" : valveSample.Brand,
                    Notes = isSpareCover ? "预留位" : "",
                    IslandId = islandRec.Id
                };
                p.Records.Add(v);
                added.Add(v.Id);
            }
            LineupTableData.RejectDuplicates(p.Records);
            return added;
        }

        // 6. 给指定阀片挂载气缸（既可用于给空阀片配第一支气缸，也可用于一阀控多缸自动生成 C01-1 / C01-2）
        public static LineupBundleResult AddCylinderToValve(LineupProject p, string valveId, int sensorPairs, LineupRecord cadBind = null) {
            var valve = p.Records.FirstOrDefault(r => r.Id == valveId && LineupTableData.Category(r.Category) == "阀片");
            if (valve == null) throw new InvalidOperationException("找不到目标阀片。");
            if (cadBind != null && !string.IsNullOrEmpty(cadBind.ReferenceKey)) {
                var conflict = p.Records.FirstOrDefault(r => string.Equals(r.ReferenceKey, cadBind.ReferenceKey, StringComparison.Ordinal));
                if (conflict != null) throw new InvalidOperationException("所选三维模型已绑定到 " + conflict.Number + "。");
            }

            // 若原本是预留盖板，挂载气缸时清掉盖板占位字样
            if (IsSpareRecord(valve)) {
                if (valve.Description == "盖板") valve.Description = "三位五通阀，中封式";
                if (valve.Parameters == "预留") valve.Parameters = "";
                if (valve.Notes == "预留位") valve.Notes = "";
            }

            // 把已经按编号归属此阀片的气缸及其隐式 H/W 磁开先补绑进来，确保多缸重编不漏磁开
            LinkImplicitChildren(p, valve);

            var sibling = p.Records.LastOrDefault(r => LineupTableData.Category(r.Category) == "气缸" && r.ValveId == valve.Id);
            var cyl = new LineupRecord {
                Category = "气缸",
                Number = "",
                Description = sibling != null ? sibling.Description : "",
                Model = sibling != null ? sibling.Model : "",
                Brand = sibling != null ? sibling.Brand : "",
                Home = sibling != null && !string.IsNullOrWhiteSpace(sibling.Home) ? sibling.Home : "缩回",
                Mounting = sibling != null && !string.IsNullOrWhiteSpace(sibling.Mounting) ? sibling.Mounting : "水平安装",
                Mechanism = sibling != null ? sibling.Mechanism : valve.Mechanism,
                Notes = sibling != null ? sibling.Notes : valve.Notes,
                ValveId = valve.Id,
                IslandId = valve.IslandId
            };
            if (cadBind != null) {
                if (!string.IsNullOrWhiteSpace(cadBind.Model)) cyl.Model = cadBind.Model;
                if (!string.IsNullOrWhiteSpace(cadBind.Brand)) cyl.Brand = cadBind.Brand;
                if (!string.IsNullOrWhiteSpace(cadBind.Description)) cyl.Description = cadBind.Description;
                Apply3DBind(p, cyl, cadBind);
            }
            p.Records.Add(cyl);

            // 联动重编号：1 支气缸 → Y1C01；多支气缸 → Y1C01-1 / Y1C01-2，并同步原气缸的 H/W 传感器与 CAD 位置图标注号
            LineupHierarchy.RenumberChildren(p, valve);

            var res = new LineupBundleResult { ValveId = valve.Id, CylinderId = cyl.Id };
            if (sensorPairs > 0) {
                var beforeIds = new HashSet<string>(p.Records.Select(r => r.Id), StringComparer.Ordinal);
                LineupNumbering.SensorPairsMulti(p, new[] { cyl.Id }, sensorPairs);
                var sensorSample = FindDefaultSensorSpec(p);
                foreach (var s in p.Records.Where(r => !beforeIds.Contains(r.Id))) {
                    if (!string.IsNullOrWhiteSpace(sensorSample.Model)) s.Model = sensorSample.Model;
                    if (!string.IsNullOrWhiteSpace(sensorSample.Brand)) s.Brand = sensorSample.Brand;
                    if (!string.IsNullOrWhiteSpace(sensorSample.Connector)) s.Connector = sensorSample.Connector;
                    res.SensorIds.Add(s.Id);
                }
            }
            LineupTableData.RejectDuplicates(p.Records);
            LineupNumbering.SyncCadPageNumbers(p, null);
            return res;
        }

        // 7. 将空闲阀片设为预留盖板（自检自动跳过缺气缸提醒）
        public static void SetValveAsSpare(LineupProject p, string valveId) {
            var valve = p.Records.FirstOrDefault(r => r.Id == valveId && LineupTableData.Category(r.Category) == "阀片");
            if (valve == null) throw new InvalidOperationException("找不到目标阀片。");
            bool hasCyl = p.Records.Any(r => LineupTableData.Category(r.Category) == "气缸"
                && (r.ValveId == valve.Id || string.Equals(LineupNumbering.ValveNumberForCylinder(r.Number), (valve.Number ?? "").Trim(), StringComparison.OrdinalIgnoreCase)));
            if (hasCyl) throw new InvalidOperationException("阀片 " + valve.Number + " 下仍关联着气缸，请先删除或移走该气缸后再设为预留盖板。");
            valve.Description = "盖板";
            valve.Parameters = "预留";
            valve.Notes = "预留位";
        }

        // 8. 切换单支气缸的磁开配置：0=无磁开（吹气/真空）、1=标准 H/W、2=双组 H1/W1/H2/W2
        public static int SetCylinderSensorMode(LineupProject p, string cylinderId, int pairCount) {
            var cyl = p.Records.FirstOrDefault(r => r.Id == cylinderId && LineupTableData.Category(r.Category) == "气缸");
            if (cyl == null) throw new InvalidOperationException("找不到目标气缸。");
            var existing = FindSensorsOfCylinder(p, cyl);
            var sample = existing.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Model)) ?? FindDefaultSensorSpec(p);

            if (pairCount == 0) {
                var ids = new HashSet<string>(existing.Select(s => s.Id), StringComparer.Ordinal);
                p.Records.RemoveAll(r => ids.Contains(r.Id));
                LineupNumbering.SyncCadPageNumbers(p, ids);
                return existing.Count;
            }
            // 先规范化导入/手工磁开的 CylinderId 与 Position（兼容 Position 为空的旧数据），防止切换模式时漏删或被 SensorPairsMulti 误报重号
            foreach (var s in existing) {
                if (string.IsNullOrEmpty(s.CylinderId)) s.CylinderId = cyl.Id;
                if (string.IsNullOrEmpty(s.ValveId) && !string.IsNullOrEmpty(cyl.ValveId)) s.ValveId = cyl.ValveId;
                if (string.IsNullOrEmpty(s.IslandId) && !string.IsNullOrEmpty(cyl.IslandId)) s.IslandId = cyl.IslandId;
                string pos = ExtractSensorPosSuffix(s);
                if (pos.Length > 0) s.Position = pos;
            }

            // 移除与目标套数冲突的旧模式（如 1 组 H/W 升级到 2 组 H1/W1/H2/W2，或反向降级）
            bool isSingleMode = pairCount == 1;
            var removeIds = new HashSet<string>(
                existing.Where(s => {
                    string pos = ExtractSensorPosSuffix(s);
                    return isSingleMode
                        ? Regex.IsMatch(pos, @"^[HW][0-9]+$", RegexOptions.IgnoreCase)
                        : Regex.IsMatch(pos, @"^[HW]$", RegexOptions.IgnoreCase);
                }).Select(s => s.Id), StringComparer.Ordinal);
            if (removeIds.Count > 0) p.Records.RemoveAll(r => removeIds.Contains(r.Id));

            var before = new HashSet<string>(p.Records.Select(r => r.Id), StringComparer.Ordinal);
            int added = LineupNumbering.SensorPairsMulti(p, new[] { cyl.Id }, pairCount);
            foreach (var s in p.Records.Where(r => !before.Contains(r.Id))) {
                if (!string.IsNullOrWhiteSpace(sample.Model)) s.Model = sample.Model;
                if (!string.IsNullOrWhiteSpace(sample.Brand)) s.Brand = sample.Brand;
                if (!string.IsNullOrWhiteSpace(sample.Connector)) s.Connector = sample.Connector;
            }
            LineupTableData.RejectDuplicates(p.Records);
            if (removeIds.Count > 0) LineupNumbering.SyncCadPageNumbers(p, removeIds);
            return added;
        }

        // 9. 删除一条气缸（含其 H/W 磁开）；若是 1阀多缸中的一支则只删该缸并重编同阀余缸；若是单缸且 deleteValveWhenLast=true 则连同阀片一并删除。
        // 删掉中间阀路/阀片后，同阀岛内剩余编号自动从 01 起按顺序顺延重排，并同步更新 CAD 位置图编号。
        public static int DeleteChannelItem(LineupProject p, string valveId, string cylinderId, bool deleteValveWhenLast) {
            return DeleteChannelItems(p, new[] { new KeyValuePair<string, string>(valveId ?? "", cylinderId ?? "") }, deleteValveWhenLast);
        }

        // 批量删除多条阀路/气缸，并在最后一次性完成剩余编号顺延重排与位置图同步
        public static int DeleteChannelItems(LineupProject p, IEnumerable<KeyValuePair<string, string>> targets, bool deleteValveWhenLast) {
            if (p == null || targets == null) return 0;
            var removeIds = new HashSet<string>(StringComparer.Ordinal);
            var affectedIslands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var affectedValves = new List<LineupRecord>();
            var affectedValveIds = new HashSet<string>(StringComparer.Ordinal);

            Action<LineupRecord> noteIsland = r => {
                if (r == null) return;
                string ik = LineupNumbering.IslandKey(r.Number);
                if (ik.Length == 0 && !string.IsNullOrEmpty(r.IslandId)) {
                    var ir = p.Records.FirstOrDefault(x => x.Id == r.IslandId && x.Category == "阀岛");
                    if (ir != null) ik = (ir.Number ?? "").Trim();
                }
                if (ik.Length > 0) affectedIslands.Add(ik);
            };

            foreach (var pair in targets) {
                string valveId = pair.Key ?? "";
                string cylinderId = pair.Value ?? "";
                LineupRecord valve = valveId.Length == 0 ? null : p.Records.FirstOrDefault(r => r.Id == valveId);
                LineupRecord cyl = cylinderId.Length == 0 ? null : p.Records.FirstOrDefault(r => r.Id == cylinderId);

                if (valve != null) LinkImplicitChildren(p, valve);
                noteIsland(valve);
                noteIsland(cyl);

                if (cyl != null) {
                    removeIds.Add(cyl.Id);
                    foreach (var s in FindSensorsOfCylinder(p, cyl)) removeIds.Add(s.Id);
                    if (valve == null && !string.IsNullOrEmpty(cyl.ValveId))
                        valve = p.Records.FirstOrDefault(r => r.Id == cyl.ValveId);
                    if (valve != null && affectedValveIds.Add(valve.Id)) affectedValves.Add(valve);
                } else if (valve != null) {
                    removeIds.Add(valve.Id);
                    var boundCyls = p.Records.Where(r => LineupTableData.Category(r.Category) == "气缸" && r.ValveId == valve.Id).ToList();
                    foreach (var c in boundCyls) {
                        removeIds.Add(c.Id);
                        foreach (var s in FindSensorsOfCylinder(p, c)) removeIds.Add(s.Id);
                    }
                }
            }

            if (removeIds.Count == 0) return 0;
            p.Records.RemoveAll(r => removeIds.Contains(r.Id));

            foreach (var valve in affectedValves) {
                if (removeIds.Contains(valve.Id)) continue;
                var leftCyls = p.Records.Where(r => LineupTableData.Category(r.Category) == "气缸" && r.ValveId == valve.Id).ToList();
                if (leftCyls.Count == 0 && deleteValveWhenLast) {
                    p.Records.RemoveAll(r => r.Id == valve.Id);
                    removeIds.Add(valve.Id);
                } else if (leftCyls.Count > 0) {
                    try { LineupHierarchy.RenumberChildren(p, valve); } catch { }
                }
            }

            foreach (string ik in affectedIslands) LineupNumbering.CompactIslandSlots(p, ik);
            LineupNumbering.SyncCadPageNumbers(p, removeIds);
            return removeIds.Count;
        }

        // 9b. 仅删除指定的气缸传感器记录并同步清理 CAD 位置图对应点位（供磁开明细子页右键单删/多删使用）
        public static int DeleteSensorItems(LineupProject p, IEnumerable<string> sensorIds) {
            if (p == null || p.Records == null || sensorIds == null) return 0;
            var targets = new HashSet<string>(sensorIds.Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
            if (targets.Count == 0) return 0;
            var removeIds = new HashSet<string>(
                p.Records.Where(r => LineupTableData.Category(r.Category) == "气缸传感器" && targets.Contains(r.Id)).Select(r => r.Id),
                StringComparer.Ordinal);
            if (removeIds.Count == 0) return 0;
            p.Records.RemoveAll(r => removeIds.Contains(r.Id));
            LineupNumbering.SyncCadPageNumbers(p, removeIds);
            return removeIds.Count;
        }

        // 10. 将选中阀片（及其气缸与 H/W）移动到目标阀岛，自动顺延槽位编号并级联重编
        public static int MoveValvesToIsland(LineupProject p, IEnumerable<string> valveIds, string targetIslandKey) {
            string pre; int num;
            if (!ParseIslandKey(targetIslandKey, out pre, out num)) throw new InvalidOperationException("目标阀岛编号无效。");
            var cfg = LineupNumbering.IslandEntry(p, targetIslandKey);
            if (cfg == null) {
                // 允许移入由记录推导出的阀岛（侧栏和换岛下拉框均展示它们），自动补建显式阀岛配置
                bool inferredExists = ListIslands(p).Any(x => string.Equals(x.Key, targetIslandKey, StringComparison.OrdinalIgnoreCase));
                if (!inferredExists) throw new InvalidOperationException("目标阀岛 " + targetIslandKey + " 不存在。");
                LineupNumbering.SetIsland(p, pre, num, "", "阀岛");
            }

            var targets = p.Records.Where(r => valveIds.Contains(r.Id) && LineupTableData.Category(r.Category) == "阀片").ToList();
            if (targets.Count == 0) throw new InvalidOperationException("请先选择要换岛的阀片。");

            var owner = EnsureIslandRecord(p, targetIslandKey);
            int moved = 0;
            foreach (var v in targets) {
                // 先确保按编号匹配的气缸与传感器都已挂到 ValveId/CylinderId，避免换岛遗漏
                LinkImplicitChildren(p, v);
                if (!string.Equals(LineupNumbering.IslandKey(v.Number), targetIslandKey, StringComparison.OrdinalIgnoreCase)) {
                    int slot = NextSlotNumber(p, targetIslandKey);
                    v.Number = targetIslandKey + "V" + slot.ToString("D2");
                }
                v.IslandId = owner.Id;
                LineupHierarchy.RenumberChildren(p, v);
                moved++;
            }
            LineupTableData.RejectDuplicates(p.Records);
            LineupNumbering.SyncCadPageNumbers(p, null);
            return moved;
        }

        // 11. 将未归属的孤立气缸/阀片一键归属到指定阀岛（可顺带补齐阀片与 H/W）
        public static int AssignOrphansToIsland(LineupProject p, IEnumerable<string> recordIds, string targetIslandKey, bool generateValves, bool generateSensors) {
            string pre; int num;
            if (!ParseIslandKey(targetIslandKey, out pre, out num)) throw new InvalidOperationException("目标阀岛编号无效。");
            var cfg = LineupNumbering.IslandEntry(p, targetIslandKey);
            if (cfg == null) LineupNumbering.SetIsland(p, pre, num, "", "阀岛");
            var owner = EnsureIslandRecord(p, targetIslandKey);

            var ids = new HashSet<string>(recordIds, StringComparer.Ordinal);
            var picked = p.Records.Where(r => ids.Contains(r.Id)).ToList();
            var pickedValves = picked.Where(r => LineupTableData.Category(r.Category) == "阀片").ToList();
            var pickedCyls = picked.Where(r => LineupTableData.Category(r.Category) == "气缸").ToList();

            // 改号前先按旧编号快照每个孤立气缸的隐式磁开，并补绑孤立阀片的隐式下级，防止先覆盖 r.Number 后找不到旧磁开
            var sensorsByCyl = new Dictionary<string, List<LineupRecord>>(StringComparer.Ordinal);
            foreach (var c in pickedCyls) {
                sensorsByCyl[c.Id] = FindSensorsOfCylinder(p, c);
            }
            foreach (var v in pickedValves) {
                LinkImplicitChildren(p, v);
            }

            var handledCylIds = new HashSet<string>(StringComparer.Ordinal);
            var cylIdsNeedValve = new List<string>();
            var cylIdsNeedSensor = new List<string>();
            int count = 0;

            // 先归属孤立阀片并级联重编其已关联气缸，避免同批气缸又被二次改号导致阀缸槽位错位
            foreach (var v in pickedValves) {
                int slot = NextSlotNumber(p, targetIslandKey);
                v.Number = targetIslandKey + "V" + slot.ToString("D2");
                v.IslandId = owner.Id;
                LineupHierarchy.RenumberChildren(p, v);
                foreach (var c in p.Records.Where(r => LineupTableData.Category(r.Category) == "气缸" && r.ValveId == v.Id)) {
                    handledCylIds.Add(c.Id);
                    if (ids.Contains(c.Id)) cylIdsNeedSensor.Add(c.Id);
                }
                count++;
            }

            foreach (var r in pickedCyls) {
                if (handledCylIds.Contains(r.Id)) { count++; continue; }
                int slot = NextSlotNumber(p, targetIslandKey);
                r.Number = targetIslandKey + "C" + slot.ToString("D2");
                r.IslandId = owner.Id;
                cylIdsNeedValve.Add(r.Id);
                cylIdsNeedSensor.Add(r.Id);
                List<LineupRecord> oldSens;
                if (sensorsByCyl.TryGetValue(r.Id, out oldSens)) {
                    foreach (var s in oldSens) {
                        s.CylinderId = r.Id;
                        s.IslandId = owner.Id;
                        string pos = ExtractSensorPosSuffix(s);
                        if (pos.Length > 0) {
                            s.Position = pos;
                            s.Number = r.Number + pos;
                        }
                    }
                }
                count++;
            }

            if (cylIdsNeedValve.Count > 0 && generateValves) LineupNumbering.GenerateValves(p, cylIdsNeedValve);
            if (cylIdsNeedSensor.Count > 0 && generateSensors) {
                var before = new HashSet<string>(p.Records.Select(r => r.Id), StringComparer.Ordinal);
                LineupNumbering.SensorPairs(p, cylIdsNeedSensor);
                var sample = FindDefaultSensorSpec(p);
                foreach (var s in p.Records.Where(r => !before.Contains(r.Id))) {
                    if (!string.IsNullOrWhiteSpace(sample.Model)) s.Model = sample.Model;
                    if (!string.IsNullOrWhiteSpace(sample.Brand)) s.Brand = sample.Brand;
                    if (!string.IsNullOrWhiteSpace(sample.Connector)) s.Connector = sample.Connector;
                }
            }
            LineupTableData.RejectDuplicates(p.Records);
            LineupNumbering.SyncCadPageNumbers(p, null);
            return count;
        }

        // 12. 批量给指定阀岛下的气缸传感器套用统一磁开规格（型号/品牌/接口），省去几十次重复填格
        public static int ApplySensorSpecToIsland(LineupProject p, string islandKey, string model, string brand, string connector, bool onlyEmpty) {
            var channels = BuildChannelRows(p, islandKey);
            int updated = 0;
            foreach (var ch in channels) {
                foreach (var s in ch.Sensors) {
                    if (onlyEmpty && !string.IsNullOrWhiteSpace(s.Model)) continue;
                    s.Model = (model ?? "").Trim();
                    s.Brand = (brand ?? "").Trim();
                    s.Connector = (connector ?? "").Trim();
                    updated++;
                }
            }
            return updated;
        }

        sealed class SlotMoveUnit {
            public LineupRecord Valve;
            public readonly List<LineupRecord> Cylinders = new List<LineupRecord>();
            public readonly Dictionary<string, List<LineupRecord>> SensorsByCyl = new Dictionary<string, List<LineupRecord>>(StringComparer.Ordinal);
            public int OrigSlot = 999999;

            public bool ContainsAny(HashSet<string> ids) {
                if (Valve != null && ids.Contains(Valve.Id)) return true;
                foreach (var c in Cylinders) {
                    if (ids.Contains(c.Id)) return true;
                    List<LineupRecord> sl;
                    if (SensorsByCyl.TryGetValue(c.Id, out sl)) {
                        foreach (var s in sl) if (ids.Contains(s.Id)) return true;
                    }
                }
                return false;
            }
        }

        static string ExtractCylinderSuffix(string cylNumber, string islandKey) {
            var m = Regex.Match((cylNumber ?? "").Trim(), "^" + Regex.Escape(islandKey.Trim()) + @"C[0-9]+(-[0-9]+|[a-z])?$", RegexOptions.IgnoreCase);
            return m.Success && m.Groups[1].Success ? m.Groups[1].Value : "";
        }

        static string ExtractSensorPosSuffix(LineupRecord s) {
            if (s == null) return "";
            if (!string.IsNullOrWhiteSpace(s.Position)) return s.Position.Trim().ToUpperInvariant();
            var m = Regex.Match((s.Number ?? "").Trim(), @"([HWM][0-9]*)$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        static void ReorderRecordsInPlace(LineupProject p, IList<string> orderedIds) {
            if (p == null || p.Records == null || orderedIds == null || orderedIds.Count <= 1) return;
            var idSet = new HashSet<string>(orderedIds, StringComparer.Ordinal);
            var map = new Dictionary<string, LineupRecord>(StringComparer.Ordinal);
            foreach (var r in p.Records) if (idSet.Contains(r.Id) && !map.ContainsKey(r.Id)) map[r.Id] = r;
            var queue = new List<LineupRecord>(orderedIds.Count);
            foreach (var id in orderedIds) {
                LineupRecord rec;
                if (map.TryGetValue(id, out rec)) queue.Add(rec);
            }
            if (queue.Count <= 1) return;
            int idx = 0;
            for (int i = 0; i < p.Records.Count && idx < queue.Count; i++) {
                if (idSet.Contains(p.Records[i].Id)) p.Records[i] = queue[idx++];
            }
        }

        static bool MoveIndices(int count, HashSet<int> selectedIndices, int direction, out List<int> newOrder) {
            newOrder = Enumerable.Range(0, count).ToList();
            bool moved = false;
            if (direction < 0) {
                for (int i = 1; i < newOrder.Count; i++) {
                    if (selectedIndices.Contains(newOrder[i]) && !selectedIndices.Contains(newOrder[i - 1])) {
                        int t = newOrder[i - 1]; newOrder[i - 1] = newOrder[i]; newOrder[i] = t;
                        moved = true;
                    }
                }
            } else {
                for (int i = newOrder.Count - 2; i >= 0; i--) {
                    if (selectedIndices.Contains(newOrder[i]) && !selectedIndices.Contains(newOrder[i + 1])) {
                        int t = newOrder[i + 1]; newOrder[i + 1] = newOrder[i]; newOrder[i] = t;
                        moved = true;
                    }
                }
            }
            return moved;
        }

        static bool MoveSlotUnitGroup(LineupProject p, string canonicalKey, List<SlotMoveUnit> units, HashSet<string> selSet, int direction, int fallbackBaseSlot, HashSet<int> occupiedOtherSlots) {
            if (units.Count <= 1) return false;
            var selIdx = new HashSet<int>();
            for (int i = 0; i < units.Count; i++) if (units[i].ContainsAny(selSet)) selIdx.Add(i);
            if (selIdx.Count == 0) return false;

            List<int> newOrder;
            if (!MoveIndices(units.Count, selIdx, direction, out newOrder)) return false;

            // 先冻结原有槽位号、气缸后缀（-1/-2/a）与磁开后缀（H/W），避免改号途中互相覆盖
            var cylSuffix = new Dictionary<string, string>(StringComparer.Ordinal);
            var senSuffix = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var u in units) {
                for (int ci = 0; ci < u.Cylinders.Count; ci++) {
                    var c = u.Cylinders[ci];
                    string suf = ExtractCylinderSuffix(c.Number, canonicalKey);
                    if (suf.Length == 0 && u.Cylinders.Count > 1) suf = "-" + (ci + 1);
                    cylSuffix[c.Id] = suf;
                    List<LineupRecord> sl;
                    if (u.SensorsByCyl.TryGetValue(c.Id, out sl)) {
                        foreach (var s in sl) senSuffix[s.Id] = ExtractSensorPosSuffix(s);
                    }
                }
            }

            bool origSlotsClean = units.All(u => u.OrigSlot > 0 && u.OrigSlot < 999999)
                && units.Select(u => u.OrigSlot).Distinct().Count() == units.Count;
            int[] targetSlots;
            if (origSlotsClean) {
                targetSlots = units.Select(u => u.OrigSlot).OrderBy(x => x).ToArray();
            } else {
                // 回退重排槽号时主动避开另一组（无阀气缸或阀片）已占用的槽号，避免中间夹着无阀气缸时撞号抛 RejectDuplicates
                targetSlots = new int[units.Count];
                int curSlot = Math.Max(1, fallbackBaseSlot);
                for (int i = 0; i < units.Count; i++) {
                    while (occupiedOtherSlots != null && occupiedOtherSlots.Contains(curSlot)) curSlot++;
                    targetSlots[i] = curSlot++;
                }
            }

            var orderedValves = new List<string>();
            var orderedCyls = new List<string>();
            var orderedSensors = new List<string>();

            for (int pos = 0; pos < newOrder.Count; pos++) {
                var u = units[newOrder[pos]];
                string slotStr = targetSlots[pos].ToString("D2");
                if (u.Valve != null) {
                    u.Valve.Number = canonicalKey + "V" + slotStr;
                    orderedValves.Add(u.Valve.Id);
                }
                for (int ci = 0; ci < u.Cylinders.Count; ci++) {
                    var c = u.Cylinders[ci];
                    string suf;
                    if (!cylSuffix.TryGetValue(c.Id, out suf)) suf = "";
                    c.Number = canonicalKey + "C" + slotStr + suf;
                    orderedCyls.Add(c.Id);
                    List<LineupRecord> sl;
                    if (u.SensorsByCyl.TryGetValue(c.Id, out sl)) {
                        foreach (var s in sl) {
                            string ps;
                            if (senSuffix.TryGetValue(s.Id, out ps) && ps.Length > 0) s.Number = c.Number + ps;
                            orderedSensors.Add(s.Id);
                        }
                    }
                }
            }

            ReorderRecordsInPlace(p, orderedValves);
            ReorderRecordsInPlace(p, orderedCyls);
            ReorderRecordsInPlace(p, orderedSensors);
            return true;
        }

        // 12b. 在气动系统（阀路总览 / 气缸明细 / 阀片明细 / 磁开明细）中上移或下移选中行，
        //      同步交换阀位序号 V**/C**/H/W、调整底层 Records 顺序并刷新 CAD 位置图标注号（支持多选与一阀控多缸）
        public static bool MoveIslandRows(
            LineupProject p,
            string islandKey,
            IList<string> selectedRecordIds,
            int subTab,
            int direction,
            out string focusRecordId,
            out string message) {

            focusRecordId = selectedRecordIds != null && selectedRecordIds.Count > 0 ? selectedRecordIds[0] : "";
            message = "";
            int dir = direction < 0 ? -1 : 1;
            if (p == null || p.Records == null || string.IsNullOrWhiteSpace(islandKey)) {
                message = "请先选择具体阀岛后再上移 / 下移。";
                return false;
            }
            var selList = (selectedRecordIds ?? new List<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
            if (selList.Count == 0) {
                message = "请先选中要移动的行。";
                return false;
            }
            var selSet = new HashSet<string>(selList, StringComparer.Ordinal);
            var channels = BuildChannelRows(p, islandKey);
            if (channels.Count == 0) {
                message = "当前阀岛暂无可移动的阀路。";
                return false;
            }

            // 1. 磁开明细（subTab == 3）：若选中的是同一气缸下的部分传感器且未触顶/底，先在气缸内部调整传感器顺序
            if (subTab == 3) {
                var ownerChs = channels.Where(ch => ch.Sensors.Any(s => selSet.Contains(s.Id))).ToList();
                if (ownerChs.Count == 1 && ownerChs[0].Sensors.Count > 1) {
                    var sensors = ownerChs[0].Sensors;
                    var selIdx = new HashSet<int>();
                    for (int i = 0; i < sensors.Count; i++) if (selSet.Contains(sensors[i].Id)) selIdx.Add(i);
                    if (selIdx.Count > 0 && selIdx.Count < sensors.Count) {
                        List<int> newOrder;
                        if (MoveIndices(sensors.Count, selIdx, dir, out newOrder)) {
                            var reordered = newOrder.Select(i => sensors[i].Id).ToList();
                            ReorderRecordsInPlace(p, reordered);
                            focusRecordId = selList[0];
                            message = dir < 0 ? "已上移选中磁开行" : "已下移选中磁开行";
                            return true;
                        }
                    }
                }
            }

            // 2. 阀路总览 / 气缸明细（subTab == 0 或 1）：若选中的全是同一“一阀控多缸”下的部分气缸，先在同阀内部交换 -1/-2 顺序
            if (subTab == 0 || subTab == 1) {
                var byValve = channels
                    .Where(ch => ch.Valve != null && ch.Cylinder != null)
                    .GroupBy(ch => ch.Valve.Id, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                var matchedValves = new HashSet<string>(StringComparer.Ordinal);
                bool allAreCylinders = true;
                foreach (string id in selList) {
                    var ch = channels.FirstOrDefault(x => x.Cylinder != null && x.Cylinder.Id == id);
                    if (ch == null || ch.Valve == null) { allAreCylinders = false; break; }
                    matchedValves.Add(ch.Valve.Id);
                }
                if (allAreCylinders && matchedValves.Count == 1) {
                    var group = byValve[matchedValves.First()];
                    if (group.Count > 1) {
                        var selIdx = new HashSet<int>();
                        for (int i = 0; i < group.Count; i++) if (selSet.Contains(group[i].Cylinder.Id)) selIdx.Add(i);
                        if (selIdx.Count > 0 && selIdx.Count < group.Count) {
                            List<int> newOrder;
                            if (MoveIndices(group.Count, selIdx, dir, out newOrder)) {
                                var valve = group[0].Valve;
                                LinkImplicitChildren(p, valve);
                                var orderedCyls = newOrder.Select(i => group[i].Cylinder.Id).ToList();
                                var orderedSens = new List<string>();
                                foreach (int idx in newOrder) foreach (var s in group[idx].Sensors) orderedSens.Add(s.Id);
                                ReorderRecordsInPlace(p, orderedCyls);
                                ReorderRecordsInPlace(p, orderedSens);
                                try { LineupHierarchy.RenumberChildren(p, valve); } catch { }
                                LineupTableData.RejectDuplicates(p.Records);
                                LineupNumbering.SyncCadPageNumbers(p, null);
                                focusRecordId = selList[0];
                                message = dir < 0 ? "已上移同阀气缸并更新 -1/-2 编号" : "已下移同阀气缸并更新 -1/-2 编号";
                                return true;
                            }
                        }
                    }
                }
            }

            // 3. 槽位级移动：把阀片单元（含所控全部气缸及 H/W）与无阀气缸单元分别按槽位整体交换顺序并级联重编
            string canonicalKey = islandKey.Trim();
            foreach (var ch in channels) {
                string probe = ch.Valve != null ? LineupNumbering.IslandKey(ch.Valve.Number)
                    : (ch.Cylinder != null ? LineupNumbering.IslandKey(ch.Cylinder.Number) : "");
                if (probe.Length > 0) { canonicalKey = probe; break; }
            }

            var valveUnits = new List<SlotMoveUnit>();
            var valveUnitById = new Dictionary<string, SlotMoveUnit>(StringComparer.Ordinal);
            var unattachedUnits = new List<SlotMoveUnit>();
            int maxValveSlot = 0;

            foreach (var ch in channels) {
                if (ch.Valve != null) {
                    SlotMoveUnit u;
                    if (!valveUnitById.TryGetValue(ch.Valve.Id, out u)) {
                        int s = ExtractSlotSortKey(ch.Valve.Number);
                        if (s < 999999 && s > maxValveSlot) maxValveSlot = s;
                        u = new SlotMoveUnit { Valve = ch.Valve, OrigSlot = s };
                        valveUnitById[ch.Valve.Id] = u;
                        valveUnits.Add(u);
                    }
                    if (ch.Cylinder != null) {
                        u.Cylinders.Add(ch.Cylinder);
                        u.SensorsByCyl[ch.Cylinder.Id] = ch.Sensors ?? new List<LineupRecord>();
                    }
                } else if (ch.Cylinder != null) {
                    var u = new SlotMoveUnit { OrigSlot = ExtractSlotSortKey(ch.Cylinder.Number) };
                    u.Cylinders.Add(ch.Cylinder);
                    u.SensorsByCyl[ch.Cylinder.Id] = ch.Sensors ?? new List<LineupRecord>();
                    unattachedUnits.Add(u);
                }
            }

            var unattachedSlots = new HashSet<int>(unattachedUnits.Where(u => u.OrigSlot > 0 && u.OrigSlot < 999999).Select(u => u.OrigSlot));
            bool movedValves = MoveSlotUnitGroup(p, canonicalKey, valveUnits, selSet, dir, 1, unattachedSlots);
            var valveSlotsNow = new HashSet<int>();
            foreach (var u in valveUnits) {
                int vs = u.Valve != null ? ExtractSlotSortKey(u.Valve.Number) : u.OrigSlot;
                if (vs > 0 && vs < 999999) { valveSlotsNow.Add(vs); if (vs > maxValveSlot) maxValveSlot = vs; }
            }
            bool movedUnattached = MoveSlotUnitGroup(p, canonicalKey, unattachedUnits, selSet, dir, Math.Max(1, maxValveSlot + 1), valveSlotsNow);
            if (!movedValves && !movedUnattached) {
                message = dir < 0 ? "选中行已到最顶部" : "选中行已到最底部";
                return false;
            }

            LineupTableData.RejectDuplicates(p.Records);
            LineupNumbering.SyncCadPageNumbers(p, null);
            focusRecordId = selList[0];
            message = dir < 0 ? "已上移选中阀路并同步更新编号" : "已下移选中阀路并同步更新编号";
            return true;
        }

        // 13. 将配置好的阀岛本体一键同步到「其他电气件 (E**)」（幂等不重复）
        public static int SyncIslandsToElectrical(LineupProject p, string singleIslandKey = null) {
            var summaries = ListIslands(p);
            if (!string.IsNullOrWhiteSpace(singleIslandKey))
                summaries = summaries.Where(s => string.Equals(s.Key, singleIslandKey, StringComparison.OrdinalIgnoreCase)).ToList();

            int added = 0;
            foreach (var s in summaries) {
                if (s.Kind == "单片阀") continue;
                bool exists = p.Records.Any(r => LineupTableData.Category(r.Category) == "其他电气件"
                    && (((r.Description ?? "").IndexOf(s.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        || ((r.Notes ?? "").IndexOf(s.Key, StringComparison.OrdinalIgnoreCase) >= 0)));
                if (exists) continue;

                var eRec = new LineupRecord {
                    Category = "其他电气件",
                    Number = LineupNumbering.Next(p, "其他电气件"),
                    Description = s.Key + " " + s.Kind + "本体（" + s.ValveCount + "位）",
                    Model = s.Model,
                    Notes = s.Key
                };
                p.Records.Add(eRec);
                added++;
            }
            LineupTableData.RejectDuplicates(p.Records);
            return added;
        }

        // 同步修改气缸描述时，自动保持配套阀片功能参数与默认 H/W 描述一致
        public static void SyncCylinderDescription(LineupProject p, LineupRecord cyl, string oldDesc, string newDesc) {
            if (p == null || cyl == null) return;
            cyl.Description = newDesc ?? "";
            oldDesc = oldDesc ?? "";
            if (!string.IsNullOrEmpty(cyl.ValveId)) {
                var valve = p.Records.FirstOrDefault(r => r.Id == cyl.ValveId);
                if (valve != null) {
                    var peers = p.Records.Where(r => LineupTableData.Category(r.Category) == "气缸" && r.ValveId == valve.Id).ToList();
                    if (peers.Count == 1 && (string.IsNullOrWhiteSpace(valve.Parameters) || valve.Parameters == oldDesc))
                        valve.Parameters = cyl.Description;
                    else if (peers.Count > 1)
                        valve.Parameters = string.Join(" / ", peers.Select(c => (c.Description ?? "").Trim()).Where(x => x.Length > 0).Distinct());
                }
            }
            foreach (var s in FindSensorsOfCylinder(p, cyl)) {
                string posLabel = PositionLabel(s.Position);
                if (posLabel.Length > 0 && (string.IsNullOrWhiteSpace(s.Description) || s.Description == oldDesc + posLabel))
                    s.Description = cyl.Description + posLabel;
            }
        }

        static string PositionLabel(string pos) {
            pos = (pos ?? "").Trim().ToUpperInvariant();
            if (pos == "H") return "原位";
            if (pos == "W") return "工作位";
            if (pos == "M") return "中间位";
            if (pos.StartsWith("H")) return "原位" + pos.Substring(1);
            if (pos.StartsWith("W")) return "工作位" + pos.Substring(1);
            return "";
        }

        static List<LineupRecord> FindSensorsOfCylinder(LineupProject p, LineupRecord cyl) {
            string cn = (cyl.Number ?? "").Trim();
            return p.Records.Where(r => LineupTableData.Category(r.Category) == "气缸传感器"
                && (r.CylinderId == cyl.Id
                    || (cn.Length > 0 && Regex.IsMatch((r.Number ?? "").Trim(), "^" + Regex.Escape(cn) + "[HWM][0-9]*$", RegexOptions.IgnoreCase))))
                .ToList();
        }

        static void LinkImplicitChildren(LineupProject p, LineupRecord valve) {
            string vn = (valve.Number ?? "").Trim();
            if (vn.Length == 0) return;
            foreach (var c in p.Records.Where(r => LineupTableData.Category(r.Category) == "气缸")) {
                if (c.ValveId == valve.Id || (string.IsNullOrEmpty(c.ValveId) && string.Equals(LineupNumbering.ValveNumberForCylinder(c.Number), vn, StringComparison.OrdinalIgnoreCase))) {
                    c.ValveId = valve.Id;
                    if (!string.IsNullOrEmpty(valve.IslandId)) c.IslandId = valve.IslandId;
                    foreach (var s in FindSensorsOfCylinder(p, c)) {
                        s.CylinderId = c.Id;
                        s.ValveId = valve.Id;
                        if (!string.IsNullOrEmpty(valve.IslandId)) s.IslandId = valve.IslandId;
                        string pos = ExtractSensorPosSuffix(s);
                        if (pos.Length > 0) s.Position = pos;
                    }
                }
            }
        }

        static LineupRecord FindDefaultValveSpec(LineupProject p, string islandKey) {
            var sameIsland = p.Records.LastOrDefault(r => LineupTableData.Category(r.Category) == "阀片"
                && string.Equals(LineupNumbering.IslandKey(r.Number), islandKey, StringComparison.OrdinalIgnoreCase)
                && !IsSpareRecord(r) && !string.IsNullOrWhiteSpace(r.Model));
            if (sameIsland != null) return sameIsland;
            var anyValve = p.Records.LastOrDefault(r => LineupTableData.Category(r.Category) == "阀片" && !IsSpareRecord(r) && !string.IsNullOrWhiteSpace(r.Model));
            if (anyValve != null) return anyValve;
            var preset = p.EntryPresets == null ? null : p.EntryPresets.LastOrDefault(x => x.Category == "阀片");
            return new LineupRecord {
                Description = preset != null && !string.IsNullOrWhiteSpace(preset.Description) ? preset.Description : "三位五通阀，中封式",
                Model = preset != null ? (preset.Model ?? "") : "",
                Brand = preset != null ? (preset.Brand ?? "") : ""
            };
        }

        static LineupRecord FindDefaultSensorSpec(LineupProject p) {
            var existing = p.Records.LastOrDefault(r => LineupTableData.Category(r.Category) == "气缸传感器" && !string.IsNullOrWhiteSpace(r.Model));
            if (existing != null) return existing;
            var preset = p.EntryPresets == null ? null : p.EntryPresets.LastOrDefault(x => x.Category == "气缸传感器");
            return new LineupRecord {
                Model = preset != null ? (preset.Model ?? "") : "",
                Brand = preset != null ? (preset.Brand ?? "") : "",
                Connector = preset != null ? (preset.Connector ?? "") : ""
            };
        }

        static void Apply3DBind(LineupProject p, LineupRecord target, LineupRecord cadBind) {
            var conflict = p.Records.FirstOrDefault(r => r.Id != target.Id && string.Equals(r.ReferenceKey, cadBind.ReferenceKey, StringComparison.Ordinal));
            if (conflict != null) throw new InvalidOperationException("所选三维模型已绑定到 " + conflict.Number + "。");
            target.ReferenceKey = cadBind.ReferenceKey;
            target.SourceFile = cadBind.SourceFile ?? "";
            target.InstancePath = cadBind.InstancePath == null ? new string[0] : (string[])cadBind.InstancePath.Clone();
        }
    }
}
