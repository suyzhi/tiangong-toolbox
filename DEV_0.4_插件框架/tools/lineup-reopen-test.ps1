param([Parameter(Mandatory=$true)][string]$FixtureDirectory)
$ErrorActionPreference='Stop'
if(Get-Process TianGong -ErrorAction SilentlyContinue){throw 'Close the independent test CAD instance before restart validation.'}
$dir=[IO.Path]::GetFullPath($FixtureDirectory)
$app=New-Object -ComObject SolidEdge.Application
if($app.Documents.Count -ne 0){throw 'New instance is not empty.'}
$app.Visible=$true
$blank=$app.Documents.Add('SolidEdge.AssemblyDocument')
$addin=$null
foreach($candidate in $app.AddIns){if($candidate.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){$addin=$candidate;break}}
if(!$addin){throw 'Native add-in missing.'}
$addin.Connect=$true
$addin.Object.StartLineupReopenTests($dir)
$deadline=[DateTime]::Now.AddMinutes(2)
do{Start-Sleep -Seconds 1;$result=$addin.Object.Result}while(!$result -and [DateTime]::Now -lt $deadline)
$result
@(('Fresh process IDs: '+((Get-Process TianGong).Id -join ',')),('LIBRARY '+$addin.Object.LoadedLibrary),$result) | Set-Content (Join-Path $dir 'restart-native.log') -Encoding UTF8
$blank.Close($false)
if($result -notmatch 'EXIT 0'){throw 'Restart validation failed; test CAD left open for inspection.'}
