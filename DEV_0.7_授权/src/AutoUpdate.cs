using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite.Licensing;

namespace TianGongCadSuite {
    // 服务器上发布的一个版本（server/tg_license_update.py 写的签名清单）
    public sealed class UpdateManifest {
        public string Version = "", Date = "", Notes = "", File = "", Sha256 = "";
        public long Size;
        public Version Parsed;
        public string SizeText { get { return (Size / 1048576.0).ToString("0.0") + " MB"; } }
    }

    // 自动更新（0.9.1）：
    //   CAD 启动后在后台问一次授权服务器「有没有新版本」（12 小时最多一次）；有就记下来，
    //   用户下一次点工具箱的命令时弹「发现新版本」：下载 → 关闭天工后自动安装 / 稍后提醒 / 跳过这个版本。
    //   功能区「检查更新」随时手动查。
    // 安全：清单用服务端私钥签名，插件只认内嵌服务端公钥（LicenseServerSlot，和授权回执同一把）；
    //   安装包的 SHA-256 和大小写在签过名的清单里，下载完对不上就不装。
    // 安装：插件跑在天工进程里，DLL 被占着没法换。所以只下载、解压，然后起一个独立的小脚本（不是天工的子进程，
    //   天工关了它还在），等所有天工进程都退出后静默运行安装包自己的 tools\install.ps1 -Force，装完弹一句。
    //   「立即更新」（用户要的，0.9.2）：脚本先请天工关闭（等于点窗口的 ×，没保存的文件天工自己会问），
    //   5 分钟内关掉了就装好后自动重新打开天工；用户在保存提示里点了取消 = 天工还开着 → 退回"关闭天工时再装"，事后不会突然把天工打开。
    public static class AutoUpdate {
        const string StateFile = "update.tsv";
        const double CheckHours = 12;
        const int TimeoutMs = 15000;

        static volatile UpdateManifest pending;   // 后台查到的新版本，等用户点命令时再提示
        static int checking;
        static bool promptedThisSession;

        // 测试：等待脚本只写日志不真装；跳过 12 小时间隔
        internal static bool DryRunForTest;
        internal static bool IgnoreIntervalForTest;
        // 测试：「立即更新」关的 / 重开的换成一个假天工（别把开发机上真的天工关掉）
        internal static string CadNameForTest, CadExeForTest;
        internal static int CloseWaitSecondsForTest;

        // ---------- 地址与版本 ----------

        // 更新地址跟着授权服务器走：…/api/v1/license → …/api/v1/update。没配授权服务器（占位）= 不检查更新
        internal static string BaseUrl {
            get {
                string url = LicenseServerSlot.Url;
#if TG_DEV_BUILD
                if (LicenseTestHooks.ServerUrlOverride != null) url = LicenseTestHooks.ServerUrlOverride;
#endif
                return UpdateUrlFor(url);
            }
        }
        internal static string UpdateUrlFor(string licenseUrl){
            if (string.IsNullOrEmpty(licenseUrl)) return null;
            string u = licenseUrl.TrimEnd('/');
            int i = u.LastIndexOf('/');
            return i < 0 ? null : u.Substring(0, i) + "/update";
        }

        public static Version Current {
            get { var v = typeof(AutoUpdate).Assembly.GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }
        }
        internal static bool IsNewer(UpdateManifest m){ return m != null && m.Parsed != null && m.Parsed > Current; }

        // ---------- 状态（%LOCALAPPDATA%\TianGongCadSuite\update.tsv） ----------

