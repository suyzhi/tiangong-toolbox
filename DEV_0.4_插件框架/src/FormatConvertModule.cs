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
                if(initialFolder!=null&&initialFolder.Length>0)info.Arguments="--input \""+initialFolder+"\"";
                Process.Start(info);
            }catch(Exception e){ MessageBox.Show("无法启动转换器："+e.Message,"天工格式转换",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        }
    }
    public sealed class FormatConvertModule:IToolModule {
        readonly ToolContext context;
        public FormatConvertModule(ToolContext value){ context=value; }
        public string Id {get{return "format-convert";}}
        public IEnumerable<ToolCommand> Commands {get{
            yield return new ToolCommand(5,"SolidWorks 批量转换","批量把 SolidWorks 装配/零件转换成天工 asm/par",
                "用天工CAD自带的转换器打开 .SLDASM/.SLDPRT，并把转换结果（asm/par/psm）自动保存到指定目录，"
                +"支持多进程并行与断点续做。转换在独立的隐藏 CAD 进程中进行，不占用当前窗口。"
                +"同目录下的 "+ConverterHost.ExeName+" 是独立运行版本。",
                delegate{ return true; },Execute);
        }}
        void Execute(){
            string initial=null;
            try{
                object document=context.Application.ActiveDocument;
                var model=document as SolidEdgeFramework.SolidEdgeDocument;
                if(model!=null){ string full=model.FullName; if(full!=null&&full.Length>0)initial=Path.GetDirectoryName(full); }
            }catch{}
            var form=new FormatConvertForm();
            form.PrefillInput(initial);
            form.Show(new CadOwner(context.Application.ActiveFramehWnd));
        }
        public void Dispose(){}
    }
}
