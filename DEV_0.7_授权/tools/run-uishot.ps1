# tools/run-uishot.ps1 —— 在私有桌面上把"自动打孔"窗口渲染成 PNG，不打扰用户桌面。
$ErrorActionPreference='Continue'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'desk-lib.ps1')
$out = Join-Path $root 'artifacts\uishot'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'uishot.log.txt'
if(Test-Path $log){ Remove-Item $log -Force }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin = Join-Path $root 'build\uishot'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $root 'build\Interop.TG.dll') $bin -Force
Copy-Item (Join-Path $root 'build\TianGongCadSuite.dll') $bin -Force
& $csc /nologo /target:exe /platform:x64 /out:"$bin\UiShot.exe" /reference:"$bin\Interop.TG.dll" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'UiShot.cs')
if($LASTEXITCODE -ne 0){ Write-Output 'UiShot 编译失败'; exit 1 }
Write-Output 'UiShot 编译完成'

Get-ChildItem $out -Filter *.png | Remove-Item -Force -ErrorAction SilentlyContinue
$run = 'cmd.exe /c ""' + (Join-Path $bin 'UiShot.exe') + '" "' + $out + '" > "' + $log + '" 2>&1"'
$p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
Write-Output ('UiShot 进程 pid=' + $p)
$deadline = (Get-Date).AddSeconds(240)
while((Get-Date) -lt $deadline){
  Start-Sleep -Seconds 2
  $done = (Test-Path $log) -and ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match 'UISHOT DONE|UISHOT FATAL')
  if($done){ break }
  if(-not (Get-Process -Id $p -ErrorAction SilentlyContinue)){ break }
}
Write-Output '--- 输出 ---'
if(Test-Path $log){ Get-Content $log -Encoding UTF8 }
Get-ChildItem $out -Filter *.png | Select-Object Name,Length | Format-Table -AutoSize | Out-String
