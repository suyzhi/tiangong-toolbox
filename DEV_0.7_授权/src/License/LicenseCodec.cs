using System;
using System.Security.Cryptography;
using System.Text;

namespace TianGongCadSuite.Licensing {
    public sealed class LicenseCode {
        internal LicenseCode(LicensePayload payload, byte[] signature, string text){
            Payload = payload; Signature = signature; Text = text;
        }
        public LicensePayload Payload { get; private set; }
        public byte[] Signature { get; private set; }
        public string Text { get; private set; }
        public bool MachineBound { get { return Payload != null && Payload.MachineBound; } }
        public LicensePlan Plan { get { return Payload == null ? null : Payload.Plan; } }
        public DateTime IssueDate { get { return Payload.IssueDate; } }
        public DateTime ExpiryDate { get { return Payload.ExpiryDate; } }
        public string Summary {
            get {
                if(Payload == null)return "无效";
                LicensePlan plan = Plan;
                string name = plan == null ? ("未知档位" + Payload.PlanCode) : plan.Name;
                return name + " 授权，有效期至 " + LicenseTime.Format(ExpiryDate) + (MachineBound ? "，已绑定本机" : "，未绑定机器");
            }
        }
    }

    // 激活码格式（纯函数，管理员工具与插件共用同一份实现）：
    //   字母表：0-9 加 a-z，去掉 i l o，共 32 个字符。大小写不敏感、无易混字符，可整段复制。
    //   每字符 5 bit：一组 8 个字符 = 40 bit = 5 字节，无损。
    //   正文 = 载荷(27B) + ECDSA-P256 签名(64B) = 91B → 19 组 = 152 字符
    //   末尾再加 1 组（8 字符）抄写校验。共 160 字符，按 5 字符分段显示。
    public static class LicenseCodec {
        internal const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        internal const string CheckSalt = "TGS-CHK-2026-01";

        public const int PayloadSize = LicensePayload.Size;
        public const int SignatureSize = LicenseSignature.Size;
        public const int BodySize = PayloadSize + SignatureSize;
        public const int GroupBytes = 5;    // 一组承载的字节数
        public const int GroupChars = 8;    // 一组占用的字符数（40 bit）
        public const int BodyGroups = 19;   // ceil(91 / 5)
        public const int CheckGroups = 1;
        public const int GroupCount = BodyGroups + CheckGroups;
        public const int TextLength = GroupCount * GroupChars;

        // ---------- 位打包 ----------

        // 5 字节 → 40 bit → 8 个 5 bit 字符。
        internal static void EncodeGroup(byte[] data,int offset,char[] output,int outOffset){
            ulong word = 0;
            for(int i = 0; i < GroupBytes; i++){
                int slot = offset + i;
                ulong b = slot < data.Length ? data[slot] : 0UL;
                word = (word << 8) | b;
            }
            for(int i = 0; i < GroupChars; i++){
                int shift = 5 * (GroupChars - 1 - i);
                int index = (int)((word >> shift) & 31UL);
                output[outOffset + i] = Alphabet[index];
            }
        }

        internal static bool DecodeGroup(char[] text,int offset,byte[] output,int outOffset){
            ulong word = 0;
            for(int i = 0; i < GroupChars; i++){
                int index = Index(text[offset + i]);
                if(index < 0)return false;
                word = (word << 5) | (ulong)index;
            }
            // 与 EncodeGroup 对称：第一个字节在最高 8 位。
            for(int i = 0; i < GroupBytes; i++){
                int shift = 8 * (GroupBytes - 1 - i);
                int target = outOffset + i;
                if(target < output.Length)output[target] = (byte)((word >> shift) & 0xFFUL);
            }
            return true;
        }

        // 用户输入宽容处理：大小写不敏感，并把 0/1 与 o/l/i 的常见误写自动纠正。
        internal static int Index(char c){
            if(c >= 'A' && c <= 'Z')c = (char)(c + 32);
            if(c == 'o')c = '0';
            else if(c == 'l' || c == 'i')c = '1';
            for(int i = 0; i < Alphabet.Length; i++)if(Alphabet[i] == c)return i;
            return -1;
        }

        internal static bool IsAlphabet(char c){ return Index(c) >= 0; }

        // ---------- 抄写校验 ----------

        internal static byte[] Checksum(byte[] body){
            using(HMAC sha = new HMACSHA256(Encoding.ASCII.GetBytes(CheckSalt))){
                byte[] digest = sha.ComputeHash(body);
                byte[] result = new byte[GroupBytes];
                Buffer.BlockCopy(digest, 0, result, 0, GroupBytes);
                return result;
            }
        }

        static void AppendCheck(byte[] body,char[] text,int outOffset){
            EncodeGroup(Checksum(body), 0, text, outOffset);
        }

        static bool CheckMatches(byte[] body,char[] text,int offset){
            byte[] expected = new byte[GroupBytes];
            if(!DecodeGroup(text, offset, expected, 0))return false;
            byte[] actual = Checksum(body);
            for(int i = 0; i < GroupBytes; i++)if(expected[i] != actual[i])return false;
            return true;
        }

        // ---------- 组装 / 解析 ----------

