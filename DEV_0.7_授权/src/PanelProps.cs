using System.Collections.Generic;

namespace TianGongCadSuite {
    // 生成内嵌板时要写进零件文件的东西（材质等），以及写的过程中没成功的地方（给界面报出来）。
    public sealed class PanelProps {
        public string Material = "";
        public readonly List<string> Problems = new List<string>();
        // 同样的问题每块板都会碰到一次，只记一条
        public void Problem(string text){ if (!Problems.Contains(text)) Problems.Add(text); }
        // 这一批里没生成出来的框口（specs 里的下标，0.8.5）：其余照常生成，窗口据此只把生成了的标成「已生成」
        public readonly List<int> Failed = new List<int>();
    }
}
