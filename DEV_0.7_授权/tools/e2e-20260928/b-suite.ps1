param([string]$Names = 'B1')
$ErrorActionPreference = 'Continue'
$e2e = $PSScriptRoot
$fixture = 'C:\temp\tg-e2e\fixture-holecheck'
$pwshExe = 'C:\Program Files\PowerShell\7\pwsh.exe'
$openasm = 'C:\temp\tg-test\openasm3.ps1'
$dump = 'C:\temp\tg-test\dumpcheck.ps1'
$deskrun = 'C:\temp\tg-test\deskrun.ps1'
$list = $Names -split ',' | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() }
foreach ($n in $list) {
    Write-Output ('##### ' + $n)
    $asm = Join-Path $fixture ($n + '.asm')
    & $pwshExe -NoProfile -ExecutionPolicy Bypass -File $deskrun -Script $openasm -OutFile ('C:\temp\tg-test\' + $n + '-open.txt') -ChildArgs ('Path ' + $asm) 2>&1 | Out-Null
    $open = Get-Content ('C:\temp\tg-test\' + $n + '-open.txt') -Encoding UTF8
    Write-Output ('  ' + (($open | Where-Object { $_ -match '^ActiveDocument|^MATCH' }) -join '  '))
    & $pwshExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $e2e 'b-run.ps1') -Tag ('b-' + $n) 2>&1 | Where-Object { $_ -match '配孔检查窗口|开始检查按钮' } | ForEach-Object { Write-Output ('  ' + $_.Trim()) }
    & $pwshExe -NoProfile -ExecutionPolicy Bypass -File $deskrun -Script $dump -OutFile ('C:\temp\tg-test\' + $n + '-dump.txt') 2>&1 | Out-Null
    $df = ('C:\temp\tg-test\' + $n + '-dump.txt')
    if (Test-Path $df) {
        Get-Content $df -Encoding UTF8 | Where-Object { $_ -match 'LISTBOX|\[\d+\]|检查了' } | ForEach-Object { Write-Output ('  ' + $_.Trim()) }
    } else { Write-Output '  (无 dump)' }
}