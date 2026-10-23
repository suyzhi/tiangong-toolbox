using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 打孔结果列表（自动打孔 / 批量排孔共用）。代替原来那个一屏都放不下的结果弹框：
    //   · 顶上一句结论（打了几个、失败几个），颜色说明好坏；
    //   · 下面一条一条：失败（红）/ 本来就有孔、跳过（橙）/ 打好的孔（绿）/ 说明（灰）；
    //   · 带位置的条目点一下就把视图镜头拉过去（Locate 事件，由窗口去调 CadView）。
    public sealed class ResultList : Panel {
        public enum Kind { Done, Skip, Fail, Info, Warn }   // Warn：做成了但打了折扣（比如孔打好了、没挂上天工关联），要用户知道
        public sealed class Item {
            public Kind Kind;
            public string Text = "";
            public bool HasWhere;
            public V3 Where;               // 装配坐标（米）
            public double DiameterMm;      // 镜头框多大
            public object Tag;
            public Item(Kind kind, string text){ Kind = kind; Text = text ?? ""; }
            public Item At(V3 where, double diameterMm){ HasWhere = where.Finite; Where = where; DiameterMm = diameterMm; return this; }
            public override string ToString(){ return Text; }
        }

        public event Action<Item> Locate;
        readonly Label headline = new Label();
        readonly ListBox list = new ListBox();
        readonly ToolTip tip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
        int lastTip = -1;

        public ResultList(){
            BackColor = Color.White;
            headline.Dock = DockStyle.Top; headline.Height = 24; headline.Font = Ui.F9B; headline.AutoEllipsis = true;
            headline.TextAlign = ContentAlignment.MiddleLeft; headline.BackColor = Color.White;
            list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.IntegralHeight = false;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 22; list.BackColor = Color.White;
            list.DrawItem += DrawItem;
            list.Click += (s, e) => Fire();
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Fire(); e.Handled = true; } };
            list.MouseMove += (s, e) => {
                int i = list.IndexFromPoint(e.Location);
                if (i == lastTip) return;
                lastTip = i;
                var it = i >= 0 && i < list.Items.Count ? list.Items[i] as Item : null;
                tip.SetToolTip(list, it == null ? "" : it.Text + (it.HasWhere ? "\r\n（点一下跳到这个位置）" : ""));
            };
            Controls.Add(list); Controls.Add(headline);
        }

        public int Count { get { return list.Items.Count; } }
        public IList<Item> Items { get { var r = new List<Item>(); foreach (var o in list.Items) r.Add((Item)o); return r; } }
        public string Headline { get { return headline.Text; } }

        public void SetResult(string head, Color color, IEnumerable<Item> items){
            headline.Text = head ?? ""; headline.ForeColor = color;
            list.BeginUpdate();
            try {
                list.Items.Clear();
                // 先坏后好：失败的放最上面，用户第一眼看到要处理的
                foreach (var k in new[]{ Kind.Fail, Kind.Warn, Kind.Skip, Kind.Done, Kind.Info })
                    foreach (var it in items) if (it.Kind == k) list.Items.Add(it);
            } finally { list.EndUpdate(); }
        }
        public void Clear(){ headline.Text = ""; list.Items.Clear(); }

        // 复制全部（给人看的纯文本）
        public string AsText(){
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(headline.Text);
            foreach (var o in list.Items) { var it = (Item)o; sb.Append(Mark(it.Kind)).Append(' ').AppendLine(it.Text); }
            return sb.ToString();
        }

        void Fire(){
            var it = list.SelectedItem as Item;
            if (it == null || !it.HasWhere) return;
            var h = Locate; if (h != null) h(it);
        }

        static string Mark(Kind k){ switch (k) { case Kind.Done: return "✓"; case Kind.Skip: return "–"; case Kind.Fail: return "✗"; case Kind.Warn: return "!"; default: return "·"; } }
        static Color ColorOf(Kind k){ switch (k) { case Kind.Done: return Ui.Ok; case Kind.Skip: case Kind.Warn: return Ui.Warn; case Kind.Fail: return Ui.Bad; default: return Ui.Muted; } }

        void DrawItem(object sender, DrawItemEventArgs e){
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var it = (Item)list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.ChipBg : Color.White)) e.Graphics.FillRectangle(b, e.Bounds);
            var c = ColorOf(it.Kind);
            TextRenderer.DrawText(e.Graphics, Mark(it.Kind), Ui.F9B, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, 16, e.Bounds.Height), c,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            int right = it.HasWhere ? 46 : 4;
            TextRenderer.DrawText(e.Graphics, it.Text, Ui.F9, new Rectangle(e.Bounds.X + 26, e.Bounds.Y, e.Bounds.Width - 26 - right, e.Bounds.Height),
                it.Kind == Kind.Info ? Ui.Muted : it.Kind == Kind.Warn ? Ui.Warn : Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (it.HasWhere)
                TextRenderer.DrawText(e.Graphics, "跳转 ›", Ui.F8, new Rectangle(e.Bounds.Right - 46, e.Bounds.Y, 42, e.Bounds.Height), Ui.Accent,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        protected override void Dispose(bool disposing){ if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }
}
