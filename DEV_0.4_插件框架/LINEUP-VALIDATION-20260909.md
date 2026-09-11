# Lineup 正式框架接入与天工 CAD 实测

## 后续修正：原生菜单全部变灰

用户实际打开生产装配时发现三个框架按钮全部禁用。根因是 ToolRegistry.Find 用 CAD 分配的运行时 ID 查找菜单回调传入的本地命令 ID。前述直接功能入口测试未覆盖此路径，因此不能用前述结果证明原生菜单启用正确。

已修正为：OnCommand / OnCommandUpdateUI 回调按稳定本地 ID 查找，NativeId 单独保存 CAD 运行时 ID，仅供 CAD StartCommand 调用。添加命令路由回归测试。

独立 CAD 验证中，本地 ID 1/2/3 对应运行时 ID 62014/62015/62016；三个装配命令的 FLAGS 均为 1，零件环境均为 0，未知 ID 为 0。通过 CAD StartCommand(62016) 实际收到 OnCommand(3)，成功打开 Lineup。日志：[command-routing-native.log](artifacts/command-fix-live-20260909/command-routing-native.log)。

当前注册的修复构建位于 build/command-fix-20260909/TianGongCadSuite.dll。用户原有 CAD 会话未关闭，也未替换其正在加载的旧 DLL；需要保存工作并彻底退出、重启 CAD 后生效。下方默认 build 路径及 SHA-256 属于此修正之前的验证记录。

