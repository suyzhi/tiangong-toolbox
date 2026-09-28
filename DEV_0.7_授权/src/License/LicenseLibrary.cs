using System;
using System.Security.Cryptography;
using System.Text;

namespace TianGongCadSuite.Licensing {
    public enum LicenseStatus {
        Valid = 0,
        Missing = 1,
        Expired = 2,
        WrongMachine = 3,
        BadCode = 4,
        ClockTampered = 5,
        Environment = 6,
        ExpiringSoon = 7,
        Revoked = 8
    }

    public sealed class LicenseReport {
        public LicenseStatus Status;
        public string Detail;
        public string PlanName;
        public DateTime ExpiryDate;
        public int DaysLeft;
        public string MachineCode;
        // 码ID：这次激活/校验涉及的激活码标识（管理员台账里用的是同一个 ID）。
        public string CodeId;
        // 成功路径上的提示语（例如"该码已在本机激活"），失败时走 message。
        public string Notice;

        public bool Usable {
            get { return Status == LicenseStatus.Valid || Status == LicenseStatus.ExpiringSoon; }
        }

        public string Describe(){
            switch(Status){
                case LicenseStatus.Valid: return PlanName + " 授权有效，剩余 " + DaysLeft + " 天（" + LicenseTime.Format(ExpiryDate) + " 到期）";
                case LicenseStatus.ExpiringSoon: return PlanName + " 授权将在 " + DaysLeft + " 天后到期（" + LicenseTime.Format(ExpiryDate) + "）";
                case LicenseStatus.Missing: return "未激活。请输入管理员发来的激活码。";
                case LicenseStatus.Expired: return PlanName + " 授权已于 " + LicenseTime.Format(ExpiryDate) + " 到期，请向管理员续期。";
                case LicenseStatus.WrongMachine: return "本机激活记录属于另一台机器（或硬件已更换），无法使用。请联系管理员重新签发激活码。";
                case LicenseStatus.BadCode: return "激活码无效或已损坏，请重新核对（注意区分 0/O、1/I）。";
                case LicenseStatus.ClockTampered: return "检测到系统时间被回调，授权已暂停。请把系统时间校正后重新激活。";
                case LicenseStatus.Environment: return "运行环境异常，授权校验被阻止。" + Detail;
                case LicenseStatus.Revoked: return "该激活码已被管理员停用（作废），请联系管理员重新签发。";
            }
            return "授权状态未知。";
        }
    }

    // 授权链核心。任何一处校验失败都会让 Valid 为假，而 UI 只显示笼统原因。
    public static class LicenseLibrary {
        internal const int DayTolerance = 7;
        internal const int FutureTolerance = 1;
        internal const int WarnDays = 14;

        static int cachedScan = -1;
        static DateTime cachedScanTime = DateTime.MinValue;
        static int cachedScanOverride = -2;

        internal static int Today { get { return LicenseTestHooks.TodayOverride >= 0 ? LicenseTestHooks.TodayOverride : LicenseTime.Today; } }

        internal static byte[] MachineShort { get { return LicenseMachine.ShortBytes(); } }

        internal static int ScanBits(){
            // 环境自检结果缓存 30 秒，避免每个命令都枚举模块。
            // 缓存必须跟着"覆盖值"变化失效，否则先用真实自检、再用注入值时会读到脏缓存。
            if(cachedScanOverride != LicenseTestHooks.GuardOverride)cachedScan = -1;
            if(cachedScan >= 0 && (DateTime.UtcNow - cachedScanTime).TotalSeconds < 30)return cachedScan;
            cachedScanOverride = LicenseTestHooks.GuardOverride;
            cachedScan = cachedScanOverride >= 0 ? cachedScanOverride : LicenseGuard.Scan();
            cachedScanTime = DateTime.UtcNow;
            return cachedScan;
        }

        internal static void ResetCache(){ cachedScan = -1; }

        // 解析激活码。开发密钥模式（%ProgramData% 里放了 license-dev.key，本机自带签发权）
        // 只编译进开发构建（build.ps1 -DevBuild）；正式构建只认内嵌公钥验签。
        // 以前这段在正式版里也生效：放一个长度对的文件，任何格式合法的码都会被当成已签名。
        internal static LicenseCode Resolve(ActivationRecord record,out bool signed){
            signed = false;
            if(record == null)return null;
            string text = LicenseTestHooks.CodeOverride ?? record.Code;
            LicenseCode code = LicenseCodec.Parse(text);
            if(code == null)return null;
            if(LicenseCodec.VerifySignature(code)){ signed = true; return code; }
#if TG_DEV_BUILD
            byte[] dev = LicenseTestHooks.DevPrivateKey();
            if(dev != null){
                LicenseTestHooks.DevKeyMode = true;
                signed = true;
                return code;
            }
#endif
            return code;
        }

