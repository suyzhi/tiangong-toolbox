using System;

namespace TianGongCadSuite {
    // 产品名与版本号。版本号只有一个来源：DEV_0.7_授权\VERSION → tools/build.ps1 写进程序集版本，
    // 这里从程序集读出来。窗口标题、加载项说明、日志一律用它，不要再在代码里手写 "0.7.x"。
    public static class Product {
        public const string Name = "天工工具箱";
        static string version;
        public static string Version {
            get {
                if (version == null) {
                    try {
                        var v = typeof(Product).Assembly.GetName().Version;
                        version = v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
                    } catch { version = "?"; }
                }
                return version;
            }
        }
        // 窗口标题：「型材自动填充 · 天工工具箱 0.7.5」
        public static string Title(string window){ return window + " · " + Name + " " + Version; }
    }
}
