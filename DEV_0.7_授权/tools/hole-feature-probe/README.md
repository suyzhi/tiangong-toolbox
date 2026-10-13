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
