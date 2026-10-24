using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TianGongCadSuite.Licensing {
    // 激活界面（DEV 0.7 新方案）：
    //   管理员只发激活码，不再收集机器码；用户把码粘进来点「激活」，这个码就在本机落盘绑定。
    //   窗口里仍然显示机器码，但只作售后核对用。
    internal sealed class ActivationForm : Form {
        readonly TextBox machineBox;
        readonly TextBox codeBox;
        readonly Label statusLabel;
        readonly Label planLabel;

        internal ActivationForm(){
            Text = "天工工具箱 授权激活";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 470);
            Font = Ui.F9;
            BackColor = Ui.Bg;

            Label title = new Label();
            title.Text = "本插件需要激活码才能使用";
            title.Font = Ui.Title;
            title.ForeColor = Ui.Text;
            title.AutoSize = true;
            title.Location = new Point(20, 16);
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = "把管理员发来的激活码整段粘贴到下面的框里，点「激活」。激活后这个码就绑定本机，以后不用再输入。";
            hint.AutoSize = false;
            hint.Size = new Size(620, 22);
            hint.Location = new Point(22, 56);
            hint.ForeColor = Ui.Text2;
            Controls.Add(hint);

            codeBox = new TextBox();
            codeBox.Multiline = true;
            codeBox.ScrollBars = ScrollBars.Vertical;
            codeBox.Font = new Font("Consolas", 10f);
            codeBox.Location = new Point(22, 82);
            codeBox.Size = new Size(616, 124);
            codeBox.BorderStyle = BorderStyle.FixedSingle;
            codeBox.WordWrap = true;
            codeBox.TextChanged += delegate { RefreshPlan(); };
            Controls.Add(codeBox);

            planLabel = new Label();
            planLabel.AutoSize = false;
            planLabel.Location = new Point(22, 212);
            planLabel.Size = new Size(616, 20);
            planLabel.ForeColor = Ui.Muted;
            Controls.Add(planLabel);

            Label machineTitle = new Label();
            machineTitle.Text = "本机机器码（只有售后核对时才需要，管理员发码时不用它）";
            machineTitle.AutoSize = true;
            machineTitle.Font = Ui.F8; machineTitle.ForeColor = Ui.Text2;
            machineTitle.Location = new Point(22, 242);
            Controls.Add(machineTitle);

            machineBox = new TextBox();
            machineBox.ReadOnly = true;
            machineBox.Font = new Font("Consolas", 12f, FontStyle.Bold);
            machineBox.BackColor = Ui.Rail;
            machineBox.BorderStyle = BorderStyle.FixedSingle;
            machineBox.Location = new Point(22, 264);
            machineBox.Size = new Size(456, 28);
            machineBox.Text = LicenseLibrary.MachineCode();
            Controls.Add(machineBox);

            Button copyButton = Ui.Secondary("复制机器码", 152, 34);
            copyButton.Location = new Point(486, 262);
            copyButton.Click += delegate {
                try{ Clipboard.SetText(machineBox.Text); statusLabel.Text = "机器码已复制（发给管理员时可作为售后凭证）。"; }
                catch(Exception){ statusLabel.Text = "复制失败，请手工抄写。"; }
            };
            Controls.Add(copyButton);

            Button importButton = Ui.Ghost("从文件导入…");
            importButton.Location = new Point(16, 405);
            importButton.Click += delegate { ImportFromFile(); };
            Controls.Add(importButton);

            Button activateButton = Ui.Primary("激活", 120, 44);
            activateButton.Location = new Point(518, 400);
            activateButton.Click += delegate { DoActivate(); };
            Controls.Add(activateButton);

            Button closeButton = Ui.Secondary("关闭", 84, 38);
            closeButton.Location = new Point(424, 403);
            closeButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(closeButton);

            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Location = new Point(22, 306);
            statusLabel.Size = new Size(616, 70);
            statusLabel.ForeColor = Ui.Text2;
            Controls.Add(statusLabel);
            // 底部操作栏的分隔线
            Paint += delegate(object sender, PaintEventArgs e) { using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, 388, ClientSize.Width, 388); };

            RefreshPlan();
            statusLabel.Text = "当前状态：" + LicenseLibrary.Current().Describe();
        }

        // 粘贴过程中就先把档位、到期日和码ID显示出来，抄错时能立刻看出来。
        void RefreshPlan(){
            string text = codeBox.Text;
            LicenseCode code = LicenseCodec.Parse(text);
            if(code == null){
                planLabel.Text = string.IsNullOrEmpty(text.Trim()) ? "" : "格式待确认…";
                planLabel.ForeColor = Ui.Muted;
                return;
            }
            bool signed = LicenseCodec.VerifySignature(code);
            // 码ID 已经由 Summary 带出来了，这里只补"验签不过"的提醒，避免同一行出现两次码ID。
            planLabel.Text = code.Summary + (signed ? "" : "（签名校验不通过）");
            planLabel.ForeColor = signed ? Ui.Ok : Ui.Bad;
        }

        void ImportFromFile(){
            using(OpenFileDialog dialog = new OpenFileDialog()){
                dialog.Title = "选择激活码文件";
                dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                if(dialog.ShowDialog(this) != DialogResult.OK)return;
                try{ codeBox.Text = File.ReadAllText(dialog.FileName, Encoding.UTF8); }
                catch(Exception e){ statusLabel.Text = "读取失败：" + e.Message; }
            }
        }

        void DoActivate(){
            string message;
            string notice;
            LicenseReport report = LicenseLibrary.Activate(codeBox.Text, out message, out notice);
            if(message != null){
                statusLabel.ForeColor = Color.FromArgb(170, 40, 40);
                statusLabel.Text = message;
                return;
            }
            string head = string.IsNullOrEmpty(notice) ? "激活成功。" : notice;
            statusLabel.ForeColor = Color.FromArgb(20, 110, 60);
            statusLabel.Text = head + "\r\n" + report.Describe();
            DialogResult = DialogResult.OK;
            MessageBox.Show(this, head + "\r\n" + report.Describe(), "天工工具箱", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }

    internal static class LicenseActivation {
        internal static bool Show(IWin32Window owner){
            using(ActivationForm form = new ActivationForm()){
                form.ShowDialog(owner);
                return LicenseLibrary.Current().Usable;
            }
        }

        internal static void OpenStatus(){
            using(ActivationForm form = new ActivationForm())form.ShowDialog();
        }
    }
}
