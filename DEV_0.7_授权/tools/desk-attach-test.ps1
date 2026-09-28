$ErrorActionPreference='Continue'
$out = 'C:\Users\admin\Documents\ChatGPT\天工CAD型材内嵌玻璃板插件\DEV_0.4_插件框架\artifacts\deskwork\attach-test.log.txt'
function W($m){ Add-Content -Path $out -Value $m -Encoding UTF8 }
Set-Content -Path $out -Value ('ATTACH TEST ' + (Get-Date -Format o)) -Encoding UTF8
W ('PID ' + $PID)
try {
  $app = New-Object -ComObject SolidEdge.Application
  W ('COM OK version=' + $app.Version + ' docs=' + $app.Documents.Count)
  foreach($d in $app.Documents){ W ('  DOC ' + $d.Name) }
  $app.Visible = $true
  W ('VISIBLE set')
} catch { W ('COM FAIL ' + $_.Exception.Message) }
W 'DONE'
