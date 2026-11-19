using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // 参考孔：用户点选的一个已有的孔。三种点法都能得到它 ——
    //   ① 点孔口的圆形边线；
    //   ② 点一个"充满孔的面"（面上的孔自动全认出来，按孔径归成几类）；
    //   ③ 点孔的内壁圆柱面（圆锥面 / 孔口圆角面也行）。
    // 沉孔 / 锥沉 / 倒角孔口在读出来的时候就会取"孔口下面那个孔径"当配做孔径（见 HoleScan），
    // 否则 Φ11 的沉孔会被当成 M10 过孔，目标件上配出来的孔整个是错的。
    public sealed class ReferenceHole : ScannedHole {
        public string Where = ""; // 给用户看的来源描述（保留旧字段，供日志与排查用）
    }

    // 目标面：用户点选的一个平面。
    public sealed class TargetFace {
        public PlaneInput Plane;      // 装配坐标下的平面
        public G.Face Face;           // 零件局部坐标下的面片（用于建基准面）
        public P.PartDocument Part;   // 拥有该面的零件
        public Transform Placement;   // 零件局部 -> 装配
        public object Pick;           // 用户点的选择引用（带实例路径，原生关联进原位编辑时用）
        public string Label = "";
        public string PartName = "";
        // 面在零件的第几个实体上（part.Models 里，1 起；0 = 还没认，AutoHoleWriter.ModelFor 现认）。多实体零件见 PartBodies
        public int Body;
        // 面内包围盒缓存（FaceFrameReader.Bounds 填）。实时预览每改一次参数都要判 N×M 个
        // 孔心是否落在面内，而算包围盒要走一遍面的边（COM），所以算一次存这里。
        public FaceFrameReader.FaceBounds Bounds;
        // 点面时预判的"这张面挂不了天工关联"的原因（零件的链接属于别的装配……），空 = 没看出问题。预览里提前说
        public string LinkNote = "";
        public string FaceId = "";    // 面片 ID（只在同一个实体里唯一，见 SameFace）
        // 这张面所在实体上已有的孔（预览里提前算"哪些位置本来就有孔"用，0.8.5）。读一次缓存；打完孔整个 TargetFace 会换新的
        internal HoleSpots Spots;
        internal HoleSpots ExistingSpots(){
            if (Spots == null) { try { Spots = AutoHoleWriter.ScanSpots(AutoHoleWriter.ModelFor(this)); } catch { Spots = new HoleSpots(); } }
            return Spots;
        }
        // 装配坐标的孔心在这张面上是不是本来就有孔（孔口在面上，见 HoleSpots.At）
        internal bool HasHoleAt(V3 centreAssembly){ double d; return HasHoleAt(centreAssembly, out d); }
        internal bool HasHoleAt(V3 centreAssembly, out double diameterMm){
            diameterMm = 0;
            try {
                var local = Placement.InversePoint(OntoFace(centreAssembly));
                var n = Placement.InverseNormal(Plane.Normal).Unit();
                return ExistingSpots().At(local, n, out diameterMm);
            } catch { return false; }
        }
        // "这个位置本来就有孔"的说法：和 DrillRequests 里的一样
        internal static string AlreadySay(string source, double existingMm, HoleSpec want){
            double w = want == null ? 0 : want.HoleDiameter;
            string where = string.IsNullOrEmpty(source) ? "" : source + "：";
            return Math.Abs(existingMm - w) <= 0.05 && w > 0
                ? where + "这个位置已经有同规格的孔（Φ" + existingMm.ToString("0.##") + "），无需再打。"
                : where + "这个位置已经有孔（Φ" + existingMm.ToString("0.##") + "），规格与要打的不同，已跳过。";
        }
        public string Risk = "";      // 点面时看出来的"改这个文件要当心"（只读 / 标准件库里的文件），空 = 没看出问题（0.8.5）
        // 只读文件（文件属性只读：PDM 没检出、从光盘/共享拷来的）：孔能打进内存，存盘时存不下；标准件库 / 外购件：打孔会改到库里的原件、所有项目都跟着变
        internal static string RiskOf(P.PartDocument part, string assemblyPath){
            string f = ""; try { f = part.FullName; } catch { }
            if (string.IsNullOrEmpty(f)) return "";
            string name = System.IO.Path.GetFileName(f);
            // 不看 part.ReadOnly：装配里没激活编辑的零件文档它也回 True（真机 User085E2E S1：刚建的零件照样打孔、存盘），会误报
            try { if (System.IO.File.Exists(f) && (System.IO.File.GetAttributes(f) & System.IO.FileAttributes.ReadOnly) != 0) return "「" + name + "」文件是只读的，孔能打上但存不了盘"; } catch { }
            return LibraryHint(f, assemblyPath);
        }
        static readonly string[] LibraryWords = { "标准件", "外购", "standard parts", "misumi", "米思米", "library", "零件库", "库文件" };
        internal static string LibraryHint(string partPath, string assemblyPath){
            if (string.IsNullOrEmpty(partPath)) return "";
            string low = partPath.ToLowerInvariant();
            string asmLow = (assemblyPath ?? "").ToLowerInvariant();
            foreach (var w in LibraryWords)
                if (low.IndexOf(w, StringComparison.Ordinal) >= 0 && asmLow.IndexOf(w, StringComparison.Ordinal) < 0)
                    return "「" + System.IO.Path.GetFileName(partPath) + "」看起来是标准件库 / 外购件里的文件（" + System.IO.Path.GetDirectoryName(partPath) + "），打孔会改到库里的原件，用到它的项目都会变";
            return "";
        }
        public int Instances;         // 这个零件文件在装配里用了几次（点面时数，0 = 没数 / 数不出）。>1 时打的孔每处都有

        // 是不是同一张面：同一个零件实例（文件 + 装配位置）、同一个实体、同一个面 ID，而且平面数据也重合。
        // 只比面 ID 不够：多实体零件的面 ID 是各实体自己编的，跨实体大量重号（真机 02-07：39 个实体 710 张面只有 298 个不同 ID）——
        // 0.8.3 以前点第二个实体上的面，会把第一个实体上同号的面"换掉"（用户说的「不同设计体上的面无法同时打」）。
        public static bool SameFace(TargetFace a, TargetFace b){
            if (a == null || b == null) return false;
            if (!string.Equals(DocName(a.Part), DocName(b.Part), StringComparison.OrdinalIgnoreCase) || DocName(a.Part).Length == 0) return false;
            if (Math.Max(1, a.Body) != Math.Max(1, b.Body)) return false;
            if (a.FaceId.Length > 0 && b.FaceId.Length > 0 && a.FaceId != b.FaceId) return false;
            if (!SamePlacement(a.Placement, b.Placement)) return false;
            if (a.Plane == null || b.Plane == null) return a.FaceId.Length > 0 && a.FaceId == b.FaceId;
            var na = a.Plane.Normal.Unit(); var nb = b.Plane.Normal.Unit();
            if (na.Dot(nb) < 1 - 1e-6) return false;                                       // 法向不同（两个实体贴在一起时共面但外法向相反）
            return Math.Abs((b.Plane.Point - a.Plane.Point).Dot(na)) < 1e-6;               // 共面
        }
        internal static bool SamePlacement(Transform a, Transform b){
            if (a == null || b == null) return a == b;
            for (int i = 0; i < 16; i++) if (Math.Abs(a.M[i] - b.M[i]) > 1e-7) return false;
            return true;
        }
        // 零件文档对象还活着吗（进出原位编辑、关掉它的窗口以后天工会重新加载它，原来的对象全部"已断开"）
        internal static bool PartAlive(P.PartDocument part){
            if (part == null) return false;
            try { var n = part.Name; return true; } catch { return false; }
        }
        internal static string DeadPartMessage(string partName){
            return "「" + (string.IsNullOrEmpty(partName) ? "零件" : partName) + "」被天工重新加载过（刚进出过原位编辑或关过它的窗口），原来点的面已经失效。请重新点一次这张面再打。";
        }
        // 装配坐标的一点沿面法向投到打孔面上（0.8.5）。"已经有孔"只认孔口在打孔面上的圆（HoleSpots.At），
        // 孔心得在面上：圆周排孔点的定位圆可能在法兰另一面（差一个板厚），原来靠"同轴就算"糊过去了
        public V3 OntoFace(V3 p){
            if (Plane == null) return p;
            var n = Plane.Normal.Unit();
            if (!(n.Length > 0.5) || !p.Finite) return p;
            return p - n * (p - Plane.Point).Dot(n);
        }
        // 零件实例的键：文件 + 装配位置。同一个零件文件的两个实例算两个（原位编辑要进的是点中的那个实例）
        public string InstanceKey {
            get {
                var sb = new System.Text.StringBuilder(DocName(Part));
                if (Placement != null) foreach (var v in Placement.M) sb.Append('|').Append(Math.Round(v, 7).ToString(CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
        static string DocName(P.PartDocument part){
            if (part == null) return "";
            try { string f = part.FullName; if (!string.IsNullOrEmpty(f)) return f; } catch { }
            try { return part.Name ?? ""; } catch { return ""; }
        }
    }

    // 参考孔轴线和打孔面不垂直（斜的角度，度）
    public sealed class ObliqueHoleException : ArgumentException {
        public readonly double Degrees;
        public ObliqueHoleException(double deg) : base("参考孔轴线和打孔面不垂直（斜 " + deg.ToString("0.#", CultureInfo.InvariantCulture) + "°）：孔只能垂直于面打，打出来和参考孔不同轴，没有打。请点和参考孔垂直的面。"){ Degrees = deg; }
    }

    // 在目标零件上实际打孔。
    public sealed class DrillResult {
        public int Requested, Created;
        // 孔心位置上"本来就有孔"的个数（规格一致=已满足；规格不同=按用户意愿跳过）。
        // 这类不算失败，也不进 Failures —— 配对打孔时很常见。
        public int AlreadyOk;
        public List<string> Notes = new List<string>();
        public string Method = "";
        public string Audit = "";        // 打完孔回读到的异常（例如 CAD 偷偷换了孔型）
        public List<string> Failures = new List<string>();
        // 特征树里新出现的节点名（特征组名，或单个孔的名字），结果框里告诉用户"去哪找"
        public List<string> Features = new List<string>();
        public string Standard = "";     // 最近一个孔对上的天工孔标准表行（"内六角螺钉 M6 普通 (H13)"），对不上为空
        public int Linked;               // 写上了参考孔关联记录的孔数（属性集关联，0.7.3 的老办法）
        public int NativeLinked;         // 挂上了天工原生关联的孔数（0.7.8，特征树里带锁链）
        public int NativeMissed;         // 准备了原生关联、但打孔时没挂上（退回属性集关联或不关联）
        public int Combined;             // 合成的多孔孔特征个数（0.7.9，天工自己的 UserDefinedPattern 结构）
        public int Disconnected;         // 没打上、而且最后一次是 CAD 报"对象已断开/没注册"（0x80010108 一类）的孔数 —— 换个编辑环境（原位 ↔ 装配）再打一次（0.7.11）
        public readonly HoleUndo Undo = new HoleUndo();   // 这次改了模型的哪些地方（「撤销本次打孔」用）
        // 这次在特征树里新出现的孔特征（多孔孔特征 / 没合成的单孔），按建的顺序。孔位链接组要把它们和链接编在一起（0.8.4）
        public readonly List<object> TopFeatures = new List<object>();
        // 每个孔位的结果（带装配坐标，结果列表点一条就能跳过去）
        public readonly List<ResultList.Item> Outcomes = new List<ResultList.Item>();
        public bool Ok { get { return Created > 0 && Failures.Count == 0 && Audit.Length == 0; } }
    }
}
