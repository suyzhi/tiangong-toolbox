using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
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
        readonly Button activateButton;
        readonly Button retryButton;
        readonly Button importButton;
        readonly Font codeFont;
        readonly Font machineFont;
        bool busy;

        internal ActivationForm(){
            Ui.Shell(this, "天工工具箱 授权激活", 660, 486, 620, 460);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            int pad = Ui.S(22), cw = ClientSize.Width - pad * 2;
            Label title = new Label();
            title.Text = "本插件需要激活码才能使用";
            title.Font = Ui.Title;
            title.ForeColor = Ui.Text;
            title.AutoSize = true;
            title.Location = new Point(pad, Ui.S(16));
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = "把管理员发来的激活码整段粘贴到下面的框里，点「激活」。激活需要联网，激活后这个码就绑定本机。";
            hint.AutoSize = false;
            hint.Size = new Size(cw, Ui.S(36));
            hint.Location = new Point(pad, Ui.S(58));
            hint.ForeColor = Ui.Text2;
            Controls.Add(hint);

            codeFont = new Font("Consolas", 10f);
            codeBox = new TextBox();
            codeBox.Multiline = true;
            codeBox.ScrollBars = ScrollBars.Vertical;
            codeBox.Font = codeFont;
            codeBox.Location = new Point(pad, Ui.S(98));
            codeBox.Size = new Size(cw, Ui.S(120));
            codeBox.BorderStyle = BorderStyle.FixedSingle;
            codeBox.WordWrap = true;
            codeBox.TextChanged += delegate { RefreshPlan(); };
            Controls.Add(codeBox);

            planLabel = new Label();
            planLabel.AutoSize = false;
            planLabel.Location = new Point(pad, Ui.S(224));
            planLabel.Size = new Size(cw, Ui.S(22));
            planLabel.ForeColor = Ui.Muted;
            Controls.Add(planLabel);

            Label machineTitle = new Label();
            machineTitle.Text = "本机机器码（只有售后核对时才需要，管理员发码时不用它）";
            machineTitle.AutoSize = true;
            machineTitle.Font = Ui.F8; machineTitle.ForeColor = Ui.Text2;
            machineTitle.Location = new Point(pad, Ui.S(254));
            Controls.Add(machineTitle);

            Button copyButton = Ui.Secondary("复制机器码", 152, 34);
            machineFont = new Font("Consolas", 12f, FontStyle.Bold);
            machineBox = new TextBox();
            machineBox.ReadOnly = true;
            machineBox.Font = machineFont;
            machineBox.BackColor = Ui.Rail;
            machineBox.BorderStyle = BorderStyle.FixedSingle;
            machineBox.Location = new Point(pad, Ui.S(278));
            machineBox.Size = new Size(Math.Max(Ui.S(200), cw - copyButton.Width - Ui.S(10)), Ui.S(32));
            machineBox.Text = LicenseLibrary.MachineCode();
            Controls.Add(machineBox);

            copyButton.Location = new Point(machineBox.Right + Ui.S(10), Ui.S(277));
            copyButton.Click += delegate {
                // 使用带重试的原生剪贴板封装，避免天工 CAD COM 进程内 WinForms Clipboard.SetText 偶发占用失败。
                if(ClipboardUtil.SetText(machineBox.Text))statusLabel.Text = "机器码已复制（发给管理员时可作为售后凭证）。";
                else statusLabel.Text = "复制失败，请手工抄写。";
            };
            Controls.Add(copyButton);

            int barY = ClientSize.Height - Ui.S(76);
            importButton = Ui.Ghost("从文件导入…");
            importButton.Location = new Point(Ui.S(16), barY + (Ui.S(76) - importButton.Height) / 2);
            importButton.Click += delegate { ImportFromFile(); };
            Controls.Add(importButton);

            activateButton = Ui.Primary("激活", 120, 44);
            activateButton.Location = new Point(ClientSize.Width - pad - activateButton.Width, barY + (Ui.S(76) - activateButton.Height) / 2);
            activateButton.Click += delegate { DoActivate(); };
            Controls.Add(activateButton);

            Button closeButton = Ui.Secondary("关闭", 84, 38);
            closeButton.Location = new Point(activateButton.Left - Ui.S(10) - closeButton.Width, barY + (Ui.S(76) - closeButton.Height) / 2);
            closeButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(closeButton);

            // 已激活、但超过宽限期没联网（或服务器暂时连不上）时，不用重新输码，点这里再试一次。
            retryButton = Ui.Secondary("重新联网验证", 126, 38);
            retryButton.Location = new Point(closeButton.Left - Ui.S(10) - retryButton.Width, barY + (Ui.S(76) - retryButton.Height) / 2);
            retryButton.Click += delegate { DoRetry(); };
            Controls.Add(retryButton);

            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Location = new Point(pad, Ui.S(320));
            statusLabel.Size = new Size(cw, Math.Max(Ui.S(48), barY - Ui.S(328)));
            statusLabel.ForeColor = Ui.Text2;
            Controls.Add(statusLabel);
            // 底部操作栏的分隔线
            Paint += delegate(object sender, PaintEventArgs e) { using (var p = new Pen(Ui.Line)) e.Graphics.DrawLine(p, 0, barY, ClientSize.Width, barY); };

            RefreshPlan();
            statusLabel.Text = "当前状态：" + LicenseLibrary.Current().Describe();
        }

        protected override void Dispose(bool disposing){
            if(disposing){
                if(codeFont != null)codeFont.Dispose();
                if(machineFont != null)machineFont.Dispose();
            }
            base.Dispose(disposing);
        }

        void SetBusy(bool on, string tip){
            busy = on;
            activateButton.Enabled = !on;
            retryButton.Enabled = !on;
            importButton.Enabled = !on;
            codeBox.ReadOnly = on;
            Cursor = on ? Cursors.WaitCursor : Cursors.Default;
            if(!string.IsNullOrEmpty(tip)){
                statusLabel.ForeColor = Ui.Muted;
                statusLabel.Text = tip;
            }
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

        // 联网复核与激活放到后台线程执行，弱网/代理超时时不再冻结天工 CAD 主窗口长达 16 秒。
        void DoRetry(){
            if(busy)return;
            SetBusy(true, "正在连接授权服务器验证…");
            Thread worker = new Thread(delegate(){
                LicenseReport report = null;
                string err = null;
                try{ report = LicenseOnline.RetryNow(); }
                catch(Exception ex){ err = ex.Message; }
                UiPost(delegate(){
                    SetBusy(false, null);
                    if(err != null){
                        statusLabel.ForeColor = Ui.Bad;
                        statusLabel.Text = "联网验证失败：" + err;
                        return;
                    }
                    statusLabel.ForeColor = report.Usable ? Ui.Ok : Ui.Bad;
                    statusLabel.Text = "当前状态：" + report.Describe();
                    if(report.Usable){
                        DialogResult = DialogResult.OK;
                        MessageBox.Show(this, "联网验证成功。\r\n" + report.Describe(), "天工工具箱", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Close();
                    }
                });
            });
            worker.IsBackground = true;
            worker.Name = "TG-License-Retry";
            worker.Start();
        }

        void DoActivate(){
            if(busy)return;
            string codeInput = codeBox.Text;
            SetBusy(true, "正在联网激活，请稍候…");
            Thread worker = new Thread(delegate(){
                string message = null;
                string notice = null;
                LicenseReport report = null;
                try{ report = LicenseLibrary.Activate(codeInput, out message, out notice); }
                catch(Exception ex){ message = "激活失败：" + ex.Message; }
                UiPost(delegate(){
                    SetBusy(false, null);
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
                });
            });
            worker.IsBackground = true;
            worker.Name = "TG-License-Activate";
            worker.Start();
        }

        void UiPost(Action action){
            if(IsDisposed || !IsHandleCreated)return;
            try{ BeginInvoke(action); }catch(InvalidOperationException){ }
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
