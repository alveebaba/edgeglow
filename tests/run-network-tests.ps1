param([switch]$C02Only)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$runName = 'network-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$evidence = Join-Path (Join-Path $projectRoot 'review-output\validation') $runName
New-Item -ItemType Directory -Path $evidence | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'Network.cs') -Destination (Join-Path $evidence 'Network.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NetworkRegressionTests.cs') -Destination (Join-Path $evidence 'NetworkRegressionTests.cs')
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $evidence 'run-network-tests.ps1')
$testOutput = Join-Path $evidence 'NetworkRegressionTests.exe'
$compileExit = $null
$testExit = $null
$started = [DateTime]::UtcNow.ToString('o')
try {
    $ErrorActionPreference = 'Continue'
    $compileText = & $compiler /nologo /target:exe "/out:$testOutput" /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $evidence 'Network.cs') (Join-Path $evidence 'NetworkRegressionTests.cs') 2>&1
    $compileExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $compileText | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'compile.log')
    if ($compileExit -ne 0) { throw "Network regression compilation failed: $compileExit" }
    $testArguments = @()
    if ($C02Only) { $testArguments += '--c02' }
    $ErrorActionPreference = 'Continue'
    $testText = & $testOutput @testArguments 2>&1
    $testExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $testText | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'test.log')
    $testText | ForEach-Object { Write-Output $_ }
    if ($testExit -ne 0) { throw "Network regression execution failed: $testExit" }
} finally {
    $result = [ordered]@{ StartedUtc=$started; EndedUtc=[DateTime]::UtcNow.ToString('o'); CompileExit=$compileExit; TestExit=$testExit; C02Only=[bool]$C02Only; Scope='Actual production Network.cs with fake sockets/interfaces/clock; no LAN or lights' }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
    $hashes = Get-ChildItem -LiteralPath $evidence -File | ForEach-Object { $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256; [pscustomobject]@{ File=$_.Name; SHA256=$hash.Hash } }
    $hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'SHA256.json')
    Write-Output "Evidence: $evidence"
}
