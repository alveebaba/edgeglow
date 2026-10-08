$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDirectory = Join-Path $projectRoot 'bin'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
& (Join-Path $projectRoot 'build-native.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $outputDirectory 'EdgeGlowCapture.dll'))) { throw 'Native capture library missing.' }
$sources = @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /unsafe+ /warn:4 ("/out:" + (Join-Path $outputDirectory 'EdgeGlow.exe')) ("/win32manifest:" + (Join-Path $projectRoot 'app.manifest')) /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'EdgeGlow build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'EdgeGlow.exe.config') -Destination $outputDirectory -Force
Write-Output (Get-Item -LiteralPath (Join-Path $outputDirectory 'EdgeGlow.exe') | Select-Object FullName,Length)
