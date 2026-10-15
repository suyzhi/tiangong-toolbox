using System;
using System.Collections.Generic;

namespace TianGongCadSuite {
    // 参考孔在连接里是什么角色（从直径反推出来的）
    public enum HoleRefRole {
        TapDrill,    // 螺纹底孔：参考件上是螺纹孔，螺钉从目标件这边穿过来
        Clearance    // 过孔：参考件上是过孔，螺钉穿过参考件拧进/穿过目标件
    }

    // 一条配做规则：规格（"*" = 全部）+ 参考孔角色 → 目标件打什么孔、过孔用哪档配合。
    public sealed class HoleRule {
        public string Size = HoleRuleSet.AnySize;
        public HoleRefRole Role;
        public HoleKind Target;
        public string Fit = HoleStandards.Normal;   // 目标孔是通孔/沉孔/锥沉时，孔径取天工表「常规螺钉间隙」的哪一档
        public HoleRule Clone(){ return (HoleRule)MemberwiseClone(); }
    }

    // 一套配做规则。找规则时先找这个规格专门的那一行，没有再用「全部」那一行，再没有就用内置默认。
    public sealed class HoleRuleSet {
        public const string AnySize = "*";
        public string Name = "";
        public readonly List<HoleRule> Rules = new List<HoleRule>();

        public HoleRule For(string size, HoleRefRole role){
            HoleRule any = null;
            foreach (var r in Rules) {
                if (r.Role != role) continue;
                if (string.Equals(r.Size, size, StringComparison.OrdinalIgnoreCase)) return r;
                if (r.Size == AnySize && any == null) any = r;
            }
            if (any != null) return any;
            // 规则表里两种角色总得有个兜底：没有就按内置默认（沉头螺钉）
            return role == HoleRefRole.TapDrill
                ? new HoleRule { Role = role, Target = HoleKind.Counterbore }
                : new HoleRule { Role = role, Target = HoleKind.Tapped };
        }

        public HoleRuleSet Clone(){
            var c = new HoleRuleSet { Name = Name };
            foreach (var r in Rules) c.Rules.Add(r.Clone());
            return c;
        }

        // 两套规则内容是否一样（用来判断"改过以后还是不是某个预设"）
        public bool SameRules(HoleRuleSet other){
            if (other == null || other.Rules.Count != Rules.Count) return false;
            for (int i = 0; i < Rules.Count; i++) {
                var a = Rules[i]; var b = other.Rules[i];
                if (!string.Equals(a.Size, b.Size, StringComparison.OrdinalIgnoreCase) || a.Role != b.Role || a.Target != b.Target || a.Fit != b.Fit) return false;
            }
            return true;
        }

        // 一句话："螺纹底孔 → 圆柱沉孔，过孔 → 螺纹孔（M8：过孔 → 通孔）"
        public string Describe(){
            var any = new List<string>(); var special = new List<string>();
            foreach (var r in Rules) {
                string s = HoleRuleBook.RoleName(r.Role) + " → " + HoleSpec.KindName(r.Target);
                if (r.Size == AnySize) any.Add(s); else special.Add(r.Size + "：" + s);
            }
            string t = string.Join("，", any.ToArray());
            if (special.Count > 0) t += "（" + string.Join("；", special.ToArray()) + "）";
            return t;
        }

