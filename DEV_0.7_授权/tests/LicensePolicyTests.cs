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
        static FakeLicenseServer server;

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
            // 联网授权：所有用例都经过进程内的假服务器（规则同 server/tg_license_server.py）。
            server = new FakeLicenseServer();
            server.Install();

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
                OnlineFirstActivation(check);
                OnlineSecondMachine(check);
                OnlineRevocation(check);
                OnlineGrace(check);
                OnlineForgery(check);
                OnlineLegacyRecord(check);
            }finally{
                server.Dispose();
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

        // ---------- 联网授权（DEV 0.8） ----------

        static void Fresh(){
            LicenseTestHooks.TodayOverride = -1;
            LicenseTestHooks.FingerprintOverride = fingerprint;
            server.Offline = false;
            server.ReplayNonce = false;
            server.Quota = 2;
            server.Install();
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
        }

        static void OnlineFirstActivation(Action<string,bool> check){
            Fresh();
            string message;
            string notice;
            string code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 61);
            server.Offline = true;
            LicenseReport report = LicenseLibrary.Activate(code, out message, out notice);
            check("断网时首次激活被拒", !report.Usable && message != null && message.IndexOf("联网") >= 0);
            check("断网激活不写激活文件", LicenseLibrary.Current().Status == LicenseStatus.Missing);
            server.Offline = false;
            int calls = server.Calls;
            report = LicenseLibrary.Activate(code, out message, out notice);
            check("联网后激活成功", message == null && report.Usable);
            check("激活时向服务器登记了一次", server.Calls == calls + 1);
            check("服务器记下了本机", server.MachineOf(LicenseCodec.Parse(code).CodeId) == LicenseOnline.Hex(fingerprint));
            calls = server.Calls;
            check("当天内校验不联网", LicenseLibrary.Current().Usable && server.Calls == calls);
            LicenseTestHooks.TodayOverride = LicenseTime.Today + 2;
            LicenseLibrary.ResetCache();
            check("回执过期一天以上会复核", LicenseLibrary.Current().Usable && server.Calls > calls);
        }

        // 同一个码拿到第二台电脑：主动输码可以转过去（占一次换机），原电脑下次复核即停用；次数用完就拒绝。
        static void OnlineSecondMachine(Action<string,bool> check){
            Fresh();
            server.Quota = 1;
            string message;
            string notice;
            byte[] other = new byte[fingerprint.Length];
            for(int i = 0; i < other.Length; i++)other[i] = (byte)(fingerprint[i] ^ 0x3C);
            string code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 62);
            check("A 机激活", LicenseLibrary.Activate(code, out message, out notice).Usable && message == null);
            ActivationRecord onA = LicenseStore.Load().Clone();

            LicenseTestHooks.FingerprintOverride = other;
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
            LicenseReport onB = LicenseLibrary.Activate(code, out message, out notice);
            check("B 机输同一个码：占用换机次数后激活", onB.Usable && message == null);
            check("B 机提示码是从别的电脑转来的", notice != null && notice.IndexOf("另一台电脑") >= 0);

            // 回到 A 机（把 A 的激活文件放回去），一天后复核：服务器说码已转走。
            LicenseTestHooks.FingerprintOverride = fingerprint;
            LicenseStore.SaveForTest(onA);
            LicenseTestHooks.TodayOverride = LicenseTime.Today + 1;
            LicenseLibrary.ResetCache();
            LicenseReport moved = LicenseLibrary.Current();
            check("A 机复核后授权转出（" + moved.Status + "）", moved.Status == LicenseStatus.MovedAway);
            check("转出后闸门关闭", !LicenseLibrary.Gate());
            check("转出后回退日期也不能恢复", Reload(LicenseTime.Today).Status == LicenseStatus.MovedAway);

            LicenseReport back = LicenseLibrary.Activate(code, out message, out notice);
            check("换机次数用完后 A 机抢不回来", !back.Usable && message != null && message.IndexOf("换机次数") >= 0);
        }

        static LicenseReport Reload(int today){
            LicenseTestHooks.TodayOverride = today;
            LicenseLibrary.ResetCache();
            return LicenseLibrary.Current();
        }

        // 服务器上作废：不发新版本，已激活的机器下次复核即失效；撤销作废后恢复。
        static void OnlineRevocation(Action<string,bool> check){
            Fresh();
            string message;
            string notice;
            string code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 63);
            string id = LicenseCodec.Parse(code).CodeId;
            LicenseLibrary.Activate(code, out message, out notice);
            server.Revoke(id, true);
            check("作废后当天本机仍按回执放行（最迟一天内生效）", Reload(LicenseTime.Today).Usable);
            check("服务器作废在下次复核时生效", Reload(LicenseTime.Today + 1).Status == LicenseStatus.Revoked);
            check("服务器作废的码不能重新激活", !LicenseLibrary.Activate(code, out message, out notice).Usable && message != null && message.IndexOf("作废") >= 0);
            server.Revoke(id, false);
            check("撤销作废后复核恢复", Reload(LicenseTime.Today + 2).Usable);
        }

        // 断网宽限期：7 天内照常用，超过 7 天必须联网一次。
        static void OnlineGrace(Action<string,bool> check){
            Fresh();
            string message;
            string notice;
            string code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 64);
            LicenseLibrary.Activate(code, out message, out notice);
            server.Offline = true;
            check("断网 3 天仍可用", Reload(LicenseTime.Today + 3).Usable);
            check("断网 7 天仍可用", Reload(LicenseTime.Today + 7).Usable);
            LicenseReport late = Reload(LicenseTime.Today + 8);
            check("断网超过 7 天要求联网", late.Status == LicenseStatus.OnlineRequired);
            check("要求联网的文案带原因", late.Describe().IndexOf("联网") >= 0 && late.Describe().IndexOf("原因") >= 0);
            server.Offline = false;
            check("恢复联网后自动复核通过", Reload(LicenseTime.Today + 8).Usable);
            server.Offline = true;
            check("复核成功后宽限期重新计算", Reload(LicenseTime.Today + 15).Usable);
            check("回调时间到服务器日期之前一周以上被识别", Reload(LicenseTime.Today).Status == LicenseStatus.ClockTampered);
        }

        // 伪造/篡改：换一把服务器密钥签的回执、改过的回执、重放旧 nonce，一律不认。
        static void OnlineForgery(Action<string,bool> check){
            Fresh();
            string message;
            string notice;
            string code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 65);
            using(FakeLicenseServer impostor = new FakeLicenseServer()){
                LicenseTestHooks.OnlineTransport = impostor.Handle;   // 插件里仍是真服务器的公钥
                LicenseReport report = LicenseLibrary.Activate(code, out message, out notice);
                check("冒充的服务器签的回执不认", !report.Usable && message != null && message.IndexOf("验签") >= 0);
            }
            server.Install();
            server.ReplayNonce = true;
            check("nonce 对不上的回执不认", !LicenseLibrary.Activate(code, out message, out notice).Usable && message != null);
            server.ReplayNonce = false;
            check("正常激活", LicenseLibrary.Activate(code, out message, out notice).Usable);

            ActivationRecord record = LicenseStore.Load();
            char[] chars = record.Receipt.ToCharArray();
            chars[10] = chars[10] == 'A' ? 'B' : 'A';
            record.Receipt = new string(chars);
            LicenseStore.SaveForTest(record);
            server.Offline = true;
            check("篡改过的回执 + 断网 = 要求联网", Reload(LicenseTime.Today).Status == LicenseStatus.OnlineRequired);
            server.Offline = false;
            check("篡改过的回执 + 联网 = 重新复核后可用", Reload(LicenseTime.Today).Usable);
        }

        // 0.7 离线激活留下的旧激活文件（没有回执）：联网时自动补登记，断网时要求联网。
        static void OnlineLegacyRecord(Action<string,bool> check){
            Fresh();
            ActivationRecord record = new ActivationRecord();
            record.MachineFingerprint = fingerprint;
            record.ActivatedDay = LicenseTime.Today;
            record.Counter = LicenseStore.CounterForTest() + 1;
            record.Code = Sign(LicensePlans.Yearly, null, false, LicenseTime.Today, 66);
            LicenseStore.SaveForTest(record);
            server.Offline = true;
            check("旧版激活文件断网时要求联网", Reload(LicenseTime.Today).Status == LicenseStatus.OnlineRequired);
            server.Offline = false;
            check("旧版激活文件联网后自动登记", Reload(LicenseTime.Today).Usable);
            check("补登记后回执已落盘", LicenseStore.Load().Receipt != null);
            LicenseTestHooks.TodayOverride = -1;
            LicenseLibrary.Deactivate();
            LicenseLibrary.ResetCache();
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
