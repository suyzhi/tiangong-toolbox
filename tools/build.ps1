param([string]$CadHome='C:\Program Files\NDS\TianGong 2025',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$bin=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $root 'build'}
New-Item -Path $bin -ItemType Directory -Force | Out-Null
$interop=Join-Path $CadHome 'Program\TGAiHelper\Interop.TG.dll'
if(!(Test-Path $interop)){throw '找不到天工 CAD 接口库。请通过 -CadHome 指定安装目录。'}
Copy-Item -LiteralPath $interop -Destination $bin -Force
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@(Get-ChildItem (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
& $csc /nologo /target:library /platform:x64 /optimize+ /debug:full /out:"$bin\TianGongPanel.dll" /reference:"$interop" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll $sources
if($LASTEXITCODE -ne 0){throw '插件编译失败'}
& $csc /nologo /target:winexe /platform:x64 /out:"$bin\PanelLauncher.exe" /reference:"$interop" /reference:"$bin\TianGongPanel.dll" /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'Launcher.cs')
if($LASTEXITCODE -ne 0){throw 'Launcher build failed'}
$tests=@(Get-ChildItem (Join-Path $root 'tests') -Filter '*.cs' -ErrorAction SilentlyContinue | ForEach-Object FullName)
if($tests.Count){& $csc /nologo /target:exe /platform:x64 /out:"$bin\PanelTests.exe" /reference:"$interop" /reference:"$bin\TianGongPanel.dll" /reference:Microsoft.CSharp.dll /reference:System.Windows.Forms.dll $tests;if($LASTEXITCODE -ne 0){throw '测试程序编译失败'}}
