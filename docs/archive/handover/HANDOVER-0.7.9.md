# 0.7.9 交接（2026-10-20 晚更新：第 1–3 步已完成，见本节；下面原来的内容保留作背景）

**进度**
* 多孔合一已接进插件（`src/HoleCombine.cs`），结论和做法写在 `AUTO-HOLE.md` 开头「0.7.9」一节。关键：**一个共享轮廓放全部孔心 + 全部复制圆边，End 拆成 N 个轮廓**（探针 L4），原生关联的孔才都跟得动。
* 真机全过：`NativeLinkE2E`（含子装配撤销 ⑨、改孔型 ⑦）、`CheckPatternE2E`、`EditPatternE2E`；`tests/pure` 全过。
* 用户围栏副本（`FenceHoleE2E`，只在 `artifacts\fence-hole-e2e*\fence-*` 副本上跑）查出并修了：
  * 孔口圆边在复制面上被切成圆弧 → `IncludeEdge` 进来的是弧（`Attach` 现在约束到弧心；挂不上时把包含进来的东西删干净，`DrillOne` 换干净轮廓照样打孔）；
  * 一个孔都没打上时「孔位链接-…」零件间复制残留（`NativeHoleLink.Session.End(s, requests)` 退出原位前删掉没用上的复制）；
  * 之前以为的"撤销卡死"是 E2E 没设 `SuppressDialogs`，确认框在私有桌面上没人点（排队脚本现在超时会截私有桌面窗口：`hang-*.png`）。
* 排队脚本 `run-cad-queue.ps1`：任务第 4 段 = 超时秒数；自己 CAD 的子进程不再误判成用户的天工。

**第 4–6 步也做完了**：`FenceHoleE2E` 在围栏副本上 5 条「漏打孔」（含跨子装配、门.asm 里三级深的零件）打孔 / 同步 / 撤销全过；
0.7.9 正式构建已安装（build\release-20261020-205355）；PanelTests 核心 106 + `--autohole-ui` 87 全过，跑完 `activation.dat` 和 HKCU 都和跑前一字不差；已提交推送。

---
# 0.7.9 交接（2026-10-20，会话中途停下，下次新会话接着做）

用户 2026-10-20 提的四件事、做到哪了、下一步怎么跑。API 层面的真机结论全在 [AUTO-HOLE.md](AUTO-HOLE.md) 开头「0.7.8」一节。

## 一、四件事的状态

| # | 用户原话（概括） | 状态 |
| --- | --- | --- |
| 1 | 点插件命令卡很久、左下角在扫文件 | **已修、已推送（0.7.8，提交 49ed91d）**。原因：自动打孔窗口一打开就「检查关联孔」，取每个实例的 OccurrenceDocument 把整个装配加载一遍；功能区按钮状态刷新在原位编辑时也会取。现在只在点「检查」/「同步孔位」时查，按钮状态只比文件名 |
| 2 | 自动打孔「CAD 正忙」重试很多遍后失败 | **已修、已推送（0.7.8）**。日志里 994 条重试没有一条是真忙：建孔失败后对死轮廓重试，另一侧也拿同一个死轮廓。现在只对真忙重试、每侧新轮廓、记住每张面打成的那一侧。真机 E2E 全程没再出现重试 |
| 3 | 孔关联改成天工原生的（特征前面带锁链） | **已做、真机 E2E 26 项通过，未提交**（见第二节）。还差一个修改没复测：第二批同规格孔并进已有的组（见第三节第 2 步） |
| 4 | 一个特征里放多个孔，彻底解决 | **找到天工自己的存法，还没接进插件**。天工「孔」命令点多个孔建出来的是 `UserDefinedPattern`（输入 = N 个单孔 Hole），不是 Hole。`AddByProfiles` 的 6 种组合只跑了 H2：建出来了、没崩，但回读被 E_FAIL 打断，不知道切了几个孔（见第三节第 1 步） |

另外：**0.7.9 还没安装**（要先关天工）；**本机激活已补好**（码 `yvz7-259y`，一年，到 2027-10-20 —— 之前跑开发版 PanelTests 把真实激活删了，测试已改成跑前快照、跑后恢复）。

