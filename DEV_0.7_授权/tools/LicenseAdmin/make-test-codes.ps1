param(
    [switch]$SkipBuild,
    [switch]$KeepTestKey
)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bin=Join-Path $root 'build\license-test'
$key=Join-Path $root 'tests\fixtures\license-test.tgkey'

$toolsRoot=Split-Path $PSScriptRoot -Parent
if(!$SkipBuild){
    & (Join-Path $toolsRoot 'build.ps1') -OutputDirectory $bin
    if($LASTEXITCODE -ne 0){throw '插件编译失败'}
}

# 1) 测试密钥：与插件内嵌公钥成对。首次生成后写入 LicenseKeySlot.cs，需要重新编译插件。
if(!(Test-Path $key)){
    if(!$SkipBuild){
        Write-Output '首次运行：生成测试密钥并写入插件公钥槽，需要再编译一次。'
        & (Join-Path $PSScriptRoot 'build-admin.ps1') | Out-Null
        $admin=Join-Path $PSScriptRoot 'build\TianGongLicenseAdmin.exe'
        & $admin keygen $key testmaster
        $slot=Join-Path $root 'tests\fixtures\license-test.public.cs'
        Copy-Item $slot (Join-Path $root 'src\License\LicenseKeySlot.cs') -Force
        & (Join-Path $toolsRoot 'build.ps1') -OutputDirectory $bin
        if($LASTEXITCODE -ne 0){throw '插件编译失败'}
    }
}
if(!(Test-Path $key)){throw '缺少测试密钥：' + $key}

$admin=Join-Path $PSScriptRoot 'build\TianGongLicenseAdmin.exe'
if(!(Test-Path $admin)){ & (Join-Path $PSScriptRoot 'build-admin.ps1') | Out-Null }

# 2) 用管理员工具按真实机器码签发三档真码，放进 build 目录供测试读取。
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