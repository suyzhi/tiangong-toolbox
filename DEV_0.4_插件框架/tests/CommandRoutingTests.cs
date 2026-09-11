using System;
using System.Collections.Generic;
using TianGongCadSuite;
static class CommandRoutingTests {
    sealed class Module:IToolModule {
        public string Id {get{return "routing-test";}}
        public IEnumerable<ToolCommand> Commands {get{for(int i=1;i<=3;i++)yield return new ToolCommand(i,"Test "+i,"","",()=>true,()=>{});}}
        public void Dispose(){}
    }
    public static void Pure(){
        using(var registry=new ToolRegistry()){
            registry.Add(new Module());registry.Bind(new int[]{45001,45002,45003});
            for(int i=1;i<=3;i++)if(registry.Find(i)==null||registry.Find(i).Id!=i||registry.NativeId(i)!=45000+i)throw new Exception("Callback local ID / CAD runtime ID mapping failed");
            if(registry.Find(45001)!=null||registry.Find(99)!=null)throw new Exception("Runtime or unknown ID accepted as a callback ID");
            registry.Bind(new int[]{45003,45001,45002});
            if(registry.Find(1).Id!=1||registry.NativeId(1)!=45003)throw new Exception("Re-registration changed callback identity");
            Console.WriteLine("PASS: local callback IDs remain stable while native runtime IDs change");
        }
    }
}
