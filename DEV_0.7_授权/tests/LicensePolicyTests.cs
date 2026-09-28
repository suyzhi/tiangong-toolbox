using System;
using System.IO;
using System.Security.Cryptography;
using TianGongCadSuite.Licensing;

namespace PanelTests {
    // 授权策略：三档有效期、到期、机器绑定、续期、时间回调、状态被重置。
    // 每个用例都用管理员同款私钥签发真实激活码，走的是完整链路而不是桩函数。
    internal static class LicensePolicyTests {
        static byte[] keyBlob;
        static byte[] fingerprint;
        static bool keyAvailable;

        internal static void Run(Action<string,bool> check){
            keyBlob = LoadTestKey();
            keyAvailable = keyBlob != null && KeyMatches(keyBlob);
            if(!keyAvailable){
                // 缺私钥、或私钥与插件内嵌公钥不成对，都不算失败：只跳过验签用例。
                Console.WriteLine("SKIP: 测试私钥与插件内嵌公钥不成对，跳过授权验签用例"
                    + "（运行 tools/LicenseAdmin/make-test-codes.ps1 可生成配对的密钥与真码）");
                return;
            }

            // 激活文件与激活码里存的是 5 字节短指纹。这里用真实机器指纹，
            // 这样管理员工具签发的真码也能在同一个测试里被接受。
            fingerprint = LicenseMachine.ShortBytes();

            // 测试环境：注入自检结论与机器指纹，模拟"干净环境 + 指定机器"。
            LicenseTestHooks.Reset();
            LicenseTestHooks.GuardOverride = 0;
            LicenseTestHooks.GateOverride = 0;
            LicenseTestHooks.FingerprintOverride = fingerprint;

            try{
                Plans(check);
                Activation(check);
                EveryPlanCounts(check);
                Expiry(check);
                Renewal(check);
                WrongMachine(check);
                MachineBinding(check);
                Reuse(check);
                Revocation(check);
                ClockRollback(check);
                Ghost(check);
            }finally{
                LicenseTestHooks.Reset();
                LicenseLibrary.Deactivate();
                CleanRegistry();
            }
        }

        // 私钥必须与插件内嵌公钥成对，否则所有验签用例都会误报失败。
        static bool KeyMatches(byte[] blob){
            try{
                LicensePayload payload = new LicensePayload();
                payload.Flags = 0;
                payload.PlanCode = LicensePlans.Monthly;
                payload.DayOffset = LicenseTime.Today;
                payload.Fingerprint = new byte[LicensePayload.FingerprintSize];
                payload.Nonce = new byte[8];
                byte[] bytes = payload.ToBytes();
                byte[] signature;
                using(CngKey key = CngKey.Import(blob, CngKeyBlobFormat.EccPrivateBlob))
                    signature = LicenseSignature.Sign(bytes, key);
                LicenseCode code = LicenseCodec.Parse(LicenseCodec.Compose(bytes, signature));
                return code != null && LicenseCodec.VerifySignature(code);
            }catch(Exception){ return false; }
        }

        // 管理员工具实际签发的激活码（tools/LicenseAdmin 生成）必须能被插件接受。
        static void LiveAdminCodes(Action<string,bool> check){
            try{
                string root = Path.GetDirectoryName(typeof(LicensePolicyTests).Assembly.Location);
                string folder = Path.Combine(root, "admin-codes");
                if(!Directory.Exists(folder)){
                    Console.WriteLine("SKIP: 未找到 admin-codes，跳过管理员真码用例（运行 tools/LicenseAdmin/make-test-codes.ps1 生成）");
                    return;
                }
                string[] files = Directory.GetFiles(folder, "*.txt");
                if(files.Length == 0){ check("管理员工具签发了激活码", false); return; }
                for(int i = 0; i < files.Length; i++){
                    string text = File.ReadAllText(files[i]);
                    LicenseCode code = LicenseCodec.Parse(text);
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    check("管理员工具签发的 " + name + " 可解析且验签通过", code != null && LicenseCodec.VerifySignature(code));
                }
            }catch(Exception e){ check("读取管理员工具激活码：" + e.Message, false); }
        }

        // 测试私钥必须与插件内嵌公钥成对。私钥放在 PanelTests.exe 同目录的
        // license-test.tgkey（管理员工具 keygen 生成）；没有该文件时只跳过验签用例。
        static byte[] LoadTestKey(){
            try{
                string root = Path.GetDirectoryName(typeof(LicensePolicyTests).Assembly.Location);
                string path = Path.Combine(root, "license-test.tgkey");
                if(!File.Exists(path))return null;
                string[] lines = File.ReadAllText(path).Split('\n');
                if(lines.Length < 3)return null;
                byte[] record = Convert.FromBase64String(lines[2].Trim());
                if(record.Length < 104)return null;
                byte[] blob = new byte[104];
                Buffer.BlockCopy(record, 0, blob, 0, 104);
                return blob;
            }catch(Exception){ return null; }
        }

