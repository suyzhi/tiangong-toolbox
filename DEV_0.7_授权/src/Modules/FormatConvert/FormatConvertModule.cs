using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // Locates the stand-alone worker host. Inside CAD the current process is TianGong.exe, so the
    // worker must be resolved from the plug-in folder instead of Application.ExecutablePath.
    public static class ConverterHost {
        public const string ExeName="TianGongConverter.exe";
        public static string Resolve(){
            try{
                string folder=Path.GetDirectoryName(typeof(ConverterHost).Assembly.Location);
                string candidate=Path.Combine(folder,ExeName);
                if(File.Exists(candidate))return candidate;
            }catch{}
            string configured=Environment.GetEnvironmentVariable("TIANGONG_CONVERTER_EXE");
            if(configured!=null&&configured.Length>0&&File.Exists(configured))return configured;
            try{
                string current=Assembly.GetEntryAssembly()==null?null:Assembly.GetEntryAssembly().Location;
                if(current!=null&&string.Equals(Path.GetFileName(current),ExeName,StringComparison.OrdinalIgnoreCase))return current;
            }catch{}
            return null;
        }
        public static void OpenStandalone(string initialFolder){
            string exe=Resolve();
            if(exe==null){ MessageBox.Show("找不到 "+ExeName+"。请先运行安装脚本编译插件，或直接把该程序放在插件目录下。","天工格式转换",MessageBoxButtons.OK,MessageBoxIcon.Warning); return; }
            try{
                var info=new ProcessStartInfo(exe);
                if(initialFolder!=null&&initialFolder.Length>0)info.Arguments=CommandLine.Join("--input",initialFolder);
                Process.Start(info);
            }catch(Exception e){ MessageBox.Show("无法启动转换器："+e.Message,"天工格式转换",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        }
    }
    public sealed class FormatConvertModule:IToolModule {
        readonly ToolContext context;
        public FormatConvertModule(ToolContext value){ context=value; }
        public string Id {get{return "format-convert";}}
        public IEnumerable<ToolCommand> Commands {get{
            yield return new ToolCommand(5,"批量格式转换","批量把 SolidWorks / STEP 转换成天工 asm/par",
                "用天工CAD自带的转换器打开 .SLDASM/.SLDPRT/.SLDDRW 和 .stp/.step，把转换结果"
                +"（asm/par/psm/dft）连同全部子装配和零件自动保存到指定目录，支持断点续做。"
                +"STEP 文件按导入结果自动决定输出是 .par 还是 .asm。"
                +"转换在独立的隐藏 CAD 进程中进行，不占用当前窗口；同目录下的 "+ConverterHost.ExeName+" 是独立运行版本。",
                delegate{ return true; },Execute);
        }}
        // 已经开着的转换窗口（0.10.3）：它不走 ToolContext（转换在后台跑好久，不能因为点了别的命令就关掉），
        // 原来每点一次功能区按钮就多开一个 —— 两个窗口各起一批隐藏的天工往同一个输出目录写。再点就把开着的那个提到前面
        FormatConvertForm open;
        void Execute(){
            if(open!=null&&!open.IsDisposed){
                try{ if(open.WindowState==FormWindowState.Minimized)open.WindowState=FormWindowState.Normal; open.Activate(); }catch{}
                return;
            }
            string initial=null;
            try{
                object document=context.Application.ActiveDocument;
                var model=document as SolidEdgeFramework.SolidEdgeDocument;
                if(model!=null){ string full=model.FullName; if(full!=null&&full.Length>0)initial=Path.GetDirectoryName(full); }
            }catch{}
            var form=new FormatConvertForm();
            form.PrefillInput(initial);
            open=form;
            form.FormClosed+=delegate{ if(open==form)open=null; };
            form.Show(new CadOwner(context.Application.ActiveFramehWnd));
        }
        public void Dispose(){}
    }
}
