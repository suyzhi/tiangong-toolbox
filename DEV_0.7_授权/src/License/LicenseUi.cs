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
            ClientSize = new Size(660, 430);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(246, 247, 249);

            Label title = new Label();
            title.Text = "本插件需要激活码才能使用";
            title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(24, 34, 52);
            title.AutoSize = true;
            title.Location = new Point(20, 16);
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = "把管理员发来的激活码整段粘贴到下面的框里，点「激活」。激活后这个码就绑定本机，以后不用再输入。";
            hint.AutoSize = false;
            hint.Size = new Size(620, 22);
            hint.Location = new Point(22, 50);
            hint.ForeColor = Color.FromArgb(70, 80, 95);
            Controls.Add(hint);

            codeBox = new TextBox();
            codeBox.Multiline = true;
            codeBox.ScrollBars = ScrollBars.Vertical;
            codeBox.Font = new Font("Consolas", 10f);
            codeBox.Location = new Point(22, 74);
            codeBox.Size = new Size(616, 132);
            codeBox.WordWrap = true;
            codeBox.TextChanged += delegate { RefreshPlan(); };
            Controls.Add(codeBox);

            planLabel = new Label();
            planLabel.AutoSize = false;
            planLabel.Location = new Point(22, 212);
            planLabel.Size = new Size(616, 20);
            planLabel.ForeColor = Color.FromArgb(70, 80, 95);
            Controls.Add(planLabel);

            Label machineTitle = new Label();
            machineTitle.Text = "本机机器码（只有售后核对时才需要，管理员发码时不用它）";
            machineTitle.AutoSize = true;
            machineTitle.Location = new Point(22, 240);
            Controls.Add(machineTitle);

            machineBox = new TextBox();
            machineBox.ReadOnly = true;
            machineBox.Font = new Font("Consolas", 12f, FontStyle.Bold);
            machineBox.BackColor = Color.White;
            machineBox.Location = new Point(22, 262);
            machineBox.Size = new Size(456, 28);
            machineBox.Text = LicenseLibrary.MachineCode();
            Controls.Add(machineBox);

            Button copyButton = new Button();
            copyButton.Text = "复制机器码";
            copyButton.Location = new Point(486, 261);
            copyButton.Size = new Size(152, 30);
            copyButton.Click += delegate {
                try{ Clipboard.SetText(machineBox.Text); statusLabel.Text = "机器码已复制（发给管理员时可作为售后凭证）。"; }
                catch(Exception){ statusLabel.Text = "复制失败，请手工抄写。"; }
            };
            Controls.Add(copyButton);

            Button importButton = new Button();
            importButton.Text = "从文件导入…";
            importButton.Location = new Point(22, 306);
            importButton.Size = new Size(122, 32);
            importButton.Click += delegate { ImportFromFile(); };
            Controls.Add(importButton);

            Button activateButton = new Button();
            activateButton.Text = "激活";
            activateButton.Location = new Point(414, 306);
            activateButton.Size = new Size(110, 32);
            activateButton.Click += delegate { DoActivate(); };
            Controls.Add(activateButton);

            Button closeButton = new Button();
            closeButton.Text = "关闭";
            closeButton.Location = new Point(536, 306);
            closeButton.Size = new Size(102, 32);
            closeButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(closeButton);

            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Location = new Point(22, 346);
            statusLabel.Size = new Size(616, 68);
            statusLabel.ForeColor = Color.FromArgb(60, 70, 85);
            Controls.Add(statusLabel);

            RefreshPlan();
            statusLabel.Text = "当前状态：" + LicenseLibrary.Current().Describe();
        }

        // 粘贴过程中就先把档位、到期日和码ID显示出来，抄错时能立刻看出来。
        void RefreshPlan(){
            string text = codeBox.Text;
            LicenseCode code = LicenseCodec.Parse(text);
            if(code == null){
                planLabel.Text = string.IsNullOrEmpty(text.Trim()) ? "" : "格式待确认…";
                planLabel.ForeColor = Color.FromArgb(70, 80, 95);
                return;
            }
            bool signed = LicenseCodec.VerifySignature(code);
            // 码ID 已经由 Summary 带出来了，这里只补"验签不过"的提醒，避免同一行出现两次码ID。
            planLabel.Text = code.Summary + (signed ? "" : "（签名校验不通过）");
            planLabel.ForeColor = signed ? Color.FromArgb(20, 110, 60) : Color.FromArgb(170, 40, 40);
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
