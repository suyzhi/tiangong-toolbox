using System;
using System.IO;
using TianGongCadSuite.Licensing;

static class LicenseActivateMain {
    // 开发/联调用：把激活码写进本机激活记录，等价于用户在激活窗口里点"激活"。
    //   LicenseActivate.exe <码文件路径 或 码正文>
    static int Main(string[] args) {
        if (args.Length < 1) { Console.Error.WriteLine("用法: LicenseActivate.exe <码文件|码正文>"); return 2; }
        string text = File.Exists(args[0]) ? File.ReadAllText(args[0]) : args[0];
        string message, notice;
        var report = LicenseLibrary.Activate(text, out message, out notice);
        Console.WriteLine("状态 : " + report.Status);
        Console.WriteLine("说明 : " + message);
        if (!string.IsNullOrEmpty(notice)) Console.WriteLine("提示 : " + notice);
        Console.WriteLine("本机机器码: " + LicenseLibrary.MachineCode());
        return report.Status == LicenseStatus.Valid || report.Status == LicenseStatus.ExpiringSoon ? 0 : 1;
    }
}
