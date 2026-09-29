# 授权管理.ps1 —— 双击就能用的"激活码签发"入口（管理员在本机用）。
# 设计注意（都踩过）：
#   1) 不要改 [Console]::OutputEncoding、不要用 Clear-Host —— 在中文 Windows 的 conhost 里
#      把控制台切到 UTF-8 再反复清屏，会出现"窗口一闪一闪、基本全黑"的渲染问题；
#   2) 循环读输入必须能识别 EOF：控制台输入不可用时 Read-Host 会立刻返回空，
#      直接循环就会疯狂刷屏；这里统一用 [Console]::ReadLine()，返回 $null 就退出。
param(
    [string]$Action,        # 可选：不带参数=菜单；带参数=直接执行一次（便于脚本调用）
    [string]$Plan,          # M/H/Y
    [string]$Note = "",
    [string]$Key = "",
    [int]$Count = 0
)
$ErrorActionPreference = "Stop"
$NL = [Environment]::NewLine
$Root = $PSScriptRoot
while ($Root -and -not (Test-Path (Join-Path $Root "src\License\LicenseKeySlot.cs"))) { $Root = Split-Path $Root -Parent }
if (-not $Root) { throw "找不到插件源码根目录（应含 src\License\LicenseKeySlot.cs）" }
$Admin = Join-Path $Root "tools\LicenseAdmin\build\TianGongLicenseAdmin.exe"
$KeyStore = Join-Path $env:LOCALAPPDATA "TianGongCadSuite\admin-key.txt"

function Say($s) { Write-Host $s }
function Line() { Write-Host ("-" * 68) }
function Get-DefaultKey {
    if ($Key) { return $Key }
    if (Test-Path $KeyStore) { $p = (Get-Content $KeyStore -TotalCount 1).Trim(); if ($p) { return $p } }
    $test = Join-Path $Root "tests\fixtures\license-test.tgkey"
    if (Test-Path $test) { return $test }
    return ""
}
function Save-DefaultKey($p) {
    New-Item -ItemType Directory -Force -Path (Split-Path $KeyStore -Parent) | Out-Null
    [IO.File]::WriteAllText($KeyStore, $p, (New-Object Text.UTF8Encoding($false)))
}
function Run-Admin([string[]]$argv) {
    # 见 license-admin-gui.ps1 里的说明：不用 Start-Process（环境变量坑），并按 UTF-8 解码子进程输出。
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
    return ($out + $err)
}
function Ask-Line($prompt) { Write-Host -NoNewline ($prompt + "："); return [Console]::ReadLine() }
function Pause-Line { $null = Ask-Line "回车返回菜单" }
function Save-CodeFile($text, $k) {
    $file = Join-Path (Split-Path -Parent $k) ("发码-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt")
    [IO.File]::WriteAllText($file, $text, (New-Object Text.UTF8Encoding($true)))
    return $file
}
function Show-Menu($k) {
    Line
    if ($k) {
        $ledger = [IO.Path]::ChangeExtension($k, ".ledger.tsv")
        Say ("  私钥文件： " + $k)
        Say ("  台账文件： " + $ledger + $(if (Test-Path $ledger) { "（已存在）" } else { "（还没有，签发时自动建）" }))
    } else {
        Say "  私钥文件： (未设置)"
    }
    Line
    Say "  [1] 签发一个激活码        [2] 批量签发"
    Say "  [3] 查看台账              [4] 校验 / 查一个码"
    Say "  [5] 作废一个码            [6] 登记状态（已激活 / 已作废）"
    Say "  [7] 更换私钥文件          [8] 生成生产密钥对（keygen）"
    Say "  [9] 档位说明              [0] 退出"
    Say ""
    Say "  也可以命令行直接用：授权管理.cmd new M 客户甲   /   授权管理.cmd list"
    Line
}

if (-not (Test-Path $Admin)) {
    Say "找不到管理员工具，正在编译…"
    & (Join-Path $Root "tools\LicenseAdmin\build-admin.ps1") | Out-Null
}
if (-not (Test-Path $Admin)) { Say ("仍然找不到：" + $Admin); $null = Ask-Line "回车退出"; exit 1 }
$key = Get-DefaultKey

# ---------- 带参数：直接执行一次，不进入菜单（可被批处理调用） ----------
if ($Action) {
    $a = $Action.Trim().ToLowerInvariant()
    if ($a -eq "keygen") { Say (Run-Admin @("keygen", $Key)); exit 0 }
    if ($a -eq "list" -or $a -eq "plans" -or $a -eq "public" -or $a -eq "machine") { Say (Run-Admin @($a, $key)); exit 0 }
    if ($a -eq "verify") { Say (Run-Admin @("verify", $key, $Plan)); exit 0 }
    if ($a -eq "new") {
        $argv = @("new", $key, $Plan.ToUpper())
        if ($Note) { $argv += @("--note", $Note) }
        $out = Run-Admin $argv; Say $out
        foreach ($l in ($out -split "\r?\n")) { if ($l -match "^激活码") { Say ("已另存： " + (Save-CodeFile $out $key)) } }
        exit 0
    }
    if ($a -eq "batch") {
        $argv = @("batch", $key, $Plan.ToUpper(), $(if ($Count -gt 0) { $Count } else { 1 }))
        if ($Note) { $argv += @("--note", $Note) }
        $out = Run-Admin $argv; Say $out
        if ($out.Length -gt 80) { Say ("已另存： " + (Save-CodeFile $out $key)) }
        exit 0
    }
    Say ("未知参数：" + $Action)
    exit 1
}

