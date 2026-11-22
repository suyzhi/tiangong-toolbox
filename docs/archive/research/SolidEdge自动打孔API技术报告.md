# Solid Edge API「自动打孔」实现技术报告

**核查基准**：Siemens 官方 Solid Edge API Help（ST7，docs 版本 107）镜像
`https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/`
**核查方法**：对每个 API 名逐一探测文档页——不存在的主题会返回 "File Error: GTAC" 页（对照组 `SolidEdgePart~Holes~AddFinite.html` 正常返回）。下文**未标注「需核实」的名字均已实测存在**。

> 注：新版文档站 `docs.plm.automation.siemens.com/docs/se/2020/api/` 证书过期（ERR_CERT_DATE_INVALID），本次无法访问；ST8 之后是否有新增 API **需核实**。

---

## 0. 首要结论：`AddHoleByCenter` 系列在 Solid Edge API 中不存在

`AddHoleByCenter`、`AddHoleByCenterEx`、`AddHoleByCenterWithDirection`、`AddHoleByCenterWithDirectionEx`、`AddHoleByCenterWithDirectionAndDepth` —— 全部**实测不存在**。请勿按这些名字编码。

同样实测不存在：`seExtentTypeConstants`（终止条件用 `FeaturePropertyConstants`）、`SolidEdgePart.RefPoints`、`SolidEdgePart.HoleGeometry/HoleGeometries`、`SolidEdgePart.SectionHole`、`SolidEdgeDraft.HoleTable2`、`Sketchs.AddByPlaneOrientation`。这些要么是天工CAD扩展，要么命名不同 → **需核实**。

---

## 1. 创建孔的 API 入口

### 1.1 `SolidEdgePart.Holes`（= `Model.Holes`）的全部创建方法
来源：[Holes Collection Members](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Holes_members.html)

```vb
AddFinite(Profile As Profile, ProfilePlaneSide As FeaturePropertyConstants,
          FiniteDepth As Double, Data As HoleData) As Hole
AddThroughAll(Profile As Profile, ProfilePlaneSide As FeaturePropertyConstants,
          Data As HoleData) As Hole
AddThroughNext(Profile As Profile, ProfilePlaneSide As FeaturePropertyConstants,
          Data As HoleData) As Hole
AddFromTo(Profile As Profile, FromFaceOrRefPlane As Object,
          ToFaceOrRefPlane As Object, Data As HoleData) As Hole
AddSync(NumberOfProfiles As Long, ProfilesArray As Variant,
        ProfilePlaneSide, ExtentType, FiniteDepth As Variant, Data As HoleData) As Hole
```

### 1.2 HoleDataCollection 的获取方式（易错点）
**挂在 Document 上，不在 Model，也不在 Application**：
- `SolidEdgePart.PartDocument.HoleDataCollection` — [来源](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~PartDocument~HoleDataCollection.html)
- `SolidEdgeAssembly.AssemblyDocument.HoleDataCollection` — [来源](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgeAssembly~AssemblyDocument_members.html)

```vb
Add(HoleType As FeaturePropertyConstants, HoleDiameter As Double,
    Optional CounterboreDiameter, CounterboreDepth, CountersinkDiameter, CountersinkAngle,
    BottomAngle, TreatmentType, TaperMethod, Taper, ThreadMinorDiameter, ThreadDepthMethod,
    ThreadDepth, VBottomDimType, TaperDimType, CounterboreProfileLocationType, TaperLValue,
    TaperRValue, ThreadExternalDiameter, ThreadDescription, IgnoreSavedDefaultValues) As HoleData
AddEx(HoleType As FeaturePropertyConstants, Optional Standard, SubType, Size, Fit, ...) As HoleData
Copy(pSrcHoleData As HoleData) As HoleData
```
[Add](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleDataCollection~Add.html) ·
[AddEx](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleDataCollection~AddEx.html) ·
[Copy](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleDataCollection~Copy.html)

