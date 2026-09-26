using System;
using System.Security.Cryptography;

namespace TianGongCadSuite.Licensing {
    // ECDSA-P256 / SHA-256。签名固定 64 字节（IEEE P1363），不打乱长度。
    public static class LicenseSignature {
        public const int Size = 64;

        internal static byte[] Blob { get { return LicenseKeyMaterial.BuildBlob(); } }

        internal static bool Verify(byte[] data,byte[] signature){
            if(data == null || signature == null || signature.Length != Size)return false;
            try{
                using(CngKey key = CngKey.Import(Blob, CngKeyBlobFormat.EccPublicBlob))
                using(ECDsaCng ecdsa = new ECDsaCng(key)){
                    return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
                }
            }catch(CryptographicException){ return false; }
            catch(ArgumentException){ return false; }
        }

        // 签发用，需要管理员私钥。插件运行时没有私钥，这条路径在客户机上不可用；
        // 保留它是为了让管理员工具、测试与插件共用同一份格式实现，避免两套编码走偏。
        public static byte[] Sign(byte[] data,CngKey privateKey){
            if(data == null || privateKey == null)throw new ArgumentNullException("sign");
            using(ECDsaCng ecdsa = new ECDsaCng(privateKey))return ecdsa.SignData(data, HashAlgorithmName.SHA256);
        }
    }
}