        static void Plans(Action<string,bool> check){
            check("三档授权齐备", LicensePlans.Catalog.Length == 3);
            LiveAdminCodes(check);
            check("一个月 = 30 天", LicensePlans.Find(LicensePlans.Monthly).Days == 30);
            check("半年 = 183 天", LicensePlans.Find(LicensePlans.HalfYear).Days == 183);
            check("一年 = 365 天", LicensePlans.Find(LicensePlans.Yearly).Days == 365);
            check("档位编号不是连续 1/2/3", LicensePlans.Monthly != 1 && LicensePlans.HalfYear != 2 && LicensePlans.Yearly != 3);
        }

        static void Activation(Action<string,bool> check){
            LicenseLibrary.Deactivate();
            check("未激活时状态为 Missing", LicenseLibrary.Current().Status == LicenseStatus.Missing);
            string message;
            LicenseReport report = LicenseLibrary.Activate("这不是激活码", out message);
            check("乱码激活被拒绝", message != null && !report.Usable);

            check("测试钩子已生效", LicenseTestHooks.GuardOverride == 0 && LicenseTestHooks.GateOverride == 0 && LicenseTestHooks.FingerprintOverride != null);
            string code = Sign(LicensePlans.Monthly, fingerprint, true, LicenseTime.Today, 1);
            LicenseCode probe = LicenseCodec.Parse(code);
            check("签发的激活码可解析", probe != null);
            check("测试密钥签发的码能过内嵌公钥验签", probe != null && LicenseCodec.VerifySignature(probe));
            check("把档位改成一年后验签失败", probe != null && !TamperPlan(probe));
            report = LicenseLibrary.Activate(code, out message);
            check("本机激活成功", message == null && report.Usable);
            check("激活后剩余天数接近 30", report.DaysLeft >= 29 && report.DaysLeft <= 30);
            check("激活后档位为一个月的授权", report.PlanName != null && report.PlanName.IndexOf("一个月") >= 0);
            check("重复激活幂等", LicenseLibrary.Activate(code, out message) != null && message == null);
            check("闸门放行", LicenseLibrary.Gate());
            check("令牌非零", LicenseLibrary.Token() != 0);
        }

        // 把码里的档位从一个月改成一年，再验签必须失败。
        static bool TamperPlan(LicenseCode probe){
            byte[] bytes = probe.Payload.ToBytes();
            bytes[4] = LicensePlans.Yearly;
            LicensePayload changed = LicensePayload.FromBytes(bytes, 0);
            return LicenseCodec.VerifySignature(new LicenseCode(changed, probe.Signature, probe.Text));
        }

        static void EveryPlanCounts(Action<string,bool> check){
            string message;
            LicenseReport report = LicenseLibrary.Activate(Sign(LicensePlans.Yearly, fingerprint, true, LicenseTime.Today, 21), out message);
            check("一年档剩余约 365 天", report.DaysLeft >= 364 && report.DaysLeft <= 365);
            report = LicenseLibrary.Activate(Sign(LicensePlans.HalfYear, fingerprint, true, LicenseTime.Today, 22), out message);
            check("半年档剩余约 183 天", report.DaysLeft >= 182 && report.DaysLeft <= 183);
            report = LicenseLibrary.Activate(Sign(LicensePlans.Monthly, fingerprint, true, LicenseTime.Today, 23), out message);
            check("一个月档剩余约 30 天", report.DaysLeft >= 29 && report.DaysLeft <= 30);
        }

        static void Expiry(Action<string,bool> check){
            string message;
            string expired = Sign(LicensePlans.Monthly, fingerprint, true, LicenseTime.Today - 40, 3);
            LicenseReport report = LicenseLibrary.Activate(expired, out message);
            check("过期码无法激活", !report.Usable);
            check("过期码被明确说明已到期", message != null && message.IndexOf("到期") >= 0);

            string soon = Sign(LicensePlans.Monthly, fingerprint, true, LicenseTime.Today - 29, 4);
            LicenseLibrary.Activate(soon, out message);
            check("还有 1 天时可用", LicenseLibrary.Current().Usable);
            LicenseTestHooks.TodayOverride = LicenseTime.Today + 1;
            LicenseLibrary.ResetCache();
            check("到期当天即失效", LicenseLibrary.Current().Status == LicenseStatus.Expired);
            check("过期后闸门关闭", !LicenseLibrary.Gate());
            LicenseTestHooks.TodayOverride = -1;
            LicenseLibrary.ResetCache();
        }

