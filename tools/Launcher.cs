using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TianGongPanel;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;
class Launcher {
    [STAThread]static void Main(){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);using(new OleFilter())try{
        var app=(F.Application)Marshal.GetActiveObject("SolidEdge.Application");var asm=app.ActiveDocument as A.AssemblyDocument;if(asm==null)throw new InvalidOperationException("请先打开天工 CAD 装配文件。");
        foreach(F.AddIn addin in app.AddIns){if(string.Equals(addin.GUID,"{98BF0FA8-7D65-4A13-9926-6A45836FD6D2}",StringComparison.OrdinalIgnoreCase)){addin.Connect=true;((dynamic)addin.Object).OpenPanel();return;}}
        throw new InvalidOperationException("请先安装插件并重启天工 CAD，再运行启动器。");
    }catch(Exception e){Log.Write("Launcher",e);MessageBox.Show(e.Message,"矩形板工具",MessageBoxButtons.OK,MessageBoxIcon.Error);}}
}
