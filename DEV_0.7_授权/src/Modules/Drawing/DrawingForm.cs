using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using A=SolidEdgeAssembly;
using F=SolidEdgeFramework;

namespace TianGongCadSuite {
    // 「一键出图」窗口（0.11.0，命令 13）。
    // 零件 / 钣金：列一行，点「出图」；装配：第一行是装配图，下面是装配里用到的零件（默认只勾公司编号的加工件）。
    // 出完每一行写上：图纸、比例、尺寸个数、完成度、要人工看一眼的地方；双击一行打开那张图。
    public sealed class DrawingForm : Form, IBusyTool {
        readonly F.Application app;
        readonly ListView list = new ListView();
        readonly Label head = new Label(), status = new Label();
        readonly CheckBox overwrite = new CheckBox(), openAfter = new CheckBox();
        readonly SlimProgress bar = new SlimProgress { Visible = false };
        readonly FlatButton run = (FlatButton)Ui.Primary("出图", 120, 40);
        readonly Button close = Ui.Secondary("关闭", 96, 36);
        readonly List<DrawingJob> jobs = new List<DrawingJob>();
        bool busy, stop;
        public bool Busy { get { return busy; } }
        public string BusyWhat { get { return "出图"; } }

        public DrawingForm(F.Application application){
            app = application;
            Ui.Shell(this, "一键出图", 860, 560, 640, 420);
            var body = new VerticalStack { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 8), Gap = 6 };
            head.AutoSize = false; head.Height = 30; head.Font = Ui.F10B; head.ForeColor = Ui.Text; head.BackColor = Ui.Bg;
            body.Add(head);
            var hint = new Label { AutoSize = false, Height = 36, Font = Ui.F8, ForeColor = Ui.Muted, BackColor = Ui.Bg,
                Text = "按公司模板（SK先惠 A4 / A3）出图：主视图 + 投影 + 轴测图，坐标尺寸、孔标注、圆角、刻字引线都标好，定位销孔加 H7 + 位置度（按公司图纸统计），标题栏从零件属性自动带出。\r\n出完请看一眼「需要人工确认」那一列；双击一行打开那张图。" };
            body.Add(hint);
            list.View = View.Details; list.CheckBoxes = true; list.FullRowSelect = true; list.GridLines = false; list.HideSelection = false;
            list.BorderStyle = BorderStyle.FixedSingle; list.Font = Ui.F9;
            list.Columns.Add("文件", 300); list.Columns.Add("类型", 56); list.Columns.Add("已有图纸", 70); list.Columns.Add("结果", 240); list.Columns.Add("需要人工确认", 400);
            list.DoubleClick += (s, e) => OpenSelected();
            body.Add(list, true);
            var opts = new FlowLayoutPanel { Height = 30, FlowDirection = FlowDirection.LeftToRight, BackColor = Ui.Bg, WrapContents = false };
            overwrite.Text = "已有同名工程图时覆盖（不勾：另存为「名字(自动).dft」）"; overwrite.AutoSize = true; overwrite.Font = Ui.F9;
            openAfter.Text = "出完打开工程图"; openAfter.AutoSize = true; openAfter.Checked = true; openAfter.Font = Ui.F9;
            opts.Controls.Add(overwrite); opts.Controls.Add(openAfter);
            body.Add(opts);
            bar.Height = 8; body.Add(bar);
            status.AutoSize = false; status.Height = 22; status.Font = Ui.F8; status.ForeColor = Ui.Muted; status.BackColor = Ui.Bg;
            body.Add(status);
            var actions = new ActionBar { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(18, 0, 18, 0) };
            run.Click += (s, e) => { if (busy) stop = true; else Run(); };
            close.Click += (s, e) => Close();
            actions.AddRight(close); actions.AddRight(run);
            Controls.Add(body); Controls.Add(actions);
            FormClosing += (s, e) => { if (busy) { stop = true; e.Cancel = true; } };
        }

