# 批量格式转换：插件里点"开始转换"弹「找不到文件 --worker」（2026-09-28）

## 现象

在天工 CAD 里用插件命令「批量格式转换」，点「开始转换」后：

1. 蹦出一个**天工 CAD 自己的报错框**：标题「天工 CAD 2025 标准版」，正文「找不到文件--worker。」
2. 主界面日志只有"已发现 1 个 SolidWorks 文件"，**没有"开始转换"那一行**
3. 进度条停在 0 / 1　成功 0　失败 0，输出目录里一个零件都没有；CSV 报告里该行状态是 pending

独立版（直接双击 TianGongConverter.exe）走同一套界面却一切正常。

## 根因

FormatConvertForm.StartConversion() 用 Application.ExecutablePath 当 worker 的 exe：

    string self=Application.ExecutablePath;
    var info=new ProcessStartInfo(self,"--worker \""+jobFile+"\" ...");

这个窗体类被两个宿主复用：

| 宿主 | Application.ExecutablePath | 结果 |
| --- | --- | --- |
| 独立版 TianGongConverter.exe | 转换器自己 | 正常 |
| CAD 插件（进程是 TianGong.exe） | **天工 CAD 本体** | 又拉起一个天工CAD，它把 --worker 当成"要打开的文件名" → 「找不到文件--worker。」 |

所以那句报错的措辞其实就是线索：**文件名就是 --worker**；弹框标题是天工CAD 而不是转换器，说明报错的是"第二个 CAD 进程"。

ConverterHost.Resolve()（按程序集位置解析 TianGongConverter.exe）当天已经写好了，注释也点明了这个坑
（"Inside CAD the current process is TianGong.exe, so the worker must be resolved from the plug-in folder"），
但它**只被用在"打开独立版"上，而 OpenStandalone 从来没有被调用过** —— 真正拉起 worker 的那一行漏改了。

## 证据链

| 证据 | 内容 |
| --- | --- |
| 截图 | 报错框标题是天工CAD 本体，正文「找不到文件--worker。」 |
| %LOCALAPPDATA%/Temp/TianGongConverter/<stamp>/status-0.txt | **0 字节** —— worker 连 READY 行都没写，说明它从未进入 ConvertWorker.Run |
| job-0.txt | 任务文件正常写好（输入路径、根目录都对） |
| 输出目录 CSV | 该行停在 pending，.par/.asm 从未生成 |
| 反编译安装中的 DLL | 该处确为 Application.ExecutablePath |

## 修复

    // 插件模式下当前进程是 TianGong.exe，Application.ExecutablePath 指向 CAD 本体，
    // 用它当 worker 会再拉起一个天工CAD（"找不到文件 --worker"）。必须解析转换器自身。
    string self=ConverterHost.Resolve();
    if(self==null)self=Application.ExecutablePath;

回退分支保证独立版行为不变（Resolve() 在独立版里同样返回转换器自身）。

顺带补了一处**静默失败**：Process.Start 抛异常时原来会冒泡成"未处理的异常"，
界面什么都不显示、看起来像点了没反应。现在会写进日志、写进 status 文件并在界面提示。

## 验证

1. 反编译修复后的 DLL：ProcessStartInfo 的第一个实参已是 ConverterHost.Resolve()，指向 TianGongConverter.exe
2. 新增 4 条回归断言（tests/FormatConvertTests.cs），在没有修复的构建上必失败：

       PASS: worker host resolves: .../build/TianGongConverter.exe
       PASS: worker host points at the converter exe, got TianGongConverter.exe
       PASS: worker host is beside the plug-in assembly (not the hosting CAD exe)
       PASS: resolved worker host actually exists on disk

3. 真实转换：MC模组-20260924.stp（11.8 MB）
   → MC模组-20260924.asm + 29 个零件；status-0.txt 出现 READY 12.36 与 EXIT 0，
   END ... ok ... 26.02 11.94 38.44 29