**用 `Add` 建自定义孔，用 `AddEx` 建标准孔**——只有 `AddEx` 才有 `Standard`/`SubType`/`Size`/`Fit` 参数。

### 1.3 HoleData 关键属性
[HoleData Members](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleData_members.html)

| 属性 | 类型 | 说明 |
|---|---|---|
| `HoleType` | FeaturePropertyConstants | 孔型 |
| `Standard` / `Size` / `SubType` | **String** | 标准库名 / 规格 / 子类型 |
| `HoleDiameter` `CounterboreDiameter` `CounterboreDepth` `CountersinkDiameter` `CountersinkAngle` `BottomAngle` | Double | 尺寸 |
| `TreatmentType` | FeaturePropertyConstants | 装饰/攻丝处理 |
| `ThreadDepth` `ThreadDepthMethod` `ThreadDescription` `ThreadNominalDiameter` `ThreadTapDrillDiameter` | — | 螺纹 |
| `Fit` `Taper` `TaperMethod` `Transfer` `Delete` | — | 其他 |

⚠️ **没有 `Threaded` 布尔属性**。"是否装饰螺纹"由 `TreatmentType = igTappedHole` + `ThreadDepth`/`ThreadDepthMethod`/`ThreadDescription` 表达。
⚠️ `Standard`/`Size` 是**字符串**，合法取值来自 Solid Edge 自带孔数据库（不是枚举）。**需核实**：确切字面量（"ISO"/"ANSI Metric"/"DIN"…、"M3"/"M6"…）。**稳妥做法**：运行时遍历 `doc.HoleDataCollection` 读 `Name`/`Standard`/`Size` 反查可用值，不要硬编码。

### 1.4 常量（全部来自 `FeaturePropertyConstants`）
[常量表](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~FeaturePropertyConstants.html)

| 用途 | 值 |
|---|---|
| HoleType | `igRegularHole`=33（简单孔）、`igCounterboreHole`=34（柱形沉孔）、`igCountersinkHole`=35（锥形沉孔）、`igCounterdrillHole`=36、`igTappedHole`=37（螺纹孔）、`igTaperedHole`=38、`igNone`=44 |
| 终止条件 ExtentType | `igFinite`=13、`igToNext`=14（=Through Next）、`igFromTo`=15、`igThroughAll`=16、`igThroughAxis`=21 |
| ProfilePlaneSide | `igLeft`=1、`igRight`=2、`igSymmetric`=3 |
| TreatmentType | `igNone`=44、`igTappedHole`=37、`igTaperedHole`=38 |
| 螺纹类型 | `igStraightPipeThread`=165、`igTaperedPipeThread`=166 |
| TaperMethod | `igTaperByAngle`=45、`igTaperByRatio`=46 |
| CounterboreProfileLocationType | `igCounterboreProfileIsAtTop`=149 / `AtBottom`=150 |
| `KeyPointExtentConstants` | `igTangentNormal`=1、`igReverseTangentNormal`=2、`igInteriorTangentNormal`=3、`igInteriorReverseTangentNormal`=4 |

---

## 2. 定位输入与调用顺序（官方示例为准）

**`AddFinite` 要的是 `Profile`，不是 `Sketch`，也不是 `Face`/`Point`。** 方向由 `ProfilePlaneSide` 决定，深度由 `FiniteDepth` 决定。

官方 VB 示例（[HoleType 属性页](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleData~HoleType.html)）的关键三步：

