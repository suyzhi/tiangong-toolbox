# tools/ 目录说明

这个目录里东西多，按用途分成四类。**日常只会用到第一类**；其它都是真机测试和排查时留下的工具，
留着是因为以后同类问题还能拿来复用。

> 脚本之间靠 `$PSScriptRoot` 互相引用（`desk-lib.ps1`、`build.ps1` 等），所以第二～四类脚本没有挪进子目录 ——
> 挪了路径就断。只有没人引用的一次性 C# 探针收进了 `probes/`。

## 1. 构建 / 安装 / 发布（日常用）

| 文件 | 用途 |
| --- | --- |
| `build.ps1` | 编译插件。版本号读 `..\VERSION`；`-DevBuild` 才编测试程序和测试开关（开发构建不外发） |
| `build-install.ps1` | 编译 + 注册到当前用户 |
| `install.ps1` | 注册 / 注销（`-Uninstall`）；安装包里用的也是它 |
| `make-installer.ps1` | 打给别人装的安装包（先跑 `tests\pure` 纯逻辑测试，不过不打包；`-SkipTests` 跳过） |
| `make-delivery.ps1` | 打带源码的留档包 |
| `uninstall-old-addins.ps1` | 卸掉机器上更早的同款插件（默认干跑） |
| `check-registration.ps1` | 查注册表里插件注册得对不对 |
| `LicenseAdmin/` | 授权管理员工具（发码、台账、作废；`授权管理.cmd` 双击用） |
| `ConverterMain.cs` / `Launcher.cs` / `TrainingExportRunner.cs` / `LicenseActivate.cs` / `DrillWorker.cs` | `build.ps1` 一起编出来的几个小程序（`DrillWorker` 现在不参与流程，留作排查） |

## 2. 真机回归（要 CAD，在私有桌面上跑，不打扰用户桌面）

这些脚本都会在私有桌面里另起一个 CAD；**本机开着 CAD 时大多会拒绝运行**（免得连到用户正在用的实例）。

| 文件 | 跑什么 |
| --- | --- |
| `run-autohole-native.ps1` | 自动打孔全部原生回归（`-Modes --autohole-scan` 等） |
| `run-hole-feature-probe.ps1` | 孔特征探针 / 端到端（`hole-feature-probe/`，见那里的 README） |
| `run-facescan-*.ps1` | 面扫描认孔（`facescan-probe/`） |
| `run-visual-acceptance.ps1` + `VisualAcceptance.cs` | 打孔视觉验收（截图 + 体积断言） |
| `run-uishot.ps1` + `UiShot.cs` | 不连 CAD，把自动打孔窗口渲染成 PNG 看排版 |
| `native-test.ps1`、`command-routing-native-test.ps1`、`*-native-test.ps1`、`lineup-reopen-test.ps1` | 内嵌板 / 命令路由 / Lineup 的原生测试 |
| `verify-autohole.ps1` | 自动打孔快速核对 |
| `mouse-e2e/` | 真鼠标点测工具链（私有桌面 + 截图回读），见 `MOUSE-E2E-20260926.md` |
| `desk-lib.ps1` | 上面这些共用的"私有桌面"函数库 |

## 3. 事实探针（量 CAD 接口的真实行为，结论写进各功能的 .md）

| 目录 / 文件 | 量的是什么 |
| --- | --- |
| `hole-feature-probe/` | 多孔合一、特征命名、螺纹深度、跟随参考孔、天工孔标准表；`HoleMultiProbe.cs`（2026-10-14）：同一草图集多轮廓、AddSync、用户定义阵列、矩形阵列、已建孔改孔型、编组失败的几种猜测 |
| `blind-probe/` | 盲孔报错根因（基准面位置/朝向） |
| `facescan-probe/` | 面扫描、沉孔识别 |
| `panel-probe/` | 内嵌板错位排查（读几何 / 插实例）；`CrossFrameProbe.cs`（只读，看一个装配按子装配拆开能识别出哪些框口）、`CrossFrameE2E.cs`（跨子装配填充端到端，参数：输出目录 + 围栏副本装配路径），都用 `run-hole-feature-probe.ps1 -Exe … -ExeArgs …` 在私有桌面跑 |
| `HoleSpike.cs`、`HoleDefaultProbe.cs`、`PlaneReuseSpike.cs`、`ChamferProbe.cs`、`DiagnosePlane.cs` + 对应 `run-*.ps1` | 早期打孔配方、默认值污染、基准面复用、倒角 |
| `TrainingViewProbe.cs`、`TrainingSemanticFixture.cs`、`*training*` | 出图训练数据导出（命令 4，已下线） |
| `LineupDemo.cs`、`LineupPreview.cs`、`Import-LineupLocations.ps1`、`LineupLocations.bas` | Lineup 演示与定位导入 |
| `probes/` | 一次性探针，没有脚本再引用（历史资料，不维护） |

## 4. 历史排查脚本（问题已解决，留作资料）

`e2e-20260928/`、`fix-e2e/`、`desk-*.ps1`（除 `desk-lib.ps1`）、`run-attach*.ps1`、`live-start-cmd*.ps1`、`com-attach.ps1`、
`inspect-*.ps1`、`Inspect.ps1`、`open-*.ps1`、`run-cachecheck.ps1`、`run-diagnose.ps1`、`run-form-ab.ps1`、
`visual-probe.ps1`、`run-visual-probe.ps1`、`crop-zoom.ps1`。
对应的问题和结论都在 `HANDOVER.md` 和各功能的验证报告里。
