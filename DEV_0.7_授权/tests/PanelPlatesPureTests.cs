using System;
using System.Collections.Generic;
using System.Linq;
namespace TianGongCadSuite {
    // 纯逻辑回归：跨子装配填充时，子装配里已有的板子不能当型材，并且要能标出"已经装了板"的框口。
    // 尺寸取自 SK26.01-OK310-03-001 实测（2026-10-15）：40x40 型材、4mm 填充板、整面 4mm 玻璃板。
    public static class PanelPlatesPureTests {
        static void Check(bool ok,string label){ if(!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        // 轴对齐的长方体的 8 个角点（米）
        static List<V3> Box(double x0,double y0,double z0,double x1,double y1,double z1){
            var r=new List<V3>();
            foreach(double x in new[]{x0,x1})foreach(double y in new[]{y0,y1})foreach(double z in new[]{z0,z1})r.Add(new V3(x,y,z));
            return r;
        }
        public static void Run(){
            V3 X=new V3(1,0,0),Y=new V3(0,1,0),Z=new V3(0,0,1);
            // 型材 / 板子的区分
            Check(!PanelPlates.IsPlate(Z,Box(0,0,0,.04,.04,2)),"40x40 立柱不是板");
            Check(!PanelPlates.IsPlate(X,Box(0,0,0,1,.04,.08)),"40x80 横梁不是板");
            Check(PanelPlates.IsPlate(Z,Box(0,0,0,.887,.004,1.192)),"887x1192x4 填充板是板（轴沿高）");
            Check(PanelPlates.IsPlate(Y,Box(0,0,0,.004,5.37,3.168)),"整面 5370x3168x4 玻璃板是板");
            Check(PanelPlates.IsPlate(X,Box(0,0,0,.6,.2,.006)),"窄长 600x200x6 板也是板");
            Check(!PanelPlates.IsPlate(X,Box(0,0,0,1,.1,.005)),"100 宽扁钢不当板（宽不到 150）");
            Check(!PanelPlates.IsPlate(X,Box(0,0,0,1,.2,.05)),"50 厚的厚块不当板");
            Check(!PanelPlates.IsPlate(X,new List<V3>()),"没有点不算板");
            var d=1/Math.Sqrt(2);var rotated=Box(0,0,0,.04,.04,2).Select(p=>new V3((p.X-p.Y)*d,(p.X+p.Y)*d,p.Z)).ToList();
            Check(!PanelPlates.IsPlate(Z,rotated),"绕轴转 45° 的方管不是板");

            // 框口：X 法向的面，Y 3507..4425、Z -1914..-574，中面 x=2.2215（单位米）
            var spec=new PanelSpec{Origin=new V3(2.2215,3.507,-1.914),U=Y,V=Z,N=X,Width=.918,Height=1.34,Thickness=.004,Gap=0};
            Check(PanelPlates.Covers(spec,Box(2.2195,3.51,-1.9,2.2235,4.42,-.58)),"同位置的 4mm 填充板盖住框口");
            Check(PanelPlates.Covers(spec,Box(2.2195,3.4,-2.0,2.2235,8.9,1.3)),"整面玻璃板盖住框口");
            Check(!PanelPlates.Covers(spec,Box(2.2195,4.47,-1.9,2.2235,5.8,-.58)),"隔壁框口的板子不算");
            Check(!PanelPlates.Covers(spec,Box(2.2195,3.51,-1.9,2.2235,3.9,-.58)),"只盖住一小半不算");
            Check(!PanelPlates.Covers(spec,Box(2.30,3.51,-1.9,2.304,4.42,-.58)),"离中面 80mm 的板子不算（别的平面）");
            Check(!PanelPlates.Covers(spec,Box(2.0,3.51,-1.9,2.9,3.514,-.58)),"垂直方向的板子不算");

            // 贴着框口的型材（提示「跨 N 个子装配」）
            Check(PanelPlates.Borders(spec,Box(2.2015,3.456,-2.066,2.2415,3.496,1.294)),"隔壁子装配的角柱贴着框口");
            Check(PanelPlates.Borders(spec,Box(2.2015,3.496,-1.954,2.2415,4.436,-1.914)),"下横梁贴着框口");
            Check(!PanelPlates.Borders(spec,Box(2.2015,7.886,-2.066,2.2415,7.926,1.254)),"远处的立柱不贴");
            Console.WriteLine("PANEL PLATES ASSERTIONS OK");
        }
    }
}
