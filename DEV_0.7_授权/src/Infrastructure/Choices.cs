using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 各窗口记住的勾选（0.10.2）：格式转换的类型 / 子文件夹 / 镜像 / 覆盖 / 校验、配孔检查显示哪几类、
    // 批量改名跟不跟着改同名工程图、属性卡补不补缺的属性 —— 原来每次打开窗口都回到默认，用户每次重新勾。
    // 存在 %LOCALAPPDATA%\TianGongCadSuite\choices.tsv（每行：窗口.名字<TAB>值）。只在用户自己点的时候存（Click，代码改 Checked 不触发）；
    // 真机测试脚本（放行授权闸的那些）一律用默认值，免得用户的设置让测试走岔（同 autohole.tsv，0.8.5）。
    public static class Choices {
        const string FileName = "choices.tsv";
        static Dictionary<string, string> cache;

        static bool TestRun {
            get {
#if TG_DEV_BUILD
                return TianGongCadSuite.Licensing.LicenseTestHooks.GateOverride >= 0;
#else
                return false;
#endif
            }
        }
        static Dictionary<string, string> All(){
            if (cache != null) return cache;
            cache = new Dictionary<string, string>(StringComparer.Ordinal);
            try { foreach (var r in SettingsFile.Read(SettingsFile.PathOf(FileName))) if (r.Length >= 2) cache[r[0]] = r[1]; }
            catch (Exception e) { Log.Write("ChoicesLoad", e.Message); }
            return cache;
        }
        public static string Get(string key, string fallback){
            if (TestRun) return fallback;
            string v; return All().TryGetValue(key, out v) ? v : fallback;
        }
        public static void Set(string key, string value){
            if (TestRun) return;
            var all = All();
            all[key] = value ?? "";
            try {
                var rows = new List<string[]>();
                foreach (var kv in all) rows.Add(new[]{ kv.Key, kv.Value });
                SettingsFile.Write(SettingsFile.PathOf(FileName), rows, "天工工具箱 · 各窗口记住的勾选（每行：窗口.名字<TAB>值）。删掉本文件恢复默认。");
            } catch (Exception e) { Log.Write("ChoicesSave", e.Message); }
        }

        // 勾选框：按记下的值设好；用户点它时存
        public static void Bind(CheckBox box, string key){
            box.Checked = Get(key, box.Checked ? "1" : "0") == "1";
            box.Click += (s, e) => Set(key, box.Checked ? "1" : "0");
        }
    }
}
