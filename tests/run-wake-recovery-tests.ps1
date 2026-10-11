param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('test-output\wake-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory; existing evidence will not be overwritten.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler not found.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$inputDirectory = Join-Path $OutputDirectory 'source'
[void][IO.Directory]::CreateDirectory($inputDirectory)
Get-ChildItem -LiteralPath $root -Filter '*.cs' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $inputDirectory }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WakeRecoveryRegressionTests.cs') -Destination $inputDirectory
$sources = @(Get-ChildItem -LiteralPath $inputDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object {$_.FullName})
$exe = Join-Path $OutputDirectory 'WakeRecoveryRegressionTests.exe'
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/unsafe+','/warn:4','/main:EdgeGlow.WakeRecoveryRegressionTests',('/out:'+$exe),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll') + $sources
$compilerArgs | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'compiler-arguments.json') -Encoding UTF8
$sourceHashes = @($sources | ForEach-Object {[pscustomobject]@{name=[IO.Path]::GetFileName($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
$sourceHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-hashes.json') -Encoding UTF8
$previousPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    & $compiler @compilerArgs 1> (Join-Path $OutputDirectory 'compiler-stdout.txt') 2> (Join-Path $OutputDirectory 'compiler-stderr.txt')
    $buildExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $previousPreference }
if ($buildExit -ne 0) { throw "Compilation failed ($buildExit); see compiler output in $OutputDirectory." }
# Only this fixture's Main runs: generated capture, injected time, in-memory LAN.
# No Program.Main, native capture, GUI, registry, installed app or physical strip.
$traces = Join-Path $OutputDirectory 'traces'
$process = New-Object System.Diagnostics.Process
$process.StartInfo.FileName = $exe
$process.StartInfo.Arguments = '"' + $traces + '"'
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
[void]$process.Start()
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$timedOut = -not $process.WaitForExit(120000)
if ($timedOut) { $process.Kill(); $process.WaitForExit() }
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'test-stdout.txt'),$stdout.Result,$utf8)
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'test-stderr.txt'),$stderr.Result,$utf8)
$receipt = [ordered]@{utc=[DateTime]::UtcNow.ToString('o');compiler=$compiler;compilerSha256=(Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant();buildExit=$buildExit;testExit=$process.ExitCode;timedOut=$timedOut;binarySha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();scope='Isolated production-component fixture with modeled delivery/firmware; no physical acceptance.'}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'RESULTS.json') -Encoding UTF8
Write-Output $stdout.Result
if ($timedOut -or $process.ExitCode -ne 0) { throw "Wake tests failed; see $OutputDirectory." }
