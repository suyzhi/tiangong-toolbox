# 交接文档（给下一个 AI / 下一个开发者）

> **2026-10-22 晚：0.7.15（找 bug 一轮）** —— 见 [DEV_0.7_授权/HANDOVER-0.7.15.md](DEV_0.7_授权/HANDOVER-0.7.15.md)。一句话：多实体零件上配孔检查 / 面扫描只读第 1 个实体（沉孔认成孔口直径、漏报缺孔）；型材自动填充的输出目录全局只记一个，换个装配生成会写进上一个装配的目录；「关联孔位 → 检查」对原生关联孔说"还没有关联孔"。三个都修了，已装 0.7.15 并确认激活有效。
> **2026-10-22 傍晚：0.7.13（多实体零件打孔）** —— 见 [DEV_0.7_授权/HANDOVER-0.7.13.md](DEV_0.7_授权/HANDOVER-0.7.13.md)。一句话：YF25.07G01-L1-M30-02-07.par 有 39 个设计体，`Holes.AddThroughAll` 在这种零件上对哪个实体都打不上，插件又把这个错误翻译成「这个位置已经有一个孔了」；现在按所选面认实体、多实体零件走 `Holes.AddMultiBody`，提示改成如实说「CAD 拒绝建孔」。
> **2026-10-22：0.7.12（链接打孔可用、不再卡住）** —— 见 [DEV_0.7_授权/HANDOVER-0.7.12.md](DEV_0.7_授权/HANDOVER-0.7.12.md)。一句话：现场卡死 / 「对象已断开」的根因是**原位编辑经过一个在别的标签页里也开着的文档**，以及**原位编辑留下的窗口被关掉**（天工重新加载、改动丢失）；现在进原位前先查、退出后把留下的窗口藏起来；保持关联只用原生关联，孔一律进多孔特征。
> **2026-10-21 晚：0.7.11（带链接的零件打不上孔）** —— 根因、改法、真机验证、没复现的那一半见 [DEV_0.7_授权/HANDOVER-0.7.11.md](DEV_0.7_授权/HANDOVER-0.7.11.md)。一句话：天工只允许一个零件的链接属于一个装配；插件原来建复制失败后退回装配上下文打孔（现场打不上的全是这条路），现在带链接的零件一律在原位编辑里打，"对象已断开"自动换环境补打。
> （下午的记录）2026-10-21 13:30：0.7.10 在用户真实装配上打孔卡死、孔打不上，曾应急装回 0.7.9；原因线索见 HANDOVER-0.7.10.md 开头。
> 2026-10-21：0.7.10（打孔提速，20 孔单零件进程外 3.9 s），**做了什么、没做完什么、下次怎么接**见 [DEV_0.7_授权/HANDOVER-0.7.10.md](DEV_0.7_授权/HANDOVER-0.7.10.md)（技术表格在 AUTO-HOLE.md 开头「0.7.10」一节）。
>
> **2026-10-20：0.7.9 已完成并安装**（天工原生关联孔 + 一个孔特征里多个孔，真机 E2E 和用户围栏副本全过）。
> 结论和做法见 [DEV_0.7_授权/AUTO-HOLE.md](DEV_0.7_授权/AUTO-HOLE.md) 开头「0.7.9」一节，过程记录见 [DEV_0.7_授权/HANDOVER-0.7.9.md](DEV_0.7_授权/HANDOVER-0.7.9.md)。

**更新：2026-10-15（0.7.7）**。读这一份就能接手，不用翻聊天记录。过去的长篇排查过程搬到了
`DEV_0.7_授权/排查记录-实例零件打孔-20260926.md`，这里只留**现在的状态、坑、下一步**。

---

## 1. 这个项目是什么

天工 CAD（Solid Edge 内核的国产 CAD，装在 `C:\Program Files\NDS\TianGong 2025`）的插件平台。
插件是一个 .NET Framework 4.x 的 COM 加载项（C# 5 语法，`csc` 直接编），往功能区「天工工具箱」加一批命令。

