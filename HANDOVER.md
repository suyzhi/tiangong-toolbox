# 交接文档（给下一个 AI / 下一个开发者）

**更新：2026-09-26 晚**。读这一份就能接手，不用翻聊天记录。

---

## 1. 这个项目是什么

天工 CAD（Solid Edge 内核的国产 CAD，装在 `C:\Program Files\NDS\TianGong 2025`）的插件平台。
插件是一个 .NET Framework 4.x 的 COM 加载项，往 CAD 功能区加一批命令。

**唯一在用的宿主目录是 `DEV_0.7_授权`**（DEV 0.6 的孔型预览面板 + DEV 0.7 的授权模块已合并到这里）。
`DEV_0.6_打孔预览`、`DEV_0.4_插件框架` 及更早目录都是历史材料，别再往上改。

编码约定：**一个源文件一个类、中文注释写清楚"为什么"、不引第三方依赖**。

### 功能模块（功能区「插件」选项卡）

| 命令 | 说明 |
| --- | --- |
| 自动打孔 / 批量排孔 / 配孔检查 | 自动打孔三件套（`AutoHole*.cs`、`HoleProfile.cs`、`HoleViews.cs`） |
| 四面生成内嵌板 / 型材自动填充 / 生成矩形板 | 内嵌板与型材（`PanelForm.cs`、`AutoPanelForm.cs`、`AutoFrame.cs`） |
| 批量格式转换 | SolidWorks/STEP → 天工（`FormatConvert*.cs` + 独立 exe） |
| Lineup 模型标记 | 装配级清单录入（`Lineup*.cs`） |
| 导出出图训练数据 | 训练数据导出（`Training*.cs`） |
| （无按钮）授权 | 激活前拦住所有命令（`src/License/*`） |

---

## 2. 怎么构建、测试、安装

    # 编译插件（生成 build\TianGongCadSuite.dll，同时写程序集版本戳）
    DEV_0.7_授权\tools\build.ps1
    # 编译到独立目录（推荐，便于回退）
    DEV_0.7_授权\tools\build.ps1 -OutputDirectory DEV_0.7_授权\build\我的试验

    # 注册到当前用户（COM 加载项）
    DEV_0.7_授权\tools\install.ps1 -LibraryPath <上面编出来的 DLL>
    # 注销
    DEV_0.7_授权\tools\install.ps1 -Uninstall

    # 单元测试（不需要 CAD）
    DEV_0.7_授权\build\PanelTests.exe              # 核心：几何/Lineup/转换 + 自动打孔纯逻辑
    DEV_0.7_授权\build\PanelTests.exe --license    # 授权链
    DEV_0.7_授权\build\PanelTests.exe --autohole-ui # 孔型预览 UI 状态

    # 授权联调（签真码、对齐测试公钥）
    DEV_0.7_授权\tools\LicenseAdmin\make-test-codes.ps1

**注册后必须完全重启天工 CAD 才会加载新 DLL。**

---

## 3. 当前状态（2026-09-26）

- ✅ 宿主合并完成，测试全绿：核心 109 项 + 自动打孔纯逻辑 203 项 + 授权 95 项 + UI 30 项，0 失败。
- ✅ 授权方案已按新需求改造：管理员只发码（不用机器码）→ 用户激活即绑定本机 → 管理员台账/作废清单。
- ✅ 真机鼠标点测跑过一轮：命令能开、面板联动对、授权激活全流程通过、作废即时失效。
- ❌ **已知阻塞性问题：在"打开磁盘上的装配"里对实例零件打孔，CAD 接口层拒绝写入**
  （`SolidEdgePart.Holes.AddThroughAll` → `CO_E_OBJNOTREG` / `RPC_E_DISCONNECTED`）。
  见第 5 节，这是接手后最该先解决的一件事。

---

## 4. 踩过的坑（都实测过，别再踩）

