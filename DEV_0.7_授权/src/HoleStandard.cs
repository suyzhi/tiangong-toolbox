using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianGongCadSuite {
    // 天工CAD 孔标准表里的一行：标准 / 子类型 / 大小 / 配合 → 尺寸。
    public sealed class HoleStdRow {
        public HoleKind Kind;
        public string Standard = HoleStandards.Standard;
        public string SubType = "", Size = "", Fit = "";
        public double Dia;                 // 孔径（螺纹孔 = 内螺纹小径）
        public double CbDia, CbDepth;      // 沉头直径 / 深度
        public double CsDia, CsAngle;      // 埋头直径 / 角度
        public string Label {
            get { return SubType + " " + Size + (Fit.Length > 0 && Fit != "精确" ? " " + Fit : ""); }
        }
    }

    // 为什么要有这张表：用户反馈"自动打孔打出来的孔，用天工自带的孔命令打开，大小一栏显示 *"。
    //
    // 天工的孔命令按「标准 + 子类型 + 大小 + 配合」去孔标准表里找一行，找到了才显示成标准孔；
    // 找不到（或者孔的尺寸和那一行对不上）就显示 *（= 非标/自定义孔）。原来插件建孔时：
    //   · 标准写 "ISO Metric"、子类型/大小/配合全部留空 —— 通孔、沉孔、锥沉一律对不上；
    //   · 沉孔、锥沉的尺寸来自插件自己的经验表（M6 沉孔深 6.5、锥孔 Φ10），和天工表（6.8 / Φ12.6）不一样。
    // 现在：打孔前按这张表认一遍，**尺寸正好等于表里某一行**就把那一行的 标准/子类型/大小/配合 一起写进孔数据；
    // 规格是"按表反推"出来的（HoleMatcher.FromRow），默认尺寸也直接取这张表 —— 天工孔命令打开就是标准孔。
    // 用户手改成表里没有的尺寸，就老实当非标孔（天工里显示 * 是对的），界面上也写明。
    //
    // 数据来源：天工CAD 2025 安装目录 Program\SEFactorySettings.sepref → Preferences\Holes\GB Metric.xlsx
    // （天工自带的「保存的设置」Custom.xml 里的孔全部用 GB Metric）。只收录插件会用到的公制常用规格。
    // 纯逻辑：不碰 CAD，tests 直接断言。
    public static class HoleStandards {
        public const string Standard = "GB Metric";
        public const string ClearanceSub = "常规螺钉间隙", DrillSub = "钻头大小", TapSub = "标准螺纹",
                            CounterboreSub = "内六角螺钉", CountersinkSub = "机用";
        public const string Close = "紧密 (H12)", Normal = "普通 (H13)", Loose = "宽松 (H14)", Exact = "精确";
        const double Tol = 0.0005;   // 毫米。表里的值最多两位小数，0.0005 只吸收浮点误差

        // 常规螺钉间隙（通孔）：大小、紧密 / 普通 / 宽松 三档孔径
        static readonly object[][] Clearance = {
            new object[]{"M1.6",1.7,1.8,2.0}, new object[]{"M2",2.2,2.4,2.6}, new object[]{"M2.5",2.7,2.9,3.1},
            new object[]{"M3",3.2,3.4,3.6}, new object[]{"M3.5",3.7,3.9,4.2}, new object[]{"M4",4.3,4.5,4.8},
            new object[]{"M5",5.3,5.5,5.8}, new object[]{"M6",6.4,6.6,7.0}, new object[]{"M8",8.4,9.0,10.0},
            new object[]{"M10",10.5,11.0,12.0}, new object[]{"M12",13.0,13.5,14.5}, new object[]{"M14",15.0,15.5,16.5},
            new object[]{"M16",17.0,17.5,18.5}, new object[]{"M18",19.0,20.0,21.0}, new object[]{"M20",21.0,22.0,24.0},
            new object[]{"M22",23.0,24.0,26.0}, new object[]{"M24",25.0,26.0,28.0}, new object[]{"M27",28.0,30.0,32.0},
            new object[]{"M30",31.0,33.0,35.0},
        };
        // 钻头大小（通孔，配合"精确"，大小 = 孔径）
        static readonly double[] Drill = {
            0.35,0.38,0.4,0.42,0.45,0.48,0.5,0.55,0.6,0.65,0.7,0.75,0.8,0.85,0.9,0.95,1,1.05,1.1,1.15,1.2,1.25,1.3,1.35,1.4,1.45,
            1.5,1.55,1.6,1.65,1.7,1.75,1.8,1.85,1.9,1.95,2,2.05,2.1,2.15,2.2,2.25,2.3,2.35,2.4,2.45,2.5,2.55,2.6,2.65,2.7,2.75,
            2.8,2.9,3,3.1,3.2,3.25,3.3,3.4,3.5,3.6,3.7,3.75,3.8,3.9,4,4.1,4.2,4.25,4.3,4.4,4.5,4.6,4.7,4.75,4.8,4.9,5,5.1,5.2,
            5.25,5.3,5.4,5.5,5.6,5.7,5.75,5.8,5.9,6,6.1,6.2,6.25,6.3,6.4,6.5,6.6,6.7,6.75,6.8,6.9,7,7.1,7.2,7.25,7.3,7.4,7.5,7.6,
            7.7,7.75,7.8,7.9,8,8.1,8.2,8.25,8.3,8.4,8.5,8.6,8.7,8.75,8.8,8.9,9,9.1,9.2,9.25,9.3,9.4,9.5,9.6,9.7,9.75,10,10.2,
            10.5,10.8,11,11.2,11.5,11.8,12,12.5,13,13.5,14,14.5,15,15.5,16,17,17.5,18,18.5,19,20,20.5,21,21.5,22,22.5,23,23.5,
            24,24.5,25,26
        };
        // 标准螺纹（螺纹孔按内螺纹小径建模）：大小、内螺纹小径
        static readonly object[][] Tap = {
            new object[]{"M3",2.459}, new object[]{"M4",3.242}, new object[]{"M5",4.134}, new object[]{"M6",4.917},
            new object[]{"M8",6.647}, new object[]{"M10",8.376}, new object[]{"M12",10.106}, new object[]{"M16",13.835},
            new object[]{"M20",17.294},
        };
        // 沉头孔·内六角螺钉：大小、沉头直径、沉头深度（孔径按配合取常规螺钉间隙那三档）
        static readonly object[][] Counterbore = {
            new object[]{"M3",6.0,3.4}, new object[]{"M4",8.0,4.6}, new object[]{"M5",10.0,5.7}, new object[]{"M6",11.0,6.8},
            new object[]{"M8",15.0,9.0}, new object[]{"M10",18.0,11.0}, new object[]{"M12",20.0,13.0}, new object[]{"M16",26.0,17.5},
            new object[]{"M20",33.0,21.5},
        };
        // 埋头孔·机用（90°）：大小、埋头直径。天工表里只到 M10。
        static readonly object[][] Countersink = {
            new object[]{"M3",6.3}, new object[]{"M4",9.4}, new object[]{"M5",10.4}, new object[]{"M6",12.6},
            new object[]{"M8",17.3}, new object[]{"M10",20.0},
        };
        static readonly string[] Fits = { Close, Normal, Loose };

        static bool Eq(double a, double b){ return Math.Abs(a - b) <= Tol; }
        static string N(double v){ return v.ToString("0.###", CultureInfo.InvariantCulture); }

        // ---- 给默认值用：某个规格在天工表里的沉头/埋头尺寸 ----
        public static bool CounterboreOf(string size, out double dia, out double depth){
            dia = 0; depth = 0;
            foreach (var r in Counterbore) if ((string)r[0] == size) { dia = (double)r[1]; depth = (double)r[2]; return true; }
            return false;
        }
        public static bool CountersinkOf(string size, out double dia){
            dia = 0;
            foreach (var r in Countersink) if ((string)r[0] == size) { dia = (double)r[1]; return true; }
            return false;
        }
        public static bool ClearanceOf(string size, string fit, out double dia){
            dia = 0;
            int k = Array.IndexOf(Fits, fit);
            if (k < 0) return false;
            foreach (var r in Clearance) if ((string)r[0] == size) { dia = (double)r[1 + k]; return true; }
            return false;
        }
        static string FitOfClearance(string size, double dia){
            foreach (var r in Clearance) {
                if ((string)r[0] != size) continue;
                for (int k = 0; k < 3; k++) if (Eq((double)r[1 + k], dia)) return Fits[k];
            }
            return null;
        }

        // 这个孔规格在天工孔标准表里是哪一行。对不上返回 null（= 非标孔，天工孔命令里会显示 *）。
        // 只认"尺寸完全等于表里那一行"的 —— 差一点也不能硬套，否则天工会按表把尺寸改掉，打出来的孔就不是用户要的了。
        public static HoleStdRow Resolve(HoleSpec s){
            if (s == null) return null;
            string size = s.ThreadSize ?? "";
            switch (s.Kind) {
                case HoleKind.Tapped: {
                    foreach (var r in Tap)
                        if ((string)r[0] == size && Eq((double)r[1], s.HoleDiameter))
                            return new HoleStdRow { Kind = HoleKind.Tapped, SubType = TapSub, Size = size, Fit = "", Dia = (double)r[1] };
                    return null;
                }
                case HoleKind.Counterbore: {
                    foreach (var r in Counterbore) {
                        string sz = (string)r[0];
                        if (size.Length > 0 && sz != size) continue;
                        if (!Eq((double)r[1], s.CounterboreDiameter) || !Eq((double)r[2], s.CounterboreDepth)) continue;
                        string fit = FitOfClearance(sz, s.HoleDiameter);
                        if (fit == null) continue;
                        return new HoleStdRow { Kind = HoleKind.Counterbore, SubType = CounterboreSub, Size = sz, Fit = fit,
                                                Dia = s.HoleDiameter, CbDia = (double)r[1], CbDepth = (double)r[2] };
                    }
                    return null;
                }
                case HoleKind.Countersink: {
                    if (!Eq(s.CountersinkAngle, 90)) return null;
                    foreach (var r in Countersink) {
                        string sz = (string)r[0];
                        if (size.Length > 0 && sz != size) continue;
                        if (!Eq((double)r[1], s.CountersinkDiameter)) continue;
                        string fit = FitOfClearance(sz, s.HoleDiameter);
                        if (fit == null) continue;
                        return new HoleStdRow { Kind = HoleKind.Countersink, SubType = CountersinkSub, Size = sz, Fit = fit,
                                                Dia = s.HoleDiameter, CsDia = (double)r[1], CsAngle = 90 };
                    }
                    return null;
                }
                default: {
                    // 通孔：有螺纹规格（M6 的过孔）优先按"常规螺钉间隙 M6 普通"认，看着最明白；其次按钻头大小认
                    if (size.Length > 0) {
                        string fit = FitOfClearance(size, s.HoleDiameter);
                        if (fit != null) return new HoleStdRow { Kind = HoleKind.Through, SubType = ClearanceSub, Size = size, Fit = fit, Dia = s.HoleDiameter };
                    }
                    foreach (var d in Drill)
                        if (Eq(d, s.HoleDiameter))
                            return new HoleStdRow { Kind = HoleKind.Through, SubType = DrillSub, Size = N(d), Fit = Exact, Dia = d };
                    return null;
                }
            }
        }

        // 界面上的一句话："天工标准孔：内六角螺钉 M6 普通 (H13)" / "非标孔：天工孔命令里大小会显示 *"
        public static string Describe(HoleSpec s){
            var row = Resolve(s);
            if (row != null) return "天工标准孔：" + row.Label;
            return "非标孔（天工孔标准表里没有这个尺寸，用天工自带的孔命令打开时大小会显示 *）";
        }
    }
}
