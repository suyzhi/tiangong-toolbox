using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TianGongCadSuite.Licensing {
    // 激活文件落盘。三层冗余：DPAPI(机器)文件 + ProgramData 影子副本 + HKCU 计数器。
    // 只有本机能解密：拷到别的机器上会因为 DPAPI 域密钥不同而直接失败。
    internal static class LicenseStore {
        const string ProductFolder = "TianGongCadSuite";
        const string FileName = "activation.dat";
        const string Domain = "TGS-STORE-\u6388\u6743-01";
        const string RegistryPath = @"Software\TianGongCadSuite";
        const string RegistryName = "State";

        internal static string PrimaryPath {
            get {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if(string.IsNullOrEmpty(programData))programData = Path.GetTempPath();
                return Path.Combine(Path.Combine(programData, ProductFolder), FileName);
            }
        }

        internal static string ShadowPath {
            get {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if(string.IsNullOrEmpty(local))local = Path.GetTempPath();
                return Path.Combine(Path.Combine(local, ProductFolder), FileName + ".bak");
            }
        }

        static byte[] Entropy(){
            using(SHA256 hash = SHA256.Create())
                return hash.ComputeHash(Encoding.UTF8.GetBytes(Domain + "#" + LicenseMachine.ShortCode()));
        }

        internal static ActivationRecord Load(){
            ActivationRecord record = ReadFile(PrimaryPath);
            if(record != null)return record;
            return ReadFile(ShadowPath);
        }

        static ActivationRecord ReadFile(string path){
            try{
                if(!File.Exists(path))return null;
                if(new FileInfo(path).Length > 16384)return null;
                byte[] blob = File.ReadAllBytes(path);
                byte[] plain = ProtectedData.Unprotect(blob, Entropy(), DataProtectionScope.LocalMachine);
                return ActivationRecord.FromBytes(plain);
            }catch(Exception){ return null; }
        }

        internal static bool Save(ActivationRecord record){
            if(record == null)return false;
            byte[] plain = record.ToBytes();
            byte[] blob;
            try{ blob = ProtectedData.Protect(plain, Entropy(), DataProtectionScope.LocalMachine); }
            catch(CryptographicException){ return false; }
            bool primary = WriteFile(PrimaryPath, blob);
            WriteFile(ShadowPath, blob);
            SaveStamp(record);
            return primary;
        }

        // 测试专用：保存指定记录 / 读取计数器，用来验证"伪造激活文件"和"状态被重置"。
        internal static bool SaveForTest(ActivationRecord record){ return Save(record); }
        internal static long CounterForTest(){ return Counter(); }

        internal static void Clear(){
            try{ if(File.Exists(PrimaryPath))File.Delete(PrimaryPath); }catch(Exception){ }
            try{ if(File.Exists(ShadowPath))File.Delete(ShadowPath); }catch(Exception){ }
        }

        static bool WriteFile(string path,byte[] blob){
            try{
                string folder = Path.GetDirectoryName(path);
                if(!Directory.Exists(folder))Directory.CreateDirectory(folder);
                string temporary = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllBytes(temporary, blob);
                if(File.Exists(path))File.Delete(path);
                File.Move(temporary, path);
                return true;
            }catch(Exception){ return false; }
        }

// 心跳：只抬高单调时间基准，不改动激活内容。
        internal static void Touch(ActivationRecord record){
            if(record == null)return;
            SaveStamp(record);
        }

        static void SaveStamp(ActivationRecord record){
            try{
                using(RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath)){
                    if(key == null)return;
                    long previous = ReadLong(key, "High");
                    long now = DateTime.UtcNow.Ticks;
                    key.SetValue("High", (now > previous ? now : previous).ToString(), RegistryValueKind.String);
                    key.SetValue("Counter", record.Counter.ToString(), RegistryValueKind.String);
                    key.SetValue("Fingerprint", LicenseMachine.ShortCode(), RegistryValueKind.String);
                }
            }catch(Exception){ }
        }

        internal static long HighWaterTicks(){
            try{
                using(RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath)){
                    if(key == null)return 0;
                    return ReadLong(key, "High");
                }
            }catch(Exception){ return 0; }
        }

        internal static long Counter(){
            try{
                using(RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath)){
                    if(key == null)return 0;
                    return ReadLong(key, "Counter");
                }
            }catch(Exception){ return 0; }
        }

        // 记录被删掉但计数器还在 = 有人试图重置激活状态。
        internal static bool GhostDetected(){
            ActivationRecord record = Load();
            long counter = Counter();
            if(record != null)return false;
            return counter > 0;
        }

        static long ReadLong(RegistryKey key,string name){
            object value = key.GetValue(name);
            if(value == null)return 0;
            long parsed;
            if(!long.TryParse(Convert.ToString(value), out parsed))return 0;
            return parsed;
        }
    }
}
