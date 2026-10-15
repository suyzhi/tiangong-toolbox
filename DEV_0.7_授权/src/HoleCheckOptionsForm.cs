using System;
using System.Drawing;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 配孔检查 →「判定口径」：五个数可以改，「保存」写进 hole-check.tsv，下次检查就按新口径。
    public sealed class HoleCheckOptionsForm : Form {
        readonly NumericUpDown minDia = Ui.Num(0.5M, 200M, 3M, 1, 0.5M, 72);
        readonly NumericUpDown maxDia = Ui.Num(1M, 500M, 25M, 1, 1M, 72);
        readonly NumericUpDown angle = Ui.Num(0.1M, 29M, 2M, 1, 0.5M, 72);
        readonly NumericUpDown offset = Ui.Num(0.01M, 20M, 0.2M, 2, 0.05M, 72);
        readonly NumericUpDown gap = Ui.Num(0M, 200M, 3M, 1, 0.5M, 72);
        readonly Label status = new Label();
        public HoleCheckOptions Result { get; private set; }

        public HoleCheckOptionsForm(HoleCheckOptions current){
            Ui.Shell(this, "配孔检查 · 判定口径", 460, 300, 420, 300);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            var card = Ui.Card("判定口径", 0, "改完点保存，下次检查生效");
            var grid = new FieldGrid { LabelWidth = 96, Dock = DockStyle.Fill };
            grid.Add("连接孔孔径", minDia, FieldGrid.Unit("mm"), Ui.Caption("～"), maxDia, FieldGrid.Unit("mm"));
            grid.Add("轴线方向差", angle, FieldGrid.Unit("°"), FieldGrid.Space(6), Ui.Muted8("小于它算同一方向"));
            grid.Add("同轴偏差", offset, FieldGrid.Unit("mm"), FieldGrid.Space(6), Ui.Muted8("超过它报「孔偏了」"));
            grid.Add("轴向间隙", gap, FieldGrid.Unit("mm"), FieldGrid.Space(6), Ui.Muted8("沿轴线挨这么近才算一对"));
            card.Controls.Add(grid);

            var actions = new Panel { Height = 40, BackColor = Ui.Bg };
            var save = Ui.Primary("保存", 90, 32);
            var reset = Ui.Secondary("恢复默认", 90, 30);
            var cancel = Ui.Secondary("取消", 80, 30);
            save.Click += (s, e) => Save();
            reset.Click += (s, e) => Fill(new HoleCheckOptions());
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            status.AutoSize = false; status.ForeColor = Ui.Bad; status.BackColor = Ui.Bg; status.TextAlign = ContentAlignment.MiddleLeft;
            actions.Controls.Add(save); actions.Controls.Add(reset); actions.Controls.Add(cancel); actions.Controls.Add(status);
            actions.Resize += (s, e) => {
                cancel.SetBounds(actions.Width - 80, 5, 80, 30);
                save.SetBounds(cancel.Left - 8 - 90, 4, 90, 32);
                reset.SetBounds(0, 5, 90, 30);
                status.SetBounds(98, 4, Math.Max(10, save.Left - 104), 32);
            };
            CancelButton = cancel;
            var stack = new VerticalStack { Dock = DockStyle.Fill };
            stack.Add(card, true); stack.Add(actions);
            Controls.Add(stack);
            Fill(current ?? new HoleCheckOptions());
        }

        void Fill(HoleCheckOptions o){
            minDia.Value = Ui.Clamp((decimal)o.MinDiameterMm, minDia.Minimum, minDia.Maximum);
            maxDia.Value = Ui.Clamp((decimal)o.MaxDiameterMm, maxDia.Minimum, maxDia.Maximum);
            angle.Value = Ui.Clamp((decimal)o.AxisAngleTolDeg, angle.Minimum, angle.Maximum);
            offset.Value = Ui.Clamp((decimal)o.AxisOffsetTolMm, offset.Minimum, offset.Maximum);
            gap.Value = Ui.Clamp((decimal)o.CoaxialSpanTolMm, gap.Minimum, gap.Maximum);
        }

        void Save(){
            var o = new HoleCheckOptions {
                MinDiameterMm = (double)minDia.Value, MaxDiameterMm = (double)maxDia.Value,
                AxisAngleTolDeg = (double)angle.Value, AxisOffsetTolMm = (double)offset.Value, CoaxialSpanTolMm = (double)gap.Value
            };
            string p = o.Problem();
            if (p.Length > 0) { status.Text = p; return; }
            try { o.Save(SettingsFile.PathOf(HoleCheckOptions.FileName)); }
            catch (Exception e) { Log.Write("HoleCheckOptionsSave", e); status.Text = "没存上：" + e.Message; return; }
            Result = o;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
