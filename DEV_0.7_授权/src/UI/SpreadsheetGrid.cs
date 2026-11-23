using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 类 Excel 电子表格行为的 DataGridView：
    //   1. 单击单元格只选中、不进入编辑态（方便框选多行、方向键移动、Ctrl+C/V/D、Delete、右键批量操作）；
    //   2. 选中后直接敲英文/数字/符号或使用中文输入法（微软拼音/搜狗等）即可自动进入编辑并替换内容，不会吞掉中文首字或后续字；
    //   3. 双击单元格或按 F2 进入原地编辑（保留原内容），按 Backspace 清空并进入编辑；
    //   4. 可按列屏蔽双击进编辑（例如「型号」列双击直接弹型号库而非闪烁文本框）。
    public class SpreadsheetGrid : DataGridView {
        const int WM_KEYDOWN = 0x0100;
        const int WM_CHAR = 0x0102;
        const int WM_IME_STARTCOMPOSITION = 0x010D;
        const int WM_IME_COMPOSITION = 0x010F;
        const int WM_IME_CHAR = 0x0286;
        const int GCS_RESULTSTR = 0x0800;
        const int CFS_POINT = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct COMPOSITIONFORM {
            public int dwStyle;
            public POINT ptCurrentPos;
            public RECT rcArea;
        }

        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, byte[] lpBuf, int dwBufLen);
        [DllImport("imm32.dll")] static extern bool ImmSetCompositionWindow(IntPtr hIMC, ref COMPOSITIONFORM lpCompForm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // 返回 true 时，该单元格双击不自动触发文本编辑（用于「型号」列双击直达型号库）
        public Func<int, int, bool> SuppressDoubleClickEdit;

        bool nativeKeyDownStartedEdit;
        bool suppressNextMouseEdit;
        bool lastLeftDownWasSingle;

        public SpreadsheetGrid() {
            DoubleBuffered = true;
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        }

        bool CanStartTypingEdit() {
            if (ReadOnly || CurrentCell == null || CurrentCell.ReadOnly) return false;
            if (CurrentCell.RowIndex < 0 || CurrentCell.ColumnIndex < 0) return false;
            if (CurrentCell is DataGridViewCheckBoxCell || CurrentCell is DataGridViewButtonCell || CurrentCell is DataGridViewLinkCell) return false;
            return true;
        }

        bool StartEditWithInitialText(string initialText) {
            if (!CanStartTypingEdit()) return false;
            if (!BeginEdit(true)) return false;
            var tb = EditingControl as TextBox;
            if (tb != null) {
                string t = initialText ?? "";
                tb.Text = t;
                tb.SelectionStart = t.Length;
                tb.SelectionLength = 0;
            }
            return true;
        }

        void PositionImeAtCurrentCell() {
            if (!IsHandleCreated || CurrentCell == null || CurrentCell.RowIndex < 0 || CurrentCell.ColumnIndex < 0) return;
            IntPtr himc = ImmGetContext(Handle);
            if (himc == IntPtr.Zero) return;
            try {
                var rc = GetCellDisplayRectangle(CurrentCell.ColumnIndex, CurrentCell.RowIndex, true);
                if (rc.Width <= 0 || rc.Height <= 0) return;
                var cf = new COMPOSITIONFORM {
                    dwStyle = CFS_POINT,
                    ptCurrentPos = new POINT { X = rc.Left + Ui.S(4), Y = rc.Top + Ui.S(4) }
                };
                ImmSetCompositionWindow(himc, ref cf);
            } catch {
            } finally {
                ImmReleaseContext(Handle, himc);
            }
        }

        string ReadImeResultString() {
            if (!IsHandleCreated) return "";
            IntPtr himc = ImmGetContext(Handle);
            if (himc == IntPtr.Zero) return "";
            try {
                int bytes = ImmGetCompositionStringW(himc, GCS_RESULTSTR, null, 0);
                if (bytes <= 0) return "";
                var buf = new byte[bytes];
                int got = ImmGetCompositionStringW(himc, GCS_RESULTSTR, buf, bytes);
                if (got <= 0) return "";
                return Encoding.Unicode.GetString(buf, 0, got);
            } catch {
                return "";
            } finally {
                ImmReleaseContext(Handle, himc);
            }
        }

        protected override void OnCellMouseDown(DataGridViewCellMouseEventArgs e) {
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0) {
                bool suppressSingle = e.Clicks == 1;
                bool suppressDbl = e.Clicks >= 2 && SuppressDoubleClickEdit != null && SuppressDoubleClickEdit(e.RowIndex, e.ColumnIndex);
                lastLeftDownWasSingle = suppressSingle;
                if (suppressSingle || suppressDbl) {
                    suppressNextMouseEdit = true;
                    try {
                        base.OnCellMouseDown(e);
                    } finally {
                        suppressNextMouseEdit = false;
                    }
                    return;
                }
            } else {
                lastLeftDownWasSingle = false;
            }
            base.OnCellMouseDown(e);
        }

        protected override void OnCellMouseUp(DataGridViewCellMouseEventArgs e) {
            if (e.Button == MouseButtons.Left && lastLeftDownWasSingle) {
                suppressNextMouseEdit = true;
                try {
                    base.OnCellMouseUp(e);
                } finally {
                    suppressNextMouseEdit = false;
                }
                return;
            }
            base.OnCellMouseUp(e);
        }

        protected override void OnCellMouseClick(DataGridViewCellMouseEventArgs e) {
            if (e.Button == MouseButtons.Left && lastLeftDownWasSingle) {
                suppressNextMouseEdit = true;
                try {
                    base.OnCellMouseClick(e);
                } finally {
                    suppressNextMouseEdit = false;
                }
                return;
            }
            base.OnCellMouseClick(e);
        }

        protected override void OnCellMouseDoubleClick(DataGridViewCellMouseEventArgs e) {
            lastLeftDownWasSingle = false;
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0) {
                if (SuppressDoubleClickEdit != null && SuppressDoubleClickEdit(e.RowIndex, e.ColumnIndex)) {
                    suppressNextMouseEdit = true;
                    try {
                        base.OnCellMouseDoubleClick(e);
                    } finally {
                        suppressNextMouseEdit = false;
                    }
                    return;
                }
                base.OnCellMouseDoubleClick(e);
                if (!IsCurrentCellInEditMode && !ReadOnly && e.RowIndex < Rows.Count && e.ColumnIndex < Columns.Count) {
                    var cell = Rows[e.RowIndex].Cells[e.ColumnIndex];
                    if (!cell.ReadOnly && !(cell is DataGridViewCheckBoxCell) && !(cell is DataGridViewButtonCell) && !(cell is DataGridViewLinkCell)) {
                        if (CurrentCell != cell) CurrentCell = cell;
                        if (BeginEdit(false)) {
                            var tb = EditingControl as TextBox;
                            if (tb != null) {
                                tb.SelectionStart = tb.TextLength;
                                tb.SelectionLength = 0;
                            }
                        }
                    }
                }
                return;
            }
            base.OnCellMouseDoubleClick(e);
        }

        protected override void OnCellBeginEdit(DataGridViewCellCancelEventArgs e) {
            if (suppressNextMouseEdit) {
                e.Cancel = true;
                return;
            }
            base.OnCellBeginEdit(e);
        }

        protected override void OnCellEndEdit(DataGridViewCellEventArgs e) {
            nativeKeyDownStartedEdit = false;
            base.OnCellEndEdit(e);
        }

        protected override void OnCurrentCellChanged(EventArgs e) {
            nativeKeyDownStartedEdit = false;
            base.OnCurrentCellChanged(e);
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            if (!IsCurrentCellInEditMode && !e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.Back && CanStartTypingEdit()) {
                if (StartEditWithInitialText("")) {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
            }
            base.OnKeyDown(e);
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_KEYDOWN) {
                bool wasEditing = IsCurrentCellInEditMode;
                base.WndProc(ref m);
                if (!wasEditing && IsCurrentCellInEditMode) nativeKeyDownStartedEdit = true;
                return;
            }

            if (m.Msg == WM_IME_STARTCOMPOSITION) {
                if (!IsCurrentCellInEditMode && CanStartTypingEdit()) PositionImeAtCurrentCell();
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WM_IME_COMPOSITION) {
                long flags = m.LParam.ToInt64();
                if (!IsCurrentCellInEditMode && CanStartTypingEdit() && (flags & GCS_RESULTSTR) != 0) {
                    string committed = ReadImeResultString();
                    if (!string.IsNullOrEmpty(committed)) {
                        StartEditWithInitialText(committed);
                        long rest = flags & ~GCS_RESULTSTR;
                        if (rest == 0) return;
                        m.LParam = new IntPtr(rest);
                    }
                }
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WM_IME_CHAR) {
                char ch = (char)(m.WParam.ToInt32() & 0xFFFF);
                if (!char.IsControl(ch)) {
                    if (!IsCurrentCellInEditMode) {
                        if (StartEditWithInitialText(ch.ToString())) return;
                    } else if (EditingControl != null && EditingControl.IsHandleCreated) {
                        SendMessage(EditingControl.Handle, WM_CHAR, (IntPtr)ch, m.LParam);
                        return;
                    }
                }
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WM_CHAR) {
                if (nativeKeyDownStartedEdit) {
                    nativeKeyDownStartedEdit = false;
                    base.WndProc(ref m);
                    return;
                }
                char ch = (char)(m.WParam.ToInt32() & 0xFFFF);
                if (!char.IsControl(ch)) {
                    if (!IsCurrentCellInEditMode) {
                        if (StartEditWithInitialText(ch.ToString())) return;
                    } else if (EditingControl != null && EditingControl.IsHandleCreated) {
                        SendMessage(EditingControl.Handle, WM_CHAR, (IntPtr)ch, m.LParam);
                        return;
                    }
                }
                base.WndProc(ref m);
                return;
            }

            base.WndProc(ref m);
        }
    }
}
