# 天工CAD 插件源码包（自动打孔 · 孔形状参考面板 · DEV 0.6.0）

压缩包根目录两件事：`调研报告/` 是自动打孔的资料调研，`DEV_0.6_打孔预览/` 是插件源码本体。

## 目录说明

| 目录 / 文件 | 内容 |
|---|---|
| `DEV_0.6_打孔预览/src/` | 插件全部源码（C# 5，WinForms + COM interop） |
| `DEV_0.6_打孔预览/src/HoleProfile.cs` | **孔形状的唯一定义**（纯计算：轮廓 + 倒角/钻尖/锥座几何） |
| `DEV_0.6_打孔预览/src/HoleViews.cs` | 2D 剖面视图 + 3D 半剖轴测视图 |
| `DEV_0.6_打孔预览/tests/` | 测试程序（`PanelTests.exe` 的源码，含 AutoHole 纯逻辑、界面状态与原生断言） |
| `DEV_0.6_打孔预览/tools/` | 构建脚本、原生测试脚本、真机探针（保留作为行为证据） |
| `DEV_0.6_打孔预览/*.md` | 各功能的踩坑记录与验证报告 |
| `DEV_0.6_打孔预览/AUTO-HOLE-UI.md` | **本版说明**：孔形状参考面板、倒角实测、排版、修掉的 4 个 bug |
| `DEV_0.6_打孔预览/AUTO-HOLE.md` | 自动打孔（命令 6/7/8）的实测结论与历次修复记录 |
| `调研报告/` | 凯元工具箱 / 嘉立创 ICAN / SolidEdge API 的调研报告 |

## 编译与验证

```powershell
# 需要本机装好天工CAD（默认 C:\Program Files\NDS\TianGong 2025），从它取 Interop.TG.dll
tools\build.ps1                        # 产出 build\TianGongCadSuite.dll + PanelTests.exe

# 不需要 CAD 的断言
build\PanelTests.exe --autohole-pure    # 203 条：规格反推 + 孔形状几何（含倒角/钻尖/锥座实测公式）
build\PanelTests.exe --autohole-ui      #  30 条：参数面板的控件状态与排版（盲孔深度框可用等）

# 界面渲染成 PNG（在私有桌面上跑，不打扰用户桌面）
tools\run-uishot.ps1                    # 输出 artifacts\uishot\*.png，覆盖 7 个孔型场景

# 原生端到端（会自己起一个天工CAD 实例，同样在私有桌面上）
tools\run-autohole-native.ps1           # 一次跑完下面全部
build\PanelTests.exe --autohole-fixes .   # 21 条：圆面判定 / 沉孔+锥沉几何 / 配孔采集
build\PanelTests.exe --autohole-tapped .  # 26 条：螺纹孔不带锥面
build\PanelTests.exe --autohole-pattern . # 18 条：排孔 + 配孔检查
build\PanelTests.exe --autohole .         # 17 条：跨零件照孔打孔
build\PanelTests.exe --autohole-multi .   # 22 条：一次多选不同大小的参考孔
build\PanelTests.exe --autohole-form .    #      三个窗口的显示 / 重复初始化
```

## 本版（0.6.0）改了什么

完整说明见 `DEV_0.6_打孔预览/AUTO-HOLE-UI.md`，要点：

1. **孔形状参考面板**：左边 2D 剖面（带尺寸标注），右边 3D 半剖轴测（打孔后的数模长什么样）。
   两个视图吃同一份纯几何定义 `HoleShape`，所以预览画的和 CAD 实际切出来的必须是同一个形状。
2. **孔口倒角的真实几何是真机量出来的**：`SetStartChamfer(1, Setback, Angle)` 里
   Setback 是孔口径向增量、Angle 是与孔轴的夹角，轴向深 = `Setback / tan(Angle)`。
   三个非 45° 用例的切除体积与公式精确吻合（26.602 / 79.807 / 65.297 mm³），
   另外三种解释差 3 倍以上。证据：`tools/ChamferProbe.cs` + `tools/run-chamfer-probe.ps1`。
3. **排版重做**：两列布局（左选东西、右定规格）+ 参数固定栅格对齐（标签 0/140/272、控件 34/174/306）、
   单位紧跟数字、说明文字搬进独立的「规格来源」卡片、卡片高度跟着行数伸缩、
   步骤说明超宽自动省略号、窗口 900×880（最小 780×800）。
4. **草稿规格**：没点参考孔时也能改参数并实时看孔形状，不用先在模型上点一个孔。
5. **修掉四个真 bug**：
   - 勾了"盲孔"深度框还是灰的（`WriteBack` 在草稿分支提前 return，跳过了 `ShowKindFields`）；
   - 没点参考孔时选标准规格不回填尺寸（`ApplySizeChange` 在 `g==null` 时直接 return）；
   - 换孔型/换规格会冲掉用户手改的贯通/盲孔/倒角（改成统一走 `CopyUserEdits`）；
   - `ShowKindFields` 拿 `Control.Visible` 当布局依据（父窗口没显示时一律 false），改成按孔型算。

## 验证结果（DEV 0.6.0，本机真机，天工CAD 225.03.00.165）

```
--autohole-pure      203 条断言全过
--autohole-ui         30 条全过
--autohole-fixes      21 条全过
--autohole-tapped     26 条全过
--autohole-pattern    18 条全过
--autohole            17 条全过
--autohole-multi      22 条全过
```

> 源码包里不含任何构建产物（`build/`、`artifacts/`）与客户样表。