## 二、工作区里没提交的改动（0.7.9）

* `src/HoleNativeLink.cs`（新）：原生关联主体 —— 认实例链（`Reference.GetOccurrencesInPath`，不遍历整个装配）、子装配里一级级包 `CreateReference`、一级级进/出原位编辑（`StartCommand(57872)` / `40013`，进程内用 `Application.DoEvents` 泵消息等）、建零件间复制（来源 = 参考孔所在的面）、改名「孔位链接-参考零件名」、隐藏、挪到上一个复制后面、孔草图 `IncludeEdge` 复制来的圆边 + Hole2d 关键点约束 + 圆改构造线、`UpdateLinks` = `UpdateAll`
* `src/HoleNativeLinkMath.cs`（新）：纯逻辑（矩阵串接、认实例、挑圆边、斜孔判断），`tests/AutoHoleLinkPureTests.cs` 里有断言
* `src/AutoHoleCad.cs`：`DrillOne` 多了 anchor 参数（挂不上不算失败）、`HoleRequest.From/Anchor/NativeLinked`、`DrillResult.NativeLinked/NativeMissed`、`TargetFace.Pick`
* `src/AutoHoleForm.cs`：打孔流程接原生关联（每个打孔面 Begin → 打孔 → End）、挂不上的孔才走旧的属性集关联（实例索引改成用到才建）、「同步孔位」先 `UpdateAll`、结果文字、`ClearAllForTest`/`ScanFaceOnPick` 测试入口
* `src/HoleScan.cs` / `src/HoleScanCad.cs`：参考孔记住 `Pick`（选择引用）和 `SourceFace`（孔壁 / 点的平面）
* `src/HoleUndo.cs`：撤销也删零件间复制（孔删完以后）
* `src/HoleLinkCad.cs`：`HoleLinkFactory.Begin` 不再立刻建实例索引（懒加载）
* `tests/LicensePolicyTests.cs`：跑前快照 / 跑后恢复真实激活（文件两份 + `HKCU\Software\TianGongCadSuite`）
* `tests/AutoHoleTests.cs`：按钮名「重新检查」→「检查」
* `VERSION` = 0.7.9；`AUTO-HOLE.md` 0.7.8 一节写了探针结论
* `tools/hole-feature-probe/`：`AsmDrivenHoleProbe.cs` + `AsmLinkProbe*.cs`（探针，第二个参数选实验）、`NativeLinkE2E.cs`（端到端）、`run-cad-queue.ps1` / `desk-mouse.ps1` / `cad-up.ps1`（见下）
* `tests/fixtures/license-test.ledger.tsv`：发码台账（本次发了 yvz7-259y），`tests/fixtures/发码-20261020-133145.txt` 是码文件

## 三、下次接着做（按顺序）

**跑真机之前先确认用户的天工关着**（`Get-Process TianGong` 为空），用户说要用天工就立刻停（见记忆 cad-sharing）。所有真机任务都用排队脚本跑：

```powershell
# 任务格式 "exe|参数|输出目录名"；结果在 artifacts\<输出目录名>\out.txt，队列日志在 artifacts\cad-queue\queue.log
tools\hole-feature-probe\run-cad-queue.ps1 -Jobs @("<exe>|<参数>|<目录名>", ...)
```
它会等本机没有天工 30 秒后才开始、自己在私有桌面 TGWork 起 CAD、开跑前确认只有自己这一个 CAD、跑的时候一旦冒出别的天工就停。

