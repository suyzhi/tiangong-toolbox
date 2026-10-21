# 0.7.10 交接（2026-10-21）：打孔提速

用户原话：「当前插件打孔时间耗时太长了，请降低算法复杂度，并缩短打孔时间。至少 20 个孔要在 5s 内打完。最后更新版本号为 0.7.10，然后帮我安装并激活」。
中途用户说「速度已经够用了，晚点继续优化，先装好并激活」—— 所以下面"没做完"那几项是**主动停下的**，不是失败。
下一个版本是 **0.7.11**（用户确认），另起会话做；开工时把 `VERSION` 改成 0.7.11。

> **⚠ 2026-10-21 13:30 现场：0.7.10 在用户真实装配上打孔卡死、孔全部打不上 → 已应急装回 0.7.9**（注册 `build
elease-20261020-205355`，激活不变）。
> **git 里的代码仍是 0.7.10**（提交 326b2ee），没有回退。0.7.11 第一件事是查清并修掉这个问题，再谈速度。
>
> 现场日志（`%LOCALAPPDATA%\TianGongCadSuite\panel.log`，装配 YF25.07底部水冷兼容短刀模组堆叠小车.asm，打孔零件 YF25.07G01-L1-M30-02-2.par）：
> * `17 个孔：关联准备 166540ms，打孔 11213ms` —— 卡的是**关联准备**（166 秒），且**没有**「关联准备：参考孔引用…」那行 → 原生关联 `Session.Begin` 中途返回 null（没走到正常结束的计时）。
>   prepMs 里还包含属性集关联 `HoleLinkFactory.Make`（懒建实例索引会把整个装配加载一遍）。先查这 166 秒到底花在哪：ChainOf 的 Walk（引用给不出顶层实例时遍历并加载子装配）、Enter 的 15 秒等待、还是 HoleLinkFactory 的索引。
>   **要对照 0.7.9 在同一装配上的耗时** —— 这段代码 0.7.10 基本没改，可能是 0.7.9 就慢、只是之前没量。
> * 之后 17 个孔**每个两侧都报 0x80010108「系统无法找到指定的资源」**，一个都没打上；没走合成（原生关联没挂上 → 要属性集关联 → 不合成），走的是逐个打 `DrillOne`。
>   0.7.10 在这条路上改了两处，嫌疑最大：① 同规格共用一个 HoleData（`DataFor`，0.7.9 每个孔各造一个 —— 建孔失败时 CAD 可能把 HoleData 也作废了）；
>   ② 打孔期间 `ScreenUpdating = false`（在用户真实环境 / 原位编辑上下文里可能有副作用）。也可能是 `checkExisting=false` 后少了一次读实体（之前那次读实体顺带让 CAD 把模型算完？）。
>   复现：在用户装配的**副本**上用 0.7.10 打同样的孔（不要动原文件），逐项开关这三处二分。DrillSpeedE2E 的小装配上全过，没测出来 —— 0.7.11 必须加一个接近现场的用例（大装配、子装配、属性集关联退回路径）。
> * 13:32 同一零件再打 1 个孔：关联准备 54.9 秒，同样两侧 0x80010108 失败。

技术结论、表格也写在 [AUTO-HOLE.md](AUTO-HOLE.md) 开头「0.7.10」一节；这里侧重**状态和怎么接着做**。

---

## 一、做完了的

### 1. 提速（全部在 `AutoHoleWriter.DrillRequests` 这条路上，自动打孔和"批量排孔·不建成阵列"共用）

