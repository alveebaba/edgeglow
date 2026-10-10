param([string]$SavedFrame = '', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('evidence\dark-background-tests-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $OutputDirectory 'DarkBackgroundTests.exe'
$sources = @((Join-Path $projectRoot 'Capture.cs'), (Join-Path $PSScriptRoot 'DarkBackgroundTests.cs'), (Join-Path $PSScriptRoot 'DarkBackgroundLegacyReference.cs'))
foreach ($source in $sources) { Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory ([IO.Path]::GetFileName($source))) }
& $compiler /nologo /optimize+ /target:exe /r:System.Drawing.dll (('/out:' + $exe)) $sources 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'build.txt')
$buildCode = $LASTEXITCODE
if ($buildCode -ne 0) { throw "Dark-background test compile failed: $buildCode" }
if ($SavedFrame) { & $exe $SavedFrame 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt') }
else { & $exe 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt') }
$testCode = $LASTEXITCODE
$hashes = Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object { $_.Name -ne 'hashes.txt' } | Get-FileHash -Algorithm SHA256
$hashes | Format-Table -AutoSize | Out-File -LiteralPath (Join-Path $OutputDirectory 'hashes.txt')
if ($testCode -ne 0) { throw "Dark-background tests failed: $testCode" }