        sealed class State {
            public DateTime LastCheck = DateTime.MinValue, SnoozeUntil = DateTime.MinValue;
            public string Skip = "", Staged = "", StagedDir = "";
        }
        static State Load(){
            var s = new State();
            try {
                foreach (var r in SettingsFile.Read(SettingsFile.PathOf(StateFile))) {
                    if (r.Length < 2) continue;
                    long t;
                    if (r[0] == "lastCheck" && long.TryParse(r[1], out t)) s.LastCheck = new DateTime(t, DateTimeKind.Utc);
                    else if (r[0] == "snoozeUntil" && long.TryParse(r[1], out t)) s.SnoozeUntil = new DateTime(t, DateTimeKind.Utc);
                    else if (r[0] == "skip") s.Skip = r[1];
                    else if (r[0] == "staged") s.Staged = r[1];
                    else if (r[0] == "stagedDir") s.StagedDir = r[1];
                }
            } catch (Exception e) { Log.Write("UpdateStateLoad", e.Message); }
            return s;
        }
        static void Save(State s){
            try {
                SettingsFile.Write(SettingsFile.PathOf(StateFile), new List<string[]>{
                    new[]{ "lastCheck", s.LastCheck.Ticks.ToString() }, new[]{ "snoozeUntil", s.SnoozeUntil.Ticks.ToString() },
                    new[]{ "skip", s.Skip }, new[]{ "staged", s.Staged }, new[]{ "stagedDir", s.StagedDir } },
                    "天工工具箱 · 自动更新状态（上次检查、跳过的版本、已下载待安装的版本）");
            } catch (Exception e) { Log.Write("UpdateStateSave", e.Message); }
        }

        // ---------- 查询 ----------

