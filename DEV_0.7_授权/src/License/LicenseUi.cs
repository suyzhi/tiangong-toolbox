using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TianGongCadSuite.Licensing {
    // 激活界面：显示机器码、接收激活码、就地校验并落盘。
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
            ClientSize = new Size(620, 380);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(246, 247, 249);

            Label title = new Label();
            title.Text = "本插件需要激活码才能使用";
            title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(24, 34, 52);
            title.AutoSize = true;
            title.Location = new Point(20, 16);
            Controls.Add(title);

            Label machineTitle = new Label();
            machineTitle.Text = "第 1 步：把下面这串机器码发给管理员";
            machineTitle.AutoSize = true;
            machineTitle.Location = new Point(22, 56);
            Controls.Add(machineTitle);

            machineBox = new TextBox();
            machineBox.ReadOnly = true;
            machineBox.Font = new Font("Consolas", 14f, FontStyle.Bold);
            machineBox.BackColor = Color.White;
            machineBox.Location = new Point(24, 78);
            machineBox.Size = new Size(440, 30);
            machineBox.Text = LicenseLibrary.MachineCode();
            Controls.Add(machineBox);

            Button copyButton = new Button();
            copyButton.Text = "复制机器码";
            copyButton.Location = new Point(478, 77);
            copyButton.Size = new Size(118, 30);
            copyButton.Click += delegate {
                try{ Clipboard.SetText(machineBox.Text); statusLabel.Text = "机器码已复制。"; }
                catch(Exception){ statusLabel.Text = "复制失败，请手工抄写。"; }
            };
            Controls.Add(copyButton);

            Label codeTitle = new Label();
            codeTitle.Text = "第 2 步：管理员签发后，把激活码整段粘贴进来（有效期分一个月 / 半年 / 一年三档）";
            codeTitle.AutoSize = true;
            codeTitle.Location = new Point(22, 122);
            Controls.Add(codeTitle);

            codeBox = new TextBox();
            codeBox.Multiline = true;
            codeBox.ScrollBars = ScrollBars.Vertical;
            codeBox.Font = new Font("Consolas", 10f);
            codeBox.Location = new Point(24, 146);
            codeBox.Size = new Size(572, 116);
            codeBox.WordWrap = true;
            codeBox.TextChanged += delegate { RefreshPlan(); };
            Controls.Add(codeBox);

            Button importButton = new Button();
            importButton.Text = "从文件导入…";
            importButton.Location = new Point(24, 272);
            importButton.Size = new Size(110, 30);
            importButton.Click += delegate { ImportFromFile(); };
            Controls.Add(importButton);

            Button activateButton = new Button();
            activateButton.Text = "激活";
            activateButton.Location = new Point(396, 272);
            activateButton.Size = new Size(96, 30);
            activateButton.Click += delegate { Activate(); };
            Controls.Add(activateButton);

            Button closeButton = new Button();
            closeButton.Text = "关闭";
            closeButton.Location = new Point(500, 272);
            closeButton.Size = new Size(96, 30);
            closeButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(closeButton);

            planLabel = new Label();
            planLabel.AutoSize = false;
            planLabel.Location = new Point(146, 278);
            planLabel.Size = new Size(240, 20);
            planLabel.ForeColor = Color.FromArgb(70, 80, 95);
            Controls.Add(planLabel);

            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Location = new Point(24, 312);
            statusLabel.Size = new Size(572, 52);
            statusLabel.ForeColor = Color.FromArgb(60, 70, 85);
            Controls.Add(statusLabel);

            RefreshPlan();
            statusLabel.Text = "当前状态：" + LicenseLibrary.Current().Describe();
        }

        void RefreshPlan(){
            string text = codeBox.Text;
            LicenseCode code = LicenseCodec.Parse(text);
            if(code == null){
                planLabel.Text = string.IsNullOrEmpty(text.Trim()) ? "" : "格式待确认…";
                return;
            }
            planLabel.Text = code.Summary;
            planLabel.ForeColor = LicenseCodec.VerifySignature(code) ? Color.FromArgb(20, 110, 60) : Color.FromArgb(170, 40, 40);
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

        void Activate(){
            string message;
            LicenseReport report = LicenseLibrary.Activate(codeBox.Text, out message);
            if(message != null){
                statusLabel.ForeColor = Color.FromArgb(170, 40, 40);
                statusLabel.Text = message;
                return;
            }
            statusLabel.ForeColor = Color.FromArgb(20, 110, 60);
            statusLabel.Text = "激活成功。" + report.Describe();
            DialogResult = DialogResult.OK;
            MessageBox.Show(this, "激活成功。\r\n" + report.Describe(), "天工工具箱", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
