using System;
using System.Collections.Generic;
namespace TianGongCadSuite {
    // 纯逻辑回归：属性卡填写（命令 10）只写改过的文件、补齐缺的属性、剪贴板和列名解析。
    public static class PropCardPureTests {
        static void Check(bool ok,string label){ if(!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        static PropCardRow Row(){
            var r=new PropCardRow{ Path=@"H:\项目\YF25.07G01-L1-M30-02-1.par" };
            r.Original["零件名称"]="";
            r.Original["设计"]="孙叶斌";
            return r;
        }
        public static void Run(){
            var fields=new List<string>{"零件名称","设计","优先等级"};

            var r=Row();
            Check(r.FileName=="YF25.07G01-L1-M30-02-1.par","文件名取路径最后一段");
            Check(r.Writes(fields,true).Count==0,"一格没改的文件不写（标准件、外购件不会被碰），哪怕勾了补齐");
            r.Set("设计","孙叶斌");
            Check(!r.Dirty,"改成和原来一样不算改");
            r.Set("优先等级","");
            Check(!r.Dirty,"文件里没有的属性填空也不算改");
            r.Set("零件名称","  固定板 \r\n");
            Check(r.Dirty&&r.Value("零件名称")=="固定板","粘贴带的空白和回车去掉："+r.Value("零件名称"));
            Check(r.IsEdited("零件名称")&&!r.IsEdited("设计"),"只标改过的格子");

            var w=r.Writes(fields,true);
            Check(w.Count==2&&w[0].Key=="零件名称"&&w[0].Value=="固定板"&&w[1].Key=="优先等级"&&w[1].Value=="","改过的文件：写改过的格子 + 缺的属性补空值");
            Check(r.Writes(fields,false).Count==1,"不勾补齐：只写改过的格子");

            r.Set("零件名称","");
            Check(!r.Dirty,"改回原来的空值 = 没改");

            var r2=Row();
            r2.Set("备注","临时");
            Check(r2.Writes(fields,false).Count==1&&r2.Writes(fields,false)[0].Key=="备注","列被删掉以后，之前改过的值照样写");

            var r3=Row();
            r3.Set("设计","李慧");r3.Set("优先等级","A");
            var w3=r3.Writes(fields,true);
            r3.Committed(w3);
            Check(!r3.Dirty&&r3.Has("优先等级")&&r3.Value("设计")=="李慧"&&r3.Has("零件名称"),"写成功后改过的值变成原值，补的空属性也算有了");
            Check(r3.Writes(fields,true).Count==0,"写完再写一次什么都不写");

            var p=PropCardFields.Parse("零件名称、设计,  项目名称；设计\r\n表面处理");
            Check(p.Count==4&&p[0]=="零件名称"&&p[3]=="表面处理","列名：顿号/逗号/分号/换行都能分隔、去重："+PropCardFields.Join(p));
            Check(PropCardFields.Parse("  ").Count==0,"全空 = 没有列（调用方恢复默认）");
            Check(new List<string>(PropCardFields.Default).Contains("零件名称")&&new List<string>(PropCardFields.Default).Contains("版本号1"),"默认列包含 SK 模板标题栏引用的属性");

            var c=PropCardFields.ParseClipboard("A\tB\r\nC\tD\r\n");
            Check(c.Count==2&&c[0].Length==2&&c[1][1]=="D","Excel 复制的 2×2：末尾空行不算");
            var one=PropCardFields.ParseClipboard("模组PACK实验室项目\r\n");
            Check(one.Count==1&&one[0].Length==1&&one[0][0]=="模组PACK实验室项目","Excel 复制一格 = 一个值（填满选区）");
            Check(PropCardFields.ParseClipboard("").Count==0,"空剪贴板什么都不做");
            Check(PropCardFields.ParseClipboard("a\n\nb").Count==3,"中间的空行保留（对应空格子）");

            string tmp=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"propcard-test-"+Guid.NewGuid().ToString("N")+".tsv");
            try{
                Check(PropCardFields.Load(tmp).Count==PropCardFields.Default.Length,"没有设置文件 = 默认列");
                PropCardFields.Save(tmp,new[]{"零件名称","批次"});
                var back=PropCardFields.Load(tmp);
                Check(back.Count==2&&back[1]=="批次","列设置存盘再读回");
            } finally { try{System.IO.File.Delete(tmp);}catch{} }
            Console.WriteLine("PROPCARD ASSERTIONS OK");
        }
    }
}
