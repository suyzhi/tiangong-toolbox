using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using F=SolidEdgeFramework;
using A=SolidEdgeAssembly;

namespace TianGongCadSuite {
    // 一批框口 → 一批独立的 PAR（每个框口一个文件），插进装配。任何一块失败，本批已插入的全部撤回。
    public static class BatchPanels {
        public static string Generate(F.Application app, A.AssemblyDocument assembly, IList<PanelSpec> specs, string parent){
            return Generate(app, assembly, specs, parent, null);
        }

        public static string Generate(F.Application app, A.AssemblyDocument assembly, IList<PanelSpec> specs, string parent, PanelProps props){
            if (specs == null || specs.Count == 0) throw new ArgumentException("没有待生成框口。");
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
            // 文件名必须带批次时间戳（原因见 PanelNaming 的注释：CAD 会按文件名把装配目录里的旧同名零件插进来）。
            string stamp = PanelNaming.Stamp(DateTime.Now);
            Func<int, string> name = i => PanelNaming.FileName(stamp, i);
            string directory = Path.Combine(parent, PanelNaming.BatchDirectoryName(stamp, Guid.NewGuid().ToString("N").Substring(0, 8)));
            Directory.CreateDirectory(directory);
            var created = new List<Tuple<A.Occurrence, string>>();
            try {
                for (int i = 0; i < specs.Count; i++) {
                    string file = Path.Combine(directory, name(i + 1));
                    created.Add(Tuple.Create(CadBuilder.Generate(app, assembly, specs[i], file, props), file));
                }
                string material = props == null ? "" : props.Material;
                File.WriteAllLines(Path.Combine(directory, "尺寸清单.csv"),
                    new[]{ "文件,宽mm,高mm,厚mm,每边间隙mm,材质" }.Concat(specs.Select((s, i) => string.Format(CultureInfo.InvariantCulture,
                        "{0},{1:R},{2:R},{3:R},{4:R},{5}", name(i + 1), s.Width * 1000, s.Height * 1000, s.Thickness * 1000, s.Gap * 1000, material))),
                    System.Text.Encoding.UTF8);
                return directory;
            } catch (Exception original) {
                var errors = new List<string>();
                foreach (var item in created.AsEnumerable().Reverse()) {
                    try { item.Item1.Delete(); File.Delete(item.Item2); }
                    catch (Exception e) { errors.Add(item.Item2 + ": " + e.Message); }
                }
                throw new InvalidOperationException("批量生成未完成：" + original.Message
                    + (errors.Count > 0 ? "\n部分结果未能撤回，请检查：\n" + string.Join("\n", errors) : "\n本批已插入的板子已撤回。")
                    + "\n批次目录：" + directory, original);
            }
        }
    }
}