```vb
' 1) 建基体
Set objBaseProfile = objDoc.ProfileSets.Add.Profiles.Add(pRefPlaneDisp:=objDoc.RefPlanes(1))
Call objBaseProfile.Circles2d.AddByCenterRadius(x:=0, y:=0, Radius:=0.1)
lngStatus = objBaseProfile.End(ValidationCriteria:=igProfileClosed)
Set objBase = objDoc.Models.AddFiniteExtrudedProtrusion(1, objBaseProfArray, igSymmetric, 0.05)

' 2) 建 HoleData（自定义孔）
Set objRegHoleData = objDoc.HoleDataCollection.Add(HoleType:=igRegularHole, _
                                                   HoleDiameter:=0.01, BottomAngle:=90)

' 3) 建孔轮廓：偏置参考面 + Holes2d.Add（不是 Circles2d！）
Set objRP = objDoc.RefPlanes.AddParallelByDistance(parentplane:=objDoc.RefPlanes(1), _
                                                   distance:=0.025, normalside:=igRight)
Set objRegHoleProfile = objDoc.ProfileSets.Add.Profiles.Add(pRefPlaneDisp:=objRP)
Call objRegHoleProfile.Holes2d.Add(xcenter:=0, ycenter:=0)
lngStatus = objRegHoleProfile.End(ValidationCriteria:=igProfileClosed)

Set objRegHole = objBase.Holes.AddFinite(Profile:=objRegHoleProfile, _
                                         ProfilePlaneSide:=igLeft, FiniteDepth:=0.02, Data:=objRegHoleData)
If (objRegHole.Status <> igFeatureOK) Then MsgBox "AddFinite fails"
```

**三个必须照抄的细节**：
1. 孔轮廓用 **`Profile.Holes2d.Add(xCenter, yCenter) As Hole2d`**（专用孔元素），[签名](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Holes2d~Add.html)。用 `Circles2d` 画圆虽然几何上也是闭合轮廓，但缺少孔的语义，**需核实**是否等价。
2. 必须 `Profile.End(ValidationCriteria:=igProfileClosed)` 并检查返回值。
3. 孔轮廓通常放在**偏置参考面**上（`AddParallelByDistance`），再用 `ProfilePlaneSide` 指向材料侧。

**关于 `Sketch`**：`PartDocument.Sketches`（返回类型 `Sketchs`）与 `PartDocument.Sketches3D` 是**3D 草图**，用于扫掠/3D 草图特征，**不是孔 Profile 的输入**。`Sketchs` 实测方法只有 `Add()`、`AddByPlane(pPlane)`、`AddByPlanarFace`、`AddByPlaneGeometry`、`AddByMirror`、`AddByTearOff`。

C# 骨架：
```csharp
PartDocument doc = (PartDocument)app.ActiveDocument;
Model model = doc.Models[1];

// 1) 孔数据
HoleData hd = doc.HoleDataCollection.AddEx(
    HoleType: FeaturePropertyConstants.igCounterboreHole,
    /* Standard/Size/SubType 视孔库而定，见 1.3 */ );

// 2) 定位轮廓
RefPlane rp = doc.RefPlanes.AddParallelByDistance(doc.RefPlanes[1], 0.025, igRight);
Profile prof = doc.ProfileSets.Add.Profiles.Add(rp);
prof.Holes2d.Add(0.0, 0.0);
prof.End(ValidationCriteria: igProfileClosed);

// 3) 打孔
Hole hole = model.Holes.AddFinite(prof, igLeft, 0.02, hd);
if (hole.Status != FeatureStatusConstants.igFeatureOK) throw new Exception("hole failed");
```

---

## 3. 装配体打孔

- `AssemblyDocument.AssemblyFeatures` → `AssemblyFeatures.AssemblyFeaturesHoles`
- `AssemblyDocument.AssemblyDrivenPartFeatures` → `AssemblyDrivenPartFeatures.AssemblyDrivenPartFeaturesHoles`

**两者都只有一个 `Add`，签名完全相同**（[AssemblyFeaturesHoles.Add](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgeAssembly~AssemblyFeaturesHoles~Add.html)）：

