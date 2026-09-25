param([string]$LibraryPath)
$ErrorActionPreference='Stop'
$app=New-Object -ComObject SolidEdge.Application
$doc=$null
try {
  $app.Visible=$true
  $app.ScreenUpdating=$true
  # 必须先开文档，CAD 才会实例化插件（与 tools\native-test.ps1 一致）
  $doc=$app.Documents.Add('SolidEdge.AssemblyDocument')
  $readyUntil=[DateTime]::Now.AddSeconds(30)
  while(!$app.AddIns -and [DateTime]::Now -lt $readyUntil){Start-Sleep -Milliseconds 500;$app.DoIdle()}
  if(!$app.AddIns){throw 'CAD add-in collection is not ready.'}
  $addin=$null
  foreach($a in $app.AddIns){if($a.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){$addin=$a;break}}
  if(!$addin){throw 'Native add-in was not discovered.'}
  $addin.Connect=$true
  $deadline=[DateTime]::Now.AddSeconds(30)
  while(!$addin.Object -and [DateTime]::Now -lt $deadline){Start-Sleep -Milliseconds 500;$app.DoIdle()}
  if(!$addin.Object){throw 'Native add-in did not expose the diagnostic object.'}
  'CONNECTED'
  'MENU ' + $addin.Object.MenuStatus
  'LIB ' + $addin.Object.LoadedLibrary
  $ids=@(); foreach($c in 1..12){ try { $n=$addin.Object.NativeCommandId($c); if($n -gt 0){$ids+="$c->$n"} } catch {} }
  'COMMAND_IDS ' + ($ids -join ', ')
} catch { 'FATAL ' + $_.Exception.Message }
finally { if($doc -ne $null){ try { $doc.Close($false) } catch {} }; try { $app.Quit() } catch {} }
