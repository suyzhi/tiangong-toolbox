$ErrorActionPreference='Stop'
$app=[Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
foreach($a in $app.AddIns){if($a.GUID -eq '{98BF0FA8-7D65-4A13-9926-6A45836FD6D2}'){
 [pscustomobject]@{Version=$app.Version;Connect=$a.Connect;Description=$a.Description;ObjectAvailable=($null -ne $a.Object);Menu=$a.Object.MenuStatus}|ConvertTo-Json -Compress
}}
