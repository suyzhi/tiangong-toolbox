using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TianGongCadSuite.Licensing.Admin {
    // 管理员侧：私钥文件读写 + 机器码解析 + 激活码签发。
    // 这个类只会被编译进 TianGongLicenseAdmin.exe，不会进入随插件分发的 DLL。
    public static class LicenseAdminKey {
        public const string Magic = "TGLK-1";

        public static byte[] CreatePrivateKey(string keyId){
            using(ECDsaCng ecdsa = new ECDsaCng(256)){
                byte[] blob = ecdsa.Key.Export(CngKeyBlobFormat.EccPrivateBlob);
                byte[] x = new byte[32];
                byte[] y = new byte[32];
                using(ECDsaCng again = new ECDsaCng(CngKey.Import(blob, CngKeyBlobFormat.EccPrivateBlob))){
                    byte[] publicBlob = again.Key.Export(CngKeyBlobFormat.EccPublicBlob);
                    Buffer.BlockCopy(publicBlob, 8, x, 0, 32);
                    Buffer.BlockCopy(publicBlob, 40, y, 0, 32);
                }
                byte[] record = new byte[blob.Length + 64];
                Buffer.BlockCopy(blob, 0, record, 0, blob.Length);
                Buffer.BlockCopy(x, 0, record, blob.Length, 32);
                Buffer.BlockCopy(y, 0, record, blob.Length + 32, 32);
                return record;
            }
        }

        public static void SavePrivateKey(string path,byte[] record,string keyId){
            StringBuilder text = new StringBuilder();
            text.Append(Magic).Append('\n');
            text.Append((keyId ?? "master").Trim()).Append('\n');
            text.Append(Convert.ToBase64String(record)).Append('\n');
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        public static byte[] LoadPrivateKey(string path){
            if(!File.Exists(path))throw new FileNotFoundException("找不到私钥文件：" + path);
            string[] lines = File.ReadAllText(path).Split('\n');
            if(lines.Length < 3 || lines[0].Trim() != Magic)throw new InvalidDataException("私钥文件格式不正确：" + path);
            byte[] record = Convert.FromBase64String(lines[2].Trim());
            if(record.Length < 104)throw new InvalidDataException("私钥文件长度不正确：" + path);
            byte[] blob = new byte[104];
            Buffer.BlockCopy(record, 0, blob, 0, 104);
            return blob;
        }

        public static byte[] PublicCoordinates(byte[] privateKeyBlob){
            using(ECDsaCng ecdsa = new ECDsaCng(CngKey.Import(privateKeyBlob, CngKeyBlobFormat.EccPrivateBlob))){
                byte[] publicBlob = ecdsa.Key.Export(CngKeyBlobFormat.EccPublicBlob);
                byte[] coordinates = new byte[64];
                Buffer.BlockCopy(publicBlob, 8, coordinates, 0, 32);
                Buffer.BlockCopy(publicBlob, 40, coordinates, 32, 32);
                return coordinates;
            }
        }

        public static string Create(string privateKeyPath,LicensePlan plan,string machineCode,int? issueDay,out string detail){
            byte[] privateKey = LoadPrivateKey(privateKeyPath);
            return Create(privateKey, plan, machineCode, issueDay, out detail);
        }

        public static string Create(byte[] privateKey,LicensePlan plan,string machineCode,int? issueDay,out string detail){
            detail = null;
            if(plan == null){ detail = "档位不存在。"; return null; }
            LicensePayload payload = new LicensePayload();
            payload.PlanCode = plan.Code;
            payload.DayOffset = issueDay.HasValue ? issueDay.Value : LicenseTime.Today;
            byte[] nonce = new byte[8];
            using(RandomNumberGenerator rng = RandomNumberGenerator.Create())rng.GetBytes(nonce);
            payload.Nonce = nonce;

            if(string.IsNullOrEmpty(machineCode)){
                payload.Flags = 0;
                byte[] filler = new byte[LicensePayload.FingerprintSize];
                using(RandomNumberGenerator rng = RandomNumberGenerator.Create())rng.GetBytes(filler);
                payload.Fingerprint = filler;
                detail = "未绑定机器的通用激活码。";
            }else{
                byte[] fingerprint;
                if(!LicenseMachine.TryParse(machineCode, out fingerprint)){
                    detail = "机器码无法识别，应为 16 位（可带分隔符），字符集 0-9A-Z 去掉 I O U。";
                    return null;
                }
                payload.Flags = 1;
                byte[] full = new byte[LicensePayload.FingerprintSize];
                Buffer.BlockCopy(fingerprint, 0, full, 0, fingerprint.Length);
                payload.Fingerprint = full;
            }

            using(CngKey key = CngKey.Import(privateKey, CngKeyBlobFormat.EccPrivateBlob)){
                byte[] bytes = payload.ToBytes();
                byte[] signature = LicenseSignature.Sign(bytes, key);
                return LicenseCodec.Compose(bytes, signature);
            }
        }

        public static string Inspect(string codeText,out string detail){
            detail = null;
            LicenseCode code = LicenseCodec.Parse(codeText);
            if(code == null){ detail = "格式错误或抄写有误。"; return null; }
            bool signature = LicenseCodec.VerifySignature(code);
            StringBuilder text = new StringBuilder();
            text.Append("码ID      : ").Append(LicenseCodec.Display(code.CodeId)).Append('\n');
            text.Append("档位      : ").Append(code.Plan == null ? "未知" : code.Plan.Name).Append('\n');
            text.Append("签发日期  : ").Append(LicenseTime.Format(code.IssueDate)).Append('\n');
            text.Append("到期日期  : ").Append(LicenseTime.Format(code.ExpiryDate)).Append('\n');
            text.Append("机器绑定  : ").Append(code.MachineBound ? "是" : "否（通用码）").Append('\n');
            text.Append("指纹前缀  : ").Append(Convert.ToBase64String(code.Payload.Fingerprint).Substring(0, 8)).Append('\n');
            text.Append("签名校验  : ").Append(signature ? "通过（本插件管理员签发）" : "失败（不是本插件签发的码）").Append('\n');
            detail = signature ? null : "签名校验失败。";
            return text.ToString();
        }
    }
}