1. **多孔合一探针（H1–H6）**：`build\AsmDrivenHoleProbe2.exe` 已按新的逐项回读编好（源码 `AsmLinkProbe10.cs`）。没有就重新编：
   ```powershell
   $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
   $src=@('tools\hole-feature-probe\AsmDrivenHoleProbe.cs') + (Get-ChildItem tools\hole-feature-probe\AsmLinkProbe*.cs | % FullName)
   & $csc /nologo /target:exe /platform:x64 /out:build\AsmDrivenHoleProbe2.exe '/reference:C:\Program Files\NDS\TianGong 2025\Program\TGAiHelper\Interop.TG.dll' /reference:Microsoft.CSharp.dll /reference:Microsoft.VisualBasic.dll $src
   ```
   排队跑 `H2,H1,H4,H5,H6,H3`（H3 最可能把 CAD 搞崩，放最后；每个一个 CAD 会话）。输出是 GBK，用 `Get-Content -Encoding Default` 看。
   看点：哪种组合 UDP 状态 OK、切出 3 个孔、`HoleGeometries=3`；H5 盲孔/螺纹能不能带过去；H6 换种子孔孔型整组跟不跟。
   天工自己的结构作对照：输入特征 = 3 个 Hole、实例 3×3、阵列面 = RefPlane、Model.Holes.Count=0（孔都进了 UDP）。
2. **复测原生关联**：`%TEMP%\tgcheck\NativeLinkE2E.exe`（引用同目录的开发构建 DLL；没有就 `tools\build.ps1 -DevBuild -OutputDirectory $env:TEMP\tgcheck` 再按 tools/hole-feature-probe/README.md 的命令编）。
   这次新加的两条断言要过：第二批并进「M6 螺纹孔 ×6」、新的孔位链接排在孔前面（`HoleNativeLink.CopyFor` 里的 Reorder）。
3. **把多孔合一接进插件**（第 1 步成了再做）。要注意的连锁影响：孔进了 UDP 以后 `Model.Holes` 里就没有它们了 ——
   `HoleGrouping`（编组/并组）、`HoleEditCad`（改孔型找孔）、`HoleUndo`（按 SystemName 在 Model.Holes 里找）、`HoleSync`（属性集关联）、
   `AutoHoleCheck`（配孔检查认孔）、`HoleSpecRead` 都按 Hole 写的，要一起改成"Hole 或 UDP 的输入孔"。原生关联：UDP 的每个输入孔各自挂零件间复制圆边，应该照样成立，要真机验。
   建议：同规格一批孔先照现在打成单孔 Hole（原生关联已验证），再用 AddByProfiles 把它们包成一个 UDP —— 具体怎么包看第 1 步结论。
4. **用户的围栏装配副本上跑一遍**（记忆 fence-test-assembly：`H:\桌面\测试\SK26.01-OK310-03-001`，**只在副本上跑**）：跨子装配的参考孔 / 打孔面、多实例零件、MISUMI 多实体件。
5. **安装 0.7.9**：天工关着时运行 `DEV_0.7_授权\Install.cmd`（正式构建 + 注册；激活文件在 `C:\ProgramData\TianGongCadSuite\activation.dat`，安装不动它）。装完：
   * 跑 `PanelTests.exe`（核心）+ `--autohole-ui`、`tests\pure` 里 `dotnet run`，**然后确认 activation.dat 还在**（快照恢复要真机证实一次）；
   * 激活已经好了，不用再激活；万一被删了，用 `build\LicenseActivate.exe <码>` 重新激活（码在 `tests\fixtures\发码-20261020-133145.txt`，同一台机器重复激活同一个码是允许的）。
6. **提交 + 推送**（用户要求做完再提交推送）。提交信息写清：原生关联、多孔合一（如果做了）、测试不再删真实激活、发码 yvz7-259y。
   要发给同事的话按记忆 colleague-delivery 打安装包。

## 四、已知的坑（这次踩过的）

* 进程外调 CAD 要注册 COM 消息过滤器（探针里的 `ProbeFilter`），否则 CAD 加载装配时 0x8001010A。
* 探针的 `-OutDir` 要给绝对路径（相对路径时私有桌面上的 cmd 写不出文件）。
* Bash 工具的 heredoc 会吃掉 `\\` 和部分引号：改中文源码时把 Python 脚本用 Write 写成文件再跑。
* `Training` 目录的零件大多是同步建模，扫不出顺序建模的多孔样本 —— 要看天工自己的结构，用 `desk-mouse.ps1` 在私有桌面上真的点「孔」命令（坐标 = 截图坐标，脚本自动减 8），再用探针 `I` 模式回读。
