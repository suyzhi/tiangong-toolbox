param(
    [Parameter(Mandatory=$true)][int64]$Hwnd,
    [string]$Tag = 'enum',
    [string]$Filter = ''
)
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$log = Invoke-DeskJob -DeskName $script:DeskName -Tag $Tag -Ops @(@{ t = 'enumchild'; hwnd = $Hwnd }) -TimeoutSec 90
$out = Join-Path $script:Out ($Tag + '.txt')
$log | Set-Content -LiteralPath $out -Encoding UTF8
Write-Output ('log -> ' + $out)
foreach ($l in $log) { if (-not $Filter -or $l -match $Filter) { Write-Output $l } }
