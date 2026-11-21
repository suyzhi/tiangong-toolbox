using System;
using System.Collections.Generic;
using System.IO;

namespace TianGongCadSuite {
    // 快速出清单（0.12.0，命令 14）的数据模型。纯逻辑，不依赖 CAD / WinForms（tests/pure 可直接测）。
    public sealed class BomHeader {
        public string Customer = "";            // 客户（C2）
        public string ProjectCode = "";         // 项目编号（E2，如 SK26.01）
        public string ProjectName = "";         // 项目名称（C3）
        public string ProjectAttr = "加工件";    // 项目属性（E3）
        public string LineNo = "";              // 线体号（H2，如 L2）
        public string StationNo = "";           // 工位号（H3，如 C10）
        public string SequenceNo = "01";        // 清单单号（用于文件夹与表名，如 01）
        public string MechanismNo = "";         // 机构号（I3，如 SK26.01-SD-15）
        public string ListTitle = "";           // 清单名称 / 模块名（如 按钮盒支架）
        public string EngineerRegion = "上海";   // 工程师地区（Q1）
        public string EngineerName = "";        // 工程师姓名（Q2）
        public DateTime IssueDate = DateTime.Today;              // 下发时间（N3）
        public DateTime DeliveryDate = DateTime.Today.AddDays(12); // 要求交货期（N2）
    }

    public sealed class BomItem {
        public bool Selected = true;
        public string ModelPath = "";           // 三维模型完整路径（.par / .psm / .asm）
        public string DraftPath = "";           // 同名工程图完整路径（.dft），没有则为空
        public string PartName = "";            // 名称（B 列）
        public string DrawingNo = "";           // 图号（C 列，也是导出的 6 种格式文件名）
        public string Material = "";            // 材料（D 列）
        public int Quantity = 1;                // 数量（E 列）
        public int OccCount = 1;                // 装配里出现的实例数
        public int PropQuantity;                // 属性卡里的「数量」（0 表示未填）
        public int Category = 4;                // 八大类类别 1..8（F 列）
        public bool CategoryManual;             // 用户手动改过分类时，重算不覆盖
        public string CategoryReason = "";      // 自动分类依据说明
        public string SurfaceTreatment = "";    // 表面处理（G 列）
        public string Batch = "第一批";          // 批次（H 列）
        public string Priority = "A";           // 加工优先等级（I 列）
        public string Remark = "";              // 备注（J:K 列）
        public string ChangeReason = "正常设计"; // 变更原因（L:M 列）
        public string Code = "";                // 编码（P 列，可选）
        public string LookupDrawingNo = "";     // 系统反查图号（Q 列，可选）
        public string LookupPartName = "";      // 系统反查名称（R 列，可选）
        public string TagCode = "";             // 标识码（S 列，可选）

        // 从零件属性读出的辅助头信息（用来自动推断表头）
        public string ProjectNameHint = "";
        public string DesignerHint = "";

        // 几何与物理属性（用于八大类自动判别）
        public double MaxLengthMm;              // 包围盒最大边（mm）
        public double MidDimMm;                 // 包围盒中间边（mm）
        public double MinThickMm;               // 包围盒最小边（mm）
        public double MassKg;                   // 质量（kg）
        public bool MetricsLoaded;              // 是否已读取尺寸/质量

        public string SkipReason = "";          // 默认未勾选原因（外购件 / 标准件 / 焊件子零件）
        public string ReadProblem = "";         // 读取属性出错说明
        public string ExportStatus = "";        // 导出结果说明
        public byte[] ThumbnailPng;             // 可选三维缩略图 PNG

        public string ModelExt {
            get { return (Path.GetExtension(ModelPath ?? "") ?? "").ToLowerInvariant(); }
        }
        public string ModelFileName {
            get { return Path.GetFileName(ModelPath ?? ""); }
        }
        public bool IsAssembly {
            get { return ModelExt == ".asm"; }
        }
        public bool IsSheetMetal {
            get { return ModelExt == ".psm"; }
        }
        public bool HasDraft {
            get { return !string.IsNullOrEmpty(DraftPath) && File.Exists(DraftPath); }
        }
    }

    public sealed class BomOptions {
        public string OutputFolder = "";
        public bool IncludeProcessingList = true;
        public bool IncludeCodeRequest;
        public bool IncludeThumbnails;
        public bool IncludeNative3d = true;
        public bool IncludeDraft = true;
        public bool IncludeDwg = true;
        public bool IncludeJpg = true;
        public bool IncludePdf = true;
        public bool IncludeStep = true;
        public bool CreateZip;
        public bool OverwriteExisting = true;
        public string CustomListTemplate = "";
        public string CustomCodeTemplate = "";

        public int SelectedFormatCount {
            get {
                int n = 0;
                if (IncludeNative3d) n++;
                if (IncludeDraft) n++;
                if (IncludeDwg) n++;
                if (IncludeJpg) n++;
                if (IncludePdf) n++;
                if (IncludeStep) n++;
                return n;
            }
        }
    }

    public sealed class BomIssue {
        public bool Blocking;
        public BomItem Item;
        public string Message = "";
    }

    public sealed class BomExportSummary {
        public string OutputFolder = "";
        public string ListXlsxPath = "";
        public string CodeXlsxPath = "";
        public string ZipPath = "";
        public int ItemsTotal;
        public int ItemsComplete;
        public int FilesExported;
        public int FilesSkipped;
        public int FilesFailed;
        public int ThumbnailsRendered;
        public long ElapsedMs;
        public bool Cancelled;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();

        public string ShortSummary() {
            string s = (Cancelled ? "已停止：" : "完成：")
                + "处理 " + ItemsTotal + " 个零件（全齐 " + ItemsComplete + " 个），写出 " + FilesExported + " 个格式文件";
            if (FilesSkipped > 0) s += "，跳过已存在 " + FilesSkipped + " 个";
            if (FilesFailed > 0) s += "，失败 " + FilesFailed + " 项";
            if (!string.IsNullOrEmpty(ListXlsxPath)) s += "；已生成加工件清单";
            if (!string.IsNullOrEmpty(CodeXlsxPath)) s += " + 编码申请表";
            if (!string.IsNullOrEmpty(ZipPath)) s += " + ZIP";
            s += "（" + (ElapsedMs / 1000.0).ToString("0.0") + " 秒）";
            return s;
        }
    }
}