        // 按当前活动文档列清单
        public void LoadActive(){
            jobs.Clear(); list.Items.Clear();
            object doc = null; try { doc = app.ActiveDocument; } catch { }
            string file = "";
            try { file = (string)((dynamic)doc).FullName; } catch { }
            if (doc == null || file.Length == 0 || !File.Exists(file)) { head.Text = "请先打开（并保存）要出图的零件或装配。"; run.Enabled = false; return; }
            bool dirty = false; try { dirty = (bool)((dynamic)doc).Dirty; } catch { }
            var asm = doc as A.AssemblyDocument;
            jobs.Add(new DrawingJob { File = file, Kind = DrawingBatch.KindOf(file), Count = 1, Selected = true, HasDrawing = DrawingBatch.HasDrawing(file) });
            if (asm != null) {
                string top = DrawingBatch.Project(Path.GetFileNameWithoutExtension(file));
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Collect(asm, counts, 0);
                foreach (var kv in counts.OrderBy(k => Path.GetFileName(k.Key), StringComparer.OrdinalIgnoreCase)) {
                    string why; bool custom = DrawingBatch.Custom(kv.Key, top, out why);
                    jobs.Add(new DrawingJob { File = kv.Key, Kind = DrawingBatch.KindOf(kv.Key), Count = kv.Value, Selected = custom, Reason = why, HasDrawing = DrawingBatch.HasDrawing(kv.Key) });
                }
            }
            foreach (var j in jobs) {
                var it = new ListViewItem(new[] { Path.GetFileName(j.File) + (j.Count > 1 ? "  ×" + j.Count : ""), j.Kind, j.HasDrawing ? "有" : "", j.Reason.Length > 0 ? "（" + j.Reason + "，默认不出）" : "", "" });
                it.Checked = j.Selected; it.Tag = j;
                list.Items.Add(it);
            }
            head.Text = (asm != null ? "装配 " : "") + Path.GetFileName(file) + (asm != null ? "　·　零件 " + (jobs.Count - 1) + " 种，默认勾选 " + jobs.Count(j => j.Selected) + " 张" : "");
            if (dirty) status.Text = "注意：当前文档有没保存的修改 —— 工程图按磁盘上的版本出。要带上修改请先保存。";
            openAfter.Checked = asm == null;
        }

        static void Collect(A.AssemblyDocument asm, Dictionary<string, int> counts, int depth){
            if (depth > 8) return;
            foreach (A.Occurrence occ in asm.Occurrences) {
                string f = ""; try { f = occ.OccurrenceFileName; } catch { }
                if (f.Length == 0) continue;
                bool sub = f.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);
                if (sub) {
                    try { var d = occ.OccurrenceDocument as A.AssemblyDocument; if (d != null) Collect(d, counts, depth + 1); } catch { }
                    // 子装配本身也要一张装配图
                }
                int n; counts.TryGetValue(f, out n); counts[f] = n + 1;
            }
        }

        void Run(){
            var todo = list.Items.Cast<ListViewItem>().Where(i => i.Checked).ToList();
            if (todo.Count == 0) { status.Text = "没有勾选要出图的文件。"; return; }
            busy = true; stop = false; run.Text = "停止"; close.Enabled = false; bar.Visible = true; bar.Maximum = todo.Count; bar.Value = 0;
            int done = 0, ok = 0;
            try {
                foreach (var it in todo) {
                    if (stop) { status.Text = "已停止。"; break; }
                    var j = (DrawingJob)it.Tag;
                    status.Text = "正在出图 " + (done + 1) + "/" + todo.Count + "：" + Path.GetFileName(j.File);
                    it.SubItems[3].Text = "出图中…"; it.EnsureVisible();
                    Application.DoEvents();
                    bool keep = openAfter.Checked && todo.Count == 1;
                    var opt = new DrawingOptions { Overwrite = overwrite.Checked, Close = !keep };
                    var o = j.Kind == "装配" ? DrawingMaker.Assembly(app, j.File, opt) : DrawingMaker.Part(app, j.File, opt);
                    j.Result = o;
                    if (o.Ok) ok++;
                    it.SubItems[3].Text = o.Ok ? (o.Sheet + " " + o.Scale + "　尺寸 " + o.Dimensions + "　完成度 " + (int)Math.Round(o.Completeness * 100) + "%") : "失败";
                    it.SubItems[4].Text = string.Join("；", o.Notes.Where(n => !n.StartsWith("视图 ")).Take(4));
                    it.ForeColor = !o.Ok ? Ui.Bad : o.Completeness < 0.9 || o.Failed > 0 ? Ui.Warn : Ui.Ok;
                    done++; bar.Value = done;
                    Application.DoEvents();
                }
                if (!stop) status.Text = "出完了：成功 " + ok + " 张" + (done - ok > 0 ? "，失败 " + (done - ok) + " 张" : "") + "。工程图和模型放在一起；双击一行打开。";
            } catch (Exception e) { status.Text = "出图中断：" + e.Message; Log.Write("DrawingForm", e); }
            finally { busy = false; run.Text = "出图"; close.Enabled = true; }
        }

        void OpenSelected(){
            if (busy || list.SelectedItems.Count == 0) return;
            var j = (DrawingJob)list.SelectedItems[0].Tag;
            string f = j.Result != null && !string.IsNullOrEmpty(j.Result.Output) ? j.Result.Output : (j.HasDrawing ? Path.ChangeExtension(j.File, ".dft") : "");
            if (f.Length == 0 || !File.Exists(f)) return;
            try { app.Documents.Open(f); } catch (Exception e) { MessageBox.Show(this, "打不开：" + e.Message, "一键出图"); }
        }
    }
}
