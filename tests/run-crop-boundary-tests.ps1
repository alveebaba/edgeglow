param([Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Evidence directory exists; refusing overwrite.' }
$sourceDirectory = Join-Path $ReportDirectory 'source'
$binaryDirectory = Join-Path $ReportDirectory 'bin'
New-Item -ItemType Directory -Path $sourceDirectory,$binaryDirectory | Out-Null
foreach ($name in @('Capture.cs','CaptureTests.cs','CaptureRecoveryTests.cs')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $sourceDirectory
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CropBoundaryTests.cs') -Destination $sourceDirectory
Copy-Item -LiteralPath $PSCommandPath -Destination $ReportDirectory
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $binaryDirectory 'CropBoundaryTests.exe'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$hashes = foreach ($path in @($sources) + @(Join-Path $ReportDirectory 'run-crop-boundary-tests.ps1')) {
    [ordered]@{ path = $path.Substring($ReportDirectory.Length + 1); bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'input-hashes.json') -Encoding UTF8
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/warn:4','/main:EdgeGlow.CropBoundaryTests',('/out:' + $executable),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll') + $sources
$receipt = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); sourceRoot = $sourceRoot; compiler = $compiler; compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant(); compilerArgs = $compilerArgs; bounds = 'Generated image pixels and scripted ScreenSampler backend only. No native DLL, desktop, UI, networking, settings or startup.' }
$build = & $compiler @compilerArgs 2>&1
$receipt['buildExitCode'] = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $ReportDirectory 'build.txt'), ($build -join [Environment]::NewLine))
if ($receipt['buildExitCode'] -eq 0) { $receipt['binarySha256'] = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
if ($receipt['buildExitCode'] -ne 0) { $build | Write-Output; throw 'Crop boundary build failed.' }
$output = & $executable 2>&1
$receipt['testExitCode'] = $LASTEXITCODE
$output | Set-Content -LiteralPath (Join-Path $ReportDirectory 'results.txt') -Encoding UTF8
$receipt['finishedUtc'] = [DateTime]::UtcNow.ToString('o')
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
$output | Write-Output
exit $receipt['testExitCode']