## 关联发现：转换被授权栅栏挡住时，报错文本会骗人

同一轮排查里还发现：worker 在未激活的机器上会直接

    FATAL	InvalidOperationException: 模型操作未能完成，请重试或检查当前选择。
    EXIT	1

这句话对用户毫无指向性（看起来像"模型有问题"），实际是 LicenseGate。转换器不像插件那样会弹激活窗口，
建议后续把这条改成人话并给出激活引导。

## 复现与回归

    DEV_0.7_授权/tools/build.ps1
    DEV_0.7_授权/tools/install.ps1          # 重启 CAD 后生效（先关 CAD 再注册）
    DEV_0.7_授权/build/PanelTests.exe       # 106 core / 203 auto-hole / 0 失败

---

# 追加（同一轮）：未激活时的那句骗人报错，已修

排查过程中还发现：机器未激活时 worker 会**先花约 12 秒启动一次 CAD**，然后才抛出

    FATAL	InvalidOperationException: 模型操作未能完成，请重试或检查当前选择。

看着像"模型坏了"，实际是授权栅栏；而转换器不像插件那样会弹激活窗口，用户完全无从下手。
现在改成：入口先查授权，不通过就直接返回（12 秒 -> 1 秒），文案也说人话：

    FATAL	未激活或授权已失效，转换器拒绝工作。请先在天工CAD里打开插件任意命令完成激活，
            或在本机运行 LicenseActivate.exe 输入激活码。
    EXIT	1

# 追加：转换产物与输入目录混在一起时的行为（实测）

用户的输出目录默认是"输入目录/天工格式输出"，而有人会把输出直接指到输入目录本身。实测：

* 第一次转换：成功，产物（.asm/.cfg/.log + 29 个零件）与源 .stp 同目录
* 第二次转换：skip / 输出已存在，不会重复转（force 未勾选时）

另外观测到**首次转换偶发** STG_E_LOCKVIOLATION（0x80030021，锁定冲突），
重试一次即成功——与 FORMAT-CONVERT.md 里"会自动重试一次"的设计一致。
但这条报错文案对用户不可读，建议后续也过 Friendly() 翻成人话。

# 鼠标点击全量实测：进度与阻塞点（2026-09-28）

## 已完成

* 私有桌面 + 真实鼠标消息的驱动链路打通（tools/fix-e2e/、tools/mouse-e2e/）
* 给技能里的 desk-helper 补了三个 op：sendclick / sendclickat / invokeclick（显式 SetCapture）
  和 uia（桌面内枚举 UI Automation 矩形）
* 「插件」标签页坐标已实测标定：**位图 (770,45) -> 屏幕 +20**；点它可稳定切页
* 已用真鼠标点开的窗口：自动打孔（916x919）、Lineup 快速录入（1280x660）、
  多型材自动填充（1036x699）、批量格式转换（916x699）、授权激活（676x469）

## 阻塞点（下一轮从这里继续）

功能区**命令按钮**的坐标标定还没收敛。已确认的硬事实：

* 客户区位图与 1500x900 窗口 1:1；位图 -> 屏幕需 +20
* 文字行中心位图 y ≈ 76 / 98 / 124 / 145
* 但把 3x 裁图量出来的按钮中心直接 +20 用，会**向右偏移**：点 (156,98) 开的是
  「天工CAD 批量格式转换」而不是「批量格式转换」，点 (137,76) 开的是「自动打孔」——
  即实际命中点比按文字中心算出来的**往右约 60px**。

**下一轮的正确做法**：不要再手工算。写一个"点 -> 读新窗口标题 -> 记下对应关系"的
自动标定循环（tools/fix-e2e/calib.ps1 已有雏形，只需把识别改成匹配窗口标题），
一次跑完 8 个命令，得到 (命令名 -> 屏幕坐标) 的实测表再跑正式用例。
