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
        // 腰孔（长圆孔）：Holes2d 只能做圆孔，所以走 ExtrudedCutouts 通切。
        // lengthMm 是总长，widthMm 是槽宽（= 两端半圆的直径）。圆心距 = lengthMm - widthMm。
        public static bool DrillSlotOne(TargetFace target, V3 localCentre, V3 localDirInPlane, double lengthMm, double widthMm, out string why){
            why = "";
            if (lengthMm <= widthMm) { why = "腰孔总长必须大于槽宽。"; return false; }
            if (widthMm <= 0) { why = "槽宽必须大于 0。"; return false; }
            var part = target.Part;
            P.Model model = ModelFor(target);   // 腰孔是拉伸切除：多实体零件上老写法就能切（实测），只要用对实体
            if (model == null) { why = "目标零件没有可用实体。"; return false; }
            P.Profile profile = null; object set = null; bool ok = false;
            try {
                var plane = FindOrCreatePlane(part, target);
                if (plane == null) { why = "无法在所选面上建立基准面。"; return false; }
                var ps = part.ProfileSets.Add(); set = ps;
                profile = ps.Profiles.Add(plane);
                P.ExtrudedCutout made;
                ok = SlotProfile(part, model, profile, localCentre, localDirInPlane, lengthMm, widthMm, out why, out made);
                return ok;
            } catch (Exception e) {
                why = "腰孔异常：" + e.Message;
                return false;
            } finally {
                if (profile != null) { try { profile.Visible = false; } catch {} }
                if (!ok) DropProfile(profile, set);
            }
        }

        // 腰孔轮廓 + 通切。profile 由调用方建好，基准面只建一次。
        static bool SlotProfile(P.PartDocument part, P.Model model, P.Profile profile, V3 localCentre, V3 localDirInPlane,
                                double lengthMm, double widthMm, out string why, out P.ExtrudedCutout made){
            why = ""; made = null;
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
                    if ((int)cut.GetStatusEx(out desc) == (int)P.FeatureStatusConstants.igFeatureOK) { made = cut; return true; }
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
            target = AnchorToActivePart(target);   // 原位编辑时只认正在编辑的那份文档（和圆孔 DrillRequests 一样）
            var part = target.Part;
            P.Model model = ModelFor(target);
            if (model == null) throw new InvalidOperationException("目标零件没有可用的实体模型。");
            var plane = FindOrCreatePlane(part, target);
            if (plane == null) throw new InvalidOperationException("无法在所选面上建立基准面。");
            for (int i = 0; i < localCentres.Count; i++) {
                string why;
                var dir = i < localDirs.Count ? localDirs[i] : new V3(1,0,0);
                P.ExtrudedCutout made;
                V3 where = target.Placement.Point(localCentres[i]);   // 装配坐标：结果列表里点一条跳过去
                if (DrillSlotOnPlane(part, model, plane, localCentres[i], dir, lengthMm, widthMm, out why, out made)) {
                    result.Created++; result.Undo.Cutout(part, made);
                    result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Done, "第 " + (i + 1) + " 个腰孔 " + N(lengthMm) + "×" + N(widthMm)).At(where, lengthMm));
                } else {
                    result.Failures.Add(why);
                    result.Outcomes.Add(new ResultList.Item(ResultList.Kind.Fail, "第 " + (i + 1) + " 个腰孔：" + why).At(where, lengthMm));
                }
            }
            return result;
        }

        static bool DrillSlotOnPlane(P.PartDocument part, P.Model model, P.RefPlane plane, V3 localCentre, V3 localDirInPlane,
                                     double lengthMm, double widthMm, out string why, out P.ExtrudedCutout made){
            why = ""; made = null;
            if (lengthMm <= widthMm) { why = "腰孔总长必须大于槽宽。"; return false; }
            if (widthMm <= 0) { why = "槽宽必须大于 0。"; return false; }
            P.Profile profile = null; object set = null; bool ok = false;
            try {
                var ps = part.ProfileSets.Add(); set = ps;
                profile = ps.Profiles.Add(plane);
                ok = SlotProfile(part, model, profile, localCentre, localDirInPlane, lengthMm, widthMm, out why, out made);
                return ok;
            } catch (Exception e) { why = "腰孔异常：" + e.Message; return false; }
            finally {
                if (profile != null) { try { profile.Visible = false; } catch {} }
                // 没切成：空草图留在特征树里会夹在两个特征之间，后面的孔就编不成组（和 DrillOne 删空轮廓同一个道理）
                if (!ok) DropProfile(profile, set);
            }
        }
    }
}
