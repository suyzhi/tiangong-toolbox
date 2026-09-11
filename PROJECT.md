# 天工 CAD 插件开发平台

本项目从现在起作为**天工 CAD 插件开发平台**维护，不再以“内嵌玻璃板插件”作为项目名称。

## 开发约定

- 所有新插件功能统一放入 `DEV_0.4_插件框架` 的宿主中。
- 每项功能实现 `IToolModule`，在 `src/PluginFramework.cs` 的 `ModuleCatalog.Create` 中登记。
- 命令 ID 全局唯一，已发布的 ID 不重复使用。
- 原有 DEV 0.1～0.3 目录和交付 ZIP 只作为历史版本和回溯材料，不直接继续开发。
- 发布前使用独立 `build`、`artifacts` 目录，不覆盖生产 CAD 文件。

## 当前模块

`inset-panel` 是第一个正式模块，包含“四面生成内嵌板”和“型材自动填充”两个命令。

`lineup` 为第二个正式模块，命令 ID 为 3，包含字段录入、装配级项目保存、顶层实例 ReferenceKey 关联、定位和 CSV 导出。实测范围及未完成项见 `DEV_0.4_插件框架/LINEUP-VALIDATION-20260909.md`。

## 目录约定

```text
DEV_0.4_插件框架/       当前平台宿主、内嵌板与 Lineup 模块
  src/                  宿主、模块、CAD 互操作和界面
  tests/                纯几何、自动填充和 CAD 验证
  tools/                构建、安装、检查脚本
  build/                本机编译输出
  artifacts/            测试输出
交付_DEV_*              历史交付包
DEV_0.2_*、DEV_0.3_*    历史开发快照
```

详细使用和构建说明见 `DEV_0.4_插件框架/README.md`。