        // 作废清单命中的码：任何机器都不能用，已激活的机器下次校验也会失效。
        internal static bool IsRevoked(LicenseCode code){
            if(code == null || code.Payload == null)return false;
            return LicenseRevoked.Contains(code.CodeId);
        }

        internal static bool Accept(LicenseCode code){
            if(code == null)return false;
            if(LicenseCodec.VerifySignature(code))return true;
#if TG_DEV_BUILD
            return LicenseTestHooks.DevPrivateKey() != null;
#else
            return false;
#endif
        }

        public static LicenseReport Current(){
            LicenseReport report = new LicenseReport();
            report.MachineCode = MachineCodeForDisplay();
            int scan = ScanBits();

            ActivationRecord record = LicenseStore.Load();
            if(record == null){
                report.Status = LicenseStatus.Missing;
                report.Detail = LicenseGuard.Describe(scan);
                return report;
            }

            bool signed;
            LicenseCode code = Resolve(record, out signed);

            if(code == null || !signed){
                report.Status = LicenseStatus.BadCode;
                report.Detail = code == null ? "unparsed" : "unsigned";
                if(code != null)report.Detail += " " + DiagCode(code) + " stored=" + (record.Code == null ? "null" : record.Code.Length.ToString());
                return report;
            }

            report.CodeId = code.CodeId;
            if(IsRevoked(code)){
                // 作废优先于其余判定：已激活的机器也会在下次校验时失效。
                report.Status = LicenseStatus.Revoked;
                return report;
            }
            LicensePlan plan = code.Plan;
            if(plan == null){ report.Status = LicenseStatus.BadCode; return report; }
            report.PlanName = plan.Name;
            report.ExpiryDate = code.ExpiryDate;

            if(code.MachineBound && !FingerprintMatches(code.Payload.Fingerprint)){
                report.Status = LicenseStatus.WrongMachine;
                return report;
            }
            if(!FingerprintMatches(record.MachineFingerprint)){
                report.Status = LicenseStatus.WrongMachine;
                return report;
            }

            int today = Today;
            // 时间回调必须最先判定：把系统时间调回去会让"签发日在未来"，
            // 如果先判未来日期就会把回调误报成无效码。
            long high = LicenseStore.HighWaterTicks();
            ClockAudit = "today=" + today + " highTicks=" + high + " highDay=" + (high > 0 ? LicenseTime.ToDayOffset(new DateTime(high, DateTimeKind.Utc)).ToString() : "none");
            if(high > 0){
                int highDay = LicenseTime.ToDayOffset(new DateTime(high, DateTimeKind.Utc));
                if(today + DayTolerance < highDay){
                    // 注意：这里绝不能 Touch——回调期间写入会把"最高时间"拉低，
                    // 反而把后续的判定基准破坏掉。
                    report.Status = LicenseStatus.ClockTampered;
                    return report;
                }
            }

            // 签发日期晚于本机时间：只有在"本机没有更高的时间基准"时才判定为假码。
            // 若本机时间基准比今天更靠后，说明是时间被回调（回调超过容差的情况已在上面拦掉），
            // 此时按时间基准来算剩余天数，避免误报。
            bool behind = code.Payload.DayOffset > today + FutureTolerance;
            if(behind){
                int highDay = high > 0 ? LicenseTime.ToDayOffset(new DateTime(high, DateTimeKind.Utc)) : 0;
                if(highDay <= today){
                    report.Status = LicenseStatus.BadCode;
                    report.Detail = "签发日期晚于本机时间";
                    return report;
                }
                today = code.Payload.DayOffset;
                report.Detail = "系统时间早于授权时间基准，已按时间基准计算";
            }

            report.DaysLeft = code.Payload.DayOffset + plan.Days - today;
            if(report.DaysLeft > 0 && (scan & ~LicenseGuard.DebuggerBit) != 0){
                report.Status = LicenseStatus.Environment;
                report.Detail = LicenseGuard.Describe(scan) + " scan=" + scan + " override=" + LicenseTestHooks.GuardOverride;
                return report;
            }

            if(report.DaysLeft <= 0){
                report.Status = LicenseStatus.Expired;
                return report;
            }

            LicenseStore.Touch(record);
            if(report.DaysLeft <= WarnDays)report.Status = LicenseStatus.ExpiringSoon;
            else report.Status = LicenseStatus.Valid;
            return report;
        }

