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
        static bool DrillOne(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, P.HoleData data, out string why, out P.Hole created){
            return DrillOne(part, model, plane, localCentre, data, 0, out why, out created);
        }

        // depthMm > 0 时打有限深度的盲孔，否则贯通。
        internal static bool DrillOne(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, P.HoleData data, double depthMm, out string why, out P.Hole created){
            bool linked;
            return DrillOne(part, model, plane, localCentre, data, depthMm, null, out why, out created, out linked);
        }

        // anchor 非空：孔心挂到零件间复制来的圆边上（天工原生关联，见 HoleNativeLink）。
        // 挂不上不算打孔失败 —— 孔照打，linked=false，调用方退回属性集关联。
        internal static bool DrillOne(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, P.HoleData data, double depthMm,
                                      NativeAnchor anchor, out string why, out P.Hole created, out bool linked){
            return DrillOne(part, model, plane, localCentre, data, depthMm, anchor, true, out why, out created, out linked);
        }
        // checkExisting=false：调用方已经按 HoleSpots 查过这个孔位（DrillRequests），不再读一遍实体
        internal static bool DrillOne(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, P.HoleData data, double depthMm,
                                      NativeAnchor anchor, bool checkExisting, out string why, out P.Hole created, out bool linked){
            why = ""; created = null; linked = false; lastSideHr = 0;
            bool anchorBroken = false;
            // 先把"这个位置已经有孔"挡在前头，别让 CAD 报一个含糊的 COM 错误。
            double existingDiameter;
            if (checkExisting && ExistingHoleAt(plane, model, localCentre, out existingDiameter)) {
                why = "这个位置已经有孔了（Φ" + existingDiameter.ToString("0.##") + "），已跳过 —— 同一根轴线上重复打孔 CAD 会拒绝。";
                return false;
            }
            // 基准面必须落在孔心上（= 落在所选面上）。偏出去时：贯通孔照样能切（轴线不变），
            // 盲孔会整段切在空气里 → CAD 只回一个含糊的 COM 错误。先把这件事说出来，省得又去猜。
            double planeGapMm = PlaneGap(plane, localCentre) * 1000.0;
            if (planeGapMm > 0.001) Info("注意：打孔基准面偏离孔心 " + planeGapMm.ToString("0.###") + "mm");
            bool blind = depthMm > 1e-9;
            // 哪一侧能打成跟基准面走：同一张面上第一个孔试出来以后，后面的孔直接先试那一侧，
            // 不用每个孔都先撞一次错的那一侧（撞错一侧 CAD 要花 1 秒多才回错误）。
            var order = SideOrder(plane);
            try {
                foreach (var side in order){
                    // 每一侧都用**新的**轮廓：建孔失败时 CAD 会把传进去的轮廓一起作废，
                    // 拿同一个轮廓去试另一侧只会得到"对象已断开"（现场日志里成百条的那个）。
                    P.Profile profile = null; object profileSet = null; P.Hole h = null;
                    try {
                        profile = Attempt("建轮廓", () => { var ps = part.ProfileSets.Add(); profileSet = ps; return ps.Profiles.Add(plane); });
                        double x2 = 0, y2 = 0;
                        Attempt("轮廓坐标", () => { profile.Convert3DCoordinate(localCentre.X, localCentre.Y, localCentre.Z, out x2, out y2); return 0; });
                        var prof00 = profile;
                        P.Hole2d h2d = Attempt("孔心", () => prof00.Holes2d.Add(x2, y2));
                        bool attached = false;
                        if (anchor != null && !anchorBroken) {
                            string awhy;
                            attached = NativeHoleLink.Attach(profile, h2d, anchor, out awhy);
                            if (!attached) {
                                anchorBroken = true; anchor.Why = awhy; Log.Write("NativeAttach", anchor.Label + "：" + awhy);
                                // 挂不上关联不能连累打孔：换一个干净的轮廓（Attach 已经清过，但轮廓里可能还留着别的东西）
                                try { ((dynamic)profileSet).Delete(); } catch { }
                                profile = Attempt("建轮廓", () => { var ps = part.ProfileSets.Add(); profileSet = ps; return ps.Profiles.Add(plane); });
                                var prof1 = profile;
                                Attempt("轮廓坐标", () => { prof1.Convert3DCoordinate(localCentre.X, localCentre.Y, localCentre.Z, out x2, out y2); return 0; });
                                Attempt("孔心", () => prof1.Holes2d.Add(x2, y2));
                            }
                        }
                        var prof0 = profile;
                        if (Attempt("闭合检查", () => prof0.End(P.ProfileValidationType.igProfileClosed)) != 0) { why = "孔轮廓未通过闭合检查"; return false; }
                        var sideLocal = side; var prof = profile;
                        var addClock = System.Diagnostics.Stopwatch.StartNew();
                        try {
                        h = Attempt("打孔", () => AddHole(part, model, prof, sideLocal, depthMm, data));
                        } finally { addMs += addClock.ElapsedMilliseconds; }
                        if (h == null) { why = "打孔失败：CAD 未返回孔特征"; continue; }
                        object desc = null;
                        var st = h.GetStatusEx(out desc);
                        if ((int)st == (int)P.FeatureStatusConstants.igFeatureOK) {
                            created = h;
                            linked = attached;
                            Learned(plane, side);
                            return true;
                        }
                        try { h.Delete(); } catch {}
                        h = null;
                        why = "打孔失败，状态 " + st + (desc == null ? "" : "（" + desc + "）");
                    } catch (Exception e) {
                        why = "打孔失败：" + Friendly(e);
                        lastSideHr = e.HResult;
                        Log.Write("AutoHoleSide " + side, e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message);
                    } finally {
                        if (profile != null) { try { profile.Visible = false; } catch {} }
                        // 没建成孔：把这次建的空轮廓删掉。留着它会夹在前后两个孔特征之间，
                        // 特征组只收连续的一段 —— 这批孔就编不成组了（现场"编组失败"的来源之一）。
                        if (h == null && created == null) DropProfile(profile, profileSet);
                    }
                }
                if (why.Length == 0) why = "打孔失败";
                if (blind) {
                    // 盲孔的失败原因和贯通不一样，直接写清楚，别让用户猜（也别一律赖"已经有孔了"）
                    why += "（盲孔深 " + N(depthMm) + "mm：请确认深度小于料厚、孔底还在材料里";
                    if (planeGapMm > 0.001) why += "；当前打孔基准面偏离孔心 " + planeGapMm.ToString("0.###") + "mm";
                    why += "）";
                }
                return false;
            } catch (Exception e) {
                why = Friendly(e);
                lastSideHr = e.HResult;
                return false;
            }
        }

        // 基准面 → 上次打成的那一侧。键用基准面的根点 + 法向（对象引用会失效，数据不会）。
        static readonly Dictionary<string, P.FeaturePropertyConstants> GoodSide = new Dictionary<string, P.FeaturePropertyConstants>();
        // 任何一张面上最近一次打成的那一侧：新建的基准面都朝外（FindOrCreatePlane），所以换一张新面时它多半也对，
        // 先试它，省掉新面上第一个孔撞错侧的那一下。
        static P.FeaturePropertyConstants? lastGoodSide;

        // 试打的顺序：这张面上打成过的那一侧 → 别的面上最近打成的那一侧 → 默认顺序
        internal static List<P.FeaturePropertyConstants> SideOrder(P.RefPlane plane){
            var order = new List<P.FeaturePropertyConstants>(Sides);
            string key = SideKey(plane);
            P.FeaturePropertyConstants known;
            if (key.Length > 0 && GoodSide.TryGetValue(key, out known)) { order.Remove(known); order.Insert(0, known); }
            else if (lastGoodSide.HasValue) { order.Remove(lastGoodSide.Value); order.Insert(0, lastGoodSide.Value); }
            return order;
        }
        internal static void Learned(P.RefPlane plane, P.FeaturePropertyConstants side){
            string key = SideKey(plane);
            if (key.Length > 0) GoodSide[key] = side;
            lastGoodSide = side;
        }

        // 在已经建好的轮廓上按指定的一侧打一个孔（多孔合一的种子孔，见 HoleCombine）。只能试一次 —— 建孔失败时 CAD 把轮廓一起作废。
        internal static P.Hole DrillOnProfile(P.PartDocument part, P.Model model, P.Profile prof, P.HoleData data, double depthMm, P.FeaturePropertyConstants side){
            bool deleted;
            return DrillOnProfile(part, model, prof, data, depthMm, side, out deleted);
        }
        // deleted=true 表示建出了失败特征并执行过 Delete（实测删光基准面上唯一的孔时 CAD 可能连基准面一起作废，调用方需重建基准面）
        internal static P.Hole DrillOnProfile(P.PartDocument part, P.Model model, P.Profile prof, P.HoleData data, double depthMm, P.FeaturePropertyConstants side, out bool deleted){
            deleted = false;
            P.Hole h = null;
            var addClock = System.Diagnostics.Stopwatch.StartNew();
            try {
                h = Attempt("打种子孔", () => AddHole(part, model, prof, side, depthMm, data));
            } catch (Exception e) {
                lastSideHr = e.HResult;
                Log.Write("AutoHoleSide " + side, e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message);
                return null;
            } finally { addMs += addClock.ElapsedMilliseconds; }
            if (h == null) return null;
            object desc = null;
            if ((int)h.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) return h;
            deleted = true;
            try { h.Delete(); } catch (Exception e) { Log.Write("AutoHoleSeedDelete", e); }
            return null;
        }
        // 建一个孔特征。单实体零件走 AddThroughAll / AddFinite（老路）；
        // 多实体零件这两个对哪个实体都打不上，要走 AddMultiBody、显式给目标实体的 Body（见 PartBodies）。
        static P.Hole AddHole(P.PartDocument part, P.Model model, P.Profile prof, P.FeaturePropertyConstants side, double depthMm, P.HoleData data){
            bool blind = depthMm > 1e-9;
            if (!PartBodies.Multi(part))
                return blind ? model.Holes.AddFinite(prof, side, depthMm * MmToM, data) : model.Holes.AddThroughAll(prof, side, data);
            Array bodies = new object[]{ model.Body };
            var keyPoint = P.KeyPointExtentConstants.igTangentNormal;
            return model.Holes.AddMultiBody(prof, side, blind ? P.FeaturePropertyConstants.igFinite : P.FeaturePropertyConstants.igThroughAll,
                                            blind ? depthMm * MmToM : 0.0, M, ref keyPoint, M, M, data, 1, ref bodies);
        }

        // 目标面所在实体的 Model（单实体零件就是第 1 个）。还没认过的现认一次、记进 target.Body
        internal static P.Model ModelFor(TargetFace target){
            if (target == null || target.Part == null) return null;
            if (target.Body < 1) {
                V3 p = new V3(), n = new V3(0, 0, 1);
                try { p = target.Placement.InversePoint(target.Plane.Point); n = target.Placement.InverseNormal(target.Plane.Normal).Unit(); } catch { }
                target.Body = PartBodies.IndexOf(target.Part, target.Face, p, n);
            }
            return PartBodies.Model(target.Part, target.Body);
        }

        static string SideKey(P.RefPlane plane){
            try {
                Array rr = new double[3], rn = new double[3];
                plane.GetRootPoint(ref rr); plane.GetNormal(ref rn);
                string doc = ""; try { doc = ((P.PartDocument)plane.Parent).FullName; } catch { }
                var ci = CultureInfo.InvariantCulture;
                if (GoodSide.Count > 500) GoodSide.Clear();
                return doc + "|" + V3.From(rr).X.ToString("0.######", ci) + "," + V3.From(rr).Y.ToString("0.######", ci) + "," + V3.From(rr).Z.ToString("0.######", ci)
                     + "|" + V3.From(rn).X.ToString("0.####", ci) + "," + V3.From(rn).Y.ToString("0.####", ci) + "," + V3.From(rn).Z.ToString("0.####", ci);
            } catch { return ""; }
        }

        static void DropProfile(P.Profile profile, object profileSet){
            if (profile != null) { try { profile.Delete(); } catch { } }
            if (profileSet != null) { try { ((dynamic)profileSet).Delete(); } catch { } }
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
        internal static P.HoleData BuildHoleData(P.PartDocument part, HoleSpec spec, DrillResult result){
            double dia = spec.HoleDiameter * MmToM;
            if (double.IsNaN(dia) || double.IsInfinity(dia) || !(dia > 0)) throw new ArgumentException("孔径必须大于 0。");
            bool vBottom = !spec.Through && spec.Bottom == HoleBottom.VBottom;
            if (vBottom && !(spec.BottomAngle > 0 && spec.BottomAngle < 180)) throw new ArgumentException("V 型孔底角度必须在 0～180° 之间。");
            double bottom = vBottom ? spec.BottomAngle : 0.0;
            var vd = vBottom
                ? P.FeaturePropertyConstants.igVBottomDimToV
                : P.FeaturePropertyConstants.igVBottomDimToFlat;
            var none = P.FeaturePropertyConstants.igNone;
            // 标准 / 子类型 / 大小 / 配合：尺寸正好是天工孔标准表（GB Metric）里某一行的，就把那一行写进去，
            // 天工自带的孔命令打开时才认得出是标准孔；原来写 "ISO Metric" + 子类型/大小/配合全空，
            // 打开后大小一栏显示 *（用户反馈）。对不上表的孔（自定义尺寸）照旧，那种孔显示 * 是对的。
            // 真机回读确认（tools/hole-feature-probe/HoleStdProbe.cs）：这四个字段原样存进文件、尺寸不被改动。
            var row = HoleStandards.Resolve(spec);
            object std = row != null ? (object)row.Standard : "ISO Metric";
            object sub = row != null ? (object)row.SubType : M;
            object fit = row != null && row.Fit.Length > 0 ? (object)row.Fit : M;
            object stdSize = row != null ? (object)row.Size : M;
            if (row != null) result.Standard = row.Label;
            P.HoleData d;
            switch (spec.Kind) {
                case HoleKind.Counterbore: {
                    double cd = spec.CounterboreDiameter * MmToM, cdep = spec.CounterboreDepth * MmToM;
                    if (double.IsNaN(cd) || double.IsInfinity(cd) || !(cd > dia)) throw new ArgumentException("沉孔直径必须大于通孔直径。");
                    if (double.IsNaN(cdep) || double.IsInfinity(cdep) || !(cdep > 0)) throw new ArgumentException("沉孔深度必须大于 0。");
                    if (!spec.Through && spec.CounterboreDepth >= spec.Depth - 1e-9) throw new ArgumentException("沉孔深度必须小于盲孔深度。");
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igCounterboreHole,
                        std, sub, stdSize, fit, dia,                              // 1-5 标准/子类型/大小/配合/孔径
                        cd, cdep, 0.0, 0.0, bottom,                               // 6-10 沉孔尺寸 + 孔底角度
                        none, M, M, M, M, M, vd, M,                               // 11-18 处理方式 / 锥度 / 螺纹 / 孔底标注
                        P.FeaturePropertyConstants.igCounterboreProfileIsAtTop, M, M, M, M, true,   // 19-24
                        M, M, M, M, M, M, M, M, M, M, M, M);                      // 25-36 螺纹与倒角（不需要）
                    result.Method = "圆柱沉孔 Φ" + N(spec.CounterboreDiameter) + " 深" + N(spec.CounterboreDepth);
                    break;
                }
                case HoleKind.Countersink: {
                    double csd = spec.CountersinkDiameter * MmToM;
                    if (double.IsNaN(csd) || double.IsInfinity(csd) || !(csd > dia)) throw new ArgumentException("锥形沉孔直径必须大于通孔直径。");
                    if (!(spec.CountersinkAngle > 0 && spec.CountersinkAngle < 180)) throw new ArgumentException("锥角必须在 0～180° 之间。");
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igCountersinkHole,
                        std, sub, stdSize, fit, dia,                              // 1-5 标准/子类型/大小/配合/孔径
                        0.0, 0.0, csd, spec.CountersinkAngle, bottom,             // 6-10 锥孔尺寸 + 锥角
                        none, M, M, M, M, M, vd, M,                               // 11-18
                        M, M, M, M, M, true,                                      // 19-24
                        M, M, M, M, M, M, M, M, M, M, M, M);                      // 25-36
                    result.Method = "锥形沉孔 Φ" + N(spec.CountersinkDiameter) + " " + N(spec.CountersinkAngle) + "°";
                    break;
                }
                case HoleKind.Tapped: {
                    string problem = spec.ThreadProblem;
                    if (problem.Length > 0) throw new ArgumentException(problem);
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
                    // 第 11 位处理方式显式给 igTappedHole（实测孔型仍是 igRegularHole，而且公称直径/螺距
                    // 都能回读出来；留 igNone 时 CAD 也会自己换成 igTappedHole，但公称直径、螺距回读为 0）。
                    // 第 15/16 位螺纹深度**必须显式给**：留空时 CAD 填的是 igFinite + 2mm（真机实测），
                    // 装饰螺纹只在孔口一小圈 —— 这就是"螺纹深度只有一点点、还没法调"。
                    //   全长 → igThroughAll：贯通孔整孔攻丝、盲孔攻到孔底（真机线框截图确认）
                    //   指定 → igFinite + 深度
                    object tdm, tdep;
                    if (spec.ThreadFull) { tdm = P.FeaturePropertyConstants.igThroughAll; tdep = M; }
                    else { tdm = P.FeaturePropertyConstants.igFinite; tdep = spec.EffectiveThreadDepth * MmToM; }
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
                        std, sub, row != null ? stdSize : (size.Length > 0 ? (object)size : M), fit, dia,   // 1-5 标准/子类型/大小/配合/孔径
                        0.0, 0.0, 0.0, 0.0, bottom,                               // 6-10 沉孔锥孔显式 0
                        P.FeaturePropertyConstants.igTappedHole, M, M, dia, tdm, tdep, vd, M,   // 11-18 处理=攻丝 / 螺纹内径=孔径 / 螺纹深度
                        M, M, M, M, M, true,                                      // 19-24
                        M, M, M, M, M, M, M, M, M, M, M, M);                      // 25-36
                    TrySet(() => d.ThreadExternalDiameter = nominal, "AutoHoleThreadDia");
                    if (size.Length > 0) TrySet(() => d.ThreadDescription = size, "AutoHoleThreadDesc");
                    result.Method = "螺纹孔 " + (size.Length > 0 ? size + "（Φ" + N(spec.HoleDiameter) + " 内径）" : "自定义 Φ" + N(spec.HoleDiameter))
                                  + " " + spec.ThreadText;
                    break;
                }
                default:
                    d = part.HoleDataCollection.AddEx(P.FeaturePropertyConstants.igRegularHole,
                        std, sub, stdSize, fit, dia,                              // 1-5 标准/子类型/大小/配合/孔径
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

        internal static string Audit(P.Hole hole, HoleSpec spec){
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
                // 天工孔标准表那一行要原样存进去（否则天工自带的孔命令打开时大小显示 *）
                var row = HoleStandards.Resolve(spec);
                if (row != null) {
                    string gotSize = SafeStr(() => d.Size), gotSub = SafeStr(() => d.SubType);
                    if ((gotSize.Length > 0 || gotSub.Length > 0) && (gotSize != row.Size || gotSub != row.SubType))
                        msgs.Add("孔标准回读 " + gotSub + " " + gotSize + "（要求 " + row.SubType + " " + row.Size + "）");
                }

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
                // 孔底角度：只有盲孔且显式要了 V 型底才比对，贯通孔或平底回读 0 是正常的
                if (!spec.Through && spec.Bottom == HoleBottom.VBottom) {
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
                    // 螺纹深度：全长要回读 igThroughAll；指定深度要回读 igFinite + 同样的深度
                    string method = SafeStr(() => d.ThreadDepthMethod.ToString());
                    if (spec.ThreadFull) {
                        if (method.Length > 0 && method != "igThroughAll")
                            msgs.Add("螺纹深度方式回读 " + method + "（要求全长）");
                    } else {
                        double gotTd = Read(d, x => x.ThreadDepth) * 1000.0;
                        if (method.Length > 0 && method != "igFinite") msgs.Add("螺纹深度方式回读 " + method + "（要求指定深度）");
                        else if (Off(gotTd, spec.EffectiveThreadDepth, AuditTolMm))
                            msgs.Add("螺纹深度回读 " + N(gotTd) + "（要求 " + N(spec.EffectiveThreadDepth) + "）");
                    }
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
    }
}
