# 交接：0.12.0 快速出清单（命令 14）

2026-11-21。用户原话：设计完下图下单时，每次都要做一套加工件清单（例如 `SK26.01-C10-01-加工件清单（按钮盒支架）-苏英治-20261118`），包含表格文件以及每个三维零件模型的 6 种格式，需要新增「快速出清单」功能并升级一个大版本（0.11.5 → 0.12.0）。

## 做了什么

### 1. 新模块 `src/Modules/QuickBom/`（命令 ID 14，`GuiVersion` 48 → 49）

* `BomModels.cs`：清单表头（`BomHeader`）、单行零件（`BomItem`）、导出选项（`BomOptions`）、校验问题（`BomIssue`）、导出汇总（`BomExportSummary`）。纯逻辑，零外部依赖。
* `BomRules.cs`：
  * **八大类自动分类**（严格遵循公司清单《八大类区分规则》）：
    * `1` 大焊接件（焊接件/装配体，长度 > 1200mm 或重量 > 100kg）
    * `2` 焊接小件（除 1 类外的焊接件/装配体）
    * `3` 机加大板（长度 > 800mm 的机加件）
    * `4` 机加小件（800mm 以下机加件）
    * `5` 车床件（回转外形/车床件名称）
    * `6` 钣金件（`.psm` 或薄板/钣金名称）
    * `7` 非金属件（POM / 尼龙 / 电木 / 优力胶 / 聚四氟等）
    * `8` 铝型材 / PC板类
  * **加工件筛选与叶级焊接件识别**：自动排除标准件库、外购件前缀/关键词，识别本身带 `.dft` 的焊接件子装配（如 `SK26.01-SD-15-009.asm`），不把焊接件内部零散子零件默认勾进清单。
  * **表头自动推断与标准命名**：从图号自动提取项目编号（`SK26.01`）和机构号（`SK26.01-SD-15`），从属性卡提取项目名称与工程师姓名，自动生成标准输出目录名（`SK26.01-C10-01-加工件清单（按钮盒支架）-苏英治-20261121`）和表格名。
  * **导出前校验**：图号为空/重复、数量 ≤ 0 阻止导出；缺 `.dft` / 缺名称 / 缺材料弹出提醒并允许继续。
* `BomXlsxWriter.cs`：纯 OpenXML 读写器（仅用 `System.IO.Compression` + `System.Xml`，**不依赖 Office / WPS COM**）：
  * 内嵌脱敏后的公司标准模板（`Templates/ProcessingListTemplate.xlsx`、`Templates/CodeRequestTemplate.xlsx`），也支持用户在界面指定自定义明文 `.xlsx` 模板；
  * 生成《加工件清单.xlsx》：原样保留公司 LOGO、八大类区分规则图、样式、合并单元格、`COUNTIF` 计数公式、日期格式（`numFmtId=31`）、冻结表头、打印区域与页脚编制人；勾选「在清单中嵌入三维缩略图」时自动放大行高与 T 列宽并锚定三维等轴测 PNG；
  * 可选同时生成 ERP《编码申请-加工件.xlsx》：原样保留 6 个工作表（含 `基本单位` / `材料` / `处理方式` / `八大类` / `dropdown_items_sheet`）、批注、`VLOOKUP` 公式，并预填材料编码（如 `CL001`）、处理方式编码（如 `CLFS202`）与八大类编码缓存值。
* `BomCad.cs`：
  * 支持三种来源：**载入当前文档**（装配 / 工程图 / 零件）、**扫描模型文件夹…**、**添加零件 / 工程图…**；
  * 进程内经 `SolidEdge.FileProperties` 毫秒级读属性卡，**兼容 `.asm` 的「零件名」「材料」与 `.par` 的「零件名称」「Material」**，读不到（如已打开的加密文档）退回已加载 CAD 文档；
  * 6 种格式导出：
    * 原三维（`.par`/`.psm`/`.asm`）与 `.dft` 经非白名单进程 `SuiteRenameWorker.exe --copy` 复制（保持 `%TSD-Header-###%` 公司透明加密头不变）；
    * `.step` 经三维文档 `SaveCopyAs` 导出，并自动清理天工顺手生成的同名 `.log`；
    * `.dwg` / `.pdf` 经工程图 `SaveCopyAs` 导出；
    * `.jpg` 经工程图 `SheetWindow.SaveAsImage` 导出（按图幅纵横比出高清图，多工作页时第 2 页起另存 `_2.jpg`）。
  * 可选离屏三维等轴测缩略图 PNG（复用 `PreviewMesh.Build` / 新增 `PreviewMesh.BuildSingle` + `MeshView.RenderPng`），无需 OpenGL/截屏；
  * 导出全程不关闭用户原本打开的文档，结束后恢复活动文档与 `DisplayAlerts`。
