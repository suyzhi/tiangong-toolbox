using System;
using System.IO;
using System.Windows.Forms;
using TianGongCadSuite;

static class ConverterMain {
    [STAThread]
    static int Main(string[] args){
        if(args.Length>2&&args[0]=="--worker")return ConvertWorker.Run(args);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form=new FormatConvertForm();
        for(int i=0;i<args.Length-1;i++)if(args[i]=="--input")form.PrefillInput(args[i+1]);
        Application.Run(form);
        return 0;
    }
}
