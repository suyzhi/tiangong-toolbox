using System;

// 纯逻辑测试入口（不需要天工 CAD）。见 PureTests.csproj。
// FormatConvertTests.Pure() 整套不在这里跑：其中"worker 解析到插件旁的 TianGongConverter.exe"
// 一项依赖 Windows 上 build.ps1 的编译产物，只能由 PanelTests.exe 验证。
static class PureProgram {
    static int Main(){
        int failed = 0;
        failed += Run("AutoHole", TianGongCadSuite.AutoHoleTests.Pure);
        failed += Run("FormatConvert.ResumeMarker", FormatConvertTests.ResumeMarker);
        failed += Run("FormatConvert.CommandLineQuoting", FormatConvertTests.CommandLineQuoting);
        failed += Run("PanelNaming", TianGongCadSuite.PanelNamingPureTests.Run);
        failed += Run("ToolWindowPlacement", TianGongCadSuite.ToolWindowPlacementPureTests.Run);
        Console.WriteLine(failed == 0 ? "ALL PURE SUITES PASSED" : failed + " SUITE(S) FAILED");
        return failed == 0 ? 0 : 1;
    }
    static int Run(string name, Action suite){
        try { suite(); Console.WriteLine("== " + name + " OK"); return 0; }
        catch (Exception e) { Console.WriteLine("== " + name + " FAILED: " + e.Message); return 1; }
    }
}
