using System;
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
            Console.WriteLine("  keygen  <私钥文件> [密钥ID]        生成一对新密钥，公钥片段写回插件源码");
            Console.WriteLine("  new     <私钥文件> <档位> <机器码> [YYYY-MM-DD]");
            Console.WriteLine("                                     签发一个激活码（档位 M=一个月 H=半年 Y=一年）");
            Console.WriteLine("  batch   <私钥文件> <档位> <机器码1,机器码2,...> [YYYY-MM-DD]");
            Console.WriteLine("                                     一次签发多个（同一档位）");
            Console.WriteLine("  verify  <激活码|@文件>             校验一个激活码并显示明细");
            Console.WriteLine("  machine                            显示本机机器码（管理员自查用）");
            Console.WriteLine("  public  <私钥文件>                 打印该私钥对应的公钥片段");
            Console.WriteLine("  plans                              列出授权档位");
            Console.WriteLine();
            Console.WriteLine("机器码由用户端插件界面显示，交给管理员后按机器签发。");
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

        static int New(string[] args){
            if(args.Length < 4)return Usage();
            LicensePlan plan = RequirePlan(args[2]);
            if(plan == null)return 5;
            string machineCode = args[3];
            int? issueDay = null;
            if(args.Length > 4)issueDay = ParseDay(args[4]);
            string detail;
            byte[] key = LicenseAdminKey.LoadPrivateKey(Path.GetFullPath(args[1]));
            string code = LicenseAdminKey.Create(key, plan, machineCode, issueDay, out detail);
            if(code == null){ Console.Error.WriteLine("签发失败：" + detail); return 6; }
            PrintCode(plan, machineCode, code, detail);
            return 0;
        }

        static int Batch(string[] args){
            if(args.Length < 4)return Usage();
            LicensePlan plan = RequirePlan(args[2]);
            if(plan == null)return 5;
            int? issueDay = args.Length > 4 ? ParseDay(args[4]) : (int?)null;
            byte[] key = LicenseAdminKey.LoadPrivateKey(Path.GetFullPath(args[1]));
            string[] machines = args[3].Split(new char[]{ ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for(int i = 0; i < machines.Length; i++){
                string detail;
                string code = LicenseAdminKey.Create(key, plan, machines[i], issueDay, out detail);
                Console.WriteLine("== " + machines[i]);
                if(code == null){ Console.Error.WriteLine("   签发失败：" + detail); continue; }
                PrintCode(plan, machines[i], code, detail);
            }
            return 0;
        }

        static int Verify(string[] args){
            if(args.Length < 2)return Usage();
            string text = args[1];
            if(text.StartsWith("@", StringComparison.Ordinal)){
                string path = Path.GetFullPath(text.Substring(1));
                if(!File.Exists(path)){ Console.Error.WriteLine("找不到文件：" + path); return 7; }
                text = File.ReadAllText(path);
            }
            string detail;
            string report = LicenseAdminKey.Inspect(text, out detail);
            if(report == null){ Console.Error.WriteLine("校验失败：" + detail); return 8; }
            Console.Write(report);
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

        static void PrintCode(LicensePlan plan,string machineCode,string code,string detail){
            Console.WriteLine("档位    ：" + plan.Name + "（" + plan.Days + " 天）");
            Console.WriteLine("机器码  ：" + (string.IsNullOrEmpty(machineCode) ? "（通用码，未绑定）" : machineCode));
            Console.WriteLine("激活码  ：");
            Console.WriteLine(code);
            if(!string.IsNullOrEmpty(detail))Console.WriteLine("说明    ：" + detail);
            Console.WriteLine();
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