```vb
Add(nNumScopeParts As ULong, pScopeParts() As Unknown,
    nNumProfiles As ULong, pProfiles() As Unknown,
    pExtentSide As FeaturePropertyConstants, pHoledata As Unknown,
    ExtentType As FeaturePropertyConstants, pHoleDepth As Double,
    pFromSurfOrPlane, pToSurfOrPlane, pKeyPoint,
    pKeyPointFlags As KeyPointExtentConstants) As AssemblyFeaturesHole
```

**"影响哪些零件"= `nNumScopeParts` + `pScopeParts`（零件数组）**。一次同轴通孔 = 一个 profile + 多个 scope parts + `ExtentType = igThroughAll`。

**区别**（**需核实**措辞）：`AssemblyFeatures` 建的是**装配特征**（孔定义在装配层，不改零件模型）；`AssemblyDrivenPartFeatures` 建的是**装配驱动的零件特征**（孔下推到各零件，在零件里生成特征）。HoleData 从 `assemblyDoc.HoleDataCollection` 取。社区有同名讨论帖（[Parts selected for Assembly feature holes in the API](https://community.sw.siemens.com/s/question/0D54O000061xsn7SAA/parts-selected-for-assembly-feature-holes-in-the-api)，页面 JS 渲染，本次未能取正文）。

---

## 4. 阵列

`Model.Patterns`（[成员](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Patterns_members.html)）：

```vb
' 沿草图点阵列 —— 孔沿草图阵列就用这个
Add(NumberOfFeatures As Long, FeatureArray() As Object, Profile As Profile,
    PatternType As PatternTypeConstants) As Pattern
' 矩形阵列
AddByRectangular(NumberOfFeatures, FeatureArray(), ReferencePlane As Object,
    XDirectionCount, YDirectionCount, XDirectionSpacing, YDirectionSpacing,
    RectangleAngle, PatternMethod As PatternOffsetTypeConstants, ReferenceIndex) As Pattern
' 圆形阵列
AddByCircular(NumberOfFeatures, FeatureArray(), ReferencePlane As Object,
    RadialCount, AngleSpacing, AxisPoint() As Double,
    PatternMethod As PatternOffsetTypeConstants,
    CurveDirection As PatternCurveAnchorSideConstants, ArcPattern As Boolean) As Pattern
```
另有 `AddByCurve`、`AddByFill`、`AddSync`、`RecognizeAndCreatePatterns`。装配侧用 `AssemblyFeatures.AssemblyFeaturesPatterns.Add`。

---

## 5. 参考几何与分度圆

- `PartDocument.RefPlanes` → `RefPlanes`：`AddParallelByDistance`、`AddBy3Points`、`AddAngularByAngle`、`AddNormalToCurve`/`AtDistance`/`AtKeyPoint`、`AddParallelByTangent`、`AddTangentToCylinderOrConeAtAngle`/`AtKeyPoint`、`AddTangentToCurvedSurfaceAtKeyPoint`
- `PartDocument.CoordinateSystems` → `CoordinateSystems`：`Add`、`AddByGeometry`、`AddByMatrix`、`AddRelativeToCoordinateSystem`
- `PartDocument.Sketches3D` → `Sketch3D`：`IncludeEdge`、`Lines3D`、`Arcs3D`、`BSplineCurves3D`、`Ellipses3D`、`Sketch3DRelations`、`EnableRegions`
- **`RefPoints` 在 SolidEdgePart 中不存在**（`SolidEdgePart~RefPoints.html` 与 `RefPoint_members.html` 均 404）→ **需核实**
- **`BoltHoleCircle` 属于 `SolidEdgeFrameworkSupport`（2D/Draft 层），不是 3D 特征**。`BoltHoleCircles` 集合**没有 `Add`**，只有：`AddBoltHoleCircleBy2Points`、`AddBoltHoleCircleBy3Points`、`AddBoltHoleCircleByCenterAndRadius(CenterObject, CenterObject_KeyPointIndex, RadiusObject, RadiusObject_KeypointIndex)`（[来源](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgeFrameworkSupport~BoltHoleCircles_members.html)）。`BoltHoleCircle` 对象有 `AddMember`/`RemoveMember`/`Circle2d`/`GetConnectElementsCenterRadius`/`SetTrimArcEndPoints`。分度圆是**画在草图里的 2D 构造**，用来定位孔心。

---

## 6. 几何查询（用户列的名字有 3 处错误）

| 你以为的 | 实际 | 签名/说明 |
|---|---|---|
| `Face.GetBody` | ❌ 不存在，是**属性** `Face.Body` | |
| `Face.GetArea` | ❌ 不存在，是**属性** `Face.Area` | |
| `Face.GetNormal` | ✅ 存在，但吃 **(U,V) 参数数组**，不是 3D 点 | `GetNormal(NumParams As Long, Params() As Double, Normals() As Double)` |
| 3D 点 → 参数 | `Face.GetParamAtPoint(NumPoints, Points(), GuessParams(), MaxDeviations(), Params(), Flags())` | 先转换再求法向 |
| `Face.GetPointAtParam` | ✅ | `(NumParams, Params(), Points())` |
| `Edge.GetEndPoints` | ✅ | `(StartPoint() As Double, EndPoint() As Double)` |
| `Vertex.GetPointData` | ✅ | `(Point() As Double)` |
| `Body.GetPointData` | ❌ 不存在 | Body 有 `GetExtremePoint`、`GetFaceByFaceID`、`GetEntityByID`、`CreateCollection`、`ComputePhysicalProperties`、`GetRange`、`GetFacetData` |

其他可用：`Edge.GetFaces()`、`Edge.GetTangent`、`Edge.IsClosed`、`Edge.EndVertex`、`Face.GetRange`、`Face.GetParamRange`、`Face.GetCurvatures`、`Face.Geometry`、`Face.GeometryForm`、`Face.Edges`、`Vertex.Faces`/`Edges`。持久引用用 `GetReferenceKey()`。

**求孔圆心**：取圆柱面的边 → `Edge.IsClosed` 判圆 → `Edge.GetEndPoints`（闭合边首尾重合，不能直接当圆心）。**需核实**：正解应是通过 `Edge.Geometry`/`Face.Geometry` 取底层曲线/曲面对象再读圆心半径；本次未验证该几何对象的成员。

---

## 7. 工程图孔表

- `SolidEdgeDraft.HoleTable` 存在：方法 `Delete`、`Update`；属性 `Holes`（`HTHoles`）、`SavedSettings`（**只写**）、`Parent`
- **`HoleTables` 集合只有 `Item`/`Count`/`Parent`，文档中没有任何 `Add` 创建方法** → 孔表如何在 API 中创建 **需核实**（可能只能交互/命令创建后读取更新）
- **`HoleTable2` 在 ST7 API 中不存在** → **需核实**
- `HTHoles` 集合也只有 `Item`/`Count`/`Parent`（只读遍历）
- 图纸上的分度圆：`SolidEdgeDraft.Sheet.BoltHoleCircles`

---

## 8. 常见坑

1. **单位是米！** 官方示例 `HoleDiameter:=0.01` = 10 mm，`ExtrusionDistance:=0.05` = 50 mm，`distance:=0.025` = 25 mm。传 mm 会得到小 1000 倍的模型。
2. **角度单位不一致**：`BottomAngle`/`Taper`/`CountersinkAngle` 文档明确是**度**；而 `Hole2d.Rotate` 等几何方法是**弧度**。
3. **HoleData 必须"新建"而不是复用**：官方示例每个孔都单独调 `HoleDataCollection.Add(...)` 生成独立 HoleData。要把已有孔数据复制给新孔，用 `HoleDataCollection.Copy(srcHoleData)`。**需核实**：直接复用同一个 HoleData 实例给多个孔是否报错。
4. **`Profile.End(igProfileClosed)` 必须调用并检查返回值**，否则 `AddFinite` 静默失败。
5. **每次都要检查 `feature.Status == igFeatureOK`**，或用 `GetStatusEx`。
6. **COM 释放**：`Marshal.ReleaseComObject` 放在 `finally`，按创建的逆序释放；不要用 `GC.Collect` 循环硬清。Solid Edge 官方 VB.NET 示例在入口用 `OleMessageFilter.Register()` / `Revoke()`（**需核实**其确切命名空间，示例中未加限定名）来抑制 "被调用方拒绝呼叫" 的 RPC 忙错误。
7. **选择集**：`Application.ActiveSelectSet` / `Document.SelectSet`，用 `Add(obj)`、`RemoveAll()`。`Profile.ChainLocate` 会**清空并重填** SelectSet。
8. **重算后失效**：`Model.Recompute()` 后特征会被重建，缓存的 `Face`/`Edge`/`Hole` COM 引用可能变悬空。重新用 `Model.Holes[i]` 或 `EdgebarName`/`Name` 取；跨重算的拓扑引用用 `GetReferenceKey()`。
9. **`AddSync` 是同步建模孔**（ST 环境），与传统顺序建模孔混用会出问题；`RecognizeAndCreateHoleGroups` 是识别已有几何自动成孔（同步）。
10. **孔的修改**：`Model.ResizeHoles.Add(NumOfHoleFaces, FaceArray(), dDiameter, ExtTypeValue, ThreadHoleData)`；`Model.DeleteHoles.Add(HoleTypeToDelete As HoleTypeToDeleteConstants, ThresholdHoleDiameter)` / `AddByFace`（`seHoleTypeToDeleteCylindersAndConesOnly`=1、`seHoleTypeToDeleteAll`=2）。
11. **`Application.Documents.Open(path)`** 打开文档后要判 `Documents.Count` 与文档类型再转型，SE 对类型不匹配的强制转换直接抛 COM 异常。

---

## 对天工CAD插件的行动建议

1. **不要用 `AddHoleByCenter` 系列编码**——先在天工CAD的 `Interop.TG.dll` 上用反射确认这些方法是否真的存在（`typeof(Holes).GetMethods()`），存在则以其签名为准，不存在则改用 `AddFinite`/`AddThroughAll`/`AddThroughNext`/`AddFromTo` 这一套。
2. **先反射确认 5 个存疑类型**：`SectionHole`、`HoleGeometry/HoleGeometries`、`RefPoints`、`HoleTable2`——它们不在 SE API 里。
3. `HoleDataCollection` 从 **Document** 取，不是 Model/Application。
4. 打孔最小可行路径：`ProfileSets.Add.Profiles.Add(RefPlane)` → `Profile.Holes2d.Add` → `Profile.End` → `Model.Holes.AddFinite`。

---

### 主要来源
- Solid Edge API Help (ST7/107) 镜像：https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/
  - [Holes](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Holes_members.html) · [HoleData](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleData_members.html) · [HoleDataCollection](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~HoleDataCollection_members.html) · [FeaturePropertyConstants](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~FeaturePropertyConstants.html) · [Patterns](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgePart~Patterns_members.html)
- [AssemblyFeaturesHoles.Add](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgeAssembly~AssemblyFeaturesHoles~Add.html) · [AssemblyDrivenPartFeaturesHoles.Add](https://support.industrysoftware.automation.siemens.com/trainings/se/107/api/SolidEdgeAssembly~AssemblyDrivenPartFeaturesHoles~Add.html)
- [Solid Edge 社区：装配特征孔选零件](https://community.sw.siemens.com/s/question/0D54O000061xsn7SAA/parts-selected-for-assembly-feature-holes-in-the-api) · [如何添加螺纹孔](https://community.sw.siemens.com/s/question/0D54O00006neK19SAE/how-to-add-threaded-hole)
