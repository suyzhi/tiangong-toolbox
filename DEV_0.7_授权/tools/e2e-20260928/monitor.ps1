param([string]$TitlePart = '批量格式转换', [int]$Minutes = 8, [int]$EverySec = 15)
$ErrorActionPreference = 'Continue'
$pwshExe = 'C:\Program Files\PowerShell\7\pwsh.exe'
$deadline = (Get-Date).AddMinutes($Minutes)
$last = ''
while ((Get-Date) -lt $deadline) {
    $df = 'C:\temp\tg-test\mon.txt'
    & $pwshExe -NoProfile -ExecutionPolicy Bypass -File 'C:\temp\tg-test\deskrun.ps1' -Script 'C:\temp\tg-test\dumpcheck.ps1' -OutFile $df -ChildArgs ('TitlePart ' + $TitlePart) 2>&1 | Out-Null
    $line = ((Get-Content $df -Encoding UTF8) | Where-Object { $_ -match '进度' }) -join ' | '
    $line = ($line -replace 'WindowsForms10.STATIC.app.0.1f71e64_r141_ad1 hwnd=\d+ rect=[\d,]+ \d+x\d+ = ','').Trim()
    if ($line -ne $last) { Write-Output ((Get-Date -Format 'HH:mm:ss') + '  ' + $line); $last = $line }
    if ($line -match '进度\s+(\d+)\s*/\s*(\d+)' ) {
        $done = [int]$Matches[1]; $total = [int]$Matches[2]
        if ($total -gt 0 -and $done -ge $total) { Write-Output 'DONE-ALL'; break }
    }
    if ($line -match '完成|失败|已停止|出错') { if ($line -match '失败\s+[1-9]') { Write-Output 'HAS-FAIL'; break } }
    Start-Sleep -Seconds $EverySec
}
Write-Output '--- X: 内容 ---'
Get-ChildItem 'X:\' -Recurse -ErrorAction SilentlyContinue | Select-Object Length,FullName | Format-Table -AutoSize | Out-String -Width 200