* `QuickBomForm.cs`：统一 `WizardShell` 侧栏风格 + 右侧 `PartPreview` 3D 预览，支持勾选筛选、表头信息编辑与记忆（`Choices`）、表格直接改、`Ctrl+V` 从 Excel 整块粘贴（如贴入 ERP 编码/反查列）、批量填入、读取尺寸/重量重算八大类、数量一键切为装配实例数、可选打包同名 `.zip`。

### 2. 配套改动

* `tools/RenameWorker.cs`：新增 `--copy <清单> <结果>` 模式（共享读流复制，天工正开着文件也能复制，且复制前后校验 `%TSD-Header` 不解密）。
* `src/Cad/PreviewMesh.cs`、`src/UI/MeshView.cs`：新增 `PreviewMesh.BuildSingle` 与 `MeshView.RenderPng` 供白底三维缩略图离屏渲染。
* `src/Host/PluginFramework.cs` / `src/Host/AddIn.cs` / `VERSION`：注册 `QuickBomModule`（命令 ID 14），`GuiVersion` 48 → 49，版本升至 `0.12.0`。
* `tools/build.ps1` / `tools/make-installer.ps1`：将两份干净 `.xlsx` 模板以 EmbeddedResource 编进 DLL，更新安装包使用说明。
* `tests/QuickBomPureTests.cs`：58 条纯逻辑断言（八大类分类、筛选、推断、命名、校验、加工件清单 + 缩略图、编码申请表 + ERP 词典/VLOOKUP 缓存），已接入 `tests/pure` 和 `PanelTests.exe`。

## 实测踩坑与关键发现（天工 2025 真机实测）

| 现象 | 处理 |
| --- | --- |
| `DraftDocument.SaveCopyAs("xxx.jpg")` 写出的 823 KB 文件实际上是 OLE 复合文档（`D0CF11E0`），不是真 JPEG | 改用 `SheetWindow.FitEx(igFitSheet)` + `SheetWindow.SaveAsImage(..., seImageQualityHigh)`，实测 ~155 ms 写出高清真 JPEG（`FF D8`） |
| `.asm` 焊接件的自定义属性叫 `零件名`、`MechanicalModeling` 里叫中文 `材料`；`.par` 里叫 `零件名称` 和英文 `Material` | `BomCad.ReadProperties` 同时兼容中英文属性名 |
| 焊接件子装配内部包含天工自动生成的虚拟/无磁盘文件子件（如 `角撑板_1..6`，`FileProperties` 报 `STG_E_PATHNOTFOUND`） | 焊接件内 `!File.Exists` 的内部子件直接过滤，不污染表格 |
| `SaveCopyAs("xxx.step")` 会在输出目录旁自动生成同名 `xxx.log` | 导出 STEP 后自动删除本次新产生的 `.log`，保持交付目录干净 |
| 在白名单进程 `TianGong.exe` 里直接 `File.Copy` 加密模型会触发透明解密 | 复用非白名单 `SuiteRenameWorker.exe --copy`，复制后逐一校验 `%TSD-Header` 仍在 |

## 验证

* **纯逻辑测试**：`PanelTests.exe` 全过（`QuickBomPureTests PASSED 58` + `CORE ASSERTIONS PASSED 50`），`PanelTests.exe --commands` 确认 12 个命令（含命令 14「快速出清单」），`activation.dat` 完好。
* **真机端到端（私有桌面 `TGWork`，在用户 `按钮盒支架` 加密副本上跑）**：
  * 打开 `按钮盒支架.asm` → 自动识别 5 个焊接件（`SK26.01-SD-15-009`～`013`），正确带出名称、材料 `Q235-B`、数量 `10/10/22/25/6`、表面处理 `哑光喷塑RAL9003`、批次 `第一批`、优先等级 `A`、工程师 `苏英治`，按实测尺寸/质量自动归为 `2-焊接小件`；
  * 一键导出 5/5 全齐（用时约 40 秒）：30/30 格式文件全部校验通过（`.asm`/`.dft` 全部保持 `%TSD-Header-###%` 加密，`.dwg` 为 `AC1021`，`.pdf` 为 `%PDF-1.7`，`.jpg` 为高清 `JFIF`，`.step` 与人工示例字节大小一致，无多余 `.log`）；
  * 生成的《加工件清单.xlsx》（含 2 张模板图 + 5 张离屏渲染 3D 缩略图）和《编码申请-加工件.xlsx》（6 个工作表 + VLOOKUP 公式 + 预填 `CL001`/`CLFS202`/`2`）均经 `openpyxl` 无错加载校验；
  * 导出结束后用户原本打开的 `按钮盒支架.asm` 仍保持打开状态；
  * 截图人工目检 `QuickBomForm` 界面布局清晰无裁切。
* **正式构建与安装**：已生成正式安装包 `天工工具箱_DEV0.12.0_安装包_20261121.zip` 并安装至本机（`%LOCALAPPDATA%\TianGongCadSuite\app`，`FileVersion 0.12.0`，`activation.dat` 完好）；**暂未推送远端、未发在线更新**，待用户试用确认。
