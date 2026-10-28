# 后台排队跑真机探针：等用户的天工关掉（连续 2 分钟没有 TianGong 进程）再跑；
# 每个任务自己在私有桌面上起一个 CAD，任务开始前确认本机只有这一个 CAD；
# 跑的过程中一旦冒出别的 TianGong 进程（用户又打开了天工），立刻杀掉探针 exe 和自己起的 CAD —— 绝不碰用户的实例。
# 0.10.2 变体（-convert）：探针 / 转换器自己用 CreateInstance 拉起的天工（父进程 = svchost，DCOM）不算用户的实例；
#   每个任务结束时把任务期间 DCOM 拉起的天工收掉（不然留着的那个会让下一个任务一直"等用户的天工关闭"）。
#   要用 Windows PowerShell 5（powershell.exe）跑：pwsh 里 Get-Process -Id $null 是终止错误，会把队列整个打断。
param([string[]]$Jobs)
$root = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
$sp = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\cad-queue'; New-Item -ItemType Directory -Force -Path $sp | Out-Null
. (Join-Path $root 'tools\desk-lib.ps1')
$log = Join-Path $sp 'queue.log'
function Q([string]$s){ $line = (Get-Date).ToString('HH:mm:ss') + ' ' + $s; Add-Content -Path $log -Value $line -Encoding UTF8; Write-Output $line }

function WaitIdle {
  $quiet = $null
  while ($true) {
    $n = @(Get-Process -Name TianGong -ErrorAction SilentlyContinue).Count
    if ($n -eq 0) { if ($quiet -eq $null) { $quiet = Get-Date }; if (((Get-Date) - $quiet).TotalSeconds -ge 30) { return } }
    else { $quiet = $null }
    Start-Sleep -Seconds 10
  }
}

# 自己起的 CAD 会再派生 TianGong 子进程（父 = 自己的 CAD），那些不算用户的实例
function Others([int]$mine) {
  Get-Process -Name TianGong -ErrorAction SilentlyContinue | ? {
    $id = $_.Id; $seen = 0
    while ($id -and $id -ne $mine -and $seen -lt 8) { $id = (Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue).ParentProcessId; $seen++ }
    if ($id -eq $mine) { return $false }
    # 格式转换的 worker 用 CreateInstance 拉起的天工：父进程是 svchost（DCOM），不是用户双击开的（父 = explorer）
    $par = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $_.Id) -ErrorAction SilentlyContinue).ParentProcessId
    $pn = (Get-Process -Id $par -ErrorAction SilentlyContinue).ProcessName
    $pn -ne 'svchost'
  }
}

