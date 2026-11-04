# 交接：0.11.3 批量改名 / 属性卡填写加零件预览

2026-11-04。用户原话：「当前插件的批量改名和属性卡填写功能没有零件预览图，我都不知道这个零件长什么样子，怎么给他改名字……点击这个零件，旁边会弹出零件的预览图，还可以旋转零件，就像天工自带的功能里的零件预览一样。」

## 做了什么

* 新文件 `src/PartPreview.cs`：表格右边一块预览（中间分隔条可拖宽窄），点表里一行就显示那个文件的三维模型。
  * 鼠标：**左键拖动旋转**、滚轮缩放、Shift+拖动平移、Ctrl+拖动缩放、双击适合；上面有「等轴测 / 前 / 上 / 右 / 适合」。
  * 标题栏「隐藏预览 / 显示预览」，开关记在 `choices.tsv`（`rename.preview`、`propcard.preview`）。
  * 工程图行显示同文件夹里同名的零件 / 钣金 / 装配（批量改名里跟着改的工程图就是这样配对的）；没有就提示。文件不在了也提示。
* `BatchRenameForm` / `PropCardForm`：接上预览（`CurrentCellChanged` / `CellClick` → `ShowFile`；重建表格时不跟着跳到第一行 —— 第一行常是总装配，要加载好几秒），`BeginBusy` / `EndBusy` 里 `Hold`；改名做完 `preview.Renamed` 换成新名字；默认窗口 1120×680 → 1440×700（小屏幕照旧按工作区收）。
* 开发构建入口：`Diagnostics.StartPreviewFormTest`（点几行、记耗时 / 占用）、`StartPreviewHold`（停在某行不关，给外部截图）；`TestRun` / `TestFillAndWrite` 先让预览占住一个文件再改名 / 写入。

## 用的是天工自带的预览控件

`C:\Program Files\NDS\TianGong 2025\Program\SEPreview.ocx`（CLSID `{7F0AA8A5-3928-4B4B-AE60-4E3C1E331103}`，ProgID `SEPREVIEW.SEPreviewCtrl.1`），就是天工打开文件对话框里那个预览。
接口（`SEPreview.tlb` 用 TlbImp 看的）：`Filename` / `PartFile` / `DraftFile` / `DisplayMode` / `ViewType` / `ViewPerspective` / `BackgroundColor` / `Fit()` / `SetGradientBackground(下, 上)`。
插件里不生成 aximp 包装，`AxHost` 子类只给 CLSID，属性按名字 `InvokeMember`。控件建不出来就只显示提示，表格照常用。

它读的是文件里存的显示数据，**不在 CAD 里打开文档**：零件 10～20 ms，二十来个零件的子装配约 230 ms，64 个零件的围栏总装约 1.2 s（引用的零件要在原位置才显示全）。

## 实测的坑（spike 在会话 scratchpad 的 `spike\PreviewSpike.cs` / `PanelSpike.cs`，私有桌面上跑、截图核对）

| 现象 | 处理 |
| --- | --- |
| 控件**占着当前显示的文件**：别的进程 `File.Move` 报「另一个进程正在使用此文件」；换文件 / `Filename=""` 才放开 | 改名、写属性前 `Hold(true)`（清空）；窗口**失去激活**也放开（用户可能在天工里 Ctrl+S 存这个文件），放开前 `PrintWindow` 截一张图顶着，回来再显示 |
| 原生只认中键：中键拖动旋转、Shift+中键平移、Ctrl+中键缩放；左键 / 右键没反应 | `Host.WndProc` 把左键按下 / 抬起 / 拖动改成中键；左键双击 = 适合 |
| `.dft` 显示不出来（控件保持上一个文件），`DraftFile` 也不行 | 显示同名模型 |
| 文件不在时不报错、只是空白 | 先 `File.Exists` |
| 换文件后沿用上一个文件的视角 | 新文件回到等轴测 + 适合；同一个文件重新显示（切窗口回来）不动视角 |
| `Fit()` 贴边（零件两头顶着窗口边） | 适合后发一次 Ctrl+中键往上拖 12 px（约 0.85 倍）留边；拖之前先发一个 `WM_MOUSEMOVE`，不然新控件第一次拖动不认 |
| 新控件加载第一个文件后自己会异步「适合」一次，马上调的视角会被冲掉 | 加载后 120 ms 再调（`settle` 定时器） |
| `ViewType` 读回来总是空，写 `Iso / Front / Top / Right / Back / Left / Bottom / Dimetric` 都生效 | 只写不读 |
| `DisplayMode="Wireframe"` 生效，`Shaded` 不认 | 用默认（带边着色） |

## 验证

