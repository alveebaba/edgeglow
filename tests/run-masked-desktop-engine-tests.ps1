param([string]$ReportDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([String]::IsNullOrWhiteSpace($ReportDirectory)) { $ReportDirectory = Join-Path $root ('review-output\masked-desktop-engine-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')) }
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Report directory exists; choose a new one.' }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$sourceDirectory = Join-Path $ReportDirectory 'source'
New-Item -ItemType Directory -Path $sourceDirectory | Out-Null
Get-ChildItem -LiteralPath $root -Filter '*.cs' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $sourceDirectory }
$name = 'MaskedDesktopEngineTests'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot ($name+'.cs')) -Destination $sourceDirectory
Copy-Item -LiteralPath $PSCommandPath -Destination $ReportDirectory
$hashes = Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object {
 [ordered]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'input-hashes.json') -Encoding UTF8
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$output = Join-Path $ReportDirectory ($name+'.exe')
$testSource = Join-Path $sourceDirectory ($name+'.cs')
$compilerArgs = @('/nologo','/target:exe','/platform:x64','/optimize+','/unsafe+','/warn:4',('/main:EdgeGlow.'+$name),('/out:'+$output),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll') + $source
$compilerArgs | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory ($name+'-compiler-arguments.json')) -Encoding UTF8
$compiled = & $compiler @compilerArgs 2>&1
$compileExit = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $ReportDirectory ($name+'-compiler-output.txt')), [String]($compiled -join [Environment]::NewLine))
if ($compileExit -ne 0) { $compiled | Write-Output; throw ('Compilation failed: '+$name) }
$process = New-Object System.Diagnostics.Process
$process.StartInfo.FileName = $output
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$process.Start() | Out-Null
$stdout = $process.StandardOutput.ReadToEnd()
$stderr = $process.StandardError.ReadToEnd()
$process.WaitForExit()
$runExit = $process.ExitCode
$process.Dispose()
($stdout+$stderr) | Set-Content -LiteralPath (Join-Path $ReportDirectory ($name+'-results.txt')) -Encoding UTF8
[ordered]@{name=$name;compileExit=$compileExit;runExit=$runExit;binarySha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant();testSourceSha256=(Get-FileHash -LiteralPath $testSource -Algorithm SHA256).Hash.ToLowerInvariant();bounds='Real production ScreenSampler, crop/color processing and GlowEngine with synthetic safe masked pixels/metadata, fake time and in-memory transport; no native DLL, Windows capture, protected playback, LAN delivery, GUI, settings, startup or physical acceptance.'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory ($name+'-receipt.json')) -Encoding UTF8
($stdout+$stderr) | Write-Output
if ($runExit -ne 0) { throw ('Tests failed: '+$name) }
Write-Output ('Masked desktop engine artifacts: '+$ReportDirectory)
