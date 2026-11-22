# 0.2 实测与交付报告

已在原 DEV_0.4 共享宿主中完成增量升级，并注册到天工 CAD。**PAR 导出现在包含原生特征、参数、草图、特征与面/边的关联，不再只有点线面。**

当前交付是通过真实 CAD 验证的开发版；缺失语义明确标记，training_ready 仍为 false。此报告区分结构可用性、接口覆盖和完整出图监督能力。

## 实际交付

- `paired-samples/`：3 组用户原始 DFT → 原生模型的真实配对导出。
- `model-only/`：以 PAR、PSM、ASM 文件为入口分别实测的三维导出。
- `verification-fixtures/`：独立生成的孔特征测试件、在工程图副本中添加的标注/剖视/局部放大测试。**仅用于接口验收，不作为合格工程图训练正样本。**
- 每份数据包含 sample.json、training-labels.json、export_summary.json、validation.json、OBJ、原生副本；图纸样本另有 PDF/SVG/PNG。
- Schema、增量源码、使用及迁移说明、自动验证证据随包提供。原生副本引用尚未全部重定位，重新打开时仍可能需要原始依赖目录。

## 配对样本结果

下表仅统计工作页，不把背景页或内部视图页重复算作图纸。

|真实配对模型|实体|Face|Edge|特征|三角形|视图|尺寸|尺寸引用条数|尺寸→二维曲线|尺寸→三维边|全部引用完整率|Mesh→Face|
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
|PAR 板件|1|30|84|1|684|3|6|11|100%|100%|100%|100%|
|PSM 钣金件|1|18|48|1|100|4|6|10|100%|100%|100%|100%|
|ASM 装配族成员|2|32|84|2|184|3|11|18|100%|100%|100%|100%|

合计 80 个面、216 条边、968 个三角形、23 个尺寸、39 条尺寸引用。所有尺寸都取得文字位置和所属视图；所有三角形都有原生 Face ID，所有已枚举面均参与网格化。双向拓扑检查为 0 错误。装配的 18 条尺寸引用均保留可解析的原生实例路径，occurrence_mapping_coverage=100%。

PAR 的 3 个、PSM 的 4 个 model_to_sheet 矩阵均通过原生边端点核验。**ASM 的整体投影矩阵仍未验证，不能把边关联的 100% 解释为装配投影也已完成。**

PAR 原生特征为 ExtrudedProtrusion，深度 0.005 m，读到 7 个 Profile。PSM 和装配子件实际是 CopiedPart 特征。孔槽如果来自草图/导入形体，本版保留真实造型方式，不伪装成原生 Hole 特征，也不虚构导入前的设计历史。

## 独立模型入口

|入口|Face / Edge|特征|三角形|拓扑错误|Mesh→Face|
|---|---|---:|---:|---:|---:|
|PAR|30 / 84|1|684|0|100%|
|PSM|14 / 36|1|84|0|100%|
|ASM 主装配|32 / 84|2|184|0|100%|

直接打开目录中的 200.psm 与 200.dft 实际 ModelLink 引用的钣金文件不同，因此几何数量不同；插件始终跟随真实链接，没有按同名文件替换。ASM 主装配的原生族成员后缀含文件路径不允许的字符，现已在规范化物理路径之前正确拆出成员身份。

## 独立语义样本

真实创建并通过天工 SDK 读回：

- 普通通孔：孔径 0.008 m；未启用的沉孔/沉头默认参数为 null。
- 沉头通孔：孔径 0.006 m、沉头直径 0.012 m、原生 90° 转换为 1.5707963267948966 rad。
- 沉孔：孔径 0.006 m、沉孔直径 0.012 m、深度 0.004 m。
- 拉伸 + 三个孔，共 4 个原生特征、9 个面、11 条边、673 个三角形。额外验证了圆锥面参数读取；孔草图中心通过 Hole2d 和 Profile 坐标转换直接读回。
- Datum A、FCF 对象、粗糙度对象均取得原生附着边；Datum→3D 覆盖率 1/1。这只证明对象、位置和附着关系，**不证明 FCF 框格已正确解析成完整 GD&T 语义**。
- 局部放大 view-4 与剖视 view-5 的父视图均为 view-1；切割线、方向、轮廓与生成的子视图已读回。坐标系未充分核实的剖切字段仍明确标记。

