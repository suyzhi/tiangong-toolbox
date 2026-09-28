param(
    [string]$DeskName = 'TGWork070',
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent   # DEV_0.7_授权
. (Join-Path $root 'tools\desk-lib.ps1')
$out = 'C:\temp\tg-e2e\fixture-holecheck'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'make.log.txt'
if (Test-Path $log) { Remove-Item $log -Force }
$bin = 'C:\temp\tg-e2e\bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
Copy-Item (Join-Path $root 'build\Interop.TG.dll') $bin -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /platform:x64 /out:"$bin\MakeHoleCheckFixture.exe" /reference:"$bin\Interop.TG.dll" (Join-Path $PSScriptRoot 'MakeHoleCheckFixture.cs')
if ($LASTEXITCODE -ne 0) { Write-Output 'FIXTURE BUILD FAILED'; exit 1 }
Write-Output 'fixture builder compiled'
$run = 'cmd.exe /c ""' + (Join-Path $bin 'MakeHoleCheckFixture.exe') + '" "' + $out + '" > "' + $log + '" 2>&1"'
$procId = Start-OnDesk -DeskName $DeskName -CommandLine $run -WorkDir $bin
Write-Output ('builder pid=' + $procId)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds 800
    if ((Get-Process -Id $procId -ErrorAction SilentlyContinue) -eq $null) { break }
}
Write-Output ('builder finished after ' + [int]$sw.Elapsed.TotalSeconds + ' s')
if (Test-Path $log) { Get-Content $log -Encoding UTF8 }
else { Write-Output 'NO LOG' }
Get-ChildItem $out | Select-Object Length,Name | Format-Table -AutoSize | Out-String -Width 120
