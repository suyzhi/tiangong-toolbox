# tools/hole-feature-probe —— 孔特征事实探针 + 关联同步端到端（2026-10-13）

自动打孔 0.7.3 的四件事（多孔合一 / 特征命名 / 螺纹深度 / 跟随参考孔）都依赖 CAD 接口的实际行为，
这里的程序在真机上逐条量出来，生产代码按量到的结果写。结论汇总在 `AUTO-HOLE.md` 的「0.7.3」一节。

| 程序 | 量什么 |
| --- | --- |
| `HoleFeatureProbe.cs` | T1 一个轮廓多个 Holes2d / T2 SetProfiles、FeatureGroups / T3 Hole.Name / T4 螺纹深度各种组合 / T5 改孔心 / T6 属性集持久化 / T7 跨零件 IncludeEdge |
| `HoleFeatureProbe2.cs` | 多 Holes2d 每步的 Count、多 Circles2d、编组改名+属性重开、螺纹深度截图 |
| `HoleFeatureProbe3.cs` | `Profile.End` 的 7 种校验标志、螺纹深度线框截图 |
| `HoleStdProbe.cs` | 同一块板上建旧配方 / 新配方（天工 GB Metric 孔标准表）两组孔，回读 标准/子类型/大小/配合，存成 HoleStd.par 供天工孔命令打开对比 |
| `LinkSyncE2E.cs` | 走「自动打孔」窗口的真实代码路径：认孔 → 打孔 → 核对编组/命名/螺纹/关联 → 挪参考零件 → 同步孔位 → 挪参考孔特征 → 同步 → 同一张面再打一批（并进已有组） |
| `CheckPatternE2E.cs` | 「配孔检查」：夹在中间的漏孔、远处零件、插销的孔、远处同轴的不同规格孔；「批量排孔」：成组 + 同一个面再排一批并组 |
| `HoleMultiProbe.cs` | （2026-10-14，已跑，结论见 AUTO-HOLE.md）一个孔特征放多个孔的第二轮：同一草图集多轮廓 + SetProfiles、AddSync、用户定义阵列、矩形阵列、已建孔换 HoleData 改孔型；编组失败的猜测 G1 空轮廓 / G2 基准面 / G3 两规格交错，以及 Reorder、ByRange 能不能救 |
| `HoleEditProbe.cs` | （2026-10-14）改孔型和阵列的接口事实：换 HoleData 孔径不跟着变（要补写孔径）、贯通孔改不成盲孔、赋回原 HoleData；矩形/圆周阵列的角度单位与方向、阵列编组。参数 `ext` 跑第二轮（补写孔径、翻面、盲孔改贯通、圆周阵列） |
| `EditPatternE2E.cs` | （0.7.6）走窗口的真实路径：批量排孔种子孔 + 矩形阵列、两批并组与撤销、点阵列里的孔改孔型（整组）与撤销、贯通改盲孔被拒、逐个打的孔「改孔型…」带入、螺纹孔改小径、改过孔型再撤销打孔、圆周阵列 |
| `LinkCensus.cs` / `LinkedDrillE2E.cs` | （0.7.11/0.7.12）装配里每个零件的链接属于哪个装配；走「自动打孔」窗口真实路径给带链接的零件打孔（`multi` = 一张面一批），核对多孔特征、原生关联、回到顶层、看得见的窗口不变多 |
| `BusyDocE2E.cs` | （0.7.12）实例链上的子装配在别的窗口里也开着：必须拒绝、不搅乱天工；回到子装配里照常打 |
| `WindowStateProbe.cs` / `WindowCloseProbe.cs` / `WindowHideProbe.cs` / `InPlaceDirectProbe.cs` | （0.7.12）原位编辑留下的窗口：是什么、关掉会丢改动、藏起来没事；`SubOccurrence.Reference` 一步进原位 |
| `PartDrillProbe.cs` | （0.7.13）多实体零件：零件概况（实体数、特征树）；老写法每个实体打一个孔（默认）/ 先 MakeActive 再打（`active`）/ `Holes.AddMultiBody`（`multi`）/ 面片→实体、Holes 归属、盲孔、多孔合一、阵列、特征组、腰孔（`facts`） |
| `MultiBodyE2E.cs` | （0.7.13）多实体零件上走插件真实接口：一批 3 孔合成、同位置重打、盲孔、批量排孔 + 撤销、实体 1 单孔、腰孔；可选单实体零件回归 |
| `MultiBodyCheckE2E.cs` | （0.7.15）多实体零件上面扫描 / 点孔口圆边认孔 / 配孔检查读全部实体：实体 6 上打沉孔后扫面认配做孔径、装配里配孔检查读到各实体的孔、射线表穿过实体 6；可选再给一个零件试打孔。会**存盘**零件副本 |
| `CheckCompare.cs` | （0.7.15）整套配孔检查（和窗口同一路径），只读。新旧两个开发构建各编一份在同一装配副本上跑，比较报告 |
| `MissProbe.cs` | （0.7.15）配孔检查报的"漏打孔"是不是真的：一条装配坐标的轴线穿过某零件每个实体的哪些面、离孔多远 |
| `LinkGroupProbe.cs` | （0.8.4）孔位链接 + 多孔孔特征能不能编一个组、删组删不删成员；多孔特征输入孔能不能写关联属性、单独挪；多实体零件面 ID 是否跨实体重号 |
| `EditUndoProbe.cs` | （0.8.4）改孔型改多孔孔特征再撤销修改：逐个输入孔回读、量孔口直径（种子孔要最后换回） |
| `Drill084E2E.cs` | （0.8.4）孔位链接组（编组 / 同步 / 第二批 / 撤销 / 改孔型 / 删组）；多实体零件两个实体上同 ID 的两张面一次打、链接属于别的装配时改用插件关联 + 同步孔位 + 撤销 |

## 编译（x64，.NET Framework csc）

```powershell
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$interop='C:\Program Files\NDS\TianGong 2025\Program\TGAiHelper\Interop.TG.dll'
# 三个探针只依赖接口库
& $csc /nologo /target:exe /platform:x64 /out:DEV_0.7_授权\build\HoleFeatureProbe.exe /reference:$interop /reference:Microsoft.CSharp.dll /reference:System.Drawing.dll DEV_0.7_授权\tools\hole-feature-probe\HoleFeatureProbe.cs
# 端到端要引用开发构建的插件 DLL（授权闸放行钩子只在开发构建里）
DEV_0.7_授权\tools\build.ps1 -DevBuild
& $csc /nologo /target:exe /platform:x64 /out:DEV_0.7_授权\build\LinkSyncE2E.exe /reference:$interop /reference:DEV_0.7_授权\build\TianGongCadSuite.dll /reference:Microsoft.CSharp.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll DEV_0.7_授权\tools\hole-feature-probe\LinkSyncE2E.cs
```

## 运行

```powershell
DEV_0.7_授权\tools\run-hole-feature-probe.ps1 -Exe DEV_0.7_授权\build\LinkSyncE2E.exe -OutDir DEV_0.7_授权\artifacts\link-sync-e2e
```

在私有桌面（TGWork）里另起一个 CAD；**本机已经开着 CAD 就拒绝运行**（免得从 ROT 连到用户的实例），
收尾只结束自己起的那个 CAD 进程。夹具都是新建文档，落在输出目录里，不碰用户文件。
