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
            Ui.Shell(this, "配孔检查 · 判定口径", 540, 380, 500, 380);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            // 0.9.0「D」对话框：上面标题 + 一行说明，中间参数，底部操作栏（左：恢复默认；右：取消、保存）
            var body = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 8), Gap = 6, AutoScroll = false };
            body.Add(new Label { Text = "判定口径", AutoSize = false, Height = 32, Font = Ui.F12B, ForeColor = Ui.Text, BackColor = Ui.Bg });
            body.Add(new Label { Text = "改完点保存，下次检查生效。", AutoSize = false, Height = 26, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg });
            var grid = new FieldGrid { LabelWidth = 96, BackColor = Ui.Bg };
            grid.Add("连接孔孔径", minDia, FieldGrid.Unit("mm"), Ui.Caption("～"), maxDia, FieldGrid.Unit("mm"));
            grid.Add("轴线方向差", angle, FieldGrid.Unit("°"), FieldGrid.Space(6), Ui.Muted8("小于它算同一方向"));
            grid.Add("同轴偏差", offset, FieldGrid.Unit("mm"), FieldGrid.Space(6), Ui.Muted8("超过它报「孔偏了」"));
            grid.Add("轴向间隙", gap, FieldGrid.Unit("mm"), FieldGrid.Space(6), Ui.Muted8("沿轴线挨这么近才算一对"));
            grid.Height = grid.PreferredHeight;
            body.Add(grid);

            var actions = new ActionBar { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(18, 0, 18, 0) };
            var save = Ui.Primary("保存", 96, 40);
            var reset = Ui.Ghost("恢复默认");
            var cancel = Ui.Secondary("取消", 80, 36);
            save.Click += (s, e) => Save();
            reset.Click += (s, e) => Fill(new HoleCheckOptions());
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            actions.AddLeft(reset); actions.AddRight(cancel); actions.AddRight(save);
            actions.Status = status; status.ForeColor = Ui.Bad;
            CancelButton = cancel;
            Controls.Add(body); Controls.Add(actions);
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
