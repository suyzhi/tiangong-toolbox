using System;
using System.Security.Cryptography;
using System.Text;
using TianGongCadSuite.Licensing;

namespace PanelTests {
    // 激活码格式与验签：编码往返、抄写校验、篡改检测、位级独立性。
    internal static class LicenseCodecTests {
        internal static void Run(Action<string,bool> check){
            RoundTrip(check);
            TypoDetection(check);
            TamperDetection(check);
            Alphabet(check);
            IndependentBitPacking(check);
            MachineCode(check);
        }

        static void RoundTrip(Action<string,bool> check){
            LicensePayload model = new LicensePayload();
            model.Flags = 1;
            model.PlanCode = LicensePlans.HalfYear;
            model.DayOffset = LicenseTime.Today;
            model.Fingerprint = new byte[LicensePayload.FingerprintSize];
            for(int i = 0; i < model.Fingerprint.Length; i++)model.Fingerprint[i] = (byte)(i * 9 + 2);
            model.Nonce = new byte[8];
            for(int i = 0; i < model.Nonce.Length; i++)model.Nonce[i] = (byte)(i * 31 + 3);
            byte[] payload = model.ToBytes();
            byte[] signature = new byte[LicenseCodec.SignatureSize];
            for(int i = 0; i < signature.Length; i++)signature[i] = (byte)(i * 71 + 13);
            string text = LicenseCodec.Compose(payload, signature);
            check("激活码去掉分隔符后为 160 字符", text.Replace("-", "").Length == LicenseCodec.TextLength);
            check("激活码按 5 字符分段", text.Split('-')[0].Length == 5);
            LicenseCode c = LicenseCodec.Parse(text);
            check("随机正文可解析（含抄写校验）", c != null);
            if(c == null)return;
            byte[] back = c.Payload.ToBytes();
            bool same = back.Length == payload.Length;
            for(int i = 0; same && i < payload.Length; i++)same = back[i] == payload[i];
            check("载荷字节级往返一致", same);
            bool signatureSame = true;
            for(int i = 0; i < signature.Length; i++)signatureSame &= c.Signature[i] == signature[i];
            check("签名字节级往返一致", signatureSame);
            check("去分隔符可解析", LicenseCodec.Parse(text.Replace("-", "")) != null);
            check("大写可解析", LicenseCodec.Parse(text.ToUpperInvariant()) != null);
            check("前后空白与换行可解析", LicenseCodec.Parse("  " + text + "\r\n") != null);
        }

        static void TypoDetection(Action<string,bool> check){
            LicensePayload model = new LicensePayload();
            model.Flags = 1;
            model.PlanCode = LicensePlans.Yearly;
            model.DayOffset = LicenseTime.Today;
            model.Fingerprint = new byte[LicensePayload.FingerprintSize];
            model.Nonce = new byte[8];
            byte[] payload = model.ToBytes();
            byte[] signature = new byte[LicenseCodec.SignatureSize];
            for(int i = 0; i < signature.Length; i++)signature[i] = (byte)(i + 1);
            string text = LicenseCodec.Compose(payload, signature);
            char[] broken = text.ToCharArray();
            int flips = 0;
            int caught = 0;
            for(int i = 0; i < broken.Length && flips < 24; i++){
                if(broken[i] == '-')continue;
                char original = broken[i];
                broken[i] = original == '0' ? '1' : '0';
                flips++;
                if(LicenseCodec.Parse(new string(broken)) == null)caught++;
                broken[i] = original;
            }
            check("抄写错误全部被拦下 (" + caught + "/" + flips + ")", caught == flips);
        }

