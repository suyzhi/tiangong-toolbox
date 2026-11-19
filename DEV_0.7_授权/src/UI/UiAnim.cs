using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // ---------------------------------------------------------------------------------------------
    // 0.9.0「D · 侧栏向导」的骨架：动效引擎 Anim、左侧步骤栏 StepRail、整窗骨架 WizardShell、
    // 底部操作栏 ActionBar、提示条 Toast、列表新增项闪一下 Flash。
    //
    // 原则：动效只管"看起来"，状态一律同步改完。切步骤时页面立刻换好，动画只是盖在上面的一张图；
    // 测试、脚本、PrintWindow 截图读到的永远是最终状态。系统关了动画 / 远程桌面 / ui.tsv 里 motion=0 时全部瞬变。
    // ---------------------------------------------------------------------------------------------

    public static class Anim {
        sealed class Tween {
            public Control Owner; public string Key;
            public DateTime Start; public int Ms;
            public Action<double> Step; public Action Done; public Func<double, double> Ease;
        }
        static readonly List<Tween> live = new List<Tween>();
        static Timer timer;
        static int setting = -1;   // -1 = 还没读；0 = 关；1 = 开
        public static bool ForceOff;   // 测试 / 截图工具用

        // 时长（毫秒），跟设计规范一一对应
        public const int Hover = 120, Select = 180, Page = 240, Pop = 180, FlashMs = 800, ToastMs = 200;

        public static bool On {
            get {
                if (ForceOff) return false;
                if (setting < 0) {
                    setting = 1;
                    try {
                        foreach (var r in SettingsFile.Read(SettingsFile.PathOf("ui.tsv")))
                            if (r.Length >= 2 && r[0] == "motion") setting = r[1] == "0" ? 0 : 1;
                    } catch (Exception e) { Log.Write("UiMotionSetting", e.Message); }
                }
                try { return setting == 1 && SystemInformation.UIEffectsEnabled && !SystemInformation.TerminalServerSession; }
                catch { return false; }
            }
        }

        public static double EaseOut(double t){ double u = 1 - t; return 1 - u * u * u; }
        public static double Linear(double t){ return t; }
        // 打勾弹一下：先冲过头一点再回来
        public static double Back(double t){ const double c1 = 1.70158, c3 = c1 + 1; double u = t - 1; return 1 + c3 * u * u * u + c1 * u * u; }

        public static Color Mix(Color a, Color b, double t){
            if (t <= 0) return a; if (t >= 1) return b;
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // 跑一段动画：step(进度 0..1，已过缓动)。同一个 owner + key 的旧动画会被新的顶掉。
        // 动画关着、控件还没句柄、已经销毁：直接 step(1) + done()，结果和动画跑完一样。
        public static void Run(Control owner, int ms, Action<double> step, Action done = null, Func<double, double> ease = null, string key = null){
            if (owner == null || step == null) return;
            if (key != null) Cancel(owner, key);
            if (!On || ms <= 0 || owner.IsDisposed || !owner.IsHandleCreated || !owner.Visible) {
                Safe(step, 1); if (done != null) Safe(done); return;
            }
            live.Add(new Tween { Owner = owner, Key = key, Start = DateTime.Now, Ms = ms, Step = step, Done = done, Ease = ease ?? EaseOut });
            if (timer == null) { timer = new Timer { Interval = 15 }; timer.Tick += Tick; }
            if (!timer.Enabled) timer.Start();
        }
        public static void Cancel(Control owner, string key){
            for (int i = live.Count - 1; i >= 0; i--) if (live[i].Owner == owner && live[i].Key == key) live.RemoveAt(i);
        }
        public static bool Running(Control owner, string key){
            foreach (var t in live) if (t.Owner == owner && t.Key == key) return true;
            return false;
        }

        static void Tick(object sender, EventArgs e){
            var now = DateTime.Now;
            foreach (var t in live.ToArray()) {
                if (!live.Contains(t)) continue;
                if (t.Owner.IsDisposed) { live.Remove(t); continue; }
                double p = Math.Min(1, (now - t.Start).TotalMilliseconds / t.Ms);
                Safe(t.Step, t.Ease(p));
                if (p >= 1) { live.Remove(t); if (t.Done != null) Safe(t.Done); }
            }
            if (live.Count == 0) timer.Stop();
        }
        static void Safe(Action<double> a, double v){ try { a(v); } catch (Exception ex) { Log.Write("UiMotion", ex.Message); } }
        static void Safe(Action a){ try { a(); } catch (Exception ex) { Log.Write("UiMotion", ex.Message); } }
    }

    // 列表新增项闪一下：底色从浅靛蓝 800ms 褪成白。自绘列表在画每一项时问 Back(项, 原底色)。
    public sealed class Flash {
        readonly Control owner;
        readonly Dictionary<object, DateTime> marks = new Dictionary<object, DateTime>();
        public Flash(Control owner){ this.owner = owner; }
        public void Mark(object item){
            if (item == null || !Anim.On) return;
            marks[item] = DateTime.Now;
            Anim.Run(owner, Anim.FlashMs + 40, t => owner.Invalidate(), () => { marks.Clear(); owner.Invalidate(); }, Anim.Linear, "flash");
        }
        public Color Back(object item, Color normal){
            DateTime at;
            if (item == null || !marks.TryGetValue(item, out at)) return normal;
            double t = (DateTime.Now - at).TotalMilliseconds / Anim.FlashMs;
            if (t >= 1) return normal;
            return Anim.Mix(Ui.AccentRing, normal, Anim.EaseOut(t));
        }
    }
}
