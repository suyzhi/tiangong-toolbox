using System;
using System.Drawing;
using Microsoft.Win32;

namespace TianGongCadSuite {
    // 工具窗口的界面偏好（位置大小、是否自动收起、卡片折叠……），按窗口分开存在
    // HKCU\Software\TianGongCadSuite\WindowLayout\<窗口> 下。
    // 读写失败一律当"没存过"，只记日志 —— 界面偏好绝不能影响命令本身。
    public static class WindowLayoutStore {
        const string Root = @"Software\TianGongCadSuite\WindowLayout\";

        public static bool ReadFlag(string window, string name, bool fallback){
            try {
                using (var k = Registry.CurrentUser.OpenSubKey(Root + window)) {
                    object v = k == null ? null : k.GetValue(name);
                    return v == null ? fallback : Convert.ToInt32(v) != 0;
                }
            } catch (Exception e) { Log.Write("WindowLayout", window + "." + name + " 读取失败：" + e.Message); return fallback; }
        }

        public static void WriteFlag(string window, string name, bool value){
            try {
                using (var k = Registry.CurrentUser.CreateSubKey(Root + window))
                    if (k != null) k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            } catch (Exception e) { Log.Write("WindowLayout", window + "." + name + " 保存失败：" + e.Message); }
        }

        public static bool TryReadBounds(string window, out Rectangle bounds){
            bounds = Rectangle.Empty;
            try {
                using (var k = Registry.CurrentUser.OpenSubKey(Root + window))
                    return k != null && ToolWindowPlacement.TryParse(k.GetValue("Bounds") as string, out bounds);
            } catch (Exception e) { Log.Write("WindowLayout", window + ".Bounds 读取失败：" + e.Message); return false; }
        }

        public static void WriteBounds(string window, Rectangle bounds){
            try {
                using (var k = Registry.CurrentUser.CreateSubKey(Root + window))
                    if (k != null) k.SetValue("Bounds", ToolWindowPlacement.Format(bounds), RegistryValueKind.String);
            } catch (Exception e) { Log.Write("WindowLayout", window + ".Bounds 保存失败：" + e.Message); }
        }

        // 测试用：删掉某个窗口的全部偏好。
        public static void Forget(string window){
            try { Registry.CurrentUser.DeleteSubKeyTree(Root + window, false); } catch { }
        }
    }
}
