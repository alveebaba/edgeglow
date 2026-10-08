# Isolated real-component regressions; preserve complete compilation inputs and executable.
param([string]$ReportDirectory = "", [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$SourceRoot = $projectRoot
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
if ([String]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $projectRoot ('review-output\regression-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
}
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Evidence directory already exists; refusing to overwrite any previous version.' }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$sourceDirectory = Join-Path $ReportDirectory 'source'
$binaryDirectory = Join-Path $ReportDirectory 'bin'
$settingsDirectory = Join-Path ([IO.Path]::GetTempPath()) ('EdgeGlow-regression-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sourceDirectory,$binaryDirectory,$settingsDirectory | Out-Null
Get-ChildItem -LiteralPath $SourceRoot -Filter '*.cs' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $sourceDirectory $_.Name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RealComponentRegressionTests.cs') -Destination $sourceDirectory
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $ReportDirectory 'run-regression-tests.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExecutable = Join-Path $binaryDirectory 'RealComponentRegressionTests.exe'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$inputs = @($sources) + @(Join-Path $ReportDirectory 'run-regression-tests.ps1')
$hashes = foreach ($path in $inputs) {
    [ordered]@{ path = $path.Substring($ReportDirectory.Length + 1); bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$hashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'input-hashes.json') -Encoding UTF8
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/warn:4',
    '/main:EdgeGlow.RealComponentRegressionTests',('/out:' + $testExecutable),
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll')
$compilerArgs += $sources
$receipt = [ordered]@{
    utc = [DateTime]::UtcNow.ToString('o'); sourceRoot = $SourceRoot
    compiler = $compiler; compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant(); compilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    compilerArgs = $compilerArgs; testSource = 'source\RealComponentRegressionTests.cs'; isolatedSettings = $settingsDirectory
    bounds = 'Generated capture, fake transport, isolated settings; no desktop, GUI, LAN ports, native DLL, live settings or registry.'
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
$buildOutput = & $compiler @compilerArgs 2>&1
$buildCode = $LASTEXITCODE
$buildOutput | Set-Content -LiteralPath (Join-Path $ReportDirectory 'build.txt') -Encoding UTF8
$buildOutput | Write-Output
$receipt['buildExitCode'] = $buildCode
if ($buildCode -eq 0) { $receipt['binarySha256'] = (Get-FileHash -LiteralPath $testExecutable -Algorithm SHA256).Hash.ToLowerInvariant() }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
if ($buildCode -ne 0) { throw ('Regression build failed; preserved full inputs: ' + $ReportDirectory) }
if ($CompileOnly) { Write-Output ('Compiled regression executable: ' + $testExecutable); Write-Output ('Regression artifacts: ' + $ReportDirectory); return }
$testProcess = New-Object System.Diagnostics.Process
$testProcess.StartInfo.FileName = $testExecutable
$testProcess.StartInfo.Arguments = '"' + $settingsDirectory + '"'
$testProcess.StartInfo.UseShellExecute = $false
$testProcess.StartInfo.CreateNoWindow = $true
$testProcess.StartInfo.RedirectStandardOutput = $true
$testProcess.StartInfo.RedirectStandardError = $true
$testProcess.Start() | Out-Null
$testStdout = $testProcess.StandardOutput.ReadToEnd()
$testStderr = $testProcess.StandardError.ReadToEnd()
$testProcess.WaitForExit()
$testCode = $testProcess.ExitCode
$testProcess.Dispose()
$testStdout | Set-Content -LiteralPath (Join-Path $ReportDirectory 'stdout.txt') -Encoding UTF8
$testStderr | Set-Content -LiteralPath (Join-Path $ReportDirectory 'stderr.txt') -Encoding UTF8
$testOutput = $testStdout + $testStderr
Copy-Item -LiteralPath $settingsDirectory -Destination (Join-Path $ReportDirectory 'isolated-settings') -Recurse
$testOutput | Set-Content -LiteralPath (Join-Path $ReportDirectory 'results.txt') -Encoding UTF8
$receipt['testExitCode'] = $testCode
$receipt['finishedUtc'] = [DateTime]::UtcNow.ToString('o')
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json') -Encoding UTF8
$testOutput | Write-Output
Write-Output ('Regression artifacts: ' + $ReportDirectory)
if ($testCode -ne 0) { throw ('Regression tests failed with exit code ' + $testCode + '; all cases collected and full inputs retained.') }
