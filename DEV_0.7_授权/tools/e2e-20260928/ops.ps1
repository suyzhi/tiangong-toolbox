param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [Parameter(Mandatory=$true)][string]$Ops,
    [int]$TimeoutSec = 90,
    [switch]$AlsoWindows
)
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$arr = @($Ops | ConvertFrom-Json)
$log = Invoke-DeskJob -DeskName $script:DeskName -Tag $Tag -Ops $arr -TimeoutSec $TimeoutSec
$logFile = Join-Path $script:Out ($Tag + '.log.txt')
$log | Set-Content -LiteralPath $logFile -Encoding UTF8
foreach ($l in $log) { Write-Output $l }
if ($AlsoWindows) { Write-Output '--- windows ---'; Show-Windows }