        // 联网取签名清单并验签。没有新版本 / 没发布过也算成功（返回清单或 null），连不上才给 error
        public static UpdateManifest Fetch(out string error){
            error = null;
            string url = BaseUrl;
            if (url == null) { error = "这个版本没有配置更新服务器。"; return null; }
            string text;
            try {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = TimeoutMs; req.ReadWriteTimeout = TimeoutMs;
                req.UserAgent = Product.Name + "/" + Product.Version;
                using (var res = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd();
            } catch (WebException e) {
                var res = e.Response as HttpWebResponse;
                if (res != null && res.StatusCode == HttpStatusCode.NotFound) { res.Close(); return null; }   // 服务器上还没发布过版本
                if (res != null) res.Close();
                error = "连不上更新服务器（" + e.Status + "）。";
                return null;
            } catch (Exception e) { error = "检查更新失败：" + e.Message; return null; }
            var m = Parse(text, out error);
            if (m == null && error == null) error = "更新服务器的应答无法识别。";
            return m;
        }

        // {"manifest": base64(JSON), "sig": base64} → 验签 → 清单
        internal static UpdateManifest Parse(string text, out string error){
            error = null;
            var outer = LicenseOnline.ParseJson(text);
            if (outer == null) { error = "更新服务器的应答无法识别。"; return null; }
            byte[] raw, sig;
            try { raw = Convert.FromBase64String(LicenseOnline.Text(outer, "manifest") ?? ""); sig = Convert.FromBase64String(LicenseOnline.Text(outer, "sig") ?? ""); }
            catch (FormatException) { error = "更新清单格式不对。"; return null; }
            if (raw.Length == 0 || raw.Length > 16384 || !LicenseOnline.VerifyServer(raw, sig)) {
                error = "更新清单验签失败（不是我们的服务器发的，或者被改过），不会下载。";
                return null;
            }
            var map = LicenseOnline.ParseJson(Encoding.UTF8.GetString(raw));
            if (map == null || LicenseOnline.Number(map, "v") != 1 || LicenseOnline.Text(map, "kind") != "update") { error = "更新清单格式不对。"; return null; }
            var m = new UpdateManifest {
                Version = LicenseOnline.Text(map, "version") ?? "", Date = LicenseOnline.Text(map, "date") ?? "",
                Notes = LicenseOnline.Text(map, "notes") ?? "", File = LicenseOnline.Text(map, "file") ?? "",
                Sha256 = (LicenseOnline.Text(map, "sha256") ?? "").ToLowerInvariant(), Size = LicenseOnline.Number(map, "size")
            };
            Version v;
            if (!System.Version.TryParse(m.Version, out v) || m.File.Length == 0 || m.File.IndexOfAny(new[]{ '/', '\\', ':' }) >= 0
                || m.Sha256.Length != 64 || m.Size <= 0) { error = "更新清单内容不完整。"; return null; }
            m.Parsed = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return m;
        }

        // CAD 启动时调：后台等一会儿再查（不和天工启动抢资源），12 小时最多一次；
        // 上次下载好还没装上（比如关机了等待脚本没跑完）：重新起等待脚本
        public static void StartBackgroundCheck(){
            if (BaseUrl == null) return;
            if (Interlocked.CompareExchange(ref checking, 1, 0) != 0) return;
            var t = new Thread(() => {
                try {
                    Thread.Sleep(IgnoreIntervalForTest ? 0 : 20000);
                    var s = Load();
                    ResumeStaged(s);
                    CleanOld(s);
                    if (!IgnoreIntervalForTest && (DateTime.UtcNow - s.LastCheck).TotalHours < CheckHours) return;
                    string error;
                    var m = Fetch(out error);
                    s = Load(); s.LastCheck = DateTime.UtcNow; Save(s);
                    if (error != null) { Log.Write("UpdateCheck", error); return; }
                    Log.Write("UpdateCheck", m == null ? "服务器上还没有发布版本" : ("服务器版本 " + m.Version + "，本机 " + Product.Version));
                    if (IsNewer(m)) pending = m;
                } catch (Exception e) { Log.Write("UpdateCheck", e); }
                finally { Interlocked.Exchange(ref checking, 0); }
            });
            t.IsBackground = true; t.Name = "TG-Update-Check";
            t.Start();
        }

        // 0.10.2：装上以后旧版本的下载（每个 zip + 解压出来的包，十几 MB）一直留在 %LOCALAPPDATA%，每次更新都多一份。
        // 版本号不比当前新、也不是等着装的那个 → 删掉
        static void CleanOld(State s){
            try {
                if (!Directory.Exists(UpdatesRoot)) return;
                foreach (var dir in Directory.GetDirectories(UpdatesRoot)) {
                    Version v;
                    string name = Path.GetFileName(dir);
                    if (!System.Version.TryParse(name, out v) || name == s.Staged) continue;
                    if (new Version(v.Major, v.Minor, Math.Max(0, v.Build)) > Current) continue;
                    try { Directory.Delete(dir, true); Log.Write("UpdateClean", "删掉已装过的旧下载 " + name); } catch (Exception e) { Log.Write("UpdateClean", name + "：" + e.Message); }
                }
            } catch (Exception e) { Log.Write("UpdateClean", e.Message); }
        }

        static void ResumeStaged(State s){
            Version staged;
            if (string.IsNullOrEmpty(s.Staged) || !System.Version.TryParse(s.Staged, out staged)) return;
            if (new Version(staged.Major, staged.Minor, Math.Max(0, staged.Build)) <= Current || !Directory.Exists(s.StagedDir)) {
                s.Staged = ""; s.StagedDir = ""; Save(s);     // 已经装上了（或包被删了）
                return;
            }
            if (WaiterRunning()) return;
            Log.Write("UpdateResume", "上次下载的 " + s.Staged + " 还没装上，重新等天工关闭后安装");
            string error;
            if (!LaunchWaiter(s.StagedDir, s.Staged, false, out error)) Log.Write("UpdateResume", error);
        }

        // ---------- 提示（UI 线程：用户点工具箱命令时） ----------

        // 有新版本、没被跳过、没在"稍后提醒"期内、这次开天工还没提示过 → 弹窗。
        // 返回 true = 用户点了「立即更新」，天工马上要关：调用方不要再打开命令窗口
        public static bool PromptIfDue(IWin32Window owner){
            var m = pending;
            if (m == null || promptedThisSession || !IsNewer(m)) return false;
            var s = Load();
            if (s.Skip == m.Version || DateTime.UtcNow < s.SnoozeUntil || s.Staged == m.Version) return false;
            promptedThisSession = true;
            return Show(owner, m, null) == UpdateForm.Answer.RestartNow;
        }

        // 功能区「检查更新」
        public static void CheckNowInteractive(IWin32Window owner){
            string error;
            UpdateManifest m = null;
            // 0.10.2：原来在 UI 线程上直接联网（最长 15 秒），网络慢时天工整个"未响应"。改成后台查，前台一个小窗口转着，可以关掉不等
            error = null;
            string err = null;
            var t = new Thread(() => { try { m = Fetch(out err); } catch (Exception e) { err = "检查更新失败：" + e.Message; } });
            t.IsBackground = true; t.Name = "TG-Update-CheckNow"; t.Start();
            if (!t.Join(400) && !WaitDialog(owner, t)) return;
            t.Join();
            error = err;
            var s = Load(); s.LastCheck = DateTime.UtcNow; Save(s);
            if (error != null) { MessageBox.Show(owner, error, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!IsNewer(m)) {
                MessageBox.Show(owner, "已经是最新版本（" + Product.Version + "）。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            pending = m;
            // 已经下载好了（等着关天工再装）：直接打开到"已下载"那一步，可以点「立即更新」
            bool staged = s.Staged == m.Version && Directory.Exists(s.StagedDir);
            Show(owner, m, staged ? s.StagedDir : null);
        }

        // "正在检查更新…"小窗口：线程结束自动关，返回 true；用户关掉 = 不等了，返回 false
        static bool WaitDialog(IWin32Window owner, Thread t){
            using (var f = new Form { Text = "检查更新", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                                      ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new System.Drawing.Size(340, 70), BackColor = Ui.Bg })
            using (var timer = new System.Windows.Forms.Timer { Interval = 100 }) {
                f.Controls.Add(new Label { Text = "正在连接更新服务器…（网络慢时最多 15 秒，关掉这个窗口就不等了）", Font = Ui.F9, ForeColor = Ui.Text,
                                           Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 0), TextAlign = System.Drawing.ContentAlignment.MiddleLeft });
                bool finished = false;
                timer.Tick += (s, e) => { if (!t.IsAlive) { finished = true; f.Close(); } };
                f.Shown += (s, e) => timer.Start();
                if (t.IsAlive) f.ShowDialog(owner); else finished = true;
                timer.Stop();
                return finished;
            }
        }

        static UpdateForm.Answer Show(IWin32Window owner, UpdateManifest m, string stagedPkg){
            using (var f = new UpdateForm(m, stagedPkg)) {
                f.ShowDialog(owner);
                var s = Load();
                if (f.Choice == UpdateForm.Answer.Skip) { s.Skip = m.Version; Save(s); }
                else if (f.Choice == UpdateForm.Answer.Later) { s.SnoozeUntil = DateTime.UtcNow.AddDays(1); Save(s); }
                return f.Choice;
            }
        }

        // ---------- 下载、校验、解压 ----------

        public static string UpdatesRoot { get { return Path.Combine(Log.Folder, "updates"); } }

        // 下载到 updates\<版本>\，校验大小和 SHA-256，解压到 updates\<版本>\pkg\。返回 pkg 目录；失败抛异常（消息给人看）
        public static string Download(UpdateManifest m, Action<long, long> progress, Func<bool> cancelled){
            string dir = Path.Combine(UpdatesRoot, m.Version);
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, "package.zip");
            string url = BaseUrl + "/file/" + Uri.EscapeDataString(m.File);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = TimeoutMs; req.ReadWriteTimeout = 60000;
            req.UserAgent = Product.Name + "/" + Product.Version;
            using (var sha = SHA256.Create())
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var input = res.GetResponseStream())
            using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write)) {
                var buf = new byte[1 << 16];
                long got = 0; int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0) {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException("已取消下载。");
                    got += n;
                    if (got > m.Size) throw new InvalidDataException("下载的安装包比清单里写的大，已丢弃。");
                    output.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    if (progress != null) progress(got, m.Size);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                if (got != m.Size) throw new InvalidDataException("安装包没下载完整（" + got + " / " + m.Size + " 字节），请稍后再试。");
                if (Hex(sha.Hash) != m.Sha256) throw new InvalidDataException("安装包校验不对（SHA-256 和服务器清单不一致），已丢弃，不会安装。");
            }
            string pkg = Path.Combine(dir, "pkg");
            if (Directory.Exists(pkg)) Directory.Delete(pkg, true);
            ZipFile.ExtractToDirectory(zip, pkg);      // .NET 4.6.2+：路径穿越（..\）会直接抛异常
            if (!File.Exists(Path.Combine(pkg, @"payload\TianGongCadSuite.dll")) || !File.Exists(Path.Combine(pkg, @"tools\install.ps1")))
                throw new InvalidDataException("下载的不是完整的天工工具箱安装包（缺 payload 或 install.ps1）。");
            return pkg;
        }

