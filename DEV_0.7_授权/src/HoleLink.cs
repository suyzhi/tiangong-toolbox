using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TianGongCadSuite {
    // 一个配做孔和它的参考孔之间的"关联"。
    //
    // 为什么要自己记：天工CAD 的接口建不了跨零件关联 —— 在装配里对另一个零件的边
    // Profile.IncludeEdge 一律 0x80040224、ProjectEdge 一律 E_FAIL，零件 HasInterpartLinks 始终为假
    // （真机探针 tools/hole-feature-probe T7，2026-10-13）。原来打出来的孔只是"打的那一刻"位置对，
    // 参考零件一挪，所有孔全部错位。
    //
    // 现在打孔时把"这个孔是照哪个孔配的"写进孔特征的属性集（AttributeSets，保存重开后仍在，已实测），
    // 之后随时可以按关联重新求一次孔心、把孔挪回同轴（HoleSync）。记的全是**零件局部坐标**：
    //   参考孔：参考零件的实例路径 + 孔心/轴线（参考零件局部）+ 参考孔特征的 SystemName（有的话）
    //   配做孔：目标零件的实例路径 + 当前孔心（目标零件局部，同步后更新）
    // 这样参考零件在装配里挪了（实例变换变了）、或者参考孔特征本身在零件里被改了位置
    // （按 SystemName 找回特征、读它现在的孔心），都能算出新的孔心。
    //
    // 纯逻辑：序列化 / 求新孔心 都在这里，tests 直接断言。
    public sealed class HoleLink {
        public const string AttributeSet = "TGAutoHole";
        public const string AttributeName = "Link";
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public string RefOcc = "";       // 参考零件实例路径（"子装配.asm:1/板A.par:1"）
        public string RefDoc = "";       // 参考零件文件名（实例改名/重排序号后按文件名兜底）
        public string RefMode = "R";     // 实例矩阵取法：R = 实例自己的矩阵，C = 逐级相乘（嵌套子装配）
        public V3 RefCenter;             // 参考孔孔心（参考零件局部，米）
        public V3 RefAxis = new V3(0, 0, 1);   // 参考孔轴线（参考零件局部，单位向量）
        public string RefFeature = "";   // 参考孔所属孔特征的 SystemName（参考孔不是孔特征时为空）
        public double RefDiameterMm;     // 参考孔孔径（只用于提示）
        public string TgtOcc = "";       // 打孔零件实例路径
        public string TgtDoc = "";
        public string TgtMode = "R";
        public V3 TgtCenter;             // 配做孔当前孔心（打孔零件局部，米）

        // ---- 序列化：k=v;k=v。名字里可能有 ; = %，转义成 %3B %3D %25 ----
        public string Format(){
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            Put(sb, "v", Version.ToString(ci));
            Put(sb, "ro", RefOcc); Put(sb, "rd", RefDoc); Put(sb, "rm", RefMode);
            Put(sb, "rc", Vec(RefCenter)); Put(sb, "ra", Vec(RefAxis));
            Put(sb, "rf", RefFeature); Put(sb, "dia", RefDiameterMm.ToString("R", ci));
            Put(sb, "to", TgtOcc); Put(sb, "td", TgtDoc); Put(sb, "tm", TgtMode);
            Put(sb, "tc", Vec(TgtCenter));
            return sb.ToString();
        }

        public static HoleLink Parse(string text){
            if (string.IsNullOrEmpty(text)) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in text.Split(';')) {
                if (part.Length == 0) continue;
                int i = part.IndexOf('=');
                if (i <= 0) continue;
                map[part.Substring(0, i)] = Unescape(part.Substring(i + 1));
            }
            string v;
            if (!map.TryGetValue("v", out v)) return null;
            int ver;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out ver) || ver < 1) return null;
            var link = new HoleLink { Version = ver };
            link.RefOcc = Get(map, "ro"); link.RefDoc = Get(map, "rd"); link.RefMode = Get(map, "rm", "R");
            link.RefFeature = Get(map, "rf");
            link.TgtOcc = Get(map, "to"); link.TgtDoc = Get(map, "td"); link.TgtMode = Get(map, "tm", "R");
            V3 p;
            if (!TryVec(Get(map, "rc"), out p)) return null; link.RefCenter = p;
            if (!TryVec(Get(map, "ra"), out p) || p.Length < 1e-9) return null; link.RefAxis = p.Unit();
            if (!TryVec(Get(map, "tc"), out p)) return null; link.TgtCenter = p;
            double d;
            if (double.TryParse(Get(map, "dia"), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) link.RefDiameterMm = d;
            if (link.RefOcc.Length == 0 && link.RefDoc.Length == 0) return null;
            return link;
        }

        // ---- 求新孔心 ----
        // 参考孔轴线（参考零件局部 → 装配）与打孔面（打孔零件局部 → 装配）求交，再换回打孔零件局部。
        // planePoint / planeNormal：打孔面（= 孔特征轮廓所在的基准面），打孔零件局部。
        public static bool Solve(Transform refPlacement, V3 refCenterLocal, V3 refAxisLocal,
                                 Transform tgtPlacement, V3 planePointLocal, V3 planeNormalLocal,
                                 out V3 desiredLocal, out string why){
            desiredLocal = new V3(); why = "";
            if (refPlacement == null || tgtPlacement == null) { why = "读不到零件在装配里的位置"; return false; }
            V3 c, a, pp, n;
            try {
                c = refPlacement.Point(refCenterLocal);
                a = refPlacement.Vector(refAxisLocal).Unit();
                pp = tgtPlacement.Point(planePointLocal);
                n = tgtPlacement.Normal(planeNormalLocal).Unit();
            } catch (Exception e) { why = "坐标换算失败：" + e.Message; return false; }
            double denom = a.Dot(n);
            if (Math.Abs(denom) < 0.05) { why = "参考孔的轴线已经和打孔面平行，无法定位孔心（参考零件可能被转了方向）"; return false; }
            double t = (pp - c).Dot(n) / denom;
            var hit = c + a * t;
            desiredLocal = tgtPlacement.InversePoint(hit);
            if (!desiredLocal.Finite) { why = "求出来的孔心无效"; return false; }
            return true;
        }

        // 孔心偏了多少才算"错位"（毫米）。CAD 的位置精度远好于这个值，
        // 0.001mm 以内的差别只是浮点舍入，不值得去改模型。
        public const double SyncToleranceMm = 0.001;

        public static double OffsetMm(V3 current, V3 desired){ return (desired - current).Length * 1000.0; }

        // ---- 工具 ----
        static void Put(StringBuilder sb, string k, string v){
            if (sb.Length > 0) sb.Append(';');
            sb.Append(k).Append('=').Append(Escape(v ?? ""));
        }
        static string Get(Dictionary<string, string> m, string k){ return Get(m, k, ""); }
        static string Get(Dictionary<string, string> m, string k, string fallback){ string v; return m.TryGetValue(k, out v) ? v : fallback; }
        static string Escape(string s){ return s.Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D"); }
        static string Unescape(string s){ return s.Replace("%3D", "=").Replace("%3B", ";").Replace("%25", "%"); }
        static string Vec(V3 v){
            var ci = CultureInfo.InvariantCulture;
            return v.X.ToString("R", ci) + "," + v.Y.ToString("R", ci) + "," + v.Z.ToString("R", ci);
        }
        static bool TryVec(string s, out V3 v){
            v = new V3();
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(',');
            if (parts.Length != 3) return false;
            double x, y, z; var ci = CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0], NumberStyles.Float, ci, out x)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, ci, out y)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, ci, out z)) return false;
            v = new V3(x, y, z);
            return v.Finite;
        }
    }
}
