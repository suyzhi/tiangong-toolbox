using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace TianGongCadSuite {
    // 通过本机官方 Excel / WPS COM Automation 接口只读读取工作簿：
    // · 用于读取公司透明加密（%TSD-Header%）的 Lineup Excel 表格以及旧版 .xls；
    // · 完全走合法的表格进程读取，只在内存中提取单元格值与纵向合并区，绝不向磁盘写入任何明文副本；
    // · 严格隔离用户已打开的 Excel / WPS 窗口：只关闭本次新打开的工作簿，仅在由插件新拉起的空实例上调用 Quit()。
    internal static class LineupExcelComReader {
        const int MaxBomRows = 5000;
        const int MaxBomCols = 15; // A..O，覆盖 10/11 列及少量冗余列
        const int ComEngineTimeoutMs = 20000; // 单个表格引擎最多等待 20 秒，防止隐藏密码/激活弹窗拖死 CAD
        const int RpcECallRejected = unchecked((int)0x80010001);
        const int RpcEServerCallRetryLater = unchecked((int)0x8001010A);
        const int MsoAutomationSecurityForceDisable = 3;

        sealed class ComEngine {
            public readonly string DisplayName;
            public readonly string ProgId;
            public readonly string ProcessName;
            // WPS 在部分版本下 CreateInstance 会复用到已打开的 et.exe 进程，需额外保护用户窗口
            public readonly bool SingleInstanceRisk;

            public ComEngine(string displayName, string progId, string processName, bool singleInstanceRisk) {
                DisplayName = displayName;
                ProgId = progId;
                ProcessName = processName;
                SingleInstanceRisk = singleInstanceRisk;
            }
        }

        sealed class ComWorkbookSnapshot {
            public SortedDictionary<int, Dictionary<int, string>> BomGrid;
            public List<LineupCellMerge> BomMerges;
            public HashSet<int> BomStruckRows;
            public SortedDictionary<int, Dictionary<int, string>> CadGrid;
            public SortedDictionary<int, Dictionary<int, string>> LogGrid;
            public string EngineName;
        }

        static readonly ComEngine EngineExcel = new ComEngine("Excel", "Excel.Application", "EXCEL", false);
        static readonly ComEngine EngineWpsKet = new ComEngine("WPS 表格", "KET.Application", "et", true);
        static readonly ComEngine EngineWpsEt = new ComEngine("WPS 表格(ET)", "ET.Application", "et", true);

        static readonly ComEngine[] Engines = { EngineExcel, EngineWpsKet, EngineWpsEt };

        // 优先选用用户系统默认的 .xlsx 关联软件（如 WPS 默认为 ET.Xlsx.6 时优先走 WPS，避免唤起未激活的 Excel）
        internal static bool PrefersWpsByDefault() {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.xlsx\UserChoice")) {
                    string prog = key != null ? Convert.ToString(key.GetValue("ProgId")) : "";
                    if (!string.IsNullOrEmpty(prog) &&
                        (prog.IndexOf("ET.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         prog.IndexOf("KET.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         prog.IndexOf("WPS", StringComparison.OrdinalIgnoreCase) >= 0)) {
                        return true;
                    }
                }
            } catch { }
            return false;
        }

        static ComEngine[] OrderedEngines() {
            return PrefersWpsByDefault()
                ? new[] { EngineWpsKet, EngineWpsEt, EngineExcel }
                : new[] { EngineExcel, EngineWpsKet, EngineWpsEt };
        }

        public static LineupImport Read(string path, bool allowDuplicates, bool isTsd) {
            string fullPath = Path.GetFullPath(path);
            var errors = new List<string>();
            int availableCount = 0;

            foreach (var engine in OrderedEngines()) {
                Type comType = Type.GetTypeFromProgID(engine.ProgId);
                if (comType == null) continue;
                availableCount++;

                LineupImport parsed;
                string err;
                if (TryReadWithEngine(fullPath, engine, comType, allowDuplicates, isTsd, out parsed, out err)) {
                    return parsed;
                }
                if (!string.IsNullOrEmpty(err)) errors.Add(engine.DisplayName + "：" + err);
            }

            string detail = availableCount == 0
                ? "本机未检测到可用的 Excel 或 WPS 表格组件"
                : (errors.Count > 0 ? string.Join("；", errors) : "未能识别工作簿内容");

            if (isTsd) {
                throw new InvalidDataException(
                    "该文件受公司透明加密保护（%TSD-Header%），已尝试通过本机 Excel / WPS 只读读取，但未成功（" + detail + "）。\n"
                    + "请改用以下任一方式导入：\n"
                    + "1. 在 WPS / Excel 中打开该文件，全选 LineUP第二版_BOM 复制，回到插件点「更多 ▾ → 导入 / 导出 → 从剪贴板粘贴整张清单」；\n"
                    + "2. 或先使用已解密的明文 .xlsx / .xlsm 副本导入。");
            }
            throw new InvalidDataException(
                "无法通过本机 Excel / WPS 读取该工作簿（" + detail + "）。\n"
                + "请确认文件未损坏、未设置打开密码，或改在 Excel / WPS 中打开并复制 LineUP第二版_BOM 后，使用插件「更多 ▾ → 导入 / 导出 → 从剪贴板粘贴整张清单」。");
        }

        // 供测试指定单一 COM 引擎（Excel.Application / KET.Application）独立验证
        internal static LineupImport ReadWithProgId(string path, string progId, bool allowDuplicates, bool isTsd) {
            string fullPath = Path.GetFullPath(path);
            var engine = Engines.FirstOrDefault(e => string.Equals(e.ProgId, progId, StringComparison.OrdinalIgnoreCase))
                ?? new ComEngine(progId, progId, "", true);
            Type comType = Type.GetTypeFromProgID(engine.ProgId);
            if (comType == null) throw new InvalidOperationException("本机未注册 COM 组件：" + engine.ProgId);
            LineupImport parsed;
            string err;
            if (!TryReadWithEngine(fullPath, engine, comType, allowDuplicates, isTsd, out parsed, out err)) {
                throw new InvalidDataException(engine.DisplayName + " 读取失败：" + err);
            }
            return parsed;
        }

        static bool TryReadWithEngine(
            string fullPath,
            ComEngine engine,
            Type comType,
            bool allowDuplicates,
            bool isTsd,
            out LineupImport result,
            out string error) {
            result = null;
            error = "";
            ComWorkbookSnapshot snap = null;
            try {
                // 在独立 STA 线程 + 超时看门狗下运行 COM 读取，避免坏文件/密码框/加密弹窗拖死天工 CAD 界面
                snap = RunExtractWithWatchdog(fullPath, engine, comType, ComEngineTimeoutMs);
            } catch (Exception ex) {
                error = CleanError(ex);
                return false;
            } finally {
                ReleaseComGarbage();
            }

            if (snap == null || snap.BomGrid == null || snap.BomGrid.Count == 0) {
                error = "工作簿内容为空";
                return false;
            }
            // 若该表格引擎不在公司解密白名单中，Excel/WPS 可能把 %TSD 密文直接当成纯文本打开
            if (isTsd && LineupXlsxReader.LooksLikeRawTsdGrid(snap.BomGrid)) {
                error = "该表格进程未能解密透明加密文件（读取到原始 %TSD 密文头）";
                return false;
            }

            try {
                var imported = LineupXlsxReader.ParseBomGrid(snap.BomGrid, snap.BomMerges, allowDuplicates, snap.BomStruckRows);
                if (snap.CadGrid != null && snap.CadGrid.Count > 0) {
                    LineupXlsxReader.ApplyCadMetadataRows(snap.CadGrid, imported);
                }
                if (snap.LogGrid != null && snap.LogGrid.Count > 0) {
                    LineupXlsxReader.ParseChangelogGrid(snap.LogGrid, imported);
                }
                result = imported;
                return true;
            } catch (InvalidDataException ex) {
                // 编号重复说明文件已成功解密且解析出真实 BOM，不应吞掉继续尝试下一个引擎
                if (ex.Message != null && ex.Message.Contains("编号重复")) throw;
                // 找不到 BOM 行时，若为加密文件则可能只是该引擎读到了乱码，允许切下一个引擎重试
                if (isTsd) {
                    error = ex.Message;
                    return false;
                }
                throw;
            }
        }

        static ComWorkbookSnapshot RunExtractWithWatchdog(string fullPath, ComEngine engine, Type comType, int timeoutMs) {
            ComWorkbookSnapshot snap = null;
            Exception workerError = null;
            int[] ownedSpawnedPid = new int[1]; // 仅记录插件本次新拉起且归我们管理的隐藏进程 PID

            var worker = new Thread(() => {
                try {
                    snap = ExtractSnapshotIsolated(fullPath, engine, comType, ownedSpawnedPid);
                } catch (Exception ex) {
                    workerError = ex;
                } finally {
                    ReleaseComGarbage();
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();

            if (!worker.Join(timeoutMs)) {
                int pid = Thread.VolatileRead(ref ownedSpawnedPid[0]);
                if (pid > 0) {
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                    worker.Join(2000);
                }
                throw new TimeoutException("打开工作簿超时（可能弹出密码框、激活提示或加密拦截）");
            }
            if (workerError != null) throw workerError;
            return snap;
        }

        static ComWorkbookSnapshot ExtractSnapshotIsolated(string fullPath, ComEngine engine, Type comType, int[] ownedSpawnedPid) {
            var beforePids = new HashSet<int>();
            if (!string.IsNullOrEmpty(engine.ProcessName)) {
                foreach (var p in Process.GetProcessesByName(engine.ProcessName)) {
                    try { beforePids.Add(p.Id); } catch { }
                }
            }

            object app = null;
            object workbooks = null;
            object wb = null;
            object sheets = null;
            bool ownServer = false;
            bool openedByUs = false;
            object prevAlerts = null;
            bool alertsChanged = false;

            try {
                app = Activator.CreateInstance(comType);
                workbooks = GetProp(app, "Workbooks");
                int initialWbCount = SafeCount(workbooks);

                int spawnedPid = 0;
                if (!string.IsNullOrEmpty(engine.ProcessName)) {
                    foreach (var p in Process.GetProcessesByName(engine.ProcessName)) {
                        try { if (!beforePids.Contains(p.Id)) { spawnedPid = p.Id; break; } } catch { }
                    }
                }
                bool spawnedNewProc = spawnedPid > 0;
                bool wasVisible = false;
                try {
                    object vis = GetProp(app, "Visible");
                    if (vis is bool) wasVisible = (bool)vis;
                } catch { }

                // 仅当该实例没有已打开工作簿，且是本次新拉起或纯后台不可见 Automation 实例时才归我们接管并 Quit，
                // 避免误关用户正在查看的 WPS / Excel 可见窗口（同时也不会漏关 WPS 后台预启动的 Automation 进程）
                ownServer = engine.SingleInstanceRisk
                    ? (initialWbCount == 0 && (spawnedNewProc || !wasVisible))
                    : (initialWbCount == 0);

                if (ownServer && spawnedPid > 0 && ownedSpawnedPid != null) {
                    Thread.VolatileWrite(ref ownedSpawnedPid[0], spawnedPid);
                }

                if (ownServer) {
                    TrySetProp(app, "Visible", false);
                    TrySetProp(app, "ScreenUpdating", false);
                    TrySetProp(app, "AskToUpdateLinks", false);
                    TrySetProp(app, "EnableEvents", false);
                    TrySetProp(app, "AutomationSecurity", MsoAutomationSecurityForceDisable);
                }
                try {
                    prevAlerts = GetProp(app, "DisplayAlerts");
                    SetProp(app, "DisplayAlerts", false);
                    alertsChanged = true;
                } catch { }

                // 如果复用的进程里用户本来就开着同一个工作簿，直接只读提取且绝不帮用户关闭
                if (initialWbCount > 0) {
                    wb = FindAlreadyOpenWorkbook(workbooks, initialWbCount, fullPath);
                }
                if (wb == null) {
                    wb = OpenReadOnlyWithRetry(workbooks, fullPath);
                    openedByUs = true;
                }

                sheets = GetProp(wb, "Worksheets");
                int sheetCount = SafeCount(sheets);
                if (sheetCount <= 0) throw new InvalidDataException("工作簿中没有找到任何工作表。");

                var sheetNames = new List<string>(sheetCount);
                for (int i = 1; i <= sheetCount; i++) {
                    object sh = null;
                    try {
                        sh = GetIndexed(sheets, "Item", i);
                        sheetNames.Add(Convert.ToString(GetProp(sh, "Name")) ?? "");
                    } finally {
                        Release(sh);
                    }
                }

                int bomIdx = LineupXlsxReader.PickBomSheetIndex(sheetNames);
                int cadIdx = LineupXlsxReader.PickCadSheetIndex(sheetNames);
                int logIdx = LineupXlsxReader.PickChangelogSheetIndex(sheetNames);

                var snap = new ComWorkbookSnapshot {
                    EngineName = engine.DisplayName,
                    BomGrid = new SortedDictionary<int, Dictionary<int, string>>(),
                    BomMerges = new List<LineupCellMerge>(),
                    BomStruckRows = new HashSet<int>(),
                    CadGrid = new SortedDictionary<int, Dictionary<int, string>>(),
                    LogGrid = new SortedDictionary<int, Dictionary<int, string>>()
                };

                object bomWs = null;
                try {
                    bomWs = GetIndexed(sheets, "Item", bomIdx + 1);
                    ReadBomWorksheet(bomWs, snap.BomGrid, snap.BomMerges, snap.BomStruckRows);
                } finally {
                    Release(bomWs);
                }

                if (cadIdx >= 0) {
                    object cadWs = null;
                    try {
                        cadWs = GetIndexed(sheets, "Item", cadIdx + 1);
                        ReadCadHeaderWorksheet(cadWs, snap.CadGrid);
                    } catch {
                        // CAD 抬头读取失败不影响主 BOM 导入
                    } finally {
                        Release(cadWs);
                    }
                }

                if (logIdx >= 0) {
                    object logWs = null;
                    try {
                        logWs = GetIndexed(sheets, "Item", logIdx + 1);
                        ReadChangelogWorksheet(logWs, snap.LogGrid);
                    } catch {
                        // 更新日志读取失败不影响主 BOM 导入
                    } finally {
                        Release(logWs);
                    }
                }

                return snap;
            } finally {
                Release(sheets);
                if (wb != null) {
                    if (openedByUs) {
                        try { Call(wb, "Close", false); } catch { }
                    }
                    Release(wb);
                }
                if (app != null && alertsChanged && !ownServer && prevAlerts != null) {
                    TrySetProp(app, "DisplayAlerts", prevAlerts);
                }
                int remainingWbs = workbooks != null ? SafeCount(workbooks) : 0;
                Release(workbooks);
                if (app != null && ownServer && remainingWbs == 0) {
                    try { Call(app, "Quit"); } catch { }
                }
                Release(app);
            }
        }

        static object FindAlreadyOpenWorkbook(object workbooks, int count, string targetFullPath) {
            for (int i = 1; i <= count; i++) {
                object candidate = null;
                try {
                    candidate = GetIndexed(workbooks, "Item", i);
                    string fn = Convert.ToString(GetProp(candidate, "FullName")) ?? "";
                    if (!string.IsNullOrWhiteSpace(fn)) {
                        string norm;
                        try { norm = Path.GetFullPath(fn); } catch { norm = fn; }
                        if (string.Equals(norm, targetFullPath, StringComparison.OrdinalIgnoreCase)) {
                            object matched = candidate;
                            candidate = null; // 交给调用方持有和释放
                            return matched;
                        }
                    }
                } catch {
                } finally {
                    Release(candidate);
                }
            }
            return null;
        }

        static object OpenReadOnlyWithRetry(object workbooks, string fullPath) {
            for (int attempt = 0; attempt < 3; attempt++) {
                try {
                    try {
                        // 传入空密码可让加密口令文件立即报错，而不会在隐藏进程中无限卡死等密码框
                        return Call(workbooks, "Open", fullPath, 0, true, Type.Missing, "", "", true);
                    } catch (TargetInvocationException tie) {
                        var inner = tie.InnerException;
                        if (inner is ArgumentException || inner is MissingMemberException) {
                            return Call(workbooks, "Open", fullPath, 0, true);
                        }
                        throw;
                    } catch (ArgumentException) {
                        return Call(workbooks, "Open", fullPath, 0, true);
                    }
                } catch (Exception ex) {
                    var comEx = UnwrapComException(ex);
                    if (comEx != null && (comEx.ErrorCode == RpcECallRejected || comEx.ErrorCode == RpcEServerCallRetryLater) && attempt < 2) {
                        Thread.Sleep(180);
                        continue;
                    }
                    throw;
                }
            }
            throw new InvalidOperationException("表格组件持续繁忙，无法打开工作簿。");
        }

        static void ReadBomWorksheet(
            object ws,
            SortedDictionary<int, Dictionary<int, string>> grid,
            List<LineupCellMerge> merges,
            HashSet<int> struckRows) {
            object usedRange = null;
            object usedRows = null;
            object usedCols = null;
            object readRange = null;
            try {
                usedRange = GetProp(ws, "UsedRange");
                if (usedRange == null) return;
                int firstRow = Convert.ToInt32(GetProp(usedRange, "Row"));
                int firstCol = Convert.ToInt32(GetProp(usedRange, "Column"));
                usedRows = GetProp(usedRange, "Rows");
                usedCols = GetProp(usedRange, "Columns");
                int rowCount = Convert.ToInt32(GetProp(usedRows, "Count"));
                int colCount = Convert.ToInt32(GetProp(usedCols, "Count"));
                if (rowCount <= 0 || colCount <= 0 || firstCol > MaxBomCols) return;

                int maxRow = Math.Min(firstRow + rowCount - 1, MaxBomRows);
                int maxCol = Math.Min(firstCol + colCount - 1, MaxBomCols);
                if (maxRow < firstRow || maxCol < firstCol) return;

                string addr = ColLetters(firstCol) + firstRow + ":" + ColLetters(maxCol) + maxRow;
                readRange = GetIndexed(ws, "Range", addr);

                // 一次 COM 往返取回整个 UsedRange 区域值，避免逐格跨进程调用
                object rawVal = GetProp(readRange, "Value2");
                FillGridFromRangeValue(rawVal, firstRow, firstCol, grid);

                // 仅对存在合并单元格的列、且仅对非空起始单元格查询 MergeArea（通常 < 50 次调用）
                ReadVerticalMerges(ws, firstRow, maxRow, firstCol, maxCol, grid, merges);

                // 快速探测 A/D 列是否存在删除线（无删除线时仅 1~2 次 COM 调用，零额外开销）
                ReadStrikethroughRows(ws, firstRow, maxRow, grid, struckRows);
            } finally {
                Release(readRange);
                Release(usedCols);
                Release(usedRows);
                Release(usedRange);
            }
        }

        static void ReadStrikethroughRows(
            object ws,
            int firstRow,
            int maxRow,
            SortedDictionary<int, Dictionary<int, string>> grid,
            HashSet<int> struckRows) {
            if (struckRows == null || grid == null || grid.Count == 0) return;
            foreach (int col1 in new[] { 1, 4 }) {
                int col0 = col1 - 1;
                string colAddr = ColLetters(col1);
                bool mayHaveStrike = false;
                object colRange = null;
                object colFont = null;
                try {
                    colRange = GetIndexed(ws, "Range", colAddr + firstRow + ":" + colAddr + maxRow);
                    colFont = GetProp(colRange, "Font");
                    object st = GetProp(colFont, "Strikethrough");
                    // 整列全无删除线时 Excel/WPS 返回 false；全有返回 true，混合返回 DBNull/null
                    if (!(st is bool && !(bool)st)) mayHaveStrike = true;
                } catch {
                    mayHaveStrike = false;
                } finally {
                    Release(colFont);
                    Release(colRange);
                }
                if (!mayHaveStrike) continue;

                foreach (var rowKv in grid) {
                    if (struckRows.Contains(rowKv.Key)) continue;
                    string val;
                    if (!rowKv.Value.TryGetValue(col0, out val) || string.IsNullOrWhiteSpace(val)) continue;
                    object cell = null;
                    object fnt = null;
                    try {
                        cell = GetIndexed(ws, "Range", colAddr + rowKv.Key);
                        fnt = GetProp(cell, "Font");
                        object st = GetProp(fnt, "Strikethrough");
                        if (st is bool && (bool)st) struckRows.Add(rowKv.Key);
                    } catch {
                    } finally {
                        Release(fnt);
                        Release(cell);
                    }
                }
            }
        }

        static void ReadVerticalMerges(
            object ws,
            int firstRow,
            int maxRow,
            int firstCol,
            int maxCol,
            SortedDictionary<int, Dictionary<int, string>> grid,
            List<LineupCellMerge> merges) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int col1 = firstCol; col1 <= maxCol; col1++) {
                int col0 = col1 - 1;
                string colAddr = ColLetters(col1);
                // 先整列一次判断是否包含任何合并单元格：无合并则整列跳过
                bool mayHaveMerges = true;
                object colRange = null;
                try {
                    colRange = GetIndexed(ws, "Range", colAddr + firstRow + ":" + colAddr + maxRow);
                    object state = GetProp(colRange, "MergeCells");
                    if (state is bool && !(bool)state) mayHaveMerges = false;
                } catch {
                    mayHaveMerges = true;
                } finally {
                    Release(colRange);
                }
                if (!mayHaveMerges) continue;

                // 合并区域的文本一定在左上角单元格，所以只查 grid 里该列非空的行即可
                foreach (var rowKv in grid) {
                    string val;
                    if (!rowKv.Value.TryGetValue(col0, out val) || string.IsNullOrWhiteSpace(val)) continue;
                    int r = rowKv.Key;
                    object cell = null;
                    object ma = null;
                    object maRows = null;
                    object maCols = null;
                    try {
                        cell = GetIndexed(ws, "Range", colAddr + r);
                        object isMerged = GetProp(cell, "MergeCells");
                        if (!(isMerged is bool) || !(bool)isMerged) continue;

                        ma = GetProp(cell, "MergeArea");
                        maRows = GetProp(ma, "Rows");
                        maCols = GetProp(ma, "Columns");
                        int r1 = Convert.ToInt32(GetProp(ma, "Row"));
                        int mc1 = Convert.ToInt32(GetProp(ma, "Column")) - 1;
                        int rc = Convert.ToInt32(GetProp(maRows, "Count"));
                        int cc = Convert.ToInt32(GetProp(maCols, "Count"));
                        // 只收集单列纵向合并（安装方式阀岛、备注模块名等），跳过横向标题合并
                        if (cc == 1 && rc > 1 && mc1 >= 0 && mc1 < MaxBomCols) {
                            string key = r1 + ":" + mc1;
                            if (seen.Add(key)) {
                                merges.Add(new LineupCellMerge { R1 = r1, C1 = mc1, R2 = r1 + rc - 1, C2 = mc1 });
                            }
                        }
                    } catch {
                    } finally {
                        Release(maCols);
                        Release(maRows);
                        Release(ma);
                        Release(cell);
                    }
                }
            }
        }

        static string ColLetters(int col1Based) {
            string res = "";
            int n = col1Based;
            while (n > 0) {
                n--;
                res = (char)('A' + (n % 26)) + res;
                n /= 26;
            }
            return res;
        }

        static void ReadCadHeaderWorksheet(object cadWs, SortedDictionary<int, Dictionary<int, string>> cadGrid) {
            object range = null;
            try {
                range = GetIndexed(cadWs, "Range", "A1:Z5");
                object rawVal = GetProp(range, "Value2");
                FillGridFromRangeValue(rawVal, 1, 1, cadGrid);
            } finally {
                Release(range);
            }
        }

        static void ReadChangelogWorksheet(object logWs, SortedDictionary<int, Dictionary<int, string>> logGrid) {
            object range = null;
            try {
                range = GetIndexed(logWs, "Range", "A1:L120");
                object rawVal = GetProp(range, "Value2");
                FillGridFromRangeValue(rawVal, 1, 1, logGrid);
            } finally {
                Release(range);
            }
        }

        static void FillGridFromRangeValue(
            object rawVal,
            int firstRow,
            int firstCol,
            SortedDictionary<int, Dictionary<int, string>> grid) {
            if (rawVal == null || rawVal is DBNull) return;
            var arr = rawVal as object[,];
            if (arr != null) {
                int rLow = arr.GetLowerBound(0), rHigh = arr.GetUpperBound(0);
                int cLow = arr.GetLowerBound(1), cHigh = arr.GetUpperBound(1);
                for (int r = rLow; r <= rHigh; r++) {
                    Dictionary<int, string> rowMap = null;
                    int excelRow = firstRow + (r - rLow);
                    for (int c = cLow; c <= cHigh; c++) {
                        string text = FormatComValue(arr[r, c]);
                        if (string.IsNullOrEmpty(text)) continue;
                        if (rowMap == null) {
                            if (!grid.TryGetValue(excelRow, out rowMap)) {
                                rowMap = new Dictionary<int, string>();
                                grid[excelRow] = rowMap;
                            }
                        }
                        int excelCol0 = (firstCol - 1) + (c - cLow);
                        rowMap[excelCol0] = text;
                    }
                }
                return;
            }
            string single = FormatComValue(rawVal);
            if (!string.IsNullOrEmpty(single)) {
                grid[firstRow] = new Dictionary<int, string> { { firstCol - 1, single } };
            }
        }

        static string FormatComValue(object val) {
            if (val == null || val is DBNull) return "";
            var s = val as string;
            if (s != null) return s;
            if (val is double) {
                double d = (double)val;
                if (double.IsNaN(d) || double.IsInfinity(d)) return "";
                if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15) {
                    return Math.Round(d).ToString("0", CultureInfo.InvariantCulture);
                }
                return d.ToString("G15", CultureInfo.InvariantCulture);
            }
            if (val is int) {
                int iv = (int)val;
                // Excel 公式错误值（如 #N/A = -2146826246）以 0x800Axxxx 编码为 Int32，过滤为空避免污染编号/型号
                if ((iv & unchecked((int)0xFFFF0000)) == unchecked((int)0x800A0000)) return "";
                return iv.ToString(CultureInfo.InvariantCulture);
            }
            if (val is bool) return ((bool)val) ? "TRUE" : "FALSE";
            return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "";
        }

        static int SafeCount(object collection) {
            if (collection == null) return 0;
            try { return Convert.ToInt32(GetProp(collection, "Count")); } catch { return 0; }
        }

        static object GetProp(object target, string name) {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        static object GetIndexed(object target, string name, params object[] args) {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);
        }

        static void SetProp(object target, string name, object value) {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }

        static void TrySetProp(object target, string name, object value) {
            try { SetProp(target, name, value); } catch { }
        }

        static object Call(object target, string name, params object[] args) {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }

        static COMException UnwrapComException(Exception ex) {
            while (ex != null) {
                var c = ex as COMException;
                if (c != null) return c;
                ex = ex.InnerException;
            }
            return null;
        }

        static string CleanError(Exception ex) {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            if (ex == null) return "未知错误";
            string msg = (ex.Message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 120) msg = msg.Substring(0, 120) + "…";
            return msg;
        }

        static void Release(object comObj) {
            if (comObj == null) return;
            try {
                if (Marshal.IsComObject(comObj)) Marshal.FinalReleaseComObject(comObj);
            } catch { }
        }

        static void ReleaseComGarbage() {
            try {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            } catch { }
        }
    }
}
