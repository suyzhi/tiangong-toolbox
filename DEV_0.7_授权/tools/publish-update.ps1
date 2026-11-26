# tools/publish-update.ps1 —— 发布新版本：大家下次打开天工、点工具箱命令时会收到更新提示。
#
#   一条命令发版（先改 DEV_0.7_授权\VERSION，再）：
#     powershell -ExecutionPolicy Bypass -File DEV_0.7_授权\tools\publish-update.ps1 -Build -Notes "自动打孔：……`n修复：……"
#   已经打好安装包：
#     ... publish-update.ps1 -Zip 天工工具箱_DEV0.9.3_安装包_20261101.zip -NotesFile 更新说明.txt
#
# 也可以不用脚本：浏览器打开 https://license.suyzhi.icu/admin/ →「版本发布」上传 zip。
# 口令：不给 -PasswordFile 就当场问（输入不显示）。
param(
    [string]$Zip,                 # 安装包 zip；不给就用仓库根目录或 releases 里最新的 天工工具箱_DEV*_安装包_*.zip
    [switch]$Build,               # 先跑 make-installer.ps1 打一个新包
    [string]$Notes = '',          # 更新说明（用户在更新提示里看到），一行一条
    [string]$NotesFile,
    [string]$Version,             # 默认从文件名里的 DEV0.9.2 认
    [string]$Server = 'https://license.suyzhi.icu',
    [string]$PasswordFile         # 看板口令文件（第一行）
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $root -Parent
if ($Build) {
    & (Join-Path $PSScriptRoot 'make-installer.ps1') | Out-Host
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw '打包失败' }
}
if (-not $Zip) {
    $searchDirs = @($repo, (Join-Path $repo 'releases')) | Where-Object { Test-Path -LiteralPath $_ }
    $latestZip = $searchDirs | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Filter '天工工具箱_DEV*_安装包_*.zip' -File -ErrorAction SilentlyContinue
    } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latestZip) { throw '仓库根目录和 releases 里都找不到安装包 zip，请用 -Zip 指定或加 -Build' }
    $Zip = $latestZip.FullName
}
$Zip = (Resolve-Path -LiteralPath $Zip).Path
$name = [IO.Path]::GetFileName($Zip)
if (-not $Version) { if ($name -match 'DEV(\d+\.\d+\.\d+)') { $Version = $Matches[1] } else { throw '文件名里没有 DEVx.y.z，请用 -Version 指定' } }
if ($NotesFile) { $Notes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile).Path, [Text.Encoding]::UTF8) }
$Notes = $Notes -replace '`n', "`n"

if ($PasswordFile) { $pw = (Get-Content -LiteralPath $PasswordFile -TotalCount 1).Trim() }
else {
    $sec = Read-Host '看板口令' -AsSecureString
    $pw = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
}
Write-Output ('发布 ' + $Version + '：' + $name + '（' + [Math]::Round((Get-Item -LiteralPath $Zip).Length / 1MB, 2) + ' MB）→ ' + $Server)
$s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = Invoke-RestMethod -Method Post -Uri "$Server/admin/api/login" -WebSession $s -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes((@{ password = $pw } | ConvertTo-Json)))
# 更新说明走 X-TG-Notes 头（UTF-8 Base64），避免长中文说明超出 URL 长度上限或写进反代日志；
# 若 URL 编码后不超过 4000 字符则同时附带 &notes= 查询参数，兼容尚未部署 X-TG-Notes 支持的旧版服务端
$q = '?name=' + [Uri]::EscapeDataString($name) + '&version=' + [Uri]::EscapeDataString($Version)
$headers = @{ 'X-TG-CSRF' = $login.csrf }
if ($Notes) {
    $headers['X-TG-Notes'] = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Notes))
    $escapedNotes = [Uri]::EscapeDataString($Notes)
    if ($escapedNotes.Length -le 4000) { $q += '&notes=' + $escapedNotes }
}
try {
    $r = Invoke-RestMethod -Method Post -Uri ("$Server/admin/api/updates" + $q) -WebSession $s -Headers $headers `
        -ContentType 'application/zip' -InFile $Zip
} catch {
    $msg = $_.ErrorDetails.Message
    throw ('发布失败：' + $(if ($msg) { $msg } else { $_.Exception.Message }))
}
Write-Output ('已发布：' + $r.latest.version + '  ' + $r.latest.date + '  sha256 ' + $r.latest.sha256.Substring(0, 12) + '…')
Write-Output '大家下次打开天工后（后台约 20 秒查一次），点任意工具箱命令就会看到更新提示；功能区「检查更新」可以马上查。'
