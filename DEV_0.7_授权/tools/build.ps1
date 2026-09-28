param(
    [string]$CadHome='C:\Program Files\NDS\TianGong 2025',
    [string]$OutputDirectory,
    [string]$Version='0.7.0.0',
    # 开发构建：打开授权测试开关 / 开发密钥模式 / Diagnostics 测试入口，并编译 PanelTests.exe。
    # 默认（不加）是正式构建，交付包和"重新编译安装.cmd"都用它。开发构建绝不能发给别人。
    [switch]$DevBuild
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$bin=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'build'}
New-Item -Path $bin -ItemType Directory -Force | Out-Null
$interop=Join-Path $CadHome 'Program\TGAiHelper\Interop.TG.dll'
if(!(Test-Path $interop)){throw '找不到天工 CAD 接口库。请通过 -CadHome 指定安装目录。'}
Copy-Item -LiteralPath $interop -Destination $bin -Force

# 版本戳：程序集版本与 LicenseLibrary 里的常量必须同时写入，运行期自检会比较两者。
$parts=$Version.Split('.')
$stamp=@()
$stamp+='// 由 tools/build.ps1 生成，请勿手工修改。'
$stamp+='using System.Reflection;'
$stamp+='[assembly:AssemblyVersion("'+$Version+'")]'
$stamp+='[assembly:AssemblyFileVersion("'+$Version+'")]'
$stamp+='[assembly:AssemblyTitle("TianGongCadSuite")]'
$stamp+='namespace TianGongCadSuite.Licensing {'
$stamp+='    internal static class LicenseConstants {'
$stamp+='        internal const int Major = '+$parts[0]+';'
$stamp+='        internal const int Minor = '+$parts[1]+';'
$stamp+='    }'
$stamp+='}'
$stampPath=Join-Path $root 'src\License\LicenseBuild.cs'
[IO.File]::WriteAllText($stampPath, ($stamp -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($true)))

$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
# src\Admin 只属于管理员工具，绝不编进随插件分发的 DLL。
$sources=@(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\src\\Admin\\' } | ForEach-Object FullName)
$shared=@(
    '/nologo','/target:library','/platform:x64','/optimize+','/debug:full',
    ('/out:' + (Join-Path $bin 'TianGongCadSuite.dll')),
    ('/reference:' + $interop),
    '/reference:System.Web.Extensions.dll','/reference:System.Data.dll',
    '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',
    '/reference:Microsoft.CSharp.dll','/reference:System.Management.dll'
)
if($DevBuild){$shared+='/define:TG_DEV_BUILD'}
& $csc ($shared + $sources)
if($LASTEXITCODE -ne 0){throw '插件编译失败'}
& $csc /nologo /target:winexe /platform:x64 /out:"$bin\PanelLauncher.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Data.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'Launcher.cs')
if($LASTEXITCODE -ne 0){throw 'Launcher build failed'}
& $csc /nologo /target:exe /platform:x64 /out:"$bin\TrainingExportRunner.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'TrainingExportRunner.cs')
if($LASTEXITCODE -ne 0){throw 'Training export runner build failed'}
& $csc /nologo /target:winexe /platform:x64 /out:"$bin\TianGongConverter.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'ConverterMain.cs')
if($LASTEXITCODE -ne 0){throw 'TianGongConverter build failed'}
# 打孔工作器：插件在独立进程里调它完成写模型（进程内写会被 CAD 拒绝，见 HANDOVER 第 5 节）
& $csc /nologo /target:exe /platform:x64 /out:"$bin\TianGongDrillWorker.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Data.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'DrillWorker.cs')
if($LASTEXITCODE -ne 0){throw '打孔工作器编译失败'}
$tests=@(Get-ChildItem (Join-Path $root 'tests') -Filter '*.cs' -ErrorAction SilentlyContinue | ForEach-Object FullName)
# 测试程序依赖开发构建才有的内部成员与测试开关，正式构建不编译它；
# 同一输出目录里以前开发构建留下的测试程序和测试私钥也一并清掉，免得混进正式版。
if(-not $DevBuild){
    foreach($stale in 'PanelTests.exe','PanelTests.pdb','license-test.tgkey'){Remove-Item -LiteralPath (Join-Path $bin $stale) -Force -ErrorAction SilentlyContinue}
}
if($DevBuild -and $tests.Count){& $csc /nologo /target:exe /platform:x64 /out:"$bin\PanelTests.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:Microsoft.CSharp.dll /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll $tests;if($LASTEXITCODE -ne 0){throw '测试程序编译失败'}}

$fixture=Join-Path $root "tests\fixtures\lineup-sample.tsv"
if(Test-Path $fixture){Copy-Item -LiteralPath $fixture -Destination $bin -Force}

$seed=Join-Path $root "tests\fixtures\lineup-models-seed.xml"
if(Test-Path $seed){Copy-Item -LiteralPath $seed -Destination $bin -Force}

$key=Join-Path $root "tests\fixtures\license-test.tgkey"
if($DevBuild -and (Test-Path $key)){Copy-Item -LiteralPath $key -Destination (Join-Path $bin 'license-test.tgkey') -Force}

$kind=if($DevBuild){'开发构建（含测试开关，勿外发）'}else{'正式构建'}
Write-Output ('插件已编译：' + (Join-Path $bin 'TianGongCadSuite.dll') + '  版本 ' + $Version + '  ' + $kind)
