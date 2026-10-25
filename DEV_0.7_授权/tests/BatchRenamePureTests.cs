using System;
using System.Collections.Generic;
using System.Text;
namespace TianGongCadSuite {
    // 纯逻辑回归：批量改名（命令 12）的新名字校验、两步改名、批量生成、同名工程图跟随、引用初筛。
    public static class BatchRenamePureTests {
        static void Check(bool ok,string label){ if(!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        const string Dir=@"H:\项目\焊接台车";
        static RenameRow Row(string name){ return new RenameRow{ Path=System.IO.Path.Combine(Dir,name) }; }
        public static void Run(){
            var a=Row("YF25.07G01-L1-M30-02-07.par");
            Check(!a.Changed&&a.NewBase=="YF25.07G01-L1-M30-02-07"&&a.Kind=="零件","没改 = 新名字就是原名（不含扩展名）");
            a.SetNewBase(" 支撑板-07.par \r\n");
            Check(a.Changed&&a.NewBase=="支撑板-07"&&a.NewFileName=="支撑板-07.par","粘贴带扩展名、空白、回车：都去掉，扩展名不让改");
            a.SetNewBase("YF25.07G01-L1-M30-02-07");
            Check(!a.Changed,"改回原名 = 没改");
            a.SetNewBase("yf25.07g01-l1-m30-02-07");
            Check(a.Changed,"只改大小写也算改");

            Check(RenameRules.CheckName("").Length>0,"空名字不行");
            Check(RenameRules.CheckName("a/b").Length>0&&RenameRules.CheckName("a:b").Length>0&&RenameRules.CheckName("a?").Length>0,"非法字符不行");
            Check(RenameRules.CheckName("abc.").Length>0&&RenameRules.CheckName("con").Length>0,"点结尾、保留名不行");
            Check(RenameRules.CheckName("支撑板 (左)-01").Length==0,"中文、空格、括号可以");

            // 跟随：同名工程图 / cfg
            var asm=Row("YF25.07G01-L1-M30-02.asm");
            var listing=new[]{"YF25.07G01-L1-M30-02.asm","YF25.07G01-L1-M30-02.cfg","yf25.07g01-l1-m30-02.dft","YF25.07G01-L1-M30-02-07.dft","其他.dft"};
            var comp=RenameRules.Companions(asm,listing,null);
            Check(comp.Count==2,"装配跟随同名 .cfg 和同名工程图（不分大小写）："+string.Join(",",comp.ToArray()));
            var part=Row("YF25.07G01-L1-M30-02-07.par");
            var pc=RenameRules.Companions(part,listing,null);
            Check(pc.Count==1&&pc[0].EndsWith("YF25.07G01-L1-M30-02-07.dft"),"零件只跟随同名工程图（.cfg 是装配的）");
            Check(RenameRules.Companions(part,listing,new HashSet<string>(pc,StringComparer.OrdinalIgnoreCase)).Count==0,"已在表里的不重复加");
            Check(RenameRules.Companions(Row("X.dft"),new[]{"X.dft"},null).Count==0,"工程图自己不带跟随");
            var d=new RenameRow{ Path=pc[0], Master=part };
            part.SetNewBase("支撑板");
            Check(d.Changed&&d.NewFileName=="支撑板.dft","跟随行的新名字 = 主行的新名字 + 自己的扩展名");
            d.SetNewBase("支撑板-图");
            part.SetNewBase("支撑板2");
            Check(d.Pinned&&d.NewBase=="支撑板-图","跟随行单独改过名字后不再跟着变");
            d.SetNewBase("支撑板2");
            Check(!d.Pinned,"改成和主行一样 = 重新跟随");

            // 校验
            Func<string,bool> disk=p=>{ var n=System.IO.Path.GetFileName(p); return n=="已有.par"||n=="B.par"||n=="A.par"||n=="C.par"; };
            var rows=new List<RenameRow>{ Row("A.par"), Row("B.par"), Row("C.par") };
            rows[0].SetNewBase("已有");
            Check(RenameRules.Validate(rows,disk)==1&&rows[0].Problem.Contains("已经有"),"撞上磁盘上已有的文件");
            rows[0].SetNewBase("新");rows[1].SetNewBase("新");
            Check(RenameRules.Validate(rows,disk)==2&&rows[1].Problem.Contains("同一个名字")&&rows[0].Problem.Contains("同一个名字"),"两个文件改成同一个名字（两行都标出来）");
            rows[0].SetNewBase("B");rows[1].SetNewBase("A");
            Check(RenameRules.Validate(rows,disk)==0,"互换名字可以（两步改名）");
            rows[0].SetNewBase("C");rows[1].Revert();
            Check(RenameRules.Validate(rows,disk)==1&&rows[0].Problem.Contains("还在用"),"撞上表里没改名的文件");
            foreach(var r in rows) r.Revert();
            rows[0].SetNewBase("B");rows[1].SetNewBase("C");rows[2].SetNewBase("D");
            Check(RenameRules.Validate(rows,disk)==0,"序号整体后移 A→B、B→C、C→D 可以");
            int k=0;
            var moves=RenameRules.Moves(rows,p=>p+".tmp"+(k++));
            Check(moves.Count==3&&moves[0].Temp!=null&&moves[1].Temp!=null&&moves[2].Temp==null,"新名字是别人的旧名字 → 经临时名两步走；D 没人占 → 一步");
            var lc=Row("abc.par"); lc.SetNewBase("ABC");
            Check(RenameRules.Validate(new List<RenameRow>{lc},p=>true)==0,"只改大小写不算撞上自己");
            Check(RenameRules.Moves(new List<RenameRow>{lc},p=>p+"~")[0].Temp!=null,"只改大小写也经临时名");
            var longRow=Row("a.par"); longRow.SetNewBase(new string('长',260));
            Check(RenameRules.Validate(new List<RenameRow>{longRow},p=>false)==1&&longRow.Problem.Contains("太长"),"路径超长");
            var same=Row("same.par");
            Check(RenameRules.Validate(new List<RenameRow>{same},p=>true)==0&&RenameRules.Moves(new List<RenameRow>{same},p=>p).Count==0,"没改名的行不校验、不动");

            // 批量生成
            Check(RenameRules.Replace("YF25.07G01-L1-M30-02-07","yf25.07g01","SK26.01")=="SK26.01-L1-M30-02-07","查找替换不分大小写");
            Check(RenameRules.Replace("a-a-a","a","b")=="b-b-b"&&RenameRules.Replace("abc","","x")=="abc","全部替换；查找为空不动");
            var props=new Dictionary<string,string>{{"图号","SK-001"},{"零件名称"," 支撑板 "},{"空","  "}};
            var miss=new List<string>();
            Check(RenameRules.Template("{图号}-{零件名称}","old",3,props,miss)=="SK-001-支撑板"&&miss.Count==0,"模板：属性（去空白）");
            Check(RenameRules.Template("{原名}_{序号}","old",3,null,null)=="old_03"&&RenameRules.Template("P{序号:3}","x",7,null,null)=="P007","模板：原名、序号补零");
            miss.Clear();RenameRules.Template("{图号}{设计}{空}","x",1,props,miss);
            Check(miss.Count==2&&miss.Contains("设计")&&miss.Contains("空"),"模板：没有的属性、空值属性都算缺");
            var tf=RenameRules.TemplateFields("{原名}-{图号}-{序号:2}-{图号}{零件名称");
            Check(tf.Count==1&&tf[0]=="图号","模板里用到的属性（去重，不含原名 / 序号，没闭合的不算）");

            // 引用初筛
            var bytes=new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("xx"));
            bytes.AddRange(Encoding.Unicode.GetBytes(@"C:\a\YF25.07G01-L1-M30-02-07.PAR"));
            bytes.Add(0);bytes.Add(0);
            bytes.AddRange(Encoding.UTF8.GetBytes(@"..\子装配\手推车.ASM"));
            bytes.Add(0x4A);bytes.Add(0);   // 紧挨着的长度前缀，读成 UTF-16 是 'J'
            bytes.AddRange(Encoding.Unicode.GetBytes("支撑板.psm"));
            var data=bytes.ToArray();
            Check(new RenameRefScan(new[]{"yf25.07g01-l1-m30-02-07.par"}).MayReference(data),"UTF-16 路径里认出文件名，扩展名不分大小写");
            Check(new RenameRefScan(new[]{"手推车.asm"}).MayReference(data),"UTF-8 路径里认出中文文件名");
            Check(new RenameRefScan(new[]{"支撑板.PSM"}).MayReference(data),"名字前面紧挨着可打印的长度前缀也认得出（不切整名）");
            Check(!new RenameRefScan(new[]{"别的.par","支撑板.par"}).MayReference(data),"对不上 = 排除（扩展名不同也不算）");
            Check(new RenameRefScan(new[]{"02-07.par"}).MayReference(data),"旧名字是别的名字的尾巴 = 当成可能链着（多开不漏）");
            Encoding gbk=null; try{ gbk=Encoding.GetEncoding(936); }catch{}   // dotnet（tests/pure）里没有 GBK，只在 PanelTests 里测
            if(gbk!=null){
                var g=new List<byte>(gbk.GetBytes(@"D:\图\支撑板B.par"));
                Check(new RenameRefScan(new[]{"支撑板b.par"}).MayReference(g.ToArray()),"GBK 写的名字也认（英文字母不分大小写）");
            }
            Check(new RenameRefScan(new string[0]).MayReference(data)==false,"没有要找的名字 = 都排除");
            Check(RenameRefScan.Encrypted(Encoding.ASCII.GetBytes("%TSD-Header-###%xxxx"))&&!RenameRefScan.Encrypted(new byte[]{0xD0,0xCF,0x11,0xE0}),"认得出加密头");
        }
    }
}