| 坑 | 现象 | 正确做法 |
| --- | --- | --- |
| **换 DLL 后 CAD 可能仍用旧版** | 注册表已指向新 DLL，实际加载的还是上一次那份 | 先关 CAD 再注册；启动后查 `%LOCALAPPDATA%\TianGongCadSuite\panel.log` 里最后一条 `AddInConnect`（插件自报 dll 路径+版本） |
| **不要删 NDS 的加载项记录** | 手删 `HKCU\Software\NDS\TianGong\Version 225\AddIns\{GUID}` 后，CAD 只把它重建回来、插件却不再加载，功能区直接少一批命令 | 需要恢复时把 `(default)=天工工具箱 (DEV 0.7.0)`、`Cookie=<原值>`、`AutoConnect=1` 写回 |
| **MessageBox 不能用 WM_CLOSE 关** | 弹框不消失，插件状态卡在"打孔中…" | 用真鼠标点确定/取消按钮 |
| **单跑授权单测会清掉本机激活** | `PanelTests.exe --license` 会 `Deactivate()` + 清 HKCU | 跑完要重新激活一次 |
| **CAD 是单实例程序** | 想并行起两个实例会静默退出 | 一次只跑一个 CAD 实例 |
| **私有桌面里 SendInput 无效** | 只能"移动该桌面的光标 + 发消息" | 用 `tools/mouse-e2e/*.ps1`（已封装好） |
| **坐标别靠肉眼估** | 点错标签/按钮 | 截图 → 裁切放大 → 量像素；窗口矩形 (-8,-8) 时"图像坐标 = 屏幕坐标 + 8" |
| **PS 单元素管道是标量** | `(...)[0]` 取到的是首字符（把激活码写成 1 个字符） | 用 `@(...)[0]` |
| **脚本里的源码根别写死层数** | `Split-Path` 多退一层会指向仓库根，脚本静默走错 | 向上查找含 `src\License\LicenseKeySlot.cs` 的目录 |

---

## 5. 当前未解决的问题：装配里的实例零件打不进孔

### 现象

打开装配（用户日常就是双击 .asm）→ 点一个已有的孔 + 点一个面 → 点「开始打孔」→
结果框：**共打孔 0 个，失败 N 个**，原因是 `Holes.AddThroughAll` 抛
`CO_E_OBJNOTREG (0x800401FB)` 或 `RPC_E_DISCONNECTED (0x80010108)`。

### 已经做过的对照实验（结论可靠）

| 场景 | 结果 |
| --- | --- |
| 客户端（独立进程 COM）在同一会话里**新建**零件+装配再打孔 | ✅ 成功（`VisualAcceptance.exe` 22/22 断言，切除体积与理论一致） |
| 把 .par 当**顶层零件**打开再打孔 | ✅ 成功（Φ6 通穿 20mm 板，切除 565.5 mm³ 对得上） |
| 打开磁盘上的装配，对**实例零件**打孔（插件代码） | ❌ 失败 |
| 同上，但走验收程序那条 `Drill()`（带"丢缓存重建再试"） | ❌ 失败 |
| 同上 + `Occurrence.MakeWritable()`（零件文档 ReadOnly 由 True 变 False） | ❌ 仍失败 |
| 同上 + `Occurrence.Activate = true`（想进原位编辑，8 秒轮询 `InPlaceActivated` 始终 False） | ❌ 没生效 |
| 同上 + `PartDocument.Activate()` | ❌ E_FAIL |
| 同上 + 先 `Documents.Open(零件路径)` 再打 | ❌ 被"未保存的更改"弹框挡住 / 仍失败 |
| **双击零件进入原位编辑**后再打 | ⚠️ 进得去（标题变「顺序建模零件 - [TappedA.par 在 …]」），但**插件命令在零件环境里点了没反应**——插件的命令只注册在装配环境 |

### 结论

写不进"打开装配里的实例零件"是 **CAD 接口层的限制**，不是几何/匹配算错（读参考孔、反推规格、求交、预览全部正确）。
CAD 自己的 UI 是"双击零件进入原位编辑"再改，但插件的命令没注册零件环境，所以走不进去。

### 根因已证实（2026-09-26 深夜）

**CAD 只允许往"当前正在编辑的那个零件文档"写模型。** 证据（同一台机器、同一份 DLL）：

