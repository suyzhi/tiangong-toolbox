using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static partial class AutoHoleWriter {
        const double MmToM = 0.001;
        static object M = Type.Missing;

        // 只有"CAD 真的在忙"才值得等一下再试：
        //   RPC_E_CALL_REJECTED (0x80010001) / RPC_E_SERVERCALL_RETRYLATER (0x8001010A)
        //
        // 2026-10-20 查日志（panel.log 两份，共 994 条 AutoHoleRetry）：**一条都不是**真正的"忙"。
        // 全部是这一个模式 ——
        //   #1  Holes.AddFinite 0x80010108「系统无法找到指定的资源」  ← CAD 建孔失败（这一侧方向不对、孔底在空气里…）
        //   #2～#6  0x80010108 / 0x800401FB / 0x800401FD「对象已断开 / 没有注册」 ← 失败时 CAD 已经把轮廓吃掉了，重试的是个死对象
        // 原来把断开/未注册也当"忙"去重试：每个孔每一侧白等 6 次，状态栏一直报"CAD 正忙，正在重试"，
        // 而另一侧拿的也是同一个死轮廓 —— 本来换一侧就能打成的孔也跟着失败。
        // 现在：断开/未注册一律不重试，交给 DrillOne 换新轮廓、换一侧再建（见 DrillOne）。
        static F.Application LastApp;
        internal static void TrackApp(P.PartDocument part){
            if (part == null) return;
            try { var a = part.Application; if (a != null) LastApp = a; } catch { }
        }

        static bool Busy(Exception e){
            int hr = e.HResult;
            return hr == unchecked((int)0x80010001)   // RPC_E_CALL_REJECTED      调用被拒（CAD 忙）
                || hr == unchecked((int)0x8001010A);  // RPC_E_SERVERCALL_RETRYLATER
        }
        // 重试要等，但界面不能看起来像死了：每等一小段就把进度报给界面（AutoHoleForm 把它
        // 写进状态栏并 DoEvents 一次）。
        public static Action<string> Progress;
        static void Report(string s){ var p = Progress; if (p != null) { try { p(s); } catch { } } }

        const int RetryCount = 20;
        const int RetrySleepMs = 250;

        internal static T Attempt<T>(string what, Func<T> f){
            Exception last = null;
            for (int i = 0; i < RetryCount; i++) {
                try { return f(); }
                catch (Exception e) {
                    last = e;
                    if (!Busy(e)) throw;
                    Log.Write("AutoHoleRetry " + what + " #" + (i + 1), e);
                    if (i >= 2) Report("CAD 正忙（" + what + "），稍等…");
                    try { if (LastApp != null) LastApp.DoIdle(); } catch { }
                    System.Threading.Thread.Sleep(RetrySleepMs);
                }
            }
            throw last;
        }
        internal static void Attempt(string what, Action a){ Attempt<object>(what, () => { a(); return null; }); }

        // 天工CAD 的孔特征方向必须指向材料内部，否则 igFeatureFailed。
        // 面片的几何法向与实体外法向不一定一致（IsParamReversed），因此用"先试一侧、失败换另一侧"。
        static readonly P.FeaturePropertyConstants[] Sides = {
            P.FeaturePropertyConstants.igLeft, P.FeaturePropertyConstants.igRight
        };

        public static DrillResult Drill(TargetFace target, HoleSpec spec, IEnumerable<V3> centresAssembly){
            TianGongCadSuite.Licensing.LicenseGate.Require();
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
            P.Model model = ModelFor(target);
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
                    result.Undo.Hole(part, created);
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
                        if (plane != null) model = ModelFor(target) ?? model;
                        if (plane != null && DrillOne(part, model, plane, lp, data, depth, out why, out created)) {
                            result.Created++;
                            result.Undo.Hole(part, created);
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
        // 每个"零件 + 面"记住一次问出来的外法向：面片失效后（读完一批孔很常见）还得靠它判断朝向。
        static readonly Dictionary<string, V3> OutwardCache = new Dictionary<string, V3>();
        static DateTime lastPrune = DateTime.MinValue;

        internal static P.RefPlane FindOrCreatePlane(P.PartDocument part, TargetFace target){
            if (target == null) throw new ArgumentNullException("target");
            TrackApp(part);
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
            // 多实体零件：两个实体贴在一起时共面的两张面外法向相反，各记各的
            if (target.Body > 1) key += "|体" + target.Body;
            TrimCache();
            // 同一批打孔里不必反复清；隔一会儿查一次就够了
            if ((DateTime.UtcNow - lastPrune).TotalSeconds > 5) { lastPrune = DateTime.UtcNow; PruneCache(LastApp); }

            // 打孔基准面的两个硬条件（缺任何一个，孔都建不出来，而 CAD 只回一个含糊的 COM 错误）：
            //   1) 位置：必须落在所选面那张平面上。实测偏 40mm 时盲孔整段切在空气里，
            //      贯通孔却"看起来正常"（圆柱轴线不变，穿到哪儿算哪儿）——
            //      用户报的"盲孔老报错、贯通就可以"就是这么来的。
            //   2) 朝向：法向必须**背对材料**（= 面片的外法向）。实测同一张面、同一个孔心：
            //      基准面法向朝外 → 贯通/盲孔都成功；法向扎进材料 → 两个方向都失败。
            // 面片的几何法向不一定朝外（IsParamReversed=True 时正好相反）——
            // 这张托盘铝板的上表面就是反的，两条都能踩上。
            //
            // 外法向必须拿准：打完第一批孔以后，用户点的那张面片对象会**失效**
            // （实测第二次读 IsParamReversed 直接抛 E_FAIL；旧的 try/catch 会把 outward 退回
            // 几何法向 —— 对反向面正好是**反的答案**，于是"位置对、朝向反"的基准面被当成合格的用上，
            // 第二次打孔就失败）。所以按三级取：
            //   1) 问目标面片；2) 面片失效时按平面数据在实体现有面里找回同一张面再问；
            //   3) 还不行就用这个位置的**已知答案**（第一次成功时记下来的）。
            bool reversed = false, knowReversed = false;
            G.Face planeFace = target.Face;
            V3 faceGeoNormal = fnv;
            knowReversed = ParamReversed(planeFace, out reversed);
            if (knowReversed) {
                // 外法向必须用同一张面片自己的几何法向配它的 IsParamReversed，避免旧 Plane 法向与新面片参数化反号
                try {
                    var pl = planeFace.Geometry as G.Plane;
                    if (pl != null) { Array fp = new double[3], fn = new double[3]; pl.GetPlaneData(ref fp, ref fn); faceGeoNormal = V3.From(fn).Unit(); }
                } catch { }
            } else {
                V3 freshNormal;
                var fresh = FindModelFace(ModelFor(target), fpt, fnv, target.FaceId, out freshNormal);
                if (fresh != null) { planeFace = fresh; faceGeoNormal = freshNormal; knowReversed = ParamReversed(fresh, out reversed); }
            }
            V3 outward = fnv;
            if (knowReversed) {
                outward = reversed ? faceGeoNormal * -1.0 : faceGeoNormal;
                OutwardCache[key] = outward;
            } else {
                V3 learned;
                if (OutwardCache.TryGetValue(key, out learned)) outward = learned;
            }
            Func<P.RefPlane, bool> usable = knowReversed
                ? (Func<P.RefPlane, bool>)(p => p != null && PlaneUsable(p) && PlaneOn(p, fpt, fnv) && PlaneFacesOut(p, outward))
                // 朝向问不出来时只能按位置收（宁可让 CAD 判，也别因为自己瞎猜把好面丢掉）
                : (Func<P.RefPlane, bool>)(p => p != null && PlaneUsable(p) && PlaneOn(p, fpt, fnv));

            // ① 缓存命中（同一张面之前打过孔）
            //    必须验证对象还活着：零件被重新加载/装配状态变化后，缓存里的基准面会变成死对象，
            //    拿它去建轮廓就报 RPC_E_DISCONNECTED(0x80010108)，而且重试同一个死对象永远失败
            //    —— 用户就是这么撞上"打孔异常：被调用的对象已与其客户端断开连接"的。
            //    还必须是"位置对 + 朝外"的那张：缓存里可能留着上一版逻辑建偏/建反的那张。
            P.RefPlane cached;
            if (PlaneCache.TryGetValue(key, out cached)) {
                if (usable(cached)) return cached;
                PlaneCache.Remove(key);
            }

            // ② 已有的基准面里找共面的（用户自己建的基准面、或平行于默认基准面的情况）
            //    要看**所有**共面的，不能只看第一张：0.7.13 现场 02-23 的打孔面正好在零件基准面 x=0 上，
            //    基准面法向扎进材料（不能用）；上一次打孔建的那张（朝外、能用）排在后面。只看第一张就会去 ③ 再建一张，
            //    而面上已经有一张重合的基准面时 ③ 建出来的是死对象（读不了位置、删不掉）——打一次、撤销、再打就一直失败。
            var existing = FindCoplanar(part, fpt, fnv, usable);
            if (existing != null) { PlaneCache[key] = existing; return existing; }

            // ③ 直接拿面片建（距离给 0）：位置天然精确，法向跟着面片的外法向走 —— 首选。
            //    实测面片建出来的这张：位置偏差 0mm、法向与面片外法向点积 = 1，不论
            //    igNormalSide/igReverseNormalSide/FlipNormal 怎么给都一样（都朝外）。
            try {
                var created = Attempt("建基准面(面)", () => part.RefPlanes.AddParallelByDistance(planeFace, 0.0, P.ReferenceElementConstants.igNormalSide, M, M, M, M));
                // 这张面：位置是精确的，法向是 CAD 按面片朝向自己给的 —— 位置对就收；
                // 万一我们自己估的外法向和它不一致，只记一笔（别因为估计错了丢掉唯一能用的面）。
                if (created != null && PlaneUsable(created) && PlaneOn(created, fpt, fnv)) {
                    if (!PlaneFacesOut(created, outward))
                        Info("注意：面片建的基准面法向与估计的外法向相反（点积 "
                             + PlaneDot(created, outward).ToString("0.###") + "），以 CAD 的为准");
                    PlaneCache[key] = created;
                    return created;
                }
                if (created != null) {
                    Info("用面片建的基准面不合格（位置偏 " + (PlaneGap(created, fpt) * 1000.0).ToString("0.###")
                       + "mm，法向·外法向 = " + PlaneDot(created, outward).ToString("0.###") + "），弃用");
                    TryDeletePlane(created);
                }
            } catch (Exception e) { Log.Write("AutoHolePlaneByFace", e); }

            // ④ 兜底：遍历所有与目标面平行且不共面的已有基准面 + 偏移新建（优先法向朝外的参照面，不碰面片）。
            //
            // 偏移有两个方向，而且实测**两边建出来的法向可能一模一样**（都继承参照面的法向），
            // 所以方向不能靠"比法向"猜，只能按位置算、按位置验收：
            //   参照面根点 r0、法向 n（|n·fnv|≈1）；两个候选面在 r0 ± mag·n；
            //   要的是轴向坐标 = fpt·fnv 的那一张 → 偏移走 +n 还是 -n 由 (fpt-r0)·n 的符号定。
            // 实测约定：igNormalSide = 沿参照面法向 +n 偏，igReverseNormalSide = 沿 -n 偏。
            // 位置对、但朝向反的（参照面本身朝材料里，本零件就是）一律不用 —— 尝试下一个平行基准面。
            var parallels = AllParallel(part, fnv, fpt, outward);
            foreach (var parallel in parallels) {
                double mag = Math.Abs((fpt - parallel.Item1).Dot(fnv));
                if (mag <= PlaneOnTolM) continue;
                bool forward = (fpt - parallel.Item1).Dot(parallel.Item3) > 0;
                var first  = forward ? P.ReferenceElementConstants.igNormalSide : P.ReferenceElementConstants.igReverseNormalSide;
                var second = forward ? P.ReferenceElementConstants.igReverseNormalSide : P.ReferenceElementConstants.igNormalSide;
                foreach (var sd in new[]{ first, second }) {
                    try {
                        var sdLocal = sd;
                        var refPlane = parallel.Item2;
                        var np = Attempt("建基准面(平行)", () => part.RefPlanes.AddParallelByDistance(refPlane, mag, sdLocal, M, M, M, M));
                        if (np == null) continue;
                        if (!usable(np)) {   // 位置或朝向不对：不是我们要的那张，删掉，别留在零件里
                            Info("平行基准面建的候选不合格（位置偏 " + (PlaneGap(np, fpt) * 1000.0).ToString("0.###")
                               + "mm，法向·外法向 = " + PlaneDot(np, outward).ToString("0.###") + "），弃用");
                            TryDeletePlane(np);
                            continue;
                        }
                        PlaneCache[key] = np;
                        return np;
                    } catch (Exception e) { Log.Write("AutoHolePlaneByRef", e); }
                }
            }
            Info("建不出基准面：零件 " + SafeName(part) + " 实体 " + target.Body + "/" + PartBodies.Count(part)
                 + "，面(局部) " + fpt + " 法向 " + fnv + "，外法向 " + outward + (knowReversed ? "" : "（问不出，猜的）")
                 + "，可用平行基准面 " + parallels.Count + " 张，基准面共 " + SafeCount(part) + " 张（与面重合 "
                 + CountCoplanar(part, fpt, fnv) + " 张）");
            throw new InvalidOperationException("无法在所选面上建立打孔基准面（位置/朝向都不对）。请重新点一次这个面再打。");
        }

        // "对象已断开 / 没注册 / 没连上"：CAD 不肯在这个编辑环境里改这个零件（0.7.11 现场：带链接的零件在装配里直接打，每个孔两侧都是 0x80010108）
        internal static bool DisconnectHr(int hr){
            return hr == unchecked((int)0x80010108) || hr == unchecked((int)0x800401FB) || hr == unchecked((int)0x800401FD);
        }

        // 打孔失败信息里是不是"对象已断开"这类没法重试同一个对象的错误
        static bool Disconnected(string why){
            if (string.IsNullOrEmpty(why)) return false;
            return why.Contains("0x80010108") || why.Contains("断开") || why.Contains("0x800401FD")
                || why.Contains("0x800401FB") || why.Contains("RPC_E_DISCONNECTED") || why.Contains("CO_E_OBJNOT");
        }
        // 也写进 panel.log：插件在天工里面跑时控制台谁也看不到（0.7.13 现场"位置/朝向都不对"就是因为这几行没留下来，查不出是哪条没过）
        internal static void Info(string s){ Console.WriteLine("INFO " + s); Log.Write("AutoHoleInfo", s); }
        static string SafeName(P.PartDocument part){ try { return part.Name; } catch { return "?"; } }
        static int SafeCount(P.PartDocument part){ try { return part.RefPlanes.Count; } catch { return -1; } }

        // 点选时的错误：对象断开 = 点到的东西刚被天工重新加载过（不是"建孔被拒"），其余同 Friendly
        public static string FriendlyPick(Exception e){
            if (e != null && DisconnectHr(e.HResult)) return "点到的对象已经失效（零件刚被天工重新加载过，或者刚进出过原位编辑），请再点一次。";
            return Friendly(e);
        }
        // 把 COM 错误翻译成用户看得懂的话（原始 HRESULT 照样保留在日志里）
        public static string Friendly(Exception e){
            if (e == null) return "";
            string m = e.Message;
            int hr = e.HResult;
            // 0.7.13：这两个错误码只说明"CAD 拒绝建这个孔"，不说明为什么。原来一律说"这个位置已经有一个孔了"，
            // 现场多实体零件（PartBodies）每个孔都这么报、其实一个孔都没有 —— 真有孔的情况 DrillOne 之前的预检已经挡掉、单独报了。
            if (hr == unchecked((int)0x800401FB) || hr == unchecked((int)0x80010108) || m.Contains("0x80010108") || m.Contains("断开"))
                return "CAD 拒绝在孔心上建孔特征。常见原因：孔心不在所选面的材料上、孔比面还宽、和旁边的孔重叠，"
                     + "或者零件刚被关掉/重新加载过。请检查该位置后重新点一次「开始打孔」。";
            if (hr == unchecked((int)0x800401FD))
                return "目标零件暂时不可用，请再点一次开始打孔。";
            if (hr == unchecked((int)0x80010001) || hr == unchecked((int)0x8001010A) || m.Contains("拒绝"))
                return "CAD 正忙，请稍后再点一次。";
            // 其它 COM 错误（0.8.5）：原来原样显示「…（异常来自 HRESULT:0x8000FFFF）」，用户看不懂也不知道该干什么。
            // 去掉 HRESULT 那一截、说一句怎么办；原始错误照样在 panel.log
            if (e is System.Runtime.InteropServices.COMException) {
                string brief = System.Text.RegularExpressions.Regex.Replace(m, @"\s*[（(]异常来自 HRESULT[^)）]*[)）]", "").Trim();
                return "天工没能完成这一步（" + (brief.Length > 0 ? brief : "0x" + hr.ToString("X8")) + "）。请再试一次；还不行就重新点一下要用的面或孔。";
            }
            return m;
        }


        // 该位置是不是"已经有一个孔了"？——同一轴线上重复打孔会被 CAD 拒绝，
        // 而且它只报一个含糊的 COM 错误（CO_E_OBJNOTREG / RPC_E_DISCONNECTED）。
        // 这里用"圆边圆心到孔心的径向距离"判断：径向距离≈0 就认为这个位置已经有孔。
        // 只问一个孔位时用；一批孔位先 ScanSpots 一次再在内存里比（见 HoleSpots）
        internal static bool ExistingHoleAt(P.RefPlane plane,P.Model model,V3 localCentre,out double diameterMm){
            return ScanSpots(model).At(localCentre,PlaneNormal(plane),out diameterMm);
        }

        static long scanMs, addMs;   // 计时（AutoHoleTiming 日志）：读实体圆边、CAD 建孔
        static int lastSideHr;       // DrillOne / DrillOnProfile 最近一次建孔抛出的 HRESULT（0 = 没抛）
        // 实体上全部圆边读一遍（零件局部坐标）。读失败的边跳过；整个读不出来时返回空表（= 当作没有孔，交给 CAD 判）。
        // 多实体零件只读目标面所在的那个实体：叠在一起的另一块板上同轴的孔不算"这里已经有孔"
        internal static HoleSpots ScanSpots(P.Model model){
            var spots=new HoleSpots();
            var sw=System.Diagnostics.Stopwatch.StartNew();
            try{
                if(model==null)return spots;
                var body=(G.Body)model.Body;
                foreach(G.Edge edge in (G.Edges)body.get_Edges(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
                    var circle=edge.Geometry as G.Circle;
                    if(circle==null)continue;
                    Array c=new double[3],ax=new double[3];double r=0;
                    try{circle.GetCircleData(ref c,ref ax,out r);}catch{continue;}
                    spots.Add(V3.From(c),V3.From(ax),r);
                }
            }catch(Exception e){Log.Write("ExistingHoleAt",e);}
            finally{scanMs+=sw.ElapsedMilliseconds;}
            return spots;
        }
        internal static V3 PlaneNormal(P.RefPlane plane){
            try{Array n=new double[3];plane.GetNormal(ref n);return V3.From(n).Unit();}catch{return new V3(0,0,1);}
        }

        // 基准面对象还活着吗？随便读一个轻量属性，死了会抛 COM 异常。
        internal static bool PlaneUsable(P.RefPlane plane){
            if (plane == null) return false;
            try { Array n = new double[3]; plane.GetNormal(ref n); return true; }
            catch { return false; }
        }
        // 这个零件上缓存过的基准面全部作废（零件被重新加载后调用）
        internal static void DropCache(P.PartDocument part){
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
            try { if (OutwardCache.Count > PlaneCacheLimit) OutwardCache.Clear(); } catch { }
        }

        static void DropCacheByDocKey(string doc){
            if (string.IsNullOrEmpty(doc)) return;
            var dead = new List<string>();
            foreach (var kv in PlaneCache) {
                if (kv.Key.StartsWith(doc + "|", StringComparison.Ordinal)) dead.Add(kv.Key);
                else if (kv.Key.IndexOf("|" + doc + "|", StringComparison.Ordinal) >= 0) dead.Add(kv.Key);
            }
            foreach (var k in dead) PlaneCache.Remove(k);
            dead.Clear();
            foreach (var kv in OutwardCache) {
                if (kv.Key.StartsWith(doc + "|", StringComparison.Ordinal)) dead.Add(kv.Key);
                else if (kv.Key.IndexOf("|" + doc + "|", StringComparison.Ordinal) >= 0) dead.Add(kv.Key);
            }
            foreach (var k in dead) OutwardCache.Remove(k);
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

        // 缓存键 = 文档 + "|" + 法向 + "|" + 偏移 [+ "|体N"]；文档名本身可能含 '|'，先剥掉多实体尾缀再从右边切两段。
        internal static string DocKeyOfCacheKey(string key){
            if (string.IsNullOrEmpty(key)) return "";
            int end = key.Length;
            int last = key.LastIndexOf('|');
            if (last > 0 && last + 2 <= end && key[last + 1] == '体') end = last;
            int i = key.LastIndexOf('|', end - 1);
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

        static int CountCoplanar(P.PartDocument part, V3 point, V3 normal){
            int n = 0;
            FindCoplanar(part, point, normal, rp => { n++; return false; });
            return n;
        }

        static P.RefPlane FindCoplanar(P.PartDocument part, V3 point, V3 normal, Func<P.RefPlane, bool> accept){
            foreach (var rp in AllPlanes(part)) {
                try {
                    Array rn = new double[3], rr = new double[3];
                    rp.GetNormal(ref rn); rp.GetRootPoint(ref rr);
                    var rnv = V3.From(rn).Unit();
                    if (Math.Abs(Math.Abs(rnv.Dot(normal)) - 1) > 1e-6) continue;         // 不平行
                    if (Math.Abs((V3.From(rr) - point).Dot(normal)) < 1e-6 && accept(rp)) return rp;   // 共面（0.001mm 容差）且能用
                } catch { }
            }
            return null;
        }

        // 找所有与目标面平行且不共面的已有基准面，法向与 outward 一致的排在前面（偏移新建会继承参照面法向）。
        static List<Tuple<V3, P.RefPlane, V3>> AllParallel(P.PartDocument part, V3 normal, V3 point, V3 outward){
            var same = new List<Tuple<V3, P.RefPlane, V3>>();
            var other = new List<Tuple<V3, P.RefPlane, V3>>();
            foreach (var rp in AllPlanes(part)) {
                try {
                    Array rn = new double[3], rr = new double[3];
                    rp.GetNormal(ref rn); rp.GetRootPoint(ref rr);
                    var rnv = V3.From(rn).Unit();
                    if (Math.Abs(Math.Abs(rnv.Dot(normal)) - 1) > 1e-6) continue;
                    var root = V3.From(rr);
                    if (Math.Abs((point - root).Dot(normal)) <= PlaneOnTolM) continue;
                    var item = Tuple.Create(root, rp, rnv);
                    if (rnv.Dot(outward) > 0.99) same.Add(item);
                    else other.Add(item);
                } catch { }
            }
            same.AddRange(other);
            return same;
        }

        const double PlaneOnTolM = 1e-6;   // 0.001mm，和 FindCoplanar 的共面容差同一个量级

        // 基准面是不是**真的落在所选面那张平面上**？
        //
        // 只验"对象还活着"和"法向对不对"都不够 —— 实测（用户托盘铝板）：
        //   上表面在零件局部 y=+20mm，法向因为面片参数反向读出来是 (0,-1,0)；
        //   与它平行的零件基面在 y=0、法向也是 (0,-1,0)。
        //   用"基面 + 偏移 20mm"新建，igNormalSide 和 igReverseNormalSide 建出来的面
        //   **法向完全一样**，位置却一张在 y=-20mm（离面 40mm，整块在材料外）、一张在 y=+20mm。
        //   旧代码只比法向，于是选中了 y=-20mm 那张。
        // 后果正是用户报的现象：贯通孔照样能切（圆柱轴线不变，穿到哪儿算哪儿），
        // 盲孔却整段切在空气里 → CAD 抛 RPC_E_DISCONNECTED / CO_E_OBJNOTREG，
        // 提示还是"这个位置已经有一个孔了"，牛头不对马嘴。
        static bool PlaneOn(P.RefPlane plane, V3 facePoint, V3 faceNormal){
            try {
                Array rr = new double[3], rn = new double[3];
                plane.GetRootPoint(ref rr); plane.GetNormal(ref rn);
                var rnv = V3.From(rn).Unit();
                if (Math.Abs(Math.Abs(rnv.Dot(faceNormal)) - 1) > 1e-6) return false;      // 不平行
                return Math.Abs((V3.From(rr) - facePoint).Dot(faceNormal)) <= PlaneOnTolM;  // 共面
            } catch { return false; }
        }

        // 面片参数是不是反的（反了说明几何法向扎进材料）。读不到（面片已失效）返回 false。
        static bool ParamReversed(G.Face face, out bool reversed){
            reversed = false;
            if (face == null) return false;
            try { reversed = face.IsParamReversed; return true; } catch { return false; }
        }

        // 按平面数据（点 + 法向，零件局部）在实体现有面里找回同一张平面面，同时返回该面片自身的几何法向。
        // 优先匹配相同 FaceId 和包围盒包含 point 的面，避免 U 型件/双翼缘上共面多面片找错张。
        static G.Face FindModelFace(P.Model model, V3 point, V3 normal, string faceId, out V3 faceNormal){
            faceNormal = normal;
            try {
                if (model == null) return null;
                var body = (G.Body)model.Body;
                G.Face best = null; double bestD = PlaneOnTolM; int bestRank = -1;
                foreach (G.Face f in (G.Faces)body.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                    var pl = f.Geometry as G.Plane;
                    if (pl == null) continue;
                    Array p = new double[3], n = new double[3];
                    try { pl.GetPlaneData(ref p, ref n); } catch { continue; }
                    var nv = V3.From(n).Unit();
                    if (Math.Abs(Math.Abs(nv.Dot(normal)) - 1) > 1e-6) continue;      // 不平行
                    double d = Math.Abs((V3.From(p) - point).Dot(normal));            // 共面程度
                    if (d > PlaneOnTolM) continue;
                    int rank = 0;
                    if (InFaceBox(f, point)) rank += 2;
                    if (!string.IsNullOrEmpty(faceId)) {
                        try { if (f.ID.ToString(CultureInfo.InvariantCulture) == faceId) rank += 4; } catch { }
                    }
                    if (rank > bestRank || (rank == bestRank && d < bestD)) {
                        bestRank = rank; bestD = d; best = f; faceNormal = nv;
                    }
                }
                return best;
            } catch (Exception e) { Log.Write("AutoHoleFindModelFace", e); return null; }
        }
        internal static bool InFaceBox(G.Face f, V3 point){
            try {
                Array lo = new double[3], hi = new double[3];
                f.GetRange(ref lo, ref hi);
                V3 a = V3.From(lo), b = V3.From(hi);
                const double tol = 1e-5;
                return point.X >= a.X - tol && point.X <= b.X + tol
                    && point.Y >= a.Y - tol && point.Y <= b.Y + tol
                    && point.Z >= a.Z - tol && point.Z <= b.Z + tol;
            } catch { return false; }
        }

        // 基准面的法向是不是背对材料（= 面片的外法向）。
        // 实测：法向扎进材料时，AddFinite/AddThroughAll 两个方向都建不出孔来。
        static bool PlaneFacesOut(P.RefPlane plane, V3 outward){
            return PlaneDot(plane, outward) > 0.99;
        }
        static double PlaneDot(P.RefPlane plane, V3 direction){
            try {
                Array rn = new double[3];
                plane.GetNormal(ref rn);
                return V3.From(rn).Unit().Dot(direction);
            } catch { return 0; }
        }

        // 孔心到基准面的距离（米）。建出来的面不在孔心上时，这个值会明显大于 0。
        static double PlaneGap(P.RefPlane plane, V3 point){
            try {
                Array rr = new double[3], rn = new double[3];
                plane.GetRootPoint(ref rr); plane.GetNormal(ref rn);
                var rnv = V3.From(rn).Unit();
                return Math.Abs((V3.From(rr) - point).Dot(rnv));
            } catch { return 0; }
        }

        // 只删我们自己刚建出来、验收没通过的那张基准面；用户自己建的、缓存里的面绝不碰。
        static void TryDeletePlane(P.RefPlane plane){
            if (plane == null) return;
            try { plane.Delete(); } catch (Exception e) { Log.Write("AutoHolePlaneDelete", e); }
        }
    }
}
