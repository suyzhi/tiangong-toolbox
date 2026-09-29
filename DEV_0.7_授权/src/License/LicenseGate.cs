using System;
using System.Windows.Forms;

namespace TianGongCadSuite.Licensing {
    // 业务栅栏。分散在多个模块入口，任何一处被绕开都不影响其余处生效。
    // 返回值与提示刻意不提"授权码算法"，避免成为破解时的路标。
    internal static class LicenseGate {
        internal static bool Pass(){
            try{
#if TG_DEV_BUILD
                if(LicenseTestHooks.GateOverride == 0)return true;
                if(LicenseTestHooks.GateOverride > 0)return false;
#endif
                if(!LicenseLibrary.Gate())return false;
                if(LicenseStore.GhostDetected())return false;
                return true;
            }catch(Exception){
                return false;
            }
        }

        // 深层调用点使用：失败与"模型操作失败"完全同形，不给破解者留下定位线索。
        internal static void Require(){
            if(!Pass())throw new InvalidOperationException("模型操作未能完成，请重试或检查当前选择。");
        }

        // 命令入口调用：不通过就弹激活窗口，返回 false 让上层直接收工。
        internal static bool EnsureInteractive(IWin32Window owner){
            if(Pass())return true;
            LicenseReport report = LicenseLibrary.Current();
            if(report.Status == LicenseStatus.Expired && !string.IsNullOrEmpty(report.PlanName)){
                MessageBox.Show("该授权已于 " + LicenseTime.Format(report.ExpiryDate) + " 到期，请输入新的激活码。",
                    "天工工具箱", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }else if(report.Status == LicenseStatus.WrongMachine){
                MessageBox.Show("本机激活记录属于另一台机器（或硬件已更换），无法在本机使用。\r\n请联系管理员重新发一个激活码；本机机器码：" + report.MachineCode + "（仅供售后核对）。",
                    "天工工具箱", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }else if(report.Status == LicenseStatus.ClockTampered || report.Status == LicenseStatus.Environment
                || report.Status == LicenseStatus.OnlineRequired || report.Status == LicenseStatus.MovedAway || report.Status == LicenseStatus.Revoked){
                MessageBox.Show(report.Describe(), "天工工具箱", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return LicenseActivation.Show(owner);
        }

        internal static void ReportStatus(){
            LicenseReport report = LicenseLibrary.Current();
            MessageBox.Show(report.Describe() + "\r\n本机机器码：" + report.MachineCode, "授权状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