        // 规则表本身有没有问题（空串 = 没问题）。界面「确定」前检查。
        public string Problem(){
            bool tap = false, clr = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Rules) {
                if (string.IsNullOrEmpty(r.Size)) return "有一行没填规格。";
                if (!seen.Add(r.Size + "|" + r.Role)) return (r.Size == AnySize ? "「全部规格」" : r.Size) + " 的「参考孔是" + HoleRuleBook.RoleName(r.Role) + "」写了两行，只能留一行。";
                if (r.Size == AnySize && r.Role == HoleRefRole.TapDrill) tap = true;
                if (r.Size == AnySize && r.Role == HoleRefRole.Clearance) clr = true;
                if (Array.IndexOf(HoleRuleBook.Fits, r.Fit) < 0) return r.Size + " 的过孔配合「" + r.Fit + "」不认识。";
            }
            if (!tap || !clr) return "「全部规格」要各有一行：参考孔是螺纹底孔、参考孔是过孔（具体规格不写的就按它配）。";
            return "";
        }
    }

    // 配做规则：内置预设 + 当前用的那一套（存在 %LOCALAPPDATA%\TianGongCadSuite\hole-rules.tsv）。
    //
    // 为什么要有：原来规则写死成"参考孔是螺纹底孔 → 配圆柱沉孔，是过孔 → 配螺纹孔"（内六角螺钉沉头的用法）。
    // 但型材/钣金上同样常见的还有：
    //   · 螺钉头直接压在板面上（不沉头）：底孔 → 过孔；
    //   · 沉头（锥头）螺钉：底孔 → 锥形沉孔；
    //   · 螺栓 + 螺母穿两块板：两边都是过孔。
    // 原来只能每组手动切孔型，现在选一套预设就行，也可以按规格单独改（例如 M8 以上一律用螺栓）。
    //
    // 纯逻辑（tests/pure 里测）。插件启动时不读文件，用到时才 LoadFromDisk；测试里直接设 Current。
    public static class HoleRuleBook {
        public const string FileName = "hole-rules.tsv";
        public static readonly string[] Fits = { HoleStandards.Close, HoleStandards.Normal, HoleStandards.Loose };

        public static HoleRuleSet[] Presets {
            get {
                return new[]{
                    Preset("沉头螺钉（默认）", HoleKind.Counterbore, HoleKind.Tapped),
                    Preset("螺钉头露在外面", HoleKind.Through, HoleKind.Tapped),
                    Preset("锥形沉头螺钉", HoleKind.Countersink, HoleKind.Tapped),
                    Preset("螺栓 + 螺母", HoleKind.Through, HoleKind.Through),
                };
            }
        }
        static HoleRuleSet Preset(string name, HoleKind tapTarget, HoleKind clearanceTarget){
            var s = new HoleRuleSet { Name = name };
            s.Rules.Add(new HoleRule { Size = HoleRuleSet.AnySize, Role = HoleRefRole.TapDrill, Target = tapTarget });
            s.Rules.Add(new HoleRule { Size = HoleRuleSet.AnySize, Role = HoleRefRole.Clearance, Target = clearanceTarget });
            return s;
        }

        static HoleRuleSet current;
        public static HoleRuleSet Current {
            get { return current ?? (current = Presets[0]); }
            set { current = value; }
        }

        // 预设名对上、内容也一样，才算"在用某个预设"；改过就叫"自定义"
        public static string NameOf(HoleRuleSet set){
            if (set == null) return "";
            foreach (var p in Presets) if (p.SameRules(set)) return p.Name;
            return "自定义";
        }

        public static string RoleName(HoleRefRole r){ return r == HoleRefRole.TapDrill ? "螺纹底孔" : "过孔"; }

        // ---- 文件 ----
        // 每行：规则 \t 规格（* = 全部）\t 参考孔是（螺纹底孔/过孔）\t 配做（螺纹孔/圆柱沉孔/锥形沉孔/通孔）\t 过孔配合
        public static HoleRuleSet Load(string path){
            var rows = SettingsFile.Read(path);
            if (rows.Count == 0) return null;
            var s = new HoleRuleSet { Name = "自定义" };
            foreach (var r in rows) {
                if (r.Length >= 2 && r[0] == "名称") { s.Name = r[1]; continue; }
                if (r.Length < 4 || r[0] != "规则") continue;
                var rule = new HoleRule { Size = r[1].Trim().Length == 0 ? HoleRuleSet.AnySize : r[1].Trim() };
                if (r[2] == "过孔") rule.Role = HoleRefRole.Clearance; else if (r[2] == "螺纹底孔") rule.Role = HoleRefRole.TapDrill; else continue;
                if (!TryKind(r[3], out rule.Target)) continue;
                rule.Fit = r.Length >= 5 && Array.IndexOf(Fits, r[4]) >= 0 ? r[4] : HoleStandards.Normal;
                s.Rules.Add(rule);
            }
            return s.Rules.Count == 0 ? null : s;
        }

        public static void Save(HoleRuleSet set, string path){
            var rows = new List<string[]>();
            rows.Add(new[]{ "名称", NameOf(set) });
            foreach (var r in set.Rules) rows.Add(new[]{ "规则", r.Size, RoleName(r.Role), HoleSpec.KindName(r.Target), r.Fit });
            SettingsFile.Write(path, rows,
                "天工工具箱 · 自动打孔的配做规则（可以用记事本改）",
                "每行：规则<TAB>规格（* = 全部规格）<TAB>参考孔是（螺纹底孔/过孔）<TAB>配做（螺纹孔/圆柱沉孔/锥形沉孔/通孔）<TAB>过孔配合（紧密 (H12)/普通 (H13)/宽松 (H14)）",
                "找规则时先找这个规格专门的那一行，没有再用 * 那一行。");
        }

        public static bool TryKind(string name, out HoleKind kind){
            foreach (HoleKind k in Enum.GetValues(typeof(HoleKind))) if (HoleSpec.KindName(k) == name) { kind = k; return true; }
            kind = HoleKind.Through; return false;
        }

        // 插件里用：从用户目录读一次（读不到/坏了就用默认，不打扰用户）
        static bool loaded;
        public static void LoadFromDisk(){
            if (loaded) return;
            loaded = true;
            try {
                var s = Load(SettingsFile.PathOf(FileName));
                if (s != null && s.Problem().Length == 0) current = s;
            } catch (Exception e) { Log.Write("HoleRulesLoad", e); }
        }
        public static void SaveToDisk(HoleRuleSet set){
            Save(set, SettingsFile.PathOf(FileName));
            current = set;
            loaded = true;
        }
    }
}
