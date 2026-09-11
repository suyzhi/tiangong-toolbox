# DEV 0.1.1 选取交互修复记录

日期：2026-09-08。当前 CAD：225.3.0.165，Windows x64。

用户报告：启动插件后无法选面、无法操作模型。

## 本次修改

- 先启动原生 CAD 命令，再取得并配置 Mouse 服务、面/边过滤器和事件订阅。
- 命令使用 seNoDeactivate，启用跨文档定位 InterDocumentLocate、智能捕捉，并限定模型视口，避免路径查找器返回整件对象。
- 初始窗口缩为左侧窄面板，选完定位点后展开预览；窗口可拖动和调整大小。
- 未命中模型时显示提示；记录命令状态、接受和拒绝的对象类型，以便定位真实操作问题。

## 证据和边界

本机实测：在命令 Start() 之前设置 InterDocumentLocate 会返回 E_FAIL；改为启动后设置，配置读取确认成功。完整原生回归测试记录在 examples/native-test-20260908-110821.log，45 条 PASS、EXIT 0，包含实际 CAD 建模、尺寸编辑、装配实例和嵌套坐标、取消及失败清理。所有写入测试使用独立夹具。

这验证了命令配置及引用处理，不等于已经证实用户的实际鼠标故障消失。原来的鼠标点击日志显示引用被拒绝，但没有记录它的底层对象类型，因此无法仅凭历史日志完全确认原始原因。

窗口截图连续返回 SetIsBorderRequired failed / 0x80004002，基于控件的点击也返回 coordinate input geometry is unavailable，无法自动完成真实鼠标选面到保存的交互验收。因此本版仍为 DEV 修复版，不宣称已通过现场操作验收。

初始化顺序参考维护者的 [MouseEvents 示例](https://github.com/SolidEdgeCommunity/Samples/blob/master/General/MouseEvents/cs/MouseEvents/MainForm.cs)；跨文档定位含义见 [SDK 接口文档](https://support.industrysoftware.automation.siemens.com/trainings/se/106/api/SolidEdgeFramework~Mouse~InterDocumentLocate.html)。

## 加载本次版本

本机已注册到本交付目录的 build/TianGongPanel.dll。正在运行的 CAD 可能仍持有旧 DLL，保存当前工作后重启 CAD 才能可靠加载更新。

新参数窗口标题应为“生成矩形板 · DEV 0.1.1”。初始窗口在左侧，选面阶段保持窄面板。没有出现该标题时，仍运行的是旧版；可在退出 CAD 后运行本目录 Install.cmd，再启动 CAD。

更新没有关闭或保存用户的工作装配。旧 DEV 0.1 压缩包保留，避免混淆请以本次目录为准。