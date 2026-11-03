# 交接：0.11.0 一键出图（命令 13）

2026-11-03 一个 /loop 会话做完。设计、规矩来源、接口事实、基准结果、已知局限全在 [DRAWING.md](DRAWING.md)。

* 新文件：`src/Drawing*.cs`（Plan / Layout / BatchData 纯逻辑，tests/pure 里 `DrawingPlanPureTests` 覆盖）；`tools/drawing-probe/`（接口探针 1～6、普查 DftSurvey、端到端 DrawE2E、窗口 FormE2E、基准 DrawCompare、DumpView / DumpDims）。
* 改的旧文件：`PluginFramework.cs`（注册 DrawingModule）、`AddIn.cs`（GuiVersion 47→48）、`VERSION`（0.11.0）、tests/pure 工程。
* 状态：开发构建 + 正式构建都编过；tests/pure 全过，PanelTests 核心全过（授权文件没被动）。**已提交 git（分支 dev-0.7.13，没推送）；没有装到本机、没有发在线更新。**
* 测试全部在副本上、私有桌面 CAD 里跑，H 盘原件没动。

## 结果（老实说）

| 集合 | 每张平均 | 单张 ≥90% | ≥80% |
| --- | --- | --- | --- |
| 调参集 40 张（YF25.07 加工件清单） | 94.0% | 29/39 | 37/39 |
| 留出集 36 张（SK26.01-OK310-02-01A，没拿来调参） | 90.3% | **20/36** | 32/36 |

用户的标准是"90% 的情况都能出好一张可加工的图"。平均数够了，但单张 ≥90% 的只有 56%。而且这个分数只量"人图里的尺寸数值插件图里有没有"，**不量公差和粗糙度**——插件一个都不加，这是离"可加工"最大的缺口。

## 下一步：公差和粗糙度（用户 2026-11-03 指定，新会话做）

### 普查里已经看到的（`artifacts/dft-survey/survey.jsonl`，204 个文件 / 207 张图页，只抽了部分尺寸样本）

* 带公差的尺寸：44/207 张图页有（`dim_tol`）。抽到的样本几乎都是**销孔 / 轴承孔**：`Ø4`、`Ø6`（常带 `2-` / `4-` 数量前缀、`H7`、`%DP9`）、`Ø18`、`Ø20 贯穿`，显示类型 `igDimDisplayTypeUnitTolerance`（38）/ `igDimDisplayTypeTolerance`（25）/ `igDimDisplayTypeClassPlusMinus`（16，即 H7 这类公差带）；还有少量线性 28、30、12、20（配合宽度）和个别坐标尺寸。
* 形位公差框 `FeatureControlFrames`：32 张图页共 75 个；基准 `DatumFrames`：19 张 24 个。
* 粗糙度 `SurfaceFinishSymbols`：19 张共 35 个（多数图靠技术要求文字 / 标题栏旁的统一粗糙度，图里不画）。
* 焊接符号 `WeldSymbols`：8 张 12 个（焊接件）。
* 模板技术要求（`TechnicalRequirements`）154 张都有，内容是模板带的（未注倒角、未注公差等）。

### 建议的做法（先量后做，和 0.11.0 一样的套路）

1. **先普查清楚**：扩 `tools/drawing-probe/DftSurvey.cs`，把所有带公差的尺寸（不只是样本）、每个形位公差框（符号、公差值、基准字母、挂在什么图元上）、每个粗糙度符号（Ra 值、挂在哪个面）、技术要求和标题栏附近的统一粗糙度文字全读出来；对照三维模型判断"什么样的几何会被标公差"（销孔直径 4/6/8、轴承孔、配合面、基准面……）。只读已解密的文件；加密文件要在 CAD 里打开（用私有桌面队列）。
2. **定规则**（纯逻辑，放 `DrawingPlan.cs` 或新的纯文件 + tests/pure 测试）：比如"孔径等于常用销径且是贯通/盲孔 → H7"；"轴承座孔（直径 = 标准轴承外径）→ H7 / K7"；"安装底面 → 基准 A + 平面度"；"配合孔 → 位置度"等，规则必须来自普查统计，别凭空编。
3. **接口探针**：天工里 `Dimension` 设公差（`DisplayType`、`ToleranceClass` / `PrimaryTolerance` 之类属性要实测），`FeatureControlFrames.Add...`、`DatumFrames.Add...`、`SurfaceFinishSymbols.Add...` 怎么挂到视图图元上（大概率也要 `GetReferenceToGraphicMember2` + 图纸坐标，参见 DRAWING.md 接口事实）。写成 `DrawProbe7.cs` 之类，先在 A4 模板上单独试。
4. **加进 `DrawingMaker.Part` 的 Annotate**，在「需要人工确认」列提示加了哪些公差。
5. **扩基准** `DrawCompare.cs`：除了数值，再比"人图里带公差的尺寸，插件图同一数值也带公差吗（类型 / 公差带对不对）""形位公差框 / 粗糙度符号个数和数值"，单独出一个公差完成度，别把原来的数值完成度搞乱。
6. 改完两个集合都跑一遍，确认原来的 94.0% / 90.3% 没掉。