回调接口对照：[SolidEdgeCommunity 原生 RibbonController 实现](https://github.com/SolidEdgeCommunity/SolidEdge.Community.AddIn/blob/master/src/SolidEdge.Community.AddIn/RibbonController.cs)，其中本地 CommandId 与 SolidEdgeCommandId 分别处理。

日期：2026-09-09。结论：正式模块、默认安装构建及本报告列出的原生专项通过；尚不能认定全部 Lineup 功能或完整鼠标操作验收通过。

## 交付和接入

- 继续使用 DEV 0.4 的唯一宿主，Lineup 实现 IToolModule，由 ModuleCatalog.Create 注册，命令 ID 3；原有两个内嵌板命令保留。
- 原生 CAD 返回 Registered 3 commands。最终默认 build/TianGongCadSuite.dll 已注册并由全新 CAD 进程加载。
- DLL SHA-256：BF32C4FC14619DCFF24273DAD2B601D9FA46909B46D763EE88E13FF215E57C55。
- 注册宿主的 OpenLineupPanel 诊断入口调用同一 Lineup 命令动作，实测打开了所属 CAD 的窗口并自动载入两条记录。这不等于鼠标点击菜单验收。
- 已保留测试装配和实际宿主打开的 Lineup 窗口，供继续观察。没有打开或改写用户生产装配。

## 本次修复和新增

1. 将装配路径从 Path 改为 FullName。CAD 实测证明 Path 仅包含目录；旧代码可能把多个装配的数据保存到错误的共同位置。
2. 拒绝尚未保存的装配。损坏、版本不支持、属于其他装配的 XML 不再静默变成空项目，避免后续覆盖旧数据。旧位置文件不自动迁移或猜测归属。
3. 模型关联与“型号”独立。捕获顶层实例的原生 ReferenceKey，Base64 写入 XML；保存实际源文件与实例名。
4. 使用 BindKeyToObject 恢复模型关联；没有持久化键的旧记录要求重新选择绑定，不按同名模型猜测匹配。
5. 列表显示编号、描述、型号，可回显字段和更新已有记录。新增记录有独立按钮，编号去除首尾空白并忽略大小写查重。
6. 增加类别选择；所属阀岛、阀片填写记录编号，保存时转换为稳定内部 ID 并执行关系一致性校验。尚不是关系下拉选择器。
7. 保存成功后才更新内存项目；保存失败保持已有项目。切换装配或另存装配时阻止继续写入旧项目。
8. 列表选择创建本窗口拥有的高亮集，切换或关闭时仅清理自己的高亮；“定位模型”使用对象范围加边距进行局部缩放。
9. 13 字段 CSV 导出沿用严格引号转义，关系字段导出可读编号。修复分栏，字段区和按钮完整显示。

## 真实 CAD 专项结果

环境：本机天工 CAD 2025 标准版，安装接口 Interop.TG.dll；测试通过已注册的进程内插件加载相邻 PanelTests.exe。测试创建专用 ASM/PAR，不使用生产文件。

第一组 19 项原生断言通过，另有 4 项 Lineup 数据层断言通过：

- 拒绝未保存装配；使用含文件名的装配路径；字段编辑区宽度合格。
- 拒绝空选择和多选；读取选择不覆盖型号。
- 持久化编号、ReferenceKey、源文件；修改记录不重复增加条目。
- 拒绝空编号、大小写/空白重复编号以及不存在的关系编号。
- 同一 PAR 的两个实例拥有不同的 ReferenceKey；阀片编号关联正确转换为阀岛 ID。
- 高亮与局部缩放原生调用成功；模型图像显示目标板件局部视图。
- 从真实窗口导出 CSV，验证 13 列、中文、逗号、引号、换行和关系编号。
- Lineup 操作前后生成的 ASM/PAR 文件 SHA-256 不变。
- 关闭重开装配后，两条记录均重新绑定；项目和关系字段正确恢复。
- 模拟保存失败，已有 XML 保持不变。

第二组：退出 CAD，确认无 CAD 进程后启动新进程，7 项断言通过：

- 加载两条持久化记录，两条 ReferenceKey 分别恢复到原实例。
- 重启后的窗口恢复字段，并执行高亮/缩放。
- 装配文件哈希不变。
- 找到实际注册宿主的三个命令。
- 通过实际宿主 Lineup 命令动作打开拥有两条记录的窗口。

最终默认 DLL 上还运行了原有纯几何/自动填充及 Lineup 数据测试，退出码 0。本次未重新执行原有内嵌板全部原生 CAD 测试。

## 证据

- [19 项原生断言及 4 项数据断言日志](artifacts/lineup-live-20260909-final/lineup-native.log)
- [重启及实际宿主命令日志](artifacts/lineup-live-20260909-final/lineup-20260909-085538/restart-native.log)
- [默认构建回归日志](artifacts/lineup-live-20260909-final/pure-regression.log)
- [构建哈希](artifacts/lineup-live-20260909-final/installed-build-sha256.csv)
- [源码哈希](artifacts/lineup-live-20260909-final/source-sha256.csv)
- [模型操作前哈希](artifacts/lineup-live-20260909-final/lineup-20260909-085538/model-sha256-before.txt)
- [实际宿主打开的窗口渲染](artifacts/lineup-live-20260909-final/lineup-20260909-085538/native-host-lineup-form.png)
- [原生 CAD 局部模型图像](artifacts/lineup-live-20260909-final/lineup-20260909-085538/native-locate.png)
- [测试装配](artifacts/lineup-live-20260909-final/lineup-20260909-085538/LineupFixture.asm)
- [导出 CSV](artifacts/lineup-live-20260909-final/lineup-20260909-085538/Lineup.csv)
- 原代码、脚本、测试、文档和旧构建备份：artifacts/lineup-integration-20260909-084522，包含修改前 SHA-256。

## 必须保留的验收边界

Windows Computer Use 在重新绑定窗口后仍两次返回 SetIsBorderRequired failed: 不支持此接口 (0x80004002)。因此没有使用盲坐标或替代 UI 注入；原生菜单鼠标点击、CAD 前台高亮颜色的肉眼检查尚未完成。

窗口图像由实际进程内窗体 DrawToBitmap 生成，CAD 模型图像由 View.SaveAsImage 生成。两者不是桌面截图；模型导出图像不作为高亮颜色覆盖层可见性的证据。高亮目前证明原生 API 调用成功，完整视觉验收仍待完成。

关联实测只覆盖普通顶层零件的两个实例。顶层子装配作为一个对象的接口路径已接入但未做专项；深层内部零件、替换/删除/重命名、迁移 XML 和跨装配关联未验收。

## 尚未实现或未完整验收

- 写回 CAD 零件/装配自定义属性。
- 模型编号标签、箭头、引线和带截图的图文 Lineup 报告。
- 关系下拉选择、按类别动态字段、高级筛选与分组。
- 生产级 CSV 导入、字段映射、重复处理和错误报告。
- 真正的 Excel 工作簿导出。
- 上述复杂实例场景及完整原生菜单鼠标操作、视觉高亮验收。

用户提到的“打开装配自动加载”应准确理解为：打开已保存装配后，再打开 Lineup 窗口时加载对应 XML；没有装配打开事件自动弹窗。

接口参考：[Siemens 原生 ReferenceKey 持久化说明](https://blogs.sw.siemens.com/solidedge/storing-roadmap-of-an-object-using-solid-edge-api/)。天工兼容性结论以本机日志为准。
