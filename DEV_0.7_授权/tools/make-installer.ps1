# tools/make-installer.ps1 —— 打"给别人的安装包"：天工工具箱_DEV<版本>_安装包_<日期>/ + 同名 zip。
# 与 make-delivery.ps1（给自己留档、带源码）不同：这里**只放安装需要的东西**——payload + 安装/卸载脚本 + 说明。
#
# 安装包行为：
#   * 先把 payload 复制到 %LOCALAPPDATA%\TianGongCadSuite\app 再注册（包可以删/挪走，插件照常用）；
#   * 安装/卸载前检测正在运行的天工进程，弹窗问一句"是否强制结束并继续"，
#     答是就代为强制结束（天工 CAD 是单实例程序，旧进程不退会导致新版本加载不上）；
#   * 支持 安装.cmd -Force 走无人值守（不弹窗）。
param(
    [string]$Version = '0.7.2',
    [string]$OutputRoot
)
$ErrorActionPreference='Stop'
$NL=[Environment]::NewLine
$sep=[IO.Path]::DirectorySeparatorChar
$root = Split-Path $PSScriptRoot -Parent          # DEV_0.7_授权
$repo = Split-Path $root -Parent                  # 仓库根
if(-not $OutputRoot){ $OutputRoot = $repo }
$stamp = Get-Date -Format 'yyyyMMdd'
$name  = '天工工具箱_DEV' + $Version + '_安装包_' + $stamp
$dst   = Join-Path $OutputRoot $name
if(Test-Path $dst){ Remove-Item $dst -Recurse -Force }

Write-Output '1/5 编译（正式构建，不含测试开关）…'
$bin = Join-Path $root ('build' + $sep + 'installer-' + $Version)
& (Join-Path $PSScriptRoot 'build.ps1') -Version $Version -OutputDirectory $bin | Out-Null
$dll = Join-Path $bin 'TianGongCadSuite.dll'
if(!(Test-Path $dll)){ throw '编译失败：没有生成 TianGongCadSuite.dll' }

Write-Output '2/5 组装 payload（只带插件运行时真正要用的文件）…'
$payload = Join-Path $dst 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
# 清单含义：插件本体 / Solid Edge 互操作库 / 批量格式转换的独立进程 / Lineup 默认型号表
foreach($f in 'TianGongCadSuite.dll','Interop.TG.dll','TianGongConverter.exe','lineup-models-seed.xml'){
    $src = Join-Path $bin $f
    if(Test-Path $src){ Copy-Item $src $payload -Force }
    else { Write-Output ('  （缺少 ' + $f + '，跳过；若运行中报错请检查这一项）') }
}

Write-Output '3/5 写安装/卸载脚本…'
$tools = Join-Path $dst 'tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $tools -Force
# 编码护栏（实测踩过，别删）：安装.cmd 里是 powershell.exe —— Windows PowerShell 5.1，
# 它读 .ps1 只认【带 BOM 的 UTF-8】；没有 BOM 就按系统 ANSI(GBK) 解码，中文注释当场变乱码，
# 脚本直接语法错误、安装半途而废（现象：弹一堆 Missing expression / Unexpected token）。
# 很多编辑器与改写工具会悄悄把 BOM 吃掉，所以在这里强制补，别指望上游一直留着。
$installPath = Join-Path $tools 'install.ps1'
$b = [IO.File]::ReadAllBytes($installPath)
if(-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)){
    [IO.File]::WriteAllBytes($installPath, [byte[]](0xEF,0xBB,0xBF) + $b)
    Write-Output '  （已给 payload 里的 install.ps1 补上 UTF-8 BOM）'
}

