using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TianGongCadSuite {
    // 识别出来的一个框口 + 它属于哪个平面（界面列表、预览、生成都用它）
    public sealed class FoundOpening {
        public FrameOpening Opening;
        public string PlaneLabel = "";      // 「平面 1（法向 Z）」
        public int PlaneIndex;
        public List<FrameMember> Members;   // 这个平面的型材（预览画灰线用）
    }

    // 型材自动分组：一次选一整个立体框架（前后左右几个面都有框口），自动按"平面"分开识别。
    //
    // 原来 FrameDetection.Detect 要求选的型材全部在同一个平面、只有两个互相垂直的方向，
    // 否则报"请按平面分组选取" —— 用户得一面一面地框选。现在：
    //   ① 收集所选型材的轴线方向，两两垂直的方向对 (u, v) 定出一个平面法向 n = u × v；
    //   ② 对每个法向，取轴线与它垂直的型材，按"厚度中心沿 n 的位置"分层（同一层 = 同一个框架平面）；
    //   ③ 每层只留沿 u / v 方向的型材（斜撑等不参与），交给原来的 Detect 识别框口 —— 判据一点没变；
    //   ④ 同一个框口从两个方向组合都识别到时去重。
    // 某一层识别失败（角部没闭合、非矩形……）只记一条说明、跳过这一层，不影响别的平面。
    // 只选了一个平面的型材时，结果和原来直接 Detect 一样。
    public static class FrameGrouping {
        const double DirTol = 1e-6;     // 方向平行/垂直判定（单位向量点积/叉积）
        const double DepthTol = 1e-7;   // 同一层的厚度中心位置（米），和 FrameDetection 的 Tol 一样

        public static List<FoundOpening> Detect(IList<FrameMember> members, double thickness, double gap, List<string> notes){
            if (members == null || members.Count < 4) throw new ArgumentException("请至少选择围成框口的四根型材，或选择包含它们的框架子装配。");
            var axes = new List<V3>();
            foreach (var m in members) {
                V3 a;
                try { a = m.Axis.Canonical(); } catch { continue; }
                if (!axes.Any(x => x.Cross(a).Length < DirTol)) axes.Add(a);
            }
            var normals = new List<V3[]>();   // {n, u, v}
            for (int i = 0; i < axes.Count; i++)
                for (int j = i + 1; j < axes.Count; j++) {
                    if (Math.Abs(axes[i].Dot(axes[j])) > DirTol) continue;
                    var n = axes[i].Cross(axes[j]).Canonical();
                    if (!normals.Any(x => x[0].Cross(n).Length < DirTol)) normals.Add(new[]{ n, axes[i], axes[j] });
                }
            if (normals.Count == 0) throw new ArgumentException(axes.Count <= 1 ? "所选型材均平行，没有闭合矩形框口。" : "所选型材没有互相垂直的，自动模式只支持相互垂直的直型材。");

            var result = new List<FoundOpening>();
            int planeNo = 0;
            foreach (var nuv in normals) {
                V3 n = nuv[0], u = nuv[1], v = nuv[2];
                // 轴线在这个平面内、而且沿 u 或 v 的型材，按厚度中心分层
                var layers = new List<KeyValuePair<double, List<FrameMember>>>();
                foreach (var m in members) {
                    V3 a;
                    try { a = m.Axis.Canonical(); } catch { continue; }
                    if (a.Cross(u).Length >= DirTol && a.Cross(v).Length >= DirTol) continue;
                    if (m.Points.Count == 0) continue;
                    double lo = m.Points.Min(p => p.Dot(n)), hi = m.Points.Max(p => p.Dot(n));
                    double mid = (lo + hi) / 2;
                    int k = layers.FindIndex(x => Math.Abs(x.Key - mid) <= DepthTol);
                    if (k < 0) layers.Add(new KeyValuePair<double, List<FrameMember>>(mid, new List<FrameMember>{ m }));
                    else layers[k].Value.Add(m);
                }
                foreach (var layer in layers.OrderBy(x => x.Key)) {
                    if (layer.Value.Count < 4) continue;
                    planeNo++;
                    string label = "平面 " + planeNo + "（法向 " + AxisName(n) + "）";
                    List<FrameOpening> found;
                    try { found = FrameDetection.Detect(layer.Value, thickness, gap); }
                    catch (ArgumentException e) {
                        if (notes != null) notes.Add(label + "，" + layer.Value.Count + " 根型材：" + e.Message);
                        continue;
                    }
                    foreach (var o in found) {
                        if (result.Any(r => (r.Opening.MidPoint - o.MidPoint).Length < 1e-6)) continue;   // 同一个框口识别两次
                        result.Add(new FoundOpening { Opening = o, PlaneLabel = label, PlaneIndex = planeNo, Members = layer.Value });
                    }
                }
            }
            if (result.Count == 0) {
                string why = notes != null && notes.Count > 0 ? string.Join("\n", notes.ToArray()) : "所选型材里没有至少 4 根在同一平面、互相垂直的型材。";
                throw new ArgumentException("没有发现闭合矩形框口。\n" + why);
            }
            return result;
        }

        // 法向写成人看得懂的：正好是坐标轴就写 X/Y/Z，否则写分量
        static string AxisName(V3 n){
            if (Math.Abs(Math.Abs(n.X) - 1) < 1e-6) return "X";
            if (Math.Abs(Math.Abs(n.Y) - 1) < 1e-6) return "Y";
            if (Math.Abs(Math.Abs(n.Z) - 1) < 1e-6) return "Z";
            return "(" + n.X.ToString("0.##", CultureInfo.InvariantCulture) + ", " + n.Y.ToString("0.##", CultureInfo.InvariantCulture) + ", " + n.Z.ToString("0.##", CultureInfo.InvariantCulture) + ")";
        }
    }
}
