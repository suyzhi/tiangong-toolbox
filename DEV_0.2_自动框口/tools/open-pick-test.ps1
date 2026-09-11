$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Join-Path $root 'artifacts\cad-20260905-162943'
$target=Join-Path $root ('artifacts\pick-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -Path $target -ItemType Directory | Out-Null
foreach($name in @('Frame.asm','Vertical.par','Horizontal.par')){Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target}
$app=New-Object -ComObject SolidEdge.Application
if($app.Documents.Count -ne 0){throw 'Expected a new empty CAD instance; leaving it untouched.'}
$app.Visible=$true
$doc=$app.Documents.Open((Join-Path $target 'Frame.asm'))
$app.ActiveWindow.View.Fit()
foreach($a in $app.AddIns){if($a.GUID -eq '{B3B28C54-11D0-4488-B226-AD62429CEED2}'){$a.Connect=$true;$a.Object.OpenPanel()}}
[pscustomobject]@{Window=$app.hWnd;Frame=$app.ActiveFramehWnd;Path=$doc.FullName;Version=$app.Version}|ConvertTo-Json -Compress