        static void TamperDetection(Action<string,bool> check){
            LicensePayload payload = new LicensePayload();
            payload.Flags = 1;
            payload.PlanCode = LicensePlans.Monthly;
            payload.DayOffset = LicenseTime.Today;
            payload.Fingerprint = new byte[LicensePayload.FingerprintSize];
            payload.Nonce = new byte[8];
            byte[] signature = new byte[LicenseCodec.SignatureSize];
            string text = LicenseCodec.Compose(payload.ToBytes(), signature);
            LicenseCode code = LicenseCodec.Parse(text);
            check("合法结构的激活码可解析", code != null);
            if(code == null)return;
            check("没有管理员私钥时验签必然失败", !LicenseCodec.VerifySignature(code));

            byte[] bytes = payload.ToBytes();
            bytes[4] = LicensePlans.Yearly;
            LicenseCode upgraded = new LicenseCode(LicensePayload.FromBytes(bytes, 0), code.Signature, text);
            check("档位被改写后签名不再匹配", !LicenseCodec.VerifySignature(upgraded));
            bytes = payload.ToBytes();
            bytes[0] = 0;
            LicenseCode unbound = new LicenseCode(LicensePayload.FromBytes(bytes, 0), code.Signature, text);
            check("机器绑定标记被改写后验签失败", !LicenseCodec.VerifySignature(unbound));
        }

        static void Alphabet(Action<string,bool> check){
            check("字母表长度为 32（5 bit 对齐）", LicenseCodec.Alphabet.Length == 32);
            check("字母表不含易混字符 i l o", LicenseCodec.Alphabet.IndexOf('i') < 0 && LicenseCodec.Alphabet.IndexOf('l') < 0 && LicenseCodec.Alphabet.IndexOf('o') < 0);
            check("空串不解析", LicenseCodec.Parse("") == null && LicenseCodec.Parse(null) == null);
            check("含非法字符不解析", LicenseCodec.Parse(new string('a', 200) + "!") == null);
            check("长度不足不解析", LicenseCodec.Parse(new string('a', 100)) == null);
        }

        // 用独立实现验证位打包：一组 8 字符必须还原出同样的 5 字节。
        static void IndependentBitPacking(Action<string,bool> check){
            byte[] data = new byte[]{ 0x00, 0x11, 0x22, 0x33, 0x44, 0xFF, 0xEE, 0xDD, 0xCC, 0xBB };
            char[] text = new char[LicenseCodec.GroupChars];
            LicenseCodec.EncodeGroup(data, 0, text, 0);
            ulong word = 0x0011223344UL;
            string expected = "";
            for(int i = 0; i < LicenseCodec.GroupChars; i++)expected += LicenseCodec.Alphabet[(int)((word >> (5 * (7 - i))) & 31UL)];
            check("位打包与独立实现一致", new string(text) == expected);
            byte[] back = new byte[LicenseCodec.GroupBytes];
            check("位解包返回真", LicenseCodec.DecodeGroup(text, 0, back, 0));
            bool same = true;
            for(int i = 0; i < LicenseCodec.GroupBytes; i++)same &= back[i] == data[i];
            check("位解包与原始字节一致", same);
            text = new char[LicenseCodec.GroupChars];
            LicenseCodec.EncodeGroup(data, 5, text, 0);
            LicenseCodec.DecodeGroup(text, 0, back, 0);
            check("第二组同样无损", back[0] == 0xFF && back[4] == 0xBB);
        }

        // 机器码：16 字符固定长度，可与激活码里的 10 字节指纹对齐。
        static void MachineCode(Action<string,bool> check){
            string machineCode = LicenseMachine.MachineCode();
            check("机器码为 4 段 4 字符", machineCode.Replace("-", "").Length == 16 && machineCode.Split('-').Length == 4);
            string shortCode = LicenseMachine.ShortCode();
            check("机器码短码为 8 字符", shortCode.Replace("-", "").Length == 8);
            byte[] parsed;
            check("机器码可被解析回来", LicenseMachine.TryParse(machineCode, out parsed));
            if(parsed != null){
                byte[] actual = LicenseMachine.ShortBytes();
                bool same = parsed.Length == actual.Length;
                for(int i = 0; same && i < actual.Length; i++)same = parsed[i] == actual[i];
                check("小写机器码解析一致", same && LicenseMachine.TryParse(machineCode.ToLowerInvariant(), out parsed));
            }
            check("机器码稳定（同一台机器两次调用一致）", LicenseMachine.MachineCode() == machineCode);
            check("乱码机器码被拒绝", !LicenseMachine.TryParse("!!!!", out parsed));
        }
    }
}
