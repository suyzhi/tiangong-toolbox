using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TianGongCadSuite;
using TianGongCadSuite.Licensing;

namespace PanelTests {
    // 自动更新（0.9.1）：
    //   --update            纯逻辑：地址推导、版本比较、签名清单（真签、篡改、冒充、格式）；「发现新版本」窗口截图
    //   --update-live <url> <LicenseServerSlot.cs> <截图/输出目录>
    //                       打本机起的 server/tg_license_server.py：取清单 → 下载 → 校验 → 解压 → 起等待脚本（只写日志不装）
    //                       本机真实的 update.tsv / updates 目录先拍快照、跑完放回
    //   --update-live <url> <slot> <dir> --real
    //                       真装：等待脚本真的运行安装包的 install.ps1 -Force（天工关着时马上装），用来给本机装正式版、验证整条更新链路
    internal static class UpdateTests {
        internal static void Run(Action<string, bool> check, string shotDir){
            check("更新地址跟着授权地址走", AutoUpdate.UpdateUrlFor("https://license.example.com/api/v1/license") == "https://license.example.com/api/v1/update");
            check("没配授权服务器 = 不查更新", AutoUpdate.UpdateUrlFor("") == null && AutoUpdate.UpdateUrlFor(null) == null);

            using (var key = new ECDsaCng(256)) {
                LicenseTestHooks.ServerKeyOverride = key.Key.Export(CngKeyBlobFormat.EccPublicBlob);
                try {
                    string error;
                    var m = AutoUpdate.Parse(Signed(key, Manifest("99.0.0", "天工工具箱_DEV99.0.0_安装包_20300101.zip")), out error);
                    check("签名清单验签通过（" + error + "）", m != null && m.Version == "99.0.0" && m.Size == 1234 && m.Notes == "修复 A\n修复 B");
                    check("比本机新", AutoUpdate.IsNewer(m));
                    var old = AutoUpdate.Parse(Signed(key, Manifest("0.1.0", "a.zip")), out error);
                    check("比本机旧的不算新版本", old != null && !AutoUpdate.IsNewer(old));
                    check("同版本不算新版本", !AutoUpdate.IsNewer(AutoUpdate.Parse(Signed(key, Manifest(AutoUpdate.Current.ToString(3), "a.zip")), out error)));

                    // 篡改：改清单里的版本号，签名对不上
                    string good = Signed(key, Manifest("99.0.0", "a.zip"));
                    var outer = LicenseOnline.ParseJson(good);
                    string raw = Encoding.UTF8.GetString(Convert.FromBase64String(LicenseOnline.Text(outer, "manifest"))).Replace("99.0.0", "99.0.1");
                    string tampered = "{\"manifest\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)) + "\",\"sig\":\"" + LicenseOnline.Text(outer, "sig") + "\"}";
                    check("改过的清单不认", AutoUpdate.Parse(tampered, out error) == null && error.IndexOf("验签") >= 0);
                    using (var other = new ECDsaCng(256))
                        check("别的服务器签的清单不认", AutoUpdate.Parse(Signed(other, Manifest("99.0.0", "a.zip")), out error) == null && error.IndexOf("验签") >= 0);
                    check("授权回执冒充清单不认（没有 kind=update）",
                          AutoUpdate.Parse(Signed(key, "{\"v\":1,\"code\":\"abcdefgh\",\"status\":\"ok\"}"), out error) == null);
                    check("文件名带路径的清单不认", AutoUpdate.Parse(Signed(key, Manifest("99.0.0", "..\\evil.zip")), out error) == null);
                    check("乱码应答不认", AutoUpdate.Parse("<html>", out error) == null);

                    if (shotDir != null) Shots(m, shotDir, check);
                } finally { LicenseTestHooks.ServerKeyOverride = null; }
            }
        }