测试图中的额外标注和视图位置是接口测试布置，含重叠及未经解释的 FCF 文本。视觉上已检查，不能作为出图质量正样本。

## 新增字段和修改文件

原有 model_to_sheet、朝向、比例、bbox、尺寸值/关联、ReferenceKey、OBJ、PDF 和后处理派生文件均保留。新增字段及使用方式详见 TRAINING-EXPORT.md。

|文件|变更|
|---|---|
|src/TrainingExport.cs|接入 0.2、模型特征、标注、原生 DrawingView/Curve Key、装配引用、自动标签及摘要；保护部分导出结果|
|src/TrainingExportModule.cs|更新现有命令说明；仍使用 IToolModule/命令 4|
|src/TrainingGeometry.cs|解析曲面/曲线、顶点/环/邻接，按原生面网格化与映射|
|src/TrainingFeatures.cs|特征、草图、变量、孔参数、有效性和 SI 归一；禁用不稳定依赖接口|
|src/TrainingNative.cs|经过类型库确认的属性读取、枚举值、逐字段错误和自由文本脱敏|
|src/TrainingAnnotations.cs|原生标注集合、附着关系、DisplayData 布局|
|src/TrainingSections.cs|剖切、局部放大、断开/局部剖状态和原生父子关系|
|src/TrainingProjection.cs|把原有已验证的边端点投影校验移入原生导出路径|
|src/TrainingDataset.cs / TrainingMetadata.cs|引用解析、脱敏、描述子和能力摘要|
|src/TrainingValidation.cs|双向拓扑、面映射、完整尺寸引用、实例/标注覆盖与监督字段状态|
|tools/prepare-training-data.py|独立 0.2 Schema/隐私/拓扑/引用/OBJ 校验，保留派生文件和保守分组|
|tools/export-training-batch.ps1|默认使用已验证的 0.2 DLL，源清单默认不写绝对路径|
|tools/TrainingSemanticFixture.cs / TrainingViewProbe.cs|独立原生接口测试及视图键诊断|
|tools/check-training02-evidence.py|实际样本及错误引用/私有路径/单位误用的防回归验证|
|training-sample.schema.json / TRAINING-EXPORT.md|完整版本字段、结构契约、使用及 0.1 迁移说明|

未重构内嵌板、Lineup 等无关功能。

## 已验证的 API 和真实边界

|接口或读取路径|实测结论|
|---|---|
|Face.Area/GetExactRange/GetCenter/GetParamRange/GetNormal/GetPointAtParam|平面/圆柱/圆锥样本可读；GetCenter 单列，不能当精确面积重心|
|Plane.GetPlaneData / Cylinder.GetCylinderData / Cone.GetConeData|原生参数可读|
|Edge.GetEndPoints/GetParamExtents/GetLengthAtParam；Line/Circle 参数|可读；参数范围保留，不猜未知角度基准|
|Body Faces/Edges、Face.Edges/Loops、Loop.Edges、Vertex.GetPointData|可恢复并校验双向拓扑|
|Face.GetFacetData|逐面网格化成功，全部实际三角形直接关联原生面|
|Model.Features、特征 Faces/Edges、Profile、HoleData|原生拉伸/孔/复制特征及已列出的有效参数可读|
|Hole2d.GetCenterPoint + Profile.Convert2DCoordinate|孔草图位置可读|
|Dimension.GetRelated / GetDisplayData；DisplayData 线/弧/文字/箭头接口|真实尺寸布局和附着引用可读|
|DrawingView.Key、二维曲线 Key、GetReferenceKey、Part/PSM BindKeyToObject|视图/曲线/三维边分别使用原生标识关联|
|ModelNode.GetAssemblyReferenceKey → ASM.BindKeyToObject → occurrence → Part/PSM.BindKeyToObject|本次装配全部 18 条尺寸引用成功|
|Datum/FCF/SurfaceFinish.GetTerminator|独立测试中的原生附着边可读|
|CuttingPlane.Profile/GetFoldLineWithViewDirection/SectionView；DetailEnvelope；SourceDrawingView|独立剖视/局部放大父子关系和原生轮廓可读|
|ViewToSheet + 原生朝向 + 所有匹配边端点校验|7 个业务 PAR/PSM 视图的矩阵有效|

