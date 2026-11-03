using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using TianGongCadSuite;

// 「一键出图」窗口走一遍：CAD 里打开模型 → new DrawingForm → LoadActive（列清单）→ 截图 → 点「出图」（反射调 Run）→ 截图 + 读每行结果。
// 输出目录要求：模型是副本（出的图存在模型旁边）。用法：FormE2E.exe <输出目录> <模型1> [模型2 ...]
class FormE2E {
    [STAThread] static int Main(string[] args){
        Console.OutputEncoding = Encoding.UTF8;
        Application.EnableVisualStyles();
        // 进程外调用：CAD 刚打开文档时会拒绝调用（RPC_E_CALL_REJECTED），插件自己的消息过滤器负责重试（插件在 CAD 进程里时没这回事）
        var filter = new OleFilter();
        var app = (F.Application)Marshal.GetActiveObject("SolidEdge.Application");
        try { app.DisplayAlerts = false; } catch { }
        for (int i = 1; i < args.Length; i++) {
            string model = args[i];
            Console.WriteLine("== " + Path.GetFileName(model));
            object doc = null;
            try { doc = app.Documents.Open(model); for (int k = 0; k < 20; k++) { try { app.DoIdle(); } catch { } Thread.Sleep(50); } }
            catch (Exception e) { Console.WriteLine("FAIL 打不开 " + e.Message); continue; }
            var form = new DrawingForm(app);
            form.Show(); Application.DoEvents();
            form.LoadActive(); Application.DoEvents();
            var list = Find<ListView>(form);
            Console.WriteLine("  清单 " + list.Items.Count + " 行，勾选 " + list.Items.Cast<ListViewItem>().Count(x => x.Checked));
            foreach (ListViewItem it in list.Items.Cast<ListViewItem>().Take(12)) Console.WriteLine("   [" + (it.Checked ? "x" : " ") + "] " + string.Join(" | ", it.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)));
            Shot(form, Path.Combine(args[0], Path.GetFileNameWithoutExtension(model) + "-before.png"));
            // 装配只出前 3 张（装配图 + 2 个零件），省时间
            int n = 0; foreach (ListViewItem it in list.Items) { if (it.Checked && ++n > 3) it.Checked = false; }
            var run = typeof(DrawingForm).GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Instance);
            var t0 = DateTime.Now;
            run.Invoke(form, null);
            Console.WriteLine("  出图用时 " + (DateTime.Now - t0).TotalSeconds.ToString("0.0") + "s");
            foreach (ListViewItem it in list.Items) if (it.SubItems[3].Text.Length > 0 && !it.SubItems[3].Text.StartsWith("（")) Console.WriteLine("   结果 " + it.SubItems[0].Text + " → " + it.SubItems[3].Text + " ｜ " + it.SubItems[4].Text);
            Shot(form, Path.Combine(args[0], Path.GetFileNameWithoutExtension(model) + "-after.png"));
            form.Close(); form.Dispose();
            // 关掉出图时留着的工程图（单张时会留着）和模型
            try { for (int k = app.Documents.Count; k >= 1; k--) { try { ((dynamic)app.Documents.Item(k)).Close(false); } catch { } } } catch { }
        }
        Console.WriteLine("RESULT form e2e done");
        return 0;
    }
    static T Find<T>(Control c) where T : Control { if (c is T) return (T)c; foreach (Control k in c.Controls) { var r = Find<T>(k); if (r != null) return r; } return null; }
    static void Shot(Form f, string path){ try { using (var b = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(path); } } catch (Exception e) { Console.WriteLine("  截图失败 " + e.Message); } }
}
