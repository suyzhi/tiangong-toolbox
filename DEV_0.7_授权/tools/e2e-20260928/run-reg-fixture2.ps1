$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $root 'tools\desk-lib.ps1')
$out = 'C:\temp\tg-e2e\fixture-holecheck'
$log = Join-Path $out 'make-reg2.log.txt'
if (Test-Path $log) { Remove-Item $log -Force }
$bin = 'C:\temp\tg-e2e\bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $root 'build\Interop.TG.dll') $bin -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /platform:x64 /out:"$bin\MakeRegFixture2.exe" /reference:"$bin\Interop.TG.dll" (Join-Path $PSScriptRoot 'MakeRegFixture2.cs')
if ($LASTEXITCODE -ne 0) { Write-Output 'BUILD FAILED'; exit 1 }
Write-Output 'reg2 builder compiled'
$run = 'cmd.exe /c ""' + (Join-Path $bin 'MakeRegFixture2.exe') + '" "' + $out + '" > "' + $log + '" 2>&1"'
$procId = Start-OnDesk -DeskName 'TGWork070' -CommandLine $run -WorkDir $bin
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 600) { Start-Sleep -Milliseconds 800; if ((Get-Process -Id $procId -ErrorAction SilentlyContinue) -eq $null) { break } }
Write-Output ('reg2 builder done in ' + [int]$sw.Elapsed.TotalSeconds + ' s')
if (Test-Path $log) { Get-Content $log -Encoding UTF8 }
