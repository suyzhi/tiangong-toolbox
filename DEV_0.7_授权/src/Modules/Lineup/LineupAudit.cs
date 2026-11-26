using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TianGongCadSuite {
    public enum LineupAuditSeverity { Error, Warning, Info }

    public sealed class LineupAuditIssue {
        public LineupAuditSeverity Severity;
        public string RuleCode = "";
        public string RecordId = "";
        public string Number = "";
        public string Category = "";
        public string Message = "";
    }

    public sealed class LineupAuditItem {
        public int Seq;
        public string Scope = "";
        public string Requirement = "";
        public string AutoStatus = "";
        public string Detail = "";
    }

    public sealed class LineupAuditResult {
        public List<LineupAuditIssue> Issues = new List<LineupAuditIssue>();
        public List<LineupAuditItem> Checklist = new List<LineupAuditItem>();
        public int ErrorCount { get { return Issues.Count(i => i.Severity == LineupAuditSeverity.Error); } }
        public int WarningCount { get { return Issues.Count(i => i.Severity == LineupAuditSeverity.Warning); } }
        public int InfoCount { get { return Issues.Count(i => i.Severity == LineupAuditSeverity.Info); } }
    }

    // 对照公司 Lineup《签字页》核心审查条款的纯逻辑自检引擎（无 CAD / UI 依赖）
    public static class LineupAudit {
        static bool IsReserved(LineupRecord r) {
            string d = (r.Description ?? "") + " " + (r.Parameters ?? "") + " " + (r.Model ?? "") + " " + (r.Notes ?? "");
            return d.Contains("预留") || d.Contains("备用") || d.Contains("盖板") || d.Contains("空位");
        }

        static bool IsBlowOrVacuumPort(LineupRecord r) {
            string d = (r.Description ?? "") + " " + (r.Number ?? "") + " " + (r.Home ?? "");
            return d.Contains("吹气") || d.Contains("破真空") || d.Contains("吸盘") || d.Contains("减压阀")
                || d.Contains("非真空") || d.Contains("真空发生") || Regex.IsMatch((r.Number ?? "").Trim(), @"C[0-9]+[a-z]$", RegexOptions.IgnoreCase);
        }

        static bool IsAirLineOrDummyValve(LineupRecord r) {
            string d = (r.Description ?? "") + " " + (r.Parameters ?? "");
            return d.Contains("主气管") || d.Contains("平衡气缸") || d.Contains("直接供气");
        }

        public static LineupAuditResult Run(LineupProject p) {
            var res = new LineupAuditResult();
            if (p == null) return res;
            var records = (p.Records ?? new List<LineupRecord>()).Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))).ToList();

            // 1. 编号唯一与空编号
            var byNum = new Dictionary<string, List<LineupRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records) {
                string n = (r.Number ?? "").Trim();
                if (n.Length == 0) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "NUM_EMPTY",
                        RecordId = r.Id, Number = "(空)", Category = r.Category,
                        Message = "记录未填写编号（若是预留或待定项可保留，正式出图前请确认）。"
                    });
                    continue;
                }
                List<LineupRecord> list;
                if (!byNum.TryGetValue(n, out list)) { list = new List<LineupRecord>(); byNum[n] = list; }
                list.Add(r);
            }
            foreach (var kv in byNum.Where(x => x.Value.Count > 1)) {
                foreach (var r in kv.Value) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Error, RuleCode = "NUM_DUP",
                        RecordId = r.Id, Number = kv.Key, Category = r.Category,
                        Message = "编号重复出现 " + kv.Value.Count + " 次：" + kv.Key
                    });
                }
            }

            // 收集集合用于跨类目对应检查
            var cyls = records.Where(r => LineupTableData.Category(r.Category) == "气缸").ToList();
            var valves = records.Where(r => LineupTableData.Category(r.Category) == "阀片").ToList();
            var cylSensors = records.Where(r => LineupTableData.Category(r.Category) == "气缸传感器").ToList();
            var genSensors = records.Where(r => LineupTableData.Category(r.Category) == "普通传感器").ToList();
            var others = records.Where(r => LineupTableData.Category(r.Category) == "其他电气件").ToList();

            var valveNums = new HashSet<string>(valves.Select(v => (v.Number ?? "").Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            var cylByNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            var cylValveTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cyls) {
                string cn = (c.Number ?? "").Trim();
                if (cn.Length > 0 && !cylByNum.ContainsKey(cn)) cylByNum[cn] = c;
                string vn = LineupNumbering.ValveNumberForCylinder(cn);
                if (vn.Length > 0) cylValveTargets.Add(vn);
            }

            // 2. 气缸必填项 + 对应阀片检查
            foreach (var c in cyls) {
                if (IsReserved(c)) continue;
                string cn = (c.Number ?? "").Trim();
                var miss = new List<string>();
                if (string.IsNullOrWhiteSpace(c.Description)) miss.Add("描述");
                if (string.IsNullOrWhiteSpace(c.Model) && string.IsNullOrWhiteSpace(c.Notes)) miss.Add("型号(或备注说明原因)");
                if (!IsBlowOrVacuumPort(c)) {
                    if (string.IsNullOrWhiteSpace(c.Home)) miss.Add("原位");
                    if (string.IsNullOrWhiteSpace(c.Mounting)) miss.Add("安装方式");
                }
                if (miss.Count > 0) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "CYL_FIELD",
                        RecordId = c.Id, Number = cn, Category = "气缸",
                        Message = "气缸缺少字段：" + string.Join("、", miss)
                    });
                }
                string expectedValve = LineupNumbering.ValveNumberForCylinder(cn);
                if (expectedValve.Length > 0 && !valveNums.Contains(expectedValve)) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "CYL_NO_VALVE",
                        RecordId = c.Id, Number = cn, Category = "气缸",
                        Message = "缺少对应的控制阀片：" + expectedValve + "（可用「从气缸一键生成阀片」补齐）"
                    });
                }
            }

            // 3. 阀片检查（对应气缸、型号/品牌、阀岛/汇流排/单片阀标注）
            foreach (var v in valves) {
                string vn = (v.Number ?? "").Trim();
                if (IsReserved(v) || IsAirLineOrDummyValve(v)) continue;
                var miss = new List<string>();
                if (string.IsNullOrWhiteSpace(v.Model)) miss.Add("型号");
                if (string.IsNullOrWhiteSpace(v.Brand)) miss.Add("品牌");
                string islandLabel = LineupNumbering.IslandLabel(p, v);
                if (string.IsNullOrWhiteSpace(islandLabel) && string.IsNullOrWhiteSpace(v.Mounting)) miss.Add("安装方式(阀岛/汇流排/单片阀)");
                if (miss.Count > 0) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "VALVE_FIELD",
                        RecordId = v.Id, Number = vn, Category = "阀片",
                        Message = "阀片缺少字段：" + string.Join("、", miss)
                    });
                }
                if (vn.Length > 0 && Regex.IsMatch(vn, @"-Y[0-9]+V[0-9]+$", RegexOptions.IgnoreCase) && !cylValveTargets.Contains(vn)) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "VALVE_NO_CYL",
                        RecordId = v.Id, Number = vn, Category = "阀片",
                        Message = "未在气缸列表找到与阀片 " + vn + " 对应的气缸（若为预留位请在描述/功能参数写明“预留”或“盖板”）"
                    });
                }
            }

            // 4. 气缸传感器成对检查 + 字段 + 备注一致性
            var sensorsByCyl = new Dictionary<string, List<LineupRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in cylSensors) {
                string sn = (s.Number ?? "").Trim();
                var m = Regex.Match(sn, @"^(.+?)([HWM][0-9]*)$", RegexOptions.IgnoreCase);
                string baseCyl = m.Success ? m.Groups[1].Value.Trim() : "";
                if (baseCyl.Length > 0) {
                    List<LineupRecord> list;
                    if (!sensorsByCyl.TryGetValue(baseCyl, out list)) { list = new List<LineupRecord>(); sensorsByCyl[baseCyl] = list; }
                    list.Add(s);
                }
                if (!IsReserved(s)) {
                    var miss = new List<string>();
                    if (string.IsNullOrWhiteSpace(s.Model)) miss.Add("型号");
                    if (string.IsNullOrWhiteSpace(s.Brand)) miss.Add("品牌");
                    if (string.IsNullOrWhiteSpace(s.Connector)) miss.Add("传感器接口类型");
                    if (miss.Count > 0) {
                        res.Issues.Add(new LineupAuditIssue {
                            Severity = LineupAuditSeverity.Warning, RuleCode = "CSEN_FIELD",
                            RecordId = s.Id, Number = sn, Category = "气缸传感器",
                            Message = "气缸传感器缺少字段：" + string.Join("、", miss)
                        });
                    }
                    LineupRecord parentCyl;
                    if (baseCyl.Length > 0 && cylByNum.TryGetValue(baseCyl, out parentCyl)) {
                        string cNote = (parentCyl.Notes ?? "").Trim();
                        string sNote = (s.Notes ?? "").Trim();
                        if (cNote.Length > 0 && sNote.Length > 0 && !string.Equals(cNote, sNote, StringComparison.OrdinalIgnoreCase)) {
                            res.Issues.Add(new LineupAuditIssue {
                                Severity = LineupAuditSeverity.Info, RuleCode = "CSEN_NOTE_DIFF",
                                RecordId = s.Id, Number = sn, Category = "气缸传感器",
                                Message = "备注（" + sNote + "）与对应气缸 " + baseCyl + " 备注（" + cNote + "）不一致"
                            });
                        }
                    }
                }
            }
            foreach (var c in cyls) {
                if (IsReserved(c) || IsBlowOrVacuumPort(c)) continue;
                string cn = (c.Number ?? "").Trim();
                if (cn.Length == 0) continue;
                List<LineupRecord> slist;
                sensorsByCyl.TryGetValue(cn, out slist);
                bool hasH = slist != null && slist.Any(x => Regex.IsMatch((x.Number ?? "").Trim(), @"H[0-9]*$", RegexOptions.IgnoreCase));
                bool hasW = slist != null && slist.Any(x => Regex.IsMatch((x.Number ?? "").Trim(), @"W[0-9]*$", RegexOptions.IgnoreCase));
                if (!hasH || !hasW) {
                    string lacking = (!hasH && !hasW) ? "H 原位与 W 工作位" : (!hasH ? "H 原位" : "W 工作位");
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "CYL_NO_SENSOR",
                        RecordId = c.Id, Number = cn, Category = "气缸",
                        Message = "气缸缺少 " + lacking + " 传感器"
                    });
                }
            }

            // 5. 普通传感器检查
            foreach (var s in genSensors) {
                if (IsReserved(s)) continue;
                var miss = new List<string>();
                if (string.IsNullOrWhiteSpace(s.Description)) miss.Add("描述");
                if (string.IsNullOrWhiteSpace(s.Model) && string.IsNullOrWhiteSpace(s.Notes)) miss.Add("型号(或备注原因)");
                if (string.IsNullOrWhiteSpace(s.Brand) && !(s.Model ?? "").Contains("提供")) miss.Add("品牌");
                if (string.IsNullOrWhiteSpace(s.Connector)) miss.Add("传感器接口类型");
                if (miss.Count > 0) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "GSEN_FIELD",
                        RecordId = s.Id, Number = (s.Number ?? "").Trim(), Category = "普通传感器",
                        Message = "普通传感器缺少字段：" + string.Join("、", miss)
                    });
                }
            }

            // 6. 其他电气件检查
            foreach (var e in others) {
                if (IsReserved(e)) continue;
                var miss = new List<string>();
                if (string.IsNullOrWhiteSpace(e.Description)) miss.Add("描述");
                if (string.IsNullOrWhiteSpace(e.Model) && string.IsNullOrWhiteSpace(e.Notes)) miss.Add("型号");
                if (miss.Count > 0) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Warning, RuleCode = "OTHER_FIELD",
                        RecordId = e.Id, Number = (e.Number ?? "").Trim(), Category = "其他电气件",
                        Message = "其他电气件缺少字段：" + string.Join("、", miss)
                    });
                }
                if ((e.Description ?? "").Contains("按钮盒") && !(e.Description ?? "").Contains("孔") && !(e.Description ?? "").Contains("（") && !(e.Description ?? "").Contains("(")) {
                    res.Issues.Add(new LineupAuditIssue {
                        Severity = LineupAuditSeverity.Info, RuleCode = "BTN_BOX_DESC",
                        RecordId = e.Id, Number = (e.Number ?? "").Trim(), Category = "其他电气件",
                        Message = "按钮盒描述建议注明孔位与功能，例如：3孔按钮盒（急停，启动，复位）"
                    });
                }
            }

            // 7. CAD 位置图覆盖率与重复标注检查（同时支持 pt.Id 与 pt.Number 匹配，避免 BOM 改号后误报 CAD_MISSING）
            var pages = p.CadPages ?? new List<LineupCadPage>();
            if (pages.Count > 0) {
                var recById = new Dictionary<string, LineupRecord>(StringComparer.Ordinal);
                foreach (var r in records) {
                    if (!string.IsNullOrEmpty(r.Id) && !recById.ContainsKey(r.Id)) recById[r.Id] = r;
                }
                var coveredNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var coveredIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pg in pages) {
                    var onPage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pt in pg.Points ?? new List<LineupLocationPoint>()) {
                        LineupRecord linked = null;
                        if (!string.IsNullOrEmpty(pt.Id)) {
                            coveredIds.Add(pt.Id);
                            recById.TryGetValue(pt.Id, out linked);
                        }
                        string pn = (linked != null && !string.IsNullOrWhiteSpace(linked.Number))
                            ? linked.Number.Trim()
                            : (pt.Number ?? "").Trim();
                        if (pn.Length == 0) continue;
                        coveredNumbers.Add(pn);
                        if (!onPage.Add(pn)) {
                            res.Issues.Add(new LineupAuditIssue {
                                Severity = LineupAuditSeverity.Warning, RuleCode = "CAD_DUP",
                                RecordId = pt.Id, Number = pn, Category = "CAD位置图",
                                Message = "位置图「" + pg.Name + "」中编号重复标注：" + pn
                            });
                        }
                        if (pt.State != "ok") {
                            res.Issues.Add(new LineupAuditIssue {
                                Severity = LineupAuditSeverity.Warning, RuleCode = "CAD_UNRESOLVED",
                                RecordId = pt.Id, Number = pn, Category = "CAD位置图",
                                Message = "位置图「" + pg.Name + "」中 " + pn + " 尚未定位箭头坐标（" + (pt.Reason ?? "待手动点选") + "）"
                            });
                        }
                    }
                }
                foreach (var r in records) {
                    if (IsReserved(r)) continue;
                    string cat = LineupTableData.Category(r.Category);
                    // 位置图重点核查需在总图/机构图上标注的主器件（气缸、普通传感器、其他电气件）；阀片与磁开通常随气缸/阀岛成组
                    if (cat == "阀片" || cat == "气缸传感器") continue;
                    string rn = (r.Number ?? "").Trim();
                    bool hasId = !string.IsNullOrEmpty(r.Id) && coveredIds.Contains(r.Id);
                    bool hasNum = rn.Length > 0 && coveredNumbers.Contains(rn);
                    if (rn.Length > 0 && !hasId && !hasNum) {
                        res.Issues.Add(new LineupAuditIssue {
                            Severity = LineupAuditSeverity.Info, RuleCode = "CAD_MISSING",
                            RecordId = r.Id, Number = rn, Category = r.Category,
                            Message = "编号 " + rn + " 尚未出现在任何一张 LineUP第三版_CAD 位置图中"
                        });
                    }
                }
            }

            // 8. 生成公司《签字页》标准自检表摘要
            BuildChecklist(res, records, pages);
            return res;
        }

        static void BuildChecklist(LineupAuditResult res, List<LineupRecord> records, List<LineupCadPage> pages) {
            Func<string[], string> statusOf = codes => {
                var hits = res.Issues.Where(i => codes.Contains(i.RuleCode)).ToList();
                if (hits.Count == 0) return "通过";
                int errs = hits.Count(h => h.Severity == LineupAuditSeverity.Error);
                int warns = hits.Count(h => h.Severity == LineupAuditSeverity.Warning);
                if (errs > 0) return "不通过(" + errs + "错" + (warns > 0 ? "/" + warns + "警" : "") + ")";
                if (warns > 0) return "待确认(" + warns + "项)";
                return "通过(" + hits.Count + "条提示)";
            };
            Func<string[], string> detailOf = codes => {
                var hits = res.Issues.Where(i => codes.Contains(i.RuleCode)).Take(5).Select(h => h.Number + ":" + h.Message).ToArray();
                int total = res.Issues.Count(i => codes.Contains(i.RuleCode));
                if (total == 0) return "";
                return string.Join("；", hits) + (total > 5 ? " 等共 " + total + " 处" : "");
            };

            res.Checklist.Add(new LineupAuditItem { Seq = 1, Scope = "更新日志", Requirement = "每次更新日志需如实填写版本号、日期与编辑人", AutoStatus = "已自动填入", Detail = "" });
            res.Checklist.Add(new LineupAuditItem { Seq = 2, Scope = "BOM 编号唯一性", Requirement = "全表编号无重复、无遗漏", AutoStatus = statusOf(new[] { "NUM_DUP", "NUM_EMPTY" }), Detail = detailOf(new[] { "NUM_DUP", "NUM_EMPTY" }) });
            res.Checklist.Add(new LineupAuditItem { Seq = 3, Scope = "BOM(气缸列表)", Requirement = "气缸描述、型号、原位及安装方式填写完整", AutoStatus = statusOf(new[] { "CYL_FIELD" }), Detail = detailOf(new[] { "CYL_FIELD" }) });
            res.Checklist.Add(new LineupAuditItem { Seq = 4, Scope = "BOM(气缸↔阀片)", Requirement = "阀片编号与气缸一一对应，控制方式（阀岛/汇流排/单片阀）标注清楚", AutoStatus = statusOf(new[] { "CYL_NO_VALVE", "VALVE_NO_CYL", "VALVE_FIELD" }), Detail = detailOf(new[] { "CYL_NO_VALVE", "VALVE_NO_CYL", "VALVE_FIELD" }) });
            res.Checklist.Add(new LineupAuditItem { Seq = 5, Scope = "BOM(气缸传感器)", Requirement = "每个气缸 H/W 传感器齐全，型号/品牌/接口类型完整，备注与气缸一致", AutoStatus = statusOf(new[] { "CYL_NO_SENSOR", "CSEN_FIELD", "CSEN_NOTE_DIFF" }), Detail = detailOf(new[] { "CYL_NO_SENSOR", "CSEN_FIELD", "CSEN_NOTE_DIFF" }) });
            res.Checklist.Add(new LineupAuditItem { Seq = 6, Scope = "BOM(普通传感器)", Requirement = "检测类传感器描述、型号、品牌、接口类型完整（常闭需注明）", AutoStatus = statusOf(new[] { "GSEN_FIELD" }), Detail = detailOf(new[] { "GSEN_FIELD" }) });
            res.Checklist.Add(new LineupAuditItem { Seq = 7, Scope = "BOM(其他电气件)", Requirement = "气源处理/阀岛/伺服/相机/安全门锁/光栅/按钮盒等明细完整", AutoStatus = statusOf(new[] { "OTHER_FIELD", "BTN_BOX_DESC" }), Detail = detailOf(new[] { "OTHER_FIELD", "BTN_BOX_DESC" }) });
            res.Checklist.Add(new LineupAuditItem {
                Seq = 8, Scope = "第三版 CAD 位置图",
                Requirement = "编号在图上是否齐全、无重复、箭头指向准确",
                AutoStatus = pages.Count == 0 ? "未生成位置图" : statusOf(new[] { "CAD_DUP", "CAD_UNRESOLVED", "CAD_MISSING" }),
                Detail = pages.Count == 0 ? "共 " + records.Count + " 条 BOM，尚未在插件中生成 CAD 位置图页" : "共 " + pages.Count + " 页位置图；" + detailOf(new[] { "CAD_DUP", "CAD_UNRESOLVED", "CAD_MISSING" })
            });
        }
    }
}
