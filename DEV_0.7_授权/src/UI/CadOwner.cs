using System;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 把天工 CAD 主窗口句柄包装成 WinForms IWin32Window，让无模式工具窗口始终浮在 CAD 之上。
    public sealed class CadOwner : IWin32Window {
        public IntPtr Handle { get; private set; }
        public CadOwner(int handle) { Handle = new IntPtr(handle); }
    }
}
