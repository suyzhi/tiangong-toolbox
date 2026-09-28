param([string]$Names = 'B1')
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$e2e = $PSScriptRoot
$out = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权\artifacts\mouse-e2e'
$pwshExe = 'C:\Program Files\PowerShell\7\pwsh.exe'
$list = $Names -split ',' | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() }
foreach ($n in $list) {
    Write-Output ('##### ' + $n)
    $asm = if ($n -match '[\\\\/]') { $n } else { 'C:\temp\tg-e2e\fixture-holecheck\' + $n + '.asm' }
    & $pwshExe -NoProfile -ExecutionPolicy Bypass -File 'C:\temp\tg-test\deskrun.ps1' -Script 'C:\temp\tg-test\openasm3.ps1' -OutFile ('C:\temp\tg-test\' + $n + '-open.txt') -ChildArgs ('Path ' + $asm) 2>&1 | Out-Null
    Get-Content ('C:\temp\tg-test\' + $n + '-open.txt') -Encoding UTF8 | Where-Object { $_ -match 'ActiveDocument|MATCH|FAILED' } | ForEach-Object { Write-Output ('  ' + $_) }
    & $pwshExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $e2e 'b-run.ps1') -Tag ('b-' + $n) 2>&1 | Where-Object { $_ -match '^(PANEL|RUNBTN|NOPANEL|NOCAD|NOBUTTON|CLOSED_PANEL|OLDPANEL_STILL_OPEN|RETRY_OPEN|MODAL|DUMP|SHOT)' } | ForEach-Object { Write-Output ('  ' + $_.Trim()) }
    $pf = Join-Path $out ('b-' + $n + '-panel.txt')
    if (Test-Path $pf) {
        Get-Content $pf -Encoding UTF8 | Where-Object { $_ -match 'LISTBOX|\[\d+\]|检查了|wins=' } | ForEach-Object { Write-Output ('  ' + $_.Trim()) }
    } else { Write-Output '  (no panel dump)' }
}