**唯一在用的宿主目录是 `DEV_0.7_授权`**。`DEV_0.2/0.3/0.4/0.6` 是历史快照：2026-10-14 起不再进 git
（文件还在本地磁盘，历史排查脚本和夹具在用；旧代码见 tag `archive/dev-0.2-0.6-snapshots`）。

编码约定：**一个源文件一个类、中文注释写清楚"为什么"、不引第三方依赖、C# 5 语法**（tests/pure 用 LangVersion 5 卡住）。

| 命令 | 说明 | 代码 |
| --- | --- | --- |
| 6 自动打孔 / 7 批量排孔 / 8 配孔检查 | 打孔三件套 | `AutoHole*.cs`、`Hole*.cs`、`ResultList.cs`、`CadView.cs`、`AssemblyParts.cs` |
| 2 型材自动填充 | 识别闭合框口生成内嵌板 | `AutoPanelForm.cs`、`AutoFrame.cs`、`FrameGrouping.cs`、`BatchPanels.cs`、`CadInterop.cs` |
| 5 批量格式转换 | SolidWorks/STEP → 天工（+ 独立 exe） | `FormatConvert*.cs` |
| 3 Lineup 模型标记 | 装配级清单录入 | `Lineup*.cs` |
| （无按钮）授权 | 激活前拦住所有命令 | `src/License/*` |
| 1 四面生成内嵌板 / 4 导出出图训练数据 | 已下线（`ModuleCatalog.Hidden`），实现仍在 DLL 里 | `PanelForm.cs`、`Training*.cs` |