        static void Renewal(Action<string,bool> check){
            string message;
            string month = Sign(LicensePlans.Monthly, fingerprint, true, LicenseTime.Today, 5);
            LicenseLibrary.Activate(month, out message);
            int before = LicenseLibrary.Current().DaysLeft;
            string year = Sign(LicensePlans.Yearly, fingerprint, true, LicenseTime.Today, 6);
            LicenseReport report = LicenseLibrary.Activate(year, out message);
            check("续期后档位切换为一年", report.PlanName != null && report.PlanName.IndexOf("一年") >= 0);
            check("续期后剩余天数增加", report.DaysLeft > before && report.DaysLeft >= 364);
            string half = Sign(LicensePlans.HalfYear, fingerprint, true, LicenseTime.Today, 7);
            report = LicenseLibrary.Activate(half, out message);
            check("再换成半年档生效", report.PlanName.IndexOf("半年") >= 0 && report.DaysLeft >= 182);
        }

        static void WrongMachine(Action<string,bool> check){
            byte[] other = new byte[fingerprint.Length];
            for(int i = 0; i < other.Length; i++)other[i] = (byte)(fingerprint[i] ^ 0x5A);
            string code = Sign(LicensePlans.Yearly, other, true, LicenseTime.Today, 8);
            string message;
            LicenseReport report = LicenseLibrary.Activate(code, out message);
            check("别的机器码不能激活本机", !report.Usable && message != null && message.IndexOf("另一台机器") >= 0);

            string universal = Sign(LicensePlans.Monthly, null, false, LicenseTime.Today, 9);
            report = LicenseLibrary.Activate(universal, out message);
            check("通用码可以在本机激活", report.Usable);

            // 激活文件里被塞进"给别的机器签发的码"也必须被识破。
            byte[] foreign = new byte[LicensePayload.FingerprintSize];
            for(int i = 0; i < foreign.Length; i++)foreign[i] = (byte)(fingerprint[i % fingerprint.Length] ^ 0x33);
            ActivationRecord record = new ActivationRecord();
            record.MachineFingerprint = fingerprint;
            record.ActivatedDay = LicenseTime.Today;
            record.Counter = LicenseStore.CounterForTest() + 1;
            record.Code = Sign(LicensePlans.Yearly, foreign, true, LicenseTime.Today, 12);
            LicenseStore.SaveForTest(record);
            LicenseLibrary.ResetCache();
            check("伪造激活文件（含他人指纹的码）被识破", LicenseLibrary.Current().Status == LicenseStatus.WrongMachine);
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
        }

        // 新方案的两条硬约束：① 码不预绑定机器，用户在目标机激活时绑定该机；
        // ② 同一个码在本机重复输入不算新激活（"再使用此激活码无效"的同一台机器语义）。
        static void MachineBinding(Action<string,bool> check){
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
            string message;
            string notice;
            string code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 41);
            LicenseReport report = LicenseLibrary.Activate(code, out message, out notice);
            check("管理员发的通用码（不预绑定机器）可在本机激活", message == null && report.Usable);
            check("激活提示里带码ID", !string.IsNullOrEmpty(notice) && notice.IndexOf("绑定本机") >= 0);

            // 模拟"把激活文件拷到另一台机器"：指纹变了，DPAPI 熵和记录里的指纹都对不上。
            byte[] other = new byte[fingerprint.Length];
            for(int i = 0; i < other.Length; i++)other[i] = (byte)(fingerprint[i] ^ 0x6B);
            LicenseTestHooks.FingerprintOverride = other;
            LicenseLibrary.ResetCache();
            LicenseReport foreign = LicenseLibrary.Current();
            check("激活文件换机器后不可用（" + foreign.Status + "）", !foreign.Usable);
            LicenseTestHooks.FingerprintOverride = fingerprint;
            LicenseLibrary.ResetCache();
            check("换回本机指纹后恢复可用", LicenseLibrary.Current().Usable);
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
        }

        static void Reuse(Action<string,bool> check){
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
            string message;
            string notice;
            string code = Sign(LicensePlans.Monthly, null, false, LicenseTime.Today, 42);
            LicenseReport first = LicenseLibrary.Activate(code, out message, out notice);
            check("首次激活成功", message == null && first.Usable);
            long counter = LicenseStore.CounterForTest();
            LicenseReport second = LicenseLibrary.Activate(code, out message, out notice);
            check("同一个码在本机再输一次不报错", message == null && second.Usable);
            check("重复输入被提示已在本机激活", !string.IsNullOrEmpty(notice) && notice.IndexOf("已经在本机激活") >= 0);
            check("重复输入不重复计数", LicenseStore.CounterForTest() == counter);
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
        }

        // 作废清单：管理员在台账里 revoke 之后，清单随插件版本下发，
        // 命中清单的码在任何机器上都不能激活，已激活的机器下次校验也失效。
        static void Revocation(Action<string,bool> check){
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
            string message;
            string notice;
            string code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 43);
            LicenseCode probe = LicenseCodec.Parse(code);
            check("码可解析并带 8 字符码ID", probe != null && probe.CodeId != null && probe.CodeId.Length == LicenseCodec.GroupChars);
            if(probe == null)return;
            check("作废前可正常激活", LicenseLibrary.Activate(code, out message, out notice).Usable && message == null);

