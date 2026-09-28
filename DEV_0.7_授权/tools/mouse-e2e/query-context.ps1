$ErrorActionPreference="Continue"
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$out = Join-Path $PSScriptRoot "context-probe.txt"
$lines = @()
try {
  $app=[Runtime.InteropServices.Marshal]::GetActiveObject("SolidEdge.Application")
} catch { ("GETACTIVEOBJECT 失败：" + $_.Exception.Message) | Set-Content $out -Encoding UTF8; exit 1 }
$lines += "CAD " + $app.Version
try { $lines += "ActiveDocument = " + $app.ActiveDocument.Name } catch { $lines += "ActiveDocument 读不到：" + $_.Exception.Message }
$addin=$null
foreach($a in $app.AddIns){ if($a.GUID -eq "{8C05165C-65A4-4EF2-A138-508589D82004}"){ $addin=$a; break } }
if($addin -eq $null){ $lines += "没找到天工工具箱加载项"; $lines | Set-Content $out -Encoding UTF8; exit 2 }
$lines += "addin.Connect = " + $addin.Connect
$diag=$addin.Object
if($diag -eq $null){ $lines += "诊断对象为空（加载项可能没连上）" } else {
  try { $lines += "MenuStatus = " + $diag.MenuStatus } catch { $lines += "MenuStatus 读不到：" + $_.Exception.Message }
  try { $lines += "LoadedLibrary = " + $diag.LoadedLibrary } catch { }
  foreach($id in 1..10){
    try {
      $f=$diag.QueryCommand($id)
      $n=$diag.NativeCommandId($id)
      $enabled = (($f -band 1) -eq 0)
      $lines += ("命令 " + $id + " enabled=" + $enabled + " flags=" + $f + " nativeId=" + $n)
    } catch { $lines += ("命令 " + $id + " 查询失败：" + $_.Exception.Message) }
  }
}
$lines | Set-Content $out -Encoding UTF8
Write-Output ("PROBE DONE -> " + $out)