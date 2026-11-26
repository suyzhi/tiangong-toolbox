using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    public sealed class LineupBomFieldChange {
        public string Field = "";
        public string Before = "";
        public string After = "";
    }

    public sealed class LineupBomMod {
        public LineupRecord Before;
        public LineupRecord After;
        public List<LineupBomFieldChange> Changes = new List<LineupBomFieldChange>();
    }

    public sealed class LineupBomDiff {
        public bool HasBaseline;
        public string BaselineVersion = "";
        public List<LineupRecord> Added = new List<LineupRecord>();
        public HashSet<string> AddedIds = new HashSet<string>(StringComparer.Ordinal);
        public List<LineupRecord> Removed = new List<LineupRecord>();
        public List<LineupBomMod> Modified = new List<LineupBomMod>();

        public bool HasAnyChange {
            get { return Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0; }
        }
    }

    // Lineup 版本升版、基准快照与 BOM 差异分析纯逻辑引擎（无 UI / CAD 依赖）
    public static class LineupRevision {
        // 智能递增版本号：V1.0 -> V1.1，V3.3 -> V3.4，V1 -> V1.1，Rev2 -> Rev3，空 -> V1.0
        public static string NextVersion(string current) {
            string s = (current ?? "").Trim();
            if (s.Length == 0) return "V1.0";

            // 匹配带小数位版本（如 V3.3 / v1.0 / 2.4 / V3.0.1）
            var mDot = Regex.Match(s, @"^([A-Za-z]*)(\d+(?:\.\d+)*)\.(\d+)$");
            if (mDot.Success) {
                int minor;
                if (int.TryParse(mDot.Groups[3].Value, out minor)) {
                    string prefix = mDot.Groups[1].Value;
                    if (prefix.Equals("v", StringComparison.Ordinal)) prefix = "V";
                    return prefix + mDot.Groups[2].Value + "." + (minor + 1);
                }
            }

            // 匹配纯整数版本（如 V1 -> V1.1；Rev2 -> Rev3）
            var mInt = Regex.Match(s, @"^([A-Za-z]+)(\d+)$");
            if (mInt.Success) {
                string prefix = mInt.Groups[1].Value;
                int major;
                if (int.TryParse(mInt.Groups[2].Value, out major)) {
                    if (prefix.Equals("V", StringComparison.OrdinalIgnoreCase)) return "V" + major + ".1";
                    return prefix + (major + 1);
                }
            }

            // 末尾含数字的任意格式
            var mTail = Regex.Match(s, @"^(.*?)(\d+)$");
            if (mTail.Success) {
                int n;
                if (int.TryParse(mTail.Groups[2].Value, out n)) return mTail.Groups[1].Value + (n + 1);
            }
            return s + ".1";
        }

        // 获取项目当前最新版本号（优先取更新日志最后一条的非空版本号，其次取 project.Revision）
        public static string CurrentVersion(LineupProject p) {
            if (p == null) return "V1.0";
            if (p.ChangeLogs != null) {
                for (int i = p.ChangeLogs.Count - 1; i >= 0; i--) {
                    string v = (p.ChangeLogs[i].Version ?? "").Trim();
                    if (v.Length > 0) return v;
                }
            }
            return string.IsNullOrWhiteSpace(p.Revision) ? "V1.0" : p.Revision.Trim();
        }

        // 抓取当前 BOM 五大类目作为基准快照
        public static LineupBaselineSnapshot CaptureSnapshot(LineupProject p, string version = null) {
            string ver = !string.IsNullOrWhiteSpace(version) ? version.Trim() : CurrentVersion(p);
            var snap = new LineupBaselineSnapshot {
                Version = ver,
                CapturedAt = DateTime.Today.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
            };
            if (p != null && p.Records != null) {
                foreach (var r in p.Records) {
                    if (LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))) {
                        snap.Records.Add(r.Copy());
                    }
                }
            }
            return snap;
        }

        // 比较当前 BOM 与 project.Baseline（上一版基准快照）的增/删/改差异
        public static LineupBomDiff CompareToBaseline(LineupProject p) {
            var diff = new LineupBomDiff();
            if (p == null || p.Baseline == null || p.Baseline.Records == null || p.Baseline.Records.Count == 0) {
                return diff;
            }
            diff.HasBaseline = true;
            diff.BaselineVersion = (p.Baseline.Version ?? "").Trim();

            var baseList = p.Baseline.Records
                .Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category)))
                .ToList();
            var curList = (p.Records ?? new List<LineupRecord>())
                .Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category)))
                .ToList();

            var matchedBase = new bool[baseList.Count];
            var matchedCur = new bool[curList.Count];

            // 1. 优先按记录 Id 精确配对：
            //    同项目内删中间行后 CompactAfterDelete 会顺延重排后续编号（如 Y1C03→Y1C02），
            //    但每条记录的 GUID Id 不变；若先按编号匹配会把旧 Y1C02 错配给重排后的 Y1C03，导致整段串行误报、末行误标取消。
            var baseById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < baseList.Count; i++) {
                string id = baseList[i].Id ?? "";
                if (id.Length > 0 && !baseById.ContainsKey(id)) baseById[id] = i;
            }
            for (int j = 0; j < curList.Count; j++) {
                string id = curList[j].Id ?? "";
                int bi;
                if (id.Length > 0 && baseById.TryGetValue(id, out bi) && !matchedBase[bi]) {
                    matchedBase[bi] = true;
                    matchedCur[j] = true;
                    CollectModIfChanged(baseList[bi], curList[j], diff);
                }
            }

            // 2. 其次按 (Category, Number) 精确配对（兼容外部重新导入 Excel 后 Id 重新生成的跨会话对比）
            var baseByCatNum = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < baseList.Count; i++) {
                if (matchedBase[i]) continue;
                string num = (baseList[i].Number ?? "").Trim();
                if (num.Length == 0) continue;
                string key = LineupTableData.Category(baseList[i].Category) + "\u001F" + num;
                Queue<int> q;
                if (!baseByCatNum.TryGetValue(key, out q)) { q = new Queue<int>(); baseByCatNum[key] = q; }
                q.Enqueue(i);
            }
            for (int j = 0; j < curList.Count; j++) {
                if (matchedCur[j]) continue;
                string num = (curList[j].Number ?? "").Trim();
                if (num.Length == 0) continue;
                string key = LineupTableData.Category(curList[j].Category) + "\u001F" + num;
                Queue<int> q;
                if (baseByCatNum.TryGetValue(key, out q) && q.Count > 0) {
                    int bi = q.Dequeue();
                    matchedBase[bi] = true;
                    matchedCur[j] = true;
                    CollectModIfChanged(baseList[bi], curList[j], diff);
                }
            }

            // 3. 对仍未匹配的无编号行（如先导阀、空位预留），按 (Category, Description, Model) 配对
            for (int j = 0; j < curList.Count; j++) {
                if (matchedCur[j] || !string.IsNullOrWhiteSpace(curList[j].Number)) continue;
                string cat = LineupTableData.Category(curList[j].Category);
                string desc = (curList[j].Description ?? "").Trim();
                string model = (curList[j].Model ?? "").Trim();
                for (int i = 0; i < baseList.Count; i++) {
                    if (matchedBase[i] || !string.IsNullOrWhiteSpace(baseList[i].Number)) continue;
                    if (LineupTableData.Category(baseList[i].Category) == cat
                        && string.Equals((baseList[i].Description ?? "").Trim(), desc, StringComparison.OrdinalIgnoreCase)
                        && string.Equals((baseList[i].Model ?? "").Trim(), model, StringComparison.OrdinalIgnoreCase)) {
                        matchedBase[i] = true;
                        matchedCur[j] = true;
                        CollectModIfChanged(baseList[i], curList[j], diff);
                        break;
                    }
                }
            }

            // 4. 收集新增行与取消行
            for (int j = 0; j < curList.Count; j++) {
                if (!matchedCur[j]) {
                    diff.Added.Add(curList[j]);
                    if (!string.IsNullOrEmpty(curList[j].Id)) diff.AddedIds.Add(curList[j].Id);
                }
            }
            for (int i = 0; i < baseList.Count; i++) {
                if (!matchedBase[i]) diff.Removed.Add(baseList[i]);
            }
            return diff;
        }

        static void CollectModIfChanged(LineupRecord before, LineupRecord after, LineupBomDiff diff) {
            var changes = new List<LineupBomFieldChange>();
            CheckField(changes, "类别", LineupTableData.Category(before.Category), LineupTableData.Category(after.Category));
            CheckField(changes, "编号", before.Number, after.Number);
            CheckField(changes, "地址", before.Address, after.Address);
            CheckField(changes, "功能参数", before.Parameters, after.Parameters);
            CheckField(changes, "描述", before.Description, after.Description);
            CheckField(changes, "型号", before.Model, after.Model);
            CheckField(changes, "品牌", before.Brand, after.Brand);
            CheckField(changes, "原位", before.Home, after.Home);
            CheckField(changes, "安装方式", before.Mounting, after.Mounting);
            CheckField(changes, "传感器接口类型", before.Connector, after.Connector);
            CheckField(changes, "机构名称", before.Mechanism, after.Mechanism);
            CheckField(changes, "备注", before.Notes, after.Notes);
            if (changes.Count > 0) {
                diff.Modified.Add(new LineupBomMod { Before = before, After = after, Changes = changes });
            }
        }

        static void CheckField(List<LineupBomFieldChange> list, string name, string before, string after) {
            string a = (before ?? "").Trim();
            string b = (after ?? "").Trim();
            if (!string.Equals(a, b, StringComparison.Ordinal)) {
                list.Add(new LineupBomFieldChange { Field = name, Before = a, After = b });
            }
        }

        // 根据差异自动草拟可直接填入「LineUP更新日志」的多行更新内容
        public static string DraftChangeSummary(LineupBomDiff diff) {
            if (diff == null || !diff.HasBaseline) return "";
            if (!diff.HasAnyChange) return "无 BOM 器件增删改变更";

            var lines = new List<string>();
            int seq = 1;
            if (diff.Added.Count > 0) {
                string sample = string.Join("、", diff.Added.Take(8).Select(FormatRecShort).ToArray());
                string tail = diff.Added.Count > 8 ? " 等共 " + diff.Added.Count + " 项" : "";
                lines.Add(seq++ + "、新增 " + diff.Added.Count + " 项器件：" + sample + tail + "；");
            }
            if (diff.Modified.Count > 0) {
                if (diff.Modified.Count <= 6) {
                    foreach (var m in diff.Modified) {
                        string desc = string.Join("，", m.Changes.Select(c =>
                            c.Field + "（" + (c.Before.Length == 0 ? "空" : c.Before) + "→" + (c.After.Length == 0 ? "空" : c.After) + "）").ToArray());
                        lines.Add(seq++ + "、变更 " + FormatRecShort(m.After) + "：" + desc + "；");
                    }
                } else {
                    string sample = string.Join("、", diff.Modified.Take(6).Select(m => FormatRecShort(m.After)).ToArray());
                    lines.Add(seq++ + "、变更 " + diff.Modified.Count + " 项器件参数：" + sample + " 等；");
                }
            }
            if (diff.Removed.Count > 0) {
                string sample = string.Join("、", diff.Removed.Take(8).Select(FormatRecShort).ToArray());
                string tail = diff.Removed.Count > 8 ? " 等共 " + diff.Removed.Count + " 项" : "";
                lines.Add(seq++ + "、取消 " + diff.Removed.Count + " 项器件：" + sample + tail + "（BOM 已按规范红底加删除线保留）。");
            }
            return string.Join("\n", lines.ToArray());
        }

        static string FormatRecShort(LineupRecord r) {
            string num = (r.Number ?? "").Trim();
            string desc = (r.Description ?? "").Trim();
            if (num.Length > 0 && desc.Length > 0) return num + "（" + desc + "）";
            if (num.Length > 0) return num;
            if (desc.Length > 0) return desc;
            return (r.Model ?? "").Trim();
        }

        // 将导入的 Excel 单元格值转换为简洁日期字符串（支持 Excel 序列号、COM DateTime 残留及常见文本日期）
        public static string NormalizeDateString(string raw) {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return "";
            double serial;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out serial) && serial >= 20000 && serial <= 90000) {
                try {
                    return new DateTime(1899, 12, 30).AddDays(Math.Floor(serial)).ToString("yyyy/M/d", CultureInfo.InvariantCulture);
                } catch { }
            }
            // 兼容部分 WPS COM / 历史导入残留的完整日期时间字符串（如 "2026/5/25 0:00:00" 或美式 "05/25/2026 00:00:00"）
            DateTime dt;
            string[] dtFmts = {
                "yyyy/M/d H:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-M-d H:mm:ss", "yyyy-MM-dd HH:mm:ss",
                "yyyy/M/d HH:mm:ss", "yyyy-M-d HH:mm:ss",
                "MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm:ss", "M/d/yyyy HH:mm:ss"
            };
            if (DateTime.TryParseExact(s, dtFmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
                return dt.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
            }
            return s;
        }

        public static bool TryParseDateSerial(string text, out int serial) {
            serial = 0;
            string s = (text ?? "").Trim();
            if (s.Length == 0) return false;
            DateTime dt;
            string[] fmts = { "yyyy/M/d", "yyyy/MM/dd", "yyyy-M-d", "yyyy-MM-dd", "yyyy.M.d", "yyyy.MM.dd" };
            if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
                || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt)) {
                serial = (int)(dt.Date - new DateTime(1899, 12, 30)).TotalDays;
                return serial > 0;
            }
            return false;
        }

        // 判断项目是否在开始下一版前需要把上一导出快照自动晋升为新对比基准
        public static bool ShouldPromoteExportedToBaseline(LineupProject p) {
            if (p == null || p.LatestExported == null || p.LatestExported.Records == null || p.LatestExported.Records.Count == 0) return false;
            if (p.Baseline == null || p.Baseline.Records == null || p.Baseline.Records.Count == 0) return true;
            return !string.Equals((p.Baseline.Version ?? "").Trim(), (p.LatestExported.Version ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