foreach ($job in $Jobs) {
  # 任务格式：exe路径|参数|输出目录名[|超时秒数（默认 1500）]
  $parts = $job.Split('|'); $exe = $parts[0]; $arg = $parts[1]; $tag = $parts[2]
  $limit = if ($parts.Count -gt 3 -and $parts[3]) { [int]$parts[3] } else { 1500 }
  Q ('等用户的天工关闭… 下一个任务 ' + $tag)
  WaitIdle
  $d = Get-Desk -Name 'TGWork'
  $cadPid = Start-OnDesk -DeskName 'TGWork' -CommandLine '"C:\Program Files\NDS\TianGong 2025\Program\TianGong.exe"' -WorkDir 'C:\Program Files\NDS\TianGong 2025\Program'
  Q ("[$tag] 私有桌面 CAD pid=$cadPid")
  $w = Wait-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*' -TimeoutSec 300
  if ($w -eq $null) { Q "[$tag] CAD 没起来，跳过"; Get-Process -Id $cadPid -ErrorAction SilentlyContinue | % { $_.Kill() }; continue }
  Start-Sleep -Seconds 25
  $others = @(Others $cadPid)
  if ($others.Count -gt 0) { Q "[$tag] 发现用户的天工 -> 不跑，收掉自己的 CAD"; Get-Process -Id $cadPid -ErrorAction SilentlyContinue | % { $_.Kill() }; continue }
  $out = Join-Path $root ('artifacts\' + $tag)
  New-Item -ItemType Directory -Force -Path $out | Out-Null
  $txt = Join-Path $out 'out.txt'
  if (Test-Path $txt) { Remove-Item $txt -Force }
  $run = 'cmd.exe /c ""' + $exe + '" "' + $out + '" ' + $arg + ' > "' + $txt + '" 2>&1"'
  $p = Start-OnDesk -DeskName 'TGWork' -CommandLine $run -WorkDir $out
  $jobStart = Get-Date
  $deadline = (Get-Date).AddSeconds($limit); $aborted = $false
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    if (-not (Get-Process -Id $p -ErrorAction SilentlyContinue)) { break }
    $others = @(Others $cadPid)
    if ($others.Count -gt 0) {
      $mineAlive = [bool](Get-Process -Id $cadPid -ErrorAction SilentlyContinue)
      foreach ($o in $others) { $ci = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $o.Id) -ErrorAction SilentlyContinue; Q ("[$tag] 另一个 TianGong pid=" + $o.Id + " 父=" + $ci.ParentProcessId + " 启动=" + $o.StartTime + " 命令行=" + $ci.CommandLine + " 会话=" + $o.SessionId) }
      Q ("[$tag] 自己的 CAD 还活着=" + $mineAlive + " -> 立刻停探针")
      Get-Process -Id $p -ErrorAction SilentlyContinue | % { $_.Kill() }
      Get-CimInstance Win32_Process -Filter "ParentProcessId=$p" -ErrorAction SilentlyContinue | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
      Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue | % { $_.Kill() }
      $aborted = $true; break
    }
  }
  # 超时还没跑完：多半是 CAD 弹了个对话框在等人点（私有桌面上看不见）—— 把可见窗口列出来、逐个截图再收
  if (-not $aborted -and (Get-Process -Id $p -ErrorAction SilentlyContinue)) {
    Q "[$tag] 超时（$limit 秒）—— 截私有桌面上的窗口"
    $k = 0
    foreach ($w in (Get-DeskWindows -Desk $d.Handle -VisibleOnly)) {
      if ($w.Wd -le 0 -or $w.Ht -le 0) { continue }
      Q ("[$tag]   窗口 pid=" + $w.Pid + " " + $w.Wd + "x" + $w.Ht + " cls=" + $w.C + " title=" + $w.T)
      $k++; try { [void](Save-TgShot -Hwnd $w.H -Path (Join-Path $out ("hang-" + $k + ".png"))) } catch { }
      if ($k -ge 8) { break }
    }
    Get-Process -Id $p -ErrorAction SilentlyContinue | % { $_.Kill() }
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue | % { $_.Kill() }
    $aborted = $true
  }
  Start-Sleep -Seconds 3
  Get-CimInstance Win32_Process -Filter "ParentProcessId=$cadPid" -ErrorAction SilentlyContinue | % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Get-Process -Id $cadPid -ErrorAction SilentlyContinue | % { $_.Kill() }
  Start-Sleep -Seconds 3
  $tail = if (Test-Path $txt) { (Get-Content $txt -Encoding Default | Select-String 'RESULT|PASS|FAIL|E2E|FATAL|异常' | Select-Object -Last 40) -join "`n" } else { '(没有输出)' }
  # 这个任务期间 DCOM 拉起的天工（探针 / 转换器 CreateInstance 的，父 = svchost、在任务开始之后启动）收掉，别留给下一个任务当"用户的天工"
  Get-Process -Name TianGong -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $jobStart } | ForEach-Object {
    $par = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $_.Id) -ErrorAction SilentlyContinue).ParentProcessId
    $pn = if ($par) { (Get-Process -Id $par -ErrorAction SilentlyContinue).ProcessName } else { '' }
    if ($pn -eq 'svchost') { Q ("[$tag] 收掉任务里 DCOM 拉起的天工 pid=" + $_.Id); $_.Kill() }
  }
  Q ("[$tag] 结束" + $(if ($aborted) { '（中止）' } else { '' }) + "`n" + $tail)
}
Q 'QUEUE DONE'
