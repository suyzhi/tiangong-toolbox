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
