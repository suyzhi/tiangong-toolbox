// 公钥槽。发布前必须由管理员工具 keygen 生成后覆盖本文件。
// 默认值是无密钥占位：插件能编译、能安装、能运行，但任何激活码都过不了验签。
// 公钥可以随源码提交；私钥在管理员机器上的 .tgkey 文件里，绝不能提交或分发。
//
//   生成生产密钥并对齐公钥槽：
//     DEV_0.7_授权\tools\LicenseAdmin\build-admin.ps1
//     TianGongLicenseAdmin.exe keygen D:\keys\master.tgkey master
//     copy D:\keys\master.public.cs DEV_0.7_授权\src\License\LicenseKeySlot.cs
//     DEV_0.7_授权\tools\build.ps1
//
//   本机联调（用测试密钥，别用于发布）：
//     DEV_0.7_授权\tools\LicenseAdmin\make-test-codes.ps1
namespace TianGongCadSuite.Licensing {
    internal static class LicenseKeySlot {
        internal const string KeyId = "placeholder";
        internal static readonly string SeedX = "TGS-SLOT-A-placeholder";
        internal static readonly string SeedY = "TGS-SLOT-B-placeholder";
        internal static readonly string MaskX = "0000000000000000000000000000000000000000000000000000000000000000";
        internal static readonly string MaskY = "0000000000000000000000000000000000000000000000000000000000000000";
        internal static readonly string ValueX = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        internal static readonly string ValueY = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    }
}
