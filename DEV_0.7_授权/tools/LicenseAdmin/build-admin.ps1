param(
    [string]$OutputDirectory,
    [string]$KeyFile
)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$bin=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $PSScriptRoot 'build'}
New-Item -Path $bin -ItemType Directory -Force | Out-Null
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@(
    (Join-Path $root 'src\License\LicensePlans.cs'),
    (Join-Path $root 'src\License\LicensePayload.cs'),
    (Join-Path $root 'src\License\LicenseCodec.cs'),
    (Join-Path $root 'src\License\LicenseSignature.cs'),
    (Join-Path $root 'src\License\LicenseKeyMaterial.cs'),
    (Join-Path $root 'src\License\LicenseKeySlot.cs'),
    (Join-Path $root 'src\License\LicenseMachine.cs'),
    (Join-Path $root 'src\License\LicenseHook.cs'),
    (Join-Path $root 'src\Admin\LicenseAdminKey.cs'),
    (Join-Path $root 'src\Admin\LicenseAdminMain.cs')
)
foreach($source in $sources){ if(!(Test-Path $source)){ throw ('缺少源文件：'+$source) } }
& $csc /nologo /target:exe /platform:x64 /optimize+ /out:"$bin\TianGongLicenseAdmin.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Management.dll $sources
if($LASTEXITCODE -ne 0){throw '管理员工具编译失败'}
$admin=Join-Path $bin 'TianGongLicenseAdmin.exe'
Write-Output ('管理员工具：' + $admin)
if($KeyFile){
    $key=[IO.Path]::GetFullPath($KeyFile)
    if(!(Test-Path $key)){ throw ('找不到私钥文件：'+$key) }
    & $admin machine
}
