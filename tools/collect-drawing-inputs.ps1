param(
  [Parameter(Mandatory=$true)][string]$Root,
  [string]$Output = "drawing-inputs.csv"
)

$files = Get-ChildItem -LiteralPath $Root -File -Recurse
$models = $files | Where-Object { $_.Extension -in '.par','.psm','.asm' }
$rows = foreach ($m in $models) {
  $stem = [IO.Path]::GetFileNameWithoutExtension($m.Name)
  $same = $files | Where-Object { $_.BaseName -eq $stem -and $_.Extension -in '.pdf','.SLDDRW','.dwg','.dft' }
  [pscustomobject]@{
    ModelType = $m.Extension.TrimStart('.').ToLowerInvariant()
    Model = $m.FullName
    ModelBytes = $m.Length
    ExistingDrawingCount = @($same).Count
    ExistingDrawings = (@($same) | ForEach-Object FullName) -join ';'
    Status = if (@($same).Count -gt 0) { 'existing-drawing' } else { 'needs-drawing' }
  }
}
$rows | Sort-Object Model | Export-Csv -LiteralPath $Output -NoTypeInformation -Encoding UTF8
Write-Host ("Wrote {0} model rows to {1}" -f @($rows).Count,(Resolve-Path $Output))
