$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$outputDirectory = Join-Path $projectRoot 'bin'
$toolRoots = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Tools\MSVC',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Tools\MSVC'
)
$toolset = $null
foreach ($toolRoot in $toolRoots) {
    if (Test-Path -LiteralPath $toolRoot) {
        $toolset = Get-ChildItem -LiteralPath $toolRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($toolset) { break }
    }
}
if (-not $toolset) { throw 'Visual Studio 2022 C++ build tools are required to build EdgeGlowCapture.dll.' }
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\d3d11.h') } | Select-Object -First 1
if (-not $sdk) { throw 'The Windows 10/11 C++ SDK is required to build EdgeGlowCapture.dll.' }
$compiler = Join-Path $toolset.FullName 'bin\Hostx64\x64\cl.exe'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$previousInclude = $env:INCLUDE
$previousLib = $env:LIB
try {
    $env:INCLUDE = @((Join-Path $toolset.FullName 'include'), (Join-Path $sdk.FullName 'ucrt'), (Join-Path $sdk.FullName 'shared'), (Join-Path $sdk.FullName 'um'), (Join-Path $sdk.FullName 'winrt')) -join ';'
    $sdkLib = Join-Path (Join-Path $sdkRoot 'Lib') $sdk.Name
    $env:LIB = @((Join-Path $toolset.FullName 'lib\x64'), (Join-Path $sdkLib 'ucrt\x64'), (Join-Path $sdkLib 'um\x64')) -join ';'
    $compilerArgs = @('/nologo', '/LD', '/O2', '/MT', '/EHsc', '/std:c++17', '/W4', '/DUNICODE', '/D_UNICODE', '/D_WIN32_WINNT=0x0A00', ('/Fo' + (Join-Path $outputDirectory 'NativeCapture.obj')), ('/Fe' + (Join-Path $outputDirectory 'EdgeGlowCapture.dll')), (Join-Path $projectRoot 'NativeCapture.cpp'), '/link', '/MACHINE:X64', '/DYNAMICBASE', '/NXCOMPAT', 'd3d11.lib', 'dxgi.lib', 'd3dcompiler.lib', 'user32.lib')
    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'EdgeGlow GPU capture build failed.' }
} finally {
    $env:INCLUDE = $previousInclude
    $env:LIB = $previousLib
}
Write-Output (Get-Item -LiteralPath (Join-Path $outputDirectory 'EdgeGlowCapture.dll') | Select-Object FullName, Length)