        // 下载好以后：起等待脚本，记下"已下载待安装"。restart = 「立即更新」：脚本先请天工关闭，装好后重新打开
        public static bool Schedule(UpdateManifest m, string pkg, out string error){ return Schedule(m, pkg, false, out error); }
        public static bool Schedule(UpdateManifest m, string pkg, bool restart, out string error){
            if (!LaunchWaiter(pkg, m.Version, restart, out error)) return false;
            var s = Load(); s.Staged = m.Version; s.StagedDir = pkg; s.SnoozeUntil = DateTime.MinValue; Save(s);
            Log.Write("UpdateStaged", m.Version + (restart ? " 立即更新：请天工关闭，装好后重新打开：" : " 已下载，等天工关闭后安装：") + pkg);
            return true;
        }

        // ---------- 等待脚本 ----------

        static string WaiterLog { get { return Path.Combine(UpdatesRoot, "update.log"); } }
        static string WaiterPidFile { get { return Path.Combine(UpdatesRoot, "waiter.pid"); } }

        static bool WaiterRunning(){
            try {
                int pid;
                if (!File.Exists(WaiterPidFile) || !int.TryParse(File.ReadAllText(WaiterPidFile).Trim(), out pid)) return false;
                var p = System.Diagnostics.Process.GetProcessById(pid);
                return !p.HasExited && p.ProcessName.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0;
            } catch { return false; }
        }

