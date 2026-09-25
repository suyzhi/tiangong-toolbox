# 天工 CAD 插件开发平台

当前项目统一用于开发天工 CAD 插件。主开发入口是：

`DEV_0.4_插件框架`

这里已经接入内嵌板和 Lineup 模型标记两个模块，共三个 CAD 命令。后续插件功能都应加入该框架，通过模块注册扩展，不再新建互相独立的 AddIn 宿主。

- **SolidWorks 批量转换（DEV 0.5 新增）**：[使用说明](DEV_0.4_插件框架/FORMAT-CONVERT.md) · 独立程序 `TianGongConverter.exe` + 插件命令 ID 5
- 项目约定：[PROJECT.md](PROJECT.md)
- 当前框架：[DEV_0.4_插件框架](DEV_0.4_插件框架)
- 当前框架说明：[DEV_0.4_插件框架/README.md](DEV_0.4_插件框架/README.md)
- 自动打孔（命令 6/7/8）用法与根因分析：[AUTO-HOLE.md](DEV_0.4_插件框架/AUTO-HOLE.md)
- 自动打孔 2026-09-25 修复与可视化验收：[AUTO-HOLE-VALIDATION-20260925.md](DEV_0.4_插件框架/AUTO-HOLE-VALIDATION-20260925.md)
- Lineup 实测范围和证据：[LINEUP-VALIDATION-20260909.md](DEV_0.4_插件框架/LINEUP-VALIDATION-20260909.md)
- Lineup 五类清单、自动编号、型号库及普通零件关联：[新版验证报告](DEV_0.4_插件框架/LINEUP-SMART-VALIDATION-20260909.md)
- 旧版本：`DEV_0.2_*`、`DEV_0.3_*` 和 `交付_DEV_*`，仅作历史参考

## 安装（发给同事）

同事安装包：`交付_DEV_0.4.0_20260911`，压缩包 `天工工具箱DEV0.4_安装包_20260911.zip`。

1. 完整解压，不要直接在压缩包里双击；
2. 保存工作并完全退出天工 CAD；
3. 双击 `安装.cmd`（当前用户注册，不需要管理员权限）；
4. 启动天工 CAD，打开装配（.asm），功能区“插件（加载项）→ 功能”里有 4 个命令。

验证用 `检查安装.cmd`（不启动 CAD）或 `验证加载.cmd`（让 CAD 实际加载一次）。
完整步骤、FAQ、版本不匹配时的 `重新编译安装.cmd` 见包内 `安装说明.md`；
制作过程与实测记录见 [artifacts/plugin-package-install-20260911/install-evidence.md](artifacts/plugin-package-install-20260911/install-evidence.md)。

本机开发迭代仍使用 `DEV_0.4_插件框架\Install.cmd`（源码编译到 `build\release-*` 后注册）。