        public static string Compose(byte[] payload,byte[] signature){
            if(payload == null || payload.Length != PayloadSize)throw new ArgumentException("payload");
            if(signature == null || signature.Length != SignatureSize)throw new ArgumentException("signature");
            byte[] body = new byte[BodySize];
            Buffer.BlockCopy(payload, 0, body, 0, PayloadSize);
            Buffer.BlockCopy(signature, 0, body, PayloadSize, SignatureSize);
            return Group(CanonicalText(body), 5);
        }

        internal static string CanonicalText(byte[] body){
            char[] text = new char[TextLength];
            for(int g = 0; g < BodyGroups; g++)EncodeGroup(body, g * GroupBytes, text, g * GroupChars);
            AppendCheck(body, text, BodyGroups * GroupChars);
            return new string(text);
        }

        public static string Canonical(byte[] payload,byte[] signature){
            if(payload == null || payload.Length != PayloadSize)return null;
            if(signature == null || signature.Length != SignatureSize)return null;
            byte[] body = new byte[BodySize];
            Buffer.BlockCopy(payload, 0, body, 0, PayloadSize);
            Buffer.BlockCopy(signature, 0, body, PayloadSize, SignatureSize);
            return Group(CanonicalText(body), 5);
        }

        public static LicenseCode Parse(string code){
            if(string.IsNullOrEmpty(code))return null;
            char[] text = new char[TextLength];
            int count = 0;
            for(int i = 0; i < code.Length && count < text.Length; i++){
                char c = code[i];
                if(c == '-' || c == '_' || c == 0x3000 || char.IsWhiteSpace(c))continue;
                if(!IsAlphabet(c))return null;
                if(c >= 'A' && c <= 'Z')c = (char)(c + 32);
                if(c == 'o')c = '0';
                else if(c == 'l' || c == 'i')c = '1';
                text[count++] = c;
            }
            if(count != text.Length)return null;
            byte[] body = new byte[BodySize];
            for(int g = 0; g < BodyGroups; g++)if(!DecodeGroup(text, g * GroupChars, body, g * GroupBytes))return null;
            if(!CheckMatches(body, text, BodyGroups * GroupChars))return null;
            LicensePayload payload = LicensePayload.FromBytes(body, 0);
            if(payload == null)return null;
            if(LicensePlans.Find(payload.PlanCode) == null)return null;
            byte[] signature = new byte[SignatureSize];
            Buffer.BlockCopy(body, PayloadSize, signature, 0, SignatureSize);
            return new LicenseCode(payload, signature, Canonical(payload.ToBytes(), signature));
        }

        internal static string Group(string text,int size){
            StringBuilder builder = new StringBuilder(text.Length + text.Length / size + 2);
            for(int i = 0; i < text.Length; i++){
                if(i > 0 && i % size == 0)builder.Append('-');
                builder.Append(text[i]);
            }
            return builder.ToString();
        }

        // ---------- 验签 ----------

        public static bool VerifySignature(LicenseCode code){
            if(code == null || code.Payload == null || code.Signature == null)return false;
            return LicenseSignature.Verify(code.Payload.ToBytes(), code.Signature);
        }

        // 内嵌公钥的 CNG BLOB。公钥不是秘密，暴露出来只为自检与现场排查。
        public static byte[] PublicKeyBlob(){ return LicenseKeyMaterial.BuildBlob(); }

        // ---------- 自检向量：构建后必须全部通过，否则插件拒绝工作 ----------

        internal static bool SelfTest(){
            // 自检向量必须是结构合法的载荷，否则无法通过 Parse 的格式校验。
            LicensePayload model = new LicensePayload();
            model.Flags = 1;
            model.PlanCode = LicensePlans.Yearly;
            model.DayOffset = 12345;
            model.Fingerprint = new byte[LicensePayload.FingerprintSize];
            for(int i = 0; i < model.Fingerprint.Length; i++)model.Fingerprint[i] = (byte)(i * 17 + 3);
            model.Nonce = new byte[8];
            for(int i = 0; i < model.Nonce.Length; i++)model.Nonce[i] = (byte)(i * 23 + 5);
            byte[] payload = model.ToBytes();
            byte[] signature = new byte[SignatureSize];
            for(int i = 0; i < signature.Length; i++)signature[i] = (byte)(255 - i * 13);
            string text = Compose(payload, signature);
            LicenseCode code = Parse(text);
            if(code == null)return false;
            byte[] roundPayload = code.Payload.ToBytes();
            if(roundPayload.Length != payload.Length)return false;
            for(int i = 0; i < payload.Length; i++)if(roundPayload[i] != payload[i])return false;
            for(int i = 0; i < signature.Length; i++)if(code.Signature[i] != signature[i])return false;
            if(Parse(text.Replace("-", "")) == null)return false;
            if(Parse(text.ToUpperInvariant()) == null)return false;
            char[] broken = text.ToCharArray();
            int flip = -1;
            for(int i = 0; i < broken.Length; i++)if(IsAlphabet(broken[i]) && broken[i] != '-'){ flip = i; break; }
            if(flip < 0)return false;
            broken[flip] = broken[flip] == '0' ? '1' : '0';
            if(Parse(new string(broken)) != null)return false;
            return true;
        }
    }
}
