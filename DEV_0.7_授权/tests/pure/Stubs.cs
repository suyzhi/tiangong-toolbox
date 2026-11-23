namespace TianGongCadSuite {
    // 真正的 ConverterHost 在 src/Modules/FormatConvert/FormatConvertModule.cs（依赖 WinForms，这里不编译）。
    // 只为让 FormatConvertTests.cs 能编译；用到它的断言不在 tests/pure 里运行（见 Program.cs）。
    public static class ConverterHost {
        public const string ExeName = "TianGongConverter.exe";
        public static string Resolve(){ return null; }
    }

    // 真正的 LineupExcelComReader 依赖 Windows COM/注册表；纯逻辑测试不调用它，仅做编译占位。
    internal static class LineupExcelComReader {
        public static LineupImport Read(string path, bool allowDuplicates, bool isTsd) {
            throw new System.InvalidOperationException("tests/pure 环境不支持 Excel/WPS COM 兜底读取。");
        }
        internal static LineupImport ReadWithProgId(string path, string progId, bool allowDuplicates, bool isTsd) {
            throw new System.InvalidOperationException("tests/pure 环境不支持 Excel/WPS COM 兜底读取。");
        }
    }
}
