using System;
using System.Globalization;
using System.Text;

namespace TianGongCadSuite.Licensing {
    // 本机激活记录：绑定机器 + 激活时刻 + 单调计数，DPAPI 加密后落盘。
    internal sealed class ActivationRecord {
        const string Magic = "TGL1";

        public byte[] MachineFingerprint;
        public int ActivatedDay;
        public long Counter;
        public string Code;

        public ActivationRecord Clone(){
            ActivationRecord copy = new ActivationRecord();
            copy.MachineFingerprint = MachineFingerprint == null ? null : (byte[])MachineFingerprint.Clone();
            copy.ActivatedDay = ActivatedDay;
            copy.Counter = Counter;
            copy.Code = Code;
            return copy;
        }

        public byte[] ToBytes(){
            StringBuilder text = new StringBuilder();
            text.Append(Magic).Append('\n');
            text.Append(Hex(MachineFingerprint)).Append('\n');
            text.Append(ActivatedDay.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append(Counter.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append(Code == null ? "" : Code).Append('\n');
            return Encoding.UTF8.GetBytes(text.ToString());
        }

        public static ActivationRecord FromBytes(byte[] raw){
            if(raw == null || raw.Length == 0 || raw.Length > 8192)return null;
            string[] lines = Encoding.UTF8.GetString(raw).Split('\n');
            if(lines.Length < 5 || lines[0].Trim() != Magic)return null;
            ActivationRecord record = new ActivationRecord();
            record.MachineFingerprint = Unhex(lines[1].Trim());
            if(record.MachineFingerprint == null)return null;
            int day;
            if(!int.TryParse(lines[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out day))return null;
            long counter;
            if(!long.TryParse(lines[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out counter))return null;
            record.ActivatedDay = day;
            record.Counter = counter;
            record.Code = lines[4].Trim();
            return record;
        }

        static string Hex(byte[] data){
            if(data == null)return "";
            StringBuilder builder = new StringBuilder(data.Length * 2);
            for(int i = 0; i < data.Length; i++)builder.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        static byte[] Unhex(string text){
            if(string.IsNullOrEmpty(text) || text.Length % 2 != 0 || text.Length > 4096)return null;
            byte[] data = new byte[text.Length / 2];
            for(int i = 0; i < data.Length; i++){
                int high = Nibble(text[i * 2]);
                int low = Nibble(text[i * 2 + 1]);
                if(high < 0 || low < 0)return null;
                data[i] = (byte)((high << 4) | low);
            }
            return data;
        }

        static int Nibble(char c){
            if(c >= '0' && c <= '9')return c - '0';
            if(c >= 'a' && c <= 'f')return c - 'a' + 10;
            if(c >= 'A' && c <= 'F')return c - 'A' + 10;
            return -1;
        }
    }
}
