param([string]$OutputPath,[switch]$NewInstance,[switch]$AutoOnly)
$ErrorActionPreference='Stop'
$app=if($NewInstance){New-Object -ComObject SolidEdge.Application}else{[Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')}
if($NewInstance -and $app.Documents.Count -ne 0){throw 'The new instance is not empty; leaving it untouched.'}
$app.Visible=$true
$app.ScreenUpdating=$true
$testBlank=$app.Documents.Add('SolidEdge.AssemblyDocument')
$readyUntil=[DateTime]::Now.AddSeconds(20)
while(!$app.AddIns -and [DateTime]::Now -lt $readyUntil){Start-Sleep -Milliseconds 500;$app.DoIdle()}
if(!$app.AddIns){throw 'CAD add-in collection is not ready.'}
$addin=$null
foreach($a in $app.AddIns){if($a.GUID -eq '{B3B28C54-11D0-4488-B226-AD62429CEED2}'){$addin=$a;break}}
if(!$addin){throw 'Native add-in was not discovered.'}
$addin.Connect=$true
if(!$addin.Object){throw 'Native add-in did not expose the DEV diagnostic object.'}
'MENU '+$addin.Object.MenuStatus
if($addin.Object.MenuStatus -like 'ERROR:*'){throw $addin.Object.MenuStatus}
if($AutoOnly){$addin.Object.StartAutoTests([IO.Path]::GetFullPath($OutputPath))}else{$addin.Object.StartTests([IO.Path]::GetFullPath($OutputPath))}
$deadline=[DateTime]::Now.AddMinutes(4)
do{Start-Sleep -Seconds 1;$text=$addin.Object.Result}while(!$text -and [DateTime]::Now -lt $deadline)
if(!$text){throw 'Native test timed out.'}
$text
Set-Content -LiteralPath (Join-Path ([IO.Path]::GetFullPath($OutputPath)) ('native-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')) -Value $text -Encoding UTF8
$testBlank.Close($false)
if($NewInstance){$app.Quit()}
if($text -notmatch 'EXIT 0'){exit 1}