| 上下文 | 写模型 |
| --- | --- |
| 客户端在同一会话新建零件+装配 | ✅ 成功 |
| 把 .par 当**顶层零件**打开 | ✅ 成功（Φ6 通穿 20mm 板，565.5 mm³） |
| **鼠标双击零件进入原位编辑**，再对活动零件文档打孔 | ✅ **成功（Φ6 通穿 10mm 板，282.7 mm³）** |
| 装配里对实例零件打孔（插件或原生 API 都一样） | ❌ CO_E_OBJNOTREG / RPC_E_DISCONNECTED |

失败的那些补救（重试、丢缓存重建、`Occurrence.MakeWritable()`、`Occurrence.Activate=true`、
`asm.ActivateAll()`、`asm.EditAssembly()`、`PartDocument.Activate()`、先 `Documents.Open(零件)`）
全都试过，都没用 —— 不要再走这些路。

### 真正的根因（2026-09-27 凌晨，已推翻前面的"上下文/进程"结论）

**打孔失败的直接原因是："这个位置上已经有一个孔了"。** 同一根轴线上重复打孔，天工CAD 会拒绝，
而且只报一个含糊的 COM 错误（`CO_E_OBJNOTREG` / `RPC_E_DISCONNECTED`），于是被一层层误读成
"零件只读装载""原位编辑才能写""必须外部进程"——这些都是错的。

决定性实验（同一会话、同一零件、同一张面、同一份代码）：

| 实验 | 结果 |
| --- | --- |
| 往**空位**打 Φ6 | ✅ `OK created=1 通孔 Φ6` |
| 紧接着往**同一个位置**再打一次 | ❌ 报 CO_E_OBJNOTREG（就是"已经有孔了"） |
| 面板「批量排孔」选空位排 4 个孔（**进程内**、零件编辑状态） | ✅ `排孔完成：4 / 4 个` |
| 面板「自动打孔」用配对夹具（参考孔投影落在已有孔上） | ❌ 必然失败（夹具两块板的孔是配对的） |

所以：**功能本身是好的**；之前的"装配里打孔失败"全都是被 TappedFixture 这个夹具坑的
（它的两块板孔位配对，参考孔投影必然撞上已有孔）。

### 已按真正根因改的代码

| 改动 | 位置 |
| --- | --- |
| **打孔前预检"这个位置是否已有孔"**：命中就跳过并说明 `这个位置已经有孔了（Φ…），已跳过` | `src/AutoHoleCad.cs`（`ExistingHoleAt`，`DrillOne` 入口） |
| 报错文案改正：`0x800401FB` / `0x80010108` 不再说"只读装载/请进原位编辑"，改成"这个位置可能已经有孔了" | `src/AutoHoleCad.cs`（`Friendly`） |
| 去掉"请先双击零件进入原位编辑"的拦路提示（不再是必需） | `src/AutoHoleForm.cs` |
| 保留但不再使用外部工作器：`tools/DrillWorker.cs` + 构建项（作为排查工具留着），`DrillWorkerClient` 已删 | `tools/build.ps1` |

仍然成立的改动（这些本身是改进，与根因无关）：命令在"原位编辑"上下文也能用
（`RequireEditableAssembly`）、按上下文决定是否开跨零件选择、整轮重试、结果框人话化、
插件启动自报 DLL 路径版本。

### 下一步

1. 用鼠标回归验证两条路径：**空位打孔 → 成功**；**已有孔位置 → 出"这个位置已经有孔了"**。
2. 考虑更友好的处理：命中已有孔时，如果**规格一致**就直接算"已满足"（不报失败），
   规格不一致才提示——这样配对打孔的常见用法就不会看到红色失败框。

### 已作废的结论（不要再照着做）

~~"只有原位编辑才能写模型"~~、~~"必须用外部进程打孔"~~、~~"MakeWritable/Activate/EditAssembly 是方向"~~
——这些都是在"位置已被占用"的前提下观察到的假象。

### 决定性的补充发现（2026-09-26 深夜第二轮）

在同一会话、同一"原位编辑"状态下做了对照，结论是：**能不能写模型，取决于是"谁在调"**。

| 调用方 | 目标 | 结果 |
| --- | --- | --- |
| 独立进程（`PanelPathDrill*/InPlaceDrill.exe`，跨进程 COM） | 活动零件文档上的面（局部坐标 + 单位变换） | ✅ 成功（565.5 mm³ 与理论一致） |
| 插件面板按钮（CAD 进程内、插件 UI 线程） | 同一零件、同一张面（含整轮重试 3×6 次） | ❌ `RPC_E_DISCONNECTED` / `CO_E_OBJNOTREG` |
| 独立进程 | 用"实例选择引用"取的面 + 实例变换 | ❌ 前两次失败、第三次成功（不稳定） |