本机类型库为 Interop.TG.dll。兼容 API 文档只用于确认接口，不代替天工实测。孔角度例子也见 [CountersinkAngle](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleData~CountersinkAngle.html) 与 [BottomAngle](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleData~BottomAngle.html)。FCF 官方接口提供格式化框格文本：[PrimaryFrame](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgeFrameworkSupport~FeatureControlFrame~PrimaryFrame.html)。

## 当前不可用或尚未充分验证

1. 精确修剪面面积重心、圆柱修剪轴向范围、部分圆弧角度基准：null + status。球/环面/NURBS 已接合法类型读取路径，尚缺对应真实样本验收。
2. 特征完整依赖/历史回放：GetParentsAndChildren 在反复实测中会使本机 CAD 退出，已全面禁用；不是把偶尔成功当可靠实现。同步/导入模型保留可访问的实际特征集合。
3. GD&T 独立公差值、直径公差带、基准次序与材料条件的可靠结构化解析：未完成，gdt_structured_tolerance=false。粗糙度单位未核实，SI 数值不填写；个别符号属性返回无效参数。
4. 独立文字 bbox、DisplayData 线条的尺寸线/界线角色、放置侧别，以及复杂布局决策：不猜测。
5. ASM 整体投影、嵌套装配/覆盖体/爆炸图的全面验收；复杂局部剖深及剖切轮廓坐标系、箭头位置、可见性规则。
6. 更完整的几何形状同一性和近似件检测。当前是保守描述子分组，不是完美去重。
7. 标准 STEP：当前受保护输出未通过 ISO-10303-21 校验，未绕过保护。
8. PDF/SVG/PNG 保留原始水印、用户名/公司图框和“没有参考”等源图属性错误。JSON 默认脱敏，视觉附件并未脱敏。

## 验证证据和运行状态

Phase 1、Phase 2、Phase 3 均有编译、真实样本导出和后处理校验记录。修复过程中发现的 CAD 崩溃、单位误用、内部视图页误当工作页、主装配成员路径问题均保留诊断记录；失败中间产物不放入本交付的训练样本目录。

相较 0.1：板件整体网格化的 720 个三角形改为逐面网格化的 684 个，PSM/ASM 的 100/184 保持。三角形数差异来自原生网格化调用粒度，不是漏面；面覆盖率和三角形覆盖率均为 100%。Phase 1 之后各轮稳定产物的几何与尺寸计数保持，7 个业务投影没有退化。

最终 3 组配对和 3 类独立入口通过 Schema、哈希、OBJ 分组、拓扑及引用检查。框架现有纯测试通过。汇总脚本在 12 份跨轮次有效产物上执行 87 项检查，全部通过；交付目录另有独立复验结果。CAD 关闭后，又对 0.1 留存审计对应的 5 个实际源模型复核 SHA-256，全部一致。

已注册 DLL：
`build/training-export-02-20260910-release2/TianGongCadSuite.dll`

SHA-256：
`e8e1e9ca06e4439390bb8f15effad0f5bead3efabf2c2d0f2e26a482d0059c95`

重启天工后的原生日志确认加载此路径，Registered 4 commands、导出命令 flags=1，且实际导出成功。**这不是物理鼠标点击菜单验收。**

推荐先对“视图选择、原生特征/几何目标、尺寸引用和布局”分别训练，并按 availability、mapping_status、supervision 与图纸审核状态筛选。对缺失 GD&T、粗糙度单位和复杂剖视的数据使用缺失掩码，不能当作不存在这些要求的负样本。
