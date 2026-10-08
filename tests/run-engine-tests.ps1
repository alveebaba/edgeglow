# Compatibility entry point. The former fake-detector harness is historical only.
[CmdletBinding()]
param([switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'run-regression-tests.ps1') -CompileOnly:$CompileOnly
