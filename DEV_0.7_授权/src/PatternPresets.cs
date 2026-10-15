using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // 批量排孔的预设：常用的一套参数（排布方式、孔数/间距、边距、横向偏移、孔型规格……）存个名字，下次一点就填好。
    // 存在 %LOCALAPPDATA%\TianGongCadSuite\pattern-presets.tsv，一行一个参数：预设名 \t 参数名 \t 值。
    // 只管"名字 → 一组键值"，具体有哪些键由窗口决定（窗口加了新参数，旧预设里没有的键就保持界面当前值）。
    public sealed class PatternPreset {
        public string Name = "";
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public string Get(string key, string fallback){ string v; return Values.TryGetValue(key, out v) ? v : fallback; }
    }

    public sealed class PatternPresetStore {
        public const string FileName = "pattern-presets.tsv";
        public readonly List<PatternPreset> Items = new List<PatternPreset>();
        readonly string path;

        public PatternPresetStore(string filePath){ path = filePath; }
        public static PatternPresetStore Default(){ return new PatternPresetStore(SettingsFile.PathOf(FileName)); }

        public PatternPresetStore Load(){
            Items.Clear();
            foreach (var r in SettingsFile.Read(path)) {
                if (r.Length < 3 || r[0].Trim().Length == 0) continue;
                var p = Find(r[0]);
                if (p == null) { p = new PatternPreset { Name = r[0].Trim() }; Items.Add(p); }
                p.Values[r[1]] = r[2];
            }
            return this;
        }

        public void Save(){
            var rows = new List<string[]>();
            foreach (var p in Items) foreach (var kv in p.Values) rows.Add(new[]{ p.Name, kv.Key, kv.Value });
            SettingsFile.Write(path, rows, "天工工具箱 · 批量排孔预设（可以用记事本改；每行：预设名<TAB>参数名<TAB>值）");
        }

        public PatternPreset Find(string name){
            if (name == null) return null;
            string n = name.Trim();
            foreach (var p in Items) if (string.Equals(p.Name, n, StringComparison.Ordinal)) return p;
            return null;
        }

        // 同名覆盖
        public void Put(PatternPreset preset){
            if (preset == null || preset.Name.Trim().Length == 0) throw new ArgumentException("预设名不能为空。");
            preset.Name = preset.Name.Trim();
            for (int i = 0; i < Items.Count; i++) if (Items[i].Name == preset.Name) { Items[i] = preset; return; }
            Items.Add(preset);
        }

        public bool Remove(string name){
            var p = Find(name);
            return p != null && Items.Remove(p);
        }
    }
}
