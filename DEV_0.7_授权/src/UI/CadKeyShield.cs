using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 线程级 WH_GETMESSAGE 快捷键护盾：
    // 天工 CAD 主框架的 TranslateAccelerator 会在 WinForms 窗口收到按键前截走 Ctrl+C / Ctrl+V / Ctrl+X / Ctrl+Z / Ctrl+Y / Delete 等键，
    // 导致无模态工具窗里的表格复制粘贴、文本框粘贴路径或撤销失灵。
    // 本类将 0.13.2 在出图清单工作台验证有效的护盾抽成公共组件，供全插件所有无模态窗口复用。
    public sealed class CadKeyShield : IDisposable {
        const int WH_GETMESSAGE = 3;
        const uint WM_NULL = 0x0000;
        const uint WM_KEYDOWN = 0x0100;
        const uint WM_CUT = 0x0300;
        const uint WM_COPY = 0x0301;
        const uint WM_PASTE = 0x0302;
        const uint EM_SETSEL = 0x00B1;
        const uint EM_UNDO = 0x00C7;
        const int VK_SHIFT = 0x10;
        const int VK_CONTROL = 0x11;
        const int VK_MENU = 0x12;

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        struct Win32Msg {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        readonly Form host;
        readonly Func<bool> isBusy;
        readonly Func<Keys, Action> resolveShortcut;
        readonly HookProc hookDelegate;
        readonly List<Control> secondaries = new List<Control>();
        IntPtr hookHandle = IntPtr.Zero;
        bool disposed;

        // resolveShortcut：仅在非文本编辑控件（或显式注册了 Ctrl+F / Ctrl+H 等导航键）时调用；返回非空 Action 即吞掉按键并异步执行。
        public CadKeyShield(Form hostForm, Func<bool> busyCheck = null, Func<Keys, Action> shortcutResolver = null) {
            if (hostForm == null) throw new ArgumentNullException("hostForm");
            host = hostForm;
            isBusy = busyCheck;
            resolveShortcut = shortcutResolver;
            hookDelegate = OnGetMessage;

            host.HandleCreated += OnHostHandleCreated;
            host.HandleDestroyed += OnHostHandleDestroyed;
            host.FormClosed += OnHostFormClosed;
            if (host.IsHandleCreated) Install();
        }

        // 附加属于同一逻辑窗口的浮层控件（例如列筛选 ToolStripDropDown），使其内部输入框也受护盾保护
        public void AttachPopup(Control popup) {
            if (popup == null) return;
            if (!secondaries.Contains(popup)) secondaries.Add(popup);
        }

        public void DetachPopup(Control popup) {
            if (popup != null) secondaries.Remove(popup);
        }

        void OnHostHandleCreated(object sender, EventArgs e) { Install(); }
        void OnHostHandleDestroyed(object sender, EventArgs e) { Uninstall(); }
        void OnHostFormClosed(object sender, FormClosedEventArgs e) { Dispose(); }

        void Install() {
            if (disposed || hookHandle != IntPtr.Zero) return;
            try { hookHandle = SetWindowsHookEx(WH_GETMESSAGE, hookDelegate, IntPtr.Zero, GetCurrentThreadId()); } catch { }
        }

        void Uninstall() {
            if (hookHandle == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(hookHandle); } catch { }
            hookHandle = IntPtr.Zero;
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            Uninstall();
            secondaries.Clear();
            try {
                host.HandleCreated -= OnHostHandleCreated;
                host.HandleDestroyed -= OnHostHandleDestroyed;
                host.FormClosed -= OnHostFormClosed;
            } catch { }
        }

        static bool IsWin32EditControl(IntPtr hwnd) {
            if (hwnd == IntPtr.Zero) return false;
            var ctl = Control.FromHandle(hwnd);
            if (ctl is TextBoxBase) return true;
            var sb = new StringBuilder(32);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0) {
                return string.Equals(sb.ToString(), "EDIT", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        bool IsInsidePopup(IntPtr hwnd) {
            for (int i = secondaries.Count - 1; i >= 0; i--) {
                var c = secondaries[i];
                if (c == null || c.IsDisposed) { secondaries.RemoveAt(i); continue; }
                if (!c.IsHandleCreated) continue;
                IntPtr ph = c.Handle;
                if (hwnd == ph || IsChild(ph, hwnd)) return true;
            }
            return false;
        }

        IntPtr OnGetMessage(int nCode, IntPtr wParam, IntPtr lParam) {
            try {
                // wParam == 1 (PM_REMOVE)：只拦截真正从队列取出的按键，避免 PeekMessage(PM_NOREMOVE) 触发两次
                if (nCode >= 0 && wParam == (IntPtr)1 && !disposed && !host.IsDisposed && host.IsHandleCreated) {
                    if (isBusy != null && isBusy()) return CallNextHookEx(hookHandle, nCode, wParam, lParam);
                    var m = (Win32Msg)Marshal.PtrToStructure(lParam, typeof(Win32Msg));
                    if (m.message == WM_KEYDOWN && m.hwnd != IntPtr.Zero) {
                        IntPtr fh = host.Handle;
                        bool inMainForm = m.hwnd == fh || IsChild(fh, m.hwnd);
                        bool inPopup = !inMainForm && IsInsidePopup(m.hwnd);
                        if (inMainForm || inPopup) {
                            bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                            bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;
                            bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                            int vk = m.wParam.ToInt32() & 0xFFFF;
                            bool handled = false;

                            if (!alt) {
                                if (IsWin32EditControl(m.hwnd)) {
                                    if (!shift) {
                                        if (ctrl) {
                                            if (vk == (int)Keys.C) { PostMessage(m.hwnd, WM_COPY, IntPtr.Zero, IntPtr.Zero); handled = true; }
                                            else if (vk == (int)Keys.V) { PostMessage(m.hwnd, WM_PASTE, IntPtr.Zero, IntPtr.Zero); handled = true; }
                                            else if (vk == (int)Keys.X) { PostMessage(m.hwnd, WM_CUT, IntPtr.Zero, IntPtr.Zero); handled = true; }
                                            else if (vk == (int)Keys.Z) { PostMessage(m.hwnd, EM_UNDO, IntPtr.Zero, IntPtr.Zero); handled = true; }
                                            else if (vk == (int)Keys.A) { PostMessage(m.hwnd, EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)); handled = true; }
                                            else if (inMainForm && resolveShortcut != null && (vk == (int)Keys.F || vk == (int)Keys.H)) {
                                                var act = resolveShortcut(Keys.Control | (Keys)vk);
                                                if (act != null) { host.BeginInvoke(act); handled = true; }
                                            }
                                        } else if (vk == (int)Keys.Delete) {
                                            IntPtr target = m.hwnd, wp = m.wParam, lp = m.lParam;
                                            host.BeginInvoke((Action)(() => { if (!host.IsDisposed && IsWindow(target)) SendMessage(target, WM_KEYDOWN, wp, lp); }));
                                            handled = true;
                                        }
                                    }
                                } else if (inMainForm && resolveShortcut != null) {
                                    Keys kd = (Keys)vk;
                                    if (ctrl) kd |= Keys.Control;
                                    if (shift) kd |= Keys.Shift;
                                    var act = resolveShortcut(kd);
                                    if (act != null) {
                                        host.BeginInvoke(act);
                                        handled = true;
                                    }
                                }
                            }

                            if (handled) {
                                m.message = WM_NULL;
                                Marshal.StructureToPtr(m, lParam, false);
                            }
                        }
                    }
                }
            } catch { }
            return CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }
    }
}
