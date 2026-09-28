$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root 'tools\desk-lib.ps1')
$out = 'C:\temp\tg-e2e\fixture-holecheck'
$log = Join-Path $out 'make-reg.log.txt'
if (Test-Path $log) { Remove-Item $log -Force }
$bin = 'C:\temp\tg-e2e\bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $root 'build\Interop.TG.dll') $bin -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /platform:x64 /out:"$bin\MakeRegFixture.exe" /reference:"$bin\Interop.TG.dll" (Join-Path $PSScriptRoot 'MakeRegFixture.cs')
if ($LASTEXITCODE -ne 0) { Write-Output 'BUILD FAILED'; exit 1 }
Write-Output 'reg builder compiled'
$srcRoot = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\cone-spike-01'
$run = 'cmd.exe /c ""' + (Join-Path $bin 'MakeRegFixture.exe') + '" "' + $out + '" "' + (Join-Path $srcRoot 'cbore-thru-30.par') + '" "' + (Join-Path $srcRoot 'tap-thru-30.par') + '" > "' + $log + '" 2>&1"'
$procId = Start-OnDesk -DeskName 'TGWork070' -CommandLine $run -WorkDir $bin
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 600) { Start-Sleep -Milliseconds 800; if ((Get-Process -Id $procId -ErrorAction SilentlyContinue) -eq $null) { break } }
Write-Output ('reg builder done in ' + [int]$sw.Elapsed.TotalSeconds + ' s')
if (Test-Path $log) { Get-Content $log -Encoding UTF8 }