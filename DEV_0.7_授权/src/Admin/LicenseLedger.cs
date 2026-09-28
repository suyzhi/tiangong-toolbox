using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TianGongCadSuite.Licensing.Admin {
    // 台账一行 = 一个已签发的激活码。状态三态：
    //   issued    已签发（码已发给客户，客户还没回报激活结果）
    //   activated 客户已回报激活（管理员主动询问后记录）
    //   void      已作废（导出到插件的 LicenseRevoked.cs 后，该码在任何机器上都不能用）
    // 台账文件与私钥放在一起 <私钥名>.ledger.tsv，只在管理员机器上，绝不进仓库、不发客户。
    public sealed class LedgerEntry {
        public string CodeId;
        public string Plan;
        public int IssueDay;
        public int ExpiryDay;
        public string Status;
        public string Machine;
        public string Note;
        public string Updated;
        public string Code;

        public string IssueDate { get { return LicenseTime.Format(LicenseTime.FromDayOffset(IssueDay)); } }
        public string ExpiryDate { get { return LicenseTime.Format(LicenseTime.FromDayOffset(ExpiryDay)); } }
        public string DisplayId { get { return LicenseCodec.Display(CodeId); } }
    }

    public static class LicenseLedger {
        public const string Issued = "issued";
        public const string Activated = "activated";
        public const string Void = "void";
        const string Header = "codeId\tplan\tissueDay\texpiryDay\tstatus\tmachine\tnote\tupdated\tcode";

        public static string PathForKey(string keyPath){
            string full = Path.GetFullPath(keyPath);
            return Path.ChangeExtension(full, null) + ".ledger.tsv";
        }

        public static List<LedgerEntry> Load(string keyPath){
            List<LedgerEntry> rows = new List<LedgerEntry>();
            string path = PathForKey(keyPath);
            if(!File.Exists(path))return rows;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for(int i = 0; i < lines.Length; i++){
                string line = lines[i];
                if(string.IsNullOrWhiteSpace(line))continue;
                if(i == 0 && line.StartsWith("codeId", StringComparison.Ordinal))continue;
                string[] cells = line.Split('\t');
                if(cells.Length < 9)continue;
                LedgerEntry entry = new LedgerEntry();
                entry.CodeId = cells[0].Trim();
                entry.Plan = cells[1].Trim();
                int issue; int.TryParse(cells[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out issue);
                int expiry; int.TryParse(cells[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out expiry);
                entry.IssueDay = issue;
                entry.ExpiryDay = expiry;
                entry.Status = cells[4].Trim();
                entry.Machine = cells[5].Trim();
                entry.Note = cells[6].Trim();
                entry.Updated = cells[7].Trim();
                entry.Code = cells[8].Trim();
                rows.Add(entry);
            }
            return rows;
        }

        public static void Save(string keyPath, List<LedgerEntry> rows){
            StringBuilder text = new StringBuilder();
            text.Append(Header).Append("\r\n");
            for(int i = 0; i < rows.Count; i++){
                LedgerEntry e = rows[i];
                text.Append(Cell(e.CodeId)).Append('\t');
                text.Append(Cell(e.Plan)).Append('\t');
                text.Append(e.IssueDay.ToString(CultureInfo.InvariantCulture)).Append('\t');
                text.Append(e.ExpiryDay.ToString(CultureInfo.InvariantCulture)).Append('\t');
                text.Append(Cell(e.Status)).Append('\t');
                text.Append(Cell(e.Machine)).Append('\t');
                text.Append(Cell(e.Note)).Append('\t');
                text.Append(Cell(e.Updated)).Append('\t');
                text.Append(Cell(e.Code)).Append("\r\n");
            }
            File.WriteAllText(PathForKey(keyPath), text.ToString(), new UTF8Encoding(false));
        }

        static string Cell(string value){
            if(string.IsNullOrEmpty(value))return "";
            return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        public static void Append(string keyPath, LedgerEntry entry){
            List<LedgerEntry> rows = Load(keyPath);
            for(int i = 0; i < rows.Count; i++){
                if(string.Equals(LicenseCodec.NormalizeId(rows[i].CodeId), LicenseCodec.NormalizeId(entry.CodeId), StringComparison.Ordinal)){
                    rows[i] = entry;
                    Save(keyPath, rows);
                    return;
                }
            }
            rows.Add(entry);
            Save(keyPath, rows);
        }

        // 把用户给过来的东西（完整激活码 / 码ID / 文件）统一解析成码ID。
        public static string ResolveCodeId(string text){
            if(string.IsNullOrEmpty(text))return null;
            LicenseCode code = LicenseCodec.Parse(text);
            if(code != null)return code.CodeId;
            string id = LicenseCodec.NormalizeId(text);
            return id.Length == LicenseCodec.GroupChars ? id : null;
        }

        public static LedgerEntry Find(List<LedgerEntry> rows,string codeId){
            string needle = LicenseCodec.NormalizeId(codeId);
            for(int i = 0; i < rows.Count; i++){
                if(string.Equals(LicenseCodec.NormalizeId(rows[i].CodeId), needle, StringComparison.Ordinal))return rows[i];
            }
            return null;
        }

        public static int CountStatus(List<LedgerEntry> rows,string status){
            int total = 0;
            for(int i = 0; i < rows.Count; i++)if(string.Equals(rows[i].Status, status, StringComparison.OrdinalIgnoreCase))total++;
            return total;
        }

        // 生成插件里的作废清单源码。只有 void 状态的码会写进去。
        public static string BuildRevokedSource(List<LedgerEntry> rows,string stamp){
            StringBuilder ids = new StringBuilder();
            int count = 0;
            for(int i = 0; i < rows.Count; i++){
                if(!string.Equals(rows[i].Status, Void, StringComparison.OrdinalIgnoreCase))continue;
                ids.Append("            \"").Append(LicenseCodec.NormalizeId(rows[i].CodeId)).Append("\", // ").Append(rows[i].DisplayId);
                if(!string.IsNullOrEmpty(rows[i].Note))ids.Append(" ").Append(rows[i].Note);
                ids.Append("\r\n");
                count++;
            }
            StringBuilder text = new StringBuilder();
            text.Append("// 作废码清单（黑名单）。由管理员工具 revoke / export 生成，请勿手工修改。\r\n");
            text.Append("// 生成时间：").Append(stamp).Append("，共 ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" 个作废码。\r\n");
            text.Append("// 语义：清单里出现的码ID，在任何机器上都不能激活；已经激活的机器在下次校验时也会失效。\r\n");
            text.Append("// 注意：这是离线吊销，只对装了「带这份清单的插件版本」的客户端生效。\r\n");
            text.Append("using System;\r\n");
            text.Append("\r\n");
            text.Append("namespace TianGongCadSuite.Licensing {\r\n");
            text.Append("    internal static class LicenseRevoked {\r\n");
            text.Append("        internal static readonly string GeneratedStamp = \"").Append(stamp).Append("\";\r\n");
            text.Append("\r\n");
            text.Append("        internal static readonly string[] GeneratedIds = new string[]{\r\n");
            text.Append(ids.ToString());
            text.Append("        };\r\n");
            text.Append("\r\n");
            text.Append("        internal static bool Contains(string codeId){\r\n");
            text.Append("            if(string.IsNullOrEmpty(codeId))return false;\r\n");
            text.Append("            string[] ids = LicenseTestHooks.RevokedOverride ?? GeneratedIds;\r\n");
            text.Append("            if(ids == null || ids.Length == 0)return false;\r\n");
            text.Append("            string needle = LicenseCodec.NormalizeId(codeId);\r\n");
            text.Append("            if(needle.Length == 0)return false;\r\n");
            text.Append("            for(int i = 0; i < ids.Length; i++){\r\n");
            text.Append("                if(string.Equals(LicenseCodec.NormalizeId(ids[i]), needle, System.StringComparison.Ordinal))return true;\r\n");
            text.Append("            }\r\n");
            text.Append("            return false;\r\n");
            text.Append("        }\r\n");
            text.Append("\r\n");
            text.Append("        internal static int Count {\r\n");
            text.Append("            get {\r\n");
            text.Append("                string[] ids = LicenseTestHooks.RevokedOverride ?? GeneratedIds;\r\n");
            text.Append("                return ids == null ? 0 : ids.Length;\r\n");
            text.Append("            }\r\n");
            text.Append("        }\r\n");
            text.Append("    }\r\n");
            text.Append("}\r\n");
            return text.ToString();
        }

        // 找到插件源码根目录（含 src\License\LicenseKeySlot.cs 的那一层），找不到就返回 null。
        public static string FindPluginRoot(string startDirectory){
            string current = string.IsNullOrEmpty(startDirectory) ? AppDomain.CurrentDomain.BaseDirectory : startDirectory;
            try{ current = Path.GetFullPath(current); }catch(Exception){ return null; }
            for(int depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++){
                string probe = Path.Combine(Path.Combine(current, "src"), Path.Combine("License", "LicenseKeySlot.cs"));
                if(File.Exists(probe))return current;
                current = Path.GetDirectoryName(current);
            }
            return null;
        }
    }
}
