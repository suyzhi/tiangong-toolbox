using System;
using System.Text;

namespace TianGongCadSuite.Licensing {
    // 激活码签名载荷，固定 27 字节。字段顺序即线上格式，禁止调整。
    //   [0]      掩码位：bit0=绑定机器 bit1=保留 bit2=保留
    //   [1..2]   格式标识 0x54,0x47 ('T','G')
    //   [3]      格式版本
    //   [4]      授权档位
    //   [5..14]  机器指纹前 10 字节（未绑定时为随机填充）
    //   [15..18] 签发日（距 2020-01-01 UTC 的天数，大端）
    //   [19..26] 随机数
    public sealed class LicensePayload {
        public const int Size = 27;
        public const byte Version = 1;
        public const int FingerprintSize = 10;
        internal const byte Magic0 = 0x54, Magic1 = 0x47;

        public byte Flags;
        public byte PlanCode;
        public byte[] Fingerprint = new byte[FingerprintSize];
        public int DayOffset;
        public byte[] Nonce = new byte[8];

        public bool MachineBound { get { return (Flags & 1) != 0; } }

        public LicensePlan Plan { get { return LicensePlans.Find(PlanCode); } }

        public DateTime IssueDate { get { return LicenseTime.FromDayOffset(DayOffset); } }

        public DateTime ExpiryDate {
            get {
                LicensePlan plan = Plan;
                if(plan == null)return LicenseTime.FromDayOffset(DayOffset);
                return LicenseTime.FromDayOffset(DayOffset + plan.Days);
            }
        }

        public byte[] ToBytes(){
            byte[] buffer = new byte[Size];
            buffer[0] = Flags;
            buffer[1] = Magic0;
            buffer[2] = Magic1;
            buffer[3] = Version;
            buffer[4] = PlanCode;
            Buffer.BlockCopy(Fingerprint,0,buffer,5,FingerprintSize);
            buffer[15] = (byte)((DayOffset >> 24) & 0xFF);
            buffer[16] = (byte)((DayOffset >> 16) & 0xFF);
            buffer[17] = (byte)((DayOffset >> 8) & 0xFF);
            buffer[18] = (byte)(DayOffset & 0xFF);
            Buffer.BlockCopy(Nonce,0,buffer,19,8);
            return buffer;
        }

        public static LicensePayload FromBytes(byte[] raw,int offset){
            if(raw == null || offset < 0 || raw.Length - offset < Size)return null;
            if(raw[offset + 1] != Magic0 || raw[offset + 2] != Magic1)return null;
            if(raw[offset + 3] != Version)return null;
            LicensePayload payload = new LicensePayload();
            payload.Flags = raw[offset];
            payload.PlanCode = raw[offset + 4];
            Buffer.BlockCopy(raw,offset + 5,payload.Fingerprint,0,FingerprintSize);
            payload.DayOffset = ((int)raw[offset + 15] << 24) | ((int)raw[offset + 16] << 16) | ((int)raw[offset + 17] << 8) | (int)raw[offset + 18];
            Buffer.BlockCopy(raw,offset + 19,payload.Nonce,0,8);
            return payload;
        }
    }

    public static class LicenseTime {
        public static readonly DateTime Epoch = new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc);
        public const int MaxDayOffset = 0x7FFFFFFF;

        public static int ToDayOffset(DateTime utc){
            double days = (utc.ToUniversalTime() - Epoch).TotalDays;
            if(days < 0)return 0;
            if(days > MaxDayOffset)return MaxDayOffset;
            return (int)Math.Floor(days);
        }

        public static DateTime FromDayOffset(int offset){ return Epoch.AddDays(offset); }

        public static int Today { get { return ToDayOffset(DateTime.UtcNow); } }

        public static string Format(DateTime date){ return date.ToLocalTime().ToString("yyyy-MM-dd"); }
    }
}