用户自己的设置都在 `%LOCALAPPDATA%\TianGongCadSuite\`（制表符分隔文本，记事本能改）：
`hole-rules.tsv`（配做规则）、`hole-check.tsv`（配孔检查口径）、`pattern-presets.tsv`（排孔预设）、
`autopanel.tsv`（型材填充上次的输入）、`panel.log`（日志，1MB 滚动成 `panel.log.1`；
排查时设环境变量 `TG_DEBUG_LOG=1` 再启动 CAD，会多记按钮状态判定这类流水）。

---

## 2. 怎么构建、测试、安装

版本号只有一个来源：`DEV_0.7_授权\VERSION`（build.ps1 / make-installer.ps1 不传 -Version 就读它；窗口标题、加载项说明从程序集版本读）。

    # 编译插件（生成 build\TianGongCadSuite.dll，同时写程序集版本戳）—— 默认是【正式构建】
    DEV_0.7_授权\tools\build.ps1
    # 编译到独立目录（推荐，便于回退）
    DEV_0.7_授权\tools\build.ps1 -OutputDirectory DEV_0.7_授权\build\我的试验
    # 开发构建：才会编出 PanelTests.exe，并打开授权测试开关 / 开发密钥模式 / Diagnostics 的 COM 测试入口
    # （native-test.ps1 等脚本要用）。开发构建绝不外发；交付包（make-delivery.ps1）永远是正式构建。
    DEV_0.7_授权\tools\build.ps1 -DevBuild
    DEV_0.7_授权\tools\build-install.ps1 -DevBuild   # 编译 + 注册开发构建

    # 注册到当前用户（COM 加载项）
    DEV_0.7_授权\tools\install.ps1 -LibraryPath <上面编出来的 DLL>
    # 注销
    DEV_0.7_授权\tools\install.ps1 -Uninstall

    # 单元测试（不需要 CAD；先用 -DevBuild 编译）
    DEV_0.7_授权\build\PanelTests.exe              # 核心：几何/Lineup/转换 + 自动打孔纯逻辑 + 规则表/排孔/设置文件
    DEV_0.7_授权\build\PanelTests.exe --license    # 授权链
    DEV_0.7_授权\build\PanelTests.exe --autohole-ui # 孔型预览 UI 状态
    DEV_0.7_授权\tools\run-autohole-native.ps1 -Modes --autohole-scan   # 面扫描/圆柱面/沉孔认孔（真机，36 条）
      # 原生用例要真写模型：本机没激活时授权闸会一律拦住，测试入口在开发构建里统一放行（见 tests/Program.cs）

    # 纯逻辑测试，不需要 Windows / CAD（Mac、Linux 也能跑；需 .NET SDK，LangVersion 固定 C# 5）
    cd DEV_0.7_授权/tests/pure && dotnet run    # 自动打孔纯逻辑 + 转换续做标记 + 命令行转义

    # 授权联调（签真码、对齐测试公钥）
    DEV_0.7_授权\tools\LicenseAdmin\make-test-codes.ps1

**注册后必须完全重启天工 CAD 才会加载新 DLL。**

---

---

## 3. 当前状态（2026-10-15，0.7.7）

**0.7.7 改了什么**：型材自动填充可以**跨子装配**选型材（围栏四个子装配环起来，某一面的立柱属于隔壁子装配，以前那几个框口生成不了）。

* 根因：在顶层装配里点选子装配内部的零件，SelectSet 里是 `Reference`（真机确认）；`FrameReader` 原来取 `Reference.Object`，
  拿到的是子装配自己文档里的实例 →「选择不属于当前装配」。现在用 `OccurrencePick`（从 `LineupCad.Instance` 抽出来，Lineup 也改用它）
  沿 `GetOccurrencesInPath` 解析成顶层装配里的 `SubOccurrence`；
* 整个子装配选上时：非直型材零件（MISUMI 多实体件等）跳过并记下，不再整批失败；薄板（填充板、玻璃板）不当型材
  （以前它们和型材同层 →「板厚超出型材共同深度范围」，整面识别不出），只用来把盖住的框口标「已有板」、默认不勾（`PanelPlates`）；
* 窗口：「追加选择」「清空」，型材汇总写来自哪几个子装配，框口标「跨 N 个子装配」；生成的板子插在当前（顶层）装配；单次上限 200→400 根；
* 验证：`tests/pure` 全过（新增 `PanelPlatesPureTests`）、`PanelTests` 核心 106 全过；真机 `tools/panel-probe/CrossFrameE2E.cs`
  在用户围栏 SK26.01-OK310-03-001 的副本上走窗口真实路径（2 号子围栏 + 1/3 号角柱 → 两端 6 个框口出现、生成的板子中心/尺寸与识别一致、
  追加选择、去重、已有板标记），结果在 `DEV_0.7_授权/artifacts/cross-frame-e2e/`；
* 没做：板子放进"型材所属的子装配"（现在插顶层）；需要的话要先探针确认能否不激活子装配直接往里插实例。

### 0.7.6

**0.7.6 改了什么**（详见 `DEV_0.7_授权/AUTO-HOLE.md` 开头「0.7.6」一节）：

* 批量排孔改成「种子孔 + 阵列」（`HolePatternWriter.cs`，参数 `PatternFit`）：1 个孔特征 + 1 个矩形/圆周阵列，改种子孔整排跟着变；
  建完逐个核对孔心，不对就退回逐个打孔；种子孔 + 阵列照样编组、并进同面同规格的上一批（`HoleGrouping` 认阵列成员）；
* 新命令 9「改孔型」（`HoleEditForm.cs` / `HoleEditCad.cs` / `HoleEditUndo.cs` / `HoleSpecRead.cs`）：点孔 → 换孔型/规格，默认整组一起改，
  组名孔名跟着改，可撤销；「自动打孔」「批量排孔」打完有「改孔型…」按钮把这批孔带进去；
* `GuiVersion` 43→44（功能区多一个按钮）。

**0.7.6 验证**：纯逻辑 + `PanelTests` 核心/界面全过；真机 `HoleEditProbe`（换孔型、阵列角度约定）、`EditPatternE2E` 41 条全过；
全套回归（`EditPatternE2E`、`NewFeaturesE2E`、`CheckPatternE2E`、`LinkSyncE2E`、`run-autohole-native` 全部模式、`--cad` 763 PASS）0 FAIL，汇总在 `DEV_0.7_授权/artifacts/e2e-0.7.6-summary.txt`；已安装 0.7.6，CAD 打开文档时 `AddInConnect ... ver=0.7.6.0`、注册无报错。

**0.7.5 改了什么**（详见 `DEV_0.7_授权/AUTO-HOLE.md`「0.7.5」一节）：

* 自动打孔 / 批量排孔：「撤销本次打孔」（`HoleUndo.cs`）；结果改成窗口里的列表，点一条跳到那个孔（`ResultList.cs`、`CadView.cs`）；
  编组失败根治：**按规格分批打孔**，同规格的孔在特征树里天然连续（真机查明根因是不同规格交错时 `AddFeatureGroupsBySet` 报 E_INVALIDARG）；
  BySet 失败仍试一次 `AddFeatureGroupsByRange`（成员数对不上就散掉）、建孔失败的空轮廓删掉、失败时把特征树顺序写进日志；不用 `Reorder`；
* 配做规则表（`HoleRules.cs`、`HoleRulesForm.cs`）：四套预设 + 按规格自定义；规格表补到 M2.5～M30（值来自天工自带 GB Metric 表）；
* 批量排孔：起止两端分别设边距、「离方向边 X mm」横向定位、预设（`PatternPresets.cs`）；
* 配孔检查：子装配里的零件也查（`AssemblyParts.cs`）、跳转到孔、「去配孔」、判定口径可改（`HoleCheckOptions`）；
* 型材自动填充：界面重写、输入防抖、多平面自动分组（`FrameGrouping.cs`）、框口勾选、材质/尺寸写进零件属性、记住输出目录、生成后不关窗口；
* 工程：版本号单一来源 `VERSION`、功能区组名去掉 DEV（`GuiVersion` 42→43）、日志滚动、打包前跑纯逻辑测试、tools 整理（`tools/README.md`）。

**验证情况（2026-10-14 真机全部通过，已安装 0.7.5）**：
* 离线：`tests/pure`、`PanelTests.exe` 核心 106 + `--autohole-ui` 87；
* 新功能端到端 `NewFeaturesE2E`（撤销含"并组后撤销恢复旧组"、结果列表/跳转、螺栓+螺母规则、排孔两端边距/离方向边、
  子装配漏打孔 + 去配孔、内嵌板属性/材质、多平面两个框口）、`CheckPatternE2E`、`LinkSyncE2E`：全部 OK；
* `run-autohole-native.ps1` 全部模式（fixes 21 / tapped 26 / pattern 18 / native 17 / form 8 / multi 22 / scan 36）、`--cad` 内嵌板/型材回归（114 核心 + 高级项全 PASS）；
* 汇总在 `DEV_0.7_授权/artifacts/e2e-0.7.5-summary.txt`，各模式全文在 `artifacts/native-regression/`、`artifacts/e2e-0.7.5/`；
* 回归里抓到并修掉一个：带预选打开「型材自动填充」时，勾选事件在窗口句柄创建前 `BeginInvoke` → 识别失败（`AutoPanelForm`，已加 `IsHandleCreated` 判断和对应测试）；
* 安装后 CAD 打开文档时 `panel.log` 里 `AddInConnect ... ver=0.7.5.0`，注册无报错。

**还要人眼确认的**：功能区组名「天工工具箱 → 功能」是否正常重建、旧的"天工工具箱 DEV"组是否消失（`GuiVersion` 42→43；
NDS 注册表里 `Assembly\AddIns\{GUID}\GuiVersion` 仍显示 42，那几个键看起来本来就不随版本更新，不能据此判断）。

**一个孔特征放多个孔**：已用 `HoleMultiProbe` 跑完，结论见 `DEV_0.7_授权/AUTO-HOLE.md`（任意位置多孔做不到；矩形阵列可行；已建孔可直接改孔型）。

---

## 4. 踩过的坑（都实测过，别再踩）

| 坑 | 现象 | 正确做法 |
| --- | --- | --- |
| **换 DLL 后 CAD 可能仍用旧版** | 注册表已指向新 DLL，实际加载的还是上一次那份 | 先关 CAD 再注册；启动后查 `%LOCALAPPDATA%\TianGongCadSuite\panel.log` 里最后一条 `AddInConnect`（插件自报 dll 路径+版本） |
| **不要删 NDS 的加载项记录** | 手删 `HKCU\Software\NDS\TianGong\Version 225\AddIns\{GUID}` 后，CAD 只把它重建回来、插件却不再加载，功能区直接少一批命令 | 需要恢复时把 `(default)=天工工具箱 (DEV 0.7.0)`、`Cookie=<原值>`、`AutoConnect=1` 写回 |
| **安装"成功"了功能区却什么都没有** | `HKCU\Software\NDS\TianGong\Version 225\AddIns\{GUID}\AutoConnect` 为 0 时，CAD 照样把插件列进它自己的会话日志 `Registered AddIns`（GUID、路径、版本全对），但紧接着那行写 **`Connect: FALSE`**——不连接、不报错、功能区什么都不出现，而安装程序这边一切正常 | 0.7.1 起安装程序会自动把它改成 1（保留名称与 Cookie，绝不删记录）。判断"到底连上没连上"看 CAD 自己的会话日志 `%TEMP%\cmdlog V225.txt` 里那行 `Connect:`，比面板日志更硬 |
| **`.ps1` 丢了 UTF-8 BOM** | `安装.cmd` 走的是 `powershell.exe`（Windows PowerShell 5.1），读**无 BOM** 的 .ps1 会按系统 ANSI(GBK) 解码，中文注释当场变乱码 → 满屏 `Missing expression` / `Unexpected token`，安装半途而废 | 别用会吃掉 BOM 的编辑器或改写工具；`make-installer.ps1` 打包时已强制给 install.ps1 补 BOM |
| **MessageBox 不能用 WM_CLOSE 关** | 弹框不消失，插件状态卡在"打孔中…" | 用真鼠标点确定/取消按钮 |
| **单跑授权单测会清掉本机激活** | `PanelTests.exe --license` 会 `Deactivate()` + 清 HKCU | 跑完要重新激活一次 |
| **CAD 是单实例程序** | 想并行起两个实例会静默退出 | 一次只跑一个 CAD 实例 |
| **私有桌面里 SendInput 无效** | 只能"移动该桌面的光标 + 发消息" | 用 `tools/mouse-e2e/*.ps1`（已封装好） |
| **坐标别靠肉眼估** | 点错标签/按钮 | 截图 → 裁切放大 → 量像素；窗口矩形 (-8,-8) 时"图像坐标 = 屏幕坐标 + 8" |
| **PS 单元素管道是标量** | `(...)[0]` 取到的是首字符（把激活码写成 1 个字符） | 用 `@(...)[0]` |
| **脚本里的源码根别写死层数** | `Split-Path` 多退一层会指向仓库根，脚本静默走错 | 向上查找含 `src\License\LicenseKeySlot.cs` 的目录 |
| **CAD 插实例是"按文件名"找文件的** | 生成的内嵌板与装配目录里的旧板子同名时，`Occurrences.AddByFilename(绝对路径)` 会把**装配目录里那个旧同名文件**插进来：位置是新算的矩阵（对），几何是旧文件的（错）——现场表现为"板子错位、大小不对"，CAD 不报错 | 生成的零件一律用**唯一文件名**（已改成 `填充板_<批次时间戳>_001.par`，见 `src/PanelNaming.cs`）；插入后回读实例绑定的文档路径核对（`CadBuilder.Generate` 里的护栏） |
| **`run-autohole-native.ps1` 收尾会结束所有 TianGong 进程** | 用户开着的 CAD 会被一起关掉 | 用户在用 CAD 时别跑；`run-hole-feature-probe.ps1` 只结束自己启动的那个 |
| **WinForms 构造里触发的事件不能 `BeginInvoke`** | 窗口句柄还没建，抛 InvalidOperationException（带预选打开自动填充就中过） | 先判 `IsHandleCreated` |
| **Bash/PowerShell 里用 Python heredoc 改源码** | 脚本里写的 `\\r\\n` 被外层 shell 吃掉一层反斜杠变成 `\r\n`，再被 Python 当成真回车换行写进文件 —— C# 字符串字面量里出现真换行，编译失败 | 改源码用编辑工具，或把 Python 脚本写成文件再跑 |
| **没 BOM 的 .ps1 + 中文** | 见上面 `.ps1 丢了 UTF-8 BOM` | `make-installer.ps1` 现在也带 BOM |
| **换 HoleData 孔径不跟着变** | `hole.HoleData = Φ9 的数据`，回读 Φ9，实际切的还是原来的 Φ6 | 换完在孔现在挂着的 HoleData 上再写一次 `HoleDiameter`（`HoleEditCad.SwapData`） |
| **换成螺纹孔时 CAD 按旧孔径查螺纹表** | Φ9 的孔换上 M6 螺纹数据 → 螺纹变成「Tr 11 x 2」、孔径还是 Φ9 | 先换同孔径的普通孔、写孔径、重算，再换螺纹数据（`SwapData`）；赋回原 HoleData 对象也会撞上，所以撤销按原规格重建 |
| **换了 HoleData 以后 SystemName 会变** | SystemName 形如「M6 标准螺纹 孔 1」，跟着孔型走；按旧 SystemName 找不到孔 | 撤销记录同时留 COM 引用（`HoleEditUndo`、`HoleUndo`） |
| **贯通孔改不成盲孔** | 贯通孔设 `ExtentType=igFinite` 后特征失败（翻面也不行） | 「改孔型」直接拒绝并说明；盲孔改深度/改贯通没问题 |
| **命令行发码在 powershell.exe 下找不到私钥** | `powershell.exe -File license-admin.ps1 -Action new …` 报「找不到私钥文件：C:\…\澶╁伐…」——5.1 把无 BOM 的 `admin-key.txt` 按 GBK 读，中文路径变乱码 | 用 PowerShell 7（`pwsh`）跑 `license-admin.ps1`，或双击 `授权管理.cmd`；发码 txt 默认存在私钥旁（`tests/fixtures`），发给同事前挪到仓库根并改名「发码-同事-<档位>-…」 |
| **`PanelTests.exe --auto` 单独跑空引用** | `Host` 只有插件内诊断入口会设，独立 exe 跑 `--auto` 时 `SectionTests` 第一句就 NullReference | 0.7.7 已改成和 `--cad` 一样连当前 CAD；`run-autohole-native.ps1 -Modes --cad,--auto` 可直接跑 |
| **阵列的角度是弧度** | `AddByRectangular` / `AddByCircular` 的角度单位、方向没文档 | 弧度，从基准面参考方向绕法向逆时针；建完核对孔心（`HolePatternWriter.Verify`） |

---

## 5. 下一步

1. 用户确认功能区组名（第 3 节），以及「改孔型」按钮出现在功能区里；
2. 改孔型的可能扩展：在特征树里直接选特征组来改（现在要在模型上点孔）；沉孔/锥孔尺寸开放手填（现在按表）；
3. Lineup：编号写回实例属性（随模型走）、导出 XLSX（README 里列着的未完成项）；
4. 授权：正式对外发之前换生产密钥（现在是 testmaster 测试密钥，`LICENSE.md` 第 9 节）。

---

## 附：2026-09-29：内嵌板"错位、大小不对"已定位并修复

现场（`H:\桌面\SK26.01\SK26.01Lineup\OK310-0924\3号子围栏.asm`）复现 + 根因 + 真机复验，详见
`DEV_0.7_授权/实测报告-内嵌板错位-20260929.md`。一句话：**几何算法没问题，是插入那一步被按名查找劫持了**。

* 修复：`src/AutoPanelForm.cs`（批次文件名带时间戳）、`src/CadInterop.cs`（插入后核对实际绑定的文档，不一致就撤回并报错）、
  `src/PanelNaming.cs` + `tests/PanelNamingPureTests.cs`（命名规则的纯逻辑回归）。
* 复验：真机跑插件命令 → 14 块板子 14/14 绑定到本批次文件、尺寸与框口吻合。
* 探针工具：`tools/panel-probe/PanelProbe.cs`（`tree/geom/readsel/panels/file/insert` 六个只读或最小写模式），
  以后查这类"读到的几何不对"的问题先跑它。
* 机器上还留着**两个更早版本**的同一款插件（`矩形板 (DEV 0.1.2)` / `(DEV 0.3.0)`，按钮同名、旧代码里有同样的按名查找坑），
  建议用 `tools/uninstall-old-addins.ps1` 卸掉（默认干跑，`-AutoUninstall yes` 才真删）。

### 发激活码的入口（管理员）

* **双击版（推荐）**：`DEV_0.7_授权\tools\LicenseAdmin\授权管理.cmd` —— 弹一个小窗口（私钥/档位/数量/备注 → 生成激活码），
  结果自动另存成 txt 方便整段复制；底层还是下面这个 exe。命令行菜单版：`license-admin.ps1`（支持 `new M 客户甲` 这种参数）。
* **控制台里的两个坑**（旧版菜单翻车过，别再踩）：不要在控制台脚本里改 `[Console]::OutputEncoding` 或循环 `Clear-Host`
  （中文 conhost 会"一闪一闪、基本全黑"）；调后台 exe 不要用 `Start-Process`（`NO_PROXY`/`no_proxy` 并存时会抛
  "已添加项。字典中的关键字"），并且要显式按 UTF-8 解码子进程输出。
* 命令行：`tools\LicenseAdmin\build\TianGongLicenseAdmin.exe new <私钥> M --note "客户甲"`
  （**它是控制台程序，双击只会闪一下**：打印完用法就退出，不是崩溃；缺了就 `build-admin.ps1` 重编）。
* 当前插件里嵌的是 **testmaster 测试公钥**（`src/License/LicenseKeySlot.cs`），私钥在 `tests/fixtures/license-test.tgkey`。
  对外发之前必须先 `keygen` 换生产密钥 + 覆盖公钥槽 + 重打包（否则拿到仓库的人都能自己签码）。
* 用户侧入口：装好插件 → 启动 CAD → 点**任意插件命令** → 弹「天工工具箱 授权激活」→ 整段粘贴 → 激活。

### 安装包（给别人装的那一份）

```powershell
DEV_0.7_授权\tools\make-installer.ps1                 # 打 天工工具箱_DEV0.7.1_安装包_<日期>.zip
DEV_0.7_授权\tools\make-installer.ps1 -Version 0.7.2  # 换版本
```

* 内容：`安装.cmd / 重新安装.cmd / 卸载.cmd`、`payload\`（DLL+互操作库+转换器+Lineup 型号表）、`tools\install.ps1`、
  `使用说明.md`、`manifest-sha256.csv`。**不带源码**（要带源码的留档包仍用 `make-delivery.ps1`）。
* 安装=把 payload 复制到 `%LOCALAPPDATA%\TianGongCadSuite\app` 再注册那份副本（包可删可挪）。
* **强制关闭天工进程**：`install.ps1` 检测到 `TianGong*` 进程会弹窗问「是否强制结束这些进程并继续」，
  答是就代为结束（含转换器拉起的隐藏 /automation 实例），不用手动逐个关、不用重启电脑；
  `安装.cmd -Force` 走无人值守。只结束 `TianGong/TianGongConverter/TianGongDrillWorker/PanelLauncher/TrainingExportRunner`。
* 下线的命令（客户版功能区不出现）：命令 **1 四面生成内嵌板**、**4 导出出图训练数据**。
  配置在 `src/PluginFramework.cs` 的 `ModuleCatalog.Hidden`；实现仍在 DLL 里，把 id 从数组里拿掉即可恢复。
* 实测报告（含提示框截图与复验数据）：`DEV_0.7_授权/安装包说明与实测-20260929.md`。

---

## 附：2026-10-13：自动打孔 0.7.3（编组命名 / 螺纹深度 / 跟随参考孔 / 界面重排）

详见 `DEV_0.7_授权/AUTO-HOLE.md` 开头「2026-10-13（0.7.3）」一节。要点：

* **一个孔特征只能放一个孔**（`Profile.End` 后 Holes2d 只剩 1 个，换任何校验标志都一样）→ 同规格的孔编成**特征组**并按规格命名；
* **螺纹深度**：不显式给时 CAD 只攻 2mm。现在默认全长（`igThroughAll`），可指定深度（`igFinite`）；
* **跨零件关联**：CAD 接口建不了（`IncludeEdge` 0x80040224）→ 孔特征上记 `TGAutoHole/Link` 属性，窗口底部「同步孔位」按关联重新求孔心并挪孔；
* 新文件：`src/HoleNaming.cs`、`src/HoleLink.cs`（纯逻辑）、`src/HoleLinkCad.cs`；探针与端到端在 `tools/hole-feature-probe/`
  （`tools/run-hole-feature-probe.ps1` 运行，本机开着 CAD 时拒绝运行，收尾只结束自己起的 CAD）。
* **天工孔命令打开显示 ***：原来建孔不写 标准/子类型/大小/配合、沉孔/锥沉尺寸也和天工表不一样 → 现在按天工 GB Metric 孔标准表
  写全这四项、默认尺寸取表值（`src/HoleStandard.cs`）。表在 `Program\SEFactorySettings.sepref` 里的 `Preferences\Holes\GB Metric.xlsx`。
* **版本戳没动**（仍是 0.7.2）：发安装包前按惯例 `make-installer.ps1 -Version 0.7.3` 并改窗体标题。

---

## 附：常用操作备忘

    # 私有桌面上启动 CAD（打开夹具副本）并截图
    DEV_0.7_授权\tools\mouse-e2e\01-start.ps1 -Fixture <asm路径>
    # 一次鼠标动作 + 截图 + 窗口回读（x,y 是屏幕坐标）
    DEV_0.7_授权\tools\mouse-e2e\act.ps1 -Tag t1 -Actions '[{"t":"clickat","x":772,"y":40}]'
    # 激活窗口里粘码并点激活
    DEV_0.7_授权\tools\mouse-e2e\activate.ps1 -CodeFile <码文件>
    # 逐个点开功能区命令，记录各自弹的窗口
    DEV_0.7_授权\tools\mouse-e2e\sweep.ps1
    # 对照实验：对打开的装配/顶层零件用产品代码打孔
    DEV_0.7_授权\build\visual-e2e\AsmDrill.exe <asm路径>
    DEV_0.7_授权\build\visual-e2e\PartDrill.exe <par路径>

截图与日志都在 `DEV_0.7_授权\artifacts\` 下（`mouse-e2e`、`visual-e2e`、`fixture-drill`）。

---

## 附：隔离纪律（照做）

- 动模型前先 `Copy-Item` 成副本，**只在副本上操作**；动完核对原文件 mtime 没变。
- 别在用户桌面上弹窗、别抢鼠标：一律在私有桌面（`Get-Desk -Name TGWork070`）里跑。
- 破坏性动作留退路；收尾杀掉自己的 CAD 实例。

---
