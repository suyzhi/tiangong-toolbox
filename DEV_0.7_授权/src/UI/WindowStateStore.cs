using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 无模态工具窗的位置 / 大小 / 最大化状态记忆：
    //   · 工程师常把工具窗拖到副屏或屏幕右侧，避免盖住 CAD 3D 视图；
    //   · 下次再开直接回到顺手位置，并在拔掉副屏/分辨率缩小后自动安全钳制回主屏工作区；
    //   · 复用 Choices（choices.tsv），测试模式下自动跳过记忆，不影响回归脚本。
    public static class WindowStateStore {
        public static void Bind(Form form, string key, Func<bool> canSave = null) {
            if (form == null || string.IsNullOrWhiteSpace(key)) return;
            string storeKey = "win." + key.Trim();

            SavedWindowBounds saved;
            if (WindowBoundsRules.TryParse(Choices.Get(storeKey, ""), out saved)) {
                var fitted = Fit(saved, form);
                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = new Rectangle(fitted.X, fitted.Y, fitted.W, fitted.H);
                bool wantMax = saved.Maximized && form.MaximizeBox && form.FormBorderStyle == FormBorderStyle.Sizable;
                form.Load += (s, e) => {
                    try {
                        // 再校验一次位置（部分宿主下 Show(owner) 会在构造后微调），并按需恢复最大化
                        form.Bounds = new Rectangle(fitted.X, fitted.Y, fitted.W, fitted.H);
                        if (wantMax) form.WindowState = FormWindowState.Maximized;
                    } catch { }
                };
            }

            form.FormClosing += (s, e) => {
                if (e.Cancel) return;
                if (canSave != null && !canSave()) return;
                try {
                    Rectangle r = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
                    if (r.Width < 200 || r.Height < 150) return;
                    var data = new SavedWindowBounds {
                        X = r.X,
                        Y = r.Y,
                        W = r.Width,
                        H = r.Height,
                        Maximized = form.WindowState == FormWindowState.Maximized
                    };
                    Choices.Set(storeKey, WindowBoundsRules.Format(data));
                } catch (Exception ex) {
                    Log.Write("WindowStateSave " + key, ex.Message);
                }
            };
        }

        static RectI Fit(SavedWindowBounds saved, Form form) {
            var areas = new List<RectI>();
            foreach (var sc in Screen.AllScreens) {
                var a = sc.WorkingArea;
                areas.Add(new RectI(a.X, a.Y, a.Width, a.Height));
            }
            var pa = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.WorkingArea : new Rectangle(0, 0, 1920, 1040);
            var primary = new RectI(pa.X, pa.Y, pa.Width, pa.Height);
            int minW = Math.Max(260, form.MinimumSize.Width);
            int minH = Math.Max(200, form.MinimumSize.Height);
            return WindowBoundsRules.FitToScreens(new RectI(saved.X, saved.Y, saved.W, saved.H), areas, primary, minW, minH);
        }
    }
}
