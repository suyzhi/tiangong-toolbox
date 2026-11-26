using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace TianGongCadSuite {
    public sealed class LineupXlsxOptions {
        public bool Export11Columns = true; // true = 公司 V3.0 模板标准 11 列（含机构名称），false = 精简 10 列
        public bool IncludeCadPages = true;
        public bool HighlightAudit = false;
        public bool MarkBomChanges = true;  // true = 相对上一版基准标红新增行、标红+删除线保留取消行
        public string CadAssetsDir = "";   // 可选覆盖图片查找目录（默认取 AssemblyFile 旁 .lineup.cad）
        public string VersionNote = "";
    }

    public sealed class LineupXlsxSummary {
        public string OutputPath = "";
        public int BomRows;
        public int BomAddedRows;
        public int BomRemovedRows;
        public int ChangelogRows;
        public int CadPages;
        public int CadCallouts;
        public int CadArrows;
        public int AuditErrors;
        public int AuditWarnings;
    }

    // 纯 OpenXML 公司标准 Lineup 工作簿生成器（零 Office / WPS COM 依赖，仅用 System.IO.Compression + System.Xml）。
    // 样式、列序、色彩、字体、边框、合并与页签结构严格对齐公司内部模板 V3.0：
    //   1. LineUP更新日志（白底黑粗外框、宋体16B标题、20行记录区 + 底部黄底备注区）
    //   2. LineUP第一版_配置表（8列黄底表头、按机构聚合纵向合并、底部黄底备注）
    //   3. LineUP第二版_BOM（11列黄底表头 + B1浅橙地址列、5类灰底居中分区栏、白底居中数据、
    //      气缸“伸出”红字、普通传感器原位黄底+“常闭”红字、阀岛/机构名称纵向合并、冻结首行首列与自动筛选）
    //   4. LineUP第三版_CAD（12列无网格线版式、中央黄底粗框插图标题栏、白底粗框画布、VLOOKUP 联动 BOM、
    //      原生红色实心箭头严格从图指向两侧单元格）
    //   5. 签字页（公司标准 64 条 lineup自检表，白底黑框、审查范围纵向合并并回填插件自动检查结果）
    public static class LineupXlsxWriter {
        const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string NsXdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string BomSheetName = "LineUP第二版_BOM";

        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // ---- cellXfs 样式索引常量（与 BuildStylesXml() 严格一一对应） ----
        const int S_Default = 0;
        // Sheet 1: LineUP更新日志
        const int S_LogTitleL = 1;
        const int S_LogTitleM = 2;
        const int S_LogTitleR = 3;
        const int S_LogHdrL = 4;
        const int S_LogHdrM = 5;
        const int S_LogHdrR = 6;
        const int S_LogDataL = 7;
        const int S_LogDataCenter = 8;
        const int S_LogDataLeft = 9;
        const int S_LogDataDate = 10;
        const int S_LogDataR = 11;
        const int S_LogRemTL = 12;
        const int S_LogRemTM = 13;
        const int S_LogRemTR = 14;
        const int S_LogRemML = 15;
        const int S_LogRemMM = 16;
        const int S_LogRemMR = 17;
        const int S_LogRemBL = 18;
        const int S_LogRemBM = 19;
        const int S_LogRemBR = 20;
        // Sheet 2: LineUP第一版_配置表
        const int S_CfgHdr = 21;
        const int S_CfgData = 22;
        const int S_CfgInt = 23;
        const int S_CfgRemark = 24;
        // Sheet 3: LineUP第二版_BOM
        const int S_BomHdrYellow = 25;
        const int S_BomHdrYellowR = 26;
        const int S_BomHdrPeach = 27;
        const int S_BomSecGray = 28;
        const int S_BomSecLightGray = 29;
        const int S_BomNum = 30;
        const int S_BomCell = 31;
        const int S_BomCellRed = 32;
        const int S_BomMech = 33;
        const int S_BomSeHome = 34;
        const int S_BomSeHomeRed = 35;
        const int S_BomWarn = 36;
        const int S_BomWarnNum = 37;
        // Sheet 4: LineUP第三版_CAD
        const int S_CadSideHdrC = 38;
        const int S_CadSideHdrR = 39;
        const int S_CadSideHdrL = 40;
        const int S_CadYelTL = 41;
        const int S_CadYelTM = 42;
        const int S_CadYelTR = 43;
        const int S_CadYelML = 44;
        const int S_CadYelMM = 45;
        const int S_CadYelMR = 46;
        const int S_CadYelBL = 47;
        const int S_CadYelBM = 48;
        const int S_CadYelBR = 49;
        const int S_CadLeftDesc = 50;
        const int S_CadIo = 51;
        const int S_CadLeftNum = 52;
        const int S_CadLeftNumUnres = 53;
        const int S_CadRightNum = 54;
        const int S_CadRightNumUnres = 55;
        const int S_CadRightDesc = 56;
        const int S_CadFrameTL = 57;
        const int S_CadFrameTM = 58;
        const int S_CadFrameTR = 59;
        const int S_CadFrameML = 60;
        const int S_CadFrameMM = 61;
        const int S_CadFrameMR = 62;
        const int S_CadFrameBL = 63;
        const int S_CadFrameBM = 64;
        const int S_CadFrameBR = 65;
        // Sheet 5: 签字页
        const int S_SignTitle = 66;
        const int S_SignCenter = 67;
        const int S_SignLeft = 68;
        const int S_SignPass = 69;
        const int S_SignWarn = 70;
        const int S_SignErr = 71;
        // Sheet 3: BOM 变更标记（严格按公司模板说明附图：新增=红底黑字，取消=红底黑字+删除线）
        const int S_BomAddNum = 72;
        const int S_BomAddCell = 73;
        const int S_BomAddMech = 74;
        const int S_BomDelNum = 75;
        const int S_BomDelCell = 76;
        const int S_BomDelMech = 77;

        static readonly string[] BomTemplateHeaders11 = {
            "SK编号No.",
            "地址\nAddress",
            "功能参数\nFunctional parameters",
            "描述\nDescription",
            "型号\nModel number",
            "品牌\nBrands",
            "原位\nOriginal position",
            "安装方式\nMounting method",
            "传感器接口类型\nDetect interface type",
            "机构名称",
            "备注\nRemarks"
        };
        static readonly double[] BomTemplateWidths11 = {
            17.21, 15.17, 29.00, 38.40, 41.46, 16.46, 26.00, 31.61, 33.67, 44.84, 39.26
        };

        static readonly string[] BomTemplateHeaders10 = {
            "SK编号No.",
            "地址\nAddress",
            "功能参数\nFunctional parameters",
            "描述\nDescription",
            "型号\nModel number",
            "品牌\nBrands",
            "原位\nOriginal position",
            "安装方式\nMounting method",
            "传感器接口类型\nDetect interface type",
            "备注\nRemarks"
        };
        static readonly double[] BomTemplateWidths10 = {
            17.21, 15.17, 29.00, 38.40, 41.46, 16.46, 26.00, 31.61, 33.67, 39.26
        };

        sealed class SstBuilder {
            readonly List<string> items = new List<string>();
            readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
            public int Add(string s) {
                s = SanitizeXml(s ?? "");
                int i;
                if (index.TryGetValue(s, out i)) return i;
                i = items.Count;
                items.Add(s);
                index[s] = i;
                return i;
            }
            public string ToXml() {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sb.Append("<sst xmlns=\"").Append(NsMain).Append("\" count=\"").Append(items.Count).Append("\" uniqueCount=\"").Append(items.Count).Append("\">");
                foreach (var s in items) {
                    sb.Append("<si><t xml:space=\"preserve\">").Append(Esc(s)).Append("</t></si>");
                }
                sb.Append("</sst>");
                return sb.ToString();
            }
        }

        sealed class MediaItem {
            public string RelId = "";
            public string ZipPath = "";
            public string Ext = "png";
            public byte[] Bytes;
        }

        sealed class SignoffRowDef {
            public int Seq;
            public string Scope;
            public string Content;
            public string FixedRemark;
            public string[] AuditCodes;
        }

        // 公司内部模板 V3.0《签字页》标准 64 条自检清单（原文对齐，同时绑定可自动核查的规则码）
        static readonly SignoffRowDef[] SignoffRows = {
            new SignoffRowDef { Seq = 1, Scope = "更新日志", Content = "每次更新日志需如实填写", FixedRemark = "", AuditCodes = new[] { "__LOG_AUTO__" } },
            new SignoffRowDef { Seq = 2, Scope = "lineup第一版配置表", Content = "伺服电机的功率与相数需提供", FixedRemark = "" },
            new SignoffRowDef { Seq = 3, Scope = "lineup第二版BOM(气缸列表)", Content = "气缸的编号顺序以动作流程为基础的前提下按模块来", FixedRemark = "", AuditCodes = new[] { "NUM_DUP", "NUM_EMPTY" } },
            new SignoffRowDef { Seq = 4, Scope = "lineup第二版BOM(气缸列表)", Content = "气缸是用阀岛还是阀片控制，编号是否正确", FixedRemark = "必须得到工程师或主设签字留档", AuditCodes = new[] { "CYL_NO_VALVE" } },
            new SignoffRowDef { Seq = 5, Scope = "lineup第二版BOM(气缸列表)", Content = "气缸的描述要接功能来命名", FixedRemark = "命名前与工程师沟通，了解动作工艺" },
            new SignoffRowDef { Seq = 6, Scope = "lineup第二版BOM(气缸列表)", Content = "气缸型号是否漏写，如没写备注后要写明原因", FixedRemark = "", AuditCodes = new[] { "CYL_FIELD" } },
            new SignoffRowDef { Seq = 7, Scope = "lineup第二版BOM(气缸列表)", Content = "原位及安装方式是否正确", FixedRemark = "", AuditCodes = new[] { "CYL_FIELD" } },
            new SignoffRowDef { Seq = 8, Scope = "lineup第二版BOM(气缸列表)", Content = "同功能的气缸命名为：xxx1#,xxx2#,1#与2#需要按标准来", FixedRemark = "" },
            new SignoffRowDef { Seq = 9, Scope = "lineup第二版BOM(气缸列表)", Content = "备注是否漏写，按模块命名", FixedRemark = "" },
            new SignoffRowDef { Seq = 10, Scope = "lineup第二版BOM(阀列表)", Content = "阀的编号要与气缸一一对应，就算后面要改也要括号写上原先对应的阀编号", FixedRemark = "", AuditCodes = new[] { "CYL_NO_VALVE", "VALVE_NO_CYL" } },
            new SignoffRowDef { Seq = 11, Scope = "lineup第二版BOM(阀列表)", Content = "阀岛，阀片的型号是否正确", FixedRemark = "", AuditCodes = new[] { "VALVE_FIELD" } },
            new SignoffRowDef { Seq = 12, Scope = "lineup第二版BOM(阀列表)", Content = "控制元件所控制的受控单元是否标注清楚（如真空发生器控制哪些吸盘）", FixedRemark = "" },
            new SignoffRowDef { Seq = 13, Scope = "lineup第二版BOM(阀列表)", Content = "备注写明是用在阀岛、阀片还是集装板", FixedRemark = "", AuditCodes = new[] { "VALVE_FIELD" } },
            new SignoffRowDef { Seq = 14, Scope = "lineup第二版BOM(气缸类传感器列表)", Content = "编号是否正确，多个缸用一组传感器还是多组传感器，每个气缸伸出缩回位是否只有一个信号检测", FixedRemark = "", AuditCodes = new[] { "CYL_NO_SENSOR" } },
            new SignoffRowDef { Seq = 15, Scope = "lineup第二版BOM(气缸类传感器列表)", Content = "型号，品牌，接口类型是否正确", FixedRemark = "", AuditCodes = new[] { "CSEN_FIELD" } },
            new SignoffRowDef { Seq = 16, Scope = "lineup第二版BOM(气缸类传感器列表)", Content = "备注的名称与对应的气缸保持一致", FixedRemark = "", AuditCodes = new[] { "CSEN_NOTE_DIFF" } },
            new SignoffRowDef { Seq = 17, Scope = "lineup第二版BOM(除气缸类检测传感器之外的检测列表)", Content = "编号需以动作流程为基础的前提下按模块分（后期更新部分直接往后排）", FixedRemark = "" },
            new SignoffRowDef { Seq = 18, Scope = "lineup第二版BOM(除气缸类检测传感器之外的检测列表)", Content = "同功能的传感器命名为：xxx1#,xxx2#,1#与2#需要按标准来", FixedRemark = "" },
            new SignoffRowDef { Seq = 19, Scope = "lineup第二版BOM(除气缸类检测传感器之外的检测列表)", Content = "传感器的是否是否有漏写，未写要说明原因，型号是否正确", FixedRemark = "", AuditCodes = new[] { "GSEN_FIELD" } },
            new SignoffRowDef { Seq = 20, Scope = "lineup第二版BOM(除气缸类检测传感器之外的检测列表)", Content = "传感器品牌是否正确", FixedRemark = "", AuditCodes = new[] { "GSEN_FIELD" } },
            new SignoffRowDef { Seq = 21, Scope = "lineup第二版BOM(除气缸类检测传感器之外的检测列表)", Content = "需要用安全型的场合是否选了常闭", FixedRemark = "" },
            new SignoffRowDef { Seq = 22, Scope = "lineup第二版BOM(除气缸类检测传感器之外的检测列表)", Content = "传感器的接口类型是否正确", FixedRemark = "", AuditCodes = new[] { "GSEN_FIELD" } },
            new SignoffRowDef { Seq = 23, Scope = "lineup第二版BOM(除气缸类检测传感器之外的检测列表)", Content = "备注的名称需与安装的组件名称保持一致", FixedRemark = "" },
            new SignoffRowDef { Seq = 24, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "每个气源处理，阀岛，汇流板，储气罐需各占一个编号", FixedRemark = "", AuditCodes = new[] { "OTHER_FIELD" } },
            new SignoffRowDef { Seq = 25, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "一套伺服配模组只需要占一个编号，型号要把伺服电机的明细及附件写上", FixedRemark = "" },
            new SignoffRowDef { Seq = 26, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "丝杆模组导程是否漏写", FixedRemark = "" },
            new SignoffRowDef { Seq = 27, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "减速机的减速比是否漏写", FixedRemark = "" },
            new SignoffRowDef { Seq = 28, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "大轮带小轮的齿数比是否漏写", FixedRemark = "" },
            new SignoffRowDef { Seq = 29, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "电机命名时要带上属性，如伺服电机，变频，单相直流电机等等。", FixedRemark = "" },
            new SignoffRowDef { Seq = 30, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "电机的功率，减速比是否有漏写错写", FixedRemark = "" },
            new SignoffRowDef { Seq = 31, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "本站上其它供应商提供的设备是否漏写", FixedRemark = "" },
            new SignoffRowDef { Seq = 32, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "相机的光源是否漏写，数量是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 33, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "扫码枪是否漏写，数量是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 34, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "RFID是否漏写，数量是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 35, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "安全门锁是否漏写，数量是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 36, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "光栅是否漏写，数量是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 37, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "三色灯是否漏写，数量是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 38, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "按钮盒是否漏写，数量是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 39, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "按钮盒描述要带上孔位及功能：3孔按钮盒（急停，启动，复位）", FixedRemark = "", AuditCodes = new[] { "BTN_BOX_DESC" } },
            new SignoffRowDef { Seq = 40, Scope = "lineup第二版BOM(其他电气器件列表)", Content = "站外是否有围栏上的东西需要在本站控制", FixedRemark = "" },
            new SignoffRowDef { Seq = 41, Scope = "lineup第三版BOM(工序流程)", Content = "本站的项目信息是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 42, Scope = "lineup第三版BOM(工序流程)", Content = "本站的客户要求节拍是否漏写", FixedRemark = "" },
            new SignoffRowDef { Seq = 43, Scope = "lineup第三版BOM(工序流程)", Content = "动作流程要按照：什么东西怎么样触发什么信号后执行了什么动作而造成什么结果", FixedRemark = "" },
            new SignoffRowDef { Seq = 44, Scope = "lineup第三版BOM(工序流程)", Content = "写动作流程内容要有编号及编号的描述", FixedRemark = "" },
            new SignoffRowDef { Seq = 45, Scope = "lineup第三版BOM(工序流程)", Content = "同步动作是否写", FixedRemark = "" },
            new SignoffRowDef { Seq = 46, Scope = "lineup第三版BOM(工序流程)", Content = "每个步骤的节拍是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 47, Scope = "lineup第三版BOM(工序流程)", Content = "动作流程是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 48, Scope = "lineup第三版CAD插图", Content = "第一页整站俯视图与布局保持一致", FixedRemark = "" },
            new SignoffRowDef { Seq = 49, Scope = "lineup第三版CAD插图", Content = "整站布局标明流向及组件名称，组件名称与BOM里的备注名称保持一致", FixedRemark = "" },
            new SignoffRowDef { Seq = 50, Scope = "lineup第三版CAD插图", Content = "每个模块的组件名称是否漏标，是否与整站上标的名称一致", FixedRemark = "" },
            new SignoffRowDef { Seq = 51, Scope = "lineup第三版CAD插图", Content = "每个模块上的线体流向是否有标", FixedRemark = "" },
            new SignoffRowDef { Seq = 52, Scope = "lineup第三版CAD插图", Content = "编号在图上是否都有", FixedRemark = "", AuditCodes = new[] { "CAD_MISSING" } },
            new SignoffRowDef { Seq = 53, Scope = "lineup第三版CAD插图", Content = "编号箭头指向是否正确", FixedRemark = "", AuditCodes = new[] { "CAD_UNRESOLVED" } },
            new SignoffRowDef { Seq = 54, Scope = "lineup第三版CAD插图", Content = "编号有无重复", FixedRemark = "", AuditCodes = new[] { "CAD_DUP", "NUM_DUP" } },
            new SignoffRowDef { Seq = 55, Scope = "lineup第三版CAD插图", Content = "气源处理在图上是否有标", FixedRemark = "" },
            new SignoffRowDef { Seq = 56, Scope = "lineup第三版CAD插图", Content = "阀岛、单阀片、汇流板在图上是否有标", FixedRemark = "" },
            new SignoffRowDef { Seq = 57, Scope = "lineup第三版CAD插图", Content = "插图上关于1#与2#的箭头指向是否按标准来", FixedRemark = "" },
            new SignoffRowDef { Seq = 58, Scope = "lineup第三版气路图", Content = "图幅大小，符号大小，字体大小都不能改", FixedRemark = "用A4纸打印出来需看得清" },
            new SignoffRowDef { Seq = 59, Scope = "lineup第三版气路图", Content = "共用气源是否标注清楚", FixedRemark = "" },
            new SignoffRowDef { Seq = 60, Scope = "lineup第三版气路图", Content = "用阀岛还是汇流板图示是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 61, Scope = "lineup第三版气路图", Content = "节流阀螺纹的大小是否标注正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 62, Scope = "lineup第三版气路图", Content = "插管的大小是否标准正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 63, Scope = "lineup第三版气路图", Content = "电磁阀的型号及符号是否正确", FixedRemark = "" },
            new SignoffRowDef { Seq = 64, Scope = "lineup第三版气路图", Content = "真空从三联件内引出，不与正压分一路气", FixedRemark = "" }
        };

        public static LineupXlsxSummary Write(string outputPath, LineupProject project, LineupXlsxOptions options = null) {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("输出路径为空。");
            if (project == null) throw new ArgumentNullException("project");
            options = options ?? new LineupXlsxOptions {
                Export11Columns = project.Export11Columns,
                MarkBomChanges = project.MarkBomChanges
            };

            var audit = LineupAudit.Run(project);
            var sst = new SstBuilder();
            var media = new List<MediaItem>();

            // 1. 先生成 BOM，拿到 编号 -> BOM 行号 的精确映射，供 CAD 页公式联动
            Dictionary<string, int> bomRowByNum;
            int bomDataCount, bomAddedCount, bomRemovedCount;
            string bomXml = BuildBomSheet(project, options, audit, sst, out bomRowByNum, out bomDataCount, out bomAddedCount, out bomRemovedCount);

            // 2. 更新日志 & 第一版配置表 & 签字页
            int changelogCount;
            string logXml = BuildChangelogSheet(project, options, sst, out changelogCount);
            string cfgXml = BuildConfigSheet(project, sst);
            string auditXml = BuildAuditSheet(project, audit, sst);

            // 3. 第三版 CAD 位置图页 + 原生 Drawing XML（图片 + 从图指向单元格的红色箭头）
            string drawingXml, drawingRelsXml;
            int cadPageCount, cadCalloutCount, cadArrowCount;
            string cadXml = BuildCadSheet(project, options, bomRowByNum, sst, media, out drawingXml, out drawingRelsXml, out cadPageCount, out cadCalloutCount, out cadArrowCount);

            bool hasDrawing = !string.IsNullOrEmpty(drawingXml);

            // 4. 打包成标准 .xlsx（先写临时文件再原子替换）
            string fullOut = Path.GetFullPath(outputPath);
            string dir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = fullOut + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Utf8NoBom)) {
                    WriteTextEntry(zip, "[Content_Types].xml", BuildContentTypes(hasDrawing, media));
                    WriteTextEntry(zip, "_rels/.rels", BuildRootRels());
                    WriteTextEntry(zip, "xl/workbook.xml", BuildWorkbookXml());
                    WriteTextEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels());
                    WriteTextEntry(zip, "xl/styles.xml", BuildStylesXml());
                    WriteTextEntry(zip, "xl/sharedStrings.xml", sst.ToXml());
                    WriteTextEntry(zip, "xl/worksheets/sheet1.xml", logXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet2.xml", cfgXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet3.xml", bomXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet4.xml", cadXml);
                    WriteTextEntry(zip, "xl/worksheets/sheet5.xml", auditXml);
                    if (hasDrawing) {
                        WriteTextEntry(zip, "xl/worksheets/_rels/sheet4.xml.rels", BuildSheetDrawingRels());
                        WriteTextEntry(zip, "xl/drawings/drawing1.xml", drawingXml);
                        WriteTextEntry(zip, "xl/drawings/_rels/drawing1.xml.rels", drawingRelsXml);
                        foreach (var m in media) WriteBytesEntry(zip, m.ZipPath, m.Bytes);
                    }
                }
                // 原子替换目标文件，避免先删后移在杀软/DLP 拦截 tmp 时导致原有 .xlsx 永久丢失
                try {
                    if (File.Exists(fullOut)) File.Replace(tmp, fullOut, null);
                    else File.Move(tmp, fullOut);
                } catch (IOException ex) {
                    int hr = ex.HResult & 0xFFFF;
                    if (hr == 32 || hr == 33) {
                        throw new IOException("「" + Path.GetFileName(fullOut) + "」正被别的程序（多半是 Excel / WPS）打开着，无法覆盖写入。请先关闭它或换个文件名导出。", ex);
                    }
                    throw;
                }
            } finally {
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
            }

            return new LineupXlsxSummary {
                OutputPath = fullOut,
                BomRows = bomDataCount,
                BomAddedRows = bomAddedCount,
                BomRemovedRows = bomRemovedCount,
                ChangelogRows = changelogCount,
                CadPages = cadPageCount,
                CadCallouts = cadCalloutCount,
                CadArrows = cadArrowCount,
                AuditErrors = audit.ErrorCount,
                AuditWarnings = audit.WarningCount
            };
        }

        // ==================== Sheet 1: LineUP更新日志（严格对齐模板样式，支持多版本累加） ====================
        static string BuildChangelogSheet(LineupProject p, LineupXlsxOptions opt, SstBuilder sst, out int logCount) {
            var entries = new List<LineupChangeLog>();
            if (p.ChangeLogs != null && p.ChangeLogs.Count > 0) {
                entries.AddRange(p.ChangeLogs);
            } else {
                string todayText = DateTime.Today.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
                string modelText = todayText;
                try {
                    if (!string.IsNullOrEmpty(p.AssemblyFile) && File.Exists(p.AssemblyFile)) {
                        modelText = File.GetLastWriteTime(p.AssemblyFile).ToString("yyyy/M/d", CultureInfo.InvariantCulture);
                    }
                } catch { }
                string content = !string.IsNullOrWhiteSpace(opt.VersionNote)
                    ? opt.VersionNote.Trim()
                    : ((p.CadPages != null && p.CadPages.Count > 0) ? "导出 LineUP第二版_BOM 及 LineUP第三版_CAD（共 " + p.CadPages.Count + " 页位置图）" : "导出 LineUP第二版_BOM");
                entries.Add(new LineupChangeLog {
                    Date = todayText,
                    Version = string.IsNullOrWhiteSpace(p.Revision) ? "V1.0" : p.Revision.Trim(),
                    ModelDate = modelText,
                    Content = content,
                    Notes = "由天工工具箱自动导出",
                    Designer = p.Designer ?? "",
                    Editor = p.Editor ?? "",
                    Approver = ""
                });
            }
            logCount = entries.Count;

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultColWidth=\"9\" defaultRowHeight=\"23.95\" customHeight=\"1\"/>");
            sb.Append("<cols>");
            double[] ws = { 12.83, 18.33, 12.83, 34.63, 50.83, 50.83, 20.13, 17.13, 29.38 };
            for (int i = 0; i < ws.Length; i++) sb.Append(ColDef(i + 1, ws[i]));
            sb.Append("</cols><sheetData>");

            // Row 1: 合并 A1:I1，宋体 16B 白底黑粗外框
            sb.Append("<row r=\"1\" ht=\"28\" customHeight=\"1\">");
            sb.Append(CellStr("A1", "LineUP更新日志", S_LogTitleL, sst));
            for (int c = 2; c <= 8; c++) sb.Append(CellBlank(ColName(c) + "1", S_LogTitleM));
            sb.Append(CellBlank("I1", S_LogTitleR));
            sb.Append("</row>");

            // Row 2: 9 列表头（宋体 10 常规白底居中，左右粗外框）
            string[] headers = { "序号", "日期", "版本号", "数模提供日期", "更新内容", "备注", "设计人", "更新人", "确认人（机械/Lineup/电气）" };
            sb.Append("<row r=\"2\" ht=\"22\" customHeight=\"1\">");
            for (int c = 0; c < headers.Length; c++) {
                int st = c == 0 ? S_LogHdrL : (c == headers.Length - 1 ? S_LogHdrR : S_LogHdrM);
                sb.Append(CellStr(ColName(c + 1) + "2", headers[c], st, sst));
            }
            sb.Append("</row>");

            // Rows 3..: 顺序写出全部历次版本记录
            int row = 3;
            for (int i = 0; i < entries.Count; i++) {
                var log = entries[i];
                double rHt = EstimateRowHeight(new[] { log.Content, log.Notes, log.Approver }, new[] { ws[4], ws[5], ws[8] }, 24.0);
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(rHt)).Append("\" customHeight=\"1\">");
                sb.Append(CellNum("A" + row, i + 1, S_LogDataL));
                sb.Append(CellDateOrStr("B" + row, log.Date, S_LogDataDate, S_LogDataCenter, sst));
                sb.Append(string.IsNullOrWhiteSpace(log.Version) ? CellBlank("C" + row, S_LogDataCenter) : CellStr("C" + row, log.Version.Trim(), S_LogDataCenter, sst));
                sb.Append(CellDateOrStr("D" + row, log.ModelDate, S_LogDataDate, S_LogDataCenter, sst));
                sb.Append(string.IsNullOrWhiteSpace(log.Content) ? CellBlank("E" + row, S_LogDataLeft) : CellStr("E" + row, log.Content, S_LogDataLeft, sst));
                sb.Append(string.IsNullOrWhiteSpace(log.Notes) ? CellBlank("F" + row, S_LogDataCenter) : CellStr("F" + row, log.Notes, S_LogDataCenter, sst));
                sb.Append(string.IsNullOrWhiteSpace(log.Designer) ? CellBlank("G" + row, S_LogDataCenter) : CellStr("G" + row, log.Designer.Trim(), S_LogDataCenter, sst));
                sb.Append(string.IsNullOrWhiteSpace(log.Editor) ? CellBlank("H" + row, S_LogDataCenter) : CellStr("H" + row, log.Editor.Trim(), S_LogDataCenter, sst));
                sb.Append(string.IsNullOrWhiteSpace(log.Approver) ? CellBlank("I" + row, S_LogDataR) : CellStr("I" + row, log.Approver.Trim(), S_LogDataR, sst));
                sb.Append("</row>");
                row++;
            }

            // 预留空白日志行（最少填满到第 20 行；超过 18 条时额外留 2 行空白）
            int endBlankRow = Math.Max(20, row + 1);
            for (; row <= endBlankRow; row++) {
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"24\" customHeight=\"1\">");
                sb.Append(CellBlank("A" + row, S_LogDataL));
                sb.Append(CellBlank("B" + row, S_LogDataDate));
                sb.Append(CellBlank("C" + row, S_LogDataCenter));
                sb.Append(CellBlank("D" + row, S_LogDataCenter));
                sb.Append(CellBlank("E" + row, S_LogDataLeft));
                sb.Append(CellBlank("F" + row, S_LogDataCenter));
                sb.Append(CellBlank("G" + row, S_LogDataCenter));
                sb.Append(CellBlank("H" + row, S_LogDataCenter));
                sb.Append(CellBlank("I" + row, S_LogDataR));
                sb.Append("</row>");
            }

            // 底部亮黄底备注合并区（紧接记录区下方，永不遮挡历史日志）
            int remStart = row;
            string remark = "备注：\n1、LineUP有更新内容，需在“LineUP更新日志”中做好记录，方便其他人员知道版本变化内容\n2、在“LineUP第二版_BOM”表中，新增的电气元件使用红色打底标记，取消的电气元件使用红色打底标记加删除线，不允许直接删除“LineUP第二版_BOM”表中的内容";
            sb.Append("<row r=\"").Append(remStart).Append("\" ht=\"26\" customHeight=\"1\">");
            sb.Append(CellStr("A" + remStart, remark, S_LogRemTL, sst));
            for (int c = 2; c <= 8; c++) sb.Append(CellBlank(ColName(c) + remStart, S_LogRemTM));
            sb.Append(CellBlank("I" + remStart, S_LogRemTR));
            sb.Append("</row>");
            sb.Append("<row r=\"").Append(remStart + 1).Append("\" ht=\"26\" customHeight=\"1\">");
            sb.Append(CellBlank("A" + (remStart + 1), S_LogRemML));
            for (int c = 2; c <= 8; c++) sb.Append(CellBlank(ColName(c) + (remStart + 1), S_LogRemMM));
            sb.Append(CellBlank("I" + (remStart + 1), S_LogRemMR));
            sb.Append("</row>");
            sb.Append("<row r=\"").Append(remStart + 2).Append("\" ht=\"26\" customHeight=\"1\">");
            sb.Append(CellBlank("A" + (remStart + 2), S_LogRemBL));
            for (int c = 2; c <= 8; c++) sb.Append(CellBlank(ColName(c) + (remStart + 2), S_LogRemBM));
            sb.Append(CellBlank("I" + (remStart + 2), S_LogRemBR));
            sb.Append("</row>");

            sb.Append("</sheetData><mergeCells count=\"2\"><mergeCell ref=\"A1:I1\"/><mergeCell ref=\"A").Append(remStart).Append(":I").Append(remStart + 2).Append("\"/></mergeCells>");
            sb.Append(LandscapePageSetup(1, 1));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        static string CellDateOrStr(string cellRef, string text, int dateStyle, int strStyle, SstBuilder sst) {
            if (string.IsNullOrWhiteSpace(text)) return CellBlank(cellRef, dateStyle);
            int serial;
            if (LineupRevision.TryParseDateSerial(text, out serial)) return CellNum(cellRef, serial, dateStyle);
            return CellStr(cellRef, text.Trim(), strStyle, sst);
        }

        // ==================== Sheet 2: LineUP第一版_配置表（严格对齐模板 8 列黄底样式） ====================
        static string BuildConfigSheet(LineupProject p, SstBuilder sst) {
            var records = (p.Records ?? new List<LineupRecord>()).Where(r => LineupTableData.Categories.Contains(LineupTableData.Category(r.Category))).ToList();

            var sb = new StringBuilder();
            var merges = new List<string>();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultColWidth=\"23.83\" defaultRowHeight=\"20\" customHeight=\"1\"/>");
            sb.Append("<cols>");
            double[] ws = { 12.0, 26.0, 36.0, 18.0, 12.0, 18.0, 26.0, 24.0 };
            for (int i = 0; i < ws.Length; i++) sb.Append(ColDef(i + 1, ws[i]));
            sb.Append("</cols><sheetData>");

            // Row 1: 8 列亮黄底 #FFFF00 粗体表头（序号 | 名称 | 型号 | 品牌 | 数量 | 阀片（数量） | 备注 | 机构）
            string[] hdr = { "序号", "名称", "型号", "品牌", "数量", "阀片（数量）", "备注", "机构" };
            sb.Append("<row r=\"1\" ht=\"28\" customHeight=\"1\">");
            for (int c = 0; c < hdr.Length; c++) sb.Append(CellStr(ColName(c + 1) + "1", hdr[c], S_CfgHdr, sst));
            sb.Append("</row>");

            // 统计机构首次出现顺序，确保同机构行连续排列便于 H 列纵向合并
            var mechOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records) {
                string mk = (r.Mechanism ?? "").Trim();
                if (mk.Length > 0 && !mechOrder.ContainsKey(mk)) mechOrder[mk] = mechOrder.Count;
            }

            var grouped = records
                .GroupBy(r => new {
                    Mech = (r.Mechanism ?? "").Trim(),
                    Cat = LineupTableData.Category(r.Category),
                    Model = (r.Model ?? "").Trim(),
                    Brand = (r.Brand ?? "").Trim()
                })
                .OrderBy(g => g.Key.Mech.Length > 0 ? mechOrder[g.Key.Mech] : int.MaxValue)
                .ThenBy(g => Array.IndexOf(LineupTableData.Categories, g.Key.Cat))
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key.Model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int row = 2;
            int seq = 1;
            int mechMergeStart = -1;
            string mechMergeVal = "";

            foreach (var g in grouped) {
                string names = string.Join("、", g.Select(x => (x.Description ?? "").Trim()).Where(s => s.Length > 0).Distinct().Take(3).ToArray());
                string label = names.Length > 0 ? names : g.Key.Cat;
                string notes = string.Join("、", g.Select(x => (x.Notes ?? "").Trim()).Where(s => s.Length > 0).Distinct().Take(3).ToArray());
                if (notes.Length == 0) {
                    notes = string.Join("、", g.Select(x => (x.Connector ?? "").Trim()).Where(s => s.Length > 0).Distinct().Take(2).ToArray());
                }

                // 阀片（数量）：阀片类直接显示阀片数，气缸类统计关联的独立控制阀片数
                int valveQty = 0;
                if (g.Key.Cat == "阀片") {
                    valveQty = g.Count();
                } else if (g.Key.Cat == "气缸") {
                    var vset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var x in g) {
                        string vn = LineupNumbering.ValveNumberForCylinder(x.Number);
                        if (!string.IsNullOrEmpty(vn)) vset.Add(vn);
                    }
                    valveQty = vset.Count;
                }

                string mech = g.Key.Mech;
                bool writeMechText = false;
                if (mech.Length > 0) {
                    if (mechMergeStart > 0 && string.Equals(mech, mechMergeVal, StringComparison.OrdinalIgnoreCase)) {
                        writeMechText = false;
                    } else {
                        if (mechMergeStart > 0 && row - 1 > mechMergeStart) merges.Add("H" + mechMergeStart + ":H" + (row - 1));
                        mechMergeStart = row;
                        mechMergeVal = mech;
                        writeMechText = true;
                    }
                } else {
                    if (mechMergeStart > 0 && row - 1 > mechMergeStart) merges.Add("H" + mechMergeStart + ":H" + (row - 1));
                    mechMergeStart = -1;
                    mechMergeVal = "";
                }

                double ht = EstimateRowHeight(new[] { label, g.Key.Model, notes }, new[] { ws[1], ws[2], ws[6] }, 22.0);
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(ht)).Append("\" customHeight=\"1\">");
                sb.Append(CellNum("A" + row, seq++, S_CfgInt));
                sb.Append(CellStr("B" + row, label, S_CfgData, sst));
                sb.Append(string.IsNullOrEmpty(g.Key.Model) ? CellBlank("C" + row, S_CfgData) : CellStr("C" + row, g.Key.Model, S_CfgData, sst));
                sb.Append(string.IsNullOrEmpty(g.Key.Brand) ? CellBlank("D" + row, S_CfgData) : CellStr("D" + row, g.Key.Brand, S_CfgData, sst));
                sb.Append(CellNum("E" + row, g.Count(), S_CfgInt));
                sb.Append(valveQty > 0 ? CellNum("F" + row, valveQty, S_CfgInt) : CellBlank("F" + row, S_CfgData));
                sb.Append(string.IsNullOrEmpty(notes) ? CellBlank("G" + row, S_CfgData) : CellStr("G" + row, notes, S_CfgData, sst));
                sb.Append(writeMechText ? CellStr("H" + row, mech, S_CfgData, sst) : CellBlank("H" + row, S_CfgData));
                sb.Append("</row>");
                row++;
            }

            if (mechMergeStart > 0 && row - 1 > mechMergeStart) {
                merges.Add("H" + mechMergeStart + ":H" + (row - 1));
            }

            // 若清单为空，至少生成 10 行空白格以保持配置表标准版式
            int minEndRow = Math.Max(row, 12);
            while (row < minEndRow) {
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"20\" customHeight=\"1\">");
                for (int c = 1; c <= 8; c++) sb.Append(CellBlank(ColName(c) + row, S_CfgData));
                sb.Append("</row>");
                row++;
            }

            // 底部黄底备注合并区（与模板 A104:F105 一致，整行横向跨 A..H 两行更齐整）
            int remStart = row;
            string cfgNote = "备注：与电气相关的元器件都需要填写到表格里。\n填写时间为：预审3天内给出初版";
            sb.Append("<row r=\"").Append(remStart).Append("\" ht=\"24\" customHeight=\"1\">");
            sb.Append(CellStr("A" + remStart, cfgNote, S_CfgRemark, sst));
            for (int c = 2; c <= 8; c++) sb.Append(CellBlank(ColName(c) + remStart, S_CfgRemark));
            sb.Append("</row>");
            sb.Append("<row r=\"").Append(remStart + 1).Append("\" ht=\"24\" customHeight=\"1\">");
            for (int c = 1; c <= 8; c++) sb.Append(CellBlank(ColName(c) + (remStart + 1), S_CfgRemark));
            sb.Append("</row>");
            merges.Add("A" + remStart + ":H" + (remStart + 1));

            sb.Append("</sheetData>");
            if (merges.Count > 0) {
                sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
                foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
                sb.Append("</mergeCells>");
            }
            sb.Append(LandscapePageSetup(1, 0));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // ==================== Sheet 3: LineUP第二版_BOM（严格对齐模板 V3.0 样式 + 红底/删除线变更标记） ====================
        static string BuildBomSheet(
            LineupProject p, LineupXlsxOptions opt, LineupAuditResult audit, SstBuilder sst,
            out Dictionary<string, int> bomRowByNum, out int dataCount, out int addedCount, out int removedCount) {

            bomRowByNum = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            dataCount = 0;
            addedCount = 0;
            removedCount = 0;

            LineupBomDiff diff = (opt.MarkBomChanges && p.Baseline != null) ? LineupRevision.CompareToBaseline(p) : null;

            bool col11 = opt.Export11Columns;
            int colCount = col11 ? 11 : 10;
            string[] headers = col11 ? BomTemplateHeaders11 : BomTemplateHeaders10;
            double[] widths = col11 ? BomTemplateWidths11 : BomTemplateWidths10;
            string lastCol = ColName(colCount);

            var warnIds = new HashSet<string>();
            if (opt.HighlightAudit && audit != null) {
                foreach (var iss in audit.Issues) {
                    if (iss.Severity != LineupAuditSeverity.Info && !string.IsNullOrEmpty(iss.RecordId)) warnIds.Add(iss.RecordId);
                }
            }

            var sb = new StringBuilder();
            var merges = new List<string>();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView tabSelected=\"1\" zoomScale=\"85\" zoomScaleNormal=\"85\" workbookViewId=\"0\"><pane xSplit=\"1\" ySplit=\"1\" topLeftCell=\"B2\" activePane=\"bottomRight\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultColWidth=\"9.33\" defaultRowHeight=\"20\" customHeight=\"1\"/>");
            sb.Append("<cols>");
            for (int i = 0; i < widths.Length; i++) sb.Append(ColDef(i + 1, widths[i]));
            sb.Append("</cols><sheetData>");

            // Row 1: 亮黄表头 + B1 浅橙底色（宋体 12B 居中换行，粗顶边；40pt 高度为自动筛选下拉按钮留足双行显示空间）
            sb.Append("<row r=\"1\" ht=\"40\" customHeight=\"1\">");
            for (int c = 0; c < colCount; c++) {
                int st = c == 1 ? S_BomHdrPeach : (c == colCount - 1 ? S_BomHdrYellowR : S_BomHdrYellow);
                sb.Append(CellStr(ColName(c + 1) + "1", headers[c], st, sst));
            }
            sb.Append("</row>");

            int row = 2;

            for (int ci = 0; ci < LineupTableData.Categories.Length; ci++) {
                string cat = LineupTableData.Categories[ci];
                var saved = (p.Sections ?? new List<LineupSection>()).LastOrDefault(s => s.Category == cat);
                string title = saved != null && saved.Cells != null && saved.Cells.Length > 0 && !string.IsNullOrWhiteSpace(saved.Cells[0])
                    ? saved.Cells[0].Trim()
                    : DefaultBomSectionTitle(ci);
                if (cat == "普通传感器" && title.IndexOf("常开", StringComparison.Ordinal) < 0 && title.IndexOf("常闭", StringComparison.Ordinal) < 0) {
                    string ncNote = saved != null && saved.Cells != null && saved.Cells.Length > 6 && !string.IsNullOrWhiteSpace(saved.Cells[6])
                        ? saved.Cells[6].Trim()
                        : "传感器常开/常闭（只有常闭的需要填写）";
                    title = title + "  " + ncNote;
                }

                // 灰底居中分区横条（与模板 #BFBFBF / #E3E3E3 完全一致）
                int bannerStyle = (ci == 1 || ci == 2) ? S_BomSecLightGray : S_BomSecGray;
                double bannerHt = ci == 1 ? 30.0 : (ci == 2 ? 26.0 : 22.0);
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(bannerHt)).Append("\" customHeight=\"1\">");
                sb.Append(CellStr("A" + row, title, bannerStyle, sst));
                for (int c = 2; c <= colCount; c++) sb.Append(CellBlank(ColName(c) + row, bannerStyle));
                merges.Add("A" + row + ":" + lastCol + row);
                sb.Append("</row>");
                row++;

                var items = (p.Records ?? new List<LineupRecord>()).Where(r => LineupTableData.Category(r.Category) == cat).ToList();

                // 跟踪阀片同阀岛/汇流排纵向合并范围（H 列 = 第 8 列）
                int valveMergeStart = -1;
                string valveMergeLabel = "";
                bool valveMergeAdded = false;
                // 跟踪 11 列模式同机构名称纵向合并范围（J 列 = 第 10 列）
                int mechMergeStart = -1;
                string mechMergeVal = "";
                bool mechMergeAdded = false;

                for (int i = 0; i < items.Count; i++) {
                    var r = items[i];
                    bool isAdded = diff != null && diff.AddedIds.Contains(r.Id);
                    if (isAdded) addedCount++;

                    string[] raw11 = LineupTableData.Cells(r);
                    // raw11 顺序：0:编号 1:地址 2:功能参数 3:描述 4:型号 5:品牌 6:原位 7:安装方式 8:机构名称 9:传感器接口类型 10:备注

                    // 阀片安装方式列处理：同阀岛/汇流排/阀组在首行写入并纵向合并（H 列；不跨正常/新增红底边界合并）
                    bool isValveMergeHead = false;
                    string mountingText = raw11[7] ?? "";
                    if (cat == "阀片") {
                        string ilabel = LineupNumbering.IslandLabel(p, r);
                        if (!string.IsNullOrWhiteSpace(ilabel) && ilabel != "单片阀") {
                            if (valveMergeStart > 0 && valveMergeAdded == isAdded && string.Equals(ilabel, valveMergeLabel, StringComparison.OrdinalIgnoreCase)) {
                                mountingText = "";
                            } else {
                                if (valveMergeStart > 0 && row - 1 > valveMergeStart) merges.Add("H" + valveMergeStart + ":H" + (row - 1));
                                valveMergeStart = row;
                                valveMergeLabel = ilabel;
                                valveMergeAdded = isAdded;
                                isValveMergeHead = true;
                                mountingText = ilabel;
                            }
                        } else {
                            if (valveMergeStart > 0 && row - 1 > valveMergeStart) merges.Add("H" + valveMergeStart + ":H" + (row - 1));
                            valveMergeStart = -1;
                            valveMergeLabel = "";
                            if (ilabel == "单片阀" && string.IsNullOrWhiteSpace(mountingText)) mountingText = "单片阀";
                        }
                    }

                    // 11 列模式：按模板顺序映射（I = 传感器接口类型 raw11[9]，J = 机构名称 raw11[8]，K = 备注 raw11[10]）
                    // 10 列模式：I = 传感器接口类型 raw11[9]，J = 备注 raw11[10]
                    string mechText = (raw11[8] ?? "").Trim();
                    bool isMechMergeBody = false;
                    if (col11) {
                        if (mechText.Length > 0) {
                            if (mechMergeStart > 0 && mechMergeAdded == isAdded && string.Equals(mechText, mechMergeVal, StringComparison.OrdinalIgnoreCase)) {
                                isMechMergeBody = true;
                                mechText = "";
                            } else {
                                if (mechMergeStart > 0 && row - 1 > mechMergeStart) merges.Add("J" + mechMergeStart + ":J" + (row - 1));
                                mechMergeStart = row;
                                mechMergeVal = mechText;
                                mechMergeAdded = isAdded;
                            }
                        } else {
                            if (mechMergeStart > 0 && row - 1 > mechMergeStart) merges.Add("J" + mechMergeStart + ":J" + (row - 1));
                            mechMergeStart = -1;
                            mechMergeVal = "";
                        }
                    }

                    string[] rowVals = col11
                        ? new[] { raw11[0], raw11[1], raw11[2], raw11[3], raw11[4], raw11[5], raw11[6], mountingText, raw11[9], mechText, raw11[10] }
                        : new[] { raw11[0], raw11[1], raw11[2], raw11[3], raw11[4], raw11[5], raw11[6], mountingText, raw11[9], raw11[10] };

                    bool warn = warnIds.Contains(r.Id);

                    // 纵向合并首格不计入单行高度，避免把整段阀岛型号或机构名称撑高到单行
                    var heightVals = new string[colCount];
                    for (int c = 0; c < colCount; c++) {
                        if (c == 7 && isValveMergeHead) heightVals[c] = "";
                        else if (col11 && c == 9 && !isMechMergeBody && mechMergeStart == row) heightVals[c] = "";
                        else heightVals[c] = rowVals[c];
                    }
                    double minHt = cat == "阀片" ? 30.0 : 20.0;
                    double rowHt = EstimateRowHeight(heightVals, widths, minHt);

                    sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(rowHt)).Append("\" customHeight=\"1\">");
                    for (int c = 0; c < colCount; c++) {
                        string val = rowVals[c] ?? "";
                        int st;
                        if (isAdded) {
                            st = c == 0 ? S_BomAddNum : ((col11 && c == 9) ? S_BomAddMech : S_BomAddCell);
                        } else if (c == 0) {
                            st = warn ? S_BomWarnNum : S_BomNum;
                        } else if (c == 6 && cat == "普通传感器") {
                            st = val.IndexOf("常闭", StringComparison.Ordinal) >= 0 ? S_BomSeHomeRed : S_BomSeHome;
                        } else if (c == 6 && cat == "气缸" && val.Trim() == "伸出") {
                            st = warn ? S_BomWarn : S_BomCellRed;
                        } else if (col11 && c == 9) {
                            st = warn ? S_BomWarn : S_BomMech;
                        } else {
                            st = warn ? S_BomWarn : S_BomCell;
                        }
                        string cellRef = ColName(c + 1) + row;
                        if (string.IsNullOrEmpty(val)) sb.Append(CellBlank(cellRef, st));
                        else sb.Append(CellStr(cellRef, val, st, sst));
                    }
                    sb.Append("</row>");

                    string numKey = (r.Number ?? "").Trim();
                    if (numKey.Length > 0 && !bomRowByNum.ContainsKey(numKey)) bomRowByNum[numKey] = row;
                    dataCount++;
                    row++;
                }

                if (cat == "阀片" && valveMergeStart > 0 && row - 1 > valveMergeStart) {
                    merges.Add("H" + valveMergeStart + ":H" + (row - 1));
                }
                if (col11 && mechMergeStart > 0 && row - 1 > mechMergeStart) {
                    merges.Add("J" + mechMergeStart + ":J" + (row - 1));
                }

                // 按公司规范保留本类目取消的电气元件：红底黑字加删除线（绝不物理删除，且不计入 CAD 公式映射）
                if (diff != null && diff.Removed.Count > 0) {
                    var cancelled = diff.Removed.Where(x => LineupTableData.Category(x.Category) == cat).ToList();
                    foreach (var cr in cancelled) {
                        string[] raw11 = LineupTableData.Cells(cr);
                        string[] rowVals = col11
                            ? new[] { raw11[0], raw11[1], raw11[2], raw11[3], raw11[4], raw11[5], raw11[6], raw11[7], raw11[9], raw11[8], raw11[10] }
                            : new[] { raw11[0], raw11[1], raw11[2], raw11[3], raw11[4], raw11[5], raw11[6], raw11[7], raw11[9], raw11[10] };
                        double minHt = cat == "阀片" ? 30.0 : 20.0;
                        double rowHt = EstimateRowHeight(rowVals, widths, minHt);
                        sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(rowHt)).Append("\" customHeight=\"1\">");
                        for (int c = 0; c < colCount; c++) {
                            string val = rowVals[c] ?? "";
                            int st = c == 0 ? S_BomDelNum : ((col11 && c == 9) ? S_BomDelMech : S_BomDelCell);
                            string cellRef = ColName(c + 1) + row;
                            if (string.IsNullOrEmpty(val)) sb.Append(CellBlank(cellRef, st));
                            else sb.Append(CellStr(cellRef, val, st, sst));
                        }
                        sb.Append("</row>");
                        removedCount++;
                        row++;
                    }
                }
            }

            sb.Append("</sheetData>");
            int lastDataRow = Math.Max(2, row - 1);
            sb.Append("<autoFilter ref=\"A1:").Append(lastCol).Append(lastDataRow).Append("\"/>");
            if (merges.Count > 0) {
                sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
                foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
                sb.Append("</mergeCells>");
            }
            sb.Append(LandscapePageSetup(1, 0));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        static string DefaultBomSectionTitle(int ci) {
            switch (ci) {
                case 0: return "CYLINDER LIST (Y*C**)  气缸列表";
                case 1: return "PNEUMATIC SOLENOID/VALVE LIST (Y*V**)  气动线圈/阀片列表 (气动填写) Q小号缩回,大号伸出";
                case 2: return "CYLINDER POSITION SENSOR LIST (原位Y*C**H or 工作位Y*C**W or 中间位Y*C**M)  气缸位置传感器列表,I小号缩回,大号伸出";
                case 3: return "GENERAL CHECK SENSOR LIST (SE**)  普通检测类传感器列表（除气缸位置检测之外的传感器）  传感器常开/常闭（只有常闭的需要填写）";
                default: return "OTHER ELECTRICAL PARTS LIST （E**）其他电气器件      编号和地址由电气填写";
            }
        }

        // ==================== Sheet 4: LineUP第三版_CAD（严格对齐模板 12 列黄头粗框样式 + 图→格箭头） ====================
        static string BuildCadSheet(
            LineupProject p, LineupXlsxOptions opt, Dictionary<string, int> bomRowByNum,
            SstBuilder sst, List<MediaItem> media,
            out string drawingXml, out string drawingRelsXml,
            out int pageCount, out int calloutCount, out int arrowCount) {

            drawingXml = ""; drawingRelsXml = "";
            pageCount = 0; calloutCount = 0; arrowCount = 0;

            var records = p.Records ?? new List<LineupRecord>();
            var byId = new Dictionary<string, LineupRecord>();
            var byNum = new Dictionary<string, LineupRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records) {
                if (!string.IsNullOrEmpty(r.Id) && !byId.ContainsKey(r.Id)) byId[r.Id] = r;
                string k = (r.Number ?? "").Trim();
                if (k.Length > 0 && !byNum.ContainsKey(k)) byNum[k] = r;
            }

            var pages = (opt.IncludeCadPages && p.CadPages != null && p.CadPages.Count > 0)
                ? p.CadPages
                : new List<LineupCadPage> { new LineupCadPage { Name = "总图", Width = 1600, Height = 1000 } };

            var sb = new StringBuilder();
            var merges = new List<string>();
            var rowBreaks = new List<int>();
            var drawAnchors = new StringBuilder();
            int shapeId = 2;

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\" xmlns:r=\"").Append(NsRel).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView showGridLines=\"0\" zoomScale=\"55\" zoomScaleNormal=\"85\" workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultColWidth=\"9\" defaultRowHeight=\"").Append(Fmt(LineupCadLayout.DataRowPt)).Append("\" customHeight=\"1\"/>");
            sb.Append("<cols>");
            for (int c = 0; c < LineupCadLayout.ColWidths.Length; c++) sb.Append(ColDef(c + 1, LineupCadLayout.ColWidths[c]));
            sb.Append("</cols><sheetData>");

            int curRow = 1;
            string projCode = string.IsNullOrWhiteSpace(p.ProjectCode) ? "" : p.ProjectCode.Trim();
            string station = !string.IsNullOrWhiteSpace(p.StationCode) ? p.StationCode.Trim() : (p.NumberPrefix ?? "").Trim();
            string defLine = string.IsNullOrWhiteSpace(p.LineCode) ? "" : p.LineCode.Trim();
            string assetsDir = !string.IsNullOrWhiteSpace(opt.CadAssetsDir) ? opt.CadAssetsDir : LineupProject.CadAssetsDir(p.AssemblyFile);

            for (int pi = 0; pi < pages.Count; pi++) {
                var page = pages[pi];
                var plan = LineupCadLayout.Solve(page, curRow, byId, byNum, bomRowByNum);
                pageCount++;

                // 1. 每页顶部 3 行：左右白底粗体列头（A:名称/描述 B:IO点位 C:SK编号 | J:SK编号 K:IO点位 L:名称/描述），中央 D..I 亮黄底粗框信息块
                int r0 = plan.StartRow;
                int r1 = plan.StartRow + 1;
                int r2 = plan.StartRow + 2;
                string lineCode = !string.IsNullOrWhiteSpace(page.LineCode) ? page.LineCode.Trim() : defLine;
                string pageName = string.IsNullOrWhiteSpace(page.Name) ? "总图" : page.Name.Trim();

                sb.Append("<row r=\"").Append(r0).Append("\" ht=\"").Append(Fmt(LineupCadLayout.HeaderRow0Pt)).Append("\" customHeight=\"1\">");
                sb.Append(CellStr("A" + r0, "名称/描述", S_CadSideHdrC, sst));
                sb.Append(CellStr("B" + r0, "IO点位", S_CadSideHdrC, sst));
                sb.Append(CellStr("C" + r0, "SK编号", S_CadSideHdrR, sst));
                sb.Append(CellStr("D" + r0, "插图区域", S_CadYelTL, sst));
                for (int c = 5; c <= 8; c++) sb.Append(CellBlank(ColName(c) + r0, S_CadYelTM));
                sb.Append(CellBlank("I" + r0, S_CadYelTR));
                sb.Append(CellStr("J" + r0, "SK编号", S_CadSideHdrL, sst));
                sb.Append(CellStr("K" + r0, "IO点位", S_CadSideHdrC, sst));
                sb.Append(CellStr("L" + r0, "名称/描述", S_CadSideHdrL, sst));
                sb.Append("</row>");

                sb.Append("<row r=\"").Append(r1).Append("\" ht=\"").Append(Fmt(LineupCadLayout.HeaderRow1Pt)).Append("\" customHeight=\"1\">");
                sb.Append(CellBlank("A" + r1, S_CadSideHdrC));
                sb.Append(CellBlank("B" + r1, S_CadSideHdrC));
                sb.Append(CellBlank("C" + r1, S_CadSideHdrR));
                sb.Append(CellStr("D" + r1, "项目编号", S_CadYelML, sst));
                sb.Append(string.IsNullOrEmpty(projCode) ? CellBlank("E" + r1, S_CadYelMM) : CellStr("E" + r1, projCode, S_CadYelMM, sst));
                sb.Append(CellBlank("F" + r1, S_CadYelMM));
                sb.Append(CellStr("G" + r1, "工位号", S_CadYelMM, sst));
                sb.Append(string.IsNullOrEmpty(station) ? CellBlank("H" + r1, S_CadYelMM) : CellStr("H" + r1, station, S_CadYelMM, sst));
                sb.Append(CellBlank("I" + r1, S_CadYelMR));
                sb.Append(CellBlank("J" + r1, S_CadSideHdrL));
                sb.Append(CellBlank("K" + r1, S_CadSideHdrC));
                sb.Append(CellBlank("L" + r1, S_CadSideHdrL));
                sb.Append("</row>");

                sb.Append("<row r=\"").Append(r2).Append("\" ht=\"").Append(Fmt(LineupCadLayout.HeaderRow2Pt)).Append("\" customHeight=\"1\">");
                sb.Append(CellBlank("A" + r2, S_CadSideHdrC));
                sb.Append(CellBlank("B" + r2, S_CadSideHdrC));
                sb.Append(CellBlank("C" + r2, S_CadSideHdrR));
                sb.Append(CellStr("D" + r2, "线体号", S_CadYelBL, sst));
                sb.Append(string.IsNullOrEmpty(lineCode) ? CellBlank("E" + r2, S_CadYelBM) : CellStr("E" + r2, lineCode, S_CadYelBM, sst));
                sb.Append(CellBlank("F" + r2, S_CadYelBM));
                sb.Append(CellStr("G" + r2, "名称", S_CadYelBM, sst));
                sb.Append(CellStr("H" + r2, pageName, S_CadYelBM, sst));
                sb.Append(CellBlank("I" + r2, S_CadYelBR));
                sb.Append(CellBlank("J" + r2, S_CadSideHdrL));
                sb.Append(CellBlank("K" + r2, S_CadSideHdrC));
                sb.Append(CellBlank("L" + r2, S_CadSideHdrL));
                sb.Append("</row>");

                merges.Add("A" + r0 + ":A" + r2);
                merges.Add("B" + r0 + ":B" + r2);
                merges.Add("C" + r0 + ":C" + r2);
                merges.Add("D" + r0 + ":I" + r0);
                merges.Add("E" + r1 + ":F" + r1);
                merges.Add("H" + r1 + ":I" + r1);
                merges.Add("E" + r2 + ":F" + r2);
                merges.Add("H" + r2 + ":I" + r2);
                merges.Add("J" + r0 + ":J" + r2);
                merges.Add("K" + r0 + ":K" + r2);
                merges.Add("L" + r0 + ":L" + r2);

                // 2. 索引本页左右标注行
                var leftByRow = new Dictionary<int, LineupCadCallout>();
                var rightByRow = new Dictionary<int, LineupCadCallout>();
                foreach (var co in plan.Callouts) {
                    calloutCount++;
                    if (co.Side == "left") leftByRow[co.ExcelRow] = co;
                    else rightByRow[co.ExcelRow] = co;
                }

                // 3. 写入本页全部数据行（显式固定 20pt 行高 + 中央 D..I 粗黑外框纯白画布）
                for (int r = plan.DataStartRow; r <= plan.DataEndRow; r++) {
                    LineupCadCallout lc, rc;
                    leftByRow.TryGetValue(r, out lc);
                    rightByRow.TryGetValue(r, out rc);
                    bool isTop = r == plan.DataStartRow;
                    bool isBot = r == plan.DataEndRow;

                    sb.Append("<row r=\"").Append(r).Append("\" ht=\"").Append(Fmt(LineupCadLayout.DataRowPt)).Append("\" customHeight=\"1\">");
                    if (lc != null) WriteLeftCalloutCells(sb, r, lc, sst);
                    else {
                        sb.Append(CellBlank("A" + r, S_CadLeftDesc));
                        sb.Append(CellBlank("B" + r, S_CadIo));
                        sb.Append(CellBlank("C" + r, S_CadLeftNum));
                    }

                    // 中央 D..I 画布外框（仅四边粗黑线，内部全无框线）
                    int fL = isTop ? S_CadFrameTL : (isBot ? S_CadFrameBL : S_CadFrameML);
                    int fM = isTop ? S_CadFrameTM : (isBot ? S_CadFrameBM : S_CadFrameMM);
                    int fR = isTop ? S_CadFrameTR : (isBot ? S_CadFrameBR : S_CadFrameMR);
                    sb.Append(CellBlank("D" + r, fL));
                    for (int c = 5; c <= 8; c++) sb.Append(CellBlank(ColName(c) + r, fM));
                    sb.Append(CellBlank("I" + r, fR));

                    if (rc != null) WriteRightCalloutCells(sb, r, rc, sst);
                    else {
                        sb.Append(CellBlank("J" + r, S_CadRightNum));
                        sb.Append(CellBlank("K" + r, S_CadIo));
                        sb.Append(CellBlank("L" + r, S_CadRightDesc));
                    }
                    sb.Append("</row>");
                }

                // 4. 嵌入图片（优先解析相对路径到 assetsDir）
                string imgPath = "";
                if (!string.IsNullOrWhiteSpace(page.ImageFile)) {
                    imgPath = Path.IsPathRooted(page.ImageFile)
                        ? page.ImageFile
                        : (!string.IsNullOrEmpty(assetsDir) ? Path.Combine(assetsDir, page.ImageFile) : page.ImageFile);
                }
                if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath)) {
                    byte[] bytes = File.ReadAllBytes(imgPath);
                    string ext = Path.GetExtension(imgPath).TrimStart('.').ToLowerInvariant();
                    if (ext != "png" && ext != "jpg" && ext != "jpeg") ext = "png";
                    string relId = "rId" + (media.Count + 1);
                    string zipPath = "xl/media/image" + (media.Count + 1) + "." + ext;
                    media.Add(new MediaItem { RelId = relId, ZipPath = zipPath, Ext = ext, Bytes = bytes });
                    drawAnchors.Append(BuildPicAnchorXml(shapeId++, "LineupView_" + (pi + 1), relId, plan.ImageFrom, plan.ImageTo));
                }

                // 5. 写入本页所有原生红色指引箭头（straightConnector1，严格从图指向单元格）
                foreach (var co in plan.Callouts) {
                    if (!co.HasArrow) continue;
                    arrowCount++;
                    drawAnchors.Append(BuildArrowAnchorXml(shapeId++, "LineupArrow_" + arrowCount, co));
                }

                if (pi < pages.Count - 1) rowBreaks.Add(plan.DataEndRow);
                curRow = plan.DataEndRow + 1;
            }

            sb.Append("</sheetData>");
            if (merges.Count > 0) {
                sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
                foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
                sb.Append("</mergeCells>");
            }
            sb.Append(CadPageSetup());
            if (rowBreaks.Count > 0) {
                sb.Append("<rowBreaks count=\"").Append(rowBreaks.Count).Append("\" manualBreakCount=\"").Append(rowBreaks.Count).Append("\">");
                foreach (int br in rowBreaks) sb.Append("<brk id=\"").Append(br).Append("\" max=\"11\" man=\"1\"/>");
                sb.Append("</rowBreaks>");
            }
            if (drawAnchors.Length > 0) {
                sb.Append("<drawing r:id=\"rId1\"/>");
                drawingXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                    + "<xdr:wsDr xmlns:xdr=\"" + NsXdr + "\" xmlns:a=\"" + NsA + "\" xmlns:r=\"" + NsRel + "\">"
                    + drawAnchors.ToString()
                    + "</xdr:wsDr>";
                drawingRelsXml = BuildDrawingRels(media);
            }
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        static string FormatTagSuffix(string tag) {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            // 清洗 XML 非法控制字符与换行/双引号，防止拼接进 Excel 公式字符串字面量时破坏 sheet4.xml
            string clean = SanitizeXml(tag).Replace("\r", " ").Replace("\n", " ").Replace("\"", "").Trim();
            if (clean.Length == 0) return "";
            return " [" + clean + "]";
        }

        static void WriteLeftCalloutCells(StringBuilder sb, int r, LineupCadCallout co, SstBuilder sst) {
            int numStyle = co.HasArrow ? S_CadLeftNum : S_CadLeftNumUnres;
            string tagSuf = FormatTagSuffix(co.Tag);
            // 用 &"" 防止目标 BOM 单元格为空（如尚未填 IO 点位）时 Excel/WPS 把 VLOOKUP 空值显示成 0
            string descFormula = "IF(C" + r + "=\"\",\"\",IFERROR(VLOOKUP(C" + r + ",'" + BomSheetName + "'!$A:$D,4,0)&\"\",\"\"))";
            if (tagSuf.Length > 0) descFormula = "IF(C" + r + "=\"\",\"\",IFERROR(VLOOKUP(C" + r + ",'" + BomSheetName + "'!$A:$D,4,0)&\"\",\"\")&\"" + tagSuf + "\")";
            string ioFormula = "IF(C" + r + "=\"\",\"\",IFERROR(VLOOKUP(C" + r + ",'" + BomSheetName + "'!$A:$D,2,0)&\"\",\"\"))";

            sb.Append(CellFormulaStr("A" + r, descFormula, (co.Description ?? "") + tagSuf, S_CadLeftDesc));
            sb.Append(CellFormulaStr("B" + r, ioFormula, co.Address, S_CadIo));
            if (co.BomRow > 0) sb.Append(CellFormulaStr("C" + r, "'" + BomSheetName + "'!A" + co.BomRow, co.Number, numStyle));
            else sb.Append(CellStr("C" + r, co.Number, numStyle, sst));
        }

        static void WriteRightCalloutCells(StringBuilder sb, int r, LineupCadCallout co, SstBuilder sst) {
            int numStyle = co.HasArrow ? S_CadRightNum : S_CadRightNumUnres;
            string tagSuf = FormatTagSuffix(co.Tag);
            string ioFormula = "IF(J" + r + "=\"\",\"\",IFERROR(VLOOKUP(J" + r + ",'" + BomSheetName + "'!$A:$D,2,0)&\"\",\"\"))";
            string descFormula = "IF(J" + r + "=\"\",\"\",IFERROR(VLOOKUP(J" + r + ",'" + BomSheetName + "'!$A:$D,4,0)&\"\",\"\"))";
            if (tagSuf.Length > 0) descFormula = "IF(J" + r + "=\"\",\"\",IFERROR(VLOOKUP(J" + r + ",'" + BomSheetName + "'!$A:$D,4,0)&\"\",\"\")&\"" + tagSuf + "\")";

            if (co.BomRow > 0) sb.Append(CellFormulaStr("J" + r, "'" + BomSheetName + "'!A" + co.BomRow, co.Number, numStyle));
            else sb.Append(CellStr("J" + r, co.Number, numStyle, sst));
            sb.Append(CellFormulaStr("K" + r, ioFormula, co.Address, S_CadIo));
            sb.Append(CellFormulaStr("L" + r, descFormula, (co.Description ?? "") + tagSuf, S_CadRightDesc));
        }

        static string BuildPicAnchorXml(int id, string name, string relId, LineupCellAnchor from, LineupCellAnchor to) {
            return "<xdr:twoCellAnchor editAs=\"oneCell\">"
                + "<xdr:from><xdr:col>" + from.Col + "</xdr:col><xdr:colOff>" + from.ColOff + "</xdr:colOff><xdr:row>" + from.Row + "</xdr:row><xdr:rowOff>" + from.RowOff + "</xdr:rowOff></xdr:from>"
                + "<xdr:to><xdr:col>" + to.Col + "</xdr:col><xdr:colOff>" + to.ColOff + "</xdr:colOff><xdr:row>" + to.Row + "</xdr:row><xdr:rowOff>" + to.RowOff + "</xdr:rowOff></xdr:to>"
                + "<xdr:pic>"
                + "<xdr:nvPicPr><xdr:cNvPr id=\"" + id + "\" name=\"" + Esc(name) + "\"/><xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr></xdr:nvPicPr>"
                + "<xdr:blipFill><a:blip r:embed=\"" + relId + "\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>"
                + "<xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr>"
                + "</xdr:pic><xdr:clientData/></xdr:twoCellAnchor>";
        }

        // 原生红色指引箭头：Start = 三维图上的器件落点，End = 两侧编号单元格边缘；
        // headEnd="none" + tailEnd="arrow" => 箭头落于 End（单元格端），严格实现“从图指向单元格”。
        static string BuildArrowAnchorXml(int id, string name, LineupCadCallout co) {
            long minX = Math.Min(co.StartX, co.EndX), maxX = Math.Max(co.StartX, co.EndX);
            long minY = Math.Min(co.StartY, co.EndY), maxY = Math.Max(co.StartY, co.EndY);
            long cx = Math.Max(LineupCadLayout.EmuPerPx, maxX - minX);
            long cy = Math.Max(LineupCadLayout.EmuPerPx, maxY - minY);
            string flips = (co.FlipH ? " flipH=\"1\"" : "") + (co.FlipV ? " flipV=\"1\"" : "");
            return "<xdr:twoCellAnchor>"
                + "<xdr:from><xdr:col>" + co.FromAnchor.Col + "</xdr:col><xdr:colOff>" + co.FromAnchor.ColOff + "</xdr:colOff><xdr:row>" + co.FromAnchor.Row + "</xdr:row><xdr:rowOff>" + co.FromAnchor.RowOff + "</xdr:rowOff></xdr:from>"
                + "<xdr:to><xdr:col>" + co.ToAnchor.Col + "</xdr:col><xdr:colOff>" + co.ToAnchor.ColOff + "</xdr:colOff><xdr:row>" + co.ToAnchor.Row + "</xdr:row><xdr:rowOff>" + co.ToAnchor.RowOff + "</xdr:rowOff></xdr:to>"
                + "<xdr:cxnSp>"
                + "<xdr:nvCxnSpPr><xdr:cNvPr id=\"" + id + "\" name=\"" + Esc(name) + "\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr>"
                + "<xdr:spPr>"
                + "<a:xfrm" + flips + "><a:off x=\"" + minX + "\" y=\"" + minY + "\"/><a:ext cx=\"" + cx + "\" cy=\"" + cy + "\"/></a:xfrm>"
                + "<a:prstGeom prst=\"straightConnector1\"><a:avLst/></a:prstGeom>"
                + "<a:ln w=\"31750\" cap=\"rnd\" cmpd=\"sng\" algn=\"ctr\">"
                + "<a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill>"
                + "<a:prstDash val=\"solid\"/><a:round/>"
                + "<a:headEnd type=\"none\" w=\"med\" len=\"med\"/>"
                + "<a:tailEnd type=\"arrow\" w=\"med\" len=\"med\"/>"
                + "</a:ln>"
                + "</xdr:spPr>"
                + "</xdr:cxnSp><xdr:clientData/></xdr:twoCellAnchor>";
        }

        // ==================== Sheet 5: 签字页（严格对齐模板 64 条 lineup自检表） ====================
        static string BuildAuditSheet(LineupProject p, LineupAuditResult audit, SstBuilder sst) {
            var issues = audit != null && audit.Issues != null ? audit.Issues : new List<LineupAuditIssue>();
            bool hasCadPages = p.CadPages != null && p.CadPages.Count > 0;

            var sb = new StringBuilder();
            var merges = new List<string> { "A1:G1" };
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultColWidth=\"12\" defaultRowHeight=\"14.4\"/>");
            sb.Append("<cols>");
            double[] ws = { 6.0, 18.0, 40.0, 10.0, 10.0, 14.0, 38.0 };
            for (int i = 0; i < ws.Length; i++) sb.Append(ColDef(i + 1, ws[i]));
            sb.Append("</cols><sheetData>");

            // Row 1: 合并 A1:G1，高度 60pt，宋体 20B 白底黑框
            sb.Append("<row r=\"1\" ht=\"60\" customHeight=\"1\">");
            sb.Append(CellStr("A1", "lineup自检表", S_SignTitle, sst));
            for (int c = 2; c <= 7; c++) sb.Append(CellBlank(ColName(c) + "1", S_SignTitle));
            sb.Append("</row>");

            // Row 2: 7 列表头（白底黑框宋体 9）
            string[] hdr = { "序号 ", "审查范围", "自审内容", "电气确认", "机械确认", "自检", "备注" };
            sb.Append("<row r=\"2\" ht=\"20\" customHeight=\"1\">");
            for (int c = 0; c < hdr.Length; c++) sb.Append(CellStr(ColName(c + 1) + "2", hdr[c], S_SignCenter, sst));
            sb.Append("</row>");

            // Rows 3..66: 全部 64 条标准自检项 + 同审查范围 B 列纵向合并
            int scopeStartRow = -1;
            string prevScope = "";
            for (int i = 0; i < SignoffRows.Length; i++) {
                var def = SignoffRows[i];
                int row = 3 + i;
                bool writeScope = false;
                if (string.Equals(def.Scope, prevScope, StringComparison.Ordinal)) {
                    writeScope = false;
                } else {
                    if (scopeStartRow > 0 && row - 1 > scopeStartRow) merges.Add("B" + scopeStartRow + ":B" + (row - 1));
                    scopeStartRow = row;
                    prevScope = def.Scope;
                    writeScope = true;
                }

                string selfStatus = "";
                int statusStyle = S_SignCenter;
                string autoDetail = "";
                if (def.AuditCodes != null && def.AuditCodes.Length > 0) {
                    if (def.AuditCodes[0] == "__LOG_AUTO__") {
                        selfStatus = "已自动填入";
                        statusStyle = S_SignPass;
                    } else if ((def.Seq == 52 || def.Seq == 53) && !hasCadPages) {
                        selfStatus = "待出图";
                        statusStyle = S_SignWarn;
                    } else {
                        var hits = issues.Where(x => def.AuditCodes.Contains(x.RuleCode)).ToList();
                        if (hits.Count == 0) {
                            selfStatus = "通过";
                            statusStyle = S_SignPass;
                        } else {
                            int errs = hits.Count(x => x.Severity == LineupAuditSeverity.Error);
                            int warns = hits.Count(x => x.Severity == LineupAuditSeverity.Warning);
                            if (errs > 0) {
                                selfStatus = "不通过(" + errs + ")";
                                statusStyle = S_SignErr;
                            } else if (warns > 0) {
                                selfStatus = "待确认(" + warns + ")";
                                statusStyle = S_SignWarn;
                            } else {
                                selfStatus = "提示(" + hits.Count + ")";
                                statusStyle = S_SignPass;
                            }
                            string[] sample = hits.Take(3).Select(h => (string.IsNullOrEmpty(h.Number) ? "" : h.Number + ":") + h.Message).ToArray();
                            autoDetail = string.Join("；", sample) + (hits.Count > 3 ? " 等" + hits.Count + "处" : "");
                        }
                    }
                }

                string remark = def.FixedRemark ?? "";
                if (autoDetail.Length > 0) remark = remark.Length > 0 ? remark + "；" + autoDetail : autoDetail;

                double ht = EstimateRowHeight(new[] { def.Content, remark }, new[] { ws[2], ws[6] }, 18.0);
                sb.Append("<row r=\"").Append(row).Append("\" ht=\"").Append(Fmt(ht)).Append("\" customHeight=\"1\">");
                sb.Append(CellNum("A" + row, def.Seq, S_SignCenter));
                sb.Append(writeScope ? CellStr("B" + row, def.Scope, S_SignCenter, sst) : CellBlank("B" + row, S_SignCenter));
                sb.Append(CellStr("C" + row, def.Content, S_SignLeft, sst));
                sb.Append(CellBlank("D" + row, S_SignCenter));
                sb.Append(CellBlank("E" + row, S_SignCenter));
                sb.Append(string.IsNullOrEmpty(selfStatus) ? CellBlank("F" + row, S_SignCenter) : CellStr("F" + row, selfStatus, statusStyle, sst));
                sb.Append(string.IsNullOrEmpty(remark) ? CellBlank("G" + row, S_SignLeft) : CellStr("G" + row, remark, S_SignLeft, sst));
                sb.Append("</row>");
            }
            if (scopeStartRow > 0 && (3 + SignoffRows.Length - 1) > scopeStartRow) {
                merges.Add("B" + scopeStartRow + ":B" + (3 + SignoffRows.Length - 1));
            }

            sb.Append("</sheetData><mergeCells count=\"").Append(merges.Count).Append("\">");
            foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
            sb.Append("</mergeCells>");
            sb.Append(PortraitPageSetup(1, 0));
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        // ==================== OOXML 样板与样式表 ====================
        static string BuildContentTypes(bool hasDrawing, List<MediaItem> media) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            if (media.Any(m => m.Ext == "jpg" || m.Ext == "jpeg")) {
                sb.Append("<Default Extension=\"jpg\" ContentType=\"image/jpeg\"/>");
                sb.Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
            }
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
            for (int i = 1; i <= 5; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            if (hasDrawing)
                sb.Append("<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        static string BuildRootRels() {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"" + NsPkgRel + "\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                + "</Relationships>";
        }

        static string BuildWorkbookXml() {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<workbook xmlns=\"" + NsMain + "\" xmlns:r=\"" + NsRel + "\">"
                + "<bookViews><workbookView activeTab=\"2\"/></bookViews>"
                + "<sheets>"
                + "<sheet name=\"LineUP更新日志\" sheetId=\"1\" r:id=\"rId1\"/>"
                + "<sheet name=\"LineUP第一版_配置表\" sheetId=\"2\" r:id=\"rId2\"/>"
                + "<sheet name=\"" + BomSheetName + "\" sheetId=\"3\" r:id=\"rId3\"/>"
                + "<sheet name=\"LineUP第三版_CAD\" sheetId=\"4\" r:id=\"rId4\"/>"
                + "<sheet name=\"签字页\" sheetId=\"5\" r:id=\"rId5\"/>"
                + "</sheets>"
                + "<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/>"
                + "</workbook>";
        }

        static string BuildWorkbookRels() {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkgRel).Append("\">");
            for (int i = 1; i <= 5; i++)
                sb.Append("<Relationship Id=\"rId").Append(i).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet").Append(i).Append(".xml\"/>");
            sb.Append("<Relationship Id=\"rId6\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("<Relationship Id=\"rId7\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        static string BuildSheetDrawingRels() {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"" + NsPkgRel + "\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/>"
                + "</Relationships>";
        }

        static string BuildDrawingRels(List<MediaItem> media) {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkgRel).Append("\">");
            foreach (var m in media)
                sb.Append("<Relationship Id=\"").Append(m.RelId).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/").Append(Path.GetFileName(m.ZipPath)).Append("\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        static string Bd(string l, string r, string t, string b) {
            var sb = new StringBuilder("<border>");
            sb.Append(BdSide("left", l)).Append(BdSide("right", r)).Append(BdSide("top", t)).Append(BdSide("bottom", b));
            sb.Append("<diagonal/></border>");
            return sb.ToString();
        }

        static string BdSide(string name, string style) {
            if (string.IsNullOrEmpty(style)) return "<" + name + "/>";
            return "<" + name + " style=\"" + style + "\"><color rgb=\"FF000000\"/></" + name + ">";
        }

        static string Xf(int fontId, int fillId, int borderId, string h, string v, bool wrap, int numFmtId = 0) {
            var sb = new StringBuilder("<xf numFmtId=\"");
            sb.Append(numFmtId).Append("\" fontId=\"").Append(fontId).Append("\" fillId=\"").Append(fillId).Append("\" borderId=\"").Append(borderId).Append("\" xfId=\"0\"");
            if (numFmtId != 0) sb.Append(" applyNumberFormat=\"1\"");
            if (fontId != 0) sb.Append(" applyFont=\"1\"");
            if (fillId != 0) sb.Append(" applyFill=\"1\"");
            if (borderId != 0) sb.Append(" applyBorder=\"1\"");
            sb.Append(" applyAlignment=\"1\"><alignment");
            if (!string.IsNullOrEmpty(h)) sb.Append(" horizontal=\"").Append(h).Append("\"");
            if (!string.IsNullOrEmpty(v)) sb.Append(" vertical=\"").Append(v).Append("\"");
            if (wrap) sb.Append(" wrapText=\"1\"");
            sb.Append("/></xf>");
            return sb.ToString();
        }

        static string BuildStylesXml() {
            // Fonts (0..15):
            // 0: 宋体11 (Normal 默认，保证列宽换算与中文版 Excel/WPS 一致)
            // 1: 宋体9,  2: 宋体9B,  3: 宋体9#FF0000,  4: 宋体10,  5: 宋体11
            // 6: 宋体12, 7: 宋体12B, 8: 宋体14, 9: 宋体14B, 10: 宋体14#FF0000
            // 11: 宋体16B, 12: 宋体20B, 13: 宋体9#006100, 14: 宋体9#9C6500, 15: 宋体9B#C00000
            var fonts = new[] {
                "<font><sz val=\"11\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"9\"/><name val=\"宋体\"/></font>",
                "<font><b/><sz val=\"9\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"9\"/><color rgb=\"FFFF0000\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"10\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"11\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"12\"/><name val=\"宋体\"/></font>",
                "<font><b/><sz val=\"12\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"14\"/><name val=\"宋体\"/></font>",
                "<font><b/><sz val=\"14\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"14\"/><color rgb=\"FFFF0000\"/><name val=\"宋体\"/></font>",
                "<font><b/><sz val=\"16\"/><name val=\"宋体\"/></font>",
                "<font><b/><sz val=\"20\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"9\"/><color rgb=\"FF006100\"/><name val=\"宋体\"/></font>",
                "<font><sz val=\"9\"/><color rgb=\"FF9C6500\"/><name val=\"宋体\"/></font>",
                "<font><b/><sz val=\"9\"/><color rgb=\"FFC00000\"/><name val=\"宋体\"/></font>",
                "<font><strike/><sz val=\"9\"/><name val=\"宋体\"/></font>",
                "<font><strike/><sz val=\"10\"/><name val=\"宋体\"/></font>",
                "<font><strike/><sz val=\"11\"/><name val=\"宋体\"/></font>"
            };

            // Fills (0..7):
            // 0:none, 1:gray125, 2:#FFFF00(亮黄), 3:#F4B183(浅橙), 4:#BFBFBF(中灰), 5:#E3E3E3(浅灰), 6:#FFF2CC(淡黄提醒), 7:#FF0000(红底变更标记)
            var fills = new[] {
                "<fill><patternFill patternType=\"none\"/></fill>",
                "<fill><patternFill patternType=\"gray125\"/></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF4B183\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFBFBFBF\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE3E3E3\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF2CC\"/></patternFill></fill>",
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFF0000\"/></patternFill></fill>"
            };

            // Borders (0..35):
            var borders = new[] {
                Bd("", "", "", ""),                     // 0: none
                Bd("thin", "thin", "thin", "thin"),     // 1: all thin
                Bd("thin", "thin", "medium", "thin"),   // 2: BOM hdr mid
                Bd("thin", "medium", "medium", "thin"), // 3: BOM hdr right
                Bd("thick", "", "thick", "thin"),       // 4: Log title L
                Bd("", "", "thick", "thin"),            // 5: Log title M
                Bd("", "thick", "thick", "thin"),       // 6: Log title R
                Bd("thick", "thin", "thin", "thin"),    // 7: Log row L
                Bd("thin", "thick", "thin", "thin"),    // 8: Log row R
                Bd("thick", "", "thin", ""),            // 9: Log rem TL
                Bd("", "", "thin", ""),                 // 10: Log rem TM
                Bd("", "thick", "thin", ""),            // 11: Log rem TR
                Bd("thick", "", "", ""),                // 12: Log rem ML
                Bd("", "thick", "", ""),                // 13: Log rem MR
                Bd("thick", "", "", "thick"),           // 14: Log rem BL
                Bd("", "", "", "thick"),                // 15: Log rem BM
                Bd("", "thick", "", "thick"),           // 16: Log rem BR
                Bd("medium", "thin", "medium", "thin"), // 17: CAD yel TL
                Bd("thin", "thin", "medium", "thin"),   // 18: CAD yel TM
                Bd("thin", "medium", "medium", "thin"), // 19: CAD yel TR
                Bd("medium", "thin", "thin", "thin"),   // 20: CAD yel ML
                Bd("thin", "medium", "thin", "thin"),   // 21: CAD yel MR
                Bd("medium", "thin", "thin", "medium"), // 22: CAD yel BL
                Bd("thin", "thin", "thin", "medium"),   // 23: CAD yel BM
                Bd("thin", "medium", "thin", "medium"), // 24: CAD yel BR
                Bd("thin", "", "thin", "thin"),         // 25: CAD left SK (no R)
                Bd("", "thin", "thin", "thin"),         // 26: CAD right SK (no L)
                Bd("medium", "", "medium", ""),         // 27: CAD frame TL
                Bd("", "", "medium", ""),               // 28: CAD frame TM
                Bd("", "medium", "medium", ""),         // 29: CAD frame TR
                Bd("medium", "", "", ""),               // 30: CAD frame ML
                Bd("", "medium", "", ""),               // 31: CAD frame MR
                Bd("medium", "", "", "medium"),         // 32: CAD frame BL
                Bd("", "", "", "medium"),               // 33: CAD frame BM
                Bd("", "medium", "", "medium")          // 34: CAD frame BR
            };

            // cellXfs (0..71) — 与顶部 S_* 常量严格一一对应
            var xfs = new[] {
                Xf(0, 0, 0, "", "center", false),            // 0  S_Default
                // Sheet 1: Log (1..20)
                Xf(11, 0, 4, "center", "center", false),     // 1  S_LogTitleL
                Xf(11, 0, 5, "center", "center", false),     // 2  S_LogTitleM
                Xf(11, 0, 6, "center", "center", false),     // 3  S_LogTitleR
                Xf(4, 0, 7, "center", "center", true),       // 4  S_LogHdrL
                Xf(4, 0, 1, "center", "center", true),       // 5  S_LogHdrM
                Xf(4, 0, 8, "center", "center", true),       // 6  S_LogHdrR
                Xf(4, 0, 7, "center", "center", false),      // 7  S_LogDataL
                Xf(4, 0, 1, "center", "center", true),       // 8  S_LogDataCenter
                Xf(4, 0, 1, "left", "center", true),         // 9  S_LogDataLeft
                Xf(4, 0, 1, "center", "center", true, 14),   // 10 S_LogDataDate
                Xf(4, 0, 8, "center", "center", true),       // 11 S_LogDataR
                Xf(6, 2, 9, "left", "center", true),         // 12 S_LogRemTL
                Xf(6, 2, 10, "left", "center", true),        // 13 S_LogRemTM
                Xf(6, 2, 11, "left", "center", true),        // 14 S_LogRemTR
                Xf(6, 2, 12, "left", "center", true),        // 15 S_LogRemML
                Xf(6, 2, 0, "left", "center", true),         // 16 S_LogRemMM
                Xf(6, 2, 13, "left", "center", true),        // 17 S_LogRemMR
                Xf(6, 2, 14, "left", "center", true),        // 18 S_LogRemBL
                Xf(6, 2, 15, "left", "center", true),        // 19 S_LogRemBM
                Xf(6, 2, 16, "left", "center", true),        // 20 S_LogRemBR
                // Sheet 2: Config (21..24)
                Xf(9, 2, 1, "center", "center", true),       // 21 S_CfgHdr (宋体14B 黄底)
                Xf(6, 0, 1, "center", "center", true),       // 22 S_CfgData (宋体12 居中)
                Xf(6, 0, 1, "center", "center", false, 1),   // 23 S_CfgInt
                Xf(8, 2, 1, "left", "center", true),         // 24 S_CfgRemark (宋体14 黄底)
                // Sheet 3: BOM (25..37)
                Xf(7, 2, 2, "center", "center", true),       // 25 S_BomHdrYellow
                Xf(7, 2, 3, "center", "center", true),       // 26 S_BomHdrYellowR
                Xf(7, 3, 2, "center", "center", true),       // 27 S_BomHdrPeach
                Xf(2, 4, 1, "center", "center", true),       // 28 S_BomSecGray
                Xf(2, 5, 1, "center", "center", true),       // 29 S_BomSecLightGray
                Xf(4, 0, 1, "center", "center", true),       // 30 S_BomNum (宋体10)
                Xf(1, 0, 1, "center", "center", true),       // 31 S_BomCell (宋体9)
                Xf(3, 0, 1, "center", "center", true),       // 32 S_BomCellRed (宋体9 红字)
                Xf(5, 0, 1, "center", "center", true),       // 33 S_BomMech (宋体11)
                Xf(1, 2, 1, "center", "center", true),       // 34 S_BomSeHome (黄底)
                Xf(3, 2, 1, "center", "center", true),       // 35 S_BomSeHomeRed (黄底红字)
                Xf(1, 6, 1, "center", "center", true),       // 36 S_BomWarn
                Xf(4, 6, 1, "center", "center", true),       // 37 S_BomWarnNum
                // Sheet 4: CAD (38..65)
                Xf(9, 0, 1, "center", "center", true),       // 38 S_CadSideHdrC
                Xf(9, 0, 25, "right", "center", true),       // 39 S_CadSideHdrR
                Xf(9, 0, 26, "left", "center", true),        // 40 S_CadSideHdrL
                Xf(9, 2, 17, "center", "center", true),      // 41 S_CadYelTL
                Xf(9, 2, 18, "center", "center", true),      // 42 S_CadYelTM
                Xf(9, 2, 19, "center", "center", true),      // 43 S_CadYelTR
                Xf(9, 2, 20, "center", "center", true),      // 44 S_CadYelML
                Xf(9, 2, 1, "center", "center", true),       // 45 S_CadYelMM
                Xf(9, 2, 21, "center", "center", true),      // 46 S_CadYelMR
                Xf(9, 2, 22, "center", "center", true),      // 47 S_CadYelBL
                Xf(9, 2, 23, "center", "center", true),      // 48 S_CadYelBM
                Xf(9, 2, 24, "center", "center", true),      // 49 S_CadYelBR
                Xf(8, 0, 1, "right", "center", false),       // 50 S_CadLeftDesc
                Xf(8, 0, 1, "center", "center", false),      // 51 S_CadIo
                Xf(8, 0, 25, "right", "center", false),      // 52 S_CadLeftNum
                Xf(10, 6, 25, "right", "center", false),     // 53 S_CadLeftNumUnres
                Xf(8, 0, 26, "left", "center", false),       // 54 S_CadRightNum
                Xf(10, 6, 26, "left", "center", false),      // 55 S_CadRightNumUnres
                Xf(8, 0, 1, "left", "center", false),        // 56 S_CadRightDesc
                Xf(8, 0, 27, "center", "center", false),     // 57 S_CadFrameTL
                Xf(8, 0, 28, "center", "center", false),     // 58 S_CadFrameTM
                Xf(8, 0, 29, "center", "center", false),     // 59 S_CadFrameTR
                Xf(8, 0, 30, "center", "center", false),     // 60 S_CadFrameML
                Xf(8, 0, 0, "center", "center", false),      // 61 S_CadFrameMM
                Xf(8, 0, 31, "center", "center", false),     // 62 S_CadFrameMR
                Xf(8, 0, 32, "center", "center", false),     // 63 S_CadFrameBL
                Xf(8, 0, 33, "center", "center", false),     // 64 S_CadFrameBM
                Xf(8, 0, 34, "center", "center", false),     // 65 S_CadFrameBR
                // Sheet 5: Signoff (66..71)
                Xf(12, 0, 1, "center", "center", true),      // 66 S_SignTitle
                Xf(1, 0, 1, "center", "center", true),       // 67 S_SignCenter
                Xf(1, 0, 1, "left", "center", true),         // 68 S_SignLeft
                Xf(13, 0, 1, "center", "center", true),      // 69 S_SignPass
                Xf(14, 0, 1, "center", "center", true),      // 70 S_SignWarn
                Xf(15, 0, 1, "center", "center", true),      // 71 S_SignErr
                // BOM 变更红底/删除线 (72..77)
                Xf(4, 7, 1, "center", "center", true),       // 72 S_BomAddNum (红底 宋体10)
                Xf(1, 7, 1, "center", "center", true),       // 73 S_BomAddCell (红底 宋体9)
                Xf(5, 7, 1, "center", "center", true),       // 74 S_BomAddMech (红底 宋体11)
                Xf(17, 7, 1, "center", "center", true),      // 75 S_BomDelNum (红底删除线 宋体10)
                Xf(16, 7, 1, "center", "center", true),      // 76 S_BomDelCell (红底删除线 宋体9)
                Xf(18, 7, 1, "center", "center", true)       // 77 S_BomDelMech (红底删除线 宋体11)
            };

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"").Append(NsMain).Append("\">");
            sb.Append("<fonts count=\"").Append(fonts.Length).Append("\">");
            foreach (var f in fonts) sb.Append(f);
            sb.Append("</fonts>");
            sb.Append("<fills count=\"").Append(fills.Length).Append("\">");
            foreach (var fl in fills) sb.Append(fl);
            sb.Append("</fills>");
            sb.Append("<borders count=\"").Append(borders.Length).Append("\">");
            foreach (var b in borders) sb.Append(b);
            sb.Append("</borders>");
            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            sb.Append("<cellXfs count=\"").Append(xfs.Length).Append("\">");
            foreach (var xf in xfs) sb.Append(xf);
            sb.Append("</cellXfs>");
            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        static string LandscapePageSetup(int fitW, int fitH) {
            return "<pageMargins left=\"0.25\" right=\"0.25\" top=\"0.5\" bottom=\"0.5\" header=\"0.25\" footer=\"0.25\"/>"
                + "<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"" + fitW + "\" fitToHeight=\"" + fitH + "\"/>";
        }

        static string PortraitPageSetup(int fitW, int fitH) {
            return "<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.25\" footer=\"0.25\"/>"
                + "<pageSetup paperSize=\"9\" orientation=\"portrait\" fitToWidth=\"" + fitW + "\" fitToHeight=\"" + fitH + "\"/>";
        }

        static string CadPageSetup() {
            return "<pageMargins left=\"0.47\" right=\"0.31\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>"
                + "<pageSetup paperSize=\"8\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>";
        }

        static string ColDef(int index1Based, double width) {
            return "<col min=\"" + index1Based + "\" max=\"" + index1Based + "\" width=\"" + Fmt(width) + "\" customWidth=\"1\"/>";
        }

        static string CellStr(string cellRef, string text, int style, SstBuilder sst) {
            int idx = sst.Add(text);
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\" t=\"s\"><v>" + idx + "</v></c>";
        }

        static string CellNum(string cellRef, int value, int style) {
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\"><v>" + value + "</v></c>";
        }

        static string CellBlank(string cellRef, int style) {
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\"/>";
        }

        static string CellFormulaStr(string cellRef, string formula, string cachedValue, int style) {
            return "<c r=\"" + cellRef + "\" s=\"" + style + "\" t=\"str\"><f>" + Esc(SanitizeXml(formula ?? "")) + "</f><v>" + Esc(SanitizeXml(cachedValue ?? "")) + "</v></c>";
        }

        public static string ColName(int col1Based) {
            string s = "";
            while (col1Based > 0) {
                col1Based--;
                s = (char)('A' + (col1Based % 26)) + s;
                col1Based /= 26;
            }
            return s;
        }

        static int ExcelSerialDate(DateTime date) {
            return (int)(date.Date - new DateTime(1899, 12, 30)).TotalDays;
        }

        // 按列宽估算自动换行所需行高（中文按 2 字符宽，ASCII 按 1.08 字符宽），避免导出后在 WPS/Excel 中被固定行高裁切
        static double EstimateRowHeight(string[] texts, double[] colWidths, double minPt) {
            int maxLines = 1;
            int n = Math.Min(texts.Length, colWidths.Length);
            for (int i = 0; i < n; i++) {
                string t = texts[i];
                if (string.IsNullOrWhiteSpace(t)) continue;
                double cap = Math.Max(6.0, colWidths[i] - 2.0);
                int lines = 0;
                foreach (var para in t.Replace("\r\n", "\n").Split('\n')) {
                    double units = 0;
                    for (int k = 0; k < para.Length; k++) units += (para[k] > 127 ? 2.1 : 1.08);
                    lines += Math.Max(1, (int)Math.Ceiling(units / cap));
                }
                if (lines > maxLines) maxLines = lines;
            }
            if (maxLines <= 1) return minPt;
            return Math.Min(160.0, Math.Max(minPt, maxLines * 14.5 + 5.5));
        }

        static string Fmt(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static string Esc(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string SanitizeXml(string s) {
            return LineupXmlSanitizer.Clean(s);
        }

        static void WriteTextEntry(ZipArchive zip, string path, string text) {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = entry.Open()) {
                byte[] bytes = Utf8NoBom.GetBytes(text);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        static void WriteBytesEntry(ZipArchive zip, string path, byte[] bytes) {
            var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
            using (var s = entry.Open()) s.Write(bytes, 0, bytes.Length);
        }
    }
}