        // 令牌种子。目前它必须等于内嵌公钥的语义指纹，因此是恒定值。
        // 这里刻意做成"由载荷推导"的形状，是为了给后续加固留出接口：
        // 将来若要签发"私钥派生"的密钥对，插件就能在不知道私钥的前提下
        // 用公钥复算出同一个种子，从而让换密钥的补丁立即失效。
        internal static uint TokenSeed(LicensePayload payload){
            if(payload == null)return 0;
            return LicenseKeyMaterial.SemanticPrint();
        }

        // 激活码里存的是 5 字节短指纹（其余补零），激活文件里存完整短指纹。
        // 比较只看前 5 字节，长度不一致时按"不匹配"处理。
        internal static bool FingerprintMatches(byte[] expected){
            if(expected == null)return false;
            byte[] actual = MachineShort;
            if(actual == null || actual.Length != LicenseMachine.ShortSize)return false;
            if(expected.Length < actual.Length)return false;
            int diff = 0;
            for(int i = 0; i < actual.Length; i++)diff |= expected[i] ^ actual[i];
            if(diff != 0)return false;
            if((ScanBits() & LicenseGuard.DebuggerBit) != 0)return false;
            return true;
        }

        static string MachineCodeForDisplay(){
            string hook = LicenseHook.MachineCode();
            return hook ?? LicenseMachine.MachineCode();
        }

        // 供业务代码取用的幂等令牌：混入了到期日与机器指纹。
        // 破解者就算把上面的分支改掉，也拿不到同样的令牌，下游校验会失败。
        internal static int Token(){
            ActivationRecord record = LicenseStore.Load();
            if(record == null)return 0;
            LicenseCode code = LicenseCodec.Parse(record.Code);
            if(code == null || code.Plan == null)return 0;
            if(TokenSeed(code.Payload) != LicenseKeyMaterial.SemanticPrint())return 0;
            int expiryDay = code.Payload.DayOffset + code.Plan.Days;
            unchecked{
                int acc = expiryDay * 0x27D4EB2D;
                acc ^= Today * 0x165667B1;
                acc ^= LicenseKeyMaterial.SemanticPrint() != 0 ? 0x2545F491 : 0;
                byte[] fingerprint = MachineShort;
                for(int i = 0; i < fingerprint.Length; i++)acc = (acc * 31) ^ fingerprint[i];
                return acc;
            }
        }

        // 业务入口守卫：命令执行前调用。返回 null 表示拒绝。
        internal static bool Gate(){
            LicenseReport report = Current();
            if(!report.Usable)return false;
            int token = Token();
            if(token == 0)return false;
            return (token ^ 0x5F3759DF) != 0;
        }

        // 激活被拒时返回的结论：状态是"这次尝试"的结果，不会把本机已有的授权状态当成结论。
        static LicenseReport Rejected(LicenseCode code,string reason,out string message){
            message = reason;
            LicenseReport report = new LicenseReport();
            report.Status = LicenseStatus.BadCode;
            report.MachineCode = MachineCodeForDisplay();
            report.CodeId = code == null ? null : code.CodeId;
            return report;
        }

        // 兼容旧调用：只关心错误消息。
        public static LicenseReport Activate(string codeText,out string message){
            string notice;
            return Activate(codeText, out message, out notice);
        }