* `tests/pure` 全过；`PanelTests.exe` 核心 50 条全过，跑完 `activation.dat` 还在。
* 私有桌面独立程序（不开 CAD）：显示 / 左键拖动旋转 / Shift 平移 / 切走窗口截图顶着并放开文件（`File.Move` 成功）/ 回来视角保持 / 工程图换同名装配 / 不在的文件提示，全部按预期。
* 真机（私有桌面天工，开发构建，加密副本 cart = 焊接台车 YF25.07G01-L1-M30-02）：`RenameE2E.exe <输出> preview|previewshot|main|propcard <副本>`（`artifacts/p5-*`）
  * `preview`：两个窗口各隔着点 8 行，每行都显示出来（零件 15～60 ms；149 个文件的总装配 3.4 s），不弹框；切走窗口后零件文件不再被占着（`FileShare.None` 打得开）；.cfg 行显示同名装配；
  * `main`：预览先显示要改名的总装配（占着它）再走真改名 → 两轮（替换 + 只改大小写）50 个文件都改好、仍是加密的、工程图 23 张链对、无临时文件。4 条「0 缺件」FAIL 是副本引用的 H 盘原件 `参考\堆叠小车0918\…-02-14.par / -02-28.par` 早被用户改成 -014 / -028（0.10.2 交接里记过，和插件无关）；
  * `propcard`（0.11.3 第一轮）：预览占着第一行再写入，120 行两轮 119/119 全写进、无弹框。
* **私有桌面上截不到天工进程里的三维画面**（进程内 / 外部 PrintWindow 都是白的或露出底下的提示字，独立进程里却截得到 —— 天工进程里控件是硬件画的）。所以经用户同意，在用户真桌面上跑了一次 `previewshot`（CopyFromScreen）：批量改名、属性卡里零件都正常显示（截图在会话 scratchpad `real-shot\screen-*.png`）。
* **已知问题**：焊接台车**总装配**那一行（天工里开着、149 个文件）预览区只有底色、没有模型，加载要 3.4 s；零件、独立进程里的围栏装配都正常。没查原因（可能是装配显示数据里的零件引用、或「适合」把模型放到了视野外）。下一步：真桌面上点一个子装配行、多等几秒再截，对比天工打开文件对话框里这个装配的预览。
* 已装回正式版 0.11.3（安装包 `天工工具箱_DEV0.11.3_安装包_20261104.zip`），`activation.dat` 在。

## 总装配预览是空的：查到哪了（2026-11-04 下午，没修完）

在用户真桌面上跑了 4 次 `RenameE2E previewfiles`（截图在会话 scratchpad `real-asm*`；探针窗口要 TopMost，不然被别的窗口挡住）：

* 罪魁是 **`聚氧模组侧向支撑子装配.asm`**：它单独预览就是空的（放大也没有），含它的焊接台车总装配、`YF25.07-L1-M30-03.asm` 都空；
  它里面的每个零部件（`UHFNSG100-S.asm`、`HGH20CA1R280Z0C…asm`、`-02-18/29/31/32/33.par`）单独预览都正常 —— 是控件内部组合时出的问题；
* 已排除：加密（加密的「定位滚轮.asm」正常）、天工里开没开着、等待时间、隐藏实例（控件根本不认隐藏，照画）、缺零件（同文件夹 / 别的文件夹、可见 / 隐藏都试过，只是少画一块）
  —— 变体装配用 `tools/hole-feature-probe/AsmVariantProbe.cs` 在明文围栏副本上做；`AsmRangeProbe.cs` 列实例可见性 / 缺件 / 包围盒；
* 围栏总装配偏到右下角：20 根 `40x40常规.par` 型材（装配里各自长度不同）控件不画，范围却算进去了；
* 文件里**没有存缩略图**（SummaryInformation 的 PIDSI_THUMBNAIL 是空的，也没有图片流），缩略图兜底不可行。

**下一步（打算的修法）**：插件自己画兜底 —— 装配行显示后 ~700 ms 用 `CopyFromScreen` 读预览区，只剩背景渐变（每行颜色一样）就判为没画出来；
这时用 `AssemblyPart.List`（配孔检查在用）取天工里开着的装配的每个零件实体，`Body.GetFacetData` 取网格（同一文件只取一次）、`Placement` 摆位
（子装配里的实例也可以直接 `SubOccurrence.GetMatrix`），软件 z-buffer 画着色图，左键拖动旋转、滚轮缩放。私有桌面上读屏永远是空白 → 测试时会一直走兜底，正好能测渲染器（GDI 画的图 PrintWindow 截得到）。

开发入口：`Diagnostics.StartPreviewFile(path | "!Front" | "!zoomin" | "!zoomout" | "")`。

## 没做 / 以后可做

* 高 DPI 下没测（同前，见 memory tiangong-dpi）。
* 大装配（几百上千个零件）在预览里加载会更久（UI 线程同步，几秒）；超过 500 ms 记 `panel.log`（`PartPreview slow …`）。真遇到再考虑装配行只在点「预览」时加载。
