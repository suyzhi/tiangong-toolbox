using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TianGongCadSuite {
    // 基于 Win32 原生 API 的高可靠纯文本剪贴板读写：
    //   · .NET WinForms 的 Clipboard.ContainsText() 在剪贴板被 Excel / Win+V / DLP 进程瞬时锁住时
    //     会吞掉错误静默返回 false，导致用户按 Ctrl+V 偶发报“剪贴板里没有文字”；
    //   · 原生 OpenClipboard 能精确区分“被其他进程占用（重试）”与“确实无文本”，
    //     同时避开 OLE 剪贴板在天工 CAD COM 进程里的延迟与偶发失败。
    public static class ClipboardUtil {
        const uint CF_TEXT = 1;
        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalFree(IntPtr hMem);

        public static bool SetText(string text) {
            string payload = text ?? "";
            for (int attempt = 0; attempt < 12; attempt++) {
                if (!OpenClipboard(IntPtr.Zero)) {
                    Thread.Sleep(20);
                    continue;
                }
                IntPtr hMem = IntPtr.Zero;
                try {
                    if (!EmptyClipboard()) continue;
                    byte[] bytes = Encoding.Unicode.GetBytes(payload + "\0");
                    hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                    if (hMem == IntPtr.Zero) return false;
                    IntPtr ptr = GlobalLock(hMem);
                    if (ptr == IntPtr.Zero) return false;
                    try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
                    finally { GlobalUnlock(hMem); }
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
                    hMem = IntPtr.Zero; // 写入成功后内存所有权交给系统，绝不能再 GlobalFree
                    return true;
                } finally {
                    if (hMem != IntPtr.Zero) GlobalFree(hMem);
                    CloseClipboard();
                }
            }
            return false;
        }

        public static string GetText(out bool busy) {
            busy = false;
            for (int attempt = 0; attempt < 12; attempt++) {
                if (!OpenClipboard(IntPtr.Zero)) {
                    busy = true;
                    Thread.Sleep(20);
                    continue;
                }
                busy = false;
                try {
                    if (IsClipboardFormatAvailable(CF_UNICODETEXT)) {
                        IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                        if (hData != IntPtr.Zero) {
                            IntPtr ptr = GlobalLock(hData);
                            if (ptr != IntPtr.Zero) {
                                try { return Marshal.PtrToStringUni(ptr) ?? ""; }
                                finally { GlobalUnlock(hData); }
                            }
                        }
                    }
                    if (IsClipboardFormatAvailable(CF_TEXT)) {
                        IntPtr hData = GetClipboardData(CF_TEXT);
                        if (hData != IntPtr.Zero) {
                            IntPtr ptr = GlobalLock(hData);
                            if (ptr != IntPtr.Zero) {
                                try { return Marshal.PtrToStringAnsi(ptr) ?? ""; }
                                finally { GlobalUnlock(hData); }
                            }
                        }
                    }
                    return "";
                } finally {
                    CloseClipboard();
                }
            }
            return "";
        }

        public static string GetText() {
            bool ignored;
            return GetText(out ignored);
        }
    }
}