        // 等所有天工进程退出 → 运行安装包里的 install.ps1 -Force → 弹一句结果。写在 updates\apply-update.ps1
        internal const string WaiterScript =
@"param([string]$Pkg, [string]$Version, [string]$Log, [switch]$DryRun, [switch]$Restart, [string]$CadExe = '', [string]$CadName = 'TianGong', [int]$CloseWait = 300)
$ErrorActionPreference = 'Continue'
function W([string]$s){ try { Add-Content -LiteralPath $Log -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $s) -Encoding UTF8 } catch {} }
function Busy { @(Get-Process -Name $CadName -ErrorAction SilentlyContinue).Count -gt 0 }
$relaunch = $false
if ($Restart) {
    # 立即更新：请天工关闭（等于点窗口的 ×；没保存的文件天工自己会问）。5 分钟内关掉了才在装好后重新打开它
    Start-Sleep -Seconds 2
    W ('立即更新 ' + $Version + '：请天工关闭')
    Get-Process -Name $CadName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { [void]$_.CloseMainWindow() }
    $deadline = (Get-Date).AddSeconds($CloseWait)
    while ((Busy) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Seconds 1 }
    if (Busy) { W ('天工 ' + $CloseWait + ' 秒内没关（保存提示里点了取消？）：改成关闭天工后再装，装完不自动打开') } else { $relaunch = $true }
}
W ('等天工关闭后安装 ' + $Version + '：' + $Pkg)
while ($true) {
    while (Busy) { Start-Sleep -Seconds 3 }
    Start-Sleep -Seconds 3
    if (-not (Busy)) { break }
}
if (-not (Test-Path -LiteralPath (Join-Path $Pkg 'tools\install.ps1'))) { W '安装包不见了，放弃'; exit 3 }
if ($DryRun) {
    W ('DRYRUN 会运行 install.ps1 -Force：' + $Pkg)
    if ($relaunch -and $CadExe) { Start-Process -FilePath $CadExe; W ('DRYRUN 已重新打开 ' + $CadExe) }
    exit 0
}
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$app = Join-Path $env:LOCALAPPDATA 'TianGongCadSuite\app'
$out = & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Pkg 'tools\install.ps1') -LibraryPath (Join-Path $Pkg 'payload\TianGongCadSuite.dll') -StageDirectory $app -Force 2>&1
$code = $LASTEXITCODE
$out | ForEach-Object { W ('  ' + $_) }
$ok = ($code -eq 0) -and (($out | Out-String) -match '注册完成')
W ('安装结束 exit=' + $code + ' ok=' + $ok)
if ($ok -and $relaunch -and $CadExe -and (Test-Path -LiteralPath $CadExe)) {
    Start-Process -FilePath $CadExe -WorkingDirectory (Split-Path -Parent $CadExe)
    W ('已重新打开天工：' + $CadExe)
    exit 0
}
Add-Type -AssemblyName System.Windows.Forms
if ($ok) {
    [System.Windows.Forms.MessageBox]::Show('天工工具箱已更新到 ' + $Version + '。' + [Environment]::NewLine + '重新打开天工就是新版本。', '天工工具箱 更新', 'OK', 'Information') | Out-Null
} else {
    [System.Windows.Forms.MessageBox]::Show('天工工具箱 ' + $Version + ' 没有装上。' + [Environment]::NewLine + '可以手动运行：' + (Join-Path $Pkg '安装.cmd') + [Environment]::NewLine + '详细记录：' + $Log, '天工工具箱 更新', 'OK', 'Warning') | Out-Null
}
";

        static bool LaunchWaiter(string pkg, string version, bool restart, out string error){
            error = null;
            try {
                KillWaiter();   // 已经有一个在等（比如先选了"关闭天工时再装"，又点「立即更新」）：换成新的
                Directory.CreateDirectory(UpdatesRoot);
                string script = Path.Combine(UpdatesRoot, "apply-update.ps1");
                File.WriteAllText(script, WaiterScript, new UTF8Encoding(true));   // 带 BOM：Windows PowerShell 5 才按 UTF-8 读中文
                string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                string cmd = "\"" + ps + "\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\" -Pkg \"" + pkg
                           + "\" -Version \"" + version + "\" -Log \"" + WaiterLog + "\"" + (DryRunForTest ? " -DryRun" : "");
                if (restart) {
                    string exe = CadExeForTest;
                    if (exe == null) { try { exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; } catch { exe = ""; } }   // 插件就在天工进程里
                    cmd += " -Restart -CadExe \"" + exe + "\"";
                }
                if (CadNameForTest != null) cmd += " -CadName \"" + CadNameForTest + "\"";
                if (CloseWaitSecondsForTest > 0) cmd += " -CloseWait " + CloseWaitSecondsForTest;
                // 用 WMI 起进程：父进程不是天工，天工退出（或它的作业对象被关掉）时不会把等待脚本一起带走
                using (var cls = new System.Management.ManagementClass("Win32_Process")) {
                    var inParams = cls.GetMethodParameters("Create");
                    inParams["CommandLine"] = cmd;
                    inParams["CurrentDirectory"] = UpdatesRoot;
                    using (var startup = new System.Management.ManagementClass("Win32_ProcessStartup")) {
                        var si = startup.CreateInstance();
                        si["ShowWindow"] = 0;   // SW_HIDE
                        inParams["ProcessStartupInformation"] = si;
                        var outParams = cls.InvokeMethod("Create", inParams, null);
                        uint rv = Convert.ToUInt32(outParams["ReturnValue"]);
                        if (rv != 0) { error = "没能启动安装脚本（Win32_Process.Create 返回 " + rv + "）。"; return false; }
                        File.WriteAllText(WaiterPidFile, Convert.ToString(outParams["ProcessId"]));
                    }
                }
                return true;
            } catch (Exception e) { error = "没能启动安装脚本：" + e.Message; Log.Write("UpdateWaiter", e); return false; }
        }

        static void KillWaiter(){
            try {
                int pid;
                if (!File.Exists(WaiterPidFile) || !int.TryParse(File.ReadAllText(WaiterPidFile).Trim(), out pid)) return;
                var p = System.Diagnostics.Process.GetProcessById(pid);
                if (!p.HasExited && p.ProcessName.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0) { p.Kill(); p.WaitForExit(3000); }
            } catch { }
        }

        static string Hex(byte[] data){
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // 测试用：后台查到的新版本、这次开天工是否提示过
        internal static UpdateManifest PendingForTest { get { return pending; } set { pending = value; promptedThisSession = false; } }
        internal static string WaiterLogPath { get { return WaiterLog; } }
    }
}
