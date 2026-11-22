# Lineup 智能录入及普通零件关联验证（2026-09-09）

## 交付内容

正式 DEV_0.4 模块现为五类表格录入，菜单分类收纳；支持项目/阀岛自动编号及手改、H/W 成对生成、统一阀岛型号、型号库搜索/批量套用/导入/自动学习，以及 440×160 模型点选窗口。样表提取了 43 个完整型号规格随插件提供。

普通顶层 PAR 和子装配内部 PAR 可通过“多选模型加入”或“绑定下一行”关联。持久化键区分同一文件的不同安装实例；显示完整装配路径。内部零件高亮使用 SubOccurrence.Reference，持久化保存 SubOccurrence 自身键，不折算成丢失装配上下文的 ThisAsOccurrence。

## 当前通过证据

- [智能录入及零件专项日志](artifacts/parts-live-20260909-final/smart-native.log)：EXIT 0，39 项检查，其中 15 项编号/型号数据检查、24 项实际 CAD/表格工作流检查。
- 零件专项创建独立 Child.asm、NestedParts.asm、SmartPart.par：混合加入顶层普通零件和两个子装配内同名零件；引用对象归属解析；按实例去重；内部零件高亮和范围缩放；保存后关闭再打开装配，逐个重绑定及再次定位；已有行绑定普通零件。
- [表格原生回归](artifacts/parts-table-live-20260909/table-native.log)：EXIT 0，编辑自动保存、批量导入、多选加入、绑定后移行、填充/粘贴、删除撤销、分类导出及关闭重开。
- 用户样表 273 条、3003 个业务单元格逐一比较无损；五类分别 56/33/72/77/35 条，保留 2 条未编号调压阀及常闭传感器说明。输出可在 outputs/lineup-table-20260909 找到 CSV/TSV。
- [核心回归](artifacts/parts-live-20260909-final/core.log)通过。
- [原生命令启动日志](artifacts/parts-command-live-20260909/command-routing-native.log)：EXIT 0，装配环境三个命令均可用；CAD StartCommand 实际回调本地命令 3 并打开新版窗口。单独零件文档环境按装配级模块设计禁用。

## 已注册构建

build/parts-20260909/TianGongCadSuite.dll

SHA-256：EDB65FE5FEB9A2120627781020A916470142D383ECD67AA5302EDF4F998CB4CE

已注册到当前用户。运行中的 CAD 仍可能加载旧 DLL，保存工作并重启后生效。Install.cmd 以后从当前源码编译到新的时间目录再注册，避免重新装回根 build 的旧版本。

## 预览与验证边界

[表格预览](artifacts/parts-live-20260909-final/smart-table.png)；[点选窗口预览](artifacts/parts-live-20260909-final/smart-picking.png)。两张图来自实际 WinForms DrawToBitmap，不是桌面截图。

测试通过注册宿主在独立天工 CAD 实例中执行真实 COM 与表格逻辑；没有将 API 调用成功描述为人工鼠标验收。桌面截图服务此前报 SetIsBorderRequired 不支持此接口，完整桌面鼠标交互验收仍未完成。生产装配未用于写入测试。

仍不含 CAD 自定义属性写回、编号箭头/引线、真正 XLSX 和图文报告、任意字段映射和导入冲突交互。独立 PAR 文档仍不提供装配级 Lineup 菜单；本次支持的是 ASM 中的普通零件实例。

## 截图反馈修正版：连续绑定、阀片换岛、缺失字段

当前构建：build/binding-ui-20260909/TianGongCadSuite.dll（取代上文历史注册路径）。

[新版专项日志](artifacts/binding-ui-live-20260909-final/smart-native.log) EXIT 0，46 项通过。新增测试通过小窗口真实按钮的 PerformClick 触发事件，在实际 CAD 选择集上连续绑定两条，验证旧选择清空、待绑定记录前进、重复选择拒绝、返回大表格再进入小窗口保持记录位置。此为按钮事件加原生 COM 测试，不是物理鼠标点击验收。

原位和安装方式默认可见，位于编号后。阀岛型号按项目/Y 数字分别保存；新增多选阀片换岛下拉入口，换岛按目标岛递增生成编号，保持实例关联及阀片自身型号，可撤销。测试确认 Y1/ISLAND-ONE 与 Y2/ISLAND-TWO 互不覆盖，选中阀片迁到 Y2 编号后继且可撤销回 Y1。

[新版表格位图](artifacts/binding-ui-live-20260909-final/smart-table.png)通过视觉检查，原位和安装方式出现在默认视图。测试仍使用独立装配副本，没有修改生产装配。

[新版表格回归](artifacts/binding-ui-table-20260909/table-native.log) EXIT 0，原有样表导入导出及编辑/绑定/撤销工作流通过。修正版 DLL SHA-256：56D8F70D81A7FD01964BE22BECE47305E46EC0FC49425784BA2B00276A4EB559。
