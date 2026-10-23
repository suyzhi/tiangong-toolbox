using System;
using System.Collections.Generic;
using System.Globalization;

namespace TianGongCadSuite {
    // 自动打孔生成的特征叫什么名字。
    //
    // 原来全靠 CAD 默认命名："4.917 简单孔 1""4.917 简单孔 2""螺纹孔 7"……特征树里一长串，
    // 看不出哪个是 M6 螺纹孔、哪个是 M8 沉孔。现在：
    //   · 同一张面、同一个规格的孔编成一个特征组（FeatureGroup），组名 = 规格短名 + " ×N"，
    //     例如「M6 螺纹孔（螺纹全长）×4」；
    //   · 组里每个孔 = 规格短名 + "-序号"，例如「M6 螺纹孔（螺纹全长）-1」；
    //   · 只有一个孔时不建组，孔本身就叫规格短名。
    // 0.7.9 起同规格的一批孔合成一个多孔孔特征（天工自己的 UserDefinedPattern 结构，见 HoleCombine），
    // 它叫「规格 ×N」、里面的孔叫「规格-序号」；同一张面再打一批时两个多孔特征编成一组「规格 ×总数」。
    // 合不成（或者要靠属性集关联）时退回上面的"单孔 + 特征组"。
    //
    // 纯逻辑：不碰 CAD，tests 直接断言。
    public static class HoleNaming {
        public const int MaxLength = 60;

        // 一个规格的短名：一眼看出孔型、规格/孔径、深度。
        public static string Short(HoleSpec s){
            if (s == null) return "孔";
            string size = s.ThreadSize ?? "";
            string name;
            switch (s.Kind) {
                case HoleKind.Tapped:
                    name = size.Length > 0 ? size + " 螺纹孔" : "螺纹孔 Φ" + N(s.HoleDiameter);
                    if (!s.Through) name += " 孔深" + N(s.Depth);
                    name += "（" + s.ThreadText + "）";
                    break;
                case HoleKind.Counterbore:
                    name = (size.Length > 0 ? size + " " : "") + "沉孔 Φ" + N(s.HoleDiameter) + "/Φ" + N(s.CounterboreDiameter) + " 深" + N(s.CounterboreDepth);
                    if (!s.Through) name += " 孔深" + N(s.Depth);
                    break;
                case HoleKind.Countersink:
                    name = (size.Length > 0 ? size + " " : "") + "锥形沉孔 Φ" + N(s.HoleDiameter) + "/Φ" + N(s.CountersinkDiameter) + " " + N(s.CountersinkAngle) + "°";
                    if (!s.Through) name += " 孔深" + N(s.Depth);
                    break;
                default:
                    name = s.Through ? "通孔 Φ" + N(s.HoleDiameter) : "盲孔 Φ" + N(s.HoleDiameter) + " 深" + N(s.Depth);
                    break;
            }
            if (s.Chamfer && s.Kind != HoleKind.Countersink) name += " 倒角" + N(s.ChamferSetback);
            return Clip(name);
        }

        public static string Group(HoleSpec s, int count){ return Clip(Short(s) + " ×" + count); }
        public static string Member(HoleSpec s, int index){ return Clip(Short(s) + "-" + index); }
        // 批量排孔的阵列特征（种子孔照常叫「…-1」，阵列出来的其余孔都在这个特征里）
        public static string PatternName(HoleSpec s){ return Clip(Short(s) + " 阵列"); }

        // 孔位链接组（0.8.4）：一张面这次打的关联孔 + 它们的孔位链接。名字说清楚"是什么孔、跟着谁"：
        //   一种规格：「关联孔 M6 螺纹孔（螺纹全长） ×4（跟随 支撑条）」
        //   几种规格：「关联孔 6 个（2 种规格，跟随 支撑条）」
        //   几个参考零件：「…（跟随 支撑条 等 2 个零件）」
        // specs：规格 + 这次打的个数（按出现顺序）；refs：参考零件名（去扩展名，重复的只算一次）
        public static string LinkGroup(IList<KeyValuePair<HoleSpec, int>> specs, IList<string> refs){
            var names = new List<string>();
            if (refs != null) foreach (var r in refs) { string n = (r ?? "").Trim(); if (n.Length > 0 && !names.Contains(n)) names.Add(n); }
            string follow = names.Count == 0 ? "" : "（跟随 " + names[0] + (names.Count > 1 ? " 等 " + names.Count + " 个零件" : "") + "）";
            int total = 0, kinds = 0;
            HoleSpec only = null;
            if (specs != null) foreach (var kv in specs) { if (kv.Value <= 0) continue; total += kv.Value; kinds++; only = kv.Key; }
            string what = kinds == 1 ? Short(only) + " ×" + total : total + " 个" + (kinds > 1 ? "（" + kinds + " 种规格）" : "");
            if (kinds > 1 && follow.Length > 0) what = total + " 个（" + kinds + " 种规格，" + follow.Substring(1);
            else what += follow;
            return Clip("关联孔 " + what);
        }

        // 在已有名字里找一个不重名的：base、base (2)、base (3)……
        // 特征重名时 CAD 的行为没有实测保证（可能拒绝、可能静默加后缀），所以自己先避开。
        public static string Unique(string wanted, ICollection<string> taken){
            if (taken == null || !taken.Contains(wanted)) return wanted;
            for (int i = 2; i < 1000; i++) {
                string c = wanted + " (" + i + ")";
                if (!taken.Contains(c)) return c;
            }
            return wanted + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // 组成员的序号：从 1 开始，跳过已经被占用的名字（同一零件上第二批同规格孔接着往下编号）。
        public static List<string> Members(HoleSpec s, int count, ICollection<string> taken){
            var result = new List<string>();
            var used = new HashSet<string>(taken ?? new string[0]);
            int k = 1;
            while (result.Count < count && k < 100000) {
                string n = Member(s, k++);
                if (used.Contains(n)) continue;
                used.Add(n);
                result.Add(n);
            }
            return result;
        }

        // 同规格判定用的键：规格短名 + 会影响几何、但短名里没写的那几项（孔底、倒角角度）。
        public static string SpecKey(HoleSpec s){
            if (s == null) return "";
            return Short(s) + "|" + s.Bottom + "|" + N(s.BottomAngle) + "|" + N(s.ChamferAngle);
        }

        static string Clip(string s){ return s.Length <= MaxLength ? s : s.Substring(0, MaxLength); }
        static string N(double v){ return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }
}
