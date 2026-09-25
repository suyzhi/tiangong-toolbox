# 天工CAD 插件源码包（自动打孔系列 · 2026-09-26）

压缩包根目录两件事：`调研报告/` 是自动打孔的资料调研，`DEV_0.4_插件框架/` 是插件源码本体。

## 目录说明

| 目录 / 文件 | 内容 |
|---|---|
| `DEV_0.4_插件框架/src/` | 插件全部源码（C# 4.5，WinForms + COM interop） |
| `DEV_0.4_插件框架/tests/` | 测试程序（`PanelTests.exe` 的源码，含 AutoHole 纯逻辑与原生断言） |
| `DEV_0.4_插件框架/tools/` | 构建脚本、原生测试脚本、API 探针（保留作为行为证据） |
| `DEV_0.4_插件框架/*.md` | 各功能的踩坑记录与验证报告 |
| `DEV_0.4_插件框架/AUTO-HOLE.md` | 自动打孔（命令 6/7/8）的实测结论与两轮修复记录 |
| `调研报告/` | 凯元工具箱 / 嘉立创 ICAN / SolidEdge API 的调研报告 |

## 编译与验证

```powershell
# 需要本机装好天工CAD（默认 C:\Program Files\NDS\TianGong 2025），从它取 Interop.TG.dll
tools\build.ps1                       # 产出 build\TianGongCadSuite.dll + PanelTests.exe

# 纯逻辑断言（不需要 CAD）
build\PanelTests.exe --autohole-pure   # 232 条
# 原生端到端（会自己起一个天工CAD 实例）
build\PanelTests.exe --autohole-fixes .   # 21 条：圆面判定 / 沉孔+锥沉几何 / 配孔采集
build\PanelTests.exe --autohole-tapped .  # 26 条：螺纹孔不带锥面
build\PanelTests.exe --autohole-pattern . # 18 条：排孔 + 配孔检查
build\PanelTests.exe --autohole .         # 17 条：跨零件照孔打孔
```

## 本轮（2026-09-26）改了什么

完整清单见 `DEV_0.4_插件框架/AUTO-HOLE.md` 的「2026-09-26 修复」一节，要点：

1. 沉孔 / 锥形沉孔 / 通孔 / 螺纹孔降级分支原来还留着 `Missing` 参数（= 会被 CAD 灌入上次用过的孔参数），
   现在四种孔型统一走全参数显式 `AddEx`；并用真机探针逐项测出哪三个参数**不能**显式给值。
2. `Audit()` 从「只比孔型」扩成回读比对孔径 / 沉孔直径深度 / 锥孔直径锥角 / 孔底角度 / 螺纹规格。
3. 配孔检查取「该零件该组最小直径」而不是最大 —— 修掉「命令 6 打的沉孔被命令 8 判成配错孔」。
4. `ContainsPoint` 支持圆/圆弧边界的面，并把面内包围盒缓存起来（预览不再反复走 COM）。
5. 规格改「自定义」时清空螺纹标注；匹配到容差边缘时提示「请手动确认规格」。
6. 排孔「手动指定方向边」原来点不到（过滤器卡在只能点面）；配孔检查可被 Ctrl+Enter 重入；
   「孤孔」过滤器与开关不一致 —— 一并修掉。
7. 漏打孔判定从 O(孤孔数×零件数²) 次 COM 调用降到 O(孤孔数×零件数)。
8. 顺带挖出并修掉 `GroupByAxis` 的两个同轴判定 bug（只比组内第一个成员；反平行轴线分桶不同键）。

## 验证结果（本机真机，天工CAD 225.03.00.165）

```
--autohole-pure      232 条断言全过
--autohole-fixes      21 条全过
--autohole-tapped     26 条全过
--autohole-pattern    18 条全过
--autohole            17 条全过
```

> 源码包里不含任何构建产物（`build/`、`artifacts/`）与客户样表。