同时排除了这些因素（都试过，都不是根因）：
文档是否活动（两种上下文都试了）、Part/Face 对象是否来自活动文档（手工锚定过）、
坐标换算（(0,0,30)→局部(0,0,10) 核对无误）、整轮重试、跨零件选择开关、`MakeWritable`、
`Activate/ActivateAll/EditAssembly`、先 `Documents.Open(零件)`。

**推荐修法（下一轮做）**：把"打孔"这一步交给**外部小工具进程**执行——面板把
（目标零件路径、面平面、孔心、规格）传给一个随插件安装的小 EXE，它用
`Marshal.GetActiveObject("SolidEdge.Application")` 连上 CAD 完成打孔再回报结果。
这条路已经被上面三个驱动反复验证可行。改动点：新增 `tools/DrillWorker.cs` + 打包/安装脚本、
`AutoHoleWriter.DrillRequests` 在"活动文档是零件"时改走外部进程。

（另一个待查方向：为什么插件进程内的调用被拒——怀疑与插件 UI 线程/消息循环或 COM 单元有关；
如果找到进程内可用的写法，就不必上外部进程。）

### 已按这个结论改的代码

| 改动 | 位置 | 状态 |
| --- | --- | --- |
| 命令 6/7 的原位编辑支持：活动文档是零件时，遍历打开的装配找回"所属装配" | `src/PluginFramework.cs`（`EditingAssembly/HasEditableContext/RequireEditableAssembly`）、`src/AutoHoleModule.cs` | 已编译、回归全绿；**鼠标验证未完成**（见下） |
| 打孔前检查"目标零件是不是当前编辑的文档"，不是就给出明确指引，不再白试 6 次 | `src/AutoHoleForm.cs`（`TargetIsActiveDocument`） | 已完成 |
| 结果框报错改人话（已验证） | `src/AutoHoleCad.cs`（`Friendly`） | 已完成 |
| 面板路线补"丢缓存→重建基准面→重试" | `src/AutoHoleCad.cs` | 已完成 |
| 插件启动自报 DLL 路径+版本 | `src/AddIn.cs` | 已完成 |

### 下一步（接着干这些）

1. **确认零件环境下按钮状态**：零件环境的功能区里，我们命令的启用状态由 `ToolContext.HasEditableContext` 决定。
   实测「配孔检查」在该上下文确实变灰（`HasAssembly` 为假 ✓），说明 CAD 在按我们的 CanExecute 判断。
   要查的是 `Util`：`HasEditableContext` 在"双击 TappedB.par 进原位编辑"时返回真还是假。
   办法：在私有桌面上用 PowerShell 迟绑定读插件的诊断对象（`$addin.Object.QueryCommand(6)`），
   或给 `Diagnostics` 加一个直接返回"当前上下文是否可编辑"的方法。**注意 C# 驱动用 dynamic 读不到
   这个对象（没有 IDispatch 接口），必须用 PowerShell 迟绑定。**
2. 如果返回假 → 调试 `EditingAssembly()`（大概率是 `ActiveDocument` 或 `OccurrenceDocument.FullName` 的比较；
   也可能在零件上下文里枚举 `asm.Occurrences` 会抛异常，要加日志）。
3. 如果返回真 → 是点击坐标问题：用截图裁切量按钮框（**注意零件环境的功能区标签整体左移，坐标与装配环境不同**），
   装配环境下 自动打孔 ≈ (143,118)，零件环境下 ≈ (164,133)（本轮量到的值，仍需二次确认）。
4. 验证目标：双击零件 → 插件选项卡 → 自动打孔 → 点一个孔 + 点该零件上的面 → 开始打孔 → **结果框显示成功**
   （写模型在原位编辑下是被允许的，见上表第 3 行）。

### 已验证失败的补救（别重复）



