$ErrorActionPreference="Continue"
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$app=[Runtime.InteropServices.Marshal]::GetActiveObject("SolidEdge.Application")
"ActiveDoc=" + $app.ActiveDocument.Name
$addin=$null
foreach($a in $app.AddIns){ if($a.GUID -eq "{8C05165C-65A4-4EF2-A138-508589D82004}"){ $addin=$a; break } }
if($addin -eq $null){ "找不到插件"; exit 1 }
$addin.Connect=$true
Start-Sleep -Milliseconds 800
$diag=$addin.Object
if($diag -eq $null){ "诊断对象为空"; exit 2 }
"MenuStatus = " + $diag.MenuStatus
foreach($id in 1..10){
  try { $f=$diag.QueryCommand($id); $n=$diag.NativeCommandId($id); "cmd " + $id + " enabled=" + (($f -band 1) -eq 0) + " flags=" + $f + " native=" + $n }
  catch { "cmd " + $id + " 查询失败：" + $_.Exception.Message }
}