# 激活码签发小窗（管理员用）—— 双击 授权管理.cmd 打开的就是它。
# 为什么做成窗口而不是控制台菜单：中文 Windows 的 conhost 在控制台里做交互很容易出幺蛾子
# （切 UTF-8 代码页 / 反复清屏会导致"窗口一闪一闪"，输入被重定向时读输入还会空转刷屏）。
# 这里用 WinForms：不依赖控制台、不闪屏、输入输出都看得见。
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$Root = $PSScriptRoot
while ($Root -and -not (Test-Path (Join-Path $Root "src\License\LicenseKeySlot.cs"))) { $Root = Split-Path $Root -Parent }
$Admin = Join-Path $Root "tools\LicenseAdmin\build\TianGongLicenseAdmin.exe"
$KeyStore = Join-Path $env:LOCALAPPDATA "TianGongCadSuite\admin-key.txt"

function Get-DefaultKey {
    if (Test-Path $KeyStore) { $p = (Get-Content $KeyStore -TotalCount 1).Trim(); if ($p) { return $p } }
    $t = Join-Path $Root "tests\fixtures\license-test.tgkey"
    if (Test-Path $t) { return $t }
    return ""
}
function Remember-Key($p) {
    New-Item -ItemType Directory -Force -Path (Split-Path $KeyStore -Parent) | Out-Null
    [IO.File]::WriteAllText($KeyStore, $p, (New-Object Text.UTF8Encoding($false)))
}
# 调后台 exe 并取回输出。两个坑都踩过：
#   1) 不能用 Start-Process —— 环境变量里同时有 NO_PROXY 和 no_proxy 时，PowerShell 5.1 组装子进程环境会抛
#      「已添加项。字典中的关键字: "NO_PROXY"」，在窗口程序里会直接变成未处理异常弹框；
#   2) 后台 exe 是按 UTF-8 写标准输出的，必须显式按 UTF-8 解码，否则中文在 GBK 控制台/窗口里全是乱码。
function Invoke-Admin([string[]]$argv) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Admin
    $psi.Arguments = (($argv | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"','\"') + '"' } else { "$_" } }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $out = $proc.StandardOutput.ReadToEnd()
    $err = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    return @{ Code = $proc.ExitCode; Text = ($out + $err) }
}
function Save-CodeFile($text, $k) {
    $file = Join-Path (Split-Path -Parent $k) ("发码-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt")
    [IO.File]::WriteAllText($file, $text, (New-Object Text.UTF8Encoding($true)))
    return $file
}

if (-not (Test-Path $Admin)) {
    $null = [System.Windows.Forms.MessageBox]::Show("找不到管理员工具，正在编译…（请稍候）","天工工具箱",[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Information)
    & (Join-Path $Root "tools\LicenseAdmin\build-admin.ps1") | Out-Null
}
if (-not (Test-Path $Admin)) {
    $null = [System.Windows.Forms.MessageBox]::Show("仍找不到：" + $Admin,"天工工具箱",[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Error)
    exit 1
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "天工工具箱 激活码签发（管理员）"
$form.ClientSize = New-Object System.Drawing.Size(700, 560)
$form.StartPosition = "CenterScreen"
$form.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

$lblKey = New-Object System.Windows.Forms.Label
$lblKey.Text = "私钥文件"; $lblKey.Location = New-Object System.Drawing.Point(16, 18); $lblKey.AutoSize = $true
$txtKey = New-Object System.Windows.Forms.TextBox
$txtKey.Location = New-Object System.Drawing.Point(110, 15); $txtKey.Size = New-Object System.Drawing.Size(470, 25); $txtKey.Text = (Get-DefaultKey)
$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text = "浏览…"; $btnBrowse.Location = New-Object System.Drawing.Point(590, 14); $btnBrowse.Size = New-Object System.Drawing.Size(90, 26)
$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = "私钥文件 (*.tgkey)|*.tgkey|所有文件 (*.*)|*.*"
    $dir = Split-Path -Parent $txtKey.Text
    if ($dir -and (Test-Path $dir)) { $dlg.InitialDirectory = $dir }
    if ($dlg.ShowDialog() -eq "OK") { $txtKey.Text = $dlg.FileName; Remember-Key $dlg.FileName }
})

$lblPlan = New-Object System.Windows.Forms.Label
$lblPlan.Text = "档位"; $lblPlan.Location = New-Object System.Drawing.Point(16, 56); $lblPlan.AutoSize = $true
$cboPlan = New-Object System.Windows.Forms.ComboBox
$cboPlan.DropDownStyle = "DropDownList"; $cboPlan.Location = New-Object System.Drawing.Point(110, 53); $cboPlan.Size = New-Object System.Drawing.Size(150, 25)
$null = $cboPlan.Items.Add("M  一个月（30 天）")
$null = $cboPlan.Items.Add("H  半年（183 天）")
$null = $cboPlan.Items.Add("Y  一年（365 天）")
$cboPlan.SelectedIndex = 0

$lblCount = New-Object System.Windows.Forms.Label
$lblCount.Text = "数量"; $lblCount.Location = New-Object System.Drawing.Point(290, 56); $lblCount.AutoSize = $true
$numCount = New-Object System.Windows.Forms.NumericUpDown
$numCount.Location = New-Object System.Drawing.Point(340, 53); $numCount.Size = New-Object System.Drawing.Size(70, 25); $numCount.Minimum = 1; $numCount.Maximum = 100; $numCount.Value = 1

$lblNote = New-Object System.Windows.Forms.Label
$lblNote.Text = "备注"; $lblNote.Location = New-Object System.Drawing.Point(440, 56); $lblNote.AutoSize = $true
$txtNote = New-Object System.Windows.Forms.TextBox
$txtNote.Location = New-Object System.Drawing.Point(480, 53); $txtNote.Size = New-Object System.Drawing.Size(200, 25)

$btnNew = New-Object System.Windows.Forms.Button
$btnNew.Text = "生成激活码"; $btnNew.Location = New-Object System.Drawing.Point(110, 92); $btnNew.Size = New-Object System.Drawing.Size(150, 34)
$btnList = New-Object System.Windows.Forms.Button
$btnList.Text = "查看台账"; $btnList.Location = New-Object System.Drawing.Point(270, 92); $btnList.Size = New-Object System.Drawing.Size(110, 34)
$btnCopy = New-Object System.Windows.Forms.Button
$btnCopy.Text = "复制结果"; $btnCopy.Location = New-Object System.Drawing.Point(390, 92); $btnCopy.Size = New-Object System.Drawing.Size(110, 34)
$btnPlans = New-Object System.Windows.Forms.Button
$btnPlans.Text = "档位说明"; $btnPlans.Location = New-Object System.Drawing.Point(510, 92); $btnPlans.Size = New-Object System.Drawing.Size(110, 34)

$txtOut = New-Object System.Windows.Forms.TextBox
$txtOut.Location = New-Object System.Drawing.Point(16, 138); $txtOut.Size = New-Object System.Drawing.Size(668, 380)
$txtOut.Multiline = $true; $txtOut.ScrollBars = "Both"; $txtOut.ReadOnly = $true; $txtOut.WordWrap = $false
$txtOut.Font = New-Object System.Drawing.Font("Consolas", 9)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Location = New-Object System.Drawing.Point(16, 528); $lblStatus.Size = New-Object System.Drawing.Size(668, 22); $lblStatus.Text = "未绑定机器的通用码：用户在自己机器上激活时自动绑定该机，不需要他提供机器码。"

$form.Controls.AddRange(@($lblKey, $txtKey, $btnBrowse, $lblPlan, $cboPlan, $lblCount, $numCount, $lblNote, $txtNote, $btnNew, $btnList, $btnCopy, $btnPlans, $txtOut, $lblStatus))

$btnNew.Add_Click({ try {
    $key = $txtKey.Text.Trim()
    if (-not (Test-Path $key)) { $null = [System.Windows.Forms.MessageBox]::Show("私钥文件不存在：" + $key); return }
    $plan = $cboPlan.SelectedItem.ToString().Substring(0,1)
    $note = $txtNote.Text.Trim()
    $n = [int]$numCount.Value
    $argv = if ($n -le 1) { @("new", $key, $plan) } else { @("batch", $key, $plan, $n) }
    if ($note) { $argv += @("--note", $note) }
    $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    try { $r = Invoke-Admin $argv } finally { $form.Cursor = [System.Windows.Forms.Cursors]::Default }
    $txtOut.Text = $r.Text
    if ($r.Code -eq 0) {
        $f = Save-CodeFile $r.Text $key
        $lblStatus.Text = "已生成，并把结果另存为：" + $f
        Remember-Key $key
    } else {
        $lblStatus.Text = "出错了（退出码 " + $r.Code + "），请看上面的输出。"
    }
  } catch { $lblStatus.Text = "出错：" + $_.Exception.Message; $null = [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "天工工具箱") } })
$btnList.Add_Click({ try {
    $key = $txtKey.Text.Trim()
    if (-not (Test-Path $key)) { $null = [System.Windows.Forms.MessageBox]::Show("私钥文件不存在：" + $key); return }
    $r = Invoke-Admin @("list", $key); $txtOut.Text = $r.Text; $lblStatus.Text = "台账文件：" + [IO.Path]::ChangeExtension($key, ".ledger.tsv")
  } catch { $lblStatus.Text = "出错：" + $_.Exception.Message } })
$btnPlans.Add_Click({ try { $r = Invoke-Admin @("plans"); $txtOut.Text = $r.Text } catch { $lblStatus.Text = "出错：" + $_.Exception.Message } })
$btnCopy.Add_Click({
    if ($txtOut.Text) { [System.Windows.Forms.Clipboard]::SetText($txtOut.Text); $lblStatus.Text = "已复制到剪贴板（可以直接粘给客户）。" }
})

$null = $form.ShowDialog()
