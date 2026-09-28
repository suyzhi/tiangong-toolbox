$ErrorActionPreference='Stop'
$app=[Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
foreach($a in $app.AddIns){if($a.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){
 [pscustomobject]@{Version=$app.Version;Connect=$a.Connect;Description=$a.Description;ObjectAvailable=($null -ne $a.Object);Menu=$a.Object.MenuStatus}|ConvertTo-Json -Compress
}}
