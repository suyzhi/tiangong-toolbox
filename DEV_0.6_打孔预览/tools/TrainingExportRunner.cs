using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using TianGongCadSuite;
using F=SolidEdgeFramework;
class TrainingExportRunner {
    [STAThread]static int Main(string[] args){
        if(args.Length<2){Console.Error.WriteLine("Usage: TrainingExportRunner OUTPUT_DIRECTORY INPUT_FILE [INPUT_FILE...]. Creates an independent CAD instance; refuses if CAD is already running.");return 2;}
        if(Process.GetProcessesByName("TianGong").Length>0){Console.Error.WriteLine("CAD is already running. Use the plugin command, or save and close CAD before batch export.");return 2;}
        F.Application app=null;bool owned=false;
        try{using(new OleFilter()){
            app=(F.Application)Activator.CreateInstance(Type.GetTypeFromProgID("SolidEdge.Application"));
            if(app.Documents.Count!=0)throw new InvalidOperationException("CAD instance contains documents; leaving it untouched.");owned=true;app.Visible=true;app.DisplayAlerts=false;
            string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
            int resultCode=0;for(int i=1;i<args.Length;i++){
                string input=Path.GetFullPath(args[i]);string hash=TrainingExporter.Hash(input);
                // Stage a unique file copy; referenced models are read through CAD links.
                string stage=Path.Combine(output,"inputs",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
                string copy=Path.Combine(stage,Path.GetFileName(input));File.Copy(input,copy,false);
                object doc=null;
                try{doc=app.Documents.Open(copy);Console.WriteLine("OPEN "+input);
                    foreach(F.AddIn addin in app.AddIns)if(addin.GUID=="{8C05165C-65A4-4EF2-A138-508589D82004}"){addin.Connect=true;dynamic diagnostic=addin.Object;Console.WriteLine("PLUGIN "+diagnostic.MenuStatus+" EXPORT_FLAGS="+diagnostic.CommandFlags(4)+" LIBRARY="+diagnostic.LoadedLibrary);}
                    var exporter=new TrainingExporter(app);string result=exporter.Export(doc,output);Console.WriteLine("SAMPLE "+result);Console.WriteLine("STATUS "+exporter.LastStatus);if(exporter.LastStatus=="failed")resultCode=1;else if(exporter.LastStatus=="partial"&&resultCode==0)resultCode=3;}
                catch(Exception e){resultCode=1;Console.Error.WriteLine("FAILED "+input+": "+e.Message);File.WriteAllText(Path.Combine(stage,"error.txt"),e.ToString());}
                finally{if(doc!=null)((dynamic)doc).Close(false);if(TrainingExporter.Hash(input)!=hash)throw new IOException("Source hash changed: "+input);}
            }
            return resultCode;
        }}catch(Exception e){Console.Error.WriteLine(e);return 1;}finally{if(owned&&app!=null)try{app.Quit();}catch{}if(app!=null)Marshal.ReleaseComObject(app);}
    }
}
