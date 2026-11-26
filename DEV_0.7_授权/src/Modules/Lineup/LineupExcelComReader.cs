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

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        static HashSet<int> SnapshotProcessIds(string processName) {
            var ids = new HashSet<int>();
            if (string.IsNullOrEmpty(processName)) return ids;
            Process[] procs = null;
            try {
                procs = Process.GetProcessesByName(processName);
                foreach (var p in procs) {
                    try { ids.Add(p.Id); } catch { }
                }
            } catch {
            } finally {
                if (procs != null) {
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
                }
            }
            return ids;
        }

        // 通过 app.Hwnd 精确反查该 COM 实例所属进程 PID，绝不靠进程快照差集误杀用户并发启动的其他 Excel
        static int TryGetAppProcessId(object app) {
            if (app == null) return 0;
            try {
                object hw = GetProp(app, "Hwnd");
                if (hw == null) return 0;
                IntPtr hwnd = new IntPtr(Convert.ToInt64(hw));
                if (hwnd == IntPtr.Zero) return 0;
                int pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid > 0 ? pid : 0;
            } catch {
                return 0;
            }
        }

        static void SafeKillProcess(int pid) {
            if (pid <= 0) return;
            try {
                using (var proc = Process.GetProcessById(pid)) {
                    if (!proc.HasExited) proc.Kill();
                }
            } catch { }
        }

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
                    SafeKillProcess(pid);
                    worker.Join(2000);
                }
                throw new TimeoutException("打开工作簿超时（可能弹出密码框、激活提示或加密拦截）");
            }
            if (workerError != null) throw workerError;
            return snap;
        }

        static ComWorkbookSnapshot ExtractSnapshotIsolated(string fullPath, ComEngine engine, Type comType, int[] ownedSpawnedPid) {
            var beforePids = SnapshotProcessIds(engine.ProcessName);

            object app = null;
            object workbooks = null;
            object wb = null;
            object sheets = null;
            bool ownServer = false;
            bool spawnedNewProc = false;
            int exactPid = 0;
            bool openedByUs = false;
            object prevAlerts = null, prevEvents = null, prevAutoSec = null, prevAskLinks = null;
            bool alertsChanged = false, eventsChanged = false, autoSecChanged = false, askLinksChanged = false;

            try {
                app = Activator.CreateInstance(comType);
                exactPid = TryGetAppProcessId(app);
                if (exactPid > 0) {
                    spawnedNewProc = !beforePids.Contains(exactPid);
                } else {
                    // 极少数无 Hwnd 的兼容引擎才降级看进程集合是否仅新增 1 个实例（避免多实例歧义）
                    var afterPids = SnapshotProcessIds(engine.ProcessName);
                    afterPids.ExceptWith(beforePids);
                    if (afterPids.Count == 1) {
                        exactPid = afterPids.First();
                        spawnedNewProc = true;
                    }
                }

                workbooks = GetProp(app, "Workbooks");
                int initialWbCount = SafeCount(workbooks);

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

                // 只要归我们接管且拿到精确 PID（含预启动的不可见自动化进程），立即向看门狗登记，防止弹窗卡死时泄漏进程
                if (ownServer && exactPid > 0 && ownedSpawnedPid != null) {
                    Thread.VolatileWrite(ref ownedSpawnedPid[0], exactPid);
                }

                if (ownServer) {
                    TrySetProp(app, "Visible", false);
                    TrySetProp(app, "ScreenUpdating", false);
                }
                // 无论是否复用用户进程，打开外部工作簿前都暂存并禁用宏/事件/链接提示/弹窗，避免 .xlsm Workbook_Open 宏执行或弹窗阻塞
                try { prevAutoSec = GetProp(app, "AutomationSecurity"); SetProp(app, "AutomationSecurity", MsoAutomationSecurityForceDisable); autoSecChanged = true; } catch { }
                try { prevEvents = GetProp(app, "EnableEvents"); SetProp(app, "EnableEvents", false); eventsChanged = true; } catch { }
                try { prevAskLinks = GetProp(app, "AskToUpdateLinks"); SetProp(app, "AskToUpdateLinks", false); askLinksChanged = true; } catch { }
                try { prevAlerts = GetProp(app, "DisplayAlerts"); SetProp(app, "DisplayAlerts", false); alertsChanged = true; } catch { }

                // 如果复用的进程里用户本来就开着同一个工作簿，直接只读提取且绝不帮用户关闭
                if (initialWbCount > 0) {
                    wb = FindAlreadyOpenWorkbook(workbooks, initialWbCount, fullPath);
                }
                if (wb == null) {
                    wb = OpenReadOnlyWithRetry(workbooks, fullPath);
                    openedByUs = true;
                }
                // 复用用户窗口时，Open 完毕立刻还原全局开关，把影响窗口压缩到最小（finally 中仍兜底还原）
                if (!ownServer) {
                    RestoreAppFlags(app, ref alertsChanged, prevAlerts, ref eventsChanged, prevEvents, ref autoSecChanged, prevAutoSec, ref askLinksChanged, prevAskLinks);
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
                bool closeSucceeded = true;
                if (wb != null) {
                    if (openedByUs) {
                        closeSucceeded = CloseWorkbookWithRetry(wb);
                    }
                    Release(wb);
                }
                if (app != null && !ownServer) {
                    RestoreAppFlags(app, ref alertsChanged, prevAlerts, ref eventsChanged, prevEvents, ref autoSecChanged, prevAutoSec, ref askLinksChanged, prevAskLinks);
                }
                int remainingWbs = workbooks != null ? SafeCount(workbooks) : 0;
                Release(workbooks);
                // 若实例归插件接管，即使 wb.Close 因 RPC 繁忙失败导致 remainingWbs 仍为 1，也必须调用 Quit 防止隐藏 EXCEL.EXE 永久泄漏锁文件
                if (app != null && ownServer && (remainingWbs == 0 || !closeSucceeded)) {
                    bool quitOk = false;
                    try { Call(app, "Quit"); quitOk = true; } catch { }
                    Release(app);
                    app = null;
                    if (!quitOk && spawnedNewProc && exactPid > 0) {
                        SafeKillProcess(exactPid);
                    }
                } else {
                    Release(app);
                }
            }
        }

        static void RestoreAppFlags(
            object app,
            ref bool alertsChanged, object prevAlerts,
            ref bool eventsChanged, object prevEvents,
            ref bool autoSecChanged, object prevAutoSec,
            ref bool askLinksChanged, object prevAskLinks) {
            if (app == null) return;
            if (alertsChanged) { if (prevAlerts != null) TrySetProp(app, "DisplayAlerts", prevAlerts); alertsChanged = false; }
            if (eventsChanged) { if (prevEvents != null) TrySetProp(app, "EnableEvents", prevEvents); eventsChanged = false; }
            if (autoSecChanged) { if (prevAutoSec != null) TrySetProp(app, "AutomationSecurity", prevAutoSec); autoSecChanged = false; }
            if (askLinksChanged) { if (prevAskLinks != null) TrySetProp(app, "AskToUpdateLinks", prevAskLinks); askLinksChanged = false; }
        }

        static bool CloseWorkbookWithRetry(object wb) {
            for (int attempt = 0; attempt < 3; attempt++) {
                try {
                    Call(wb, "Close", false);
                    return true;
                } catch (Exception ex) {
                    var comEx = UnwrapComException(ex);
                    if (comEx != null && (comEx.ErrorCode == RpcECallRejected || comEx.ErrorCode == RpcEServerCallRetryLater) && attempt < 2) {
                        Thread.Sleep(150);
                        continue;
                    }
                    return false;
                }
            }
            return false;
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

                // 先在内存中定位表头行与动态列映射，供合并单元格和删除线探测精确过滤目标列（0 次 COM 调用）
                int headerRow = -1;
                int[] colMapping = null;
                foreach (var kv in grid) {
                    var cand = LineupXlsxReader.TryMapBomHeaders(kv.Value);
                    if (cand != null) { headerRow = kv.Key; colMapping = cand; break; }
                }

                // 仅对可能存在纵向合并的列、且仅对“下一行该列为空”的候选格查询 MergeArea，避开全宽分区标题引发的数千次 COM 往返
                ReadVerticalMerges(ws, firstRow, maxRow, firstCol, maxCol, headerRow, colMapping, grid, merges);

                // 对真实映射出的编号/描述列使用递归二分区间探测删除线（O(K log N) COM 调用，避免中大型表超时）
                ReadStrikethroughRows(ws, firstRow, maxRow, headerRow, colMapping, grid, struckRows);
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
            int headerRow,
            int[] colMapping,
            SortedDictionary<int, Dictionary<int, string>> grid,
            HashSet<int> struckRows) {
            if (struckRows == null || grid == null || grid.Count == 0) return;

            var targetCols0 = new List<int>();
            if (colMapping != null) {
                if (colMapping[0] >= 0 && colMapping[0] < MaxBomCols) targetCols0.Add(colMapping[0]);
                if (colMapping[3] >= 0 && colMapping[3] < MaxBomCols && !targetCols0.Contains(colMapping[3])) targetCols0.Add(colMapping[3]);
            }
            if (targetCols0.Count == 0) { targetCols0.Add(0); targetCols0.Add(3); }

            foreach (int col0 in targetCols0) {
                string colAddr = ColLetters(col0 + 1);
                var nonEmptyRows = new List<int>();
                foreach (var rowKv in grid) {
                    if (rowKv.Key <= headerRow || struckRows.Contains(rowKv.Key)) continue;
                    string val;
                    if (!rowKv.Value.TryGetValue(col0, out val) || string.IsNullOrWhiteSpace(val)) continue;
                    if (LineupXlsxReader.IsSectionTitle(val)) continue;
                    nonEmptyRows.Add(rowKv.Key);
                }
                if (nonEmptyRows.Count == 0) continue;
                ProbeStrikethroughRange(ws, colAddr, nonEmptyRows, 0, nonEmptyRows.Count - 1, struckRows);
            }
        }

        // 递归二分探测区间 [lo..hi] 的删除线状态：整段 false 瞬时跳过，整段 true 批量标记，仅混合态继续二分
        static void ProbeStrikethroughRange(
            object ws,
            string colAddr,
            List<int> rows,
            int lo,
            int hi,
            HashSet<int> struckRows) {
            if (lo > hi) return;
            int rStart = rows[lo];
            int rEnd = rows[hi];
            string addr = rStart == rEnd ? (colAddr + rStart) : (colAddr + rStart + ":" + colAddr + rEnd);
            object range = null;
            object font = null;
            object st = null;
            bool ok = false;
            try {
                range = GetIndexed(ws, "Range", addr);
                font = GetProp(range, "Font");
                st = GetProp(font, "Strikethrough");
                ok = true;
            } catch {
                ok = false;
            } finally {
                Release(font);
                Release(range);
            }
            if (!ok) return;
            if (st is bool) {
                if ((bool)st) {
                    for (int i = lo; i <= hi; i++) struckRows.Add(rows[i]);
                }
                return;
            }
            if (lo == hi) return; // 单格内混合态（局部文字删除线）不按整行取消处理
            int mid = lo + (hi - lo) / 2;
            ProbeStrikethroughRange(ws, colAddr, rows, lo, mid, struckRows);
            ProbeStrikethroughRange(ws, colAddr, rows, mid + 1, hi, struckRows);
        }

        static void ReadVerticalMerges(
            object ws,
            int firstRow,
            int maxRow,
            int firstCol,
            int maxCol,
            int headerRow,
            int[] colMapping,
            SortedDictionary<int, Dictionary<int, string>> grid,
            List<LineupCellMerge> merges) {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // 仅探测可能纵向合并的列（安装方式/机构名称/传感器接口类型/备注），绕开被全宽分区横条污染的 A..G 列
            var candidateCols0 = new List<int>();
            if (colMapping != null) {
                foreach (int fi in new[] { 7, 8, 9, 10 }) {
                    int c0 = colMapping[fi];
                    if (c0 >= firstCol - 1 && c0 <= maxCol - 1 && !candidateCols0.Contains(c0)) candidateCols0.Add(c0);
                }
            }
            if (candidateCols0.Count == 0) {
                for (int col1 = firstCol; col1 <= maxCol; col1++) candidateCols0.Add(col1 - 1);
            }

            foreach (int col0 in candidateCols0) {
                int col1 = col0 + 1;
                string colAddr = ColLetters(col1);
                // 先在内存中筛选「本行非空，且紧邻下一行存在数据但本列为空」的候选格：
                // 纵向合并区 (R1..R2, R2>R1) 的 R1+1 行在本列必然为空，无候选格直接 0 次 COM 调用跳过！
                var candidateRows = new List<int>();
                foreach (var rowKv in grid) {
                    int r = rowKv.Key;
                    if (r <= headerRow || r >= maxRow) continue;
                    string val;
                    if (!rowKv.Value.TryGetValue(col0, out val) || string.IsNullOrWhiteSpace(val)) continue;
                    if (LineupXlsxReader.IsSectionTitle(val)) continue;
                    Dictionary<int, string> nextRow;
                    if (!grid.TryGetValue(r + 1, out nextRow)) continue;
                    string nextVal;
                    if (nextRow.TryGetValue(col0, out nextVal) && !string.IsNullOrWhiteSpace(nextVal)) continue;
                    candidateRows.Add(r);
                }
                if (candidateRows.Count == 0) continue;

                // 粗筛：整列完全不含任何合并格时跳过
                bool mayHaveMerges = true;
                object colRange = null;
                try {
                    colRange = GetIndexed(ws, "Range", colAddr + candidateRows[0] + ":" + colAddr + maxRow);
                    object state = GetProp(colRange, "MergeCells");
                    if (state is bool && !(bool)state) mayHaveMerges = false;
                } catch {
                    mayHaveMerges = true;
                } finally {
                    Release(colRange);
                }
                if (!mayHaveMerges) continue;

                foreach (int r in candidateRows) {
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
                        // 只收集单列纵向合并（安装方式阀岛、机构名称、备注模块名等），跳过横向标题合并
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
            if (val is DateTime) {
                var dt = (DateTime)val;
                // 部分 WPS 引擎在 Value2 上仍直接返回 DateTime；统一格式化为 yyyy/M/d 防止落入 InvariantCulture 美式月/日格式
                return dt.TimeOfDay == TimeSpan.Zero
                    ? dt.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture);
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
