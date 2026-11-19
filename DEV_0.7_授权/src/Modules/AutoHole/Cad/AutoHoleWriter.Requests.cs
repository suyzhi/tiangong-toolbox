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
        // 一个待打的孔：规格 + 装配坐标孔心。
        public sealed class HoleRequest {
            public HoleSpec Spec;
            public V3 Centre;
            public string Source = "";   // 来自哪个参考孔，出错时能说清
            public HoleLink Link;        // 和参考孔的关联记录（不保持关联时为 null）；打成功后写进孔特征
            public ReferenceHole From;   // 照哪个参考孔配的（天工原生关联要用它的选择引用和来源面）
            public NativeAnchor Anchor;  // 天工原生关联：孔心挂在哪条零件间复制来的圆边上（null = 不走原生关联）
            public bool NativeLinked;    // 打出来的孔确实挂上了原生关联
            public string LinkWhy = "";  // 要关联却没挂上天工原生关联的原因（给用户看，带怎么办）
            public bool PluginLinked;    // 挂不上原生关联、改写了插件关联记录（「同步孔位」能把它挪回去）
            public bool LinkNotNeeded;   // 参考孔和打孔面在同一个零件上：不用关联
            public string PluginWhy = "";// 插件关联记录也写不了的原因（认不出实例……）
            public bool Created;         // DrillRequests 之后：这个孔是这次新打出来的（不含"本来就有孔"）
            public bool Done;            // DrillRequests 之后：打上了，或这个位置本来就有孔（不用再打）
            public bool Disconnected;    // DrillRequests 之后：没打上，最后一次是"对象已断开/没注册"
        }

        // 一批不同规格的孔一次打完。基准面只建一次，每个规格各自建 HoleData。

        // 原位编辑（在装配里双击零件）时，用户点选的面来自"装配里的实例"那份文档对象，
        // 而天工CAD 只认"正在编辑的那个零件文档"——用它建孔会被拒绝（RPC_E_DISCONNECTED）。
        // 这里把目标面重新锚定到活动零件文档上的**同一张面**：Part/Face 换成活动文档的，
        // 装配->局部的坐标映射（Placement）和平面数据（Plane）保持原样，孔心换算因此不受影响。
        public static TargetFace AnchorToActivePart(TargetFace target){
            if(target==null||target.Part==null)return target;
            P.PartDocument active=null;
            try{active=target.Part.Application.ActiveDocument as P.PartDocument;}catch(Exception e){Log.Write("AnchorFaceApp",e);return target;}
            if(active==null)return target;                                   // 活动文档不是零件：维持原样（装配上下文）
            string activeName=Name(active), targetName=Name(target.Part);
            if(string.IsNullOrEmpty(activeName)||string.IsNullOrEmpty(targetName))return target;
            if(!string.Equals(activeName,targetName,StringComparison.OrdinalIgnoreCase))return target;
            try{
                if(active.Models.Count<1)return target;
                ModelFor(target);                                            // 没认过实体的现在认（记进 target.Body）
                int bodyIndex=Math.Max(1,target.Body);
                var model=PartBodies.Model(active,bodyIndex);   // 同一个零件文件，实体的序号不变
                V3 localPoint=target.Placement.InversePoint(target.Plane.Point);
                V3 localNormal=target.Placement.InverseNormal(target.Plane.Normal).Unit();
                G.Face face=null;double best=double.MaxValue;
                foreach(G.Face candidate in (G.Faces)((G.Body)model.Body).get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)){
                    var plane=candidate.Geometry as G.Plane;
                    if(plane==null)continue;
                    Array point=new double[3],normal=new double[3];
                    try{plane.GetPlaneData(ref point,ref normal);}catch{continue;}
                    V3 candidateNormal=V3.From(normal).Unit();
                    if(Math.Abs(candidateNormal.Dot(localNormal))<0.999)continue;
                    double distance=Math.Abs((V3.From(point)-localPoint).Dot(localNormal));
                    if(distance<best){best=distance;face=candidate;}
                }
                if(face==null||best>1e-6){                                   // 找不到同一张面：别硬换
                    Info("原位编辑：在「"+targetName+"」实体 "+bodyIndex+"/"+PartBodies.Count(active)+" 里找不到所选的面（最近 "+(face==null?"无":(best*1000.0).ToString("0.###")+"mm")+"），沿用装配里的面");
                    return target;
                }
                var anchored=new TargetFace();
                anchored.Plane=target.Plane;
                anchored.Face=face;
                anchored.Part=active;
                anchored.Placement=target.Placement;
                anchored.Pick=target.Pick;
                anchored.Bounds=target.Bounds;
                anchored.Body=bodyIndex;
                anchored.PartName=target.PartName;   // 原来用 FullName（全路径）：结果列表里零件名变成一长串路径
                try{anchored.FaceId=face.ID.ToString(CultureInfo.InvariantCulture);}catch{anchored.FaceId=target.FaceId;}
                anchored.Label=AutoHoleReader.FaceText(active,bodyIndex,anchored.FaceId);
                anchored.LinkNote=target.LinkNote;
                return anchored;
            }catch(Exception e){Log.Write("AnchorFace",e);return target;}
        }
        static string Name(P.PartDocument part){try{return part.FullName;}catch{return null;}}

        public static DrillResult DrillRequests(TargetFace target, IList<HoleRequest> requests){
            var result = new DrillResult();
            if (requests == null || requests.Count == 0) return result;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            scanMs = 0; addMs = 0; HoleCombine.AttachMs = HoleCombine.IncludeMs = HoleCombine.KeypointMs = 0; HoleCombine.Timing = "";
            target = AnchorToActivePart(target);   // 原位编辑时把面锚定到正在编辑的文档（见上）
            result.Requested = requests.Count;
            var part = target.Part;
            // 零件对象已经断开（刚进出过原位编辑、天工把它重新加载了）：原来往下走拿不到模型，报一句"目标零件没有可用的实体模型"（10-23 现场），不知所云
            if (!TargetFace.PartAlive(part)) throw new InvalidOperationException(TargetFace.DeadPartMessage(target.PartName));
            P.Model model = ModelFor(target);
            if (model == null) throw new InvalidOperationException("目标零件没有可用的实体模型。");
            P.RefPlane plane = FindOrCreatePlane(part, target);
            if (plane == null) throw new InvalidOperationException("无法在所选面上建立打孔基准面。");
            var methods = new List<string>();
            // 整轮重试：实测（原位编辑上下文）第一次、第二次可能被 CAD 拒绝，第三次就成功；
            // 而"把同一次调用重发 6 遍"（Attempt）救不回来。所以这里按**轮**重来：
            // 每轮丢掉基准面缓存、重建基准面、重取模型，再打没打上的那些孔。
            const int Rounds = 3;
            var done = new bool[requests.Count];
            var whyByRequest = new string[requests.Count];
            var discon = new bool[requests.Count];         // 最后一次失败是"对象已断开"一类
            var made = new P.Hole[requests.Count];        // 每个请求打出来的孔特征（命名、编组、写关联用）
            var madeLocal = new V3[requests.Count];
            var multis = new Dictionary<string, P.UserDefinedPattern>(StringComparer.Ordinal);   // 规格键 → 这次合成的多孔孔特征
            // 这张面上已经有哪些孔：读一遍实体（见 HoleSpots），之后每个孔位只在内存里比
            var spots = ScanSpots(model);
            V3 normal = PlaneNormal(plane);
            // 同一规格只造一个 HoleData（原来每个孔各造一个，HoleDataCollection 里留下一堆一模一样的）
            var dataBySpec = new Dictionary<string, P.HoleData>(StringComparer.Ordinal);
            // 按规格分批打，同规格的孔在特征树里挨着建。天工的特征组只收特征树里连续的一段：
            // 同一批里两种规格交错（6、9、6、9）时 AddFeatureGroupsBySet 报 E_INVALIDARG —— 真机复现
            // （tools/hole-feature-probe/HoleMultiProbe.cs G3），就是 10-13 现场"编组失败"的原因。
            // 整轮重试也放在每个规格里面做：重试补上的孔仍然紧挨着同规格的孔。
            foreach (var batch in SpecBatches(requests)) {
                var spec = requests[batch[0]].Spec;
                string key = spec == null ? "" : HoleNaming.SpecKey(spec);
                // ① 孔位：换算到零件局部、挡掉"已经有孔"的（CAD 会拒绝重复建孔且只报含糊的 COM 错误）
                var todo = new List<int>();
                foreach (int i in batch) {
                    var r = requests[i];
                    var local = target.Placement.InversePoint(target.OntoFace(r.Centre));
                    if (!local.Finite) { whyByRequest[i] = r.Source + " 孔心换算失败"; continue; }
                    double existing;
                    if (spots.At(local, normal, out existing)) {
                        string say = TargetFace.AlreadySay(r.Source, existing, r.Spec);
                        result.AlreadyOk++;
                        result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Skip, say).At(r.Centre, existing));
                        done[i] = true;
                        continue;
                    }
                    double other;
                    if (spots.Overlaps(local, normal, r.Spec == null ? 0 : r.Spec.HoleDiameter, out other)) {
                        // 和这批里另一个孔重叠：放进去整批都合不成（见 HoleSpots.Overlaps），这一个不打、说清楚
                        whyByRequest[i] = (r.Source.Length > 0 ? r.Source + "：" : "") + "和这一批里另一个孔（Φ" + other.ToString("0.##") + "）重叠，没有打（参考孔可能是腰孔的两头）";
                        continue;
                    }
                    spots.Plan(local, normal, r.Spec == null ? 0 : r.Spec.HoleDiameter);   // 同一位置后面的请求算"已经有孔"
                    madeLocal[i] = local;
                    todo.Add(i);
                }
                if (todo.Count == 0) continue;
                P.HoleData data;
                try { data = DataFor(part, spec, dataBySpec, methods); }
                catch (Exception e) { foreach (int i in todo) whyByRequest[i] = requests[i].Source + " 规格无效：" + e.Message; continue; }
                double depth = spec == null ? 0 : spec.Depth;

                // ② 多孔合一（0.7.10 起直接建）：N 个孔心画进一个共享轮廓 → 种子孔 → AddByProfiles，CAD 只建 2 个特征。
                //    原来先逐个打 N 个单孔、再删掉 N 个、再合成 —— 2N+2 次特征重算，20 个孔要半分钟以上。
                //    合不成就删干净、退回下面逐个打（每个孔位都单独验过，原生关联挂不上的也在那里退回属性集关联）。
                //    0.8.4：挂不上原生关联的孔改写插件关联记录（Link），也照样合进多孔特征 —— 真机 LinkGroupProbe U1～U3：
                //    多孔特征的输入孔能挂属性、改它轮廓的孔心 + 重算就能单独挪（种子孔也行），「同步孔位」照样挪得动。
                bool combinable = CombineHoles && todo.Count >= 2;
                if (combinable) {
                    var locals = new List<V3>(); var anchors = new List<NativeAnchor>();
                    foreach (int i in todo) { locals.Add(madeLocal[i]); anchors.Add(requests[i].Anchor); }
                    List<P.Hole> inputs; string why; bool touched;
                    P.UserDefinedPattern multi = null;
                    try { multi = HoleCombine.Build(part, model, plane, locals, anchors, data, depth, SideOrder(plane), out inputs, out why, out touched); }
                    catch (Exception e) { Log.Write("AutoHoleCombine", e); inputs = null; why = Friendly(e); touched = true; }
                    if (multi != null) {
                        multis[key] = multi;
                        result.Undo.Combined(part, new string[0], multi, inputs);
                        result.Combined++;
                        for (int j = 0; j < todo.Count; j++) {
                            int i = todo[j];
                            done[i] = true; made[i] = inputs[j]; whyByRequest[i] = null;
                            result.Created++;
                            if (anchors[j] != null) { requests[i].NativeLinked = true; result.NativeLinked++; }
                            else if (requests[i].Anchor != null) { result.NativeMissed++; requests[i].LinkWhy = MissedWhy(requests[i].Anchor); }
                        }
                        string note = inputs.Count > 0 ? Audit(inputs[0], spec) : "";
                        if (note.Length > 0 && !result.Audit.Contains(note)) result.Audit = result.Audit.Length == 0 ? note : result.Audit + "；" + note;
                        continue;
                    }
                    Log.Write("HoleCombine", "直接合成没成功，逐个打：" + why);
                    result.Notes.Add(todo.Count + " 个「" + HoleNaming.Short(spec) + "」没能合成一个孔特征（" + why + "），已按单孔打好。");
                    // 合成失败时删过特征：CAD 可能把手里的模型/基准面对象一起作废了（一张面上的孔全删光时连基准面一起删，真机 J9），重取
                    if (touched) {
                        DropCache(part);
                        try { model = ModelFor(target) ?? model; } catch (Exception e) { Log.Write("HoleCombineModel", e); }
                        try { plane = FindOrCreatePlane(part, target) ?? plane; } catch (Exception e) { Log.Write("HoleCombinePlane", e); }
                    }
                }

                // ③ 逐个打（合不成、只有 1 个孔、要靠属性集关联的孔）
                for (int round = 1; round <= Rounds; round++) {
                    if (round > 1) {
                        Info("整轮重试第 " + round + " 轮（重建基准面后重来）");
                        DropCache(part);
                        try {
                            plane = FindOrCreatePlane(part, target);
                            model = ModelFor(target) ?? model;
                        } catch (Exception e) { Info("重建基准面失败：" + Friendly(e)); }
                    }
                    bool anyLeft = false;
                    foreach (int i in todo) {
                        if (done[i]) continue;
                        var r = requests[i];
                        string why; P.Hole created; bool nativeLinked;
                        if (DrillOne(part, model, plane, madeLocal[i], data, depth, r.Anchor, false, out why, out created, out nativeLinked)) {
                            done[i] = true;
                            if (nativeLinked) { r.NativeLinked = true; result.NativeLinked++; }
                            else if (r.Anchor != null) { result.NativeMissed++; r.LinkWhy = MissedWhy(r.Anchor); }
                            made[i] = created;
                            result.Created++;
                            result.Undo.Hole(part, created);
                            string note = Audit(created, r.Spec);
                            if (note.Length > 0 && !result.Audit.Contains(note)) result.Audit = result.Audit.Length == 0 ? note : result.Audit + "；" + note;
                            whyByRequest[i] = null;
                            continue;
                        }
                        whyByRequest[i] = (r.Source.Length > 0 ? r.Source + "：" : "") + why;
                        discon[i] = DisconnectHr(lastSideHr);
                        anyLeft = true;
                    }
                    if (!anyLeft) break;
                }
            }
            for (int i = 0; i < requests.Count; i++) {
                requests[i].Done = done[i];
                requests[i].Created = made[i] != null;
                requests[i].Disconnected = !done[i] && discon[i];
                if (requests[i].Disconnected) result.Disconnected++;
            }
            for (int i = 0; i < requests.Count; i++) if (!done[i] && whyByRequest[i] != null) {
                result.Failures.Add(whyByRequest[i]);
                result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Fail, whyByRequest[i]).At(requests[i].Centre, requests[i].Spec == null ? 6 : requests[i].Spec.HoleDiameter));
            }
            result.Method = methods.Count > 0 ? string.Join("／", methods.ToArray()) : "—";
            try { model = ModelFor(target) ?? model; } catch { }
            long drilledMs = clock.ElapsedMilliseconds;
            Organize(part, model, requests, made, madeLocal, multis, result);
            CheckBlindThrough(model, plane, requests, made, madeLocal, result);
            Log.Write("AutoHoleTiming", requests.Count + " 个孔：打孔 " + drilledMs + "ms，命名/编组 " + (clock.ElapsedMilliseconds - drilledMs)
                      + "ms（其中读实体圆边 " + scanMs + "ms，CAD 建孔 " + addMs + "ms；合成 " + result.Combined + " 个多孔孔特征：" + HoleCombine.Timing + "）");
            // 打好的孔：名字是编组/命名之后的（「M6 螺纹孔（螺纹全长）-3」），用户在特征树里找得到
            string partName = "";
            try { partName = part.Name; } catch { }
            for (int i = 0; i < requests.Count; i++) {
                if (made[i] == null) continue;
                string nm = "";
                try { nm = made[i].Name; } catch { }
                result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Done, (partName.Length > 0 ? partName + "：" : "") + (nm.Length > 0 ? nm : HoleNaming.Short(requests[i].Spec)))
                    .At(requests[i].Centre, requests[i].Spec == null ? 6 : requests[i].Spec.HoleDiameter));
            }
            return result;
        }

        // 盲孔比这里的材料还深：CAD 照样打成通孔（真机 User085E2E S5：10 厚板上 15 深），结果里要说出来；沉孔比板还深同理
        static void CheckBlindThrough(P.Model model, P.RefPlane plane, IList<HoleRequest> requests, P.Hole[] made, V3[] madeLocal, DrillResult result){
            HoleSpots after = null;
            var n = PlaneNormal(plane);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < requests.Count; i++) {
                var sp = requests[i].Spec;
                if (made[i] == null || sp == null) continue;
                // 沉孔比板还深：沉头那一段直接切穿，整个成了 Φ沉孔 的通孔，螺钉头卡不住（5 厚板上 M6 沉孔深 6.8）
                if (sp.Kind == HoleKind.Counterbore && sp.CounterboreDepth > 0) {
                    if (after == null) after = ScanSpots(model);
                    double cbExit = after.ExitDepth(madeLocal[i], n, sp.CounterboreDiameter / 2000.0, sp.CounterboreDepth / 1000.0);   // 沉孔底那一圈正好在沉孔深处，不算
                    if (cbExit > 0) {
                        string cb = "沉孔深 " + sp.CounterboreDepth.ToString("0.##") + "mm 比这里的板厚（" + (cbExit * 1000).ToString("0.##") + "mm）还深，整个打成了 Φ" + sp.CounterboreDiameter.ToString("0.##") + " 的通孔，螺钉头卡不住 —— 请换孔型（过孔 / 锥形沉孔）或改小沉孔深度";
                        if (seen.Add(cb)) result.Notes.Add(cb);
                        result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Warn, (requests[i].Source.Length > 0 ? requests[i].Source + "：" : "") + "沉孔切穿了板").At(requests[i].Centre, sp.CounterboreDiameter));
                        continue;
                    }
                }
                if (sp.Through) continue;
                if (after == null) after = ScanSpots(model);
                double exit = after.ExitDepth(madeLocal[i], n, sp.HoleDiameter / 2000.0, sp.Depth / 1000.0);
                if (exit <= 0) continue;
                string note = "盲孔深 " + sp.Depth.ToString("0.##") + "mm 比这里的材料还深（" + (exit * 1000).ToString("0.##") + "mm 处就穿出去了），打出来是通孔 —— 要盲孔请把深度改小（「改孔型」或撤销重打）";
                if (seen.Add(note)) result.Notes.Add(note);
                result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Warn, (requests[i].Source.Length > 0 ? requests[i].Source + "：" : "") + "打穿了（盲孔比材料深）").At(requests[i].Centre, sp.HoleDiameter));
            }
        }

        // 换个编辑环境补打（0.7.11）：retry 是对 first 里没打上的那些请求再打一遍的结果。
        // 失败以补打的为准（补打的就是 first 里全部没打上的），打上的、说明、撤销记录都累加。
        public static void MergeRetry(DrillResult first, DrillResult retry){
            if (first == null || retry == null) return;
            first.Created += retry.Created; first.AlreadyOk += retry.AlreadyOk;
            first.Linked += retry.Linked; first.NativeLinked += retry.NativeLinked; first.NativeMissed += retry.NativeMissed;
            first.Combined += retry.Combined;
            first.Disconnected = retry.Disconnected;
            first.Failures.Clear(); first.Failures.AddRange(retry.Failures);
            first.Outcomes.RemoveAll(o => o.Kind == ResultList.Kind.Fail);
            first.Outcomes.AddRange(retry.Outcomes);
            foreach (var n in retry.Notes) if (!first.Notes.Contains(n)) first.Notes.Add(n);
            first.Features.AddRange(retry.Features);
            first.TopFeatures.AddRange(retry.TopFeatures);
            if (retry.Method.Length > 0 && retry.Method != "—" && first.Method.IndexOf(retry.Method, StringComparison.Ordinal) < 0)
                first.Method = first.Method.Length == 0 || first.Method == "—" ? retry.Method : first.Method + "／" + retry.Method;
            if (retry.Audit.Length > 0 && !first.Audit.Contains(retry.Audit)) first.Audit = first.Audit.Length == 0 ? retry.Audit : first.Audit + "；" + retry.Audit;
            if (first.Standard.Length == 0) first.Standard = retry.Standard;
            first.Undo.Absorb(retry.Undo);
        }

        // 准备好了原生关联、打孔时却没挂上：孔心约束到复制来的圆边那一步天工没接受
        static string MissedWhy(NativeAnchor a){
            string w = a == null || string.IsNullOrEmpty(a.Why) ? "" : "（" + a.Why + "）";
            return "孔心约束到参考孔圆边时天工没接受" + w + "，孔照常打好了";
        }

        static P.HoleData DataFor(P.PartDocument part, HoleSpec spec, Dictionary<string, P.HoleData> cache, List<string> methods){
            string key = spec == null ? "" : HoleNaming.SpecKey(spec);
            P.HoleData data;
            if (cache.TryGetValue(key, out data)) return data;
            var one = new DrillResult();
            data = BuildHoleData(part, spec, one);
            if (one.Method.Length > 0 && !methods.Contains(one.Method)) methods.Add(one.Method);
            cache[key] = data;
            return data;
        }

        // 请求按规格分批（批的顺序 = 规格第一次出现的顺序，批内保持原顺序）
        static List<List<int>> SpecBatches(IList<HoleRequest> requests){
            var order = new List<string>();
            var by = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < requests.Count; i++) {
                string key = requests[i].Spec == null ? "" : HoleNaming.SpecKey(requests[i].Spec);
                List<int> l;
                if (!by.TryGetValue(key, out l)) { l = new List<int>(); by[key] = l; order.Add(key); }
                l.Add(i);
            }
            var result = new List<List<int>>();
            foreach (var k in order) result.Add(by[k]);
            return result;
        }

        // 打完一张面以后：多孔孔特征 / 同规格的单孔编组、按规格命名，并写上和参考孔的关联记录。
        // 这一步任何一处失败都只记一笔、不影响已经打好的孔（孔是对的，只是名字/编组/关联没写上）。
        static void Organize(P.PartDocument part, P.Model model, IList<HoleRequest> requests, P.Hole[] made, V3[] madeLocal,
                             Dictionary<string, P.UserDefinedPattern> multis, DrillResult result){
            if (model == null) return;
            var taken = ExistingFeatureNames(model);
            // 按规格分组，保持打孔顺序
            var order = new List<string>();
            var bySpec = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < requests.Count; i++) {
                if (made[i] == null) continue;
                string key = HoleNaming.SpecKey(requests[i].Spec);
                List<int> l;
                if (!bySpec.TryGetValue(key, out l)) { l = new List<int>(); bySpec[key] = l; order.Add(key); }
                l.Add(i);
            }
            // 有孔挂上了天工原生关联：这张面的孔要和它们的孔位链接编成一个组（0.8.4，见 NativeHoleLink.Session.End），
            // 一个特征只能进一个组，所以这里只命名、不并进之前那批的规格组
            bool linkGroup = false;
            foreach (var r in requests) if (r.NativeLinked) linkGroup = true;
            // 编组、命名、并进之前那批的组：见 HoleGrouping
            foreach (var key in order) {
                var idx = bySpec[key];
                P.UserDefinedPattern multi;
                multis.TryGetValue(key, out multi);
                var holes = new List<P.Hole>();
                foreach (int i in idx) holes.Add(made[i]);
                if (multi != null) result.TopFeatures.Add(multi); else foreach (var h in holes) result.TopFeatures.Add(h);
                string node = "";
                try {
                    var spec = requests[idx[0]].Spec;
                    if (linkGroup) node = HoleGrouping.NameOnly(spec, multi != null ? new List<object>{ multi } : holes.ConvertAll(h => (object)h), taken);
                    else node = multi != null ? HoleGrouping.ApplyMulti(model, spec, multi, taken, result.Notes, result.Undo, part)
                                              : HoleGrouping.Apply(model, spec, holes, taken, result.Notes, result.Undo, part);
                }
                catch (Exception e) { Log.Write("AutoHoleGrouping", e); }
                if (node.Length > 0 && !result.Features.Contains(node)) result.Features.Add(node);
            }
            int wanted = 0;
            for (int i = 0; i < requests.Count; i++) {
                if (made[i] == null || requests[i].Link == null || requests[i].NativeLinked || requests[i].LinkNotNeeded) continue;
                wanted++;
                try {
                    requests[i].Link.TgtCenter = madeLocal[i];
                    HoleLinkStore.Write(made[i], requests[i].Link);
                    requests[i].PluginLinked = true;
                    result.Linked++;
                } catch (Exception e) { Log.Write("AutoHoleLinkWrite", e); requests[i].LinkWhy += "；插件关联记录也没写上（" + Friendly(e) + "）"; }
            }
            if (result.Linked < wanted)
                result.Notes.Add("有 " + (wanted - result.Linked) + " 个孔没写上关联记录，参考零件挪动后它们不会跟着同步。");
        }

        // 多孔合一的开关（测试里关掉可以走回 0.7.8 的单孔 + 编组）
        public static bool CombineHoles = true;

        // 零件里已有的孔特征名 + 特征组名 + 阵列名：新名字要避开它们（第二批同规格孔接着往下编号）
        internal static HashSet<string> ExistingFeatureNames(P.Model model){
            var set = new HashSet<string>(StringComparer.Ordinal);
            try {
                int n = model.Holes.Count;
                for (int i = 1; i <= n; i++) { try { set.Add(model.Holes.Item(i).Name); } catch { } }
            } catch { }
            try {
                int n = model.FeatureGroups.Count;
                for (int i = 1; i <= n; i++) { try { set.Add(model.FeatureGroups.Item(i).Name); } catch { } }
            } catch { }
            try {
                int n = model.Patterns.Count;
                for (int i = 1; i <= n; i++) { try { set.Add(model.Patterns.Item(i).Name); } catch { } }
            } catch { }
            // 多孔特征和它的输入孔（输入孔不在 Model.Holes 里）
            try {
                int n = model.UserDefinedPatterns.Count;
                for (int i = 1; i <= n; i++) {
                    try {
                        var u = model.UserDefinedPatterns.Item(i);
                        set.Add(u.Name);
                        foreach (var h in HoleCombine.Inputs(u)) { try { set.Add(h.Name); } catch { } }
                    } catch { }
                }
            } catch { }
            return set;
        }
    }
}
