$ErrorActionPreference='Stop'
$app=[Runtime.InteropServices.Marshal]::GetActiveObject('SolidEdge.Application')
foreach($a in $app.AddIns){if($a.GUID -eq '{B3B28C54-11D0-4488-B226-AD62429CEED2}'){
 [pscustomobject]@{Version=$app.Version;Connect=$a.Connect;Description=$a.Description;ObjectAvailable=($null -ne $a.Object);Menu=$a.Object.MenuStatus}|ConvertTo-Json -Compress
}}
