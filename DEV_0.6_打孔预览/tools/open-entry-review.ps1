param([Parameter(Mandatory=$true)][string]$Fixture,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$file=[IO.Path]::GetFullPath($Fixture)
$output=[IO.Path]::GetFullPath($OutputPath)
if(!(Test-Path -LiteralPath $file)){throw 'Review fixture missing'}
$dll=([uri](Get-ItemProperty 'HKCU:\Software\Classes\CLSID\{8C05165C-65A4-4EF2-A138-508589D82004}\InprocServer32').CodeBase).LocalPath
[Reflection.Assembly]::LoadFrom($dll) | Out-Null
$filter=New-Object TianGongCadSuite.OleFilter
$app=New-Object -ComObject SolidEdge.Application
if($app.Documents.Count -ne 0){$filter.Dispose();throw 'New review instance is not empty; leaving it untouched.'}
try{
    $app.Visible=$true
    $doc=$app.Documents.Open($file)
    $addin=$null
    foreach($item in $app.AddIns){if($item.GUID -eq '{8C05165C-65A4-4EF2-A138-508589D82004}'){$addin=$item;break}}
    if(!$addin){throw 'Installed host missing'}
    $addin.Connect=$true
    $diagnostic=$addin.Object
    if($diagnostic.LoadedLibrary -ne $dll){throw 'Unexpected installed library'}
    $app.StartCommand($diagnostic.NativeCommandId(3))
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do{Start-Sleep -Milliseconds 300;$app.DoIdle()}while($diagnostic.LineupWindowCount -ne 1 -and [DateTime]::UtcNow -lt $deadline)
    if($diagnostic.LineupWindowCount -ne 1 -or $diagnostic.LastCommand -ne 3){throw 'Native command did not open Lineup'}
    $result=[pscustomobject]@{Library=$diagnostic.LoadedLibrary;Sha256=(Get-FileHash -LiteralPath $dll).Hash;Menu=$diagnostic.MenuStatus;LastCommand=$diagnostic.LastCommand;LineupWindows=$diagnostic.LineupWindowCount;Fixture=$file;ProcessIds=@((Get-Process TianGong).Id)}
    $result | ConvertTo-Json | Set-Content -LiteralPath $output -Encoding UTF8
    $result | ConvertTo-Json
}finally{$filter.Dispose()}
# Leave the generated review fixture and its Lineup window open for inspection.
