# tools/ 目录说明

这个目录按用途分成三类。**日常只会用到第一类**；第二、三类是真机测试和事实探针工具，
留着是因为以后同类问题还能拿来复用。

> 脚本之间靠 `$PSScriptRoot` 互相引用（`desk-lib.ps1`、`build.ps1` 等），所以第二、三类脚本保留在同级目录，不随意移动路径。

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
| `ConverterMain.cs` / `Launcher.cs` / `TrainingExportRunner.cs` / `LicenseActivate.cs` / `DrillWorker.cs` / `RenameWorker.cs` | `build.ps1` 一起编出来的几个辅助程序（`DrillWorker` 现不参与主流程，留作排查） |

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
| `desk-lib.ps1` | 上面这些共用的"私有桌面"函数库 |

## 3. 事实探针（量 CAD 接口的真实行为，结论写进各功能的 .md）

| 目录 / 文件 | 量的是什么 |
| --- | --- |
| `hole-feature-probe/` | 多孔合一、特征命名、螺纹深度、跟随参考孔、天工孔标准表；`HoleMultiProbe.cs`（2026-10-14）：同一草图集多轮廓、AddSync、用户定义阵列、矩形阵列、已建孔改孔型、编组失败的几种猜测 |
| `drawing-probe/` | 一键出图探针与真机回归脚本 |
| `blind-probe/` | 盲孔报错根因（基准面位置/朝向） |
| `facescan-probe/` | 面扫描、沉孔识别 |
| `panel-probe/` | 内嵌板错位排查（读几何 / 插实例）；`CrossFrameProbe.cs`、`CrossFrameE2E.cs`，用 `run-hole-feature-probe.ps1 -Exe … -ExeArgs …` 在私有桌面跑 |
| `HoleSpike.cs`、`HoleDefaultProbe.cs`、`PlaneReuseSpike.cs`、`ChamferProbe.cs`、`DiagnosePlane.cs` + 对应 `run-*.ps1` | 早期打孔配方、默认值污染、基准面复用、倒角 |
| `LineupDemo.cs`、`LineupPreview.cs`、`Import-LineupLocations.ps1`、`LineupLocations.bas` | Lineup 演示与定位导入 |

> 注：早期的第 4 类一次性排查脚本（`e2e-20260928/`、`fix-e2e/`、`mouse-e2e/`、`probes/`、`desk-*.ps1` 等）以及已下线命令 4 的 `*training*` 探针已从工作树移除，如需查阅可从 Git 历史恢复。
