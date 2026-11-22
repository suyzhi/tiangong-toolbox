# 自动打孔系列 验证记录

日期：2026-09-24　环境：Windows x64，天工CAD 2025 高级版 **225.03.00.165**。
构建：`DEV_0.4_插件框架/build/autohole-2/TianGongCadSuite.dll`

## 一句话结论

三个命令（6 自动打孔 / 7 批量排孔 / 8 配孔检查）**全部跑通**，所有切除体积与理论值**精确吻合**，
参照件未被改动，插件加载报 `Registered 8 commands`。
**尚未做真实鼠标点击的可视化验收**——原生测试直接调用了真实 CAD 接口与真实选择引用，但没有用鼠标走完整条交互链。

## 验证总览

| 测试 | 命令 | 结果 |
|---|---|---|
| 纯逻辑 | `PanelTests.exe --autohole-pure` | **65 条断言全过** |
| 命令 6 原生 | `PanelTests.exe --autohole <dir>` | **17 条断言全过** |
| 命令 7+8 原生 | `PanelTests.exe --autohole-pattern <dir>` | **18 条断言全过** |
| 命令注册 | `PanelTests.exe --commands` | `COMMAND COUNT 8` |
| 窗口生命周期 | `PanelTests.exe --autohole-form <dir>` | **7 条断言全过**（见下） |
| 插件加载 | `tools/verify-autohole.ps1` | `MENU Registered 8 commands`，6→62019 / 7→62020 / 8→62021 |
| 命令路由回归 | `tools/command-routing-native-test.ps1` | `EXIT 0`，1/2/3 号命令未受影响 |

## 命令 7 / 8 的原生实测数据（关键体积全部精确吻合）

```
AUTOHOLE started a new CAD instance
PASS: 自动取到 200mm 长边，实读 200
PASS: 方向沿 X
PASS: 打出 4 个孔（失败 0：）
PASS: 4 个 Φ6 通孔体积吻合（实 1131 / 期 1131 mm³）
PASS: 采集到 4 个孔（通孔上下两条圆边已去重），实得 4
PASS: 采集到的直径都是 6mm
PASS: 4 个孔分属 4 条独立轴线，实得 4
PASS: 同零件上的 4 个独立孔不报问题，实得 0
PASS: 要求报孤孔时得 4 条，实得 4
PASS: 圆周均布打出 6 个孔（失败 0）
PASS: 圆周 6 孔体积吻合（实 1696.5 / 期 1696.5 mm³）
PASS: 腰孔通切成功
PASS: 腰孔体积吻合（实 2262.7 / 期 2262.7 mm³）
AUTO-HOLE PATTERN ASSERTIONS 18
```

## 实机反馈抓出来的 bug 5：窗口"闪一下就没了"

**现象**：用户点「自动打孔」，窗口出现一瞬间就自己关了。

**根因**：`AutoHoleForm` 和 `AutoHolePatternForm` 都在 `Shown` 事件里调了一次 `StartPicking()`，
而宿主 `ToolContext.Show` 的启动回调**也会调一次**：

```csharp
public void Show(Func<Form> create, Action<Form> start){
    Dispose(); var next = create(); active = next;
    next.Show(owner);      // <-- 触发 Shown -> StartPicking() #1
    if (start != null) start(next);   // <-- StartPicking() #2
}
```

第二次 `command.Start()` 会结束第一条命令，CAD 回调 `Terminate()`，而 `Terminate` 的实现是
`BeginInvoke(() => Close())` —— 于是窗口把自己关掉了。日志里没有任何异常，正好对应"闪一下"。

**对照**：既有的 `PanelForm` **没有** `Shown` 处理器，`StartPicking` 只由启动回调调一次。这是照抄时漏掉的一点。

**修复**：两处都删掉 `Shown` 里的调用；同时把 `StartPicking` 改成幂等（进来先 `StopCommand()`），
以后就算被重复调用也不会留下第二条命令。

