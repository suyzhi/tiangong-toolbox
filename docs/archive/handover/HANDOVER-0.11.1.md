# 交接：0.11.1 一键出图加公差和粗糙度

2026-11-03 一个 /loop 会话做完。规矩、普查数字、接口事实、基准结果都在 [DRAWING.md](DRAWING.md)「公差和粗糙度（0.11.1）」「天工接口的实测事实」第 9～12 条、「公差和粗糙度完成度」。

## 做了什么

* **普查**：`tools/drawing-probe/DftSurvey.cs` 加了 `tol` 字段（全部尺寸的公差 / 公差带、形位公差框、基准、粗糙度，连同按位置找到的最近图元 / 最近尺寸——人图里这些符号几乎都没真挂在图元上）。734 张公司图纸副本（`artifacts/dft-corpus/`，`map.tsv` 是编号 → 原路径）全读完，0 出错（0333.dft 会把 CAD 弄崩，单独重跑过）。
* **三维对照**：新探针 `tools/drawing-probe/ModelHoleSurvey.cs`（用插件自己的 `DrawingModelRead.Holes`）读了 461 个零件副本（`artifacts/model-corpus/`）的孔。
* **统计脚本**：`artifacts/drawing-bench/tolstats.py`（总表）、`tolstats3.py`（粗糙度）、`tolmodel.py`（和三维孔对照）；结果文本 `artifacts/tol-survey/stats.txt / roughness.txt / model-join.txt`。
* **接口探针**：`DrawProbe7.cs`（公差 / 形位公差框 / 基准 / 粗糙度）、`DrawProbe7c.cs`（H7 怎么显示出来：要设 `PrimaryToleranceSymbol`）。
* **规则**（纯逻辑 `src/DrawingTolerance.cs`，测试 `tests/DrawingTolerancePureTests.cs`，已加进 tests/pure）：光孔（没螺纹、没沉孔）Ø4 / 5 / 6 / 8 → H7 + 标注下面紧贴 ⌖Ø0.03；销孔开口那个面 Ra3.2（依据偏弱，已在提示里写明）。不加基准、不加带基准的形位公差。
* **接进出图**：`DrawingMaker.Annotate` 孔标注那段；`DwgWriter.Fit / Position / Roughness`；视图圆对不上三维孔时按直径在三维孔里找（同直径全是光孔才算）。「需要人工确认」第一条写加了什么。窗口说明文字加了一句。
* **基准**：`DrawCompare.cs` 另算公差完成度（`TOL` 行 + `RESULT 公差完成度`），数值完成度那部分没动。
* `VERSION` 0.11.0 → 0.11.1（GuiVersion 没动，功能区没变）。

## 结果（第 13 轮，`artifacts/draw-e2e-13`、`draw-v5`、`draw-compare-13`、`draw-compare-v5`）

| 集合 | 数值完成度 | 公差完成度 | 插件加的项准确率 |
| --- | --- | --- | --- |
| 调参集 40 | 94.0%，29/39 ≥90%（和 0.11.0 一样） | 0% → 17.1%（27/157） | 59% |
| 留出集 36 | 90.3%，20/36 ≥90%（和 0.11.0 一样） | 0% → 16.6%（7/42） | 18% |

* 出图 40/40、36/36；窗口路径（FormE2E，02-013 副本）提示列正常显示公差那句。
* tests/pure 全过；PanelTests 核心 50 全过，`activation.dat` 在、哈希没变。
* 都在副本 + 私有桌面 CAD 上跑，H 盘原件只读（没在 CAD 里打开原件，普查用的是拷出来的副本）。

## 已定（用户 2026-11-03）

1. **Ø5 保留**：`DrawingTolerance.PinDiameters` 仍是 `{ 4, 5, 6, 8 }`。留出集准确率低就是 Ø5 光孔被加了 H7 而人没标（5 张图），全普查 Ø5 光孔带公差 55%；用户看过后决定保留。
   * 第 14 轮（去掉 Ø5 的对照，`artifacts/draw-e2e-14`）跑到一半被停掉了，**没有结果**，不要拿它的半截输出做比较。
2. **Ra3.2 保留**：`DrawingMaker.Annotate` 末尾 `MatingFaces` 照旧调用，「需要人工确认」里仍写明这条依据偏弱。
3. 提交 + 推送：用户已同意，随 0.11.1 一起提交推送。安装、发在线更新还没做，要先问。

## 规矩（同 0.11.0）

真机测试只走私有桌面队列；用户说"我要用天工了"就停；只在副本上测；PanelTests 后查 `activation.dat`；提交 / 推送 / 安装 / 发在线更新先问。
