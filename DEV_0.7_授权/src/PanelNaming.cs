using System;
namespace TianGongCadSuite {
    // 内嵌板（型材自动填充）的命名规则。单独放一个不依赖 CAD 的类，好让它能被纯逻辑测试盯住。
    //
    // 为什么文件名里一定要带批次时间戳：
    //   天工 CAD 的 Occurrences.AddByFilename 是**按文件名做文件查找**的，不是"只认你给的那条绝对路径"。
    //   装配所在目录（CAD 的文件查找路径）里如果存在同名零件——用户把上一批生成的板子拷回装配目录
    //   就会这样——插进来的会是那个旧零件：位置取新算的矩阵（对），几何却是旧文件的（错）。
    //   现场看到的就是"板子错位、大小也不对"，而且 CAD 不报任何错。
    //   时间戳让每批文件名天然唯一，从根上避开同名解析；万一还是撞上，
    //   CadBuilder.Generate 里还有一道"实例实际绑定的文档必须等于刚生成的文件"的护栏。
    public static class PanelNaming {
        public const string Prefix = "填充板_";
        public const string BatchPrefix = "自动填充_";
        // 批次时间戳：同一秒内连点两次由目录名里的随机后缀区分，文件名只需保证"跨批次不重名"。
        public static string Stamp(DateTime now){ return now.ToString("yyyyMMdd_HHmmss"); }
        public static string BatchDirectoryName(string stamp,string token){ return BatchPrefix + stamp + "_" + token; }
        public static string FileName(string stamp,int index){ return Prefix + stamp + "_" + index.ToString("D3") + ".par"; }
    }
}
