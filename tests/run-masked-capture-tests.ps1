param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..'),
    [string]$ReportDirectory = (Join-Path $PSScriptRoot '..\test-output\masked-capture')
)
$ErrorActionPreference = 'Stop'
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$sourcePaths = @((Join-Path $SourceDirectory 'Capture.cs'), (Join-Path $SourceDirectory 'CaptureRecoveryTests.cs'), (Join-Path $PSScriptRoot 'MaskedCaptureRegressionTests.cs'))
$copiedPaths = @()
$sourceHashes = @()
foreach ($source in $sourcePaths) {
    $destination = Join-Path $ReportDirectory ([IO.Path]::GetFileName($source))
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $copiedPaths += $destination
    $sourceHashes += [ordered]@{ source=$source; sourceSHA256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash; copiedSHA256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $ReportDirectory 'MaskedCaptureRegressionTests.exe'
$compilerArguments = @('/nologo','/target:exe','/platform:x64','/optimize+','/warn:4','/main:EdgeGlow.MaskedCaptureRegressionTests',('/out:'+$executable),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll') + $copiedPaths
$compileOutput = & $compiler @compilerArguments 2>&1
$compileExit = $LASTEXITCODE
$compileOutput | Set-Content -LiteralPath (Join-Path $ReportDirectory 'compiler-output.txt')
if ($compileExit -ne 0) { $compileOutput | Write-Output; throw 'Managed masked-capture compilation failed.' }
$testOutput = & $executable 2>&1
$testExit = $LASTEXITCODE
$testOutput | Set-Content -LiteralPath (Join-Path $ReportDirectory 'test-output.txt')
[ordered]@{
    recordedUtc=[DateTime]::UtcNow.ToString('o'); compiler=$compiler
    compilerSHA256=(Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
    compilerArguments=$compilerArguments; sources=$sourceHashes
    executableSHA256=(Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    compileExitCode=$compileExit; testExitCode=$testExit
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'receipt.json')
$testOutput | Write-Output
Write-Output ('Evidence saved: '+$ReportDirectory)
exit $testExit
