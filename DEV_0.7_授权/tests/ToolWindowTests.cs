using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 工具窗口"给模型让路"的界面回归（不需要 CAD）：贴边打开、收起成条、条上跟着状态走、展开复原、记住位置。
    // 在 PanelTests.exe --autohole-ui 里跑，窗口会在屏幕上闪一两秒。
    // 用独立的键存偏好（跑完删掉），不会冲掉用户自己记住的窗口位置。
    public static class ToolWindowTests {
        const string Key = "PanelTests-ToolWindow";
        static int checks;
        static void Check(bool ok, string label){ if (!ok) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        static void Pump(){ for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(15); } }

        // 模拟一个"要在模型上点选"的工具窗口：纵向堆叠 + 底部状态行 + 一个按钮，跟打孔窗口同一套排版。
        sealed class ProbeForm : Form, IPickingWindow {
            public readonly Label Status = new Label { Height = 26, Text = "就绪" };
            public readonly Button Run = Ui.Primary("开始", 120, 34);
            public int QuickCalls;
            public ProbeForm(){
                Ui.Shell(this, "让路测试", 700, 760, 560, 560);
                var stack = new VerticalStack { Dock = DockStyle.Fill };
                stack.Add(Ui.Card("内容", 0, ""), true);
                stack.Add(Run); stack.Add(Status);
                Controls.Add(stack);
            }
            Label IPickingWindow.StatusLabel { get { return Status; } }
            bool IPickingWindow.AutoCollapseByDefault { get { return true; } }
            string IPickingWindow.QuickActionText { get { return "读取选择"; } }
            void IPickingWindow.QuickAction(){ QuickCalls++; }
        }

        static Control FindText(Control root, string text){
            foreach (Control c in root.Controls) {
                if (c.Text == text) return c;
                var hit = FindText(c, text);
                if (hit != null) return hit;
            }
            return null;
        }

        public static void Run(){
            checks = 0;
            WindowLayoutStore.Forget(Key);
            int savedAnim = ToolWindow.AnimationMs;
            ToolWindow.AnimationMs = 0;   // 断言要的是终点状态；动画另外测
            try {
                using (var plain = new Form())
                    Check(ToolWindow.Attach(plain, 0, Key) == null && plain.Controls.Count == 0, "没实现 IPickingWindow 的窗口（格式转换、Lineup）不挂条，行为不变");

                Point remembered; Size rememberedSize;
                var work = Screen.PrimaryScreen.WorkingArea;
                using (var f = new ProbeForm()) {
                    var tw = ToolWindow.Attach(f, 0, Key);
                    Check(tw != null, "挂上了停靠/收起条");
                    Check(tw.AutoCollapse, "默认勾上「点模型时自动收起」（按窗口声明）");
                    Check(f.StartPosition == FormStartPosition.Manual, "不再 CenterParent");
                    Check(f.Right == work.Right - 8, "第一次打开贴右边，right=" + f.Right + " 工作区右=" + work.Right);
                    Check(f.Bottom <= work.Bottom, "底边不出工作区");
                    f.Show(); Pump();
                    Check(f.Run.Parent.Top == ToolWindow.BarHeight, "条占住顶部，原内容排在它下面（内容区 top=" + f.Run.Parent.Top + "）");
                    Check(FindText(f, "收起 ▲") != null && FindText(f, "◀ 靠左") != null && FindText(f, "靠右 ▶") != null, "条上有 靠左 / 靠右 / 收起");

                    var before = f.Bounds;
                    tw.Collapse(); Pump();
                    Check(tw.Collapsed && f.ClientSize.Height == ToolWindow.BarHeight, "收起后只剩一条，客户区高 " + f.ClientSize.Height);
                    Check(f.Width <= 460, "条宽不超过 460，实得 " + f.Width);
                    Check(f.Right == before.Right && f.Top == before.Top, "贴右收起：右上角不动");
                    Check(!f.Run.Visible, "收起后窗口内容藏起来");
                    Check(FindText(f, "读取选择") != null && FindText(f, "读取选择").Visible, "收起条上有窗口声明的快捷按钮");

                    f.Status.Text = "已选 3 个参考孔"; f.Status.ForeColor = Ui.Ok; Pump();
                    var mirror = FindText(f, "● 已选 3 个参考孔");
                    Check(mirror != null && mirror.ForeColor == Ui.Ok, "条上实时显示状态行（文字和颜色都跟着变）");

                    f.Height += 200; Pump();
                    Check(f.ClientSize.Height == ToolWindow.BarHeight, "收起状态下拉不高（不会拉出一块空白）");

                    f.Location = new Point(f.Left - 60, f.Top + 20); Pump();      // 用户把条拖走
                    var strip = f.Bounds;
                    ((Button)FindText(f, "读取选择")).PerformClick(); Pump();
                    Check(f.QuickCalls == 1, "点快捷按钮执行了窗口的动作");
                    Check(!tw.Collapsed && f.Run.Visible, "点快捷按钮后窗口自己展开");
                    Check(f.Size == before.Size, "展开恢复原大小 " + f.Size + "，原来 " + before.Size);
                    Check(f.Right == strip.Right, "以条被拖到的新位置为准展开");
                    Check(f.Bottom <= work.Bottom && f.Top >= work.Top, "展开后整个在屏幕里");

                    tw.DockTo(false); Pump();
                    Check(f.Left == work.Left + 8, "靠左：贴到左边，left=" + f.Left);
                    tw.Collapse(); Pump();
                    Check(f.Left == work.Left + 8, "贴左收起：左上角不动");
                    remembered = f.Location; rememberedSize = before.Size;
                    tw.AutoCollapse = false;
                    f.Close();   // 收起状态下关：记住的应是展开后的大小，不是那一条
                }
                using (var again = new ProbeForm()) {
                    var tw = ToolWindow.Attach(again, 0, Key);
                    Check(again.Location == remembered, "重新打开：回到上次的位置 " + again.Location + "，期望 " + remembered);
                    Check(again.Size == rememberedSize, "重新打开：是展开后的大小，不是收起的那一条 " + again.Size);
                    Check(!tw.AutoCollapse, "「自动收起」按用户上次的选择");
                }
                // 动画：收起过程中被打断（马上展开 / 关窗口）也必须落到正确的终点，不能卡在半路
                ToolWindow.AnimationMs = 400;
                using (var f = new ProbeForm()) {
                    var tw = ToolWindow.Attach(f, 0, Key);
                    f.Show(); Pump();
                    var full = f.Bounds;
                    tw.Collapse();
                    Check(tw.Collapsed && f.Height > 100, "收起有过渡：刚开始窗口还没缩完（高 " + f.Height + "）");
                    tw.Expand();
                    Check(!tw.Collapsed && f.Run.Visible == false, "动画中途展开：先把收起做完，再开始展开");
                    Thread.Sleep(450); Pump(); Pump();
                    Check(f.Bounds == full && f.Run.Visible, "展开动画播完：回到原来的位置大小、内容放出来 " + f.Bounds);
                    tw.Collapse(); Pump();
                    f.Close();   // 动画没播完就关：存下的仍应是展开后的大小
                }
                Rectangle saved;
                Check(WindowLayoutStore.TryReadBounds(Key, out saved) && saved.Height > 200, "收起动画中途关窗口，记住的仍是展开后的大小 " + saved);
                Console.WriteLine("TOOL WINDOW UI ASSERTIONS " + checks);
            } finally {
                ToolWindow.AnimationMs = savedAnim;
                WindowLayoutStore.Forget(Key);
            }
        }
    }
}
