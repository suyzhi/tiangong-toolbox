// tools/UiShot.cs —— 把"自动打孔"窗口真实渲染成 PNG，用来验收排版。
//
// 不需要 CAD：窗口的排版、参数联动、两个孔形状视图都只依赖控件本身，
// 只有"点孔/点面/开打"才会走到 assembly 那条路（构造函数传 null 就不会碰）。
//
// 用法：UiShot.exe <输出目录>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TianGongCadSuite;

static class UiShot {
    [STAThread]
    static int Main(string[] args){
        string outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try {
            using (var form = new AutoHoleForm(null, null)) {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(60, 30);
                form.Show();
                form.Refresh();
                Pump(900);

                var all = new List<Control>();
                Collect(form, all);
                Console.WriteLine("控件总数 " + all.Count);
                Dump(all, "初始");

                Dump(all, "1 默认"); Shot(form, Path.Combine(outDir, "1-默认-点孔前.png"));

                // 圆柱沉孔（自动反推的典型结果）
                SetCombo(all, "通孔", "圆柱沉孔");
                Pump(200);
                Shot(form, Path.Combine(outDir, "2-圆柱沉孔.png"));

                // 锥形沉孔
                SetCombo(all, "通孔", "锥形沉孔");
                Pump(200);
                Shot(form, Path.Combine(outDir, "3-锥形沉孔.png"));

                // 螺纹孔 + 规格 M6
                SetCombo(all, "通孔", "螺纹孔");
                SetCombo(all, "自定义", "M6");
                Pump(200);
                Shot(form, Path.Combine(outDir, "4-螺纹孔M6.png"));

                // 盲孔 V 型底 + 孔口倒角
                SetCombo(all, "通孔", "通孔");
                SetCombo(all, "自定义", "自定义");
                Click(all, "贯通");                 // 取消贯通 —— 这一下会联动把"盲孔"勾上
                SetCombo(all, "平底", "V 型底");
                Click(all, "孔口倒角");
                Pump(200);
                Dump(all, "5 盲孔V底倒角"); Shot(form, Path.Combine(outDir, "5-盲孔V底倒角.png"));

                // 盲孔平底 + 无倒角
                SetCombo(all, "平底", "平底");      // 下拉框按第一项定位，选回第 0 项 = 平底
                Click(all, "孔口倒角");
                Pump(200);
                Dump(all, "6 盲孔平底"); Shot(form, Path.Combine(outDir, "6-盲孔平底.png"));

                // 很小的孔：看缩放会不会把标注挤爆
                SetNum(all, 0.1M, 500M, 6M, 2.5M);
                Pump(200);
                Shot(form, Path.Combine(outDir, "6b-小孔Φ2.5.png"));

                // 不成立的规格：沉孔 Φ11 配 Φ20 的孔 -> 两个视图必须拒画并说明原因
                SetCombo(all, "通孔", "圆柱沉孔");
                SetNum(all, 0.1M, 500M, 6.6M, 20M);
                Pump(200);
                Dump(all, "8 非法沉孔");
                Shot(form, Path.Combine(outDir, "8-非法沉孔-拒画.png"));
                SetCombo(all, "圆柱沉孔", "通孔");

                // 窄窗口：看两列布局在最小宽度下会不会挤坏
                form.ClientSize = new Size(800, 720);
                Pump(400);
                Dump(all, "7 最小尺寸"); Shot(form, Path.Combine(outDir, "7-最小尺寸.png"));

                form.Close();
            }
            Console.WriteLine("UISHOT DONE " + outDir);
            return 0;
        } catch (Exception e) {
            Console.WriteLine("UISHOT FATAL " + e);
            return 1;
        }
    }

    static void Collect(Control c, List<Control> all){
        foreach (Control k in c.Controls) { all.Add(k); Collect(k, all); }
    }
    static void Pump(int ms){
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        Application.DoEvents();
    }
    static void SetCombo(List<Control> all, string firstItem, string pick){
        foreach (var c in all) {
            var cb = c as ComboBox;
            if (cb == null || cb.Items.Count == 0 || (string)cb.Items[0] != firstItem) continue;
            for (int i = 0; i < cb.Items.Count; i++) if ((string)cb.Items[i] == pick) { cb.SelectedIndex = i; return; }
        }
        Console.WriteLine("找不到下拉框：" + firstItem + " -> " + pick);
    }
    static void SetNum(List<Control> all, decimal min, decimal max, decimal from, decimal to){
        foreach (var c in all) {
            var n = c as NumericUpDown;
            if (n == null || n.Minimum != min || n.Maximum != max || n.Value != from) continue;
            n.Value = to; return;
        }
        Console.WriteLine("找不到数值框 " + from);
    }
    static void Click(List<Control> all, string text){
        foreach (var c in all) {
            var cb = c as CheckBox;
            if (cb != null && cb.Text == text) { cb.Checked = !cb.Checked; return; }
        }
        Console.WriteLine("找不到勾选框：" + text);
    }

    // 关键控件的可用状态：光看截图分不清"灰"是禁用还是没焦点，直接把 Enabled 打出来。
    // 一律用 ASCII 输出 —— 控制台是 GBK，中文全是乱码，判断不了"可用/禁用"。
    static void Dump(List<Control> all, string tag){
        var sb = new System.Text.StringBuilder();
        sb.Append("[" + tag + "] ");
        foreach (var c in all) {
            var cb = c as CheckBox;
            if (cb != null && (cb.Text == "贯通" || cb.Text == "盲孔" || cb.Text == "孔口倒角"))
                sb.Append("chk" + (int)cb.Name.Length + cb.Text.Length + "=" + (cb.Checked ? "ON" : "off") + (cb.Enabled ? "" : "/DISABLED") + " ");
            var n = c as NumericUpDown;
            if (n != null && (n.Value == 10M || n.Value == 0.5M) && n.Maximum == (n.Value == 10M ? 2000M : 20M))
                sb.Append("num" + n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "=" + (n.Enabled ? "ENABLED" : "DISABLED") + " ");
            var bx = c as ComboBox;
            if (bx != null && bx.Items.Count > 0 && ((string)bx.Items[0] == "平底" || (string)bx.Items[0] == "通孔"))
                sb.Append("combo" + (bx.Items.Count == 2 ? "(bottom)" : "(kind)") + "=" + (bx.Enabled ? "ENABLED" : "DISABLED") + " ");
            var nd = c as NumericUpDown;
            if (nd != null && (nd.Value == 6M || nd.Value == 118M))
                sb.Append("num" + nd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "=" + (nd.Enabled ? "ENABLED" : "DISABLED") + " ");
        }
        Console.WriteLine(sb.ToString());
    }

    static void Shot(Form f, string path){
        f.Refresh();
        Application.DoEvents();
        var r = f.Bounds;
        using (var bmp = new Bitmap(r.Width, r.Height))
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            bool ok = PrintWindow(f.Handle, hdc, 2);
            g.ReleaseHdc(hdc);
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine((ok ? "OK   " : "FAIL ") + Path.GetFileName(path) + "  " + r.Width + "x" + r.Height);
        }
    }
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
}
