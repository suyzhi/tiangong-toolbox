using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TianGongCadSuite.Licensing {
    // 运行期自检：调试器、分析工具、二进制的结构性改动。
    // 结论不直接返回给调用方，而是累积到状态位里，由 LicenseLibrary 统一归并。
    internal static class LicenseGuard {
        internal const int DebuggerBit = 1;
        internal const int ProfilerBit = 2;
        internal const int SelfTestBit = 4;
        internal const int ProbeBit = 8;
        internal const int IntegrityBit = 16;
        internal const int NameBit = 32;

        static string[] probes = new string[]{
            "dnspy", "ilspy", "dotpeek", "reflector", "de4dot", "confuserex",
            "x64dbg", "ollydbg", "cheatengine", "processhacker", "scylla",
            "megadumper", "extreme dumper", "simpleassemblyexplorer", "justdecompile"
        };

        internal static int Scan(){
            int bits = 0;
            if(IsDebuggerAttached())bits |= DebuggerBit;
            if(HasProfiler())bits |= ProfilerBit;
            if(!LicenseCodec.SelfTest())bits |= SelfTestBit;
            if(HasAnalysisTool())bits |= ProbeBit;
            if(!Integrity())bits |= IntegrityBit;
            if(!AssemblyNameOk())bits |= NameBit;
            return bits;
        }

        static bool IsDebuggerAttached(){
            try{ if(Debugger.IsAttached)return true; }catch(Exception){ }
            try{ if(Debugger.IsLogging())return true; }catch(Exception){ }
            try{
                string value = Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING");
                if(!string.IsNullOrEmpty(value) && value != "0")return true;
            }catch(Exception){ }
            return false;
        }

        static bool HasProfiler(){
            try{
                string[] names = new string[]{ "COR_PROFILER", "CORECLR_PROFILER", "COR_PROFILER_PATH", "CORECLR_PROFILER_PATH", "COMPLUS_ProfAPI_ProfilerCompatibilitySetting" };
                for(int i = 0; i < names.Length; i++){
                    string value = Environment.GetEnvironmentVariable(names[i]);
                    if(!string.IsNullOrEmpty(value))return true;
                }
            }catch(Exception){ }
            return false;
        }

        static bool HasAnalysisTool(){
            try{
                Process current = Process.GetCurrentProcess();
                ProcessModuleCollection modules = current.Modules;
                for(int i = 0; i < modules.Count; i++){
                    string name = modules[i].ModuleName;
                    if(name == null)continue;
                    string lower = name.ToLowerInvariant();
                    for(int p = 0; p < probes.Length; p++)if(lower.IndexOf(probes[p], StringComparison.Ordinal) >= 0)return true;
                }
            }catch(Exception){ }
            return false;
        }

        static bool Integrity(){
            try{
                Assembly self = typeof(LicenseGuard).Assembly;
                // 版本号由构建脚本写入 LicenseBuild.cs，并同时写入程序集特性。
                // 直接把整个授权模块删掉另起一份实现，这个等式就不再成立。
                Version version = self.GetName().Version;
                if(version == null)return false;
                if(version.Major != LicenseConstants.Major)return false;
                if(version.Minor != LicenseConstants.Minor)return false;
                // 公钥与掩码常量必须能还原成一条合法的 P-256 公钥。
                byte[] blob = LicenseKeyMaterial.BuildBlob();
                if(blob.Length != 72)return false;
                if(blob[0] != 0x45 || blob[1] != 0x43 || blob[2] != 0x53 || blob[3] != 0x31)return false;
                if(blob[4] != 32 || blob[5] != 0 || blob[6] != 0 || blob[7] != 0)return false;
                // 公钥不能是占位全零：说明密钥槽没被 keygen 覆盖过。
                if(LicenseKeyMaterial.SemanticPrint() == 0)return false;
                if(LicenseMachine.Fingerprint().Length != LicensePayload.FingerprintSize)return false;
                // 卸载自检：Guard 的类型信息必须仍在本程序集里。
                if(self.GetType("TianGongCadSuite.Licensing.LicenseGate", false) == null)return false;
                return true;
            }catch(Exception){ return false; }
        }

        static bool AssemblyNameOk(){
            try{
                Assembly self = typeof(LicenseGuard).Assembly;
                string name = self.GetName().Name;
                if(string.IsNullOrEmpty(name))return false;
                if(name.Length < 4)return false;
                // 允许测试宿主以 InternalsVisibleTo 复用同一份代码。
                if(name == "TianGongCadSuite" || name == "PanelTests")return true;
                return name.StartsWith("TianGong", StringComparison.Ordinal);
            }catch(Exception){ return false; }
        }

        // 供 UI 展示的诊断串：故意不暴露具体是哪一项失败。
        internal static string Describe(int bits){
            if(bits == 0)return "正常";
            StringBuilder builder = new StringBuilder();
            builder.Append("环境异常(");
            builder.Append(bits.ToString("X2"));
            builder.Append(')');
            return builder.ToString();
        }

        internal static int Digest(int bits){
            unchecked{
                int value = bits * (int)0x9E3779B1;
                value ^= LicenseConstants.Major * (int)0x85EBCA6B;
                if(bits != 0)value ^= 0x7F4A7C15;
                return value & 0x7FFFFFFF;
            }
        }
    }

    // LicenseConstants 由 tools/build.ps1 生成在 LicenseBuild.cs 里，
    // 与程序集版本特性同源，运行期自检会比较两者。
}
