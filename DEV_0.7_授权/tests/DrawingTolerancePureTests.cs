using System;
namespace TianGongCadSuite {
    // 一键出图 0.11.1 的公差规矩：H7 数值、公差写法统一、形位公差框编码、哪些孔算定位销孔。
    // 例子都取自普查里真实的人图写法（YF25.07G01-L1-M30-02-002 / -023、SK26.01-OK310-02-005 / -019）。
    public static class DrawingTolerancePureTests {
        static void Check(bool ok, string label){ if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        public static void Run(){
            Check(DrawingTolerance.H7Upper(6) == 0.012 && DrawingTolerance.H7Upper(8) == 0.015 && DrawingTolerance.H7Upper(20) == 0.021 && DrawingTolerance.H7Upper(30) == 0.021, "H7：Ø6 +0.012、Ø8 +0.015、Ø20/Ø30 +0.021（和人图读回的一样）");
            Check(DrawingTolerance.H7Upper(4) == 0.012 && DrawingTolerance.H7Upper(3) == 0.010 && DrawingTolerance.H7Upper(10) == 0.015, "H7 分段：段上限含在本段");

            Check(DrawingTolerance.Normalize(6, "H7", "", "+0.012", "0") == "H7", "公差带直接用");
            Check(DrawingTolerance.Normalize(6, "", "", "+0.012", "0") == "H7", "直接写数值的 H7（Ø6 +0.012/0）认成 H7");
            Check(DrawingTolerance.Normalize(6, "", "", "+0.012", "-0.000") == "H7", "下偏差写成 -0.000 也认");
            Check(DrawingTolerance.Normalize(21, "", "", "+0.021", "-0") == "H7", "Ø21 +0.021/-0 = H7");
            Check(DrawingTolerance.Normalize(25.8, "", "", "±0.03", "±0.03") == "±0.03", "对称公差（天工读回是 ±0.03 / ±0.03）");
            Check(DrawingTolerance.Normalize(10, "", "", "+0.05", "-0.05") == "±0.05", "上下偏差相反数 = 对称公差");
            Check(DrawingTolerance.Normalize(25, "", "", "+0.08", "+0.03") == "+0.08/+0.03", "不对称的上下偏差原样（槽宽 25 +0.08/+0.03）");
            Check(DrawingTolerance.Normalize(50, "", "", "+.025", "0") == "H7", "省掉 0 的 +.025 也能读（Ø50 H7）");
            Check(DrawingTolerance.Normalize(12, "", "f8", "", "") == "f8", "轴的公差带");
            Check(DrawingTolerance.Normalize(12, "", "", "", "") == "", "没公差");

            Check(DrawingTolerance.FrameKey("%PO%VB%DI0.03") == "PO0.03" && DrawingTolerance.FrameKey("%PO%VB0.03") == "PO0.03", "位置度：有没有 Ø 都一样");
            Check(DrawingTolerance.FrameKey("%PP%VB%DI0.04%VBA") == "PP0.04", "平行度：去掉基准字母");
            Check(DrawingTolerance.FrameKey("%FL%VB0.12%VB整面铣面") == "FL0.12", "平面度带说明文字");
            Check(DrawingTolerance.FrameKey("") == "" && DrawingTolerance.FrameKey("abc") == "", "空框 / 不认识的");
            Check(DrawingTolerance.FrameKey(DrawingTolerance.PositionFrame) == "PO0.03", "插件加的位置度框和人图的写法对得上");

            Check(DrawingTolerance.IsPinHole(6, true, false, false) && DrawingTolerance.IsPinHole(4, true, false, false) && DrawingTolerance.IsPinHole(8, true, false, false), "Ø4 / Ø6 / Ø8 光孔是定位销孔");
            Check(DrawingTolerance.IsPinHole(5, true, false, false), "Ø5 光孔也加（55%，用户定了保留）");
            Check(!DrawingTolerance.IsPinHole(6.6, true, false, false) && !DrawingTolerance.IsPinHole(5.5, true, false, false), "过孔不是");
            Check(!DrawingTolerance.IsPinHole(6, false, false, false), "Ø6 的轴 / 凸台不是");
            Check(!DrawingTolerance.IsPinHole(6, true, true, false) && !DrawingTolerance.IsPinHole(6, true, false, true), "螺纹孔、沉孔不是");

            // 销孔所在的面：侧面视图里 20×100 的长条（x 0..20, y 0..100），Ø8 盲孔从 x=20 那个面往里钻、孔心 y=30，
            // 孔壁轮廓（两条横线 y=26 / 34，x 10..20）和孔底那条短竖线 x=10 不是面
            const double mm = 0.001;
            var side = new System.Collections.Generic.List<DwgEdge> {
                DwgEdge.Line(0, 0, 20 * mm, 0), DwgEdge.Line(20 * mm, 0, 20 * mm, 100 * mm), DwgEdge.Line(20 * mm, 100 * mm, 0, 100 * mm), DwgEdge.Line(0, 100 * mm, 0, 0),
                DwgEdge.Line(10 * mm, 26 * mm, 20 * mm, 26 * mm), DwgEdge.Line(10 * mm, 34 * mm, 20 * mm, 34 * mm), DwgEdge.Line(10 * mm, 26 * mm, 10 * mm, 34 * mm) };
            double[] at;
            var face = DrawingTolerance.MouthFace(side, true, 20 * mm, 30 * mm, 4 * mm, out at);
            Check(face != null && Math.Abs(face.X1 - 20 * mm) < 1e-9 && Math.Abs(face.Length - 100 * mm) < 1e-9, "孔口那个面 = x=20 的长竖线");
            Check(at != null && Math.Abs(at[0] - 20 * mm) < 1e-9 && Math.Abs(at[1] - 67 * mm) < 1e-9, "粗糙度挂在孔带外面长的那一段中间（y 34..100 → 67）");
            Check(DrawingTolerance.MouthFace(side, true, 10 * mm, 30 * mm, 4 * mm, out at) == null, "孔底那条短线不算面");
            Check(DrawingTolerance.MouthFace(side, false, 20 * mm, 30 * mm, 4 * mm, out at) == null, "方向不对（孔轴沿 y 时找横线 y=30）找不到");

            Check(DrawingTolerance.Note(0, 0, false) == "", "没加就不提示");
            Check(DrawingTolerance.Note(1, 2, true).Contains("2 个定位销孔") && DrawingTolerance.Note(1, 2, true).Contains("Ra3.2"), "提示写出加了什么");
        }
    }
}
