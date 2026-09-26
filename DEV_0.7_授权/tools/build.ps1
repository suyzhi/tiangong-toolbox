param(
    [string]$CadHome='C:\Program Files\NDS\TianGong 2025',
    [string]$OutputDirectory,
    [string]$Version='0.7.0.0'
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
& $csc ($shared + $sources)
if($LASTEXITCODE -ne 0){throw '插件编译失败'}
& $csc /nologo /target:winexe /platform:x64 /out:"$bin\PanelLauncher.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Data.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'Launcher.cs')
if($LASTEXITCODE -ne 0){throw 'Launcher build failed'}
& $csc /nologo /target:exe /platform:x64 /out:"$bin\TrainingExportRunner.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'TrainingExportRunner.cs')
if($LASTEXITCODE -ne 0){throw 'Training export runner build failed'}
& $csc /nologo /target:winexe /platform:x64 /out:"$bin\TianGongConverter.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll (Join-Path $PSScriptRoot 'ConverterMain.cs')
if($LASTEXITCODE -ne 0){throw 'TianGongConverter build failed'}
$tests=@(Get-ChildItem (Join-Path $root 'tests') -Filter '*.cs' -ErrorAction SilentlyContinue | ForEach-Object FullName)
if($tests.Count){& $csc /nologo /target:exe /platform:x64 /out:"$bin\PanelTests.exe" /reference:"$interop" /reference:"$bin\TianGongCadSuite.dll" /reference:Microsoft.CSharp.dll /reference:System.Data.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll $tests;if($LASTEXITCODE -ne 0){throw '测试程序编译失败'}}

$fixture=Join-Path $root "tests\fixtures\lineup-sample.tsv"
if(Test-Path $fixture){Copy-Item -LiteralPath $fixture -Destination $bin -Force}

$seed=Join-Path $root "tests\fixtures\lineup-models-seed.xml"
if(Test-Path $seed){Copy-Item -LiteralPath $seed -Destination $bin -Force}

$key=Join-Path $root "tests\fixtures\license-test.tgkey"
if(Test-Path $key){Copy-Item -LiteralPath $key -Destination (Join-Path $bin 'license-test.tgkey') -Force}

Write-Output ('插件已编译：' + (Join-Path $bin 'TianGongCadSuite.dll') + '  版本 ' + $Version)
