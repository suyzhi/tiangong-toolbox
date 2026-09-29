using System;
namespace TianGongCadSuite {
    // 纯逻辑回归：内嵌板批次文件名必须唯一（带时间戳），否则 CAD 会按文件名把装配目录里的旧同名零件插进来。
    public static class PanelNamingPureTests {
        static void Check(bool ok,string label){ if(!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        public static void Run(){
            var t1=new DateTime(2026,9,29,13,3,6);
            var t2=t1.AddSeconds(1);
            string s1=PanelNaming.Stamp(t1), s2=PanelNaming.Stamp(t2);
            Check(s1=="20260929_130306","时间戳格式 yyyyMMdd_HHmmss：" + s1);
            Check(PanelNaming.FileName(s1,1)=="填充板_20260929_130306_001.par","第 1 块名字：" + PanelNaming.FileName(s1,1));
            Check(PanelNaming.FileName(s1,14)=="填充板_20260929_130306_014.par","第 14 块名字：" + PanelNaming.FileName(s1,14));
            Check(PanelNaming.FileName(s1,1)!=PanelNaming.FileName(s2,1),"相邻两批的第 1 块不同名");
            Check(PanelNaming.FileName(s1,1).EndsWith(".par"),"扩展名是 .par");
            Check(PanelNaming.FileName(s1,1).StartsWith(PanelNaming.Prefix),"统一前缀（便于用户识别）");
            // 以前的老名字只带序号：装配目录里一旦有同名旧板子，CAD 就会把旧的插进来。
            Check(PanelNaming.FileName(s1,1)!="填充板_001.par","不再产生只带序号的旧式名字");
            string dir1=PanelNaming.BatchDirectoryName(s1,"c32ff3b2");
            Check(dir1=="自动填充_20260929_130306_c32ff3b2","批次目录名：" + dir1);
            Check(dir1!=PanelNaming.BatchDirectoryName(s1,"712fa478"),"同秒两批目录仍不同（随机后缀）");
            Check(PanelNaming.FileName(s1,0)=="填充板_20260929_130306_000.par","序号从 0 开始也是三位固定宽");
            Console.WriteLine("PANEL NAMING ASSERTIONS OK");
        }
    }
}