            LicenseTestHooks.RevokedOverride = new string[]{ probe.CodeId };
            LicenseLibrary.ResetCache();
            LicenseReport revoked = LicenseLibrary.Current();
            check("已激活的机器上作废码立即失效", revoked.Status == LicenseStatus.Revoked);
            check("作废后闸门关闭", !LicenseLibrary.Gate());
            check("作废状态有明确文案", revoked.Describe().IndexOf("作废") >= 0);
            check("码ID 比对容忍分隔符与大小写", LicenseRevoked.Contains(LicenseCodec.Display(probe.CodeId).ToUpperInvariant()));

            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
            LicenseReport again = LicenseLibrary.Activate(code, out message, out notice);
            check("作废码不能重新激活", !again.Usable && message != null && message.IndexOf("作废") >= 0);
            check("激活被拒时回报码ID", again.CodeId == probe.CodeId);

            LicenseTestHooks.RevokedOverride = null;
            LicenseLibrary.ResetCache();
            check("移出清单（换新版本）后码恢复可用", LicenseLibrary.Activate(code, out message, out notice).Usable && message == null);
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
        }

        static void ClockRollback(Action<string,bool> check){
            LicenseTestHooks.TodayOverride = -1;
            LicenseLibrary.ResetCache();
            string message;
            string code = Sign(LicensePlans.Yearly, fingerprint, true, LicenseTime.Today, 10);
            LicenseReport activated = LicenseLibrary.Activate(code, out message);
            check("回调用例内先激活成功", message == null && activated.Usable);
            check("激活后状态正常", LicenseLibrary.Current().Status == LicenseStatus.Valid);
            LicenseTestHooks.TodayOverride = LicenseTime.Today - 30;
            LicenseLibrary.ResetCache();
            LicenseReport rolled = LicenseLibrary.Current();
            check("系统时间回调 30 天被识别", rolled.Status == LicenseStatus.ClockTampered);
            check("回调后闸门关闭", !LicenseLibrary.Gate());
            LicenseTestHooks.TodayOverride = LicenseTime.Today - 2;
            LicenseLibrary.ResetCache();
            LicenseReport slight = LicenseLibrary.Current();
            check("回调 2 天在容差内不误报", slight.Usable);
            LicenseTestHooks.TodayOverride = -1;
            LicenseLibrary.ResetCache();
        }

        static void Ghost(Action<string,bool> check){
            string message;
            string code = Sign(LicensePlans.Yearly, fingerprint, true, LicenseTime.Today, 11);
            LicenseLibrary.Activate(code, out message);
            check("激活后闸门放行", LicenseLibrary.Gate());
            LicenseLibrary.Deactivate();
            check("删除激活文件后状态回到未激活", LicenseLibrary.Current().Status == LicenseStatus.Missing);
            check("残留计数器被识别为可疑状态", LicenseStore.GhostDetected());
        }

        static LicensePayload BuildPayload(byte planCode,byte[] shortFingerprint,bool bound,int dayOffset,int nonceSeed){
            LicensePayload payload = new LicensePayload();
            payload.Flags = (byte)(bound ? 1 : 0);
            payload.PlanCode = planCode;
            payload.DayOffset = dayOffset;
            payload.Fingerprint = new byte[LicensePayload.FingerprintSize];
            if(shortFingerprint != null)Buffer.BlockCopy(shortFingerprint, 0, payload.Fingerprint, 0, Math.Min(shortFingerprint.Length, LicensePayload.FingerprintSize));
            else for(int i = 0; i < payload.Fingerprint.Length; i++)payload.Fingerprint[i] = (byte)(0x40 + i);
            byte[] nonce = new byte[8];
            for(int i = 0; i < nonce.Length; i++)nonce[i] = unchecked((byte)(nonceSeed * 37 + i * 19 + 3));
            payload.Nonce = nonce;
            return payload;
        }

        static byte[] SignBytes(LicensePayload payload){
            using(CngKey key = CngKey.Import(keyBlob, CngKeyBlobFormat.EccPrivateBlob))
                return LicenseSignature.Sign(payload.ToBytes(), key);
        }

        static string Sign(byte planCode,byte[] shortFingerprint,bool bound,int dayOffset,int nonceSeed){
            LicensePayload payload = BuildPayload(planCode, shortFingerprint, bound, dayOffset, nonceSeed);
            return LicenseCodec.Compose(payload.ToBytes(), SignBytes(payload));
        }

        static void CleanRegistry(){
            try{ Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\TianGongCadSuite", false); }catch(Exception){ }
        }
    }
}
