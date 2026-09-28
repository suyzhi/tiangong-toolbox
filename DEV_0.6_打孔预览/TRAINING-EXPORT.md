# 天工 CAD 出图训练数据导出器 0.2

本模块在 DEV_0.4_插件框架中增量升级，命令仍为“导出出图训练数据”（ID 4）。格式为 `schema=tiangong.drawing-training`、`schema_version=0.2.0`。验证记录见 [0.2 实测报告](TRAINING-EXPORT-02-REPORT.md)。

## 使用

在天工中打开已保存的 DFT / PAR / PSM / ASM，执行导出命令并选择独立目录。优先从 DFT 导出配对数据：模型关联来自原生 ModelLink，不按文件名猜配。模型入口只导出三维数据。

每次生成独立 sample 文件夹，包括：

- `sample.json`：原生几何、特征、工程图、关联、状态及脱敏清单。
- `training-labels.json`：模型和工程图标签；导出命令即生成，后处理补充派生视图。
- `export_summary.json`：自动运行的结构校验与覆盖率。status 表示结构检查结果，不能代替 training_ready。
- `models/m0001/body-1.obj` 等：单位米，逐个原生 Face 网格化，JSON 的 triangle_face_ids 与 OBJ 三角形顺序一致。
- 原生 CAD 副本及标准 PDF。当前受保护 STEP 输出未通过标准文件头验证，记为不可用，不尝试解密。

不保存源 CAD 文件；格式转换只在副本上进行，导出前后核对源文件 SHA-256。已有未保存修改的文档会被拒绝。部分 SDK getter 会刷新 CAD 内存缓存；导出代码不会把这种状态写回源文件。

## 批量和派生文件

已有 CAD 进程运行时，独立批量工具会拒绝启动。使用插件命令，或自行保存关闭 CAD 后运行：

```powershell
powershell -NoProfile -File tools\export-training-batch.ps1 -SourceDirectory '输入目录' -OutputDirectory '独立输出目录' -MaxFiles 3
powershell -NoProfile -File tools\prepare-training-data.ps1 -DatasetDirectory '独立输出目录'
```

批量入口默认使用 `build/training-export-02-20260910-release2`。加 -Recurse 才扫描子目录。不要把输出目录放到输入目录内。退出码：0 为无原生读取问题，3 为部分提取，1 为失败，2 为启动条件不满足。

后处理执行独立 JSON Schema、拓扑、引用、隐私路径、OBJ 分组与哈希验证，保留已有 PDF，生成逐页 SVG 和 150 dpi PNG，以及 validation.json、dataset-validation.json、index.jsonl。本机 numpy/pypdf 使用已有 Python；jsonschema/PyMuPDF 安装在独立的 build/training-python。原生插件不依赖 Python 即可输出 JSON、OBJ、PDF 和 export_summary。

## 新增数据

|范围|主要字段|
|---|---|
|Face|surface_kind、原生曲面参数、area_m2、native_center_m、normal_sample、parameter_range、edge_ids、adjacent_face_ids|
|Edge|curve_kind、原生直线/圆/椭圆/样条参数、start_m/end_m、length_m、parameter_range、vertex_ids|
|拓扑|vertices、loops、face_adjacency、edge_face_relations，包含双向校验|
|网格|triangle_face_ids、mapping_source、failed_face_ids、逐面 OBJ group|
|特征|feature_tree.features、原生类型、parameters、parameters_native、face_ids/edge_ids、草图、变量、设计树顺序|
|孔|孔径、启用的沉孔/沉头孔参数、孔类型/范围、草图孔中心、原生孔面关联|
|尺寸布局|原生 DisplayData 线/弧/文字位置/箭头、track_distance_m、text_offsets_m、所属视图与引用|
|标注|Datum、FCF、粗糙度、中心线、中心标记、焊接符号、气泡/引出线、修订符号的集合、属性、位置及可用附着关系|
|剖视和局部放大|section_data、原生父子视图、切割线/轮廓、方向、范围类型、局部放大边界|
|装配|occurrence_path、occurrence_records、零件 document/model ID、原生边引用；覆盖体拒绝套用未覆盖零件几何|
|数据集|文件内容哈希、几何/拓扑描述子、按最大边长归一化描述子、原生族身份、保守分组|
|验证|几何计数、拓扑一致性、逐三角形/逐面覆盖率、尺寸引用完整率、标注/实例覆盖率|

