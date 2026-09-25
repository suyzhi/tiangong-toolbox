# 自动打孔功能调研：凯元工具箱 / 嘉立创 ICAN，以及天工CAD 落地方案

日期：2026-09-25　范围：只做调研与可行性设计，不改代码。
证据分级：**[实测]** 本机 DLL 反射 / 官方安装包解包 / 官方帮助页原文；**[官方]** 厂商文档与公告；**[推断]** 按语义推测，未证实。

> **修订记录 v2**（本版修正了两处 v1 的错误，均由深度调研推翻）：
> 1. v1 称"凯元工具箱没有自动打孔"——**错**。凯元 v4.69（2024-07-27）就加了「自动打孔」，提示语为"装配体批量打孔"。v1 只看了 2024 年的功能简介 PDF，该 PDF 早于该功能上线。
> 2. v1 对 ICAN 实现机制是 **[推断]**，本版换成 **[实测]**：核心是**参照孔反推**，不是面中心自动布孔。

---

## 一、结论速览

1. **两家都有自动打孔，但成熟度差一个数量级。** 凯元 v4.69 上线后官方自己标注"**初级版**"，到 v4.76 再无任何增强，且**官方从未发布任何该功能的文档**（安装包帮助索引里没有它的条目）。ICAN 则是 5 个打孔功能 + 一整套规则文件 + 完整帮助中心。
2. **ICAN 的核心机制是「参照孔反推」**，不是"选个面自动布孔"：选参照件上的**参考孔** → 取其边线与直径投影到目标面 → 生成草图点并与孔边线做**同心约束** → 调 SolidWorks 异型孔向导生成孔特征。官方 FAQ 原话可作铁证："两个零件本身就不平行，孔边线与草图点都无法手动约束同心"。
3. **ICAN 的孔规格靠"直径区间查表"反推**，型材排孔靠 **CSV 查表**——都不是几何推理。它把工程知识外置成了可编辑的文本规则文件。
4. **ICAN 有专门的「机架设计模块」，含「自动开口哨孔」**（铝型材机架开口打沉头孔）。**这与本项目（型材框架）场景直接对应，是最该抄的一块。**
5. **天工CAD 原生孔命令已经很强**，底层 API 与 Solid Edge 同构且是其**超集**。插件要做的是自动化与校验，不是重写打孔内核。
6. **最值钱的是"配孔检查"**：它是**规则比对**（孔径范围 + 轴向间距阈值），不是布尔干涉，纯几何 + 规则，可移植性最高。

---

## 二、凯元工具箱（KYTool）——有，但极其简陋

**版本 4.76（2026-04-25），支持 SW2012–2026。** [实测] 从官方安装包 KYTool4.76 解包后读取功能注册表：

- `datas/IMG/ID.cfg:45` → `3=69\t=自动打孔\t=装配体批量打孔`（**功能 ID 69，提示语即"装配体批量打孔"**）
- `datas/IMG/listaddin.kdb`（base64 明文工具条清单）第 38 组整组是"孔/上色"组：`38=69=3=自动打孔`、`38=67=1=孔上色`、`38=68=1=快速上色`
- `datas/IMG/Exten/hole.png` 图标文件存在

