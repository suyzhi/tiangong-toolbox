param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$BuildDirectory,
    [switch]$Recurse,
    [int]$MaxFiles=0
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$BuildDirectory){$BuildDirectory=Join-Path $root 'build\training-export-02-20260910-release2'}
$exe=Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) 'TrainingExportRunner.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Build TrainingExportRunner first.'}
$source=(Get-Item -LiteralPath $SourceDirectory).FullName
$output=[IO.Path]::GetFullPath($OutputDirectory)
if($output.TrimEnd('\') -eq $source.TrimEnd('\') -or $output.StartsWith($source.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use an output directory outside the source tree to prevent exported copies being scanned again.'}
if(Get-Process TianGong -ErrorAction SilentlyContinue){throw 'Save and close CAD before standalone batch export, or use the plugin command in CAD.'}
$files=@(Get-ChildItem -LiteralPath $source -Filter '*.dft' -File -Recurse:$Recurse | Sort-Object FullName)
if($MaxFiles -gt 0){$files=@($files | Select-Object -First $MaxFiles)}
if(!$files.Count){throw 'No native DFT drawings found. SolidWorks drawings are not supported by this exporter.'}
New-Item -Path $output -ItemType Directory -Force | Out-Null
$inventory=Join-Path $output ('source-inventory-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.csv')
$files | ForEach-Object {[pscustomobject]@{FileType=$_.Extension;Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}} | Export-Csv -LiteralPath $inventory -NoTypeInformation -Encoding UTF8
& $exe $output @($files.FullName)
$code=$LASTEXITCODE
if($code -eq 3){Write-Warning 'Extraction finished with partial samples. Review sample.json issues and run prepare-training-data.py.'}
if($code -eq 1){throw 'One or more samples failed. Inspect errors and source integrity results.'}
if($code -eq 2){throw 'CAD process or command precondition failed.'}
exit $code