1. **让插件在零件环境也注册命令**（`AddIn.OnConnectToEnvironment` 里把 category 扩到零件环境，
   `SupportedEnvironment` 放开），这样用户"双击零件→开始打孔"就能跑通；同时把面板文案改成
   "请先双击要打孔的零件进入原位编辑"。
2. 或者：在装配环境里找到真正的"可写"入口（CAD UI 的原位编辑背后一定有个 API 调用，可用
   `Occurrence` 的完整成员表逐个试，或者查 Solid Edge 文档里 `EditAssembly/InPlaceActivate` 的用法）。
3. 兜底：把失败原因和"下一步该怎么做"清楚地告诉用户（这一条已经做了，见第 6 节）。

---

## 6. 2026-09-26 这一轮改了什么

| 改动 | 文件 |
| --- | --- |
| 结果框不再甩原始 COM 报错：`Friendly()` 把 `0x800401FB` 翻译成"零件是只读装载，请先双击该零件进入原位编辑" | `src/AutoHoleCad.cs` |
| `DrillOne` 的失败信息走 `Friendly()`（以前直接拼 `e.Message`） | `src/AutoHoleCad.cs` |
| 面板路线（`DrillRequests`）补上验收路线才有的"丢缓存→重建基准面→重取模型→再试一次" | `src/AutoHoleCad.cs` |
| 放开"原位编辑"这条唯一的可写上下文（原来直接拒绝），只读装配仍拒绝 | `src/AutoHoleForm.cs` |
| 插件连接时自报 DLL 路径+版本到 panel.log（换版本可自证） | `src/AddIn.cs` |
| install.ps1 不再动 NDS 注册表，改为提示自检命令 | `tools/install.ps1` |
| 授权方案改版（管理员只发码 / 激活即绑定 / 台账+作废） | `src/License/*`、`src/Admin/*`、`LICENSE.md` |
| 真机鼠标点测工具链（私有桌面 + 真鼠标 + 截图回读） | `tools/mouse-e2e/*.ps1` |

---

## 7. 常用操作备忘

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

## 8. 隔离纪律（照做）

- 动模型前先 `Copy-Item` 成副本，**只在副本上操作**；动完核对原文件 mtime 没变。
- 别在用户桌面上弹窗、别抢鼠标：一律在私有桌面（`Get-Desk -Name TGWork070`）里跑。
- 破坏性动作留退路；收尾杀掉自己的 CAD 实例。

---

## 6. 结项：2026-09-27 凌晨的最终验证

**真鼠标回归（普通装配上下文、无原位编辑、无外部进程）**

| 场景 | 结果 |
| --- | --- |
| 干净夹具（LiveA 有 1 个 Φ6.6 孔、LiveB 实心）：点孔 + 点 LiveB 的面 + 开始打孔 | ✅ **共打孔 1 个**，LiveB 上出现同轴配做的孔（截图 `artifacts/mouse-e2e/z-AC.png`） |
| 配对夹具（TappedFixture：参考孔投影落在已有孔上） | ✅ **共打孔 0 个，1 个位置本来就有孔（见下方说明）**，不再报红色失败 |
| 批量排孔（空位排 4 孔，零件编辑状态，进程内） | ✅ 排孔完成：4 / 4 个 |
| 同一位置连续打两次（排查实验） | 第 1 次 OK / 第 2 次报错 → 这就是"重复孔被拒"的直接证据 |

**结论**：「装配里对实例零件打孔失败」不是权限/上下文问题，而是**往已有孔的位置重复打孔**——
CAD 会拒绝并回一个含糊的 COM 错误。现已：①打孔前预检并如实说明；②规格一致时报"无需再打"、
规格不同报"已跳过"、都不算失败；③报错文案不再误导。

**回归**：核心 109 项 / 授权 95 项 / 孔型预览 UI 30 项 / 自动打孔纯逻辑 203 项，全部 0 失败。

**顺带修掉的错诊断遗留**：那条"必须先双击零件进入原位编辑"的守卫（会拦住正常的装配上下文打孔）已删除。

**可以删掉/不用再管的东西**：`tools/DrillWorker.cs`（外部打孔工作器，排查用，保留但不参与流程）、
`tools/mouse-e2e/AsmDrill*.cs / PanelPathDrill*.cs / PPD*.cs`（都是排查脚本，留作资料）。
