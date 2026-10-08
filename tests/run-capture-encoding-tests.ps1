param(
    [string]$NativeSource = (Join-Path $PSScriptRoot '..\NativeCapture.cpp'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\test-output\capture-encoding')
)
$ErrorActionPreference = 'Stop'
$NativeSource = [IO.Path]::GetFullPath($NativeSource)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$testSource = Join-Path $PSScriptRoot 'CaptureEncodingRegression.cpp'
$copiedTest = Join-Path $OutputDirectory 'CaptureEncodingRegression.cpp'
$copiedNative = Join-Path $OutputDirectory 'NativeCaptureUnderTest.cpp'
Copy-Item -LiteralPath $testSource -Destination $copiedTest -Force
Copy-Item -LiteralPath $NativeSource -Destination $copiedNative -Force
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
if (-not $toolset) { throw 'Visual Studio 2022 C++ build tools are required to build the native capture regressions.' }
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\d3d11.h') } | Select-Object -First 1
if (-not $sdk) { throw 'The Windows 10/11 C++ SDK is required to build the native capture regressions.' }
$compiler = Join-Path $toolset.FullName 'bin\Hostx64\x64\cl.exe'
$priorInclude = $env:INCLUDE; $priorLib = $env:LIB
$executable = Join-Path $OutputDirectory 'CaptureEncodingRegression.exe'
$argsForCompiler = @('/nologo','/O2','/MT','/EHsc','/std:c++17','/W4','/DUNICODE','/D_UNICODE','/D_WIN32_WINNT=0x0A00',('/Fo'+(Join-Path $OutputDirectory 'CaptureEncodingRegression.obj')),('/Fe'+$executable),$copiedTest,'/link','/MACHINE:X64','/DYNAMICBASE','/NXCOMPAT',('/IMPLIB:'+(Join-Path $OutputDirectory 'CaptureEncodingRegression.lib')),'d3d11.lib','dxgi.lib','d3dcompiler.lib','user32.lib')
try {
    $env:INCLUDE = @((Join-Path $toolset.FullName 'include'),(Join-Path $sdk.FullName 'ucrt'),(Join-Path $sdk.FullName 'shared'),(Join-Path $sdk.FullName 'um'),(Join-Path $sdk.FullName 'winrt')) -join ';'
    $sdkLib = Join-Path (Join-Path $sdkRoot 'Lib') $sdk.Name
    $env:LIB = @((Join-Path $toolset.FullName 'lib\x64'),(Join-Path $sdkLib 'ucrt\x64'),(Join-Path $sdkLib 'um\x64')) -join ';'
    $buildOutput = & $compiler @argsForCompiler 2>&1
    $compileExit = $LASTEXITCODE
    $buildOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-output.txt')
    if ($compileExit -ne 0) { $buildOutput | Write-Output; throw 'Native capture regression build failed.' }
    $testOutput = & $executable 2>&1
    $testExit = $LASTEXITCODE
    $testOutput | Set-Content -LiteralPath (Join-Path $OutputDirectory 'test-output.txt')
    $receipt = [ordered]@{
        recordedUtc = [DateTime]::UtcNow.ToString('o'); nativeSource = $NativeSource
        nativeSourceSHA256 = (Get-FileHash -LiteralPath $NativeSource -Algorithm SHA256).Hash
        copiedNativeSHA256 = (Get-FileHash -LiteralPath $copiedNative -Algorithm SHA256).Hash
        testSourceSHA256 = (Get-FileHash -LiteralPath $testSource -Algorithm SHA256).Hash
        copiedTestSHA256 = (Get-FileHash -LiteralPath $copiedTest -Algorithm SHA256).Hash
        compiler = $compiler; compilerSHA256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
        toolset = $toolset.FullName; sdkInclude = $sdk.FullName; sdkLibrary = $sdkLib
        compilerArguments = $argsForCompiler; executableSHA256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        compileExitCode = $compileExit; testExitCode = $testExit
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'receipt.json')
    $testOutput | Write-Output
    Write-Output ('Evidence saved: '+$OutputDirectory)
    exit $testExit
} finally { $env:INCLUDE = $priorInclude; $env:LIB = $priorLib }
