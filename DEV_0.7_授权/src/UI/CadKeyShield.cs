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
        const uint WM_SYSKEYDOWN = 0x0104;
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
        readonly List<Control> secondaries = new List<Control>();
        bool registered;
        bool disposed;

        // 全线程共享唯一一个 WH_GETMESSAGE 钩子：多窗口共存时不再叠加 N 层原生→托管回调；
        // 且入口直接按偏移读取 4 字节 message 字段，99.9% 的 WM_MOUSEMOVE / WM_PAINT / WM_TIMER 零分配瞬时放行。
        [ThreadStatic] static List<CadKeyShield> threadShields;
        [ThreadStatic] static IntPtr threadHookHandle;
        [ThreadStatic] static HookProc threadHookDelegate;
        [ThreadStatic] static StringBuilder classNameBuf;

        // resolveShortcut：仅在非文本编辑控件（或显式注册了 Ctrl+F / Ctrl+H 等导航键）时调用；返回非空 Action 即吞掉按键并异步执行。
        public CadKeyShield(Form hostForm, Func<bool> busyCheck = null, Func<Keys, Action> shortcutResolver = null) {
            if (hostForm == null) throw new ArgumentNullException("hostForm");
            host = hostForm;
            isBusy = busyCheck;
            resolveShortcut = shortcutResolver;

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
            if (disposed || registered) return;
            if (threadShields == null) threadShields = new List<CadKeyShield>();
            if (!threadShields.Contains(this)) threadShields.Add(this);
            registered = true;
            if (threadHookHandle == IntPtr.Zero) {
                if (threadHookDelegate == null) threadHookDelegate = OnThreadGetMessage;
                try { threadHookHandle = SetWindowsHookEx(WH_GETMESSAGE, threadHookDelegate, IntPtr.Zero, GetCurrentThreadId()); } catch { }
            }
        }

        void Uninstall() {
            if (!registered) return;
            registered = false;
            if (threadShields != null) {
                threadShields.Remove(this);
                if (threadShields.Count == 0 && threadHookHandle != IntPtr.Zero) {
                    try { UnhookWindowsHookEx(threadHookHandle); } catch { }
                    threadHookHandle = IntPtr.Zero;
                }
            }
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
            if (classNameBuf == null) classNameBuf = new StringBuilder(32);
            classNameBuf.Length = 0;
            if (GetClassName(hwnd, classNameBuf, classNameBuf.Capacity) > 0) {
                return string.Equals(classNameBuf.ToString(), "EDIT", StringComparison.OrdinalIgnoreCase);
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

        static IntPtr OnThreadGetMessage(int nCode, IntPtr wParam, IntPtr lParam) {
            // 极速零分配快路径：仅当 PM_REMOVE 且消息是 WM_KEYDOWN / WM_SYSKEYDOWN（Alt+↑/↓ 等组合键）时才解析，
            // 鼠标移动 / 绘制 / 定时器等高频消息不装箱、不 PtrToStructure、不触发任何 Win32 API。
            if (nCode >= 0 && wParam == (IntPtr)1 && lParam != IntPtr.Zero && threadShields != null && threadShields.Count > 0) {
                uint msg = (uint)Marshal.ReadInt32(lParam, IntPtr.Size);
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) {
                    IntPtr hwnd = Marshal.ReadIntPtr(lParam, 0);
                    if (hwnd != IntPtr.Zero) {
                        for (int i = threadShields.Count - 1; i >= 0; i--) {
                            var s = threadShields[i];
                            if (s.TryHandleKeyDown(hwnd, lParam)) break;
                        }
                    }
                }
            }
            return CallNextHookEx(threadHookHandle, nCode, wParam, lParam);
        }

        bool TryHandleKeyDown(IntPtr hwnd, IntPtr lParam) {
            try {
                if (disposed || host.IsDisposed || !host.IsHandleCreated) return false;
                IntPtr fh = host.Handle;
                bool inMainForm = hwnd == fh || IsChild(fh, hwnd);
                bool inPopup = !inMainForm && IsInsidePopup(hwnd);
                if (!inMainForm && !inPopup) return false;
                if (isBusy != null && isBusy()) return true;

                bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;
                bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                var m = (Win32Msg)Marshal.PtrToStructure(lParam, typeof(Win32Msg));
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
                } else if (inMainForm && resolveShortcut != null && !IsWin32EditControl(m.hwnd)) {
                    // Alt+↑/↓ 等系统键组合（WM_SYSKEYDOWN）交由窗体快捷键表裁决；保留 ComboBox 原生 Alt+↓ 展开行为。
                    bool isComboArrow = (vk == (int)Keys.Up || vk == (int)Keys.Down) && (Control.FromHandle(m.hwnd) is ComboBox);
                    if (!isComboArrow) {
                        Keys kd = (Keys)vk | Keys.Alt;
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
                    Marshal.WriteInt32(lParam, IntPtr.Size, (int)WM_NULL);
                }
                return true;
            } catch {
                return false;
            }
        }
    }
}
