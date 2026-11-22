# 天工 CAD 插件开发平台（天工工具箱）

面向天工 CAD 2025（Solid Edge 内核）的统一 COM 插件平台，当前唯一在用宿主目录为 **`DEV_0.7_授权`**（最新版本见 [DEV_0.7_授权/VERSION](DEV_0.7_授权/VERSION)）。

后续所有插件功能均在 `DEV_0.7_授权` 内通过 `IToolModule` 模块注册扩展，不再新建独立的 AddIn 宿主。

## 文档入口

- **接手必读**：[HANDOVER.md](HANDOVER.md)（构建 / 测试 / 安装命令、当前版本状态、已知问题与踩坑记录）
- **项目与目录约定**：[PROJECT.md](PROJECT.md)
- **核心模块说明**：
  - [自动打孔 / 批量排孔 / 配孔检查 / 改孔型](DEV_0.7_授权/AUTO-HOLE.md)
  - [一键出图](DEV_0.7_授权/DRAWING.md)
  - [SolidWorks / STEP 批量格式转换](DEV_0.7_授权/FORMAT-CONVERT.md)
  - [授权管理与联网激活](DEV_0.7_授权/LICENSE.md)
  - [宿主与 Lineup 说明](DEV_0.7_授权/README.md)
  - [tools 脚本说明](DEV_0.7_授权/tools/README.md)
- **历史归档**：版本交接记录、历史实测报告与竞品技术调研已统一收归至 `docs/archive/`；更早的 `DEV_0.2`～`0.6` 快照见 Git tag `archive/dev-0.2-0.6-snapshots`。

## 快速构建与安装

```powershell
# 1. 编译正式构建（版本号读取 DEV_0.7_授权/VERSION）
DEV_0.7_授权\tools\build.ps1

# 2. 编译并注册到当前用户（无需管理员权限；注册后需完全重启天工 CAD）
DEV_0.7_授权\tools\build-install.ps1

# 3. 打包对外安装包（自动先跑 tests/pure 纯逻辑测试）
DEV_0.7_授权\tools\make-installer.ps1

# 4. 卸载插件
DEV_0.7_授权\tools\install.ps1 -Uninstall
```

本地发版产物与对外图文说明书存放在 `releases/`（已被 `.gitignore` 忽略，不入库）。