**回归测试**：`PanelTests.exe --autohole-form` 会 `Show()` 窗口、连调两次 `StartPicking`、泵消息，
断言窗口仍然 `Visible` 且未 `IsDisposed`。三个窗口都测。

```
PASS: 自动打孔窗口显示后仍然可见
PASS: 自动打孔 StartPicking 一次后窗口仍在
PASS: 自动打孔 StartPicking 重复调用后窗口仍在（幂等）
PASS: 自动打孔窗口能正常关闭
PASS: 批量排孔窗口显示后仍然可见
PASS: 批量排孔 StartPicking 重复调用后窗口仍在（幂等）
PASS: 配孔检查窗口显示后仍然可见
AUTO-HOLE FORM ASSERTIONS 7
```

## 实机反馈抓出来的 bug 6：选择会"透过"当前视图

**现象**：在第一层零件上点孔，选到的却是后面零件的边线。

**根因**：`StartPicking` 里只设了 `InterDocumentLocate = true`（允许跨零件选），
**没有设 `ISEMouseEx2.LocateFrontToBack`** —— 后者才是"按前后遮挡优先"的开关。

**修复**：两个选择窗口都补上 `((ISEMouseEx2)mouse).LocateFrontToBack = true`。

**注意**：这是鼠标行为，**自动化测试覆盖不到**，必须人工点一下确认。

## 实机反馈带来的功能补强：孔参数可手动调

原来的「高级」只有一个"强制孔型"下拉，用户反馈"打出来的都是工程师不想要的孔"。
现在改成完整的参数编辑区：孔型 / 规格 / 孔径 / 深度（贯通或指定）/ 沉孔Φ / 沉孔深 / 锥孔Φ / 锥角 / 螺纹深。
改动同步到该直径组的全部孔，不动就用自动推断值。

同时 CAD 层补上了**盲孔**支持：`HoleSpec.Depth > 0` 时走 `Holes.AddFinite`，否则走 `AddThroughAll`。
实测 Φ10 深 5mm 盲孔切除体积 **392.7 / 392.7 mm³**。

## 实现过程中被测试抓出来的 4 个真 bug

这些都不是"测试写错了"，是产品代码的缺陷：

1. **基准面每个孔建一次** → 同一张面第 2 个孔起 E_FAIL。改成"一批孔共用一个基准面"。
2. **跨批次再建基准面也 E_FAIL** → 改成 `FindOrCreatePlane`：先找已存在的共面基准面，找不到才新建。
3. **打完孔后缓存的面对象失效** → 再对同一个面打孔 E_FAIL。改成每次打孔前重新解析选择。
4. **排孔起点取的是面的角点** → 所有孔压在面的边线上、一半在材料外面。改成"方向起点 + 面宽正中"。

另外两个是测试夹具自己的坑（已在文档里记下）：
- `RefPlanes.Item(3)` 不是 XY 基准面，按序号猜会把孔打到侧面。
- 平面的 root point 可以落在平面上任意位置，**只按 z 匹配会误中侧面**（实测 x=0 的侧面 root point 的 z 正好是 +0.005，面积只有 1000mm²）。

## 腰孔的两个关键细节

1. **相邻元素必须加 keypoint 重合关系**，否则 `Profile.End(igProfileClosed)` 返回 `-103`（不闭合）。
2. **圆弧必须用 `Arcs2d.AddByStartAlongEnd` 并显式给出"弧上一点"**。用 `AddByCenterStartEnd` 会让圆弧朝内鼓，腰孔变成内凹的狗骨形——实测面积正好是 `2rL - πr²`（差一个整圆）。改用 Along 点后体积比 **1.00000000000001**。

## 一、API 探测（tools/HoleSpike.cs，6 轮）

每一轮都在真实 CAD 里跑，不是查文档推的。

