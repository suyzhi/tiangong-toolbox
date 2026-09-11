param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$dir=[IO.Path]::GetFullPath($OutputPath)
New-Item -Path $dir -ItemType Directory -Force | Out-Null
$app=New-Object -ComObject SolidEdge.Application
if($app.Documents.Count -ne 0){throw 'Instance is not empty; leaving it untouched.'}
$log=New-Object System.Collections.Generic.List[string]
try {
    $app.Visible=$true
    $doc=$app.Documents.Add('SolidEdge.AssemblyDocument')
    $doc.SaveAs((Join-Path $dir 'CommandFixture.asm'))
    $addin=$null
    foreach($a in $app.AddIns){if($a.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){$addin=$a;break}}
    if(!$addin){throw 'Native add-in missing'}
    $addin.Connect=$true
    $diagnostic=$addin.Object
    $log.Add('LIBRARY '+$diagnostic.LoadedLibrary)
    foreach($id in 1..3){
        $flags=$diagnostic.CommandFlags($id)
        $nativeId=$diagnostic.NativeCommandId($id)
        $log.Add("LOCAL=$id NATIVE=$nativeId FLAGS=$flags")
        if(($flags -band 1) -ne 1){throw "Assembly command $id is disabled"}
    }
    if($diagnostic.CommandFlags(99) -ne 0){throw 'Unknown command enabled'}
    $part=$app.Documents.Add('SolidEdge.PartDocument')
    foreach($id in 1..3){if($diagnostic.CommandFlags($id) -ne 0){throw "Part context command $id is enabled"}}
    $log.Add('PASS: all three assembly commands enabled; all disabled in part context; unknown ID disabled')
    $part.Close($false)
    $doc.Activate()
    $startDeadline=[DateTime]::Now.AddSeconds(15)
    while($true){
        try{$app.StartCommand($diagnostic.NativeCommandId(3));break}
        catch{
            if($_.Exception.HResult -ne -2147418111 -and $_.Exception.HResult -ne -2147417846){throw}
            if([DateTime]::Now -ge $startDeadline){throw}
            Start-Sleep -Milliseconds 400
            if($diagnostic.LastCommand -eq 3){break}
        }
    }
    $until=[DateTime]::Now.AddSeconds(15)
    do{Start-Sleep -Milliseconds 300;$app.DoIdle()}while($diagnostic.LineupWindowCount -ne 1 -and [DateTime]::Now -lt $until)
    if($diagnostic.LastCommand -ne 3 -or $diagnostic.LineupWindowCount -ne 1){throw ('Native dispatch failed: callback='+$diagnostic.LastCommand+' windows='+$diagnostic.LineupWindowCount)}
    $log.Add('PASS: CAD StartCommand(runtime ID) delivered OnCommand(local ID=3) and opened Lineup')
    $log.Add('EXIT 0')
} catch {
    $log.Add($_.ToString())
    throw
} finally {
    $log | Set-Content (Join-Path $dir 'command-routing-native.log') -Encoding UTF8
    $log
    $app.Quit()
}
