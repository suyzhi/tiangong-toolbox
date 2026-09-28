param([Parameter(Mandatory=$true)][string]$OutputPath,[string]$ExpectedLibrary,[switch]$EntryOnly,[switch]$Reopen)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputPath)
New-Item -Path $output -ItemType Directory -Force | Out-Null
$dll=if($ExpectedLibrary){[IO.Path]::GetFullPath($ExpectedLibrary)}else{([uri](Get-ItemProperty 'HKCU:\Software\Classes\CLSID\{8C05165C-65A4-4EF2-A138-508589D82004}\InprocServer32').CodeBase).LocalPath}
[Reflection.Assembly]::LoadFrom($dll) | Out-Null
$filter=New-Object TianGongCadSuite.OleFilter
$app=New-Object -ComObject SolidEdge.Application
if($app.Documents.Count -ne 0){throw 'Instance not empty; leaving it untouched.'}
try {
    $app.Visible=$true
    $blank=$app.Documents.Add('SolidEdge.AssemblyDocument')
    $addin=$null
    foreach($candidate in $app.AddIns){if($candidate.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){$addin=$candidate;break}}
    if(!$addin){throw 'Lineup host not found'}
    $addin.Connect=$true
    $library=$addin.Object.LoadedLibrary
    if($ExpectedLibrary -and $library -ne [IO.Path]::GetFullPath($ExpectedLibrary)){throw ('Unexpected library: '+$library)}
    Write-Output ('LIBRARY '+$library)
    @(("LIBRARY "+$library),("MENU "+$addin.Object.MenuStatus),("PID "+((Get-Process TianGong).Id -join ','))) | Set-Content -LiteralPath (Join-Path $output $(if($Reopen){'host-restart.txt'}else{'host.txt'})) -Encoding UTF8
    $modes=if($Reopen){@('entry-reopen','lineup-reopen')}elseif($EntryOnly){@('entry')}else{@('entry','smart','table','lineup')}
    foreach($mode in $modes){
        $dir=Join-Path $output $(if($Reopen){'entry'}else{$mode})
        if($mode -eq 'lineup-reopen'){$dir=[IO.File]::ReadAllText((Join-Path $output 'lineup\latest-lineup.txt')).Trim()}
        New-Item -Path $dir -ItemType Directory -Force | Out-Null
        switch($mode){'entry'{$addin.Object.StartEntryTests($dir)} 'entry-reopen'{$addin.Object.StartEntryReopenTests($dir)} 'lineup-reopen'{$addin.Object.StartLineupReopenTests($dir)} 'smart'{$addin.Object.StartSmartTests($dir)} 'table'{$addin.Object.StartTableTests($dir)} 'lineup'{$addin.Object.StartLineupTests($dir)}}
        $deadline=[DateTime]::UtcNow.AddMinutes(4)
        do{Start-Sleep -Milliseconds 500;$result=$addin.Object.Result}while(!$result -and [DateTime]::UtcNow -lt $deadline)
        $result | Set-Content -LiteralPath (Join-Path $dir $(if($Reopen){'restart.log'}else{'native.log'})) -Encoding UTF8
        $result
        if($result -notmatch 'EXIT 0'){throw ($mode+' native test failed; see native.log')}
    }
} finally {
    # Only this newly created empty instance and its generated fixtures are owned by this script.
    try{$app.Quit()}catch{Write-Warning ('Test CAD teardown: '+$_.Exception.Message)}
    $filter.Dispose()
}
if(Get-ChildItem -LiteralPath $output -Recurse -Filter 'shutdown-exception.log'){throw 'Unhandled exception during CAD shutdown; see shutdown-exception.log'}
'CAD_QUIT_OK' | Set-Content -LiteralPath (Join-Path $output $(if($Reopen){'shutdown-restart.txt'}else{'shutdown.txt'})) -Encoding UTF8
