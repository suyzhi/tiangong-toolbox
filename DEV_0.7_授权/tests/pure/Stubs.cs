namespace TianGongCadSuite {
    // 真正的 ConverterHost 在 src/FormatConvertModule.cs（依赖 WinForms，这里不编译）。
    // 只为让 FormatConvertTests.cs 能编译；用到它的断言不在 tests/pure 里运行（见 Program.cs）。
    public static class ConverterHost {
        public const string ExeName = "TianGongConverter.exe";
        public static string Resolve(){ return null; }
    }
}
