using System;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TianGongCadSuite.Licensing {
    // 机器码：由主板/卷/BIOS 标识派生，重装系统后可能变化，换机器一定变化。
    public static class LicenseMachine {
        internal const string SeedSalt = "TGS-MID-容器-01";
        public const int FingerprintSize = LicensePayload.FingerprintSize;
        public const int ShortSize = 5;

        static string cached;
        static byte[] cachedFingerprint;

        internal static string Seed(){
            if(cached != null)return cached;
            string guid = RegistryValue(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid", RegistryView.Registry64);
            if(guid == null)guid = RegistryValue(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid", RegistryView.Registry32);
            string volume = WmiValue("SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID='C:'", "VolumeSerialNumber");
            string uuid = WmiValue("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID");
            if(uuid == null)uuid = WmiValue("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber");
            string seed = Normalize(guid) + "|" + Normalize(volume) + "|" + Normalize(uuid);
            cached = seed;
            return seed;
        }

        static string Normalize(string value){
            if(string.IsNullOrEmpty(value))return "";
            StringBuilder builder = new StringBuilder(value.Length);
            foreach(char c in value){
                if(char.IsLetterOrDigit(c))builder.Append(char.ToUpperInvariant(c));
            }
            return builder.ToString();
        }

        static string RegistryValue(string path,string name,RegistryView view){
            try{
                using(RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using(RegistryKey key = baseKey.OpenSubKey(path)){
                    if(key == null)return null;
                    object value = key.GetValue(name);
                    return value == null ? null : Convert.ToString(value);
                }
            }catch(Exception){ return null; }
        }

        static string WmiValue(string query,string property){
            try{
                using(ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
                using(ManagementObjectCollection rows = searcher.Get()){
                    foreach(ManagementObject row in rows){
                        object value = row[property];
                        if(value != null)return Convert.ToString(value);
                    }
                }
            }catch(Exception){ }
            return null;
        }

        public static byte[] Fingerprint(){
            byte[] hook = LicenseHook.Fingerprint();
            if(hook != null)return (byte[])hook.Clone();
            if(cachedFingerprint != null)return (byte[])cachedFingerprint.Clone();
            byte[] digest;
            using(SHA256 hash = SHA256.Create())digest = hash.ComputeHash(Encoding.UTF8.GetBytes(SeedSalt + "#" + Seed()));
            byte[] result = new byte[FingerprintSize];
            Buffer.BlockCopy(digest, 0, result, 0, FingerprintSize);
            cachedFingerprint = result;
            return (byte[])result.Clone();
        }

        // 显示给用户的机器码：16 字符 = 80 bit 指纹，去掉易混字符。
        public static string MachineCode(){
            string hook = LicenseHook.MachineCode();
            if(!string.IsNullOrEmpty(hook))return hook;
            return Encode(Fingerprint(), 16);
        }

        // 激活码里只携带前 5 字节，够用且短。
        public static string ShortCode(){
            byte[] fingerprint = Fingerprint();
            return Encode(fingerprint, 8);
        }

        internal static byte[] ShortBytes(){
            byte[] fingerprint = LicenseHook.Fingerprint() ?? Fingerprint();
            byte[] result = new byte[ShortSize];
            Buffer.BlockCopy(fingerprint, 0, result, 0, ShortSize);
            return result;
        }

        // 每字符 5 bit（与激活码同一套字母表），8 字符 = 40 bit = 5 字节，无损。
        // 机器码 16 字符 = 80 bit，正好覆盖 10 字节指纹。
        static string Encode(byte[] data,int chars){
            StringBuilder builder = new StringBuilder(chars + 4);
            ulong acc = 0;
            int bits = 0;
            for(int i = 0; i < data.Length && builder.Length < chars; i++){
                acc = (acc << 8) | (uint)data[i];
                bits += 8;
                while(bits >= 5 && builder.Length < chars){
                    bits -= 5;
                    builder.Append(LicenseCodec.Alphabet[(int)((acc >> bits) & 31UL)]);
                }
            }
            while(builder.Length < chars)builder.Append(LicenseCodec.Alphabet[0]);
            return LicenseCodec.Group(builder.ToString(), 4);
        }

        // 解析用户或管理员输入的机器码，只取前 5 字节。
        public static bool TryParse(string text,out byte[] fingerprint){
            fingerprint = null;
            if(string.IsNullOrEmpty(text))return false;
            ulong acc = 0;
            int bits = 0;
            byte[] bytes = new byte[ShortSize];
            int written = 0;
            for(int i = 0; i < text.Length; i++){
                char c = text[i];
                if(c == '-' || c == '_' || char.IsWhiteSpace(c))continue;
                int index = LicenseCodec.Index(c);
                if(index < 0)return false;
                acc = (acc << 5) | (uint)index;
                bits += 5;
                while(bits >= 8){
                    bits -= 8;
                    if(written < ShortSize)bytes[written++] = (byte)((acc >> bits) & 0xFF);
                }
            }
            if(written < ShortSize)return false;
            fingerprint = bytes;
            return true;
        }

        internal static bool Matches(byte[] expected){
            if(expected == null || expected.Length != ShortSize)return false;
            byte[] actual = ShortBytes();
            int diff = 0;
            for(int i = 0; i < ShortSize; i++)diff |= expected[i] ^ actual[i];
            return diff == 0;
        }
    }
}
