using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 现代 Windows 文件夹选择对话框（Vista / Win10+ IFileDialog + FOS_PICKFOLDERS）：
    // 取代 WinForms 古董 FolderBrowserDialog（不能贴路径、不能搜索、深层目录要像爬树一样点十几下）。
    // 不引任何第三方库；极少数非 STA 或精简系统异常时自动降级回 FolderBrowserDialog。
    public static class FolderPicker {
        const uint FOS_NOCHANGEDIR = 0x00000008;
        const uint FOS_PICKFOLDERS = 0x00000020;
        const uint FOS_FORCEFILESYSTEM = 0x00000040;
        const uint FOS_PATHMUSTEXIST = 0x00000800;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        static readonly Guid ClsidFileOpenDialog = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
        static readonly Guid IidIShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        // 选择目录。取消返回 null；rememberKey 非空时自动记住该场景上次选过的文件夹。
        public static string Pick(IWin32Window owner, string title, string initialPath = "", string rememberKey = "") {
            string startDir = ResolveExistingDir(initialPath);
            if (startDir.Length == 0 && !string.IsNullOrEmpty(rememberKey)) {
                startDir = ResolveExistingDir(Choices.Get("dir." + rememberKey, ""));
            }

            string picked;
            if (!TryPickModern(owner, title, startDir, out picked)) {
                picked = PickLegacy(owner, title, startDir);
            }
            if (!string.IsNullOrEmpty(picked) && !string.IsNullOrEmpty(rememberKey)) {
                Choices.Set("dir." + rememberKey, picked);
            }
            return picked;
        }

        // 返回 true 表示现代对话框已走完（picked == null 说明用户点取消）；返回 false 表示需要降级到老式对话框。
        static bool TryPickModern(IWin32Window owner, string title, string startDir, out string picked) {
            picked = null;
            IFileDialog dlg = null;
            IShellItem initItem = null;
            IShellItem resultItem = null;
            try {
                Type comType = Type.GetTypeFromCLSID(ClsidFileOpenDialog);
                if (comType == null) return false;
                dlg = (IFileDialog)Activator.CreateInstance(comType);
                uint opts = 0;
                try { dlg.GetOptions(out opts); } catch { }
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                if (!string.IsNullOrWhiteSpace(title)) dlg.SetTitle(title);

                if (startDir.Length > 0) {
                    Guid iid = IidIShellItem;
                    if (SHCreateItemFromParsingName(startDir, IntPtr.Zero, ref iid, out initItem) == 0 && initItem != null) {
                        dlg.SetFolder(initItem);
                    }
                }

                IntPtr hwnd = owner != null ? owner.Handle : IntPtr.Zero;
                int hr = dlg.Show(hwnd);
                if (hr == ERROR_CANCELLED) return true;
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                dlg.GetResult(out resultItem);
                if (resultItem != null) {
                    string path;
                    resultItem.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    if (!string.IsNullOrWhiteSpace(path)) picked = path;
                }
                return true;
            } catch (Exception e) {
                Log.Write("FolderPickerModern", e.Message);
                return false;
            } finally {
                ReleaseCom(resultItem);
                ReleaseCom(initItem);
                ReleaseCom(dlg);
            }
        }

        static string PickLegacy(IWin32Window owner, string title, string startDir) {
            using (var dlg = new FolderBrowserDialog {
                Description = title ?? "选择文件夹",
                ShowNewFolderButton = true
            }) {
                if (startDir.Length > 0) dlg.SelectedPath = startDir;
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        // 若目标目录尚未创建，逐级向上回溯到最近存在的父目录，避免对话框打开在“此电脑”根目录
        public static string ResolveExistingDir(string path) {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try {
                string p = path.Trim().Trim('"');
                if (File.Exists(p)) p = Path.GetDirectoryName(p) ?? "";
                while (!string.IsNullOrEmpty(p)) {
                    if (Directory.Exists(p)) return p;
                    string parent = Path.GetDirectoryName(p);
                    if (string.IsNullOrEmpty(parent) || string.Equals(parent, p, StringComparison.OrdinalIgnoreCase)) break;
                    p = parent;
                }
            } catch { }
            return "";
        }

        static void ReleaseCom(object o) {
            if (o != null) {
                try { Marshal.ReleaseComObject(o); } catch { }
            }
        }

#if TG_DEV_BUILD
        // 离线自检：验证 IFileDialog 与 IShellItem COM vtable 绑定无误（不弹窗）
        internal static bool ProbeComSelfTest(string dir) {
            IFileDialog dlg = null;
            IShellItem item = null;
            try {
                Type comType = Type.GetTypeFromCLSID(ClsidFileOpenDialog);
                if (comType == null) return false;
                dlg = (IFileDialog)Activator.CreateInstance(comType);
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                dlg.SetTitle("SelfTest");
                string existing = ResolveExistingDir(dir);
                if (existing.Length > 0) {
                    Guid iid = IidIShellItem;
                    if (SHCreateItemFromParsingName(existing, IntPtr.Zero, ref iid, out item) == 0 && item != null) {
                        dlg.SetFolder(item);
                        string back;
                        item.GetDisplayName(SIGDN_FILESYSPATH, out back);
                        if (string.IsNullOrEmpty(back)) return false;
                    }
                }
                return true;
            } finally {
                ReleaseCom(item);
                ReleaseCom(dlg);
            }
        }
#endif
    }
}
