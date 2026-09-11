using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
namespace TianGongCadSuite {
    public static class TrainingNative {
        public static long Identity(object value){IntPtr p=Marshal.GetIUnknownForObject(value);try{return p.ToInt64();}finally{Marshal.Release(p);}}
        public static object Read(List<object> errors,string path,Func<object> get){try{Trace("BEGIN "+path);object result=get();Trace("END "+path);return result;}catch(Exception e){while(e is TargetInvocationException&&e.InnerException!=null)e=e.InnerException;Trace("FAIL "+path+" "+e.HResult);errors.Add(TrainingExporter.Obj("location",path,"error",e.Message,"hresult",e.HResult));return null;}}
        static void Trace(string line){string path=Environment.GetEnvironmentVariable("TG_TRAINING_TRACE");if(!string.IsNullOrEmpty(path))System.IO.File.AppendAllText(path,line+Environment.NewLine);}
        public static object Value(object value){if(value==null)return null;Type t=value.GetType();if(t.IsEnum)return TrainingExporter.Obj("value",Convert.ToInt64(value),"enum_name",value.ToString());if(value is double&&(double.IsNaN((double)value)||double.IsInfinity((double)value)))throw new InvalidOperationException("Non-finite CAD number");return value;}
        public static Dictionary<string,object> Properties(object obj,Type type,List<object> errors,string path){var result=new Dictionary<string,object>();var availability=new Dictionary<string,object>();result["availability"]=availability;if(type==null)return result;
            foreach(var p in type.GetProperties().Where(p=>p.CanRead&&p.GetIndexParameters().Length==0&&(p.PropertyType.IsPrimitive||p.PropertyType.IsEnum||p.PropertyType==typeof(string)))){var property=p;int before=errors.Count;object value=Read(errors,path+"/"+p.Name,()=>Value(property.GetValue(obj,null)));result[p.Name]=value;availability[p.Name]=errors.Count==before?"available":"failed";
                if(value is string){result[p.Name+"_sha256"]=TrainingDataset.Digest((string)value);result[p.Name]=null;availability[p.Name]="redacted";}}
            return result;}
        public static Type Interface(object obj,string ns,Func<Type,bool> filter){return typeof(SolidEdgePart.Model).Assembly.GetTypes().FirstOrDefault(t=>t.IsInterface&&t.Namespace==ns&&filter(t)&&t.IsInstanceOfType(obj));}
    }
}
