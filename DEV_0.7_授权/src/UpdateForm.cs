using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 「发现新版本」（0.9.1 自动更新）：D 对话框 —— 标题 + 版本说明 + 更新内容，底部「跳过这个版本 / 稍后提醒 / 下载更新」。
    // 下载在后台线程，按钮本身不动，下面一条细进度条；下载好了起等待脚本，关闭天工后自动安装。
    public sealed class UpdateForm : Form {
        public enum Answer { None, Downloaded, Later, Skip, RestartNow }
        public Answer Choice { get; private set; }

        readonly UpdateManifest m;
        readonly Label status = new Label();
        readonly SlimProgress bar = new SlimProgress { Visible = false };
        readonly FlatButton download = (FlatButton)Ui.Primary("下载更新", 128, 40);
        readonly Button later = Ui.Secondary("稍后提醒", 96, 36);
        readonly Button skip = Ui.Ghost("跳过这个版本");
        volatile bool cancel;
        bool busy, done, scheduled;
        string pkg;

        // stagedPkg：已经下载好（等着关天工再装）的包，直接打开到"已下载"那一步
        public UpdateForm(UpdateManifest manifest) : this(manifest, null) { }
        public UpdateForm(UpdateManifest manifest, string stagedPkg){
            m = manifest;
            Ui.Shell(this, "发现新版本", 560, 470, 480, 400);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Text = "天工工具箱 更新";

            var body = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 8), Gap = 6, AutoScroll = false };
            body.Add(new Label { Text = "发现新版本 " + m.Version, AutoSize = false, Height = 34, Font = Ui.Title, ForeColor = Ui.Text, BackColor = Ui.Bg });
            body.Add(new Label { Text = "你现在用的是 " + Product.Version + "　·　" + (m.Date.Length > 0 ? m.Date + " 发布　·　" : "") + "安装包 " + m.SizeText,
                                 AutoSize = false, Height = 24, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg });
            body.Add(new Label { Text = "更新内容", AutoSize = false, Height = 26, Font = Ui.F9B, ForeColor = Ui.Text2, BackColor = Ui.Bg, TextAlign = ContentAlignment.BottomLeft });
            var notes = new TextBox {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Ui.Rail, ForeColor = Ui.Text, Font = Ui.F9, TabStop = false,
                Text = m.Notes.Length > 0 ? m.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n") : "（这次发布没有写更新说明）"
            };
            var notesFrame = new Panel { BackColor = Ui.Rail, Padding = new Padding(12, 10, 8, 10) };
            notes.Dock = DockStyle.Fill; notesFrame.Controls.Add(notes);
            body.Add(notesFrame, true);
            bar.Height = 8;
            body.Add(bar);
            status.AutoSize = false; status.Height = 44; status.Font = Ui.F8; status.ForeColor = Ui.Muted; status.BackColor = Ui.Bg;
            status.Text = "下载好以后，关闭天工时自动安装（大约 10 秒），再打开天工就是新版本。下载时可以照常用天工。";
            body.Add(status);

            var actions = new ActionBar { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(18, 0, 18, 0) };
            download.Click += (s, e) => { if (done) RestartNow(); else StartDownload(); };
            later.Click += (s, e) => {
                if (done) { ScheduleOnClose(); Choice = Answer.Downloaded; Close(); }
                else { Choice = Answer.Later; Close(); }
            };
            skip.Click += (s, e) => { Choice = Answer.Skip; Close(); };
            actions.AddLeft(skip); actions.AddRight(later); actions.AddRight(download);
            Controls.Add(body); Controls.Add(actions);
            // 下载好了但没选（直接点了 ×）：按"关闭天工时再装"
            FormClosing += (s, e) => { if (busy) cancel = true; else if (done && !scheduled) { ScheduleOnClose(); if (Choice == Answer.None) Choice = Answer.Downloaded; } };
            if (stagedPkg != null) { pkg = stagedPkg; scheduled = true; ShowReady(true); }
        }

        // 「立即更新」：确认 → 起等待脚本（请天工关闭 → 装 → 重新打开天工）
        void RestartNow(){
            var r = MessageBox.Show(this, "天工会关闭，装好 " + m.Version + " 后自动重新打开（大约半分钟）。\r\n"
                + "没保存的文件，天工关闭前会先问你要不要保存。\r\n\r\n现在更新吗？",
                "立即更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (r != DialogResult.OK) return;
            string error;
            if (!AutoUpdate.Schedule(m, pkg, true, out error)) { status.ForeColor = Ui.Bad; status.Text = error; return; }
            scheduled = true;
            Choice = Answer.RestartNow;
            Close();
        }
        void ScheduleOnClose(){
            if (scheduled || pkg == null || noScheduleForTest) return;
            string error;
            if (AutoUpdate.Schedule(m, pkg, false, out error)) scheduled = true;
            else Log.Write("UpdateSchedule", error);
        }

        // 下载好的样子：主按钮「立即更新」，次按钮「关闭天工时再装」
        void ShowReady(bool alreadyWaiting){
            done = true;
            bar.Visible = true; bar.Maximum = 1000; bar.Value = 1000;
            status.ForeColor = Ui.Ok;
            status.Text = (alreadyWaiting ? "已经下载好了，正等着天工关闭后安装。" : "已下载好。")
                        + "点「立即更新」：天工关闭、装好 " + m.Version + " 后自动重新打开（大约半分钟）；手上有活就点「关闭天工时再装」。";
            download.Text = "立即更新"; download.Enabled = true;
            later.Text = "关闭天工时再装"; later.Width = 128; later.Enabled = true; later.Visible = true;
            skip.Visible = false;
        }

        void StartDownload(){
            busy = true; cancel = false;
            download.Enabled = false; later.Enabled = false; skip.Enabled = false;
            bar.Visible = true; bar.Maximum = 1000; bar.Value = 0;
            status.ForeColor = Ui.Muted; status.Text = "正在下载 " + m.SizeText + "…";
            var t = new Thread(() => {
                string pkg = null, error = null;
                try {
                    pkg = AutoUpdate.Download(m, (got, total) => UiPost(() => {
                        bar.Value = (int)(1000.0 * got / Math.Max(1, total));
                        status.Text = "正在下载 " + (got / 1048576.0).ToString("0.0") + " / " + m.SizeText + "…";
                    }), () => cancel);
                } catch (Exception e) { error = e.Message; Log.Write("UpdateDownload", e); }
                UiPost(() => Finished(pkg, error));
            });
            t.IsBackground = true; t.Name = "TG-Update-Download";
            t.Start();
        }

        void Finished(string downloaded, string error){
            busy = false;
            if (downloaded != null && error == null) {
                pkg = downloaded;
                ShowReady(false);
                return;
            }
            bar.Visible = false;
            status.ForeColor = Ui.Bad;
            status.Text = (error ?? "下载没完成。") + "　可以点「重试」，或者以后在功能区点「检查更新」。";
            download.Text = "重试"; download.Enabled = true; later.Enabled = true; skip.Enabled = true;
        }

        void UiPost(Action a){
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        // 测试：不弹窗截图；关窗时不真的起等待脚本、不写 update.tsv
        bool noScheduleForTest;
        internal void FinishForTest(string downloaded, string error){ noScheduleForTest = true; Finished(downloaded, error); }
    }
}
