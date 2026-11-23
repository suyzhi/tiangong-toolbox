using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
namespace TianGongCadSuite {
    // A world-coordinate preview, independent of the CAD viewport's render backend.
    public sealed class Preview : Control {
        static readonly Font TitleFont = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        static readonly Pen FacePen = new Pen(Color.FromArgb(100, 111, 125), 1.5f);
        static readonly SolidBrush PanelFill = new SolidBrush(Color.FromArgb(65, 40, 150, 220));
        static readonly Pen BluePen = new Pen(Color.FromArgb(0, 130, 200), 2f);
        static readonly Pen OrangeDashPen = MakeOrangeDash();
        static Pen MakeOrangeDash() { var p = new Pen(Color.DarkOrange, 1.5f); p.DashStyle = DashStyle.Dash; return p; }

        public PanelSpec Spec;public V3 PickedPoint;public string EmptyText="选四个面和定位点，并输入总厚度后显示。";public List<V3[]> SourceEdges=new List<V3[]>();
        public int DrawCount;double yaw=-.55,pitch=.65,zoom=1;Point last;bool dragging;
        public Preview(){DoubleBuffered=true;BackColor=Color.FromArgb(245,248,252);Dock=DockStyle.Fill;SetStyle(ControlStyles.ResizeRedraw,true);}
        public void ResetView(){yaw=-.55;pitch=.65;zoom=1;Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){dragging=true;last=e.Location;Capture=true;}}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging){yaw+=(e.X-last.X)*.01;pitch=Math.Max(-1.45,Math.Min(1.45,pitch+(e.Y-last.Y)*.01));last=e.Location;Invalidate();}}
        protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);zoom=Math.Max(.2,Math.Min(8,zoom*Math.Pow(1.1,e.Delta/120.0)));Invalidate();}
        protected override void OnDoubleClick(EventArgs e){base.OnDoubleClick(e);ResetView();}
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            g.DrawString("位置预览",TitleFont,Brushes.DimGray,16,16);
            g.DrawString("拖动旋转 · 滚轮缩放 · 双击复位",Font,Brushes.Gray,16,42);
            if(Spec==null){g.DrawString(EmptyText,Font,Brushes.Gray,16,90);return;}
            DrawCount++;
            // 无 LINQ 堆分配直接单遍扫包围盒，拖动旋转与滚轮缩放流畅不卡顿
            double minX=double.MaxValue,maxX=double.MinValue,minY=double.MaxValue,maxY=double.MinValue,minZ=double.MaxValue,maxZ=double.MinValue;
            double halfT=Spec.Thickness/2;
            var cornersBot=new V3[4];var cornersTop=new V3[4];var cornersMid=new V3[4];
            for(int i=0;i<4;i++){
                var cb=Spec.Corner(i,-halfT);var ct=Spec.Corner(i,halfT);cornersBot[i]=cb;cornersTop[i]=ct;cornersMid[i]=Spec.Corner(i,0);
                if(cb.X<minX)minX=cb.X;if(cb.X>maxX)maxX=cb.X;if(cb.Y<minY)minY=cb.Y;if(cb.Y>maxY)maxY=cb.Y;if(cb.Z<minZ)minZ=cb.Z;if(cb.Z>maxZ)maxZ=cb.Z;
                if(ct.X<minX)minX=ct.X;if(ct.X>maxX)maxX=ct.X;if(ct.Y<minY)minY=ct.Y;if(ct.Y>maxY)maxY=ct.Y;if(ct.Z<minZ)minZ=ct.Z;if(ct.Z>maxZ)maxZ=ct.Z;
            }
            for(int li=0;li<SourceEdges.Count;li++){
                var line=SourceEdges[li];
                for(int pi=0;pi<line.Length;pi++){
                    var p=line[pi];
                    if(p.X<minX)minX=p.X;if(p.X>maxX)maxX=p.X;if(p.Y<minY)minY=p.Y;if(p.Y>maxY)maxY=p.Y;if(p.Z<minZ)minZ=p.Z;if(p.Z>maxZ)maxZ=p.Z;
                }
            }
            var center=new V3((minX+maxX)/2,(minY+maxY)/2,(minZ+maxZ)/2);
            var right=new V3(Math.Cos(yaw),Math.Sin(yaw),0);var up=new V3(-Math.Sin(yaw)*Math.Sin(pitch),Math.Cos(yaw)*Math.Sin(pitch),Math.Cos(pitch));
            double cMinX=double.MaxValue,cMaxX=double.MinValue,cMinY=double.MaxValue,cMaxY=double.MinValue;
            Action<V3> acc=p=>{
                var d=p-center;double x=d.Dot(right),y=d.Dot(up);
                if(x<cMinX)cMinX=x;if(x>cMaxX)cMaxX=x;if(y<cMinY)cMinY=y;if(y>cMaxY)cMaxY=y;
            };
            for(int i=0;i<4;i++){acc(cornersBot[i]);acc(cornersTop[i]);}
            for(int li=0;li<SourceEdges.Count;li++){var line=SourceEdges[li];for(int pi=0;pi<line.Length;pi++)acc(line[pi]);}
            double spanX=Math.Max(1e-6,cMaxX-cMinX),spanY=Math.Max(1e-6,cMaxY-cMinY);
            double scale=Math.Min(Math.Max(80,Width-80)/spanX,Math.Max(80,Height-190)/spanY)*zoom;
            double cx0=Width/2.0,cy0=Height/2.0+15.0;
            Func<V3,PointF> project=p=>{var d=p-center;return new PointF((float)(cx0+d.Dot(right)*scale),(float)(cy0-d.Dot(up)*scale));};
            for(int li=0;li<SourceEdges.Count;li++){
                var line=SourceEdges[li];
                if(line.Length<2)continue;
                var pts=new PointF[line.Length];
                for(int pi=0;pi<line.Length;pi++)pts[pi]=project(line[pi]);
                g.DrawLines(FacePen,pts);
            }
            var pTop=new PointF[4];var pBot=new PointF[4];var pMid=new PointF[4];
            for(int i=0;i<4;i++){pTop[i]=project(cornersTop[i]);pBot[i]=project(cornersBot[i]);pMid[i]=project(cornersMid[i]);}
            g.FillPolygon(PanelFill,pTop);
            for(int i=0;i<4;i++){int j=(i+1)%4;g.DrawLine(BluePen,pBot[i],pBot[j]);g.DrawLine(BluePen,pTop[i],pTop[j]);g.DrawLine(BluePen,pBot[i],pTop[i]);g.DrawLine(OrangeDashPen,pMid[i],pMid[j]);}
            var picked=project(PickedPoint);if(ClientRectangle.Contains(Point.Round(picked))){g.FillEllipse(Brushes.DarkOrange,picked.X-4,picked.Y-4,8,8);g.DrawString("定位点",Font,Brushes.DarkOrange,picked.X+6,picked.Y+4);}
            g.DrawString("灰：所选面边界   蓝：板子   橙：中面 / 定位点",Font,Brushes.DimGray,16,Height-64);
            g.DrawString("按真实比例显示；不代表已检查与其他零件的干涉。",Font,Brushes.Gray,16,Height-40);
        }
    }
}
