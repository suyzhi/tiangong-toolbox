# tools/make-delivery.ps1 —— 打交付包：交付_DEV_0.7.0_<日期>/ + 同名 zip。
# 只放源码、二进制、脚本和文档；不放 build/、artifacts/ 和客户样表。
param([string]$Version='0.7.0')
$ErrorActionPreference='Stop'
$root = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $root -Parent
$stamp = Get-Date -Format 'yyyyMMdd'
$name = '交付_DEV_' + $Version + '_' + $stamp
$dst = Join-Path $repo $name
if(Test-Path $dst){ Remove-Item $dst -Recurse -Force }

Write-Output '1/6 编译（正式构建）…'
# 不加 -DevBuild：交付物里不能有授权测试开关、开发密钥模式和测试入口。
& (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
$bin = Join-Path $root 'build'

Write-Output '2/6 拷贝二进制…'
$payload = Join-Path $dst 'payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
# PanelTests.exe 不再随交付包发出：它只在开发构建里编译，且会被插件加载进 CAD 进程运行。
foreach($f in 'TianGongCadSuite.dll','TianGongCadSuite.pdb','Interop.TG.dll','PanelLauncher.exe','TianGongConverter.exe','TrainingExportRunner.exe'){
  $src = Join-Path $bin $f
  if(Test-Path $src){ Copy-Item $src $payload -Force } else { Write-Output ('  （缺少 ' + $f + '，跳过）') }
}

Write-Output '3/6 拷贝源码 / 测试 / 脚本…'
foreach($d in 'src','tests','tools'){
  $target = Join-Path $dst $d
  New-Item -ItemType Directory -Force -Path $target | Out-Null
  Copy-Item (Join-Path $root ($d + '\*')) $target -Recurse -Force
}

Write-Output '4/6 拷贝文档…'
foreach($f in '使用说明.md','AUTO-HOLE-UI.md','AUTO-HOLE.md','AUTO-HOLE-测试指南.md','AUTO-HOLE-VALIDATION-20260925.md','AUTO-HOLE-VALIDATION-20260924.md','README.md'){
  $src = Join-Path $root $f
  if(Test-Path $src){ Copy-Item $src $dst -Force }
}

Write-Output '5/6 写安装脚本与说明…'
$utf8 = New-Object System.Text.UTF8Encoding($true)
function Write-Cmd([string]$file, [string]$body){
  [IO.File]::WriteAllText((Join-Path $dst $file), "@echo off`r`nsetlocal`r`n" + $body + "`r`necho.`r`npause`r`n", [Text.Encoding]::Default)
}
Write-Cmd '安装.cmd' 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" -LibraryPath "%~dp0payload\TianGongCadSuite.dll"'
Write-Cmd '卸载.cmd' 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" -Uninstall'
# 这一份是 .cmd，由 cmd.exe 执行：不能写 PowerShell 的 ';' 和 if(){ } —— 整串会被当成 -File 的实参，
# 报 "file does not have a .ps1 extension"（实测踩过）。必须写成分行的 cmd 语句。
$rebuild = @'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build.ps1"
if errorlevel 1 (
echo.
echo 编译失败，未安装。
goto :eof
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" -LibraryPath "%~dp0build\TianGongCadSuite.dll"
'@
Write-Cmd '重新编译安装.cmd' $rebuild

Write-Output '6/6 计算清单 + 打包…'
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add('相对路径,SHA256,字节')
foreach($f in Get-ChildItem $dst -Recurse -File | Sort-Object FullName){
  $rel = $f.FullName.Substring($dst.Length + 1)
  $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
  $rows.Add($rel + ',' + $h + ',' + $f.Length)
}
[IO.File]::WriteAllLines((Join-Path $dst 'manifest-sha256.csv'), $rows, $utf8)

$zip = Join-Path $repo ($name + '.zip')
if(Test-Path $zip){ Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dst '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Output ('交付目录：' + $dst)
Write-Output ('压缩包  ：' + $zip + '  (' + [math]::Round((Get-Item $zip).Length/1MB,2) + ' MB)')
Write-Output ('文件数  ：' + (Get-ChildItem $dst -Recurse -File).Count)
