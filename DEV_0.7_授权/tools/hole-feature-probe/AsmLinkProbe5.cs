using System;
using System.Collections.Generic;
using System.IO;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;

// S：只读扫天工自带 Training 目录里的零件，找"一个孔特征里有多个孔"的样本，看它们是怎么存的
//    （轮廓数 / 每个轮廓几个 Hole2d / 草图集几个轮廓 / 孔特征的圆柱面有几根不同轴线）。只开不存。
partial class AsmDrivenHoleProbe {
    static void Scan(){
        string root = @"C:\Program Files\NDS\TianGong 2025\Training";
        var files = Directory.GetFiles(root, "*.par", SearchOption.AllDirectories);
        Say("   S 共 " + files.Length + " 个零件");
        int found = 0, holesSeen = 0, opened = 0;
        foreach (var file in files) {
            P.PartDocument part = null;
            try { part = app.Documents.Open(file) as P.PartDocument; } catch (Exception e) { Say("   打不开 " + Path.GetFileName(file) + " " + e.Message); continue; }
            if (part == null) continue;
            opened++;
            try {
                for (int mi = 1; mi <= part.Models.Count; mi++) {
                    var model = (P.Model)part.Models.Item(mi);
                    P.Holes holes = null; try { holes = model.Holes; } catch { }
                    if (holes == null) continue;
                    for (int i = 1; i <= holes.Count; i++) {
                        holesSeen++;
                        P.Hole h = null; try { h = holes.Item(i); } catch { continue; }
                        int np = 0; Array profs = new object[0];
                        try { h.GetProfiles(out np, ref profs); } catch { }
                        var per = new List<string>(); int total2d = 0; int setCount = -1;
                        for (int k = 0; k < np; k++) {
                            try {
                                var pr = (P.Profile)profs.GetValue(k);
                                int c = pr.Holes2d.Count; total2d += c; per.Add(c.ToString());
                                if (k == 0) { try { setCount = ((P.ProfileSet)pr.Parent).Profiles.Count; } catch { } }
                            } catch { per.Add("?"); }
                        }
                        var axes = new HashSet<string>();
                        try {
                            foreach (object o in (System.Collections.IEnumerable)h.get_Faces(G.FeatureTopologyQueryTypeConstants.igQueryAll)) {
                                var cyl = ((G.Face)o).Geometry as G.Cylinder; if (cyl == null) continue;
                                Array bp = new double[3], ax = new double[3]; double r; cyl.GetCylinderData(ref bp, ref ax, out r);
                                // 轴线在垂直于轴的平面上的位置（粗取：去掉沿轴分量）
                                double ax0 = D(ax,0), ax1 = D(ax,1), ax2 = D(ax,2);
                                double t = D(bp,0)*ax0 + D(bp,1)*ax1 + D(bp,2)*ax2;
                                axes.Add(Math.Round((D(bp,0)-t*ax0)*1e4).ToString() + "," + Math.Round((D(bp,1)-t*ax1)*1e4) + "," + Math.Round((D(bp,2)-t*ax2)*1e4));
                            }
                        } catch { }
                        if (np > 1 || total2d > 1 || axes.Count > 1) {
                            found++;
                            string nm = ""; try { nm = h.Name; } catch { }
                            Say("FOUND " + Path.GetFileName(file) + " | " + nm + " | 轮廓数=" + np + " 各轮廓Hole2d=[" + string.Join(",", per.ToArray()) + "] 草图集轮廓数=" + setCount + " 不同轴线=" + axes.Count);
                        }
                    }
                }
            } catch (Exception e) { Say("   读 " + Path.GetFileName(file) + " 异常 " + e.Message); }
            try { part.Close(false); } catch { }
            if (found >= 25) break;
        }
        Say("RESULT S 打开 " + opened + " 个零件，孔特征 " + holesSeen + " 个，多孔样本 " + found + " 个");
    }
}