| 轮次 | 探测内容 | 结论 |
|---|---|---|
| 1 | `Holes2d` + `AddThroughAll` / `AddFinite` | **通路成立**。单位确认为**米**（100×100×10mm 板 range = 0,0,-0.005 → 0.1,0.1,0.005） |
| 1 | `Hole.Status` 属性 | C# 不可直接读，须 `GetStatusEx(out desc)`；`igFeatureOK = 1216476310`（**不是 0**） |
| 1 | `Circles2d` 也能通过闭合检查并打出孔 | 但官方示例用 `Holes2d`，产品代码用 `Holes2d` |
| 2 | 面驱动：`RefPlanes.AddBy3Points` | **失败**，三种参数组合全部 `DISP_E_TYPEMISMATCH` |
| 2 | 体积校验发现切除量正好是理论值的 **1/2** | 轮廓面在板中面时，`AddThroughAll` 只朝一侧切 |
| 3 | `Profiles.Add(face)` | **失败** `E_NOINTERFACE` |
| 3 | `Sketches.AddByPlanarFace(face)` | **失败** `E_NOTSUPPORTED` |
| 3 | `AddParallelByDistance(基准面, 距离, side)` | 基准面成立，`Convert3DCoordinate` 精确；但**方向指反**导致 `igFeatureFailed` |
| 3 | `HoleDataCollection.AddEx(igTappedHole,"ISO Metric",…,"M6",…)` | **成功** |
| 4 | **`AddParallelByDistance(FACE, 0, igNormalSide, …)`** | **成功** —— 面可以直接当父平面，这是唯一的可行面定位方式 |
| 4 | 修正方向后 | **体积比 0.999999999999995**，贯通切除精确成立 |
| 5 | 一个轮廓里放 4 个 `Holes2d` | **只切出 1 个**（`Holes2d.Count=4`、`End=0`、状态 OK，但体积比正好 0.25） |
| 6 | `AddSync(4, profiles, …)` | **失败** `E_NOTSUPPORTED (0x80004021)` |
| 6 | **每孔一个轮廓 + 一次 `AddThroughAll`** | **成功**：4 孔 4 特征，**体积比 0.999999999999977** |
| 6 | 反转面（`IsParamReversed=True`）上的方向重试 | **成功**，一次即中 |

### 由此固化的六条硬约束

1. 孔轮廓用 `Profile.Holes2d.Add(x, y)`。
2. `ProfilePlaneSide` **必须指向材料内部**；面片几何法向与实体外法向不一定一致，所以**不算法向，先试 `igLeft` 失败再试 `igRight`**。
3. **一个轮廓只放一个孔心**，每孔一个特征。
4. **`AddSync` 在天工CAD 不可用**。
5. 面定位只能走 `AddParallelByDistance(face, 0, …)`。
6. `RefPlanes.AddBy3Points` 不可用。

## 二、纯逻辑测试（`PanelTests.exe --autohole-pure`）

**35 条断言全部通过**，不需要 CAD。覆盖：

- 配做反推：Φ8.5 → M10 底孔 → 目标件配**沉孔**（Φ18/深11/通孔Φ11）；Φ13.5 → M12 过孔 → 目标件配**螺纹孔**（底孔Φ10.2）。这两个样例取自 ICAN 的实际 UI 状态栏。
- 边界：Φ12.7 距 M12 精配过孔 13.0 只有 0.3，**仍命中 M12**；Φ7.5 距所有标准值均 > 0.35，**正确判定未命中**，退化为同径通孔。
- 手工指定：M8 螺纹孔底孔Φ6.8、M8 沉孔Φ14.5深8.6。
- 表完整性：9 个规格、底孔单调递增。
- 非法输入：拒绝 0 与 NaN 直径。
- `Transform` 逆变换往返一致（点与法向）。

## 三、原生端到端测试（`PanelTests.exe --autohole <目录>`）

夹具：两块板 → A 板打一个 **Φ8.5** 底孔（模拟"别人已经打好的孔"）→ 装配里 A 在下、B 在上方 20mm → 从装配中取 A 的**孔圆边**和 B 的**下表面**，走完整的"读参考孔 → 反推规格 → 求交 → 打孔"链路。

