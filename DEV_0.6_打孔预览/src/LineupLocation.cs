using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Xml.Serialization;
using System.Windows.Forms;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
namespace TianGongCadSuite {
    public sealed class LineupLocationPoint {
        public string Id="",Number="",Model="",Category="",State="",Reason="",Method="";
        public double X,Y;public int Pixels;
    }
    public sealed class LineupLocationPackage {
        public int Version=1,Width,Height;public string Image="view.png",AssemblyFile="";
        public List<LineupLocationPoint> Points=new List<LineupLocationPoint>();
    }
    public static class LineupLocation {
        public static void SetIsometric(F.Application app){
            F.View view=(F.View)((dynamic)app.ActiveWindow).View;
            double ex,ey,ez,tx,ty,tz,ux,uy,uz,scale;bool perspective;
            view.GetCamera(out ex,out ey,out ez,out tx,out ty,out tz,out ux,out uy,out uz,out perspective,out scale);
            double distance=Math.Max(.1,Math.Sqrt((ex-tx)*(ex-tx)+(ey-ty)*(ey-ty)+(ez-tz)*(ez-tz))/Math.Sqrt(3));
            view.SetCamera(tx+distance,ty-distance,tz+distance,tx,ty,tz,-1,1,2,false,Math.Max(scale,.001));
            view.Fit();view.Update();app.DoIdle();
            view.GetCamera(out ex,out ey,out ez,out tx,out ty,out tz,out ux,out uy,out uz,out perspective,out scale);
            double x=Math.Abs(ex-tx),y=Math.Abs(ey-ty),z=Math.Abs(ez-tz),tolerance=Math.Max(x,Math.Max(y,z))*1e-5+1e-8;
            if(perspective||Math.Abs(x-y)>tolerance||Math.Abs(y-z)>tolerance)throw new InvalidOperationException("CAD 未成功切换正交等轴测视图，已停止截图。");
        }
        static void Capture(F.Application app,dynamic view,string file){
            for(int attempt=0;attempt<5;attempt++){
                app.DoIdle();view.Update();System.Threading.Thread.Sleep(200);view.SaveAsImage(file,1600,1000);
                using(var bitmap=new Bitmap(file)){
                    var colors=new HashSet<int>();for(int y=0;y<bitmap.Height;y+=13)for(int x=0;x<bitmap.Width;x+=13)colors.Add(bitmap.GetPixel(x,y).ToArgb());
                    if(colors.Count>8)return;
                }
            }
            throw new InvalidOperationException("CAD 返回空白截图，请等待模型显示稳定后重试。");
        }
        public static LineupLocationPoint DetectDifference(Bitmap background,Bitmap first,Bitmap second){
            if(background.Size!=first.Size||first.Size!=second.Size)throw new InvalidDataException("截图尺寸发生变化。");
            var points=new List<Point>();double sx=0,sy=0;
            for(int y=0;y<first.Height;y++)for(int x=0;x<first.Width;x++){
                var a=background.GetPixel(x,y);var b=first.GetPixel(x,y);var c=second.GetPixel(x,y);
                int delta=Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B),stable=Math.Abs(c.R-b.R)+Math.Abs(c.G-b.G)+Math.Abs(c.B-b.B);
                if(delta>100&&stable<15){points.Add(new Point(x,y));sx+=x;sy+=y;}
            }
            if(points.Count<6||points.Count>first.Width*first.Height/3)return new LineupLocationPoint{State="unresolved",Reason="高亮差分缺失或影响范围过大",Pixels=points.Count};
            double cx=sx/points.Count,cy=sy/points.Count;var hit=points.OrderBy(p=>(p.X-cx)*(p.X-cx)+(p.Y-cy)*(p.Y-cy)).First();
            return new LineupLocationPoint{State="ok",Method="stable-highlight-difference",X=(hit.X+.5)/first.Width,Y=(hit.Y+.5)/first.Height,Pixels=points.Count};
        }
        // Use an actual highlighted pixel nearest the mask centroid, never an empty bounding-box center.
        public static LineupLocationPoint Detect(Bitmap first,Bitmap second){
            if(first.Size!=second.Size)throw new InvalidDataException("截图尺寸发生变化。");
            var points=new List<Point>();double sx=0,sy=0;
            for(int y=0;y<first.Height;y++)for(int x=0;x<first.Width;x++){
                Color a=first.GetPixel(x,y),b=second.GetPixel(x,y);
                bool yellow=a.R>150&&a.G>150&&a.B<140,magenta=b.R>150&&b.B>150&&b.G<140;
                if(yellow&&magenta){points.Add(new Point(x,y));sx+=x;sy+=y;}
            }
            if(points.Count<6)return new LineupLocationPoint{State="unresolved",Reason="未识别到足够的双色高亮像素；请调整视角或检查遮挡",Pixels=points.Count};
            double cx=sx/points.Count,cy=sy/points.Count;var hit=points.OrderBy(p=>(p.X-cx)*(p.X-cx)+(p.Y-cy)*(p.Y-cy)).First();
            return new LineupLocationPoint{State="ok",X=(hit.X+.5)/first.Width,Y=(hit.Y+.5)/first.Height,Pixels=points.Count};
        }
        public static LineupLocationPackage Export(F.Application app,A.AssemblyDocument doc,LineupProject project,IEnumerable<string> ids,string directory){
            if(Directory.Exists(directory))throw new IOException("输出目录已存在，请使用新的目录。");Directory.CreateDirectory(directory);
            var package=new LineupLocationPackage{AssemblyFile=project.AssemblyFile};dynamic view=((dynamic)app.ActiveWindow).View;
            var selected=new List<object>();for(int i=1;i<=doc.SelectSet.Count;i++)selected.Add(doc.SelectSet.Item(i));
            try{
                doc.SelectSet.RemoveAll();SetIsometric(app);
                double minX=double.PositiveInfinity,minY=minX,minZ=minX,maxX=double.NegativeInfinity,maxY=maxX,maxZ=maxX;
                foreach(var record in project.Records.Where(r=>ids.Contains(r.Id)))try{
                    dynamic target=LineupCad.ResolveTarget(doc,record);double x,y,z,u,v,w;target.Range(out x,out y,out z,out u,out v,out w);
                    minX=Math.Min(minX,x);minY=Math.Min(minY,y);minZ=Math.Min(minZ,z);maxX=Math.Max(maxX,u);maxY=Math.Max(maxY,v);maxZ=Math.Max(maxZ,w);
                }catch{}
                if(!double.IsInfinity(minX)){double pad=Math.Max(.003,Math.Max(maxX-minX,Math.Max(maxY-minY,maxZ-minZ))*.1);view.RangeZoomCamera(minX-pad,minY-pad,minZ-pad,maxX+pad,maxY+pad,maxZ+pad);view.Update();app.DoIdle();}
                Capture(app,view,Path.Combine(directory,package.Image));
                using(var baseImage=new Bitmap(Path.Combine(directory,package.Image))){package.Width=baseImage.Width;package.Height=baseImage.Height;}
                foreach(var r in project.Records.Where(r=>ids.Contains(r.Id))){
                    var point=new LineupLocationPoint{State="unresolved"};dynamic highlight=null;
                    try{
                        var target=LineupCad.ResolveTarget(doc,r);highlight=doc.HighlightSets.Add();highlight.Color=0x00FFFF;highlight.AddItem(LineupCad.HighlightTarget(target));highlight.Draw();view.Update();
                        string yellow=Path.Combine(directory,r.Id+"-yellow.png"),magenta=Path.Combine(directory,r.Id+"-magenta.png");Capture(app,view,yellow);
                        highlight.Color=0xFF00FF;highlight.Draw();Capture(app,view,magenta);
                        using(var a=new Bitmap(yellow))using(var b=new Bitmap(magenta))using(var bg=new Bitmap(Path.Combine(directory,package.Image))){point=Detect(a,b);if(point.State!="ok")point=DetectDifference(bg,a,b);else point.Method="dual-color";}
                        if(point.State!="ok"){
                            highlight.Delete();highlight=null;dynamic occurrence=target;bool visible=occurrence.Visible;
                            if(visible)try{
                                occurrence.Visible=false;
                                string hide1=Path.Combine(directory,r.Id+"-hide1.png"),hide2=Path.Combine(directory,r.Id+"-hide2.png");Capture(app,view,hide1);Capture(app,view,hide2);
                                using(var a=new Bitmap(hide1))using(var b=new Bitmap(hide2))using(var bg=new Bitmap(Path.Combine(directory,package.Image)))point=DetectDifference(bg,a,b);
                                if(point.State=="ok")point.Method="stable-visibility-difference";
                            }finally{occurrence.Visible=visible;view.Update();}
                        }
                    }catch(Exception ex){point.Reason=ex.Message;}
                    finally{if(highlight!=null)try{highlight.Delete();}catch{}view.Update();}
                    point.Id=r.Id;point.Number=r.Number;point.Model=r.Model;point.Category=r.Category;package.Points.Add(point);
                }
                string checkFile=Path.Combine(directory,"view-check.png");Capture(app,view,checkFile);
                using(var a=new Bitmap(Path.Combine(directory,package.Image)))using(var b=new Bitmap(checkFile)){
                    int changed=0,total=0;for(int y=0;y<a.Height;y+=5)for(int x=0;x<a.Width;x+=5){total++;var c=a.GetPixel(x,y);var d=b.GetPixel(x,y);if(Math.Abs(c.R-d.R)+Math.Abs(c.G-d.G)+Math.Abs(c.B-d.B)>60)changed++;}
                    if(changed>total*.002)foreach(var point in package.Points){point.State="unresolved";point.Reason="截图前后视图发生变化，请保持视角不动重新导出";point.X=point.Y=0;}
                }
                using(var stream=File.Create(Path.Combine(directory,"locations.xml")))new XmlSerializer(typeof(LineupLocationPackage)).Serialize(stream,package);
                var rows=new List<string[]>{new[]{"编号","型号","状态","原因","X比例","Y比例"}};rows.AddRange(package.Points.Select(p=>new[]{p.Number,p.Model,p.State,p.Reason,p.X.ToString(System.Globalization.CultureInfo.InvariantCulture),p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}));File.WriteAllText(Path.Combine(directory,"locations.csv"),LineupCsv.Write(rows),new System.Text.UTF8Encoding(true));
                return package;
            }finally{foreach(var item in selected)try{doc.SelectSet.Add(item);}catch{}view.Update();}
        }
    }
    public sealed partial class LineupTableForm {
        void ExportLocationDialog(){
            SaveDraft();var ids=SelectedIds();if(ids.Count==0)throw new InvalidOperationException("请选择本张位置图需要标注的清单行；可跨类别多选。");
            using(var d=new FolderBrowserDialog{Description="选择位置图包保存目录（会新建带时间的子目录）"})if(d.ShowDialog(this)==DialogResult.OK){
                string folder=Path.Combine(d.SelectedPath,"Lineup位置图-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));ClearHighlight();Hide();
                try{var package=LineupLocation.Export(app,assembly,project,ids,folder);status.Text="已导出："+folder+"；识别 "+package.Points.Count(p=>p.State=="ok")+" / "+package.Points.Count+"。请核对位置后导入 WPS。";}
                finally{Show();}
            }
        }
    }
}
