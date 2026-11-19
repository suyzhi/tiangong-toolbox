using System;
using System.Collections.Generic;
using System.Globalization;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;
using P=SolidEdgePart;
using G=SolidEdgeGeometry;
using S=SolidEdgeFrameworkSupport;

namespace TianGongCadSuite {
    public static class AutoHoleReader {
        public const double ObliqueLimitDeg = 2.0;
        // 读到的孔（装配坐标）转成参考孔。面扫描/圆柱面/圆边三条路都走这里。
        public static ReferenceHole ToReference(ScannedHole scanned){
            if (scanned == null) throw new ArgumentNullException("scanned");
            var hole = new ReferenceHole();
            hole.Center = scanned.Center;
            hole.Axis = scanned.Axis;
            hole.DiameterMm = scanned.DiameterMm;
            hole.MouthDiameterMm = scanned.MouthDiameterMm;
            hole.MouthKind = scanned.MouthKind;
            hole.Source = scanned.Source;
            hole.Tag = scanned.Tag;
            hole.Owner = scanned.Owner;
            hole.Placement = scanned.Placement;
            hole.Pick = scanned.Pick;
            hole.SourceFace = scanned.SourceFace;
            hole.Where = "孔口 Φ" + scanned.MouthDiameterMm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return hole;
        }

        public static ReferenceHole ReadReference(object selection){
            return ToReference(HoleScanCad.ScanEdge(selection));
        }

        public static TargetFace ReadTarget(object selection){
            var pg = PickGeometry.Unwrap(selection);
            var face = pg.Geometry as G.Face;
            if (face == null) throw new ArgumentException("请选择一个平面作为打孔面。");
            var plane = face.Geometry as G.Plane;
            if (plane == null) throw new ArgumentException("选中的是曲面，打孔面必须是平面。");
            var part = face.Document as P.PartDocument;
            if (part == null) {
                // 钣金件（.psm）：原来报"选中的面不属于零件文档，请直接在装配中选择零件上的平面"—— 用户点的明明就是装配里的零件面（0.8.5）
                object doc = null; try { doc = face.Document; } catch { }
                if (doc is P.SheetMetalDocument)
                    throw new ArgumentException(SheetMetalTargetMessage);
                throw new ArgumentException("选中的面不属于零件文档，请直接在装配中选择零件上的平面。");
            }
            Array p = new double[3], n = new double[3]; plane.GetPlaneData(ref p, ref n);
            var tf = new TargetFace {
                Plane = new PlaneInput(pg.Transform.Point(V3.From(p)), pg.Transform.Normal(V3.From(n)), "打孔面"),
                Face = face,
                Part = part,
                Placement = pg.Transform,
                Pick = selection
            };
            try { tf.PartName = part.Name; } catch { tf.PartName = "零件"; }
            try { tf.FaceId = face.ID.ToString(CultureInfo.InvariantCulture); } catch { }
            tf.Body = PartBodies.IndexOf(part, face, V3.From(p), V3.From(n).Unit());   // 面片这时还是活的，现在认最准
            tf.Label = FaceText(part, tf.Body, tf.FaceId);
            return tf;
        }

        // 点平面认参考孔用（0.10.2）：钣金件（.psm）的面也认。原来一律走 ReadTarget，点钣金件的面就报"只支持普通零件"——
        // 那句话说的是打孔面；参考孔本来就可以是钣金件上的孔（点孔边一直可以），点整张面一次认全却不行（OK310 围栏 13 个钣金件）。
        // 钣金件的面：Part = null（和点钣金件孔边时一样，参考孔不记所属零件、挂不了天工关联），sheetMetal = true，调用方别拿它当打孔面
        public static TargetFace ReadScanFace(object selection, out bool sheetMetal){
            sheetMetal = false;
            var pg = PickGeometry.Unwrap(selection);
            var face = pg.Geometry as G.Face;
            object doc = null; try { if (face != null) doc = face.Document; } catch { }
            var sheet = doc as P.SheetMetalDocument;
            if (sheet == null) return ReadTarget(selection);
            var plane = face.Geometry as G.Plane;
            if (plane == null) throw new ArgumentException("选中的是曲面。要认面上的孔请点平面；单个孔可以点孔口的圆形边线。");
            sheetMetal = true;
            Array p = new double[3], n = new double[3]; plane.GetPlaneData(ref p, ref n);
            var tf = new TargetFace {
                Plane = new PlaneInput(pg.Transform.Point(V3.From(p)), pg.Transform.Normal(V3.From(n)), "参考面"),
                Face = face, Part = null, Placement = pg.Transform, Pick = selection, Body = 1
            };
            try { tf.PartName = sheet.Name; } catch { tf.PartName = "钣金件"; }
            try { tf.FaceId = face.ID.ToString(CultureInfo.InvariantCulture); } catch { }
            tf.Label = "平面 " + tf.FaceId;
            return tf;
        }
        public const string SheetMetalTargetMessage = "这是钣金件（.psm）上的面：自动打孔 / 批量排孔目前只支持普通零件（.par）。钣金件请用天工自己的「孔」命令（参考孔照样可以点钣金件上的孔）。";

        // 「平面 533」；多实体零件写上第几个实体（面 ID 跨实体重号，只写 ID 用户分不清是哪张）
        internal static string FaceText(P.PartDocument part, int body, string faceId){
            string s = "平面 " + faceId;
            return PartBodies.Multi(part) ? "实体 " + Math.Max(1, body) + " · " + s : s;
        }

        // 参考孔轴线与目标面的交点（装配坐标）。两零件不平行时给出明确原因。
        public static V3 Intersect(ReferenceHole hole, TargetFace face){
            double denom = hole.Axis.Unit().Dot(face.Plane.Normal.Unit());
            if (Math.Abs(denom) < 0.05)
                throw new ArgumentException("参考孔的轴线与打孔面几乎平行，无法定位孔心。请确认两个零件是平行装配的。");
            // 斜着的参考孔（0.8.5）：孔特征总是垂直于打孔面打，参考孔斜 5° 时打出来的孔和它不同轴，螺钉根本穿不过去。
            // 原来照样投点、照样打，结果里还写"打好了"。超过 2° 就不打，说清楚
            if (Math.Abs(denom) < Math.Cos(ObliqueLimitDeg * Math.PI / 180.0))
                throw new ObliqueHoleException(Math.Acos(Math.Min(1.0, Math.Abs(denom))) * 180.0 / Math.PI);
            double t = (face.Plane.Point - hole.Center).Dot(face.Plane.Normal) / denom;
            return hole.Center + hole.Axis * t;
        }
    }
}
