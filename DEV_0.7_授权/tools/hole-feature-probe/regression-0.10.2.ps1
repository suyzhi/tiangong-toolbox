# 0.10.2 发版前的整套真机回归（全部在副本上跑，不保存原件）。
# 先编开发构建、把探针编进同一个目录、注册开发构建（RenameE2E 要插件里的 Diagnostics），然后：
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File regression-0.10.2.ps1 -Build <开发构建目录> -Work <放 copies\ 的目录>
# 跑完看 <Work>\q\artifacts\cad-queue\queue.log 和 DEV_0.7_授权\artifacts\f2-*\out.txt；**跑完马上装回正式版**（见 HANDOVER-0.10.2.md）。
# 用 Windows PowerShell 5 跑（不是 pwsh），原因见 run-cad-queue-convert.ps1 文件头。
param([Parameter(Mandatory=$true)][string]$Build, [Parameter(Mandatory=$true)][string]$Work, [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$c = Join-Path $Work 'copies'

# ① 探针编进开发构建目录（Interop.TG.dll 要在旁边）
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$interop = Join-Path $Build 'Interop.TG.dll'
if (-not (Test-Path $interop)) { Copy-Item 'C:\Program Files\NDS\TianGong 2025\Program\TGAiHelper\Interop.TG.dll' $interop }
$probes = 'hole-feature-probe\MultiBodyE2E','hole-feature-probe\EditPatternE2E','hole-feature-probe\CheckPatternE2E','hole-feature-probe\NativeLinkE2E',
          'hole-feature-probe\User085E2E','hole-feature-probe\Drill084E2E','hole-feature-probe\CoaxialE2E','panel-probe\CrossFrameE2E',
          'hole-feature-probe\RenameE2E','hole-feature-probe\LinkedDrillE2E','hole-feature-probe\EditDeepE2E','hole-feature-probe\SheetFaceE2E',
          'hole-feature-probe\SheetLinkE2E','hole-feature-probe\FaceScanAudit','hole-feature-probe\CheckCompare','hole-feature-probe\ConvertE2E',
          'hole-feature-probe\AsmList','hole-feature-probe\LinkAudit','panel-probe\PanelScanE2E'
foreach ($p in $probes) {
    $name = Split-Path $p -Leaf
    & $csc /nologo /target:exe /platform:x64 /out:"$Build\$name.exe" /reference:"$interop" /reference:"$Build\TianGongCadSuite.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll (Join-Path $root "tools\$p.cs")
    if ($LASTEXITCODE -ne 0) { throw "探针编译失败：$name" }
}
if ($CompileOnly) { 'probes compiled'; return }

# ② 改名用的新副本（RenameE2E main 会真的改名）
$rn = Join-Path $c 'rn-regress'
if (Test-Path $rn) { Remove-Item $rn -Recurse -Force }
robocopy (Join-Path $c 'cart') $rn /E /NFL /NDL /NJH /NJS | Out-Null

# ③ 排队（队列脚本拷到 Work\q 下，日志就落在 Work\q\artifacts\cad-queue）
$q = Join-Path $Work 'q\tools\hole-feature-probe'; New-Item -ItemType Directory -Force $q | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'run-cad-queue-convert.ps1') $q -Force
$o = $Build
$jobs = @(
  "$o\MultiBodyE2E.exe|`"$c\mb\YF25.07G01-L1-M30-02-07.par`" `"$c\mb\YF25.07G01-L1-M30-02-17.par`"|f2-multibody|1800",
  "$o\EditPatternE2E.exe||f2-editpattern|1800",
  "$o\CheckPatternE2E.exe||f2-checkpattern|1800",
  "$o\NativeLinkE2E.exe||f2-nativelink|1800",
  "$o\User085E2E.exe||f2-user085|1800",
  "$o\CoaxialE2E.exe||f2-coaxial|1800",
  "$o\Drill084E2E.exe|`"$c\mb\mb084.par`"|f2-drill084|1800",
  "$o\CrossFrameE2E.exe|`"$c\fence2\SK26.01-OK310-03-001.asm`"|f2-crossframe|1800",
  "$o\RenameE2E.exe|main `"$rn`"|f2-rename|1800",
  "$o\RenameE2E.exe|propcard `"$c\cart`" YF25.07-L1-M30-03.asm|f2-propcard|1800",
  "$o\LinkedDrillE2E.exe|`"$c\cart\YF25.07G01-L1-M30-02.asm`" YF25.07G01-L1-M30-02-002 3 multi|f2-drill002|1800",
  "$o\EditDeepE2E.exe|`"$c\cart\YF25.07-L1-M30-03.asm`" YF25.07G01-L1-M30-02-002|f2-editdeep|1800",
  "$o\SheetFaceE2E.exe|`"$c\ok310\SK26.01-OK310-03.asm`"|f2-sheetface|900",
  "$o\SheetLinkE2E.exe|`"$c\ok310\SK26.01-OK310-03.asm`"|f2-sheetlink|900",
  "$o\ConvertE2E.exe|`"$o\TianGongConverter.exe`" `"$c\bmw\装配体\3C412D9__A__1__A__FENCED.sldasm`" `"$c\bmw`" 0 25|f2-convert|1800",
  "$o\CheckCompare.exe|`"$c\cart\YF25.07G01-L1-M30-02.asm`"|f2-check-cart|1500"
)
& (Join-Path $q 'run-cad-queue-convert.ps1') -Jobs $jobs
