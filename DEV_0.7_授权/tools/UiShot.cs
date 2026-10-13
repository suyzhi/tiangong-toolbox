// tools/UiShot.cs —— 把"自动打孔"窗口真实渲染成 PNG，用来验收排版。
//
// 不需要 CAD：窗口的排版、参数联动、两个孔形状视图都只依赖控件本身，
// 只有"点孔/点面/开打"才会走到 assembly 那条路（构造函数传 null 就不会碰）。
// 参考孔列表用 AddReferenceForTest 填假数据（规格照常自动反推）。
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
                form.Location = new Point(40, 20);
                form.Show();
                form.Refresh();
                Pump(900);

                var all = new List<Control>();
                Collect(form, all);
                Console.WriteLine("控件总数 " + all.Count);

                Shot(form, Path.Combine(outDir, "1-默认-点孔前.png"));

                // 有参考孔：Φ6.6 ×4（M6 过孔 → 配螺纹孔）、Φ5 ×2（M6 底孔 → 配沉孔）
                form.AddReferenceForTest(6.6, 4);
                form.AddReferenceForTest(5.0, 2);
                Pump(300);
                Kind(all, "螺纹孔");                    // 选中第二组时是沉孔；切回第一组看螺纹孔
                Pump(200);
                Shot(form, Path.Combine(outDir, "2-螺纹孔M6-全长.png"));

                Radio(all, "指定深度");
                SetNum(all, 0.5M, 2000M, 12M, 10M);
                Pump(200);
                Shot(form, Path.Combine(outDir, "3-螺纹孔M6-指定深度10.png"));

                Kind(all, "圆柱沉孔");
                Pump(200);
                Shot(form, Path.Combine(outDir, "4-圆柱沉孔.png"));

                Kind(all, "锥形沉孔");
                Pump(200);
                Shot(form, Path.Combine(outDir, "5-锥形沉孔.png"));

                Kind(all, "通孔");
                Radio(all, "盲孔");
                SetCombo(all, "平底", "V 型底");
                Check(all, "孔口倒角");
                Pump(200);
                Shot(form, Path.Combine(outDir, "6-盲孔V底倒角.png"));

                // 盲孔螺纹孔，螺纹深度超过孔深 -> 视图拒画 + 预览给原因 + 开始打孔变灰
                SetCombo(all, "平底", "平底");
                Check(all, "孔口倒角");
                Kind(all, "螺纹孔");
                Radio(all, "指定深度");
                SetNumAny(all, 0.5M, 2000M, 30M);
                Pump(200);
                Shot(form, Path.Combine(outDir, "7-螺纹深超孔深-拒画.png"));

                // 窄窗口：看两列布局在最小尺寸下会不会挤坏
                form.ClientSize = new Size(900, 700);
                Pump(400);
                Shot(form, Path.Combine(outDir, "8-最小尺寸.png"));

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
    static void Kind(List<Control> all, string pick){
        foreach (var c in all) {
            var s = c as Segmented;
            if (s == null) continue;
            int i = s.Items.IndexOf(pick);
            if (i >= 0) { s.SelectedIndex = i; return; }
        }
        Console.WriteLine("找不到孔型：" + pick);
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
    // 按范围找（不管当前值）——螺纹深度框只有它是 0.5～2000
    static void SetNumAny(List<Control> all, decimal min, decimal max, decimal to){
        foreach (var c in all) {
            var n = c as NumericUpDown;
            if (n == null || n.Minimum != min || n.Maximum != max) continue;
            n.Value = to; return;
        }
        Console.WriteLine("找不到数值框 " + min + "~" + max);
    }
    static void Check(List<Control> all, string text){
        foreach (var c in all) {
            var cb = c as CheckBox;
            if (cb != null && cb.Text == text) { cb.Checked = !cb.Checked; return; }
        }
        Console.WriteLine("找不到勾选框：" + text);
    }
    static void Radio(List<Control> all, string text){
        foreach (var c in all) {
            var rb = c as RadioButton;
            if (rb != null && rb.Text == text) { rb.Checked = true; return; }
        }
        Console.WriteLine("找不到单选：" + text);
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