        static string Manifest(string version, string file){
            return "{\"v\":1,\"kind\":\"update\",\"version\":\"" + version + "\",\"date\":\"2030-01-01\",\"notes\":\"修复 A\\n修复 B\",\"file\":\""
                 + file.Replace("\\", "\\\\") + "\",\"size\":1234,\"sha256\":\"" + new string('a', 64) + "\"}";
        }
        static string Signed(ECDsaCng key, string json){
            byte[] raw = Encoding.UTF8.GetBytes(json);
            byte[] sig = key.SignData(raw, HashAlgorithmName.SHA256);
            return "{\"manifest\":\"" + Convert.ToBase64String(raw) + "\",\"sig\":\"" + Convert.ToBase64String(sig) + "\"}";
        }

        // 「发现新版本」窗口：初始 / 下载完 / 下载失败 三张截图
        static void Shots(UpdateManifest m, string dir, Action<string, bool> check){
            Directory.CreateDirectory(dir);
            Anim.ForceOff = true;
            m.Notes = "自动打孔：参考孔页点面 = 认面上的孔，打孔面页点面 = 打孔面，点了不再自动翻页\n自动打孔：点面连腰孔一起认\n修复：方管上下两壁打穿的孔被当成凸台跳过";
            m.Size = 1567759;
            using (var f = new UpdateForm(m)) {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(40, 40);
                f.Show(); Application.DoEvents();
                Shot(f, Path.Combine(dir, "update-1-found.png"));
                f.FinishForTest(@"C:\x\pkg", null);
                Shot(f, Path.Combine(dir, "update-2-done.png"));
                check("下载完：主按钮「立即更新」、次按钮「关闭天工时再装」", FindButton(f, "立即更新") != null && FindButton(f, "关闭天工时再装") != null);
                f.FinishForTest(null, "连不上更新服务器（ConnectFailure）。");
                Shot(f, Path.Combine(dir, "update-3-failed.png"));
                check("下载失败：主按钮变「重试」", FindButton(f, "重试") != null);
                f.Close();
            }
        }
        static Button FindButton(Control root, string text){
            foreach (Control c in root.Controls) {
                var b = c as Button; if (b != null && b.Text == text && b.Visible) return b;
                var r = FindButton(c, text); if (r != null) return r;
            }
            return null;
        }
        static void Shot(Form f, string path){
            f.Refresh(); Application.DoEvents();
            var r = f.Bounds;
            using (var bmp = new Bitmap(r.Width, r.Height))
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc(); PrintWindow(f.Handle, hdc, 2); g.ReleaseHdc(hdc);
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("SHOT " + path);
        }
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        // ---------- 「立即更新」：请天工关闭 → 装 → 重新打开（用假天工，不碰真的天工） ----------
        // --update-restart <TianGongFake.exe>
        // --update-restart-cad <TianGongFake.exe>：真天工（排队脚本在私有桌面上起的、没有打开文件）收到关闭请求会不会退出；
        //   "重新打开"换成假天工，不在用户桌面上真开一个天工
        internal static void RunRestartRealCad(Action<string, bool> check, string fakeExe){
            string dir = Path.GetDirectoryName(Path.GetFullPath(fakeExe));
            string pkg = Path.Combine(dir, "pkg");
            Directory.CreateDirectory(Path.Combine(pkg, "tools"));
            File.WriteAllText(Path.Combine(pkg, @"tools\install.ps1"), "# 假安装包");
            string state = SettingsFile.PathOf("update.tsv");
            byte[] savedState = File.Exists(state) ? File.ReadAllBytes(state) : null;
            bool hadRoot = Directory.Exists(AutoUpdate.UpdatesRoot);
            var before = new HashSet<string>(hadRoot ? Directory.GetFileSystemEntries(AutoUpdate.UpdatesRoot) : new string[0], StringComparer.OrdinalIgnoreCase);
            AutoUpdate.DryRunForTest = true; AutoUpdate.CadExeForTest = Path.GetFullPath(fakeExe); AutoUpdate.CloseWaitSecondsForTest = 90;
            try {
                int cads = System.Diagnostics.Process.GetProcessesByName("TianGong").Length;
                check("真天工：开着（" + cads + " 个进程）", cads > 0);
                string error, log = AutoUpdate.WaiterLogPath;
                if (File.Exists(log)) File.Delete(log);
                bool ok = AutoUpdate.Schedule(new UpdateManifest { Version = "9.9.9" }, pkg, true, out error);
                check("真天工：起了等待脚本（" + error + "）", ok);
                string text = "";
                for (int i = 0; i < 300 && text.IndexOf("已重新打开") < 0 && text.IndexOf("没关") < 0; i++) { Thread.Sleep(500); try { text = File.ReadAllText(log); } catch { } }
                Console.WriteLine("   update.log: " + text.Trim().Replace("\r\n", " ⏎ "));
                check("真天工：收到关闭请求后退出了（没有打开的文件时不弹保存提示）", System.Diagnostics.Process.GetProcessesByName("TianGong").Length == 0 && text.IndexOf("没关") < 0);
                check("真天工：装完（DRYRUN）后走到重新打开那一步", text.IndexOf("已重新打开") >= 0);
            } finally {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fakeExe))) { try { p.Kill(); } catch { } }
                AutoUpdate.DryRunForTest = false; AutoUpdate.CadExeForTest = null; AutoUpdate.CloseWaitSecondsForTest = 0;
                if (Directory.Exists(AutoUpdate.UpdatesRoot))
                    foreach (var e in Directory.GetFileSystemEntries(AutoUpdate.UpdatesRoot))
                        if (!before.Contains(e)) { try { if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e); } catch { } }
                if (!hadRoot) { try { Directory.Delete(AutoUpdate.UpdatesRoot, true); } catch { } }
                if (savedState != null) File.WriteAllBytes(state, savedState); else if (File.Exists(state)) File.Delete(state);
            }
        }

        internal static void RunRestart(Action<string, bool> check, string fakeExe){
            string dir = Path.GetDirectoryName(Path.GetFullPath(fakeExe));
            string starts = Path.Combine(dir, "fake-starts.log");
            string pkg = Path.Combine(dir, "pkg");
            Directory.CreateDirectory(Path.Combine(pkg, "tools"));
            File.WriteAllText(Path.Combine(pkg, @"tools\install.ps1"), "# 假安装包");
            string state = SettingsFile.PathOf("update.tsv");
            byte[] savedState = File.Exists(state) ? File.ReadAllBytes(state) : null;
            bool hadRoot = Directory.Exists(AutoUpdate.UpdatesRoot);
            var before = new HashSet<string>(hadRoot ? Directory.GetFileSystemEntries(AutoUpdate.UpdatesRoot) : new string[0], StringComparer.OrdinalIgnoreCase);
            AutoUpdate.DryRunForTest = true;
            AutoUpdate.CadNameForTest = Path.GetFileNameWithoutExtension(fakeExe);
            AutoUpdate.CadExeForTest = Path.GetFullPath(fakeExe);
            AutoUpdate.CloseWaitSecondsForTest = 6;
            var m = new UpdateManifest { Version = "9.9.9" };
            try {
                // (a) 假天工收到关闭就退出 → 装（DRYRUN）→ 重新打开
                if (File.Exists(starts)) File.Delete(starts);
                var fake = System.Diagnostics.Process.Start(fakeExe);
                Thread.Sleep(1500);
                string error, log = AutoUpdate.WaiterLogPath;
                if (File.Exists(log)) File.Delete(log);
                bool ok = AutoUpdate.Schedule(m, pkg, true, out error); check("立即更新：起了等待脚本（" + error + "）", ok);
                string text = "";
                for (int i = 0; i < 60 && text.IndexOf("DRYRUN 已重新打开") < 0; i++) { Thread.Sleep(500); try { text = File.ReadAllText(log); } catch { } }
                Console.WriteLine("   update.log: " + text.Trim().Replace("\r\n", " ⏎ "));
                check("立即更新：天工收到关闭请求、退出了", fake.HasExited);
                check("立即更新：装完（DRYRUN）后重新打开了天工", text.IndexOf("DRYRUN 已重新打开") >= 0);
                Thread.Sleep(1500);
                var lines = File.Exists(starts) ? File.ReadAllLines(starts) : new string[0];
                check("立即更新：假天工启动了两次（原来那次 + 重新打开），实 " + lines.Length, lines.Length == 2);
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(AutoUpdate.CadNameForTest)) { try { p.Kill(); } catch { } }

                // (b) 用户在保存提示里点了取消（假天工拒绝关闭）→ 退回"关闭时再装"，装完不重新打开
                File.Delete(starts); File.Delete(log);
                var stubborn = System.Diagnostics.Process.Start(fakeExe, "--refuse");
                Thread.Sleep(1500);
                check("取消关闭：起了等待脚本", AutoUpdate.Schedule(m, pkg, true, out error));
                text = "";
                for (int i = 0; i < 40 && text.IndexOf("没关") < 0; i++) { Thread.Sleep(500); try { text = File.ReadAllText(log); } catch { } }
                check("取消关闭：天工没关，脚本说改成关闭后再装", text.IndexOf("没关") >= 0 && !stubborn.HasExited);
                stubborn.Kill(); stubborn.WaitForExit(3000);       // 用户过了一阵自己关掉天工
                for (int i = 0; i < 40 && text.IndexOf("DRYRUN 会运行") < 0; i++) { Thread.Sleep(500); try { text = File.ReadAllText(log); } catch { } }
                Thread.Sleep(2000);
                try { text = File.ReadAllText(log); } catch { }
                Console.WriteLine("   update.log: " + text.Trim().Replace("\r\n", " ⏎ "));
                check("取消关闭：天工关掉后照样装（DRYRUN）", text.IndexOf("DRYRUN 会运行") >= 0);
                check("取消关闭：装完不会突然把天工打开", text.IndexOf("已重新打开") < 0 && File.ReadAllLines(starts).Length == 1);
            } finally {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(AutoUpdate.CadNameForTest)) { try { p.Kill(); } catch { } }
                AutoUpdate.DryRunForTest = false; AutoUpdate.CadNameForTest = null; AutoUpdate.CadExeForTest = null; AutoUpdate.CloseWaitSecondsForTest = 0;
                if (Directory.Exists(AutoUpdate.UpdatesRoot))
                    foreach (var e in Directory.GetFileSystemEntries(AutoUpdate.UpdatesRoot))
                        if (!before.Contains(e)) { try { if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e); } catch { } }
                if (!hadRoot) { try { Directory.Delete(AutoUpdate.UpdatesRoot, true); } catch { } }
                if (savedState != null) File.WriteAllBytes(state, savedState); else if (File.Exists(state)) File.Delete(state);
            }
        }

        // ---------- 真服务器 ----------

        internal static void RunLive(Action<string, bool> check, string url, string slotPath, string outDir, string tamperZip){
            bool real = tamperZip == "--real";
            if (real) tamperZip = null;
            string slot = File.ReadAllText(slotPath);
            LicenseTestHooks.ServerKeyOverride = Blob(Const(slot, "KeyX"), Const(slot, "KeyY"));
            LicenseTestHooks.ServerUrlOverride = url;
            AutoUpdate.DryRunForTest = !real;
            string state = SettingsFile.PathOf("update.tsv");
            byte[] savedState = File.Exists(state) ? File.ReadAllBytes(state) : null;
            bool hadRoot = Directory.Exists(AutoUpdate.UpdatesRoot);
            var before = new HashSet<string>(hadRoot ? Directory.GetFileSystemEntries(AutoUpdate.UpdatesRoot) : new string[0], StringComparer.OrdinalIgnoreCase);
            try {
                string error;
                var m = AutoUpdate.Fetch(out error);
                check("真服务器：取到签名清单（" + (error ?? (m == null ? "没发布" : m.Version)) + "）", m != null && error == null);
                if (!real) check("真服务器：清单版本比本机新（" + m.Version + " > " + AutoUpdate.Current + "）", AutoUpdate.IsNewer(m));
                else Console.WriteLine("   清单 " + m.Version + "（" + m.Date + "，" + m.SizeText + "）：" + m.Notes.Replace("\n", " ⏎ "));
                long last = 0; int calls = 0;
                string pkg = AutoUpdate.Download(m, (got, total) => { last = got; calls++; }, () => false);
                check("真服务器：下载完、大小对、SHA-256 对（" + last + " 字节，进度回调 " + calls + " 次）", last == m.Size && calls > 0);
                check("真服务器：解压出 payload 和 install.ps1", File.Exists(Path.Combine(pkg, @"payload\TianGongCadSuite.dll")) && File.Exists(Path.Combine(pkg, @"tools\install.ps1")));
                string log = AutoUpdate.WaiterLogPath;
                if (File.Exists(log)) File.Delete(log);
                bool scheduled = AutoUpdate.Schedule(m, pkg, out error);
                check("真服务器：起了等待脚本（" + error + "）", scheduled);
                bool cad = System.Diagnostics.Process.GetProcessesByName("TianGong").Length > 0;
                if (cad) {
                    Console.WriteLine("SKIP: 本机天工开着，等待脚本会一直等它关掉 —— 不等 DRYRUN 行（关掉天工后看 " + log + "）");
                } else {
                    string text = "", want = real ? "安装结束" : "DRYRUN";
                    for (int i = 0; i < (real ? 360 : 60) && text.IndexOf(want) < 0; i++) { Thread.Sleep(500); try { text = File.ReadAllText(log); } catch { } }
                    if (real) check("真装：等待脚本运行 install.ps1 -Force 成功（" + (text.IndexOf("ok=True") >= 0 ? "ok" : "看 update.log") + "）", text.IndexOf("ok=True") >= 0);
                    else check("真服务器：等待脚本发现天工没开，走到安装那一步（DRYRUN 只写日志）", text.IndexOf("DRYRUN") >= 0);
                    Console.WriteLine("   update.log: " + text.Trim().Replace("\r\n", " ⏎ "));
                }
                // 服务器上的安装包被人换了（清单没变）：下载完校验不过，不装
                if (tamperZip != null) {
                    File.AppendAllText(tamperZip, "x");
                    bool refused = false;
                    try { AutoUpdate.Download(m, null, () => false); } catch (InvalidDataException e) { refused = true; Console.WriteLine("   被换过的包：" + e.Message); }
                    check("真服务器：安装包被换过 → 校验不过、拒绝安装", refused);
                }
            } finally {
                LicenseTestHooks.ServerKeyOverride = null; LicenseTestHooks.ServerUrlOverride = null;
                AutoUpdate.DryRunForTest = false;
                // 只删这次测试新建的东西，原样放回状态文件
                if (Directory.Exists(AutoUpdate.UpdatesRoot))
                    foreach (var e in Directory.GetFileSystemEntries(AutoUpdate.UpdatesRoot))
                        if (!before.Contains(e)) { try { if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e); } catch { } }
                if (!hadRoot) { try { Directory.Delete(AutoUpdate.UpdatesRoot, true); } catch { } }
                if (savedState != null) File.WriteAllBytes(state, savedState); else if (File.Exists(state)) File.Delete(state);
            }
        }
        static string Const(string source, string name){
            var mt = System.Text.RegularExpressions.Regex.Match(source, name + @"\s*=\s*""([0-9a-fA-F]*)""");
            return mt.Success ? mt.Groups[1].Value : "";
        }
        static byte[] Blob(string x, string y){
            byte[] blob = new byte[72];
            blob[0] = 0x45; blob[1] = 0x43; blob[2] = 0x53; blob[3] = 0x31; blob[4] = 32;
            for (int i = 0; i < 32; i++) { blob[8 + i] = Convert.ToByte(x.Substring(i * 2, 2), 16); blob[40 + i] = Convert.ToByte(y.Substring(i * 2, 2), 16); }
            return blob;
        }
    }
}
