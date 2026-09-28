param([Parameter(Mandatory=$true)][string]$OutputPath,[switch]$KeepOpen)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputPath)
New-Item -Path $output -ItemType Directory -Force | Out-Null
$app=New-Object -ComObject SolidEdge.Application
if($app.Documents.Count -ne 0){throw 'New CAD instance is not empty; leaving it untouched.'}
$app.Visible=$true
$app.ScreenUpdating=$true
$blank=$null
try {
    $blank=$app.Documents.Add('SolidEdge.AssemblyDocument')
    $deadline=[DateTime]::Now.AddSeconds(30)
    do { $addin=$null;foreach($candidate in $app.AddIns){if($candidate.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){$addin=$candidate;break}};if(!$addin){Start-Sleep -Milliseconds 500;$app.DoIdle()} } while(!$addin -and [DateTime]::Now -lt $deadline)
    if(!$addin){throw 'Native add-in was not discovered.'}
    $addin.Connect=$true
    if(!$addin.Object){throw 'No native diagnostic object.'}
    $menu=$addin.Object.MenuStatus
    $library=$addin.Object.LoadedLibrary
    if($menu -notmatch '^Registered [0-9]+ commands$' -or $addin.Object.CommandFlags(3) -ne 1){throw ('Lineup command unavailable: '+$menu)}
    "MENU $menu"
    "LIBRARY $library"
    $addin.Object.StartLineupTests($output)
    $deadline=[DateTime]::Now.AddMinutes(4)
    do {Start-Sleep -Seconds 1;$result=$addin.Object.Result} while(!$result -and [DateTime]::Now -lt $deadline)
    if(!$result){throw 'Lineup native test timed out.'}
    $result
    @("MENU $menu","LIBRARY $library",$result) | Set-Content -LiteralPath (Join-Path $output 'lineup-native.log') -Encoding UTF8
    if($result -notmatch 'EXIT 0'){throw 'Lineup native tests failed. See log.'}
    if($KeepOpen){
        $fixture=Join-Path ([IO.File]::ReadAllText((Join-Path $output 'latest-lineup.txt'))) 'LineupFixture.asm'
        $app.Documents.Open($fixture) | Out-Null
        $addin.Object.OpenLineupPanel()
    }
} finally {
    if($blank){$blank.Close($false)}
    if(!$KeepOpen){$app.Quit()}
}
