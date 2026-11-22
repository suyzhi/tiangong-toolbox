using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TianGongCadSuite {
    // 脱离 WinForms 的整数矩形与窗口位置/列宽序列化纯逻辑，方便在 tests/pure 与 PanelTests.exe 中直接覆盖。
    public struct RectI {
        public readonly int X, Y, W, H;
        public RectI(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public int Right { get { return X + W; } }
        public int Bottom { get { return Y + H; } }
    }

    public sealed class SavedWindowBounds {
        public int X, Y, W, H;
        public bool Maximized;
    }

    public static class WindowBoundsRules {
        public static string Format(SavedWindowBounds b) {
            if (b == null) return "";
            var ci = CultureInfo.InvariantCulture;
            return b.X.ToString(ci) + "," + b.Y.ToString(ci) + "," + b.W.ToString(ci) + "," + b.H.ToString(ci) + "," + (b.Maximized ? "1" : "0");
        }

        public static bool TryParse(string text, out SavedWindowBounds b) {
            b = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim().Split(',');
            if (parts.Length < 4) return false;
            int x, y, w, h;
            var ci = CultureInfo.InvariantCulture;
            if (!int.TryParse(parts[0], NumberStyles.Integer, ci, out x)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, ci, out y)) return false;
            if (!int.TryParse(parts[2], NumberStyles.Integer, ci, out w)) return false;
            if (!int.TryParse(parts[3], NumberStyles.Integer, ci, out h)) return false;
            if (w < 200 || h < 150 || w > 16000 || h > 16000) return false;
            bool max = parts.Length >= 5 && parts[4].Trim() == "1";
            b = new SavedWindowBounds { X = x, Y = y, W = w, H = h, Maximized = max };
            return true;
        }

        // 把上次记住的窗口矩形校验并收进当前可用显示器工作区，防止拔掉副屏或分辨率缩小后窗口飞出屏幕外看不见。
        public static RectI FitToScreens(RectI desired, IList<RectI> workAreas, RectI primary, int minW, int minH) {
            int w = Math.Max(minW, desired.W);
            int h = Math.Max(minH, desired.H);
            int x = desired.X;
            int y = desired.Y;

            // 先检查标题栏条带（顶部 40px）落在哪块屏幕上最多；能露出足够抓手就留在那块屏上
            int titleH = Math.Min(40, Math.Max(20, h));
            int needTitleW = Math.Min(120, Math.Max(40, w / 3));
            RectI bestScreen = primary;
            long bestOverlap = -1;
            bool foundScreen = false;

            if (workAreas != null) {
                for (int i = 0; i < workAreas.Count; i++) {
                    var s = workAreas[i];
                    if (s.W <= 0 || s.H <= 0) continue;
                    int ox = Math.Min(x + w, s.Right) - Math.Max(x, s.X);
                    int oy = Math.Min(y + titleH, s.Bottom) - Math.Max(y, s.Y);
                    if (ox >= needTitleW && oy >= 20) {
                        long area = (long)ox * oy;
                        if (area > bestOverlap) {
                            bestOverlap = area;
                            bestScreen = s;
                            foundScreen = true;
                        }
                    }
                }
            }

            if (!foundScreen) {
                // 上次所在副屏已拔掉或坐标离谱：安全居中回主屏工作区
                int fw = Math.Min(w, Math.Max(200, primary.W - 20));
                int fh = Math.Min(h, Math.Max(150, primary.H - 20));
                int fx = primary.X + Math.Max(0, (primary.W - fw) / 2);
                int fy = primary.Y + Math.Max(0, (primary.H - fh) / 2);
                return new RectI(fx, fy, fw, fh);
            }

            // 命中可用屏幕：尺寸不超该屏工作区，顶部不跑出屏幕上方，保证标题栏始终可拖回
            w = Math.Min(w, Math.Max(200, bestScreen.W));
            h = Math.Min(h, Math.Max(150, bestScreen.H));
            if (y < bestScreen.Y) y = bestScreen.Y;
            if (y > bestScreen.Bottom - 40) y = Math.Max(bestScreen.Y, bestScreen.Bottom - h);
            if (x + w < bestScreen.X + 120) x = bestScreen.X;
            if (x > bestScreen.Right - 120) x = Math.Max(bestScreen.X, bestScreen.Right - w);
            return new RectI(x, y, w, h);
        }

        // 列宽字典持久化（格式：key=width;key2=width2，值存逻辑像素，自动兼容不同 DPI）
        public static string FormatWidths(IDictionary<string, int> widths) {
            if (widths == null || widths.Count == 0) return "";
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            foreach (var kv in widths) {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value < 24 || kv.Value > 2000) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(kv.Key.Replace(";", "").Replace("=", "")).Append('=').Append(kv.Value.ToString(ci));
            }
            return sb.ToString();
        }

        public static Dictionary<string, int> ParseWidths(string raw) {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(raw)) return map;
            var ci = CultureInfo.InvariantCulture;
            foreach (var seg in raw.Split(';')) {
                int eq = seg.LastIndexOf('=');
                if (eq <= 0 || eq >= seg.Length - 1) continue;
                string key = seg.Substring(0, eq).Trim();
                int w;
                if (key.Length > 0 && int.TryParse(seg.Substring(eq + 1).Trim(), NumberStyles.Integer, ci, out w) && w >= 24 && w <= 2000) {
                    map[key] = w;
                }
            }
            return map;
        }

        // 单元格查找替换纯逻辑（支持区分 / 不区分大小写，返回替换结果与是否发生变化）
        public static string ReplaceInText(string source, string find, string replace, bool matchCase, out bool changed) {
            source = source ?? "";
            replace = replace ?? "";
            changed = false;
            if (string.IsNullOrEmpty(find) || source.Length == 0) return source;
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int idx = source.IndexOf(find, cmp);
            if (idx < 0) return source;
            var sb = new StringBuilder(source.Length);
            int pos = 0;
            while (idx >= 0) {
                sb.Append(source, pos, idx - pos);
                sb.Append(replace);
                pos = idx + find.Length;
                idx = source.IndexOf(find, pos, cmp);
            }
            sb.Append(source, pos, source.Length - pos);
            string res = sb.ToString();
            changed = !string.Equals(res, source, StringComparison.Ordinal);
            return res;
        }
    }
}
