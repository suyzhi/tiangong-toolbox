param([string]$OutDir)
$ErrorActionPreference='Continue'
$log = Join-Path $OutDir 'attach2.log.txt'
function W($m){ Add-Content -Path $log -Value $m -Encoding UTF8; Write-Output $m }
Set-Content -Path $log -Value ('ATTACH2 ' + (Get-Date -Format o)) -Encoding UTF8
W ('PID ' + $PID)
try {
  $app = [Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
  W ('GETACTIVEOBJECT OK version=' + $app.Version + ' docs=' + $app.Documents.Count)
  foreach($d in $app.Documents){ W ('  DOC ' + $d.Name) }
} catch {
  W ('GETACTIVEOBJECT FAIL ' + $_.Exception.Message)
}
try {
  $app2 = New-Object -ComObject SolidEdge.Application
  W ('NEWOBJECT version=' + $app2.Version + ' docs=' + $app2.Documents.Count)
} catch { W ('NEWOBJECT FAIL ' + $_.Exception.Message) }
W 'DONE'