# ---------- 交互菜单 ----------
Say "天工工具箱 激活码管理（管理员用）"
Show-Menu $key
$empty = 0
while ($true) {
    $line = Ask-Line "请选择"
    if ($null -eq $line) { Say ""; Say "（读不到控制台输入，退出）"; exit 0 }
    $choice = $line.Trim()
    if (-not $choice) { $empty++; if ($empty -ge 3) { Say "（连续回车，退出）"; exit 0 }; continue }
    $empty = 0
    try {
        switch ($choice) {
            "1" {
                if (-not (Test-Path $key)) { Say "私钥文件不存在：先用 [7] 指定，或用 [8] 生成一对新的。"; Pause-Line; break }
                $plan = "$(Ask-Line "档位  M=一个月  H=半年  Y=一年")".Trim().ToUpper()
                if ($plan -notin @("M","H","Y")) { Say "档位只能是 M / H / Y"; Pause-Line; break }
                $note = "$(Ask-Line "备注（发给谁 / 用途，可留空）")".Trim()
                $argv = @("new", $key, $plan); if ($note) { $argv += @("--note", $note) }
                $out = Run-Admin $argv
                Say ""; Say $out
                foreach ($l in ($out -split "\r?\n")) {
                    if ($l -match "^激活码") {
                        $f = Save-CodeFile $out $key
                        Line; Say "激活码已另存为文本（可整段复制发给客户）："; Say ("  " + $f)
                        $o = "$(Ask-Line "现在用记事本打开它？(Y/N)")".Trim()
                        if ($o -match "^(y|Y|是)$") { Start-Process notepad.exe $f }
                    }
                }
                Pause-Line
            }
            "2" {
                if (-not (Test-Path $key)) { Say "私钥文件不存在。"; Pause-Line; break }
                $plan = "$(Ask-Line "档位  M=一个月  H=半年  Y=一年")".Trim().ToUpper()
                if ($plan -notin @("M","H","Y")) { Say "档位只能是 M / H / Y"; Pause-Line; break }
                $n = "$(Ask-Line "签几个")".Trim()
                $note = "$(Ask-Line "备注（可留空）")".Trim()
                $argv = @("batch", $key, $plan, $n); if ($note) { $argv += @("--note", $note) }
                $out = Run-Admin $argv; Say ""; Say $out
                if ($out.Length -gt 80) { $f = Save-CodeFile $out $key; Line; Say ("已另存为：" + $f); $o = "$(Ask-Line "现在用记事本打开它？(Y/N)")".Trim(); if ($o -match "^(y|Y|是)$") { Start-Process notepad.exe $f } }
                Pause-Line
            }
            "3" { if (-not (Test-Path $key)) { Say "私钥文件不存在。"; Pause-Line; break }; Say ""; Say (Run-Admin @("list", $key)); Pause-Line }
            "4" {
                $code = "$(Ask-Line "把激活码整段粘进来")".Trim()
                $out = if (Test-Path $key) { Run-Admin @("verify", $key, $code) } else { Run-Admin @("verify", $code) }
                Say ""; Say $out; Pause-Line
            }
            "5" {
                if (-not (Test-Path $key)) { Say "私钥文件不存在。"; Pause-Line; break }
                $id = "$(Ask-Line "要作废的码ID（形如 abcd-efgh）")".Trim()
                $note = "$(Ask-Line "原因（可留空）")".Trim()
                $argv = @("revoke", $key, $id); if ($note) { $argv += @("--note", $note) }
                Say ""; Say (Run-Admin $argv)
                Line; Say "提醒：作废是「随版本生效」的 —— 要重新编译插件/安装包并发给客户，才会真正拦住。"
                Pause-Line
            }
            "6" {
                if (-not (Test-Path $key)) { Say "私钥文件不存在。"; Pause-Line; break }
                $id = "$(Ask-Line "码ID（形如 abcd-efgh）")".Trim()
                $state = "$(Ask-Line "登记成：issued=已签发 / activated=客户已激活 / void=作废")".Trim().ToLower()
                if ($state -notin @("issued","activated","void")) { Say "只能是 issued / activated / void"; Pause-Line; break }
                $note = "$(Ask-Line "备注（可留空）")".Trim()
                $argv = @("mark", $key, $id, $state); if ($note) { $argv += @("--note", $note) }
                Say ""; Say (Run-Admin $argv); Pause-Line
            }
            "7" {
                $p = "$(Ask-Line "私钥文件完整路径（*.tgkey）")".Trim().Trim([char]34)
                if (Test-Path $p) { $key = $p; Save-DefaultKey $key; Say ("已记住：" + $key) } else { Say "这个路径下没有文件。" }
                Pause-Line
            }
            "8" {
                Say "生成生产密钥对：建议私钥放 U 盘或非仓库目录；公钥源码写到同名的 .public.cs。"
                $p = "$(Ask-Line "私钥输出路径，例如 D:\keys\master.tgkey")".Trim().Trim([char]34)
                if (-not $p) { break }
                $id = "$(Ask-Line "密钥ID（可留空，默认 master）")".Trim()
                $argv = @("keygen", $p); if ($id) { $argv += @($id) }
                Say ""; Say (Run-Admin $argv)
                Line
                Say "下一步：把公钥源码覆盖进插件，然后重编译重打包："
                Say ("  copy " + [IO.Path]::ChangeExtension($p, ".public.cs") + " " + (Join-Path $Root "src\License\LicenseKeySlot.cs"))
                Say ("  " + (Join-Path $Root "tools\make-installer.ps1") + " -Version 0.7.2")
                Say "换完公钥后，只有这把私钥签的码能用；之前用测试密钥签的码全部失效。"
                Pause-Line
            }
            "9" { Say ""; Say (Run-Admin @("plans")); Pause-Line }
            "0" { return }
            default { Say "没有这个选项（0-9）" }
        }
    } catch {
        Say ("出错：" + $_.Exception.Message)
    }
    Say ""
}