# .cmd 由 cmd.exe 执行：不能写 PowerShell 的 ';' 和 if(){ }（会被当成 -File 的实参）。
# 编码必须是**系统 ANSI(GBK)**：cmd.exe 按它解码 .cmd，写 UTF-8 会让中文变成乱码
# （注意 PowerShell 7 里 [Text.Encoding]::Default 是 UTF-8，不等于系统 ANSI，必须显式取代码页）。
# %* 用来透传 -Force（无人值守安装）。
$ansiEncoding = [Text.Encoding]::GetEncoding([int][System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage)
function Write-Cmd([string]$file,[string]$body){
    $head = '@echo off' + $NL + 'setlocal' + $NL
    [IO.File]::WriteAllText((Join-Path $dst $file), $head + $body + $NL + 'echo.' + $NL + 'pause' + $NL, $ansiEncoding)
}
$installBody = (@(
    ('echo 正在安装 天工工具箱 DEV ' + $Version + ' …'),
    'echo （若检测到天工 CAD 正在运行，会弹窗询问是否强制结束它）',
    ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools' + $sep + 'install.ps1" -LibraryPath "%~dp0payload' + $sep + 'TianGongCadSuite.dll" -StageDirectory "%LOCALAPPDATA%' + $sep + 'TianGongCadSuite' + $sep + 'app" %*')
) -join $NL)
$uninstallBody = (@(
    ('echo 正在卸载 天工工具箱 DEV ' + $Version + ' …'),
    ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools' + $sep + 'install.ps1" -Uninstall %*')
) -join $NL)
Write-Cmd '安装.cmd'     $installBody
Write-Cmd '重新安装.cmd' $installBody
Write-Cmd '卸载.cmd'     $uninstallBody

Write-Output '4/5 写使用说明…'
$appDir  = '%LOCALAPPDATA%' + $sep + 'TianGongCadSuite' + $sep + 'app'
$logPath = '$env:LOCALAPPDATA' + $sep + 'TianGongCadSuite' + $sep + 'panel.log'
$cmdLogPath = '$env:TEMP' + $sep + 'cmdlog V225.txt'
$addInGuid = '{8C05165C-65A4-4EF2-A138-508589D82004}'
$readme = (@(
    '# 天工工具箱（天工 CAD 插件）安装说明',
    '',
    ('适用：天工 CAD 2025（Solid Edge 内核）。版本：DEV ' + $Version + '。'),
    '',
    '## 一、安装',
    '',
    '1. 把整个文件夹解压到磁盘上（不要直接在压缩包里双击运行）。',
    '2. 建议先保存图纸；天工 CAD 开着也没关系，安装程序会问你。',
    '3. 双击 **安装.cmd**。',
    '   - 如果检测到还有天工进程在运行，会弹窗：「是否强制结束这些进程并继续？」',
    '     选 **是**，安装程序会替你强制结束所有天工进程（不用自己逐个关，也不用重启电脑）。',
    '     注意：未保存的图纸修改会丢失，所以先保存再装。',
    ('   - 安装过程会把插件文件复制到 ' + $appDir + '，'),
    '     之后这个安装包可以删掉或挪走，不影响使用。',
    '4. 安装完成后**启动天工 CAD**，功能区「插件」选项卡里即可看到命令。',
    '',
    '无人值守（不弹窗，直接强制结束天工进程）：命令行执行 安装.cmd -Force',
    '',
    '## 二、功能区里有什么',
    '',
    '| 命令 | 用途 |',
    '| --- | --- |',
    '| 型材自动填充 | 选中围成框口的型材（或框架子装配）→ 自动识别闭合框口 → 一次生成并插入全部内嵌板 |',
    '| Lineup 模型标记 | 装配级清单录入（五类清单、自动编号、零件关联） |',
    '| 批量格式转换 | SolidWorks / STEP 转天工 asm / par（批量、可续做） |',
    '| 自动打孔 | 参考孔 + 目标面 → 按规格配对打孔 |',
    '| 批量排孔 | 在一个面上按行列 / 圆周 / 腰孔等方式批量排孔 |',
    '| 配孔检查 | 检查同轴孔是否配做、有没有漏打孔、孔是否偏心 |',
    '',
    '> 早期版本的「四面生成内嵌板」「导出出图训练数据」两个命令已从功能区下线',
    '> （前者被「型材自动填充」取代，后者属于内部研究用）。',
    '',
    '## 三、激活',
    '',
    '插件需要激活码才能执行命令（第一次点命令会弹出「天工工具箱 授权激活」窗口）。',
    '把管理员发的激活码粘进去点「激活」，激活后与本机绑定；有效期由签发时的档位决定。',
    '',
    '## 四、卸载',
    '',
    '双击 **卸载.cmd**（同样会先问是否强制结束正在运行的天工进程）。',
    '卸载只删当前用户的注册项，已生成的模型文件不受影响。',
    '',
    '## 五、常见问题',
    '',
    '* **装了新版，功能区却还是旧插件**：天工 CAD 是单实例程序，注册表换了但旧进程还在。',
    '  再运行一次 安装.cmd（它会问你是否强制结束天工进程），然后重新启动 CAD。',
    '  自检命令（PowerShell，最后一行会自报本次实际加载的 DLL）：',
    ('  Select-String -Path "' + $logPath + '" -Pattern AddInConnect | Select-Object -Last 1'),
    '* **安装提示「注册完成」，但 CAD 里根本没有插件（选项卡、命令都不出现）**：',
    '  真实踩过：CAD 除了 COM 注册，还看当前用户下的一条「加载项记录」里的 AutoConnect——',
    ('  HKCU' + $sep + 'Software' + $sep + 'NDS' + $sep + 'TianGong' + $sep + 'Version 225' + $sep + 'AddIns' + $sep + $addInGuid + '。'),
    '  它是 0 时，CAD 照样把这个加载项列进自己的会话日志（GUID、路径、版本都对），却**不连接它**：',
    '  功能区里什么都不出现，而且没有任何报错——看起来就是「安装成功却没装上」。',
    '  **本安装程序会自动把它改回 1**（0.7.1 之前不会，所以之前会「提示成功却装不上」）：',
    '  再双击一次 安装.cmd，然后重启 CAD 即可。装完想知道 CAD 到底连没连上，跑这两条：',
    ('  Select-String -Path "' + $cmdLogPath + '" -Pattern "' + $addInGuid + '" -Context 0,4 -SimpleMatch'),
    '  输出里 Connect: TRUE 才算连上；Connect: FALSE 就是没加载。',
    ('  Select-String -Path "' + $logPath + '" -Pattern AddInConnect | Select-Object -Last 1'),
    '  插件自报本次实际加载的 DLL 与版本。',
    '* **功能区里没有「插件」选项卡 / 没有命令**：确认安装时选的是当前用户；重启 CAD 后仍没有，',
    '  用 CAD 的加载项管理器把「天工工具箱」勾上。',
    '* **个别进程没杀干净**：等几秒再运行一次安装程序即可（脚本会重试并报出没结束的 PID）。',
    '',
    '## 六、目录说明',
    '',
    '    安装.cmd / 重新安装.cmd / 卸载.cmd   安装入口',
    ('    payload' + $sep + '                            插件本体与随行文件（TianGongCadSuite.dll、Interop.TG.dll、TianGongConverter.exe …）'),
    ('    tools' + $sep + 'install.ps1                   实际执行安装/卸载的脚本（含强制关闭天工进程的逻辑）'),
    '    manifest-sha256.csv                 全部文件的 SHA256 清单（核对完整性用）'
) -join $NL) + $NL
[IO.File]::WriteAllText((Join-Path $dst '使用说明.md'), $readme, (New-Object Text.UTF8Encoding($true)))

Write-Output '5/5 清单 + 打包…'
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add('相对路径,SHA256,字节')
foreach($f in Get-ChildItem $dst -Recurse -File | Sort-Object FullName){
    $rel = $f.FullName.Substring($dst.Length + 1)
    $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
    $rows.Add($rel + ',' + $h + ',' + $f.Length)
}
[IO.File]::WriteAllLines((Join-Path $dst 'manifest-sha256.csv'), $rows, (New-Object Text.UTF8Encoding($true)))

$zip = Join-Path $OutputRoot ($name + '.zip')
if(Test-Path $zip){ Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dst '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Output ('安装包目录：' + $dst)
Write-Output ('压缩包    ：' + $zip + '  (' + [math]::Round((Get-Item $zip).Length/1MB,2) + ' MB)')
Write-Output ('文件数    ：' + (Get-ChildItem $dst -Recurse -File).Count)
Write-Output ('插件版本  ：' + $Version + '  (assembly ' + $Version + ')')
