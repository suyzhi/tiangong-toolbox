using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TianGongCadSuite.Licensing;

namespace TianGongCadSuite.Licensing.Admin {
    public static class LicenseAdminProgram {
        public static int Main(string[] args){
            Console.OutputEncoding = Encoding.UTF8;
            try{
                if(args.Length == 0)return Usage();
                string command = args[0].Trim().ToLowerInvariant();
                switch(command){
                    case "keygen": return Keygen(args);
                    case "new": return New(args);
                    case "batch": return Batch(args);
                    case "verify": return Verify(args);
                    case "list": return List(args);
                    case "mark": return Mark(args);
                    case "revoke": return Revoke(args);
                    case "export": return ExportCommand(args);
                    case "machine": return Machine();
                    case "public": return Public(args);
                    case "plans": return Plans();
                    case "help": case "-h": case "--help": return Usage();
                    default: Console.Error.WriteLine("未知命令：" + command); return Usage();
                }
            }catch(Exception e){
                Console.Error.WriteLine("失败：" + e.Message);
                return 3;
            }
        }

        static int Usage(){
            Console.WriteLine("天工工具箱 激活码管理工具（仅管理员使用）");
            Console.WriteLine();
            Console.WriteLine("签发（不需要用户的机器码，签出来的码用户在目标机激活时自动绑定该机）：");
            Console.WriteLine("  new    <私钥> <档位> [YYYY-MM-DD] [--note 备注]");
            Console.WriteLine("                                     签发一个激活码并记入台账");
            Console.WriteLine("  new    <私钥> <档位> --bind <机器码> [YYYY-MM-DD]");
            Console.WriteLine("                                     签发绑定到指定机器的码（老方案，按需使用）");
            Console.WriteLine("  batch  <私钥> <档位> <数量> [YYYY-MM-DD] [--note 备注]");
            Console.WriteLine("                                     一次签发多个（同一档位）");
            Console.WriteLine();
            Console.WriteLine("台账与作废（管理员主动问用户“激活了吗”，然后在这里登记 / 作废）：");
            Console.WriteLine("  list   <私钥> [issued|activated|void]");
            Console.WriteLine("                                     列出台账与三态统计");
            Console.WriteLine("  mark   <私钥> <码ID|激活码> <issued|activated|void> [--note 备注] [--machine 机器码]");
            Console.WriteLine("                                     登记“已发给谁 / 客户已激活 / 作废”等状态");
            Console.WriteLine("  revoke <私钥> <码ID|激活码|@文件>   作废该码，并重新生成插件里的作废清单");
            Console.WriteLine("  export <私钥> [输出路径]            只按台账重新生成插件里的作废清单");
            Console.WriteLine();
            Console.WriteLine("其它：");
            Console.WriteLine("  verify <激活码|@文件>               校验一个激活码并显示明细（码ID、档位、签发/到期）");
            Console.WriteLine("  verify <私钥> <激活码|@文件>        连台账状态一起显示（是否已作废）");
            Console.WriteLine("  machine                             显示本机机器码（管理员自查用）");
            Console.WriteLine("  public <私钥>                       打印该私钥对应的公钥片段");
            Console.WriteLine("  plans                               列出授权档位");
            Console.WriteLine("  keygen <私钥> [密钥ID]              生成一对新密钥，公钥片段写回插件源码");
            Console.WriteLine();
            Console.WriteLine("台账文件：与私钥同名同目录的 *.ledger.tsv（只在管理员机器上，别提交、别外发）。");
            Console.WriteLine("作废生效范围：作废清单随插件版本分发，只对装了新版本的客户端生效（离线吊销）。");
            return 0;
        }

        static int Plans(){
            foreach(LicensePlan plan in LicensePlans.Catalog)
                Console.WriteLine(plan.Id + "  " + plan.Name + "  " + plan.Days + " 天  (档位编号 " + plan.Code + ")");
            return 0;
        }

        static int Machine(){
            Console.WriteLine("本机机器码：" + LicenseMachine.MachineCode());
            Console.WriteLine("指纹短码  ：" + LicenseMachine.ShortCode());
            Console.WriteLine("环境种子  ：" + LicenseMachine.Seed());
            return 0;
        }