**沿革**（[官方更新日志](http://kytool.cn/upload/soft.html)，我已直接核对）：

| 日期 | 版本 | 与孔有关的内容 |
|---|---|---|
| **2024-07-27** | **4.69** | **「添加了自动打孔工具（初级版）」** ← 首次出现 |
| 2026-04-08 | 4.75 | 修复公差标注不能标孔标注的尺寸 |
| 2026-04-25 | 4.76 | 新增"孔上色" |

**关键判断：**
- 自 v4.69 上线至 v4.76，自动打孔**再无任何增强记录**，官方自己标"初级版"。
- **官方从未发布该功能的文档**：[实测] 安装包 `help/IDHelp.cfg`（全量帮助索引，241 行）**没有 69/67/68 任何条目**，且全文**无"打孔"二字**；在线帮助树逐 ID 枚举零命中；help/ 目录 90 余份文档也无。→ 选什么对象、有哪些参数、有没有预览，**全都无法确认**。
- [实测] 程序集被防破解壳加密（.NET #US 堆不可读）。内嵌的 SolidWorks interop 里含 `IWizardHoleFeatureData2`、`IHoleTable`、`swSelHOLESERIES`、`get_TapDrillDepth/TapDrillDiameter`、`FeatureCut3`、`FeatureLinearPattern2`、`FeatureCircularPattern2/3/4`；`datas/Lun/DB/` 下有 `普通螺纹1.csv`（ID,d,p,cu,d2,d1）、梯形螺纹、锯齿形螺纹。**但 interop 是整表内嵌，接口存在只属"能力面"证据，不能证明实际调用。**

> **结论：** 凯元把"孔"收敛为"**一个装配体级批量功能 + 一个可视化辅助（孔上色）**"，入口单一、功能收敛——这个设计取向值得借鉴。但它的实现细节不可知、功能被官方自认初级，**不适合作为主要参考对象**。唯一可确证的启发是：**它也在装配体层级做批量打孔**，说明这是真实需求场景。

---

## 三、嘉立创 ICAN 工具箱——真正的参考对象

**版本 V10.0.5，95+ 功能，2025-09 被嘉立创收购后永久免费。**

### 3.1 打孔功能全景 [实测·官方帮助中心]

ICAN 有 **5 个打孔功能**（功能区1），外加一批孔相关工具：

| 功能 | 作用 |
|---|---|
| **平板打孔** | 平板上批量配孔；一键选相同孔、自动识别规格、自动标注孔草图尺寸 |
| **超级打孔** | 装配体快速配孔：以参照件的孔为基准，在目标件对应面配做同规格孔 |
| **定位销孔** | 按销径/孔深/打孔方式批量生成销孔 |
| **线阵列孔** | 沿直线按间距/数量阵列打孔 |
| **环阵列孔** | 沿圆周均布打孔，支持圆管端面 |

其余孔相关：**配孔检查**（查漏配孔/偏孔/规格不匹配）、**合并同孔**、**修改孔规格 / 编辑孔草图**、**圆孔上色**、**第三方零件孔转 Sw 孔特征**（无参 STEP 的沉头孔/通孔转成异型孔向导特征）、**自动开口哨孔**、快速特征里的**圆柱面打孔**。工程图侧：**孔规格标注、插入孔表、孔中心线**，自动出图可勾选"标孔定位尺寸"。

### 3.2 超级打孔交互流程 [实测·官方 CHM 截图]

对话框共 **6 个页签**：**通孔 / 螺纹孔 / 圆柱沉孔 / 腰孔 / 锥形沉孔 / 沉头腰孔**。

- **通孔页**：直径、孔深、贯穿、下一面、平孔；按钮「写入参照孔径 / 打错孔 / 配做螺纹通孔 / 打通孔」
- **螺纹孔页**：规格（下拉，如 M12）、孔深、螺纹深、贯穿、下一面
- **圆柱沉孔页**：规格、沉孔Φ、沉孔深、通孔Φ
- **底部开关**：保持关联 / 尺寸约束 / 智能选择
- **状态栏实时反馈**："参考孔直径：13.5　孔数量：4　已匹配到螺纹孔！"

**操作四步**：① 选**参考孔**（参照件上的孔）→ ② 按住 Ctrl/Shift 选**打孔面**（目标件）→ ③ 选页签定孔类型 → ④ 点「打孔」。完成后目标零件生成孔特征，孔草图尺寸自动标注。

### 3.3 底层机制 [实测·安装包规则文件 + 官方 FAQ]

这是本报告最有价值的部分——**全部来自官方安装包内的规则文件，不是猜测**：

1. **孔位来自"参考孔"几何反推，不用面中心/质心。** 取参照件孔的**边线与直径**，投影到目标件打孔面生成草图点，再与**孔边线做同心约束**。官方 FAQ 铁证："两个零件本身就不平行，孔边线与草图点都无法手动约束同心"（[超级打孔相关问题](https://www.jlc-jdgf.com/icanhelp/JDGF0000010107/56125)）。
2. **规格靠直径区间查表反推。** `用户默认设置/螺纹孔底孔匹配规则.txt` = `1.1≤X<1.35=M1.6 … 17≤X<18=M20`；`沉孔匹配规格.txt` 同理。UI 里"参考孔直径 8.5 → M10 圆柱沉孔（沉孔Φ18/深11/通孔Φ11）"就是这两张表的输出。
3. **孔类型调 SolidWorks 异型孔向导标准库，不自造孔。** `超级打孔孔默认标准与类型配置.txt` = "螺纹孔-标准13／圆柱沉孔-标准13／锥形沉孔-标准13"（SW 孔标准索引）。
4. **型材排孔是查表，不是几何推理。** `口哨孔数据.csv` 按型材系列（欧标20/30系列、国标40×8系列…）给出 **孔径、孔深、边距、实际打孔直径**、品牌、连接件型号、采购链接。表里出现 19 与 **19.001** 这类值——更新日志解释："新增实际打孔直径参数，**避免相切情况导致孔特征生成失败**"，即刻意加 0.001mm 破相切。
5. **配孔检查是规则比对，不是布尔干涉。** `配孔检查配置.txt` 只有两行："**孔径检测范围：3-25**"、"**两孔上下间距小于等于多少视为配孔：6**"。按孔径 + 轴向距离配对后判 漏配孔/偏孔/规格不匹配。
6. **无参孔识别同样靠"底孔直径→规格"表**；官方还提示"通孔直径为整数，可能是销孔或螺纹"。
7. **未找到**最小壁厚/边距碰撞检查的公开资料；`干涉报告` 是独立装配体干涉检测，**与打孔不联动**。

### 3.4 结果形态

孔是**目标零件内部的 SolidWorks 异型孔向导切除特征**，在装配体上下文中生成（特征名带 `零件2^超级打孔<1>` 后缀），**不是装配体特征、不是多实体切除**。一次命令可生成多孔；「智能选择相同孔」把同面同规格孔并入同一特征。

> **这一点对我们的 API 选型有直接影响**：ICAN 走的是"**在零件文档里逐个建孔特征**"（相当于 Solid Edge 的 `PartDocument.Models.Holes`），**不是** `AssemblyFeaturesHoles`。天工CAD 侧建议同样优先走零件级 `Model.Holes`，装配级 `AssemblyFeaturesHoles` 留作"一次贯穿多零件"的补充。

### 3.5 已知限制 [官方 FAQ / 更新日志]

1. **必须先打开"外部参考"**，否则打孔失败；
2. **两个零件不平行 → 同心约束失败**（核心机制的固有约束）；
3. **孔规格库缺规格会报警**；
4. 轻化零件状态下单零件多选孔会报警（V9.5.1 已修）；环阵列孔曾无法在圆管端面打孔；线阵列孔曾不能输入小数间距；
5. 自动装配螺丝长度识别"不一定准确，仅供参考"；自动出图"不是万能，只对板类块类零件效果较好"；
6. 无公开算法/API 文档，官方教程几乎全是视频。

### 3.6 机架设计模块——与本项目最相关 [实测·官方帮助中心]

ICAN 有一个独立的**机架设计模块**，8 项功能：

> 1. 快速绘制机架3D草图　2. 自动生成机架模型　3. 多型材机架快速生成　4. 偏移型材功能　5. 旋转型材　6. 同类选择　7. **自动装配角码**　8. **自动开口哨孔**

**这几乎就是本项目要做的事**：型材框架 → 自动建模 → 自动装角码 → 自动在型材上开口打孔。建议后续单独调研这一模块，它比"超级打孔"更贴近我们的场景。

---

## 四、天工CAD 侧能力盘点

### 4.1 原生孔命令已经很强 [官方帮助]

**顺序建模（孔命令条）** —— 步骤：孔模板 → 孔类型 → 孔规格 → 平面 → 孔（放置孔圈）→ 范围 → 体选择 → 完成。
- 范围四选一：**贯通 / 穿过下一个 / 起始·终止范围 / 有限范围**
- 体选择：**切割活动的**（只穿活动体）或**选择切割体**
- 支持 **V 型孔底、孔底角度、到孔肩部的深度、到孔端部的深度**（新版命令条）
- 支持**物理螺纹**（真实螺纹）vs 装饰螺纹

**直接建模** —— 拖到面上单击放置；F3 锁定平面；悬停边按 **E/C/M** 取端点/中心/中点做动态尺寸；**一次命令放多个孔，共享同一组属性**。

**装配环境（孔命令条）** —— 步骤：孔模板 → 装配特征选项 → 孔类型 → 孔规格 → 草图绘制 → 孔 → 范围 → **选择零件** → 完成。其中「选择零件」会**自动选中落在轮廓与范围之内的零件**，Ctrl 可取消选中。这是"一次打穿多个零件"的原生入口。

**工程图孔参数表** —— 可按图纸视图自动收集，也可按用户选择；支持沉头孔/埋头孔/螺纹孔的**同心圆识别**，支持孔编号、智能深度列。

### 4.2 插件 API：与 Solid Edge 同构，且是其超集 [实测]

`Interop.TG.dll` 的命名空间是 `SolidEdgePart` / `SolidEdgeAssembly` / `SolidEdgeFramework` / `SolidEdgeGeometry` / `SolidEdgeDraft`。**Solid Edge 的二次开发资料可直接参考。**

**天工CAD 是 Solid Edge ST7 API 的超集**（双向核对）：

| 类型 | Solid Edge ST7 官方文档 | 天工CAD `Interop.TG.dll` 反射 |
|---|---|---|
| `SolidEdgePart.RefPoints` / `RefPoint` | 不存在（404） | **存在**（TG 扩展，带 `_TG` 后缀自动接口） |
| `SolidEdgePart.HoleGeometry` / `HoleGeometries` | 不存在 | **存在**（TG 扩展） |
| `SolidEdgePart.SectionHole` / `SectionHoles` | 不存在 | **存在**（TG 扩展） |
| `SolidEdgeDraft.HoleTable2` / `HoleTables2` | 不存在 | **存在**（TG 扩展或更新版 SE） |
| `Holes.AddHoleByCenter*` | 不存在 | **同样不存在** |

**实践含义**：写代码以 `Interop.TG.dll` 反射为准，Solid Edge 文档作语义参考；文档里没有的名字先反射再写。

**原生 UI 步骤 ↔ API 对照表：**

| 原生 UI | API |
|---|---|
| 范围=贯通 | `Holes.AddThroughAll(Profile, ProfilePlaneSide, HoleData)` |
| 范围=穿过下一个 | `Holes.AddThroughNext(Profile, ProfilePlaneSide, HoleData)` |
| 范围=起始/终止范围 | `Holes.AddFromTo(Profile, FromFaceOrRefPlane, ToFaceOrRefPlane, HoleData)` |
| 范围=有限范围 | `Holes.AddFinite(Profile, ProfilePlaneSide, FiniteDepth, HoleData)` |
| 一次多轮廓（直接建模多孔） | `Holes.AddSync(NumProfiles, ProfilesArray, ProfilePlaneSide, ExtentType, FiniteDepth, HoleData)` |
| 体选择=选择切割体 | `Holes.AddMultiBody(...)` / `AddSyncMultiBody(..., NumberOfBodies, BodyArray)` |
| 物理螺纹 | `*Ex` 重载多一个 `Boolean bPhysicalThread`（`AddFiniteEx`/`AddThroughAllEx`/`AddFromToEx`/`AddThroughNextEx`/`AddSyncEx`） |
| 装配环境孔 + 选择零件 | `AssemblyFeaturesHoles.Add(nNumScopeParts, pScopeParts, nNumProfiles, pProfiles, pExtentSide, pHoledata, ExtentType, pHoleDepth, pFromSurfOrPlane, pToSurfOrPlane, pKeyPoint, pKeyPointFlags)` |
| 孔规格（标准/规格/配合） | `HoleDataCollection.AddEx(HoleType, Standard, SubType, Size, Fit, HoleDiameter, ...)` |
| 孔参数表 | `SolidEdgeDraft.HoleTable` / `HoleTable2` |

**孔类型常量**（`FeaturePropertyConstants`）：`igRegularHole=33`、`igCounterboreHole=34`（沉孔）、`igCountersinkHole=35`（埋头）、`igCounterdrillHole=36`（柱形沉孔）、`igTappedHole=37`（螺纹）、`igTaperedHole=38`（锥孔）。

**范围常量**：`igFinite=13`、`igToNext=14`、`igFromTo=15`、`igThroughAll=16`、`igThroughAxis=21`。

**`ProfilePlaneSide`（打孔方向）**：`igLeft=1`、`igRight=2`、`igSymmetric=3`。另有 `igNone=44`。

**角度单位不一致（坑）**：`BottomAngle` / `CountersinkAngle` / `Taper` 文档明确是**度**；而 `Hole2d.Rotate` 等几何方法是**弧度**。

**`HoleData` 关键属性**：`HoleType`、`HoleDiameter`、`CounterboreDiameter/Depth`、`CountersinkDiameter/Angle`、`BottomAngle`、`ThreadNominalDiameter`、`ThreadDepth`、`ThreadSetting`、`ThreadTapDrillDiameter`、`ThreadDescription`，以及天工自加的 `TGHoleDepth`、`TGThreadPitch`、`TGHoleExtent`。

**阵列**（`Model.Patterns`）：`AddByRectangular`、`AddByCircular`、`AddBySketchDriven`、`AddByFill`、`AddAlongLine`、`AddAlongCircle`、`AddPatternByTable`、`AddByVariable`、`AddDuplicate`；另有 `Add(NumberOfFeatures, FeatureArray, Profile, PatternType)`（**沿草图点阵列**）。还有 `RecognizeAndCreatePatterns`（**自动识别阵列**）和 `Holes.RecognizeAndCreateHoleGroups`（**自动识别孔组**）。

**建模入口范式：**

```csharp
// 1) 造孔数据 —— 必须挂在 Document 上，不是 Model / Application
//    Add  = 自定义孔（直接给直径）；AddEx = 标准孔（给 Standard/Size 字符串）
var data = part.HoleDataCollection.Add(
    P.FeaturePropertyConstants.igTappedHole,   // 孔类型
    0.0065,                                    // HoleDiameter，单位【米】
    /* ...Optional: CounterboreDiameter/Depth, CountersinkDiameter/Angle, BottomAngle,
       TreatmentType, ThreadDepth, ThreadDescription... */);

// 2) 在参考面上建孔轮廓 —— 用 Holes2d.Add，不是 Circles2d！
var profile = part.ProfileSets.Add().Profiles.Add(plane);   // plane = RefPlane 或偏置参考面
profile.Holes2d.Add(0.0, 0.0);                              // 孔心（轮廓坐标，单位米）
if (profile.End(P.ProfileValidationType.igProfileClosed) != 0)
    throw new InvalidOperationException("孔轮廓未通过闭合检查。");   // 不检查会静默失败

// 3) 打孔
var hole = part.Models.Item(1).Holes.AddThroughAll(
    profile, P.FeaturePropertyConstants.igRight, data);
if (hole.Status != P.FeatureStatusConstants.igFeatureOK)
    throw new InvalidOperationException("孔特征构造失败。");
```

> **⚠ 孔轮廓必须用 `Profile.Holes2d.Add(x, y) As Hole2d`**，不是 `Circles2d.AddByCenterRadius`。依据：Siemens 官方 [Holes2d.Add 签名](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Holes2d~Add.html)、[Profile.Holes2d](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Profile~Holes2d.html)、以及 [HoleType 属性页的官方 VB 示例](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleData~HoleType.html)。`Circles2d` 画圆几何上也是闭合轮廓，但缺少"孔"语义，是否等价**未经验证**，不要赌。
>
> **⚠ 不要使用 `AddHoleByCenter*` 系列**：`AddHoleByCenter` / `AddHoleByCenterEx` / `AddHoleByCenterWithDirection*` 在 Solid Edge 官方文档与天工CAD DLL 中**都不存在**。孔只能走 `AddFinite` / `AddThroughAll` / `AddThroughNext` / `AddFromTo` / `AddSync`。
>
> **单位陷阱：** API 内部长度单位是**米**。官方示例 `HoleDiameter:=0.01` = 10 mm、`distance:=0.025` = 25 mm；现有代码 `_m` 后缀与 `1e-7` 容差也印证这点。`profile.Holes2d.Add(x, y)` 的坐标同样传米。

### 4.3 批量性能与几何校验：天工CAD 侧的对应手段 [实测]

SolidWorks 侧有两个成熟的批量打孔套路（挂起重算、射线法查壁厚）。**天工CAD 有直接对应物，不用自己发明**：

| 用途 | SolidWorks | 天工CAD（`Interop.TG.dll` 实测） |
|---|---|---|
| 批量时挂起重算 | Suspend Rebuild | `Application.DelayCompute`（get/set Boolean） |
| 挂起图形刷新 | Suspend Graphics Update | `Application.ScreenUpdating`（get/set Boolean） |
| 统一重算 | ForceRebuild3 | `PartDocument.Recompute()` |
| 射线查壁厚/是否穿透 | `IModelDocExtension.RayIntersections` | `SolidEdgeGeometry.Body.GetFacesByRay(Xorigin, Yorigin, Zorigin, Xdir, Ydir, Zdir)`；另有 `Model.Intersects` |
| 阵列逐实例抑制（跳过某些孔位） | `I*PatternFeatureData.SetFeatureScope` | `Pattern.Suppressed(Occurrence)` / `set_Suppressed`；`SetSuppressRegionProfiles` |

**三条从 SolidWorks 侧学来、但完全适用于我们的设计原则：**

1. **"先建定义对象、再建特征"两段式** —— SolidWorks 是 `CreateDefinition → 填参数 → CreateFeature`；天工CAD 天然同构：`HoleDataCollection.Add(...) → 填 HoleData → Holes.AddFinite/AddThroughAll(...)`。这种两段式天然支持"预览 → 改参数 → 再确认"，比一次性传几十个参数可维护得多。**我们的 UI 应该按这个生命周期设计。**
2. **定位与特征解耦，但坐标必须变换到模型空间** —— 这是两个平台**共同的最大坑**。SolidWorks 要 `ModelToSketchTransform.Inverse`；天工CAD 对应 `Profile.Convert2DCoordinate(x2d, y2d, out x3d, out y3d, out z3d)` 与 `Convert3DCoordinate`。**把"孔位从哪来"和"怎么建孔"分成两个独立模块，中间只传模型空间的三维点**——这样四种定位模式（参照孔/型材查表/装配贯穿/面驱动）可以共用同一个建孔器。
3. **范围（scope）是一等参数** —— 多实体和装配下"孔切哪些实体"必须显式声明，不能依赖默认全切。天工CAD 对应 `AddMultiBody` / `AddSyncMultiBody(..., NumberOfBodies, BodyArray)` 与装配侧 `pScopeParts`。

---

## 五、建议的天工CAD 自动打孔设计

### 5.1 四种"孔从哪来"的定位模式

| 模式 | 用户操作 | 孔位来源 | 依据 | 优先级 |
|---|---|---|---|---|
| **A 参照孔反推** | 选参照件的**参考孔** + 目标件的**打孔面** | 参考孔边线+直径投影到目标面，做同心约束 | ICAN 核心机制 [实测] | **最高** |
| **B 型材查表排孔** | 选型材 + 一个面 | 按型材系列的 CSV 规则表给孔径/孔深/边距 | ICAN 口哨孔数据.csv [实测] | **最高**（本项目主场景） |
| **C 装配贯穿** | 选螺栓/连接件实例 | 取轴线与面交点，一次贯穿多零件 | `AssemblyFeaturesHoles.Add` | 高 |
| **D 面驱动** | 选一个平面 | 面中心 / 包围盒四角 / 按边距等分 | — | 中 |

**A 模式是 ICAN 的护城河，也是最该抄的**：它绕开了"孔位从哪来"这个最难的问题——**让用户指定一个已知正确的孔，其余全靠投影 + 约束**。工程上极其可靠，因为参照孔本身就是设计意图的载体。

**注意 A 的固有约束**：ICAN 明确"两零件不平行则失败"，且需要打开外部参考。我们的实现要**提前检测平行性**并给出可读的失败原因，而不是让它静默失败——ICAN 在这方面被用户抱怨过。

**B 模式的正确做法是抄它的"查表"思路，而不是写几何推理**：型材打孔的知识（哪个系列用 M8、边距多少、连接件型号是什么）是**工程数据**不是**几何算法**。建议照 `口哨孔数据.csv` 的形式外置成可编辑表格。特别注意 ICAN 那个 **19 → 19.001** 的细节：**刻意加 0.001mm 破相切**，避免孔特征与型材面相切导致生成失败。这个坑我们一定会遇到。

### 5.2 孔模板（对应 ICAN「超级打孔」6 页签）

ICAN 是 6 个页签，建议先做前四个（腰孔风险高，可后置）：

- **通孔** → `igRegularHole` + `AddThroughAll`
- **螺纹孔** → `igTappedHole` + `AddFinite` + 装饰螺纹（默认），可选物理螺纹（`*Ex`）
- **圆柱沉孔** → `igCounterboreHole` + `CounterboreDiameter`/`CounterboreDepth`
- **锥形沉孔** → `igCountersinkHole` + `CountersinkDiameter`/`CountersinkAngle`
- **腰孔 / 沉头腰孔** → `Holes2d.Add` 只能做圆孔，腰孔必须用 `Lines2d` + `Arcs2d` 拼封闭轮廓。**未经验证，技术风险最高，建议后置。**

**规格反推照抄 ICAN 的查表法**：`螺纹孔底孔匹配规则.txt` 那种"直径区间 → 规格"的表，直接外置成文本，用户可编辑。**不要硬编码**。

**底部开关也值得抄**：保持关联 / 尺寸约束 / 智能选择。这三个开关本质是"生成后孔是否随参照孔联动"的控制。

### 5.3 配孔检查（建议一并做，这是差异化）

ICAN 的做法是**规则比对，不是布尔干涉**——`配孔检查配置.txt` 只有两行配置（孔径检测范围 3-25、轴向间距≤6 视为配孔）。**这个设计极其轻量，投入产出比最高。**

1. **漏打孔** —— 枚举"应有孔位"（参照件孔投影）与"实有孔"，做集合差。
2. **配错孔** —— 比 `HoleData.HoleType / ThreadNominalDiameter` 与配套紧固件规格是否一致（查 ICAN 那种规格表）。
3. **孔偏了** —— 比同轴孔对是否同心、孔心距是否等于紧固件间距（阈值可配）。

现有 `TrainingFeatures.cs` 里已有一段**可复用**的读孔代码：遍历 `profile.Holes2d`，用 `h.GetCenterPoint` + `p.Convert2DCoordinate` 拿模型坐标下的孔心。

**三维里怎么求孔心（已解决）**：不要用 `Edge.GetEndPoints`——闭合边首尾点重合，拿不到圆心。正解是先取底层几何：`Edge.Geometry` / `Face.Geometry` 转成 `SolidEdgeGeometry.Circle`，再调 `GetCircleData(CenterPoint, AxisVector, Radius)` 或 `GetCenterPoint(CenterPoint)`。这条路径**现有代码已在用**（`CadInterop.cs` 的 `PickGeometry.Point()`），是已验证写法。

**顺带纠正几个常见错名**（Solid Edge 官方文档核对）：`Face.GetBody` / `Face.GetArea` **不存在**，是**属性** `Face.Body` / `Face.Area`；`Body.GetPointData` **不存在**（用 `GetRange` / `GetExtremePoint` / `ComputePhysicalProperties`）；`Face.GetNormal` 存在但吃 **(U,V) 参数数组**而非三维点，三维点要先过 `Face.GetParamAtPoint` 转换。

### 5.4 接入方式（遵循 PROJECT.md 约定）

- 新模块 `AutoHoleModule : IToolModule`，`Id = "auto-hole"`。
- 在 `PluginFramework.cs` 的 `ModuleCatalog.Create` 登记。
- 命令 ID 从 **6** 开始（1 内嵌板、2 型材填充、3 Lineup、4 训练数据、5 格式转换）。
- 建议拆成：6「自动打孔」、7「配孔检查」、8「孔模板与规格表管理」。
- UI 沿用 `ToolContext.Show` + `CadOwner` 的单例无模式窗口范式。

### 5.5 风险与边界

- **命令条版本差异**：孔模板 / 孔类型 / 孔规格 / V 型孔底只在"启用新版孔命令"勾选后可用。插件不应依赖 UI 状态，直接走 API。
- **`Sketch` 不是孔的输入**：`PartDocument.Sketches` / `Sketches3D` 是 **3D 草图**。孔只吃 `Profile`，且必须由 `ProfileSets.Add().Profiles.Add(RefPlane)` 这条路建。
- **`HoleData` 要新建、不要复用**：官方示例每个孔都单独调 `HoleDataCollection.Add`。复用请用 `HoleDataCollection.Copy(src)`。直接复用同一实例是否报错**未验证**。
- **`Standard` / `Size` 是字符串不是枚举**：合法值来自 CAD 自带孔数据库，**不要硬编码**。稳妥做法是运行时遍历 `doc.HoleDataCollection` 反查可用值。
- **工程图孔表 API 只能读不能建**：`HoleTables` 集合只有 `Item`/`Count`/`Parent`，**没有 Add**；`HoleTable` 只有 `Delete`/`Update`。孔表大概率只能交互式建好再由插件读写。若"自动出图含孔表"是需求，这块要单独验证。
- **相切破面**：孔与型材面相切会导致特征生成失败——ICAN 用 +0.001mm 规避。我们必须做同样的防御。
- **`AddSync` 是同步建模孔**，与传统顺序建模孔混用会出问题，不要混。
- **腰孔**需自行拼轮廓，无现成 API，风险最高。
- **未做实测验证**：本报告只做 API 反射与文档核对，**没有在真实 CAD 里跑通一次 `AddThroughAll`**。

---

## 六、下一步建议

1. **先做 spike**（前提）：新建 PAR，在参考面上 `Holes2d.Add` 一个点，调 `Models.Item(1).Holes.AddThroughAll` 打穿，验证 API 通路、`Holes2d` vs `Circles2d`、单位制。
2. **确定范围**：本项目场景下，**B 型材查表排孔 + 配孔检查**是最短闭环，也最贴近 ICAN 的机架设计模块。
3. **单独调研 ICAN 机架设计模块**：它的 8 项功能（快速绘制机架3D草图 / 自动生成机架模型 / 自动装配角码 / 自动开口哨孔）与本项目重合度极高，比"超级打孔"更值得抄。
4. **规格表先外置**：不管做哪个模式，先把"直径区间→规格"和"型材系列→孔参数"两张表做成可编辑文本，这是 ICAN 验证过的做法。

---

## 七、来源

**凯元工具箱**
- [官方更新日志（含 v4.69 自动打孔上线记录）](http://kytool.cn/upload/soft.html)
- [凯元工具简介 2024 官方 PDF（46 项功能全表，早于自动打孔上线）](http://www.kytool.cn/helpV5/01_%e4%b8%8b%e8%bd%bd-%e5%ae%89%e8%a3%85-%e8%ae%be%e7%bd%ae-%e6%bf%80%e6%b4%bb-%e7%8b%97/105.001_%e5%87%af%e5%85%832024%e5%8a%9f%e8%83%bd%e7%ae%80%e4%bb%8b.pdf)
- [ICT 插件页功能分类](https://www.ict.com.cn/chajian/5453.html)
- [实测] 官方安装包 KYTool4.76 解包：`datas/IMG/ID.cfg`、`datas/IMG/listaddin.kdb`、`help/IDHelp.cfg`

**嘉立创 ICAN 工具箱**
- [ICAN 帮助中心·功能区1（5 个打孔功能）](https://www.jlc-jdgf.com/icanhelp/JDGF0000010103/57461)
- [超级打孔相关问题（参照孔机制与限制的铁证）](https://www.jlc-jdgf.com/icanhelp/JDGF0000010107/56125)
- [配孔检查](https://www.jlc-jdgf.com/icanhelp/JDGF0000010104/55626) · [合并同孔](https://www.jlc-jdgf.com/icanhelp/JDGF0000010104/60176) · [第三方零件孔转 Sw 孔特征](https://www.jlc-jdgf.com/icanhelp/JDGF0000010106/59084)
- [更新日志（含"实际打孔直径"破相切说明）](https://www.jlc-jdgf.com/icanhelp/JDGF00000108/56113)
- [嘉立创官方收购公告（含用户原话与 90% 错误率数据）](https://m.jlc.com/portal/q7i54281.html)
- [ICAN 官网](https://ican.jlc.com/)
- [实测] 官方安装包 V10.0.5 内的 `用户默认设置/` 规则文件（螺纹孔底孔匹配规则.txt、沉孔匹配规格.txt、超级打孔孔默认标准与类型配置.txt、配孔检查配置.txt、口哨孔数据.csv）

**天工CAD 官方帮助**
- [孔命令条（顺序建模）](https://helptgy.tiangong.cloud/helptgy_CN/zh_CN/design/feat10d.html) · [孔命令条（装配环境）](https://helptgy.tiangong.cloud/helptgy_CN/zh_CN/design/asmhole1d.html) · [构造孔（直接建模）](https://helptgy.tiangong.cloud/helptgy_CN/zh_CN/design/hole1h.html) · [创建孔参数表](https://helptgy.tiangong.cloud/helptgy_CN/zh_CN/manufacture/holtbl1h.html)

**Solid Edge 官方 API 文档（语义参考，ST7/107）**
- [Holes 集合成员](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Holes_members.html) · [HoleData 成员](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleData_members.html) · [FeaturePropertyConstants](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~FeaturePropertyConstants.html)
- [Holes2d.Add](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Holes2d~Add.html) · [Profile.Holes2d](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Profile~Holes2d.html) · [Holes.AddFinite](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Holes~AddFinite.html)
- [AssemblyFeaturesHoles.Add](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgeAssembly~AssemblyFeaturesHoles~Add.html)

**本机实测**
- `Interop.TG.dll` 反射：`DEV_0.4_插件框架/build/release-20260911-200150/Interop.TG.dll`（4004 个导出类型）

**配套深度报告**
- [凯元工具箱_自动打孔_技术调研报告.md](凯元工具箱_自动打孔_技术调研报告.md)
- [ICAN自动打孔_技术调研报告.md](ICAN自动打孔_技术调研报告.md) · [ICAN调研证据](ICAN调研证据)（官方规则表 + CHM 截图）
- [SolidEdge自动打孔API技术报告.md](SolidEdge自动打孔API技术报告.md)
- SolidWorks 侧 API 调研（**仅作设计模式参考**，因天工CAD 不走 SolidWorks 体系）：HoleWizard5 / CreateDefinition / IWizardHoleFeatureData2 / RayIntersections / IComponent2.Transform2；[codestack suspend-rebuild](https://github.com/xarial/codestack/blob/master/solidworks-api/document/suspend-rebuild/index.md)、[suspend-graphics-update](https://github.com/xarial/codestack/blob/master/solidworks-api/document/suspend-graphics-update/index.md)

**四个子调研的交叉核对结论**：三家厂商的自动打孔在**架构上高度一致**——都是"孔位来源"与"孔特征生成"解耦，都是"先定参数、再建特征"两段式，都把"范围/关联"作为显式开关。差异只在孔位来源（参照孔投影 / 查表 / 草图点 / 标准件轴线）和工程数据的存放方式（外置规则表 vs 内嵌数据库）。**这印证了第 5.1 节四种定位模式 + 统一建孔器的设计方向。**
