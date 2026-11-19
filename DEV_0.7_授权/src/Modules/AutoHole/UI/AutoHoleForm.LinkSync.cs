using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

namespace TianGongCadSuite {
    public sealed partial class AutoHoleForm {
        // ---------------- 关联孔位 ----------------
        const string NotScanned = "点「检查」查看本装配里的关联孔（要遍历全部零件，所以不自动查）";

        void ShowLinkReport(SyncReport report, string fallback){
            lastLinks = report;
            if (report == null) {
                linkLabel.Text = fallback;
                linkLabel.ForeColor = Ui.Muted;
                // 0.7.8：原生关联的孔靠「同步孔位」里的"更新链接"，所以不用先检查也能点
                syncButton.Enabled = assembly != null && !busy;
                syncButton.Text = "同步孔位";
                rescanButton.Enabled = assembly != null;
                return;
            }
            linkLabel.Text = report.Summary;
            linkLabel.ForeColor = report.Misplaced > 0 ? Ui.Warn : (report.Broken > 0 ? Ui.Warn : Ui.Text);
            syncButton.Enabled = assembly != null && !busy;
            syncButton.Text = report.Misplaced > 0 ? "同步孔位 (" + report.Misplaced + ")" : "同步孔位";
            rescanButton.Enabled = !busy;
            var detail = new List<string>();
            foreach (var it in report.Items) {
                if (it.NeedsMove) detail.Add(it.Label + "：偏了 " + it.OffsetMm.ToString("0.###", CultureInfo.InvariantCulture) + "mm");
                else if (it.Problem.Length > 0) detail.Add(it.Label + "：" + it.Problem);
                if (detail.Count >= 12) { detail.Add("……"); break; }
            }
            tips.SetToolTip(linkLabel, detail.Count == 0 ? report.Summary : string.Join("\r\n", detail.ToArray()));
        }

        // 查一遍装配里的关联孔。manual = 用户点了「重新检查」（要在状态栏给回应）。
        // 查关联孔要把装配里每个零件加载一遍（大装配一两分钟）：期间刷状态栏、泵消息，别让 Windows 把天工判成"未响应"。
        // 泵消息时窗口按 busy 挡住关闭和改列表（IBusyTool），所以查的时候也要置 busy（原来「检查」不置 busy）
        Action<int, int> LinkPulse(){
            var tick = System.Diagnostics.Stopwatch.StartNew();
            return (i, n) => {
                if (tick.ElapsedMilliseconds < 300) return;
                tick.Restart();
                SetStatus("正在读取装配里的零件 " + i + " / " + n + " …", Ui.Accent);
                try { status.Update(); System.Windows.Forms.Application.DoEvents(); } catch { }
            };
        }
        // 窗口开着时装配被关掉了：关联孔检查 / 同步原来报一句看不懂的"对象已断开"
        bool AssemblyAlive(){
            try { var nm = assembly.Name; return true; }
            catch { SetStatus("打开这个窗口时的装配已经关掉了。请关掉这个窗口，在装配里重新点「自动打孔」。", Ui.Bad); return false; }
        }
        void ScanLinks(bool manual){
            if (assembly == null || closing) return;
            if (busy) return;
            if (!AssemblyAlive()) return;
            try {
                UseWaitCursor = true; busy = true; rescanButton.Enabled = false; syncButton.Enabled = false; run.Enabled = false;
                var report = HoleSync.Scan(assembly, LinkPulse());
                busy = false;
                ShowLinkReport(report, "");
                if (manual) SetStatus(report.Summary, report.Misplaced > 0 ? Ui.Warn : Ui.Ok);
            } catch (Exception e) {
                busy = false;
                Log.Write("AutoHoleLinkScan", e);
                ShowLinkReport(null, "检查失败：" + AutoHoleWriter.Friendly(e));
            } finally { busy = false; UseWaitCursor = false; RefreshPreview(); }
        }

