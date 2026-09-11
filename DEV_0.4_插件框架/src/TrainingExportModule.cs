using System;
using System.Collections.Generic;
using System.Windows.Forms;
namespace TianGongCadSuite {
    public sealed class TrainingExportModule:IToolModule {
        readonly ToolContext context;
        public TrainingExportModule(ToolContext c){context=c;}
        public string Id {get{return "training-export";}}
        bool Available(){try{object d=context.Application.ActiveDocument;return d is SolidEdgeDraft.DraftDocument || d is SolidEdgePart.PartDocument || d is SolidEdgePart.SheetMetalDocument || d is SolidEdgeAssembly.AssemblyDocument;}catch{return false;}}
        public IEnumerable<ToolCommand> Commands {get{yield return new ToolCommand(4,"导出出图训练数据","导出模型特征、几何和工程图标注","0.2：导出特征与孔参数、面边拓扑、带原生面映射的网格、尺寸布局及三维引用。JSON 默认脱敏，并生成 export_summary.json 校验报告。",Available,Export);}}
        void Export(){using(var dialog=new FolderBrowserDialog{Description="选择训练数据输出目录（每次建立独立样本文件夹）",ShowNewFolderButton=true}){if(dialog.ShowDialog()!=DialogResult.OK)return;Cursor.Current=Cursors.WaitCursor;try{var exporter=new TrainingExporter(context.Application);string path=exporter.Export(context.Application.ActiveDocument,dialog.SelectedPath);MessageBox.Show("状态："+exporter.LastStatus+"\n导出记录已写入：\n"+path+"\n\n请检查 sample.json 中 extraction_status 和 issues；样本需审核后才能用于训练。","训练数据导出");}finally{Cursor.Current=Cursors.Default;}}}
        public void Dispose(){}
    }
}
