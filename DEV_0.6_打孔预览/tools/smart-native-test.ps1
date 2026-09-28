param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputPath)
New-Item -Path $output -ItemType Directory -Force | Out-Null
$app=New-Object -ComObject SolidEdge.Application
if($app.Documents.Count -ne 0){throw 'Instance not empty; leaving it untouched.'}
try{
    $app.Visible=$true
    $blank=$app.Documents.Add('SolidEdge.AssemblyDocument')
    $addin=$null
    foreach($a in $app.AddIns){if($a.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){$addin=$a;break}}
    if(!$addin){throw 'Native host not found'}
    $addin.Connect=$true
    foreach($id in 1..3){if($addin.Object.CommandFlags($id) -ne 1){throw "Command $id disabled"}}
    $addin.Object.StartSmartTests($output)
    $deadline=[DateTime]::Now.AddMinutes(4)
    do{Start-Sleep -Seconds 1;$result=$addin.Object.Result}while(!$result -and [DateTime]::Now -lt $deadline)
    @(('LIBRARY '+$addin.Object.LoadedLibrary),$result)|Set-Content (Join-Path $output 'smart-native.log') -Encoding UTF8
    $result
    if($result -notmatch 'EXIT 0'){throw 'Native smart-entry test failed'}
    $blank.Close($false)
}finally{$app.Quit()}
