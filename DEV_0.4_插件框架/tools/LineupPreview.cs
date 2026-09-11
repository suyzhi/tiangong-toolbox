using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TianGongCadSuite;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
class LineupPreview {
    [STAThread] static void Main(){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using(new OleFilter())try{
            var app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application");
            var doc=app.ActiveDocument as A.AssemblyDocument;
            if(doc==null)throw new InvalidOperationException("请先在天工 CAD 打开独立测试装配副本，再运行 LineupPreview。");
            Application.Run(new LineupTableForm(app,doc));
        }catch(Exception ex){MessageBox.Show(ex.Message,"Lineup 开发版预览");}
    }
}
