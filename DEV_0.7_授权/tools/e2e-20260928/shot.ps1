param([Parameter(Mandatory=$true)][int64]$Hwnd, [Parameter(Mandatory=$true)][string]$Name)
$ErrorActionPreference='Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
Save-TgShot -Hwnd $Hwnd -Path (Join-Path $script:Out ($Name + '.png')) -Flags 2 | Out-Null
Write-Output ('shot -> ' + (Join-Path $script:Out ($Name + '.png')))
Show-Windows