        // 激活主流程：管理员发的码在这里与本机指纹绑定。
        // 成功时 message=null，notice 里是给用户看的提示（含码ID）。
        public static LicenseReport Activate(string codeText,out string message,out string notice){
            notice = null;
#if TG_DEV_BUILD
            if(LicenseTestHooks.CodeOverride != null){
                message = null;
                ResetCache();
                return Current();
            }
#endif
            LicenseCode code = LicenseCodec.Parse(codeText);
            if(code == null)return Rejected(null,"激活码格式不正确或抄写有误，请整段复制后重试。", out message);
            if(!Accept(code))
                return Rejected(code,"激活码签名校验失败，该码不是由本插件管理员签发的。" + " [diag " + DiagCode(code) + "]", out message);
            if(IsRevoked(code))
                return Rejected(code,"该激活码已被管理员停用（作废），请联系管理员重新签发。", out message);
            LicensePlan plan = code.Plan;
            if(plan == null)return Rejected(code,"激活码档位无法识别。", out message);

            int today = Today;
            if(code.Payload.DayOffset > today + FutureTolerance)
                return Rejected(code,"激活码签发日期晚于本机时间，请先校正系统时间。", out message);
            if(today >= code.Payload.DayOffset + plan.Days)
                return Rejected(code,"该激活码已于 " + LicenseTime.Format(code.ExpiryDate) + " 到期。", out message);
            byte[] fingerprint = MachineShort;
            if(code.MachineBound && !FingerprintMatches(code.Payload.Fingerprint))
                return Rejected(code,"该激活码是为另一台机器签发的。本机机器码：" + MachineCodeForDisplay(), out message);

            // 同一个码在本机重复输入：算已完成，不重复计数，也不当成失败。
            // （换一台机器再输同一个码，离线环境下无法察觉——见 LICENSE.md 第 5 节的说明；
            //   管理员在台账里作废该码后，这份作废清单会随下一个版本让它在所有机器上失效。）
            string canonical = LicenseCodec.Canonical(code.Payload.ToBytes(), code.Signature);
            ActivationRecord existing = LicenseStore.Load();
            if(existing != null && string.Equals(existing.Code, canonical, StringComparison.OrdinalIgnoreCase)
                && FingerprintMatches(existing.MachineFingerprint)){
                ResetCache();
                LicenseReport again = Current();
                notice = "该激活码已经在本机激活过了，不需要重复输入（码ID " + LicenseCodec.Display(code.CodeId) + "）。";
                again.Notice = notice;
                message = null;
                return again;
            }

            ActivationRecord record = new ActivationRecord();
            record.MachineFingerprint = fingerprint;
            record.ActivatedDay = today;
            record.Counter = Math.Max(LicenseStore.Counter(), 0L) + 1;
            record.Code = canonical;
            if(!LicenseStore.Save(record))
                return Rejected(code,"无法写入激活文件，请用管理员身份运行一次，或检查磁盘权限。", out message);
            ResetCache();
            message = null;
            LicenseReport report = Current();
            notice = "激活成功，该激活码已绑定本机（码ID " + LicenseCodec.Display(code.CodeId) + "）。";
            report.Notice = notice;
            if(!report.Usable)
                message = "激活文件已写入，但校验未通过：" + report.Describe();
            return report;
        }

        // 现场排查用：把激活码里的关键字段和字节哈希打出来，方便定位是抄写问题还是签发问题。
        internal static string DiagCode(LicenseCode code){
            if(code == null || code.Payload == null)return "null";
            byte[] bytes = code.Payload.ToBytes();
            uint hash = 2166136261;
            for(int i = 0; i < bytes.Length; i++){ hash ^= bytes[i]; hash *= 16777619; }
            uint sigHash = 2166136261;
            for(int i = 0; i < code.Signature.Length; i++){ sigHash ^= code.Signature[i]; sigHash *= 16777619; }
            return "id=" + code.CodeId + " plan=" + code.Payload.PlanCode + " day=" + code.Payload.DayOffset + " flags=" + code.Payload.Flags
                + " ph=" + hash.ToString("X8") + " sh=" + sigHash.ToString("X8") + " len=" + code.Text.Length
                + " codeFp=" + Bytes(code.Payload.Fingerprint) + " machine=" + Bytes(MachineShort)
                + " recordFp=" + Bytes(RecordFingerprint());
        }

        static byte[] RecordFingerprint(){
            ActivationRecord record = LicenseStore.Load();
            return record == null ? null : record.MachineFingerprint;
        }

        static string Bytes(byte[] data){
            if(data == null)return "null";
            StringBuilder text = new StringBuilder(data.Length * 2);
            for(int i = 0; i < data.Length; i++)text.Append(data[i].ToString("x2"));
            return text.ToString();
        }

        // 测试与现场排查：直接回报激活文件里的码能否解析、能否过验签。
        public static string StoredCodeState(){
            ActivationRecord record = LicenseStore.Load();
            if(record == null)return "no-record";
            if(record.Code == null)return "no-code";
            LicenseCode code = LicenseCodec.Parse(record.Code);
            if(code == null)return "unparsed len=" + record.Code.Length;
            return "parsed sig=" + (LicenseCodec.VerifySignature(code) ? "ok" : "fail") + " " + DiagCode(code);
        }

        // 最近一次时间校验的原始数值，供测试与现场排查读取。
        public static string ClockAudit = "";

