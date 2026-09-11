$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$target=Join-Path $root ('build\release-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $target
& (Join-Path $PSScriptRoot 'install.ps1') -LibraryPath (Join-Path $target 'TianGongCadSuite.dll')
