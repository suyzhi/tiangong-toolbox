param([string]$Points = '1113,510;1129,510;1121,507;1113,509')
$ErrorActionPreference = 'Continue'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'mouse-e2e\lib.ps1')
$pwshExe = 'C:\Program Files\PowerShell\7\pwsh.exe'
$d = Get-Desk -Name $script:DeskName
$main = Get-CadMain -TimeoutSec 30 -Desk $d.Handle
foreach ($p in ($Points -split ';')) {
    $xy = $p -split ','
    $x = [int]$xy[0]; $y = [int]$xy[1]
    Invoke-Act -Tag ('pick-' + $x + '-' + $y) -Actions (@(@{ t='clickat'; x=$x; y=$y }, @{ t='sleep'; ms=2000 }) | ConvertTo-Json -Compress) | Out-Null
    $df = 'C:\temp\tg-test\pick-' + $x + '-' + $y + '.txt'
    & $pwshExe -NoProfile -ExecutionPolicy Bypass -File 'C:\temp\tg-test\deskrun.ps1' -Script 'C:\temp\tg-test\dumpcheck.ps1' -OutFile $df -ChildArgs 'TitlePart 自动打孔' 2>&1 | Out-Null
    $line = (Get-Content $df -Encoding UTF8 | Where-Object { $_ -match '共 \d+ 个孔' }) -join ' / '
    Write-Output ('click (' + $x + ',' + $y + ') -> ' + $line.Trim())
}
