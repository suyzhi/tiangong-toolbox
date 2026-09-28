using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
namespace TianGongCadSuite {
    // A world-coordinate preview, independent of the CAD viewport's render backend.
    public sealed class Preview : Control {
        public PanelSpec Spec;public V3 PickedPoint;public List<V3[]> SourceEdges=new List<V3[]>();
        public int DrawCount;double yaw=-.55,pitch=.65,zoom=1;Point last;bool dragging;
        public Preview(){DoubleBuffered=true;BackColor=Color.FromArgb(245,248,252);Dock=DockStyle.Fill;SetStyle(ControlStyles.ResizeRedraw,true);}
        public void ResetView(){yaw=-.55;pitch=.65;zoom=1;Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){dragging=true;last=e.Location;Capture=true;}}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging){yaw+=(e.X-last.X)*.01;pitch=Math.Max(-1.45,Math.Min(1.45,pitch+(e.Y-last.Y)*.01));last=e.Location;Invalidate();}}
        protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);zoom=Math.Max(.2,Math.Min(8,zoom*Math.Pow(1.1,e.Delta/120.0)));Invalidate();}
        protected override void OnDoubleClick(EventArgs e){base.OnDoubleClick(e);ResetView();}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var title=new Font("Microsoft YaHei UI",10,FontStyle.Bold))g.DrawString("位置预览",title,Brushes.DimGray,16,16);
            g.DrawString("拖动旋转 · 滚轮缩放 · 双击复位",Font,Brushes.Gray,16,42);
            if(Spec==null){g.DrawString("选四个面和定位点，并输入总厚度后显示。",Font,Brushes.Gray,16,90);return;}
            DrawCount++;
            var all=SourceEdges.SelectMany(x=>x).Concat(Enumerable.Range(0,4).SelectMany(i=>new[]{Spec.Corner(i,-Spec.Thickness/2),Spec.Corner(i,Spec.Thickness/2)})).ToList();
            var center=new V3((all.Min(p=>p.X)+all.Max(p=>p.X))/2,(all.Min(p=>p.Y)+all.Max(p=>p.Y))/2,(all.Min(p=>p.Z)+all.Max(p=>p.Z))/2);
            var right=new V3(Math.Cos(yaw),Math.Sin(yaw),0);var up=new V3(-Math.Sin(yaw)*Math.Sin(pitch),Math.Cos(yaw)*Math.Sin(pitch),Math.Cos(pitch));
            var cameraPoints=all.Select(p=>new PointF((float)((p-center).Dot(right)),(float)((p-center).Dot(up)))).ToList();
            double spanX=Math.Max(1e-6,cameraPoints.Max(p=>p.X)-cameraPoints.Min(p=>p.X)),spanY=Math.Max(1e-6,cameraPoints.Max(p=>p.Y)-cameraPoints.Min(p=>p.Y));
            double scale=Math.Min(Math.Max(80,Width-80)/spanX,Math.Max(80,Height-190)/spanY)*zoom;
            Func<V3,PointF> project=p=>new PointF((float)(Width/2+(p-center).Dot(right)*scale),(float)(Height/2+15-(p-center).Dot(up)*scale));
            using(var facePen=new Pen(Color.FromArgb(100,111,125),1.5f))foreach(var line in SourceEdges)if(line.Length>=2)g.DrawLines(facePen,line.Select(project).ToArray());
            using(var fill=new SolidBrush(Color.FromArgb(65,40,150,220)))g.FillPolygon(fill,Enumerable.Range(0,4).Select(i=>project(Spec.Corner(i,Spec.Thickness/2))).ToArray());
            using(var blue=new Pen(Color.FromArgb(0,130,200),2))using(var orange=new Pen(Color.DarkOrange,1.5f)){
                orange.DashStyle=DashStyle.Dash;
                for(int i=0;i<4;i++){g.DrawLine(blue,project(Spec.Corner(i,-Spec.Thickness/2)),project(Spec.Corner((i+1)%4,-Spec.Thickness/2)));g.DrawLine(blue,project(Spec.Corner(i,Spec.Thickness/2)),project(Spec.Corner((i+1)%4,Spec.Thickness/2)));g.DrawLine(blue,project(Spec.Corner(i,-Spec.Thickness/2)),project(Spec.Corner(i,Spec.Thickness/2)));g.DrawLine(orange,project(Spec.Corner(i,0)),project(Spec.Corner((i+1)%4,0)));}
            }
            var picked=project(PickedPoint);if(ClientRectangle.Contains(Point.Round(picked))){g.FillEllipse(Brushes.DarkOrange,picked.X-4,picked.Y-4,8,8);g.DrawString("定位点",Font,Brushes.DarkOrange,picked.X+6,picked.Y+4);}
            g.DrawString("灰：所选面边界   蓝：板子   橙：中面 / 定位点",Font,Brushes.DimGray,16,Height-64);
            g.DrawString("按真实比例显示；不代表已检查与其他零件的干涉。",Font,Brushes.Gray,16,Height-40);
        }
    }
}
