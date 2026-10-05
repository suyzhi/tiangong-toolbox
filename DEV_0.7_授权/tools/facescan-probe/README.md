# tools/facescan-probe —— 面扫描 / 沉孔识别事实探针（2026-10-06）

"点一个带孔的面 → 自动认出上面的孔（沉孔要取下面的孔径）"这个功能依赖一批 CAD 接口事实。
本目录的探针把这些事实在真机上逐条量出来，生产代码按量到的结果写。

## 编译（x64，.NET Framework csc，和插件同一套引用）

```powershell
$cad='C:\Program Files\NDS\TianGong 2025'
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$interop=Join-Path $cad 'Program\TGAiHelper\Interop.TG.dll'
DEV_0.7_授权\tools\build.ps1 -DevBuild        # 先编出 build\TianGongCadSuite.dll（开发构建才放行授权闸）
& $csc /nologo /target:exe /platform:x64 /out:DEV_0.7_授权\build\FaceScanProbe.exe `
    /reference:$interop /reference:DEV_0.7_授权\build\TianGongCadSuite.dll `
    DEV_0.7_授权\tools\facescan-probe\FaceScanProbe.cs
```

## 运行

```powershell
DEV_0.7_授权\tools\run-facescan-probe.ps1
```

在私有桌面（TGWork）里起一个 CAD，自己新建一块 100×100×20 的板，造出
圆柱沉孔 / 平孔 / 锥形沉孔 / 孔口倒角孔 / 圆凸台，然后把面拓扑、相邻壁面、
同轴小圆、GetRange 语义、get_FacesByRay 语义全部打出来，最后关掉文档。
**不碰用户桌面，也不碰用户的文件**（夹具是新文档，落在 artifacts 里）。
