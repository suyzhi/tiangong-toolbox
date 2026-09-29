using System;
using System.Drawing;
namespace TianGongCadSuite {
    // 纯逻辑回归：工具窗口贴边停靠 / 收起 / 展开 / 记住位置的几何（不需要 Windows，tests/pure 里跑）。
    public static class ToolWindowPlacementPureTests {
        static void Check(bool ok, string label){ if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        public static void Run(){
            var work = new Rectangle(0, 0, 1920, 1040);          // 1080p，任务栏 40px
            var cad = new Rectangle(-8, -8, 1936, 1056);          // 最大化的 CAD 主窗口，四边比屏幕多出 8px 边框

            // ---- 贴边停靠 ----
            var d = ToolWindowPlacement.Dock(cad, work, new Size(916, 919), new Size(796, 639), true, 150, 8);
            Check(d.Right == 1912, "靠右：离工作区右边 8px，实得 " + d);
            Check(d.Top == 150, "顶部让开功能区（150px），实得 " + d);
            Check(d.Width == 916, "宽度保持不变");
            Check(d.Height == 882 && d.Bottom == 1032, "太高就压到功能区以下的可用高度，底边不出界，实得 " + d);
            var dl = ToolWindowPlacement.Dock(cad, work, new Size(916, 919), new Size(796, 639), false, 150, 8);
            Check(dl.Left == 8 && dl.Top == 150, "靠左：离左边 8px，实得 " + dl);
            var tall = ToolWindowPlacement.Dock(cad, work, new Size(600, 1000), new Size(560, 950), true, 150, 8);
            Check(tall.Height == 950 && tall.Bottom == 1032 && tall.Top == 82, "最小高度比功能区以下还高：整体上移、不低于最小高度，实得 " + tall);
            var none = ToolWindowPlacement.Dock(Rectangle.Empty, work, new Size(600, 700), new Size(560, 560), true, 0, 8);
            Check(none.Right == 1912 && none.Top == 0, "读不到 CAD 窗口：退回整个工作区贴右，实得 " + none);
            var leftScreen = new Rectangle(-1920, 0, 1920, 1040);
            var ds = ToolWindowPlacement.Dock(leftScreen, leftScreen, new Size(600, 700), new Size(560, 560), false, 150, 8);
            Check(ds.Left == -1912 && ds.Top == 150, "CAD 在左边的副屏（负坐标）也能贴边，实得 " + ds);
            var small = ToolWindowPlacement.Dock(cad, work, new Size(640, 400), new Size(560, 300), true, 150, 8);
            Check(small.Size == new Size(640, 400) && small.Top == 150, "放得下的窗口大小不动，只挪位置，实得 " + small);

            // ---- 收起 ----
            var exp = new Rectangle(996, 150, 916, 882);
            Check(ToolWindowPlacement.AnchorRight(exp, work), "窗口在右半边：收起时贴右边");
            Check(!ToolWindowPlacement.AnchorRight(new Rectangle(8, 150, 916, 882), work), "窗口在左半边：收起时贴左边");
            var strip = ToolWindowPlacement.Collapse(exp, new Size(460, 63), true);
            Check(strip == new Rectangle(1452, 150, 460, 63), "收起：右上角不动，变成 460×63 的条，实得 " + strip);
            var stripL = ToolWindowPlacement.Collapse(new Rectangle(8, 150, 916, 882), new Size(460, 63), false);
            Check(stripL.Left == 8 && stripL.Top == 150, "贴左收起：左上角不动");
            var narrow = ToolWindowPlacement.Collapse(new Rectangle(8, 150, 300, 400), new Size(460, 63), false);
            Check(narrow.Width == 300, "条不比原窗口宽");

            // ---- 展开 ----
            Check(ToolWindowPlacement.Expand(strip, exp.Size, true, work) == exp, "原地收起再展开：回到原来的位置和大小");
            var moved = new Rectangle(strip.X - 100, strip.Y + 40, strip.Width, strip.Height);   // 用户把条往左下拖了
            var back = ToolWindowPlacement.Expand(moved, exp.Size, true, work);
            Check(back.Size == exp.Size && back.Right == moved.Right, "条被拖走后展开：以条的新位置为准、大小不变，实得 " + back);
            Check(back.Bottom <= work.Bottom, "展开后底边不出屏幕（往上挪），实得 " + back);

            // ---- 塞回工作区 ----
            Check(ToolWindowPlacement.Fit(new Rectangle(1800, 900, 400, 300), work) == new Rectangle(1520, 740, 400, 300), "出了右下角：推回来");
            Check(ToolWindowPlacement.Fit(new Rectangle(100, 100, 2000, 1200), work).Location == Point.Empty, "比工作区还大：左上角对齐（标题栏抓得到）");

            // ---- 记住的位置还能不能用 ----
            var areas = new Rectangle[]{ work };
            Check(ToolWindowPlacement.Reachable(exp, areas), "屏幕里的位置可用");
            Check(!ToolWindowPlacement.Reachable(new Rectangle(2500, 100, 800, 600), areas), "副屏拔掉后，那块屏幕上的位置不可用");
            Check(!ToolWindowPlacement.Reachable(new Rectangle(-790, 100, 800, 600), areas), "只露出 10px 标题栏：不可用");
            Check(ToolWindowPlacement.Reachable(new Rectangle(-700, 100, 800, 600), areas), "露出 100px 标题栏：可用（用户自己拖过去的）");
            Check(!ToolWindowPlacement.Reachable(new Rectangle(100, 1035, 800, 600), areas), "标题栏在任务栏后面：不可用");

            // ---- 存取格式 ----
            string s = ToolWindowPlacement.Format(new Rectangle(-1912, 150, 916, 882));
            Check(s == "-1912,150,916,882", "位置存成 x,y,w,h：" + s);
            Rectangle r;
            Check(ToolWindowPlacement.TryParse(s, out r) && r == new Rectangle(-1912, 150, 916, 882), "读回来一样");
            Check(!ToolWindowPlacement.TryParse("1,2,3", out r), "少一项：读不出");
            Check(!ToolWindowPlacement.TryParse("a,b,c,d", out r), "不是数字：读不出");
            Check(!ToolWindowPlacement.TryParse("1,2,0,5", out r), "宽为 0：读不出");
            Check(!ToolWindowPlacement.TryParse(null, out r), "没存过：读不出");
            Console.WriteLine("TOOL WINDOW PLACEMENT ASSERTIONS OK");
        }
    }
}