## 基准数据和怎么跑（新会话直接用）

都在 `artifacts/drawing-bench/`（`artifacts/` 不进 git；原来在旧会话的临时 scratchpad 里，已拷出来）：

| 路径 | 内容 |
| --- | --- |
| `gold\` | 调参集：40 个零件 `X.par` + 人画的 `X.human.dft` / `X.human.jpg`（源：`H:\桌面\YF25.07\*加工件清单*`） |
| `gold2\` | 留出集 36 张人画图 `X.human.dft`；对应零件在 `p01A\` |
| `p01A\` | SK26.01-OK310-02-01A 项目副本（装配 + 零件；里面有旧的 `(自动).dft` 是窗口测试留下的，可删） |
| `tpl\` | A3 / A4 SK 模板副本（插件自己用 `DrawingTemplates.Find` 找模板，这里只是备查） |
| `gold-list.txt` / `gold2-list.txt` | 两个集合的零件清单（DrawE2E 的输入） |
| `pairs11.txt` / `pairsv3.txt` | 第 11 轮的「人图\|插件图」配对（指向 `artifacts/draw-e2e-11`、`artifacts/draw-v3`） |
| `pairs.py` | `python -X utf8 pairs.py <gold目录> <插件图目录> <输出清单>` 生成新一轮配对 |
| `q.ps1` | 队列包装：`pwsh -File q.ps1 -JobsFile jobs.txt`（直接在 bash 里传 `-Jobs "a","b"` 会变成一个字符串） |
| `dev\` | 0.11.0 开发构建的 `TianGongCadSuite.dll` + 编好的 DrawE2E / FormE2E / DrawCompare / DumpDims / DumpView |
| `bin\` | 旧会话编的探针 exe（DrawProbe1～6、DftSurvey、DumpDims/DumpView） |

一轮基准（jobs.txt 每行一个任务 `exe|参数|artifacts 下的输出目录名|超时秒`）：

```
<bench>\dev\DrawE2E.exe|<bench>\gold-list.txt|draw-e2e-12|2400
<bench>\dev\DrawCompare.exe|<bench>\pairs12.txt|draw-compare-12|1800
```

（第二行的 pairs12.txt 要等第一行跑完、用 pairs.py 从 `artifacts\draw-e2e-12` 生成；所以一般分两次排队。）改了插件代码要先 `tools\build.ps1 -DevBuild -OutputDirectory <新目录>`，再用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -platform:x64 -r:Interop.TG.dll -r:TianGongCadSuite.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll tools\drawing-probe\DrawE2E.cs` 重编探针（Interop.TG.dll 要拷到 exe 旁边）。

## 规矩（别忘）

* 真机测试只走私有桌面队列 `tools/hole-feature-probe/run-cad-queue.ps1`，不碰用户正在用的天工；用户说"我要用天工了"就停。
* 跑过 PanelTests 后确认 `C:\ProgramData\TianGongCadSuite\activation.dat` 还在。
* 拷贝 / 改名 CAD 文件不能用天工白名单进程做（会被 DLP 解密）；只在副本上测。
* 用户 CAD 开着时别覆盖 `%LOCALAPPDATA%\TianGongCadSuite\app`。
* 提交 / 推送 / 安装 / 发在线更新都要用户点头。