        // 一键同步：先重新查一遍（别拿着几分钟前的结果去改模型），再把错位的孔挪回去。
        void SyncLinks(){
            if (assembly == null || busy || closing) return;
            if (!AssemblyAlive()) return;
            hadCommand = command != null;
            try {
                busy = true; UseWaitCursor = true; syncButton.Enabled = false; run.Enabled = false;
                SetStatus("正在同步孔位…", Ui.Accent);
                StopCommand();
                // 天工原生关联的孔：让天工更新链接（= 天工里「更新所有链接」），孔自己跟上参考孔
                try { NativeHoleLink.UpdateLinks(assembly); } catch (Exception e) { Log.Write("AutoHoleUpdateLinks", e); }
                var report = HoleSync.Scan(assembly, LinkPulse());
                int want = report.Misplaced;
                string native = report.NativeLinks > 0
                    ? "天工关联：已更新 " + report.NativeLinks + " 个孔位链接" + (report.NativeBroken > 0 ? "，其中 " + report.NativeBroken + " 个断了（参考零件被删掉或换掉了）" : "")
                    : "";
                if (want == 0) {
                    ShowLinkReport(report, "");
                    string ok = (native.Length > 0 ? native + "；" : "已让天工更新链接；")
                              + (report.Linked > 0 ? "插件关联的 " + report.Linked + " 个孔都已和参考孔同轴" : "没有插件关联的孔")
                              + (report.Broken > 0 ? "（" + report.Broken + " 个找不到参考孔）" : "") + "。";
                    LastResult = ok;
                    SetStatus(ok, report.NativeBroken > 0 || report.Broken > 0 ? Ui.Warn : Ui.Ok);
                    if (report.NativeBroken > 0 && !SuppressDialogs)
                        MessageBox.Show(this, ok + "\r\n\r\n断了的孔位链接：\r\n· " + string.Join("\r\n· ", report.NativeBrokenLabels.Take(12).ToArray())
                                        + "\r\n\r\n这些孔不会再跟着参考孔走。在天工特征树里看这个链接的状态；不要了就删掉它所在的「关联孔 …」组重新打。",
                                        "同步孔位", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                } else {
                    HoleSync.Apply(report);
                    string msg = "同步孔位：挪回 " + report.Moved + " 个孔" + (report.Failed > 0 ? "，失败 " + report.Failed + " 个" : "") + "。";
                    if (native.Length > 0) msg += "\r\n" + native + "。";
                    if (report.NativeBrokenLabels.Count > 0) msg += "\r\n断了的孔位链接：" + string.Join("；", report.NativeBrokenLabels.Take(8).ToArray());
                    if (report.Notes.Count > 0) msg += "\r\n\r\n说明：\r\n· " + string.Join("\r\n· ", report.Notes.ToArray());
                    msg += "\r\n\r\n请保存装配。";
                    LastResult = msg;
                    if (!SuppressDialogs)
                        MessageBox.Show(this, msg, "同步孔位", MessageBoxButtons.OK, report.Failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                    SetStatus("同步完成：挪回 " + report.Moved + " 个孔" + (report.Failed > 0 ? "，失败 " + report.Failed + " 个" : "") + "。",
                              report.Failed > 0 ? Ui.Warn : Ui.Ok);
                    // 模型改过了：打孔面的面片缓存全部作废，下次用到时重读
                    resolved.Clear();
                    busy = false;
                    // Apply 已经把挪好的孔在报告里改成"同轴"：直接显示，不再整装配重扫一遍（建实例索引要把每个零件加载一遍，大装配上一分多钟）
                    ShowLinkReport(report, "");
                }
            } catch (Exception e) {
                Log.Write("AutoHoleSync", e);
                SetStatus("同步孔位失败：" + AutoHoleWriter.Friendly(e), Ui.Bad);
                LastResult = "同步中断：" + e.Message;
                if (!SuppressDialogs) MessageBox.Show(this, AutoHoleWriter.Friendly(e), "同步孔位", MessageBoxButtons.OK, MessageBoxIcon.Error);
            } finally {
                busy = false; UseWaitCursor = false;
                if (hadCommand) { try { StartPicking(); } catch (Exception ex) { SetStatus(AutoHoleWriter.Friendly(ex), Ui.Warn); } }
                if (lastLinks != null) ShowLinkReport(lastLinks, "");
                RefreshPreview();
            }
        }
    }
}