| 改动 | 文件 | 为什么慢 |
| --- | --- | --- |
| 多孔孔特征**直接建**：N 个孔心画进一个共享轮廓（原生关联的圆边也在里面挂）→ 第 1 个轮廓打种子孔 → `UserDefinedPatterns.AddByProfiles` → 核对；合不成就删干净、退回逐个打 | `HoleCombine.Build`（替换了 0.7.9 的 `HoleCombine.Run`）、`AutoHoleCad.cs` DrillRequests | 0.7.9 先逐个打 N 个单孔、再删 N 个、再合成：2N+2 次特征重算 |
| 已有孔**只读一遍实体**，孔位在内存里比；这批要打的孔也记进去（同一位置的重复请求照样认得出） | 新文件 `src/HoleSpots.cs`；`AutoHoleWriter.ScanSpots` / `PlaneNormal`；`ExistingHoleAt` 变成薄包装 | 每个孔前后各把实体全部边读一遍 + 合成后每孔再一遍：O(孔数×边数) 次 COM |
| 同规格只造一个 HoleData | `AutoHoleCad.cs` `DataFor` | 每个孔各造一个 |
| 零件间复制的圆边读一遍、同一复制的锚点共用；对不上 / 包含失败时重读 | `HoleNativeLink.cs` `CopyCircles`、`NativeAnchor.Circles` | 每个孔在 Begin、Attach 里各把复制的全部边读一遍 |
| 拆出来的轮廓 / 输入孔配孔位：先读一遍孔心再在内存里配（`HoleSpots.Assign`） | `HoleCombine.cs` Order / OrderProfiles | 两层循环里反复读孔心（O(N²) 次 COM） |
| 种子孔、新面先试上次打成的那一侧 | `AutoHoleWriter.SideOrder` / `Learned` / `DrillOnProfile(…, side)` | 新面上第一个孔撞错侧要 1 秒多 |
| **打孔期间关掉 CAD 屏幕刷新**，finally 里恢复 | `AutoHoleForm.ScreenUpdating(bool)`，包住 RunDrill 里的打孔循环 | 共享轮廓里每加一个孔心/圆边/约束都重画；单零件保持关联 6.3 s → 3.9 s |
| 批量排孔的"有没有孔"检查也改成读一遍 | `HolePatternWriter.cs` 两处 | 同上 |
| 分段计时写进插件日志（正式版也写，现场排查用） | `AutoHoleTiming` 行：每张面一行（关联准备 / 打孔 / 退出原位）+ DrillRequests 一行（读圆边、CAD 建孔、合成各段）+ 原生关联准备一行 | — |

### 2. 顺手修的老 bug
**一批打到几个零件上时「撤销本次打孔」删不掉第一个零件的孔**：`HoleUndo` 里孔/多孔特征的 COM 引用表只按 SystemName 存，
不同零件里的 SystemName 是一样的（都从「… 孔 1」编起），后一个零件把前一个的覆盖了。现在键 = 零件 + SystemName（`HoleUndo.RefKey`）。
0.7.9 以前单孔也会中；bars4 场景实测撤销后孔和零件间复制全清。

### 3. 测试
* 纯逻辑：`tests/AutoHoleLinkPureTests.cs` 新增 `HoleSpotsPure`（判孔口径和原 ExistingHoleAt 一致、横孔不算、0.5mm 容差、重复孔位、配对）；`tests/pure` 全过（PureTests.csproj 加了 HoleSpots.cs）。
* `PanelTests.exe`（开发构建）核心 106、自动打孔纯逻辑 448、`--autohole-ui` 87 全过；跑前跑后 `activation.dat` 哈希一样。
* 真机计时 `DrillSpeedE2E`（进程外，私有桌面 CAD，结果在 `artifacts/drill-speed-*`）：

| 模式（20 个孔） | 0.7.9 | 0.7.10 | 每个模式还核对 |
| --- | --- | --- | --- |
| link：1 个零件，保持关联 | 25.6 s | **3.88 s** | 20 个孔、一个「×20」多孔特征、1 个零件间复制、切除体积、挪参考件后孔跟过去、撤销干净、屏幕刷新已恢复 |
| nolink：不关联 | 8.1 s | **1.07 s** | 同上（没有关联那几条） |
| mixed：12×Φ6.6 + 8×Φ5，保持关联 | 25.4 s | **3.98 s** | 两个多孔特征 |
| bars4：4 个零件各 5 孔，保持关联 | — | **9.71 s（超 5 s）** | 每根 5 孔一个多孔特征、孔位对、撤销干净 |

  bars4 每个零件：进原位 ~0.9 s、零件间复制 ~0.17 s、打孔 ~0.4 s、退出原位 ~0.7 s —— 大头是天工自己切原位编辑。

### 4. 版本 / 安装 / 激活
* `VERSION` = 0.7.10；正式构建 `build\release-20261021-125544` 已注册。私有桌面起 CAD 核对：`AddInConnect … release-20261021-125544\TianGongCadSuite.dll ver=0.7.10.0`，加载项「天工工具箱 0.7.10 Connect=True」。
* 激活没重做：本机原来的一年码 yvz7-259y 仍有效（2027-10-20 到期），换版本不用重新激活。
* 中途为了进程内计时**临时注册过开发构建**（%TEMP%\tgspeed-new），装正式版时已覆盖回来。

