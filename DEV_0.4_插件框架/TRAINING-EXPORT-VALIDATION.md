# 出图训练导出实测记录 · 2026-09-10

状态：开发版已编译、注册并通过天工 CAD 原生 API 导出实测；具备实际可读的网格和图纸数据。尚未完成桌面鼠标点击导出命令的交互验收，不称为完整训练数据终版。

## 样本与结果

源目录：`H:\桌面\SK26.01\SK26.01 OK310\OK310 围栏`。递归盘点发现 75 份 DFT；本次仅测试以下 3 份，不代表全部目录已经导出或验收。

|工程图|类型|导出实体网格|三角形|视图|尺寸|通过端点验证的投影|
|---|---|---:|---:|---:|---:|---:|
|SK26.01-OK310-03-004.dft|PAR 零件|1|720|3|6|3|
|200.dft|PSM 钣金|1|100|4|6|4|
|110-200线槽3095.dft|ASM 族成员 3095，包含 PAR 和 PSM|2|184|3|11|0，保持未验证|
|合计||4|1004|10|23|7|

三份样本分别导出了 1 页标准 PDF，后处理生成对应的 SVG 和 PNG。PDF 和 PNG 已做视觉检查，图框、视图和标注可见；存在原图/导出环境带来的水印，以及图框“错误：没有参考”等文字。这些内容原样保留，不能直接作为已清洁的训练目标。

## 通过的检查

- 插件编译成功；既有核心测试通过。
- 新 DLL 在独立天工 CAD 实例加载，三份 DFT 均记录 `Registered 4 commands` 和 `EXPORT_FLAGS=1`。
- 真实 ModelLink 关联 PAR/PSM/ASM；ASM 的 `!3095` 被识别为族成员，物理文件和成员身份分别存储。
- 网格坐标有限，面索引有效，三角形数一致，网格包围盒与 CAD 精确包围盒在导出公差内一致。
- 三维边面引用键、图纸线/圆弧/圆引用键和尺寸关联被导出；通过原生 BindKeyToObject 取得可对齐的边引用。
- 零件和钣金共 7 个视图的模型到视图矩阵通过所有匹配直线端点检查，阈值 1 µm；通过原生 ViewToSheet 组合得到纸面投影矩阵。
- 标准 PDF 页数与工作页数匹配；生成逐页 SVG/PNG。
- 所有已登记导出文件 SHA-256 核验通过。3 份用户原始 DFT 与导出前记录的哈希一致，已读取模型的导出前后哈希校验通过。

## 尚未通过/尚未覆盖

- STEP：本机 SaveCopyAs 和副本 SaveAs 产生的输出没有标准 STEP 文件头，观察到 `%TSD-Header-###%`，原因未确定。导出文件以 `.invalid` 保留诊断，`step_file=null`，不交给 STEP 训练解析器。
- 原生 ModelToView 返回 E_POINTER。保留接口错误；仅在后处理中对零件/钣金使用独立几何一致性验证过的替代矩阵。没有伪造原生成功。
- 装配视图涉及族成员和断开显示，尚未实现其分段映射；不把单一仿射矩阵标为有效。
- 完整形位公差、粗糙度、焊接符号、明细表、所有复杂曲线、剖面遮挡语义以及可回放的出图操作序列尚未实现。
- 尚未验证单独 PAR/PSM/ASM 入口的鼠标交互、任意大目录批量运行和 SolidWorks 原生工程图。当前样本来自 DFT 入口。
- `training_ready=false`。文件校验通过仅表明数据可读且一致，不等于标注语义完整或可以直接训练。

## 交付位置

- 导出模块：`src/TrainingExport.cs`、`src/TrainingExportModule.cs`
- 已注册 DLL：`build/training-export-20260910/TianGongCadSuite.dll`
- 使用与格式说明：[TRAINING-EXPORT.md](TRAINING-EXPORT.md)
- 机器可读结构约定：[training-sample.schema.json](training-sample.schema.json)
- 实测样本目录：[artifacts/training-export-20260910/samples](artifacts/training-export-20260910/samples)
- 数据校验汇总：[dataset-validation.json](artifacts/training-export-20260910/samples/dataset-validation.json)
- 原始文件哈希：[original-source-integrity.json](artifacts/training-export-20260910/samples/original-source-integrity.json)
- 原生运行日志：[native-final.log](artifacts/training-export-20260910/native-final.log)
- 修改前代码备份与注册表备份：`artifacts/training-export-20260910/*.before`、`registration-before.reg`

原来的 DEV 0.1–0.3 和交付包未修改，未向网络上传模型或图纸。
