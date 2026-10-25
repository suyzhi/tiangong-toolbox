using System;
using System.IO;
using System.Windows.Forms;

// 「立即更新」测试用的假天工：开一个窗口；收到关闭请求就退出（--refuse：拒绝关闭，模拟用户在保存提示里点了取消）。
// 每次启动在同目录 fake-starts.log 记一行，测试据此判断"装完以后有没有被重新打开"。
static class TianGongFake {
    [STAThread] static void Main(string[] args){
        bool refuse = args.Length > 0 && args[0] == "--refuse";
        File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fake-starts.log"),
            DateTime.Now.ToString("HH:mm:ss.fff") + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + (refuse ? " refuse" : "") + Environment.NewLine);
        var f = new Form { Text = "假天工（测试用）", Width = 320, Height = 120, ShowInTaskbar = true, WindowState = FormWindowState.Minimized };   // 正常的任务栏窗口（和真天工一样有主窗口），最小化不挡人
        if (refuse) f.FormClosing += (s, e) => { e.Cancel = true; };   // 一律拒绝（测试结束靠 Kill）
        Application.Run(f);
    }
}