---

## 二、没做完的（下次从这里接）

1. **进程内计时还没跑出数字**（用户叫停时正在跑）。进程外每次 COM 调用都跨进程，比插件里慢，用户实际体验应该更快，但没量到。
   已经搭好的：
   * `PanelTests.exe --autohole-speed <输出目录>`：只跑 `tests/AutoHoleSpeedTests.cs`（`DrillSpeed.Run`，和进程外同一套场景），不跑授权链；
   * 开发构建 `Diagnostics.StartSpeedTests(output)`（`src/AddIn.cs`，`#if TG_DEV_BUILD`）；
   * 驱动 `tools/hole-feature-probe/SpeedInProc.exe <输出目录>`：先建空装配（**插件要等 CAD 打开文档才连上** —— 第一次就因为这个拿不到 Diagnostics）、找插件、调 StartSpeedTests、等 Result。
   跑法：天工关着时 `tools\build.ps1 -DevBuild -OutputDirectory %TEMP%\tgspeed-new` → `tools\install.ps1 -LibraryPath %TEMP%\tgspeed-new\TianGongCadSuite.dll`（**临时**）→
   `run-cad-queue.ps1 -Jobs @("$env:TEMP\tgspeed-new\SpeedInProc.exe||drill-speed-inproc|1200")` → **跑完一定装回正式版**（`tools\build-install.ps1`）。
2. **多零件场景超 5 s**（bars4 9.7 s，进程外）。打孔本身已经快了，剩下是每个零件进出原位编辑 ~1.6 s。可以试：
   * 不退回装配、直接从一个零件的原位编辑切到下一个（先探针确认 57872 在原位状态下能不能用）；
   * `NativeHoleLink.Leave` 等 ActiveDocument 变化的那段是否能少等（先量 CAD 自己要多久）；
   * 原生关联准备里 CopyFor 每个请求都 `get_Status` 一次，可以每个复制只查一次（小头）。
3. **其它打孔回归没跑**：`NativeLinkE2E`、`CheckPatternE2E`、`EditPatternE2E`、`NewFeaturesE2E`、`LinkSyncE2E` 已对新 DLL 编好（%TEMP%\tgspeed-new，重编命令见 tools/hole-feature-probe/README.md），
   还有 `run-autohole-native.ps1` 各模式、用户围栏副本 `FenceHoleE2E`（记忆 fence-test-assembly）。**改动碰了多孔合一、撤销、原生关联圆边、排孔检查，发同事之前这些要跑一遍。**
   重点看：EditPatternE2E「逐个打 3 个 M6 过孔」那段（现在一开始就是多孔特征，没有"先单孔后合成"）、FenceHoleE2E 的圆弧边（`CopyCircles` 缓存 + Attach 重读）。
4. **合不成时的退回路径真机没触发过**（这次所有场景都一次合成）。`HoleCombine.Build` 失败 → `touched` → 重取模型/基准面 → 逐个打，代码路径是按 0.7.9 的退回逻辑写的，但没有真机用例覆盖。
   可以在 DrillSpeed 里加一个"故意让第 1 个孔位落在材料外"的模式（种子孔两侧都打不成 → 退回逐个打 → 其余 19 个照样打上、那 1 个报失败）。
5. 用户没要求，但交付同事前要做：按记忆 colleague-delivery 打 0.7.10 安装包 + 发码。

---

## 三、这次踩到的坑

* **私有桌面上起的 CAD 只有同一桌面上的进程 GetActiveObject 得到**（从我自己的 shell 连 → MK_E_UNAVAILABLE）。要连就做成小 exe 走 `run-cad-queue.ps1`。pwsh 7 也没有 `Marshal.GetActiveObject`。
* Bash heredoc 里写 C# 字符串 `"\r\n"` 会被吃成真换行（又踩了一次）—— 改源码一律用 Write/Edit 或把 Python 脚本写成文件再跑。
* 给 powershell.exe 跑的 .ps1 必须带 BOM（又踩了一次）。
* Python `utf-8-sig` 写回会给原本**没有 BOM** 的文件加 BOM（AddIn.cs、tests/Program.cs、HolePatternWriter.cs 原来都没有）—— 写完核对一下首 3 字节。
