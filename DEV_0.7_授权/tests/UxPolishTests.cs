using System;
using System.IO;
using System.Windows.Forms;

namespace TianGongCadSuite {
    // 0.13.3 交互与便利性优化离线验证（不启动天工 CAD）：
    //   1. FolderPicker 现代 COM IFileDialog / IShellItem vtable 自检；
    //   2. QuickBomForm 批量查找替换 + Ctrl+Z 撤销 + Ctrl+Y 重做 + 复制粘贴 + ReuseIfOpen；
    //   3. DrawingForm 离线添加文件、关键词筛选、只选缺图、ReuseIfOpen。
    public static class UxPolishTests {
        static int passed;
        static void Check(bool ok, string label) {
            if (!ok) throw new Exception("UxPolishTests FAIL: " + label);
            passed++;
            Console.WriteLine("PASS: " + label);
        }

        public static void Run() {
            passed = 0;
            // 1. 现代文件夹选择器 COM vtable 绑定测试（不弹窗）
            string sysTemp = Path.GetTempPath();
            Check(FolderPicker.ProbeComSelfTest(sysTemp), "FolderPicker IFileDialog COM vtable self-test");
            Check(FolderPicker.ResolveExistingDir(Path.Combine(sysTemp, "nonexistent-sub-a", "sub-b")).Length > 0, "FolderPicker resolves nearest existing parent dir");

            string tmpDir = Path.Combine(sysTemp, "tg-ux-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try {
                string f1 = Path.Combine(tmpDir, "SK26.01-SD-10-001.par");
                string f2 = Path.Combine(tmpDir, "SK26.01-SD-10-002.par");
                string f3 = Path.Combine(tmpDir, "SK26.01-SD-10-003.par");
                string f4 = Path.Combine(tmpDir, "SK26.01-SD-10-004.par");
                string d1 = Path.Combine(tmpDir, "SK26.01-SD-10-001.dft");
                File.WriteAllText(f1, "dummy");
                File.WriteAllText(f2, "dummy");
                File.WriteAllText(f3, "dummy");
                File.WriteAllText(f4, "dummy");
                File.WriteAllText(d1, "dummy");

                // 2. QuickBomForm：批量查找替换 + Undo / Redo + 复制粘贴 + 复用
                using (var qb = new QuickBomForm(null)) {
                    qb.LoadFolder(tmpDir, true);
                    Check(qb.TestVisibleRowCount == 4, "QuickBomForm loaded 4 models from folder");
                    Check(qb.ReuseIfOpen(), "QuickBomForm ReuseIfOpen returns true");

                    string rr = qb.TestVerifyRedoAndReplace();
                    Check(rr.Contains("rep=2") && rr.Contains("afterRep=BETA-01/BETA-02") && rr.Contains("undo=ALPHA-01") && rr.Contains("redo=BETA-01"),
                        "QuickBomForm FindReplace + Undo + Redo: " + rr);

                    string cp = qb.TestVerifyCopyPasteAndPerf();
                    Check(cp.Contains("single=喷塑特注/喷塑特注") && cp.Contains("block=ECN-01/ECN-02/喷塑特注"),
                        "QuickBomForm Copy/Paste still intact: " + cp);
                    qb.TestForceClose();
                }

                // 3. DrawingForm：添加文件、关键词过滤、只选缺图、复用
                using (var df = new DrawingForm(null)) {
                    int added = df.TestAddFiles(new[] { f1, f2, f3, f4 }, false);
                    Check(added == 4 && df.TestVisibleCount == 4, "DrawingForm TestAddFiles loaded 4 models");
                    Check(df.ReuseIfOpen(), "DrawingForm ReuseIfOpen returns true");

                    df.TestSelectMissingOnly();
                    Check(df.TestCheckedCount == 3, "DrawingForm SelectMissingOnly checked 3 missing-draft parts (got " + df.TestCheckedCount + ")");

                    df.TestSetSearch("002");
                    Check(df.TestVisibleCount == 1, "DrawingForm search filter narrows to 1 item");
                    df.TestSetSearch("");
                    Check(df.TestVisibleCount == 4, "DrawingForm clearing search restores 4 items");

                    Check(df.TestDimensionStyle == "A3-国标-水平" || df.TestDimensionStyle == "A3-国标", "DrawingForm default dimension style is A3-国标-水平 or A3-国标 (got " + df.TestDimensionStyle + ")");
                    df.TestSetDimensionStyle("A3-国标");
                    Check(df.TestDimensionStyle == "A3-国标", "DrawingForm supports switching to A3-国标");
                    df.TestSetDimensionStyle("A3-国标水平");
                    Check(df.TestDimensionStyle == "A3-国标-水平", "DrawingForm normalizes A3-国标水平 to A3-国标-水平");
                }
            } finally {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
            Console.WriteLine("UX POLISH TESTS PASSED " + passed);
        }
    }
}
