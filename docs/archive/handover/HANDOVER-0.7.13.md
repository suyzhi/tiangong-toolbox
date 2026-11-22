# 0.7.13 交接（2026-10-22）：多实体零件打孔

用户原话：「YF25.07G01-L1-M30-02-07，请创建这个零件的副本，然后尝试使用自动打孔功能，为什么我总是提示错误，该位置已经有了一个孔？这个零件在 H:\桌面\YF25.07\焊接台车」

全部在**副本**上做（scratchpad：零件副本 + 整个焊接台车文件夹副本，原文件没动）。探针 / E2E 在 `tools/hole-feature-probe/`，结果在 `artifacts/part-drill-0207*`、`artifacts/e2e-*`。

## 一、现场日志（0.7.12，2026-10-22 16:12～16:27）

02-07 上打了四次，每次：面片建基准面 `AddParallelByDistance` E_FAIL → 退回平行基准面 → 建孔两侧都 0x80010108 → 换到装配里补打同样失败。
插件自己的「已有孔」预检每次都**没**拦（拦了会报「已经有孔（Φ…），已跳过」）——用户看到的是 `Friendly()` 把 0x80010108 翻译成的「常见原因：这个位置已经有一个孔了」。

## 二、真机查明的事实（`PartDrillProbe.cs`）

| 事实 | 模式 |
| --- | --- |
| 02-07 有 **39 个设计体**（`Models.Count = 39`，全部活动），由 39 个复制构造 + 切换为设计体做成，另有 8 个被抑制的面移动 | 默认 |
| `Models.Item(m).Holes.AddThroughAll` 在每个实体最大的平面上都失败（实体 1 也失败），0x800401FD / 0x80010108 | 默认 |
| 先 `MakeActive()` 再打：实体 1 / 6 / 39 三种写法全失败 | `active` |
| `Holes.AddMultiBody(…, igThroughAll, …, BodyArray = { model.Body })` **成功**（面 18→20）；错的一侧 igFeatureFailed；BodyArray 放 Model = E_FAIL | `multi` |
| 面片 `Body.Tag` 唯一对上所在的 Model；`Body.Parent` 就是 Model | `facts` F1 |
| `Holes` 是整个零件共用的：实体 6 上打一个孔，39 个 Model 的 `Holes.Count` 都是 1 | `facts` F2 |
| 盲孔（igFinite）、多孔合一（UDP AddByProfiles）、矩形阵列、特征组在实体 6 上都成功 | `facts` F3～F6 |
| 腰孔 `ExtrudedCutouts.AddThroughAll` 老写法就成功；`AddThroughAllMultiBody` 反而 igFeatureFailed | `facts` F7 |

## 三、改了什么

| 改动 | 文件 |
| --- | --- |
| `PartBodies`：实体数、按序号取 Model、面片 → 第几个实体（先比 `Body.Tag`，面片失效时按共面 + 包围盒兜底） | `src/PartBodies.cs`（新） |
| `TargetFace.Body`：`ReadTarget` 时认实体；`AutoHoleWriter.ModelFor(target)` 取目标实体的 Model；`AnchorToActivePart` 在同一序号的实体里找面 | `AutoHoleCad.cs` |
| 建孔统一走 `AddHole`：单实体零件照旧 `AddThroughAll / AddFinite`；多实体零件 `AddMultiBody` + 目标实体的 Body | `AutoHoleCad.DrillOne / DrillOnProfile` |
| `ScanSpots`（已有孔预检、合成/阵列核对）、`FindModelFace` 只看目标实体；基准面缓存键带实体序号（两个实体贴在一起时共面的面外法向相反） | `AutoHoleCad.cs`、`HoleCombine.cs`、`HolePatternWriter.cs` |
| 自动打孔 / 批量排孔 / 腰孔的 Model 都换成 `ModelFor(target)` | 同上 |
| `Friendly()`：0x80010108 / 0x800401FB 不再说「已经有一个孔了」，改成「CAD 拒绝在孔心上建孔特征」+ 常见原因 | `AutoHoleCad.Friendly` |
| E2E：`MultiBodyE2E`（新）；`LinkedDrillE2E` 找目标面时看所有实体 | `tools/hole-feature-probe/` |
| VERSION = 0.7.13 | |

**没动**（实测不用动）：找孔 / 编组 / 撤销 / 改孔型 / 同步孔位里的 `Models.Item(1)` —— `Holes`、`FeatureGroups`、`Patterns`、`UserDefinedPatterns` 每个 Model 都看得到整个零件的。

## 四、验证

* 离线：`tests/pure` 全过；`PanelTests` 核心 106、`--autohole-ui` 87 全过，跑前跑后 activation.dat 哈希一样。
* `MultiBodyE2E`（02-07 副本 + 30-02-2 副本，`artifacts/e2e-multibody-2`）15/15：实体 6 一批 3 孔合成一个多孔特征、别的实体没被切；同位置再打 3 个都报「本来就有孔」不算失败；实体 15 盲孔；实体 12 批量排孔（种子孔 + 矩形阵列）+ 撤销；实体 1 单孔；实体 6 腰孔；单实体零件单孔 + 一批 2 孔合成（回归）。
  第一次跑（`artifacts/e2e-multibody`）单实体两条 FAIL 是检查写错了（那块板一个孔只多 1 张面），孔都打上了，改检查后重跑全过。
* `LinkedDrillE2E`（整个焊接台车文件夹副本，`YF25.07兼容小车手推车.asm` → 02-07，走「自动打孔」窗口真实路径，`artifacts/e2e-cart-0207`）：保持关联 / 不保持关联各一次，都是 **4 个 M10 螺纹孔进一个多孔特征**，保持关联那次 4 个全部挂上天工原生关联；回到顶层、窗口没变多。另外 4 个是参考孔两两重叠（腰孔两头），0.7.12 起本来就跳过并说明；12 个投影落在面外。
* 安装：`tools\build-install.ps1` → `build\release-20261022-171029`（正式构建），删掉 0.7.12.0 子键；私有桌面起 CAD：`AddInConnect … release-20261022-171029 … ver=0.7.13.0`。
* 激活：正式 DLL 读到 `Valid`，一年码 yvz7259y，到期 2027-10-20（换小版本不用重新激活）。

## 五、没做 / 下次注意

* **配孔检查**和**面扫描找孔**（`AssemblyParts.BodyOf`、`HoleScanCad.BodyOf`）仍只读第 1 个实体：多实体零件上第 2 个以后的实体里的孔，配孔检查看不到、点面扫孔也扫不到。自动打孔「点孔口圆边 / 孔壁」选参考孔不受影响（直接读选到的几何）。
* 0.7.12 交接里那个「万向轮范围--兼容小车.par」怎么都打不上孔，没查是不是同一个原因（多实体），下次可以先看它的 `Models.Count`。
