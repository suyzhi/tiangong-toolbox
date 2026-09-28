param(
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
# 源码根：一路向上找到含 src\License\LicenseKeySlot.cs 的那一层（原实现多退了一层，指向了仓库根）。
$root=$PSScriptRoot
while($root -and !(Test-Path (Join-Path $root 'src\License\LicenseKeySlot.cs'))){ $root=Split-Path $root -Parent }
if(!$root){ throw '找不到插件源码根目录（应含 src\License\LicenseKeySlot.cs）' }
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
    # 开发构建：随后要跑 PanelTests.exe --license（授权单测依赖测试开关）。这个目录里的 DLL 不要外发。
    & (Join-Path $toolsRoot 'build.ps1') -OutputDirectory $bin -DevBuild
    if($LASTEXITCODE -ne 0){throw '插件编译失败'}
}

# 3) 签发三档真码。新方案不收集用户机器码：签出来的是通用码，用户在目标机激活时绑定该机。
$codes=Join-Path $bin 'admin-codes'
Remove-Item $codes -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $codes | Out-Null
$plans=@(@('M','month'),@('H','half'),@('Y','year'))
foreach($plan in $plans){
    $out=& $admin new $key $plan[0] --note ('联调测试码 ' + $plan[1])
    # 注意：单元素管道结果是标量，直接 [0] 会取到字符串的首字符（踩过）。必须用 @() 包住。
    $code=(@($out | Where-Object { $_ -match '^[0-9a-z]{5}-' }))[0]
    $id=(($out | Where-Object { $_ -match '码ID' }) -replace '.*：','').Trim()
    if(!$code){ throw ('签发失败：' + $plan[0]) }
    [IO.File]::WriteAllText((Join-Path $codes ($plan[1]+'.txt')), $code, (New-Object System.Text.UTF8Encoding($false)))
    & $admin mark $key $id activated --note '联调：视为客户已回报激活' | Out-Null
    Write-Output ('  ' + $plan[1] + '：码ID ' + $id)
}
Write-Output ('已签发三档真码到：' + $codes)
& $admin list $key | Select-Object -Last 2
Write-Output '提示：正式发布前把 src\License\LicenseKeySlot.cs 换回生产 keygen 生成的公钥。'