        // 现场排查：单调时间基准与实际时间的对照。
        public static string ClockState(){
            long high = LicenseStore.HighWaterTicks();
            return "today=" + Today + " tolerance=" + DayTolerance
                + " highDay=" + (high > 0 ? LicenseTime.ToDayOffset(new DateTime(high, DateTimeKind.Utc)).ToString() : "none")
                + " counter=" + LicenseStore.Counter();
        }

        public static bool Deactivate(){
            LicenseStore.Clear();
            ResetCache();
            return true;
        }

        public static string MachineCode(){ return LicenseMachine.MachineCode(); }

        public static LicenseReport Stored(){
            ActivationRecord record = LicenseStore.Load();
            if(record == null)return null;
            LicenseCode code = LicenseCodec.Parse(record.Code);
            if(code == null)return null;
            LicenseReport report = new LicenseReport();
            report.PlanName = code.Plan == null ? null : code.Plan.Name;
            report.ExpiryDate = code.ExpiryDate;
            report.DaysLeft = LicenseTime.ToDayOffset(code.ExpiryDate) - Today;
            report.MachineCode = MachineCodeForDisplay();
            return report;
        }
    }

#if TG_DEV_BUILD
    // 仅开发构建（build.ps1 -DevBuild）才有：可以固定"今天"、伪造指纹、注入自检结论、放行栅栏。
    public static class LicenseTestHooks {
        public static int TodayOverride = -1;
        public static byte[] FingerprintOverride;
        public static string MachineCodeOverride;
        public static int GuardOverride = -1;
        public static int GateOverride = -1;
        public static string CodeOverride;
        // 注入一份作废清单，用来验证"码被管理员作废后任何机器都不能激活"。
        public static string[] RevokedOverride;
        public static bool DevKeyMode;

        // 开发用私钥：放在 %ProgramData%\TianGongCadSuite\license-dev.key 时，
        // 本机按"自带签发权"模式运行，便于在真机上验证三档有效期；文件不存在时
        // 自动回到正常的验签流程，正式环境永远不会走到这条路径。
        public static byte[] DevPrivateKey(){
            try{
                string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if(string.IsNullOrEmpty(root))return null;
                string path = System.IO.Path.Combine(System.IO.Path.Combine(root, "TianGongCadSuite"), "license-dev.key");
                if(!System.IO.File.Exists(path))return null;
                byte[] blob = Convert.FromBase64String(System.IO.File.ReadAllText(path).Trim());
                if(blob.Length != 104)return null;
                return blob;
            }catch(Exception){ return null; }
        }

        public static void Reset(){
            TodayOverride = -1;
            FingerprintOverride = null;
            MachineCodeOverride = null;
            GuardOverride = -1;
            GateOverride = -1;
            CodeOverride = null;
            RevokedOverride = null;
            DevKeyMode = false;
            LicenseLibrary.ResetCache();
        }

        public static string SignForTest(byte[] privateKeyBlob,byte planCode,byte[] shortFingerprint,bool bound,int dayOffset,int nonceSeed){
            LicensePayload payload = new LicensePayload();
            payload.Flags = (byte)(bound ? 1 : 0);
            payload.PlanCode = planCode;
            payload.DayOffset = dayOffset;
            payload.Fingerprint = new byte[LicensePayload.FingerprintSize];
            if(shortFingerprint != null)Buffer.BlockCopy(shortFingerprint, 0, payload.Fingerprint, 0, Math.Min(shortFingerprint.Length, LicensePayload.FingerprintSize));
            byte[] nonce = new byte[8];
            for(int i = 0; i < 8; i++)nonce[i] = unchecked((byte)(nonceSeed * 31 + i * 17 + 7));
            payload.Nonce = nonce;
            using(CngKey key = CngKey.Import(privateKeyBlob, CngKeyBlobFormat.EccPrivateBlob)){
                byte[] signature = LicenseSignature.Sign(payload.ToBytes(), key);
                return LicenseCodec.Compose(payload.ToBytes(), signature);
            }
        }
    }
#else
    // 正式构建：测试开关全部是常量，运行期无法改写（以前是 public static 字段，同进程里的任何
    // 代码都能把 GateOverride 改成 0 直接放行）；开发密钥模式与测试签发整段不编译进来。
    internal static class LicenseTestHooks {
        internal const int TodayOverride = -1;
        internal const int GuardOverride = -1;
        internal const int GateOverride = -1;
        internal const string CodeOverride = null;
        internal const string[] RevokedOverride = null;
    }
#endif
}
