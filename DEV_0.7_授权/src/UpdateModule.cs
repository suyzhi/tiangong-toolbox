using System.Collections.Generic;

namespace TianGongCadSuite {
    // 检查更新（0.9.1 新增，命令 11）：手动问一次更新服务器；平时 CAD 启动后在后台自动查，见 AutoUpdate。
    // 不经过授权闸：授权有问题时也要能装上修好了的新版本。
    public sealed class UpdateModule : IToolModule {
        public const int CommandId = 11;
        readonly ToolContext context;
        public UpdateModule(ToolContext value){ context = value; }
        public string Id { get { return "update"; } }
        public IEnumerable<ToolCommand> Commands { get {
            yield return new ToolCommand(CommandId, "检查更新", "看看有没有天工工具箱的新版本",
                "问一次更新服务器有没有新版本（平时打开天工后会自动查，有新版本时点任意工具箱命令就会提示）。\r\n"
                + "有新版本可以下载，关闭天工后自动安装；也可以「稍后提醒」或「跳过这个版本」。",
                delegate { return true; },
                () => AutoUpdate.CheckNowInteractive(new CadOwner(context.Application.ActiveFramehWnd)));
        } }
        public void Dispose(){}
    }
}
