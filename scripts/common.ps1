# Shared helpers for the build/run/test scripts.
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$WebDir = Join-Path $RepoRoot 'apps\pos-web'
$Solution = Join-Path $RepoRoot 'PosSaaS.slnx'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# A freshly installed SDK is not on PATH until a new terminal is opened.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $candidate = Join-Path $env:ProgramFiles 'dotnet'
    if (Test-Path (Join-Path $candidate 'dotnet.exe')) { $env:PATH = "$candidate;$env:PATH" }
    else { throw '.NET SDK not found. Install the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10).' }
}

function Invoke-Step([string]$Name, [scriptblock]$Command) {
    Write-Host "`n==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $Name (exit code $LASTEXITCODE)" }
}

function Get-DesktopExe([string]$Configuration) {
    Join-Path $RepoRoot "apps\windows-pos\Pos.Desktop\bin\$Configuration\net10.0-windows\Pos.Desktop.exe"
}
