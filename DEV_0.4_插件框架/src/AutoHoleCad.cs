using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    // 参考孔：用户点选的一条圆形边线。
    public sealed class ReferenceHole {
        public V3 Center;        // 装配坐标
        public V3 Axis;          // 装配坐标，单位向量
        public double DiameterMm;
        public string Where = ""; // 给用户看的来源描述
    }

    // 目标面：用户点选的一个平面。
    public sealed class TargetFace {
        public PlaneInput Plane;      // 装配坐标下的平面
        public G.Face Face;           // 零件局部坐标下的面片（用于建基准面）
        public P.PartDocument Part;   // 拥有该面的零件
        public Transform Placement;   // 零件局部 -> 装配
        public string Label = "";
        public string PartName = "";
        // 面内包围盒缓存（FaceFrameReader.Bounds 填）。实时预览每改一次参数都要判 N×M 个
        // 孔心是否落在面内，而算包围盒要走一遍面的边（COM），所以算一次存这里。
        public FaceFrameReader.FaceBounds Bounds;
    }

    public static class AutoHoleReader {
        public static ReferenceHole ReadReference(object selection){
            var pg = PickGeometry.Unwrap(selection);
            var edge = pg.Geometry as G.Edge;
            if (edge == null) throw new ArgumentException("请选择一条圆形孔边线（孔的轮廓圆）。");
            var circle = edge.Geometry as G.Circle;
            if (circle == null) throw new ArgumentException("选中的边不是圆孔。请在模型上点击孔口的圆形边线。");
            Array c = new double[3], axis = new double[3]; double radius = 0;
            circle.GetCircleData(ref c, ref axis, out radius);
            if (radius <= 0 || double.IsNaN(radius)) throw new ArgumentException("读取到的孔半径为 0，请重新选择孔边线。");
            var hole = new ReferenceHole {
                Center = pg.Transform.Point(V3.From(c)),
                Axis = pg.Transform.Vector(V3.From(axis)).Unit(),
                DiameterMm = radius * 2000.0   // 半径(米) -> 直径(毫米)
            };
            hole.Where = "Φ" + hole.DiameterMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return hole;
        }

        public static TargetFace ReadTarget(object selection){
            var pg = PickGeometry.Unwrap(selection);
            var face = pg.Geometry as G.Face;
            if (face == null) throw new ArgumentException("请选择一个平面作为打孔面。");
            var plane = face.Geometry as G.Plane;
            if (plane == null) throw new ArgumentException("选中的是曲面，打孔面必须是平面。");
            var part = face.Document as P.PartDocument;
            if (part == null) throw new ArgumentException("选中的面不属于零件文档，请直接在装配中选择零件上的平面。");
            Array p = new double[3], n = new double[3]; plane.GetPlaneData(ref p, ref n);
            var tf = new TargetFace {
                Plane = new PlaneInput(pg.Transform.Point(V3.From(p)), pg.Transform.Normal(V3.From(n)), "打孔面"),
                Face = face,
                Part = part,
                Placement = pg.Transform
            };
            try { tf.PartName = part.Name; } catch { tf.PartName = "零件"; }
            tf.Label = "平面 " + face.ID;
            return tf;
        }

        // 参考孔轴线与目标面的交点（装配坐标）。两零件不平行时给出明确原因。
        public static V3 Intersect(ReferenceHole hole, TargetFace face){
            double denom = hole.Axis.Dot(face.Plane.Normal);
            if (Math.Abs(denom) < 0.05)
                throw new ArgumentException("参考孔的轴线与打孔面几乎平行，无法定位孔心。请确认两个零件是平行装配的。");
            double t = (face.Plane.Point - hole.Center).Dot(face.Plane.Normal) / denom;
            return hole.Center + hole.Axis * t;
        }
    }

    // 在目标零件上实际打孔。
    public sealed class DrillResult {
        public int Requested, Created;
        public string Method = "";
        public string Audit = "";        // 打完孔回读到的异常（例如 CAD 偷偷换了孔型）
        public List<string> Failures = new List<string>();
        public bool Ok { get { return Created > 0 && Failures.Count == 0 && Audit.Length == 0; } }
    }

    public static class AutoHoleWriter {
        const double MmToM = 0.001;
        static object M = Type.Missing;

        // CAD 正忙的时候，COM 调用会被直接拒绝：
        //   RPC_E_CALL_REJECTED  (0x80010001) / CO_E_OBJNOTCONNECTED (0x800401FD) / RPC_E_SERVERCALL_RETRYLATER
        // 这类是**瞬态**的，等一下重试就好。实测"开始打孔"第一次经常撞上，
        // 不重试就会给用户报一个假的"打孔失败"。
        // 最近一次用到的 Application，重试前拿它泵一下消息（CAD 靠消息循环推进内部状态）。
        static F.Application LastApp;

        static bool Transient(Exception e){
            int hr = e.HResult;
            return hr == unchecked((int)0x80010001)   // RPC_E_CALL_REJECTED      调用被拒（CAD 忙）
                || hr == unchecked((int)0x800401FD)   // CO_E_OBJNOTCONNECTED     对象连接已断开
                || hr == unchecked((int)0x800401FB)   // CO_E_OBJNOTREG           对象刚建好还没注册
                || hr == unchecked((int)0x8001010A)   // RPC_E_SERVERCALL_RETRYLATER
                || hr == unchecked((int)0x80010108)   // RPC_E_DISCONNECTED
                || (hr == unchecked((int)0x80004005) && e.Message != null && e.Message.Contains("拒绝"));
        }
        // 重试要等，但界面不能看起来像死了：每等一小段就把进度报给界面（AutoHoleForm 把它
        // 写进状态栏并 DoEvents 一次）。总计仍然是最多约 2.4 秒，和原来同一量级。
        public static Action<string> Progress;
        static void Report(string s){ var p = Progress; if (p != null) { try { p(s); } catch { } } }

        const int RetryCount = 6;
        const int RetrySleepMs = 400;

        static T Attempt<T>(string what, Func<T> f){
            Exception last = null;
            for (int i = 0; i < RetryCount; i++) {
                try { return f(); }
                catch (Exception e) {
                    last = e;
                    if (!Transient(e)) throw;
                    Log.Write("AutoHoleRetry " + what + " #" + (i + 1), e);
                    Report("CAD 正忙（" + what + "），正在重试 " + (i + 1) + "/" + (RetryCount) + "…");
                    try { if (LastApp != null) LastApp.DoIdle(); } catch { }
                    System.Threading.Thread.Sleep(RetrySleepMs);
                }
            }
            throw last;
        }
        static void Attempt(string what, Action a){ Attempt<object>(what, () => { a(); return null; }); }

        // 天工CAD 的孔特征方向必须指向材料内部，否则 igFeatureFailed。
        // 面片的几何法向与实体外法向不一定一致（IsParamReversed），因此用"先试一侧、失败换另一侧"。
        static readonly P.FeaturePropertyConstants[] Sides = {
            P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight
        };

        public static DrillResult Drill(TargetFace target, HoleSpec spec, IEnumerable<V3> centresAssembly){
            if (target == null) throw new ArgumentNullException("target");
            if (spec == null) throw new ArgumentNullException("spec");
            var result = new DrillResult();
            var centres = new List<V3>();
            foreach (var c in centresAssembly) centres.Add(c);
            result.Requested = centres.Count;
            if (centres.Count == 0) return result;

            var part = target.Part;
            try { LastApp = part.Application; } catch { }
            // 装配里刚加载/刚激活的零件，COM 可能还没完全就绪（实测第一写操作为 CO_E_OBJNOTREG）。
            // 泵一次消息循环，成本几乎为零，能挡掉这类假失败。
            try { if (LastApp != null) { LastApp.DoIdle(); } } catch { }
            P.Model model = null;
            if (part.Models.Count >= 1) model = part.Models.Item(1);
            if (model == null) throw new InvalidOperationException("目标零件没有可用的实体模型。");

            P.HoleData data = BuildHoleData(part, spec, result);

            // 把装配坐标的孔心换算到零件局部坐标
            var local = new List<V3>();
            foreach (var c in centres){
                var lp = target.Placement.InversePoint(c);
                if (!lp.Finite) { result.Failures.Add("孔心换算失败"); continue; }
                local.Add(lp);
            }
            if (local.Count == 0) return result;

            // 基准面：先找已存在的共面基准面，找不到才新建。
            // 实测：同一张面上反复 AddParallelByDistance 会在第二次开始 E_FAIL（连不同批次的调用也算）。
            P.RefPlane plane = FindOrCreatePlane(part, target);
            if (plane == null) throw new InvalidOperationException("无法在所选面上建立打孔基准面。");

            double depth = spec.Depth;
            bool rebuilt = false;
            foreach (var lp in local){
                string why; P.Hole created;
                if (DrillOne(part, model, plane, lp, data, depth, out why, out created)) {
                    result.Created++;
                    if (result.Audit.Length == 0) result.Audit = Audit(created, spec);
                    continue;
                }
                // 连接断开（RPC_E_DISCONNECTED 之类）：多半是缓存的基准面/模型对象已经失效。
                // 丢掉这个零件的缓存、重建基准面、重新取模型，再试一次 —— 重试同一个死对象是没用的。
                if (!rebuilt && Disconnected(why)) {
                    rebuilt = true;
                    Info("打孔遇到断开的对象，重建基准面后重试");
                    DropCache(part);
                    try {
                        plane = FindOrCreatePlane(part, target);
                        if (plane != null && part.Models.Count >= 1) model = part.Models.Item(1);
                        if (plane != null && DrillOne(part, model, plane, lp, data, depth, out why, out created)) {
                            result.Created++;
                            if (result.Audit.Length == 0) result.Audit = Audit(created, spec);
                            continue;
                        }
                    } catch (Exception e) { why = "重建后仍失败：" + Friendly(e); }
                }
                result.Failures.Add(why);
            }
            return result;
        }

        // 打孔基准面：同一张面上要能反复打孔（用户打完一批再点同一个面接着打很常见）。
        //
        // 实测把这个函数逼出来的三条事实（tools/PlaneReuseSpike.cs）：
        // 1) AddParallelByDistance(face,0,…) 建出来的基准面 **Count 会加一，但 foreach 枚举不到**，
        //    所以"遍历 RefPlanes 找共面"永远找不到它 —— 必须自己缓存。
        // 2) 复用同一个基准面对象打多批孔**完全可行**（实测第二批成功）。
        // 3) 用"与目标面平行的已有基准面 + 偏移"新建也成功，而且**不碰面片对象**，
        //    即使缓存的面已经失效也能建。
        // 另外：共面判断只用 TargetFace.Plane（装配坐标的纯数据）换算到零件局部坐标，
        // 不依赖 face.Geometry —— 打完孔后面片会失效，GetPlaneData 直接抛。
        static readonly Dictionary<string, P.RefPlane> PlaneCache = new Dictionary<string, P.RefPlane>();
        static DateTime lastPrune = DateTime.MinValue;

        static P.RefPlane FindOrCreatePlane(P.PartDocument part, TargetFace target){
            if (target == null) throw new ArgumentNullException("target");
            V3 fpt = new V3(), fnv = new V3(0, 0, 1); bool havePlane = false;
            try {
                if (target.Plane != null) {
                    fpt = target.Placement.InversePoint(target.Plane.Point);
                    fnv = target.Placement.InverseNormal(target.Plane.Normal).Unit();
                    havePlane = fpt.Finite && fnv.Length > 0.5;
                }
            } catch { havePlane = false; }
            if (!havePlane) {
                try {   // 退路：新选的面片一定读得到
                    var pl = target.Face.Geometry as G.Plane;
                    if (pl != null) {
                        Array fp = new double[3], fn = new double[3];
                        pl.GetPlaneData(ref fp, ref fn);
                        fpt = V3.From(fp); fnv = V3.From(fn).Unit(); havePlane = true;
                    }
                } catch { }
            }
            if (!havePlane) throw new InvalidOperationException("读不到所选面的平面信息，请重新点一次这个面。");

            string key = PlaneKey(part, fpt, fnv);
            TrimCache();
            // 同一批打孔里不必反复清；隔一会儿查一次就够了
            if ((DateTime.UtcNow - lastPrune).TotalSeconds > 5) { lastPrune = DateTime.UtcNow; PruneCache(LastApp); }

            // ① 缓存命中（同一张面之前打过孔）
            //    必须验证对象还活着：零件被重新加载/装配状态变化后，缓存里的基准面会变成死对象，
            //    拿它去建轮廓就报 RPC_E_DISCONNECTED(0x80010108)，而且重试同一个死对象永远失败
            //    —— 用户就是这么撞上"打孔异常：被调用的对象已与其客户端断开连接"的。
            P.RefPlane cached;
            if (PlaneCache.TryGetValue(key, out cached)) {
                if (cached != null && PlaneUsable(cached)) return cached;
                PlaneCache.Remove(key);
            }

            // ② 已有的基准面里找共面的（用户自己建的基准面、或平行于默认基准面的情况）
            var existing = FindCoplanar(part, fpt, fnv);
            if (existing != null) { PlaneCache[key] = existing; return existing; }

            // ③ 找一个与目标面平行的已有基准面，用"基准面 + 偏移"新建（不碰面片，最稳）
            var parallel = FindParallel(part, fnv, fpt);
            if (parallel != null) {
                // 距离取绝对值 + 两侧都试，并且**建完校验法向**：
                // 实测下表面（相对 XY 基准面是负偏移）用 igNormalSide 建出来的基准面方向是反的，
                // 打孔会失败。方向不对就换另一侧再建。
                double mag = Math.Abs((fpt - parallel.Item1).Dot(fnv));
                var sides = new[]{ P.ReferenceElementConstants.igNormalSide, P.ReferenceElementConstants.igReverseNormalSide };
                foreach (var sd in sides) {
                    try {
                        var sdLocal = sd;
                        var np = Attempt("建基准面(平行)", () => part.RefPlanes.AddParallelByDistance(parallel.Item2, mag, sdLocal, M, M, M, M));
                        if (np == null) continue;
                        Array nn = new double[3];
                        np.GetNormal(ref nn);
                        var nnv = V3.From(nn).Unit();
                        if (nnv.Dot(fnv) > 0.99) { PlaneCache[key] = np; return np; }   // 方向对，收工
                    } catch (Exception e) { Log.Write("AutoHolePlaneByRef", e); }
                }
            }

            // ④ 兜底：直接拿面片当父平面（首次打孔走这条）
            try {
                var created = Attempt("建基准面(面)", () => part.RefPlanes.AddParallelByDistance(target.Face, 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M));
                if (created != null) { PlaneCache[key] = created; return created; }
            } catch (Exception e) {
                throw new InvalidOperationException("无法在所选面上建立打孔基准面。请重新点一次这个面（打完孔以后原来的面对象会失效）再打。原始错误：" + e.Message, e);
            }
            throw new InvalidOperationException("无法在所选面上建立打孔基准面。");
        }

        // 打孔失败信息里是不是"对象已断开"这类没法重试同一个对象的错误
        static bool Disconnected(string why){
            if (string.IsNullOrEmpty(why)) return false;
            return why.Contains("0x80010108") || why.Contains("断开") || why.Contains("0x800401FD")
                || why.Contains("0x800401FB") || why.Contains("RPC_E_DISCONNECTED") || why.Contains("CO_E_OBJNOT");
        }
        static void Info(string s){ Console.WriteLine("INFO " + s); }

        // 把 COM 错误翻译成用户看得懂的话（原始 HRESULT 照样保留在日志里）
        public static string Friendly(Exception e){
            if (e == null) return "";
            string m = e.Message;
            int hr = e.HResult;
            if (hr == unchecked((int)0x80010108) || m.Contains("0x80010108") || m.Contains("断开"))
                return "目标零件的数据连接已失效（零件可能被重新加载或被其它操作改动过）。请重新点一次打孔面，再点开始打孔。";
            if (hr == unchecked((int)0x800401FD) || hr == unchecked((int)0x800401FB))
                return "目标零件暂时不可用，请再点一次开始打孔。";
            if (hr == unchecked((int)0x80010001) || m.Contains("拒绝"))
                return "CAD 正忙，请稍后再点一次开始打孔。";
            return m;
        }

        // 基准面对象还活着吗？随便读一个轻量属性，死了会抛 COM 异常。
        static bool PlaneUsable(P.RefPlane plane){
            try { Array n = new double[3]; plane.GetNormal(ref n); return true; }
            catch { return false; }
        }
        // 这个零件上缓存过的基准面全部作废（零件被重新加载后调用）
        static void DropCache(P.PartDocument part){
            try { DropCacheByDocKey(DocKey(part)); } catch { }
        }

        // 文档被关闭时调用：清掉这个文档的缓存条目，别攥着已经失效的 COM 对象。
        // 文档名可能是 FullName，也可能只剩 Name（关掉以后 ReadOnly/FullName 读不到），
        // 所以两种键都清一遍。AutoHoleForm 订阅 CAD 的文档关闭事件来触发这里。
        public static void DropCache(P.PartDocument part, string nameHint){
            try { DropCacheByDocKey(DocKey(part)); } catch { }
            if (!string.IsNullOrEmpty(nameHint)) { try { DropCacheByDocKey(nameHint); } catch { } }
        }

        // 兜底：缓存条目数超过上限就整体清一次。正常流程由文档关闭事件清理，
        // 这里只防"某些关闭路径没触发事件"导致的长期增长。
        const int PlaneCacheLimit = 256;
        static void TrimCache(){
            try { if (PlaneCache.Count > PlaneCacheLimit) PlaneCache.Clear(); } catch { }
        }

        static void DropCacheByDocKey(string doc){
            if (string.IsNullOrEmpty(doc)) return;
            var dead = new List<string>();
            foreach (var kv in PlaneCache) {
                if (kv.Key.StartsWith(doc + "|", StringComparison.Ordinal)) dead.Add(kv.Key);
                else if (kv.Key.IndexOf("|" + doc + "|", StringComparison.Ordinal) >= 0) dead.Add(kv.Key);
            }
            foreach (var k in dead) PlaneCache.Remove(k);
        }

        public static int PlaneCacheCount { get { return PlaneCache.Count; } }

        // 缓存条目只在"同一张面接着打第二批孔"时才用得到，而它攥着 COM 对象；
        // 用户开开关关很多零件文件时字典会一直长。AutoHoleForm 在窗口关闭/开始打孔时
        // 调这里：把"文档已经不在 CAD 里"的条目清掉。
        public static void PruneCache(F.Application app){
            if (app == null) return;
            try {
                var open = new List<string>();
                int n = 0;
                try { n = app.Documents.Count; } catch { }
                for (int i = 1; i <= n; i++) {
                    P.PartDocument pd = null;
                    try { pd = app.Documents.Item(i) as P.PartDocument; } catch { }
                    if (pd == null) continue;
                    open.Add(DocKey(pd));
                    try { open.Add(pd.Name); } catch { }
                }
                if (open.Count == 0) return;
                var dead = new List<string>();
                var seen = new List<string>();
                foreach (var kv in PlaneCache) {
                    string doc = DocKeyOfCacheKey(kv.Key);
                    if (doc.Length == 0) continue;
                    if (!seen.Contains(doc)) {
                        seen.Add(doc);
                        if (!open.Contains(doc)) dead.Add(doc);
                    }
                }
                foreach (var doc in dead) DropCacheByDocKey(doc);
            } catch (Exception e) { Log.Write("AutoHolePruneCache", e); }
        }

        // 缓存键 = 文档 + "|" + 法向 + "|" + 偏移；文档名本身可能含 '|'，所以从右边切两段。
        static string DocKeyOfCacheKey(string key){
            if (string.IsNullOrEmpty(key)) return "";
            int i = key.LastIndexOf('|');
            if (i <= 0) return "";
            int j = key.LastIndexOf('|', i - 1);
            if (j <= 0) return "";
            return key.Substring(0, j);
        }
        static string DocKey(P.PartDocument part){
            string doc = null;
            try { doc = part.FullName; } catch { }
            if (string.IsNullOrEmpty(doc)) { try { doc = part.Name; } catch { doc = "?"; } }
            return doc;
        }

        // 缓存键：零件 + 平面（点取整到 0.1 微米，法向取整到 1e-6）
        static string PlaneKey(P.PartDocument part, V3 point, V3 normal){
            string doc = DocKey(part);
            var ci = CultureInfo.InvariantCulture;
            return doc + "|" + Math.Round(normal.X, 6).ToString(ci) + "," + Math.Round(normal.Y, 6).ToString(ci) + "," + Math.Round(normal.Z, 6).ToString(ci)
                 + "|" + Math.Round(point.Dot(normal), 7).ToString(ci);
        }

        // 枚举已有基准面。注意用 Count/Item 而不是 foreach —— 实测 foreach 看不到新建的那些。
        static IEnumerable<P.RefPlane> AllPlanes(P.PartDocument part){
            int n = 0;
            try { n = part.RefPlanes.Count; } catch { }
            for (int i = 1; i <= n; i++) {
                P.RefPlane rp = null;
                try { rp = part.RefPlanes.Item(i); } catch { }
                if (rp != null) yield return rp;
            }
        }

        static P.RefPlane FindCoplanar(P.PartDocument part, V3 point, V3 normal){
            foreach (var rp in AllPlanes(part)) {
                try {
                    Array rn = new double[3], rr = new double[3];
                    rp.GetNormal(ref rn); rp.GetRootPoint(ref rr);
                    var rnv = V3.From(rn).Unit();
                    if (Math.Abs(Math.Abs(rnv.Dot(normal)) - 1) > 1e-6) continue;         // 不平行
                    if (Math.Abs((V3.From(rr) - point).Dot(normal)) < 1e-6) return rp;      // 共面（0.001mm 容差）
                } catch { }
            }
            return null;
        }

        // 找一个与目标面平行的基准面，返回它的(根点, 对象)
        static Tuple<V3, P.RefPlane> FindParallel(P.PartDocument part, V3 normal, V3 point){
            foreach (var rp in AllPlanes(part)) {
                try {
                    Array rn = new double[3], rr = new double[3];
                    rp.GetNormal(ref rn); rp.GetRootPoint(ref rr);
                    var rnv = V3.From(rn).Unit();
                    if (Math.Abs(Math.Abs(rnv.Dot(normal)) - 1) > 1e-6) continue;
                    return Tuple.Create(V3.From(rr), rp);
                } catch { }
            }
            return null;
        }

        static bool DrillOne(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, P.HoleData data, out string why, out P.Hole created){
            return DrillOne(part, model, plane, localCentre, data, 0, out why, out created);
        }

        // depthMm > 0 时打有限深度的盲孔，否则贯通。
        static bool DrillOne(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, P.HoleData data, double depthMm, out string why, out P.Hole created){
            why = ""; created = null;
            P.Profile profile = null;
            try {
                profile = Attempt("建轮廓", () => part.ProfileSets.Add().Profiles.Add(plane));
                double x2 = 0, y2 = 0;
                Attempt("轮廓坐标", () => { profile.Convert3DCoordinate(localCentre.X, localCentre.Y, localCentre.Z, out x2, out y2); return 0; });
                Attempt("孔心", () => { profile.Holes2d.Add(x2, y2); return 0; });
                if (Attempt("闭合检查", () => profile.End(P.ProfileValidationType.igProfileClosed)) != 0) { why = "孔轮廓未通过闭合检查"; return false; }
                bool blind = depthMm > 1e-9;

                foreach (var side in Sides){
                    P.Hole h = null;
                    try {
                        var sideLocal = side;
                        h = Attempt("打孔", () => blind ? model.Holes.AddFinite(profile, sideLocal, depthMm * MmToM, data)
                                                        : model.Holes.AddThroughAll(profile, sideLocal, data));
                    } catch (Exception e) { why = "打孔失败：" + e.Message; continue; }
                    if (h == null) { why = "打孔失败：CAD 未返回孔特征"; continue; }
                    object desc = null;
                    var st = h.GetStatusEx(out desc);
                    if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) { created = h; return true; }
                    try { h.Delete(); } catch {}
                    why = "打孔失败，状态 " + st + (desc == null ? "" : "（" + desc + "）");
                }
                return false;
            } catch (Exception e) {
                why = Friendly(e);
                return false;
            } finally {
                if (profile != null) { try { profile.Visible = false; } catch {} }
            }
        }

        // 按规格造 HoleData —— 全部走「全参数显式」的 AddEx。
        //
        // 三条实测结论决定了这里的写法（tools/HoleDefaultProbe.cs 逐项二分出来的）：
        //
        // 1) 凡是留 Missing 的几何参数，CAD 会把"上次用过的孔参数"灌进来。
        //    实测灌进来的是 沉头 Φ13.71×0.8 / 90° / 深 50.8mm，于是螺纹孔的孔型被改写成
        //    igCounterdrillHole、孔口多一个没设过的锥面 —— 这就是"螺纹孔自动带锥面"的根因。
        //    IgnoreSavedDefaultValues=true 只对**显式传了值**的参数有效，所以沉孔、锥形沉孔、
        //    通孔也必须把"不需要就显式 0"写全，否则同样有被污染的风险（而且污染不一定改孔型，
        //    静默改尺寸的话用户根本看不出来）。
        // 2) 螺纹孔不能走 igTappedHole。实测 AddEx(igTappedHole,…) 一律被换成
        //    igCounterdrillHole + 沉头锥面（换 SubType/Fit/Standard 都没用）。
        //    正确做法是"普通孔 + 螺纹数据"：孔按螺纹内小径建模，螺纹以装饰螺纹显示。
        // 3) 有三个参数**不能**显式给值，留 Missing 才是对的（实测，给了就 E_INVALIDARG）：
        //      TaperMethod / Taper / TaperDimType —— 给 0 直接 AddEx 失败。
        //    留空时 CAD 灌进来的是 igTaperByRatio / 0.05 / igTaperDimAtBottom，那是占位值，
        //    对非锥孔没有任何几何影响（实测切除体积与理论值精确相等、锥面数 0）。
        //    螺纹孔路径里第 15 位 ThreadDepthMethod 同理（留 Missing 时 CAD 填的是 igNone）。
        // 4) 圆柱沉孔的 CounterboreProfileLocationType 显式给 igCounterboreProfileIsAtTop：
        //    剖面画在孔的起始端（= 用户点的那张面）那一侧。实测给 igCounterboreProfileIsAtBottom
        //    时沉孔会被切到材料内部、表面上看不到沉孔（切除体积只剩通孔那一段）。
        static P.HoleData BuildHoleData(P.PartDocument part, HoleSpec spec, DrillResult result){
            double dia = spec.HoleDiameter * MmToM;
            if (dia <= 0) throw new ArgumentException("孔径必须大于 0。");
            double bottom = spec.Bottom == HoleBottom.Flat ? 0.0 : spec.BottomAngle;
            var vd = spec.Bottom == HoleBottom.Flat
                ? P.FeaturePropertyConstants.igVBottomDimToFlat
                : P.FeaturePropertyConstants.igVBottomDimToV;
            var none = P.FeaturePropertyConstants.igNone;
            P.HoleData d;
            switch (spec.Kind) {
                case HoleKind.Counterbore: {
                    double cd = spec.CounterboreDiameter * MmToM, cdep = spec.CounterboreDepth * MmToM;
                    if (cd <= dia) throw new ArgumentException("沉孔直径必须大于通孔直径。");
                    if (cdep <= 0) throw new ArgumentException("沉孔深度必须大于 0。");
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igCounterboreHole,
                        "ISO Metric", M, M, M, dia,                               // 1-5
                        cd, cdep, 0.0, 0.0, bottom,                               // 6-10 沉孔尺寸 + 孔底角度
                        none, M, M, M, M, M, vd, M,                               // 11-18 处理方式 / 锥度 / 螺纹 / 孔底标注
                        P.FeaturePropertyConstants.igCounterboreProfileIsAtTop, M, M, M, M, true,   // 19-24
                        M, M, M, M, M, M, M, M, M, M, M, M);                      // 25-36 螺纹与倒角（不需要）
                    result.Method = "圆柱沉孔 Φ" + N(spec.CounterboreDiameter) + " 深" + N(spec.CounterboreDepth);
                    break;
                }
                case HoleKind.Countersink: {
                    double csd = spec.CountersinkDiameter * MmToM;
                    if (csd <= dia) throw new ArgumentException("锥形沉孔直径必须大于通孔直径。");
                    if (spec.CountersinkAngle <= 0 || spec.CountersinkAngle >= 180) throw new ArgumentException("锥角必须在 0～180° 之间。");
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igCountersinkHole,
                        "ISO Metric", M, M, M, dia,                               // 1-5
                        0.0, 0.0, csd, spec.CountersinkAngle, bottom,             // 6-10 锥孔尺寸 + 锥角
                        none, M, M, M, M, M, vd, M,                               // 11-18
                        M, M, M, M, M, true,                                      // 19-24
                        M, M, M, M, M, M, M, M, M, M, M, M);                      // 25-36
                    result.Method = "锥形沉孔 Φ" + N(spec.CountersinkDiameter) + " " + N(spec.CountersinkAngle) + "°";
                    break;
                }
                case HoleKind.Tapped: {
                    string size = spec.ThreadSize.Length > 0 ? spec.ThreadSize : "";
                    double nominal = dia;
                    double nominalMm = NominalMm(size);
                    if (nominalMm > 0) nominal = nominalMm * MmToM;
                    // 参数位次：AddEx(type, Standard, SubType, Size, Fit, HoleDiameter,
                    //   cbD, cbDep, csD, csA, BottomAngle, Treatment, TaperMethod, Taper,
                    //   ThreadMinorDiameter, ThreadDepthMethod, ThreadDepth, VBottomDimType, TaperDimType,
                    //   CbProfileLoc, TaperL, TaperR, ThreadExternalDiameter, ThreadDescription,
                    //   IgnoreSavedDefaults, ThreadDiameterOption, TapDrill, HeadClearance, 9×倒角)
                    // 第 22/23 位（公称直径与螺纹规格）显式给值，装饰螺纹才带得上 M6 这个标注。
                    // 第 14 位 ThreadMinorDiameter = 孔径：孔按螺纹内小径建模（与 CAD 自己的孔命令一致）。
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
                        "ISO Metric", M, size.Length > 0 ? (object)size : M, M, dia,   // 1-5 标准/尺寸/孔径
                        0.0, 0.0, 0.0, 0.0, bottom,                               // 6-10 沉孔锥孔显式 0
                        none, M, M, dia, M, M, vd, M,                             // 11-18 螺纹内径 = 孔径
                        M, M, M, M, M, true,                                      // 19-24
                        M, M, M, M, M, M, M, M, M, M, M, M);                      // 25-36
                    TrySet(() => d.ThreadExternalDiameter = nominal, "AutoHoleThreadDia");
                    if (size.Length > 0) TrySet(() => d.ThreadDescription = size, "AutoHoleThreadDesc");
                    result.Method = "螺纹孔 " + (size.Length > 0 ? size + "（Φ" + N(spec.HoleDiameter) + " 内径）" : "自定义 Φ" + N(spec.HoleDiameter));
                    break;
                }
                default:
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
                        "ISO Metric", M, M, M, dia,                               // 1-5
                        0.0, 0.0, 0.0, 0.0, bottom,                               // 6-10 空孔型参数显式 0
                        none, M, M, M, M, M, vd, M,                               // 11-18
                        M, M, M, M, M, true,                                      // 19-24
                        M, M, M, M, M, M, M, M, M, M, M, M);                      // 25-36
                    result.Method = "通孔 Φ" + N(spec.HoleDiameter);
                    break;
            }
            if (spec.Chamfer && spec.Kind != HoleKind.Countersink) {
                try { d.SetStartChamfer(1, spec.ChamferSetback * MmToM, spec.ChamferAngle); }
                catch (Exception e) { Log.Write("AutoHoleChamfer", e); }
            }
            return d;
        }

        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }
        static void TrySet(Action a, string area){ try { a(); } catch (Exception e) { Log.Write(area, e); } }

        // "M6" -> 6；"M6x0.75" -> 6。解析不出来返回 0。
        static double NominalMm(string size){
            if (string.IsNullOrEmpty(size)) return 0;
            var sb = new System.Text.StringBuilder();
            foreach (char c in size) {
                if (char.IsDigit(c) || c == '.') sb.Append(c);
                else if (sb.Length > 0) break;
            }
            double v;
            return double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // 打完孔回读一次特征，确认 CAD 真的按我们要的孔型建的。
        // 这条自检就是用来防"螺纹孔被换成沉头孔"这类静默改写的。
        //
        // 只回读**这条孔自己的特征值**，而且只报"该有却没有 / 明显不对"：
        // 回读出来的 HoleData 里 CounterboreDiameter / CountersinkDiameter 这类
        // "本孔型用不到"的字段，CAD 会顺手填上同义值（实测普通孔 cbd=0 但锥孔位会给
        // 20mm 的占位），那不是几何问题，按它报警只会制造噪音。几何对不对由测试里的
        // 切除体积 + 锥面数来钉死（tests/AutoHoleTests.cs）。
        const double AuditTolMm = 0.05;

        static string Audit(P.Hole hole, HoleSpec spec){
            try {
                var d = hole.HoleData as P.HoleData;
                if (d == null) return "";
                var msgs = new List<string>();
                string type = d.HoleType.ToString();
                string want;
                switch (spec.Kind) {
                    case HoleKind.Counterbore: want = "igCounterboreHole"; break;
                    case HoleKind.Countersink: want = "igCountersinkHole"; break;
                    default: want = "igRegularHole"; break;
                }
                if (type != want) msgs.Add("CAD 实际生成的是 " + type + "（要求 " + want + "）");

                double gotDia = Read(d, x => x.HoleDiameter) * 1000.0;
                if (Off(gotDia, spec.HoleDiameter, AuditTolMm))
                    msgs.Add("孔径回读 Φ" + N(gotDia) + "（要求 Φ" + N(spec.HoleDiameter) + "）");

                if (spec.Kind == HoleKind.Counterbore) {
                    double cbd = Read(d, x => x.CounterboreDiameter) * 1000.0;
                    double cdep = Read(d, x => x.CounterboreDepth) * 1000.0;
                    if (Missing(cbd)) msgs.Add("沉孔直径回读为 0（要求 Φ" + N(spec.CounterboreDiameter) + "）");
                    else if (Off(cbd, spec.CounterboreDiameter, AuditTolMm))
                        msgs.Add("沉孔直径回读 Φ" + N(cbd) + "（要求 Φ" + N(spec.CounterboreDiameter) + "）");
                    if (Missing(cdep)) msgs.Add("沉孔深度回读为 0（要求深 " + N(spec.CounterboreDepth) + "）");
                    else if (Off(cdep, spec.CounterboreDepth, AuditTolMm))
                        msgs.Add("沉孔深度回读 " + N(cdep) + "（要求 " + N(spec.CounterboreDepth) + "）");
                }
                if (spec.Kind == HoleKind.Countersink) {
                    double csd = Read(d, x => x.CountersinkDiameter) * 1000.0;
                    double csa = Read(d, x => x.CountersinkAngle);
                    if (Missing(csd)) msgs.Add("锥孔直径回读为 0（要求 Φ" + N(spec.CountersinkDiameter) + "）");
                    else if (Off(csd, spec.CountersinkDiameter, AuditTolMm))
                        msgs.Add("锥孔直径回读 Φ" + N(csd) + "（要求 Φ" + N(spec.CountersinkDiameter) + "）");
                    if (Missing(csa)) msgs.Add("锥角回读为 0（要求 " + N(spec.CountersinkAngle) + "°）");
                    else if (Off(csa, spec.CountersinkAngle, 1.0))
                        msgs.Add("锥角回读 " + N(csa) + "°（要求 " + N(spec.CountersinkAngle) + "°）");
                }
                // 孔底角度：只有显式要了 V 型底才比对，平底回读 0 是正常的
                if (spec.Bottom == HoleBottom.VBottom) {
                    double ba = Read(d, x => x.BottomAngle);
                    if (Off(ba, spec.BottomAngle, 1.0))
                        msgs.Add("孔底角度回读 " + N(ba) + "°（要求 " + N(spec.BottomAngle) + "°）");
                }
                if (spec.Kind == HoleKind.Tapped) {
                    string td = SafeStr(() => d.ThreadDescription);
                    if (string.IsNullOrEmpty(td) || td.Trim().Length == 0)
                        msgs.Add("CAD 未回读螺纹规格" + (spec.ThreadSize.Length > 0 ? "（要求 " + spec.ThreadSize + "）" : ""));
                    else if (spec.ThreadSize.Length > 0 && td.IndexOf(spec.ThreadSize, StringComparison.OrdinalIgnoreCase) < 0)
                        msgs.Add("螺纹规格回读 " + td + "（要求 " + spec.ThreadSize + "）");
                }
                return msgs.Count == 0 ? "" : "注意：" + string.Join("；", msgs.ToArray());
            } catch { return ""; }
        }

        static double Read(P.HoleData d, Func<P.HoleData, double> get){
            try { return get(d); } catch { return double.NaN; }
        }
        static string SafeStr(Func<string> f){ try { return f(); } catch { return ""; } }
        // 读不到（COM 抛异常）时返回 NaN：与其报一个假的"回读 0"，不如跳过这一项
        static bool Off(double got, double want, double tol){
            if (double.IsNaN(got)) return false;
            return Math.Abs(got - want) > tol;
        }
        static bool Missing(double got){ return !double.IsNaN(got) && got <= 0.001; }

        // 一个待打的孔：规格 + 装配坐标孔心。
        public sealed class HoleRequest {
            public HoleSpec Spec;
            public V3 Centre;
            public string Source = "";   // 来自哪个参考孔，出错时能说清
        }

        // 一批不同规格的孔一次打完。基准面只建一次，每个规格各自建 HoleData。
        public static DrillResult DrillRequests(TargetFace target, IList<HoleRequest> requests){
            var result = new DrillResult();
            if (requests == null || requests.Count == 0) return result;
            result.Requested = requests.Count;
            var part = target.Part;
            P.Model model = part.Models.Count >= 1 ? part.Models.Item(1) : null;
            if (model == null) throw new InvalidOperationException("目标零件没有可用的实体模型。");
            P.RefPlane plane = FindOrCreatePlane(part, target);
            if (plane == null) throw new InvalidOperationException("无法在所选面上建立打孔基准面。");
            var methods = new List<string>();
            foreach (var r in requests) {
                var local = target.Placement.InversePoint(r.Centre);
                if (!local.Finite) { result.Failures.Add(r.Source + " 孔心换算失败"); continue; }
                var one = new DrillResult();
                P.HoleData data;
                try { data = BuildHoleData(part, r.Spec, one); }
                catch (Exception e) { result.Failures.Add(r.Source + " 规格无效：" + e.Message); continue; }
                if (one.Method.Length > 0 && !methods.Contains(one.Method)) methods.Add(one.Method);
                string why; P.Hole created;
                if (DrillOne(part, model, plane, local, data, r.Spec == null ? 0 : r.Spec.Depth, out why, out created)) {
                    result.Created++;
                    string note = Audit(created, r.Spec);
                    if (note.Length > 0 && !result.Audit.Contains(note)) result.Audit = result.Audit.Length == 0 ? note : result.Audit + "；" + note;
                } else result.Failures.Add((r.Source.Length > 0 ? r.Source + "：" : "") + why);
            }
            result.Method = methods.Count > 0 ? string.Join("／", methods.ToArray()) : "—";
            return result;
        }

        // 腰孔（长圆孔）：Holes2d 只能做圆孔，所以走 ExtrudedCutouts 通切。
        // lengthMm 是总长，widthMm 是槽宽（= 两端半圆的直径）。圆心距 = lengthMm - widthMm。
        public static bool DrillSlotOne(TargetFace target, V3 localCentre, V3 localDirInPlane, double lengthMm, double widthMm, out string why){
            why = "";
            if (lengthMm <= widthMm) { why = "腰孔总长必须大于槽宽。"; return false; }
            if (widthMm <= 0) { why = "槽宽必须大于 0。"; return false; }
            var part = target.Part;
            P.Model model = part.Models.Count >= 1 ? part.Models.Item(1) : null;
            if (model == null) { why = "目标零件没有可用实体。"; return false; }
            P.Profile profile = null;
            try {
                var plane = FindOrCreatePlane(part, target);
                if (plane == null) { why = "无法在所选面上建立基准面。"; return false; }
                profile = part.ProfileSets.Add().Profiles.Add(plane);
                return SlotProfile(part, model, profile, localCentre, localDirInPlane, lengthMm, widthMm, out why);
            } catch (Exception e) {
                why = "腰孔异常：" + e.Message;
                return false;
            } finally {
                if (profile != null) { try { profile.Visible = false; } catch {} }
            }
        }

        // 腰孔轮廓 + 通切。profile 由调用方建好，基准面只建一次。
        static bool SlotProfile(P.PartDocument part, P.Model model, P.Profile profile, V3 localCentre, V3 localDirInPlane,
                                double lengthMm, double widthMm, out string why){
            why = "";
            try {
                double cx, cy, dx1, dy1;
                profile.Convert3DCoordinate(localCentre.X, localCentre.Y, localCentre.Z, out cx, out cy);
                profile.Convert3DCoordinate(localCentre.X + localDirInPlane.X, localCentre.Y + localDirInPlane.Y, localCentre.Z + localDirInPlane.Z, out dx1, out dy1);
                double ux = dx1 - cx, uy = dy1 - cy;
                double ul = Math.Sqrt(ux*ux + uy*uy);
                if (ul < 1e-12) { why = "腰孔方向在轮廓平面内退化为 0。"; return false; }
                ux /= ul; uy /= ul;
                double half = (lengthMm - widthMm) / 2000.0;   // 圆心到端心的距离（米）
                double r = widthMm / 2000.0;                    // 半圆半径（米）
                double px1 = cx - ux*half, py1 = cy - uy*half;
                double px2 = cx + ux*half, py2 = cy + uy*half;
                // 垂直于槽向的法向
                double nx = -uy, ny = ux;
                // 必须用 AddByStartAlongEnd 并显式给出"弧上一点"，否则圆弧会朝内鼓，
                // 把腰孔变成内凹的狗骨形（实测面积 = 2rL - πr²，正好差一个圆）。
                var l1 = profile.Lines2d.AddBy2Points(px1 + nx*r, py1 + ny*r, px2 + nx*r, py2 + ny*r);
                var a1 = profile.Arcs2d.AddByStartAlongEnd(px2 + nx*r, py2 + ny*r, px2 + ux*r, py2 + uy*r, px2 - nx*r, py2 - ny*r);
                var l2 = profile.Lines2d.AddBy2Points(px2 - nx*r, py2 - ny*r, px1 - nx*r, py1 - ny*r);
                var a2 = profile.Arcs2d.AddByStartAlongEnd(px1 - nx*r, py1 - ny*r, px1 - ux*r, py1 - uy*r, px1 + nx*r, py1 + ny*r);
                var rel = (S.Relations2d)profile.Relations2d;
                int ks = (int)SolidEdgeConstants.KeypointIndexConstants.igLineStart;
                int ke = (int)SolidEdgeConstants.KeypointIndexConstants.igLineEnd;
                int as_ = (int)SolidEdgeConstants.KeypointIndexConstants.igArcStart;
                int ae = (int)SolidEdgeConstants.KeypointIndexConstants.igArcEnd;
                rel.AddKeypoint(l1, ke, a1, as_);
                rel.AddKeypoint(a1, ae, l2, ks);
                rel.AddKeypoint(l2, ke, a2, as_);
                rel.AddKeypoint(a2, ae, l1, ks);
                if (profile.End(P.ProfileValidationType.igProfileClosed) != 0) { why = "腰孔轮廓未通过闭合检查。"; return false; }
                foreach (var side in Sides) {
                    P.ExtrudedCutout cut = null;
                    try { cut = model.ExtrudedCutouts.AddThroughAll(profile, P.FeaturePropertyConstants.igRight, side); }
                    catch (Exception e) { why = "腰孔失败：" + e.Message; continue; }
                    if (cut == null) { why = "腰孔失败：CAD 未返回切除特征"; continue; }
                    object desc = null;
                    if ((int)cut.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return true;
                    try { cut.Delete(); } catch {}
                    why = "腰孔失败，状态 " + desc;
                }
                return false;
            } catch (Exception e) {
                why = "腰孔异常：" + e.Message;
                return false;
            }
        }

        // 一次排多个腰孔：基准面只建一次（和圆孔同样的原因）。
        public static DrillResult DrillSlots(TargetFace target, IList<V3> localCentres, IList<V3> localDirs, double lengthMm, double widthMm){
            var result = new DrillResult { Requested = localCentres == null ? 0 : localCentres.Count, Method = "腰孔（通切）" };
            if (localCentres == null || localCentres.Count == 0) return result;
            var part = target.Part;
            P.Model model = part.Models.Count >= 1 ? part.Models.Item(1) : null;
            if (model == null) throw new InvalidOperationException("目标零件没有可用的实体模型。");
            var plane = FindOrCreatePlane(part, target);
            if (plane == null) throw new InvalidOperationException("无法在所选面上建立基准面。");
            for (int i = 0; i < localCentres.Count; i++) {
                string why;
                var dir = i < localDirs.Count ? localDirs[i] : new V3(1,0,0);
                if (DrillSlotOnPlane(part, model, plane, localCentres[i], dir, lengthMm, widthMm, out why)) result.Created++;
                else result.Failures.Add(why);
            }
            return result;
        }

        static bool DrillSlotOnPlane(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, V3 localDirInPlane,
                                     double lengthMm, double widthMm, out string why){
            why = "";
            if (lengthMm <= widthMm) { why = "腰孔总长必须大于槽宽。"; return false; }
            if (widthMm <= 0) { why = "槽宽必须大于 0。"; return false; }
            P.Profile profile = null;
            try {
                profile = part.ProfileSets.Add().Profiles.Add(plane);
                return SlotProfile(part, model, profile, localCentre, localDirInPlane, lengthMm, widthMm, out why);
            } catch (Exception e) { why = "腰孔异常：" + e.Message; return false; }
            finally { if (profile != null) { try { profile.Visible = false; } catch {} } }
        }

        // 全装配孔采集：遍历每个零件实例的所有圆形边，换算到装配坐标。
        // 用于配孔检查。返回的记录带 Selection（可高亮）。
        public static List<HoleRecord> CollectAssemblyHoles(A.AssemblyDocument assembly, out List<string> warnings){
            warnings = new List<string>();
            var result = new List<HoleRecord>();
            if (assembly == null) return result;
            var byBucket = new Dictionary<string, List<HoleRecord>>(StringComparer.Ordinal);
            foreach (A.Occurrence occ in assembly.Occurrences) {
                P.PartDocument part = null;
                try { part = occ.OccurrenceDocument as P.PartDocument; } catch { }
                if (part == null) continue;
                if (part.Models.Count < 1) continue;
                G.Body body = null;
                try { body = (G.Body)((P.Model)part.Models.Item(1)).Body; } catch (Exception e) { warnings.Add(occ.Name + " 读取实体失败：" + e.Message); continue; }
                if (body == null) continue;
                string partName;
                try { partName = occ.Name; } catch { partName = "零件"; }
                object rawEdges;
                try { rawEdges = body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll); }
                catch (Exception e) { warnings.Add(partName + " 读取边失败：" + e.Message); continue; }
                foreach (G.Edge edge in (G.Edges)rawEdges) {
                    G.Circle circle = null;
                    try { circle = edge.Geometry as G.Circle; } catch { }
                    if (circle == null) continue;
                    Array c = new double[3], ax = new double[3]; double r = 0;
                    try { circle.GetCircleData(ref c, ref ax, out r); } catch { continue; }
                    if (r <= 0) continue;
                    object sel = null; V3 centre = new V3(), axis = new V3(0,0,1);
                    try {
                        sel = assembly.CreateReference(occ, edge);
                        var pg = PickGeometry.Unwrap(sel);
                        centre = pg.Transform.Point(V3.From(c));
                        axis = pg.Transform.Vector(V3.From(ax)).Unit();
                    } catch { continue; }
                    var rec = new HoleRecord { Center = centre, Axis = axis, DiameterMm = r*2000.0,
                                               PartName = partName, Selection = sel };
                    // 通孔有上下两条圆边，同一个孔只保留一条记录。
                    // 去重原来是对全表线性扫描（O(E²)，E = 全装配圆边数）；SameHoleLine 要求
                    // "同一零件 + 直径相差 ≤0.01mm"，所以先按「零件名 + 直径取整到 0.1mm」分桶，
                    // 只跟同桶里的记录比，绝大多数无关比较直接跳过。
                    string bucket = partName + "|" + Math.Round(rec.DiameterMm, 1).ToString(CultureInfo.InvariantCulture);
                    List<HoleRecord> same;
                    if (!byBucket.TryGetValue(bucket, out same)) { same = new List<HoleRecord>(); byBucket[bucket] = same; }
                    bool dup = false;
                    foreach (var existing in same) if (HoleCheck.SameHoleLine(existing, rec)) { dup = true; break; }
                    if (!dup) { same.Add(rec); result.Add(rec); }
                }
            }
            return result;
        }

        // 轴线是否穿过某个零件的材料（用于判"漏打孔"）。
        //
        // 这个函数会被调用 S×P 次（S = 待判的孤孔数，P = 装配零件数）。原来的写法每次都要
        // 重新遍历一遍 assembly.Occurrences 去按名字找零件 —— 每次 occ.Name 都是一次跨进程
        // COM 调用，于是整体变成 O(S×P²) 次 COM 调用，大装配上要跑分钟级。
        // 改成：按名字一次性建好「零件名 -> 实体」的表，后面查表 O(1)。
        public sealed class OccurrenceBodies {
            readonly Dictionary<string, G.Body> byName = new Dictionary<string, G.Body>(StringComparer.Ordinal);
            public static OccurrenceBodies Build(A.AssemblyDocument assembly, List<string> names){
                var map = new OccurrenceBodies();
                if (names != null) foreach (var n in names) if (n != null && !map.byName.ContainsKey(n)) map.byName[n] = null;
                if (assembly == null) return map;
                foreach (A.Occurrence occ in assembly.Occurrences) {
                    string name; try { name = occ.Name; } catch { continue; }
                    if (map.byName.ContainsKey(name)) continue;      // 只认第一次出现的同名实例
                    P.PartDocument part = null;
                    try { part = occ.OccurrenceDocument as P.PartDocument; } catch { }
                    G.Body body = null;
                    if (part != null) { try { if (part.Models.Count >= 1) body = (G.Body)((P.Model)part.Models.Item(1)).Body; } catch { } }
                    map.byName[name] = body;
                }
                return map;
            }
            public G.Body BodyOf(string partName){
                G.Body b;
                return (partName != null && byName.TryGetValue(partName, out b)) ? b : null;
            }
            public List<string> Names { get { return new List<string>(byName.Keys); } }
        }

        public static bool RayPassesThrough(OccurrenceBodies parts, HoleRecord hole, string partName){
            if (parts == null || hole == null) return false;
            var body = parts.BodyOf(partName);
            if (body == null) return false;
            // 沿轴线双向各打一条射线；命中面即认为穿过了该零件的材料。
            foreach (double sign in new double[]{ 1.0, -1.0 }) {
                try {
                    var faces = body.get_FacesByRay(hole.Center.X, hole.Center.Y, hole.Center.Z,
                                                    hole.Axis.X*sign, hole.Axis.Y*sign, hole.Axis.Z*sign);
                    var coll = faces as System.Collections.IEnumerable;
                    if (coll != null) foreach (object o in coll) if (o != null) return true;
                } catch { }
            }
            return false;
        }

        // 兼容旧签名（没有预建表时现建一次，慢但不至于错）
        public static bool RayPassesThrough(A.AssemblyDocument assembly, HoleRecord hole, string partName){
            return RayPassesThrough(OccurrenceBodies.Build(assembly, null), hole, partName);
        }
    }
}
