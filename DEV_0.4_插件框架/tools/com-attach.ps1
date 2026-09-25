$ErrorActionPreference='Stop'
try {
  $app = [Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
  Write-Output ('ATTACH OK version=' + $app.Version + ' docs=' + $app.Documents.Count)
} catch {
  Write-Output ('ATTACH FAIL ' + $_.Exception.Message)
}
