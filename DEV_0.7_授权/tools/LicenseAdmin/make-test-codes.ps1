param(
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$toolsRoot=Split-Path $PSScriptRoot -Parent
$bin=Join-Path $root 'build\license-test'
$key=Join-Path $root 'tests\fixtures\license-test.tgkey'
$slotSource=Join-Path $root 'tests\fixtures\license-test.public.cs'
$slotTarget=Join-Path $root 'src\License\LicenseKeySlot.cs'

# 管理员工具
$admin=Join-Path $PSScriptRoot 'build\TianGongLicenseAdmin.exe'
if(!(Test-Path $admin)){ & (Join-Path $PSScriptRoot 'build-admin.ps1') | Out-Null }

# 1) 测试密钥：缺就生成。私钥只留在 tests/fixtures（已 gitignore）。
if(!(Test-Path $key)){
    Write-Output '生成测试密钥（只用于本机测试，与生产私钥无关）…'
    & $admin keygen $key testmaster
}

# 2) 公钥槽必须与测试私钥成对，否则插件编译出来后验签必然失败。
$needSlot=$true
if(Test-Path $slotTarget){
    $current=(Select-String -Path $slotTarget -Pattern 'KeyId = "([a-z0-9]+)"').Matches
    if($current.Count -gt 0 -and $current[0].Groups[1].Value -eq 'testmaster'){ $needSlot=$false }
}
if($needSlot){
    if(!(Test-Path $slotSource)){ throw '找不到测试公钥源码：' + $slotSource }
    Write-Output '把测试公钥写入插件公钥槽（正式发布前必须换回 keygen 生成的公钥）…'
    Copy-Item $slotSource $slotTarget -Force
}

if(!$SkipBuild){
    & (Join-Path $toolsRoot 'build.ps1') -OutputDirectory $bin
    if($LASTEXITCODE -ne 0){throw '插件编译失败'}
}

# 3) 用管理员工具按真实机器码签发三档真码，供测试读取。
$machineLine=(& $admin machine | Select-String '本机机器码').ToString()
$machine=$machineLine.Substring($machineLine.IndexOf([char]0xFF1A)+1).Trim()
$codes=Join-Path $bin 'admin-codes'
Remove-Item $codes -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $codes | Out-Null
$plans=@(@('M','month'),@('H','half'),@('Y','year'))
foreach($plan in $plans){
    $code=(& $admin new $key $plan[0] $machine) | Where-Object { $_ -match '^[0-9a-z]{5}-' }
    if(!$code){ throw ('签发失败：' + $plan[0]) }
    [IO.File]::WriteAllText((Join-Path $codes ($plan[1]+'.txt')), $code, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Output ('机器码：' + $machine)
Write-Output ('已签发三档真码到：' + $codes)
Write-Output '提示：正式发布前把 src\License\LicenseKeySlot.cs 换回 keygen 生成的公钥。'
