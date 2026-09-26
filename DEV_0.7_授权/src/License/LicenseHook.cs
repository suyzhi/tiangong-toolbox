using System;
using System.Reflection;

namespace TianGongCadSuite.Licensing {
    // 测试钩子只在插件与测试程序里存在。用反射探测，让管理员工具可以直接复用
    // 同一份机器码实现，而不必依赖只存在于插件里的类型。
    internal static class LicenseHook {
        internal static byte[] Fingerprint(){
            FieldInfo field = Field("FingerprintOverride");
            if(field == null)return null;
            try{ return field.GetValue(null) as byte[]; }catch(Exception){ return null; }
        }

        internal static string MachineCode(){
            FieldInfo field = Field("MachineCodeOverride");
            if(field == null)return null;
            try{ return field.GetValue(null) as string; }catch(Exception){ return null; }
        }

        static FieldInfo Field(string name){
            try{
                Type type = Type.GetType("TianGongCadSuite.Licensing.LicenseTestHooks, TianGongCadSuite", false);
                if(type == null){
                    // 程序集名在测试宿主里可能不同，逐个已加载程序集再找一遍。
                    System.Reflection.Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                    for(int i = 0; i < loaded.Length && type == null; i++)
                        type = loaded[i].GetType("TianGongCadSuite.Licensing.LicenseTestHooks", false);
                }
                return type == null ? null : type.GetField(name);
            }catch(Exception){ return null; }
        }
    }
}
