# Copies immutable inputs; never changes historical evidence. Runs only the private fixture contract.
param([Parameter(Mandatory=$true)][string]$SourceRoot,
      [Parameter(Mandatory=$true)][string]$FixtureSourceRoot,
      [Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$FixtureSourceRoot = (Resolve-Path -LiteralPath $FixtureSourceRoot).Path
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Evidence directory exists; refusing overwrite.' }
$sourceDirectory = Join-Path $ReportDirectory 'source'
$binaryDirectory = Join-Path $ReportDirectory 'bin'
New-Item -ItemType Directory -Path $sourceDirectory,$binaryDirectory | Out-Null
Get-ChildItem -LiteralPath $SourceRoot -Filter '*.cs' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $sourceDirectory $_.Name)
}
Copy-Item -LiteralPath (Join-Path $FixtureSourceRoot 'tests\RealComponentRegressionTests.cs') -Destination $sourceDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FixtureCopyContractTests.cs') -Destination $sourceDirectory
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $ReportDirectory 'run-fixture-copy-tests.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $binaryDirectory 'FixtureCopyContractTests.exe'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$inputs = @($sources) + @(Join-Path $ReportDirectory 'run-fixture-copy-tests.ps1')
$hashes = foreach ($path in $inputs) {
    [ordered]@{ path = $path.Substring($ReportDirectory.Length + 1); bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'input-hashes.json') -Encoding UTF8
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/warn:4',
    '/main:EdgeGlow.FixtureCopyContractTests',('/out:' + $executable),
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll') + $sources
$receipt = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); sourceRoot = $SourceRoot; fixtureSourceRoot = $FixtureSourceRoot
    compiler = $compiler; compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant(); compilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    compilerArgs = $compilerArgs; testSource = 'source\FixtureCopyContractTests.cs'
    bounds = 'Only the actual private legacy fake transport; no engine started, desktop, native DLL, LAN sockets, GUI, settings or registry.' }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
$build = & $compiler @compilerArgs 2>&1
$receipt['buildExitCode'] = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $ReportDirectory 'build.txt'), ($build -join [Environment]::NewLine))
if ($receipt['buildExitCode'] -eq 0) { $receipt['binarySha256'] = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
if ($receipt['buildExitCode'] -ne 0) { $build | Write-Output; throw 'Fixture contract build failed; inputs preserved.' }
$output = & $executable 2>&1
$receipt['testExitCode'] = $LASTEXITCODE
$output | Set-Content -LiteralPath (Join-Path $ReportDirectory 'results.txt') -Encoding UTF8
$receipt['finishedUtc'] = [DateTime]::UtcNow.ToString('o')
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
$output | Write-Output
Write-Output ('Fixture contract artifacts: ' + $ReportDirectory)
exit $receipt['testExitCode']
