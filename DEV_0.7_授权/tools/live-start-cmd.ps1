param([int]$Id = 7)
$ErrorActionPreference='Continue'
try {
  $app = [Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
  $addin = $null
  foreach($a in $app.AddIns){ if($a.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){ $addin = $a; break } }
  if($addin -eq $null){ Write-Output 'ADDIN NOT FOUND'; exit 1 }
  $addin.Connect = $true
  $nid = $addin.Object.NativeCommandId($Id)
  Write-Output ('CMD' + $Id + ' native=' + $nid)
  $app.StartCommand($nid)
  Start-Sleep -Seconds 2
  Write-Output 'STARTED'
} catch { Write-Output ('FAIL ' + $_.Exception.Message) }