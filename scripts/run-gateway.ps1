<#
.SYNOPSIS
  Development: runs the branch gateway as a console app (initializing a test branch on first use).
.DESCRIPTION
  Data folder: -DataDir (default %LOCALAPPDATA%\PosSaaS\Gateway). Listens on https://0.0.0.0:<Port>.
  In another terminal, create a pairing code for each POS:
    apps\branch-gateway\Pos.Gateway\bin\Debug\net10.0-windows\Pos.Gateway.exe enroll --name "Caisse A"
  Two POS instances on one PC: start each with its own data folder, e.g.
    $env:POS_DATA_DIR = "$env:TEMP\pos-a"; .\scripts\run.ps1 -NoBuild
.EXAMPLE
  .\scripts\run-gateway.ps1
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'PosSaaS\Gateway'),
    [int]$Port = 7443,
    [string]$BranchName = 'Succursale de développement'
)

. "$PSScriptRoot\common.ps1"

$exe = Join-Path $RepoRoot "apps\branch-gateway\Pos.Gateway\bin\$Configuration\net10.0-windows\Pos.Gateway.exe"
if (-not (Test-Path $exe)) { & "$PSScriptRoot\build.ps1" -Configuration $Configuration }

$status = & $exe status --data-dir $DataDir
if ($status -match 'Non initialisée') {
    Invoke-Step 'Initialize development branch' { & $exe init --branch-id ([guid]::NewGuid()) --branch-name $BranchName --data-dir $DataDir }
}

Write-Host "Gateway data: $DataDir" -ForegroundColor Green
& $exe run --data-dir $DataDir --listen "https://0.0.0.0:$Port"