原生参数缺失或不适用时使用 null + availability/status。parameters_native 是 SDK 回读区，包含 CAD 默认值，不能直接作为“生效尺寸”监督标签；优先使用归一后的 parameters / hole_parameters。

孔接口 CountersinkAngle、BottomAngle 使用度，归一字段转换为弧度。未启用的沉孔、沉头、螺纹默认参数不会进入有效参数；通孔不使用其缓存默认深度。长度以米、面积以平方米存储。仍未核实单位的字段仅保留 native 值并明确标记。

## 关联和坐标

- DrawingView.Sheet 指向内部视图页，不能拿它的 Index 当工作页编号。本版从真实图纸集合枚举视图，以原生 DrawingView.Key 关联父子及所属关系。
- 二维对象使用原生 drawing_reference_key，在所属视图中匹配唯一曲线 ID。三维键经 BindKeyToObject 后取 canonical_reference_key；不使用最近距离或内存地址猜配。
- 装配样本经 ModelMember.ModelNode.GetAssemblyReferenceKey → AssemblyDocument.BindKeyToObject → 原生 occurrence → 零件 BindKeyToObject 关联到边。嵌套/覆盖/爆炸等未验证情形仍需检查状态。
- *_native_m 曲线点位于未缩放视图坐标；尺寸布局的点位于纸面坐标。使用 view_to_sheet_affine 连接。
- model_to_sheet_affine 是 2×4 矩阵。原生 ModelToView 在本机失败；用原生朝向和真实绑定直线校验平移，再组合 ViewToSheet。要求所有参与直线端点误差 ≤ 1 µm，保留验证过程和来源。ASM 整体投影及复杂视图不能因为局部引用成功就认定矩阵有效。
- 曲面 GetCenter 不被冒充为修剪面面积重心。centroid_m 仍为 null；native_center_m 与 normal_sample 单独保留。
- 剖切轮廓保留原生坐标及来源；未验证的坐标系、剖深、独立箭头位置明确标记，不自动当作纸面训练标签。

## 脱敏、数据分组与训练

正式 JSON 默认移除原始文件名、绝对路径、作者/项目等自由文本；必要时保留 SHA-256。尺寸/特征 ID、几何数值、原生类型和安全的标准基准标签保留。

**原生 CAD、PDF、SVG、PNG 仍包含原始内容，不能视为已脱敏。** 训练若要求视觉脱敏，需要单独审核这些文件。本版不清除水印或绕过导出保护。

normalized_geometry_hash 用原生曲面类型/面积/半径、边类型/长度形成排序描述子，并去除统一尺寸比例。它可能碰撞，不是形状同一性证明。index.jsonl 把共享源模型或相同归一描述子的样本保守地分到同组；完整近似件识别尚未实现，near_duplicate_group 保持 null。

training_ready 默认 false。已有几何、原生孔语义、视图选择、尺寸目标和布局可以按字段状态取出，构建对应任务的监督数据。supervision 提供引用完整性和布局可用性；GD&T 数值语义、复杂剖视、BOM、视觉图框和水印等仍需审核，不能把未支持项作为负样本。

## 从 0.1 迁移

原 `schema_version=tiangong.drawing-training/0.1` 改成 schema + schema_version + exporter_version 三字段，另保留 legacy_schema_version。surface_type、curve_type、value_native、reference_key、model_to_sheet_affine、OBJ/PDF 和原有文件名继续保留。

新增 surface_kind/curve_kind 为规范化类型；feature_tree 在每个零件模型下。geometry/native 参数缺失状态不可忽略。native_face_ids 现在有实际逐三角形映射，推荐使用 triangle_face_ids。source_name 变为 null + source_name_sha256；不能再把它当训练文本。

## 已知边界

- 本机 GetParentsAndChildren 曾使 CAD 退出，已禁用；设计树顺序、特征类型及实际几何关联仍保留。复制/导入模型不会伪造原始设计历史。
- 直中心线 GetCenter 不适用且会使本机 CAD 退出，使用实际起终点关联。
- FCF 接口返回格式化框格文本，没有经过验证的独立数值/GD&T 字段；本版不从文本猜语义，gdt_structured_tolerance=false。
- 粗糙度可读符号/数值文本及附着关系，数值单位未核实则 roughness_value_m=null。
- DisplayData 不提供经过验证的线条角色分类与独立文字 bbox，保留全部原生显示线，不猜哪条是尺寸线或界线。
- 球、环面、NURBS 和部分特征类别有合法类型库读取路径，但当前实测证据仅覆盖报告列出的类型。
