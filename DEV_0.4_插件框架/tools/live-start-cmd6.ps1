$ErrorActionPreference='Continue'
try {
  $app = [Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
  Write-Output ('ATTACH OK ' + $app.Version + ' docs=' + $app.Documents.Count)
  $addin = $null
  foreach($a in $app.AddIns){ if($a.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){ $addin = $a; break } }
  if($addin -eq $null){ Write-Output 'ADDIN NOT FOUND'; exit 1 }
  $addin.Connect = $true
  $diag = $addin.Object
  $nid = $diag.NativeCommandId(6)
  Write-Output ('CMD6 native=' + $nid)
  $app.StartCommand($nid)
  Start-Sleep -Seconds 2
  Write-Output 'STARTED'
} catch { Write-Output ('FAIL ' + $_.Exception.Message) }