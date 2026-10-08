# Isolated combined tests: production engine and LAN parser/controller, in-memory physical boundaries.
param([string]$SourceRoot = "", [string]$ReportDirectory = "", [switch]$Baseline, [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([String]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = $projectRoot }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
if ([String]::IsNullOrWhiteSpace($ReportDirectory)) {
    $label = if ($Baseline) { 'before' } else { 'after' }
    $ReportDirectory = Join-Path $projectRoot ('review-output\combined-' + $label + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
}
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Evidence directory already exists; refusing to overwrite any previous version.' }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$sourceDirectory = Join-Path $ReportDirectory 'source'
$binaryDirectory = Join-Path $ReportDirectory 'bin'
$traceDirectory = Join-Path $ReportDirectory 'traces'
New-Item -ItemType Directory -Path $sourceDirectory,$binaryDirectory,$traceDirectory | Out-Null
Get-ChildItem -LiteralPath $SourceRoot -Filter '*.cs' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $sourceDirectory $_.Name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CombinedControlRegressionTests.cs') -Destination $sourceDirectory
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $ReportDirectory 'run-combined-tests.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $binaryDirectory 'CombinedControlRegressionTests.exe'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$inputs = @($sources) + @(Join-Path $ReportDirectory 'run-combined-tests.ps1')
$hashes = foreach ($path in $inputs) {
    [ordered]@{ path = $path.Substring($ReportDirectory.Length + 1); bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'input-hashes.json') -Encoding UTF8
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/warn:4',
    '/main:EdgeGlow.CombinedControlRegressionTests',('/out:' + $testExecutable),
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll')
if ($Baseline) { $compilerArgs += '/define:PHASE_C_BASELINE' }
$compilerArgs += $sources
$receipt = [ordered]@{
    utc = [DateTime]::UtcNow.ToString('o'); sourceRoot = $SourceRoot; baseline = [bool]$Baseline
    compiler = $compiler; compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant(); compilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    compilerArgs = $compilerArgs; testSource = 'source\CombinedControlRegressionTests.cs'
    bounds = 'Generated capture; in-memory sockets; named events only; no desktop, GUI, LAN ports, native DLL, live settings or registry.'
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
$buildOutput = & $compiler @compilerArgs 2>&1
$buildCode = $LASTEXITCODE
$buildOutput | Set-Content -LiteralPath (Join-Path $ReportDirectory 'build.txt') -Encoding UTF8
$buildOutput | Write-Output
$receipt['buildExitCode'] = $buildCode
if ($buildCode -eq 0) { $receipt['binarySha256'] = (Get-FileHash -LiteralPath $testExecutable -Algorithm SHA256).Hash.ToLowerInvariant() }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
if ($buildCode -ne 0) { throw ('Combined build failed; preserved full inputs: ' + $ReportDirectory) }
if ($CompileOnly) { Write-Output ('Compiled combined executable: ' + $testExecutable); Write-Output ('Combined artifacts: ' + $ReportDirectory); return }
$testOutput = & $testExecutable $traceDirectory 2>&1
$testCode = $LASTEXITCODE
$testOutput | Set-Content -LiteralPath (Join-Path $ReportDirectory 'results.txt') -Encoding UTF8
$receipt['testExitCode'] = $testCode
$receipt['finishedUtc'] = [DateTime]::UtcNow.ToString('o')
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
$testOutput | Write-Output
Write-Output ('Combined artifacts: ' + $ReportDirectory)
if ($testCode -ne 0) { throw ('Combined tests failed with exit code ' + $testCode + '; all cases collected and full inputs retained.') }
