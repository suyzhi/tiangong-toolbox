// 测试公钥夹具（testmaster，非生产密钥；供 server/test_server.py 与 tools/LicenseAdmin/make-test-codes.ps1 使用）。
// 仅包含公开公钥坐标，不含任何私钥材料。
namespace TianGongCadSuite.Licensing {
    internal static class LicenseKeySlot {
        internal const string KeyId = "testmaster";
        internal static readonly string MaskX = "307099ea34d0d600f18a0881f9171dd293640d707458e042428e822875d88017";
        internal static readonly string MaskY = "414f782a52c3ea1b4512fa2b7edad8fd7d8dee294cd5c668f4742d8bf29fff4c";
        internal static readonly string SeedX = "TGS-SLOT-A-testmaster";
        internal static readonly string SeedY = "TGS-SLOT-B-testmaster";
        internal static readonly string ValueX = "vF7E3BcCnX95PJpVPcxKnGSYv9Iv9V5yrtKLyXiqK58=";
        internal static readonly string ValueY = "N4iMZrVQLa5PzEYby/LsPnPPXh1VDHgvYX7seEMNPWo=";
    }
}