实测输出：

```
AUTOHOLE started a new CAD instance
PASS: A 板有一个底孔
PASS: 装配内两个实例
PASS: 读到 A 板的圆孔边线
PASS: 读到 B 板的下表面
READ hole center=(0.050000, 0.050000, 0.005000) axis=(0.000000, 0.000000, -1.000000) dia=8.5
PASS: 参考孔直径读为 8.5mm，实读 8.5
PASS: 参考孔轴向为 Z，实读 (0.000000, 0.000000, -1.000000)
PASS: 从面片反查到所属零件文档
PASS: 零件名非空：PlateB.par
PASS: 打孔面法向为 Z
PASS: 孔心与参考孔同轴 (0.050000, 0.050000, 0.010000)
PASS: 孔心落在 B 板下表面 z=10mm，实读 0.01
PASS: 无失败项
PASS: B 板切除体积符合「沉孔Φ18深11 + 通孔Φ11」两段（实 3654.5 mm³ / 期 3654.5 mm³）
PASS: B 板新增 1 个孔特征
PASS: A 板（参照件）未被改动
```

**体积 3654.5 mm³ 与理论值 3654.5 mm³ 完全吻合**，说明沉孔与通孔两段几何都对。
**"A 板未被改动"这一条是关键**——参照件绝不能被插件改到。

### 过程中被测试夹具（不是产品）抓出来的两个坑

1. `RefPlanes.Item(3)` **不是 XY 基准面**。夹具原本按序号取面，结果把一个 Φ8.5 的孔打在了板侧面（轴向变成 Y）。产品代码本身没问题——它如实读回了那个孔。夹具改成按几何找 XY 面后正确。**这条对后续开发同样重要：不要按序号猜基准面。**
2. 拉伸件的**下表面其底层平面法向仍是 +Z**，靠 `IsParamReversed` 表达朝向。所以按法向符号找面会漏，要按平面位置找。

## 四、插件加载

命令注册表（`PanelTests.exe --commands`，不需要 CAD）：

```
COMMAND 1 四面生成内嵌板
COMMAND 2 型材自动填充
COMMAND 3 Lineup 模型标记
COMMAND 4 导出出图训练数据
COMMAND 5 批量格式转换
COMMAND 6 自动打孔 | 照着一个已有的孔，在其他零件上打同样的孔
COMMAND COUNT 6
```

注册：`tools/install.ps1 -LibraryPath build\autohole-1\TianGongCadSuite.dll`（当前用户，不需管理员）。
加载自检：`tools/verify-autohole.ps1`。

> 注意：CAD **必须先在文档打开后**才实例化插件。verify 脚本因此先 `Documents.Add` 再读 `AddIns`（与既有的 `native-test.ps1` 一致）。不按这个顺序会看到 `addin.Object` 为 NULL。

## 五、尚未验证 / 未完成

- **截图证据无效**：测试里调用了 `View.SaveAsImage`，但导出的 `autohole.png` 是**全白空图**（CAD 窗口未实际渲染）。本项目的 `VALIDATION.md` 里也记录过同类视口/绘图回调问题。**所以本次的结论全部以数值为准（切除体积、特征数、参照件体积不变），没有可看的图。**
- **真实鼠标交互链**：没有用鼠标点孔、点面、点按钮跑一遍。原生测试调用的是真实 CAD 接口和真实选择引用，但不等于桌面操作验收。
- **在用户实际装配上跑**：未在真实型材框架总装上试。
- **腰孔**：`Holes2d.Add` 只能做圆孔，腰孔需 `Lines2d` + `Arcs2d` 拼封闭轮廓，未验证。
- **配孔检查（命令 7）**：未实现。
- **型材专用排孔**：未实现。
- **非平行装配**：产品代码在两零件不平行时会明确报错（`Intersect` 里 `|cos| < 0.05` 即拒绝），但未做原生测试覆盖。
- **多面一次打孔**：UI 支持选多个面，原生测试只覆盖了 1 个面。
