# tools/fix-e2e/calib2.ps1 —— 自动标定：点 -> 读新窗口标题 -> 建立"命令名 -> 屏幕坐标"表。
# 与 calib.ps1 的区别：它自己判断点开的窗口是不是**本次**点出来的（比对 before/after），
# 并把命中结果写成 JSON，供正式用例复用。
$ErrorActionPreference='Continue'
$Root='C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.7_授权'
. (Join-Path $Root 'tools\mouse-e2e\lib.ps1')
$Desk='TGWork070'
$d=Get-Desk -Name $Desk
$main=Get-TgWindow -Desk $d.Handle -ClassLike 'EngineFrame*'
if($main -eq $null){ Write-Output 'NO CAD'; exit 1 }

function Strays { @(Get-DeskWindows -Desk $d.Handle -VisibleOnly | Where-Object { $_.Pid -eq $main.Pid -and $_.H -ne $main.H -and $_.Wd -gt 120 -and $_.Ht -gt 60 }) }
function CloseAll { foreach($w in (Strays)){ Close-StrayWindow -Hwnd $w.H | Out-Null }; Start-Sleep -Milliseconds 600 }
function Titles { (Strays | ForEach-Object { $_.T }) -join ' | ' }

CloseAll
Invoke-DeskJob -DeskName $Desk -Tag 'tab' -Ops @(@{ t='clickat'; x=($main.X+770); y=($main.Y+45) }, @{ t='sleep'; ms=2200 }) | Out-Null
CloseAll

# 目标：命令名（用于匹配窗口标题）-> 候选屏幕坐标（位图 + 20 偏移在调用处加）
$targets = @(
  @{ n='自动打孔';         m='自动打孔' },
  @{ n='批量排孔';         m='批量排孔' },
  @{ n='配孔检查';         m='配孔检查' },
  @{ n='批量格式转换';     m='批量格式转换' },
  @{ n='Lineup 模型标记';  m='Lineup' },
  @{ n='型材自动填充';     m='自动填充' },
  @{ n='生成矩形板';       m='生成矩形板' },
  @{ n='四面生成内嵌板';   m='生成矩形板' }
)
$found = @{}
# 粗略网格：先扫已知的 4 行 x 6 列
foreach($by in @(76,98,124,145)){
  foreach($bx in @(40,80,120,160,200,240,280,320,360,400)){
    $hit = $null
    foreach($t in $targets){
      if($found.ContainsKey($t.n)){ continue }
      CloseAll
      $before = Titles
      $sx=$main.X+$bx+20; $sy=$main.Y+$by+20
      Invoke-DeskJob -DeskName $Desk -Tag 'c' -Ops @(@{ t='clickat'; x=$sx; y=$sy }, @{ t='sleep'; ms=1800 }) | Out-Null
      $after = Titles
      if($after -ne $before -and $after -ne ''){
        $win = (@(Strays))[0].T
        if($win -like ('*' + $t.m + '*')){ $found[$t.n] = @($bx,$by,$win); $hit = $t.n; Write-Output ("HIT " + $t.n + " @ [" + $bx + "," + $by + "] -> " + $win); break }
      }
    }
    if($hit){ }
  }
}
CloseAll
Write-Output '--- 标定结果 ---'
foreach($k in $found.Keys){ Write-Output ("  " + $k + " -> bx=" + $found[$k][0] + " by=" + $found[$k][1] + "  (" + $found[$k][2] + ")") }
$out = @{}
foreach($k in $found.Keys){ $out[$k] = @($found[$k][0],$found[$k][1]) }
$out | ConvertTo-Json | Set-Content (Join-Path $Root 'artifacts\fix-e2e\cmdmap.json') -Encoding UTF8
Write-Output ('已写 ' + (Join-Path $Root 'artifacts\fix-e2e\cmdmap.json'))