        static int Keygen(string[] args){
            if(args.Length < 2)return Usage();
            string path = Path.GetFullPath(args[1]);
            string keyId = args.Length > 2 ? args[2] : "master";
            if(File.Exists(path)){
                Console.Error.WriteLine("私钥文件已存在，拒绝覆盖：" + path);
                Console.Error.WriteLine("确需轮换请先手工改名备份（注意：换私钥后所有已发出的激活码全部失效）。");
                return 4;
            }
            byte[] record = LicenseAdminKey.CreatePrivateKey(keyId);
            LicenseAdminKey.SavePrivateKey(path, record, keyId);
            byte[] blob = LicenseAdminKey.LoadPrivateKey(path);
            byte[] coordinates = LicenseAdminKey.PublicCoordinates(blob);
            string snippet = Path.ChangeExtension(path, ".public.cs");
            File.WriteAllText(snippet, BuildSlotSource(coordinates, keyId), new UTF8Encoding(false));
            Console.WriteLine("私钥已写入：" + path);
            Console.WriteLine("公钥源码已写入：" + snippet);
            Console.WriteLine("把公钥源码覆盖到插件的 src\\License\\LicenseKeySlot.cs 后重新编译插件。");
            Console.WriteLine("私钥文件务必离线保管，不要提交到代码仓库、不要发给客户。");
            return 0;
        }

        static string BuildSlotSource(byte[] coordinates,string keyId){
            byte[] x = new byte[32];
            byte[] y = new byte[32];
            Buffer.BlockCopy(coordinates, 0, x, 0, 32);
            Buffer.BlockCopy(coordinates, 32, y, 0, 32);
            string seedA = "TGS-SLOT-A-" + keyId;
            string seedB = "TGS-SLOT-B-" + keyId;
            byte[] maskA = LicenseKeyMaterial.Stream(seedA, 32);
            byte[] maskB = LicenseKeyMaterial.Stream(seedB, 32);
            StringBuilder text = new StringBuilder();
            text.Append("// 由 TianGongLicenseAdmin keygen 生成，请勿手工修改。密钥ID：").Append(keyId).Append('\n');
            text.Append("// 这是公钥，可以随源码一起提交；私钥在同名的 .tgkey 文件里，绝不能提交。\n");
            text.Append("namespace TianGongCadSuite.Licensing {\n");
            text.Append("    internal static class LicenseKeySlot {\n");
            text.Append("        internal const string KeyId = \"").Append(keyId).Append("\";\n");
            text.Append("        internal static readonly string MaskX = \"").Append(Hex(maskA)).Append("\";\n");
            text.Append("        internal static readonly string MaskY = \"").Append(Hex(maskB)).Append("\";\n");
            text.Append("        internal static readonly string SeedX = \"").Append(seedA).Append("\";\n");
            text.Append("        internal static readonly string SeedY = \"").Append(seedB).Append("\";\n");
            text.Append("        internal static readonly string ValueX = \"").Append(Convert.ToBase64String(x)).Append("\";\n");
            text.Append("        internal static readonly string ValueY = \"").Append(Convert.ToBase64String(y)).Append("\";\n");
            text.Append("    }\n");
            text.Append("}\n");
            return text.ToString();
        }

