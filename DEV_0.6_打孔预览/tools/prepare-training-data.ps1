param([Parameter(Mandatory=$true)][string]$DatasetDirectory,[string]$Python,[string]$Poppler)
$ErrorActionPreference='Stop'
$runtime=Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies'
if(!$Python){$Python=Join-Path $runtime 'python\python.exe'}
if(!$Poppler){$Poppler=Join-Path $runtime 'native\poppler\Library\bin'}
if(!(Test-Path -LiteralPath $Python)){throw 'Specify -Python with numpy and pypdf installed.'}
if(!(Test-Path -LiteralPath (Join-Path $Poppler 'pdftoppm.exe'))){throw 'Specify -Poppler with pdftoppm.exe available.'}
$env:PYTHONUTF8='1';$env:PYTHONIOENCODING='utf-8'
& $Python (Join-Path $PSScriptRoot 'prepare-training-data.py') ([IO.Path]::GetFullPath($DatasetDirectory)) --poppler $Poppler
exit $LASTEXITCODE
