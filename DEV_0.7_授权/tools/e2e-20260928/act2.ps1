param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [Parameter(Mandatory=$true)][string]$Actions,
    [int]$WaitMs = 1500,
    [switch]$NoShot,
    [switch]$NoWinList
)
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$log = Invoke-Act -Tag $Tag -Actions $Actions -WaitMs $WaitMs
$logFile = Join-Path $script:Out ($Tag + '.log.txt')
$log | Set-Content -LiteralPath $logFile -Encoding UTF8
Write-Output ('log -> ' + $logFile)
if (-not $NoShot) { Save-Shot -Name $Tag | Out-Null }
if (-not $NoWinList) { Write-Output '--- windows ---'; Show-Windows }