        static string Hex(byte[] data){
            StringBuilder text = new StringBuilder(data.Length * 2);
            for(int i = 0; i < data.Length; i++)text.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        // ---------- 签发 ----------

        static int New(string[] args){
            if(args.Length < 3)return Usage();
            LicensePlan plan = RequirePlan(args[2]);
            if(plan == null)return 5;
            string keyPath = Path.GetFullPath(args[1]);
            byte[] key = LicenseAdminKey.LoadPrivateKey(keyPath);
            string bind = Option(args, "--bind");
            int? issueDay = null;
            for(int i = 3; i < args.Length; i++){
                string arg = args[i];
                if(arg.StartsWith("--", StringComparison.Ordinal)){ i++; continue; }
                if(IsDate(arg)){ issueDay = ParseDay(arg); continue; }
                if(string.IsNullOrEmpty(bind)){ bind = arg; continue; }
                Console.Error.WriteLine("多余的参数（已忽略）：" + arg);
            }
            string note = Option(args, "--note");
            string detail;
            string code = LicenseAdminKey.Create(key, plan, bind, issueDay, out detail);
            if(code == null){ Console.Error.WriteLine("签发失败：" + detail); return 6; }
            return RecordAndPrint(keyPath, plan, code, bind, note, detail);
        }

        static int Batch(string[] args){
            if(args.Length < 4)return Usage();
            LicensePlan plan = RequirePlan(args[2]);
            if(plan == null)return 5;
            string keyPath = Path.GetFullPath(args[1]);
            byte[] key = LicenseAdminKey.LoadPrivateKey(keyPath);
            string note = Option(args, "--note");
            string third = args[3];
            int count = 0;
            bool countMode = int.TryParse(third, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0 && count <= 500;
            List<string> machines = new List<string>();
            if(!countMode){
                foreach(string item in third.Split(new char[]{ ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))machines.Add(item);
            }
            int? issueDay = null;
            for(int i = 4; i < args.Length; i++){
                string arg = args[i];
                if(arg.StartsWith("--", StringComparison.Ordinal)){ i++; continue; }
                if(IsDate(arg)){ issueDay = ParseDay(arg); continue; }
                Console.Error.WriteLine("多余的参数（已忽略）：" + arg);
            }
            int total = countMode ? count : machines.Count;
            if(total <= 0){ Console.Error.WriteLine("数量不对。"); return 6; }
            for(int i = 0; i < total; i++){
                string machine = countMode ? null : machines[i];
                string detail;
                string code = LicenseAdminKey.Create(key, plan, machine, issueDay, out detail);
                if(code == null){ Console.Error.WriteLine("签发失败：" + detail); return 6; }
                RecordAndPrint(keyPath, plan, code, machine, note, detail);
            }
            Console.WriteLine("共签发 " + total + " 个。台账：" + LicenseLedger.PathForKey(keyPath));
            return 0;
        }

        static int RecordAndPrint(string keyPath,LicensePlan plan,string code,string machineCode,string note,string detail){
            LicenseCode parsed = LicenseCodec.Parse(code);
            LedgerEntry entry = new LedgerEntry();
            entry.CodeId = parsed == null ? LicenseLedger.ResolveCodeId(code) : parsed.CodeId;
            entry.Plan = plan.Id;
            entry.IssueDay = parsed == null ? LicenseTime.Today : parsed.Payload.DayOffset;
            entry.ExpiryDay = parsed == null ? entry.IssueDay + plan.Days : LicenseTime.ToDayOffset(parsed.ExpiryDate);
            entry.Status = LicenseLedger.Issued;
            entry.Machine = machineCode ?? "";
            entry.Note = note ?? "";
            entry.Updated = Stamp();
            entry.Code = code;
            LicenseLedger.Append(keyPath, entry);
            PrintCode(plan, entry, code, detail);
            return 0;
        }

        static void PrintCode(LicensePlan plan,LedgerEntry entry,string code,string detail){
            Console.WriteLine("码ID    ：" + entry.DisplayId);
            Console.WriteLine("档位    ：" + plan.Name + "（" + plan.Days + " 天）");
            Console.WriteLine("签发日期：" + entry.IssueDate);
            Console.WriteLine("到期日期：" + entry.ExpiryDate);
            Console.WriteLine("机器绑定：" + (string.IsNullOrEmpty(entry.Machine) ? "否（用户在目标机激活时自动绑定该机器）" : entry.Machine));
            if(!string.IsNullOrEmpty(entry.Note))Console.WriteLine("备注    ：" + entry.Note);
            Console.WriteLine("激活码  ：");
            Console.WriteLine(code);
            if(!string.IsNullOrEmpty(detail))Console.WriteLine("说明    ：" + detail);
            Console.WriteLine();
        }

        // ---------- 台账与作废 ----------

        static int List(string[] args){
            if(args.Length < 2)return Usage();
            string keyPath = Path.GetFullPath(args[1]);
            string filter = args.Length > 2 ? args[2].Trim().ToLowerInvariant() : null;
            List<LedgerEntry> rows = LicenseLedger.Load(keyPath);
            Console.WriteLine("台账：" + LicenseLedger.PathForKey(keyPath));
            if(rows.Count == 0){ Console.WriteLine("（空）"); return 0; }
            Console.WriteLine("码ID      档位 签发日      到期日      状态       机器码 / 备注");
            for(int i = 0; i < rows.Count; i++){
                LedgerEntry row = rows[i];
                if(filter != null && !string.Equals(row.Status, filter, StringComparison.OrdinalIgnoreCase))continue;
                string tail = string.IsNullOrEmpty(row.Machine) ? row.Note : (row.Machine + (string.IsNullOrEmpty(row.Note) ? "" : " " + row.Note));
                Console.WriteLine(Pad(row.DisplayId, 10) + Pad(row.Plan, 4) + Pad(row.IssueDate, 12) + Pad(row.ExpiryDate, 12) + Pad(row.Status, 10) + tail);
            }
            Console.WriteLine();
            Console.WriteLine("合计 " + rows.Count + " 条：已签发 " + LicenseLedger.CountStatus(rows, LicenseLedger.Issued)
                + " / 已激活 " + LicenseLedger.CountStatus(rows, LicenseLedger.Activated)
                + " / 已作废 " + LicenseLedger.CountStatus(rows, LicenseLedger.Void));
            return 0;
        }

        static int Mark(string[] args){
            if(args.Length < 4)return Usage();
            string keyPath = Path.GetFullPath(args[1]);
            string codeId = LicenseLedger.ResolveCodeId(args[2]);
            if(codeId == null){ Console.Error.WriteLine("无法识别码ID或激活码：" + args[2]); return 10; }
            string status = NormalizeStatus(args[3]);
            if(status == null){ Console.Error.WriteLine("状态只能是 issued / activated / void。"); return 11; }
            List<LedgerEntry> rows = LicenseLedger.Load(keyPath);
            LedgerEntry entry = LicenseLedger.Find(rows, codeId);
            if(entry == null){
                entry = new LedgerEntry();
                entry.CodeId = codeId;
                entry.Plan = "?";
                entry.IssueDay = LicenseTime.Today;
                entry.ExpiryDay = LicenseTime.Today;
                rows.Add(entry);
                Console.WriteLine("提示：台账里没有这个码ID，已按手工登记补一条。");
            }
            entry.Status = status;
            entry.Updated = Stamp();
            string note = Option(args, "--note");
            if(note != null)entry.Note = note;
            string machine = Option(args, "--machine");
            if(machine != null)entry.Machine = machine;
            LicenseLedger.Save(keyPath, rows);
            Console.WriteLine("已更新：" + entry.DisplayId + " → " + entry.Status + "（" + entry.Updated + "）");
            if(string.Equals(status, LicenseLedger.Void, StringComparison.OrdinalIgnoreCase)){
                int rc = ExportLedger(keyPath, null);
                if(rc != 0)return rc;
                Console.WriteLine("提醒：作废要真正生效，必须重新编译插件（tools\\build.ps1）并把新版本发给客户。");
            }
            return 0;
        }

        static int Revoke(string[] args){
            if(args.Length < 3)return Usage();
            string keyPath = Path.GetFullPath(args[1]);
            string text = ReadCodeArg(args[2]);
            if(text == null)return 7;
            string codeId = LicenseLedger.ResolveCodeId(text);
            if(codeId == null){ Console.Error.WriteLine("无法识别码ID或激活码。"); return 10; }
            List<LedgerEntry> rows = LicenseLedger.Load(keyPath);
            LedgerEntry entry = LicenseLedger.Find(rows, codeId);
            if(entry == null){
                entry = new LedgerEntry();
                entry.CodeId = codeId;
                entry.Plan = "?";
                entry.IssueDay = LicenseTime.Today;
                entry.ExpiryDay = LicenseTime.Today;
                entry.Code = LicenseCodec.Parse(text) == null ? "" : text;
                rows.Add(entry);
                Console.WriteLine("提示：台账里没有这个码ID，已按手工作废登记。");
            }
            entry.Status = LicenseLedger.Void;
            entry.Updated = Stamp();
            string note = Option(args, "--note");
            if(note != null)entry.Note = note;
            LicenseLedger.Save(keyPath, rows);
            Console.WriteLine("已作废：" + entry.DisplayId);
            int rc = ExportLedger(keyPath, null);
            if(rc != 0)return rc;
            Console.WriteLine("提醒：作废要真正生效，必须重新编译插件（tools\\build.ps1）并把新版本发给客户；");
            Console.WriteLine("      已经发出去的旧版本插件无法被即时吊销——离线环境没有联网心跳。");
            return 0;
        }

        static int ExportCommand(string[] args){
            if(args.Length < 2)return Usage();
            string keyPath = Path.GetFullPath(args[1]);
            string outPath = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
            return ExportLedger(keyPath, outPath);
        }

        static int ExportLedger(string keyPath,string outPath){
            List<LedgerEntry> rows = LicenseLedger.Load(keyPath);
            string source = LicenseLedger.BuildRevokedSource(rows, Stamp());
            if(string.IsNullOrEmpty(outPath)){
                string root = LicenseLedger.FindPluginRoot(AppDomain.CurrentDomain.BaseDirectory);
                if(root == null){
                    Console.Error.WriteLine("没找到插件源码目录，请显式指定输出路径：export <私钥> <输出路径>");
                    return 12;
                }
                outPath = Path.Combine(Path.Combine(root, "src"), Path.Combine("License", "LicenseRevoked.cs"));
            }
            File.WriteAllText(outPath, source, new UTF8Encoding(true));
            Console.WriteLine("作废清单已写入：" + outPath + "（作废 " + LicenseLedger.CountStatus(rows, LicenseLedger.Void) + " 个）");
            return 0;
        }

        // ---------- 校验与工具 ----------

        static int Verify(string[] args){
            if(args.Length < 2)return Usage();
            string keyPath = null;
            string text;
            if(args.Length >= 3){
                keyPath = Path.GetFullPath(args[1]);
                text = ReadCodeArg(args[2]);
            }else{
                text = ReadCodeArg(args[1]);
            }
            if(text == null)return 7;
            string detail;
            string report = LicenseAdminKey.Inspect(text, out detail);
            if(report == null){ Console.Error.WriteLine("校验失败：" + detail); return 8; }
            Console.Write(report);
            if(keyPath != null && File.Exists(keyPath)){
                string codeId = LicenseLedger.ResolveCodeId(text);
                LedgerEntry entry = LicenseLedger.Find(LicenseLedger.Load(keyPath), codeId);
                Console.WriteLine("台账状态  : " + (entry == null
                    ? "台账里没有这个码ID"
                    : entry.Status + "（" + entry.Updated + "）" + (string.IsNullOrEmpty(entry.Note) ? "" : " " + entry.Note)));
            }
            return detail == null ? 0 : 9;
        }

        static int Public(string[] args){
            if(args.Length < 2)return Usage();
            byte[] blob = LicenseAdminKey.LoadPrivateKey(Path.GetFullPath(args[1]));
            byte[] coordinates = LicenseAdminKey.PublicCoordinates(blob);
            byte[] x = new byte[32];
            byte[] y = new byte[32];
            Buffer.BlockCopy(coordinates, 0, x, 0, 32);
            Buffer.BlockCopy(coordinates, 32, y, 0, 32);
            Console.WriteLine("X = " + Convert.ToBase64String(x));
            Console.WriteLine("Y = " + Convert.ToBase64String(y));
            return 0;
        }

        static string ReadCodeArg(string text){
            if(string.IsNullOrEmpty(text))return null;
            if(text.StartsWith("@", StringComparison.Ordinal)){
                string path = Path.GetFullPath(text.Substring(1));
                if(!File.Exists(path)){ Console.Error.WriteLine("找不到文件：" + path); return null; }
                return File.ReadAllText(path);
            }
            return text;
        }

        static string Option(string[] args,string name){
            for(int i = 0; i < args.Length - 1; i++){
                if(string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))return args[i + 1];
            }
            return null;
        }

        static string NormalizeStatus(string text){
            if(string.IsNullOrEmpty(text))return null;
            string value = text.Trim().ToLowerInvariant();
            if(value == "issued" || value == "activated" || value == "void")return value;
            if(value == "已签发" || value == "签发")return LicenseLedger.Issued;
            if(value == "已激活" || value == "激活")return LicenseLedger.Activated;
            if(value == "作废" || value == "已作废")return LicenseLedger.Void;
            return null;
        }

        static string Stamp(){ return DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }

        static string Pad(string text,int width){
            string value = text ?? "";
            if(value.Length >= width)return value + " ";
            return value + new string(' ', width - value.Length);
        }

        static bool IsDate(string text){
            if(string.IsNullOrEmpty(text))return false;
            DateTime parsed;
            return DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }

        static LicensePlan RequirePlan(string text){
            LicensePlan plan = LicensePlans.FindById(text);
            if(plan == null){
                Console.Error.WriteLine("档位只能是 M（一个月）/ H（半年）/ Y（一年）。");
            }
            return plan;
        }

        static int? ParseDay(string text){
            DateTime parsed;
            if(!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)){
                Console.Error.WriteLine("日期格式应为 YYYY-MM-DD，已忽略：" + text);
                return null;
            }
            return LicenseTime.ToDayOffset(parsed);
        }
    }
}
