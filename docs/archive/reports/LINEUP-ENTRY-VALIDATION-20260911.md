# Lineup 快速录入验证 · 2026-09-11

快速录入已经编译、安装，并在天工 CAD 2025 的独立测试装配上通过原生流程及全新进程重开验证。最后通过 CAD 的 `StartCommand` 启动了实际安装版的 Lineup 窗口，当前保留的是生成的 `EntryFixture.asm` 测试装配。

## 用户可见变化

- 常用字段集中在一个录入区，直接填写前缀和 Y 数字。按类目记忆共用字段，支持沿用已有行和数量批量录入。
- 多选模型后，一次操作完成新增、编号、绑定、上级关系和可选 H/W；同源实例的唯一已知规格可复用。
- 连续新增 / 连续绑定自动处理稳定的 CAD 选择，重复不前进，末行自动停止；支持整批撤销。
- 原位、安装方式常显，高级插入 / 移动 / 层级操作收进菜单；F4 根据焦点选择本批或当前行型号。
- 修复退出时高亮 COM 对象已断开仍触发动态空值检查的问题，避免 .NET 异常对话框。

操作说明见 [LINEUP-QUICK-ENTRY.md](LINEUP-QUICK-ENTRY.md)。

## 最终构建和安装

- 构建：`build/release-20260911-200150/TianGongCadSuite.dll`
- 实际安装：`C:\Users\admin\AppData\Local\TianGongCadSuite\app\TianGongCadSuite.dll`
- 二者 SHA-256 相同：`3C60C38AC0257498C9ADBAEA4CC0C015F6FE3EAF956EA438CB035902DD34AD87`
- 实际 CAD 加载路径、哈希、命令回调及窗口数记录：`artifacts/lineup-entry-20260911-143610/installed-native.json`。结果为 `Registered 4 commands`、`LastCommand = 3`、`LineupWindows = 1`。
- 实际安装版的可访问性控件树已核对并保存为 `installed-ui-tree.txt`，可见快速新增、连续新增、连续绑定、数量、上级、原位和安装控件。

## 最终验证

以下六组共 161 项断言，全部 `EXIT 0`。各原生测试组包含前置的纯数据检查，不能把每项断言都称作鼠标实测。

| 验证组 | 通过数 | 主要覆盖 |
| --- | ---: | --- |
| 快速录入数据与原生流程 | 40 | 批量 H/W、稳定父 ID、重复保护、原生多选、定时器连续录入、整批撤销、末行停止、重开绑定 |
| 编号 / 型号库 / 嵌套模型回归 | 46 | 自动编号、手填保留、子装配实例区分、定位、顺序绑定、阀岛改派 / 撤销 |
| 清单表格回归 | 37 | 273 条样表 / 3003 个业务单元格、10 列 BOM、不移位 IO / 接口、粘贴、自动保存、撤销 |
| 原有 Lineup 数据与原生关联 | 24 | 空 / 多选拒绝、字段和关系、CSV、保存失败保护、CAD 文件哈希不变 |
| 快速录入全新进程重开 | 7 | 11 条记录、共用规格、5 个原生关联恢复；不会自动开启连续模式 |
| 原有 Lineup 全新进程重开 | 7 | 字段 / 关系重载、定位、宿主命令打开窗口、原生文件哈希不变 |

另有 7 项层级纯数据检查通过，见 `artifacts/lineup-entry-20260911-143610/hierarchy-final.log`。

最终测试证据目录：`artifacts/lineup-entry-20260911-143610/verified/`。完整退出与重开退出分别记录在 `shutdown.txt`、`shutdown-restart.txt`，均为 `CAD_QUIT_OK`，无 `shutdown-exception.log`。

主界面和紧凑模式的 WinForms 原生渲染位图：

- `verified/entry/entry-form.png`
- `verified/entry/entry-continuous.png`
- `verified/entry/entry-fresh-process.png`

## 已修复的实测失败

1. 上级下拉框初始化顺序错误：必须先指定 `AutoCompleteSource.ListItems`，再设置自动完成模式。
2. CAD 暂忙时测试脚本被拒绝调用：测试使用已有 `OleFilter` 的有界 COM 重试。
3. 旧检查仍假定平台只有 3 个命令：改为验证 Lineup 本地 ID 3 的实际运行时 ID 和启用状态，兼容训练导出命令 ID 4。
4. 宿主退出时清理高亮发生 `RPC_E_DISCONNECTED`：将空值判断改为普通对象判断，把 COM 删除调用完整放入清理异常保护内。修复前的调用栈保存在较早的 `native-final` 目录，最终 `verified` 无此异常。

## 数据保护和验收边界

修改前已备份 DEV 源码、测试、脚本、注册信息和 DLL，并记录 SHA-256；检测到安装版路径后又备份 `installed-app-before`。测试使用独立生成的 ASM / PAR / XML 和隔离型号库，没有编辑生产 CAD 或 WPS 文件。

已完成原生 API / 定时器 / 实际命令调度验证、WinForms 渲染检查以及实际窗口控件树核对。Windows 图像捕获仍返回 `SetIsBorderRequired failed: 不支持此接口 (0x80004002)`，输入调用返回 `coordinate input geometry is unavailable`，所以没有声称完成物理鼠标 / 键盘端到端验收。
