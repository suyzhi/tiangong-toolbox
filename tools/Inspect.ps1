param([string]$Mode='app')
$ErrorActionPreference='Stop'
$sdk='C:\Program Files\NDS\TianGong 2025\Program\TGAiHelper\Interop.TG.dll'
$a=[Reflection.Assembly]::LoadFrom($sdk)
if($Mode -eq 'app') {
  $app=[Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
  'VERSION '+$app.Version
  'ACTIVE '+$app.ActiveDocument.FullName
  'ENV '+$app.ActiveEnvironment
  foreach($e in $app.Environments){'ENVIRONMENT '+$e.Name+' '+$e.CATID}
  foreach($d in $app.Documents){'DOCUMENT '+$d.FullName}
} else {
  foreach($n in $Mode.Split(',')) {
    $t=$a.GetType($n); 'TYPE '+$n
    if($t.IsEnum){[Enum]::GetNames($t) | ForEach-Object {$_+' = '+[int][Enum]::Parse($t,$_)}; continue}
    $t.GetMethods() | ForEach-Object { $_.ToString(); ($_.GetParameters() | ForEach-Object {$_.Name + $(if($_.IsOptional){'=?'}else{''})}) -join ', ' }
  }
}
