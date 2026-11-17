<#
.SYNOPSIS
  Builds (unless -NoBuild) and starts the Windows POS prototype.
.PARAMETER UiUrl
  Debug only: load the UI from the Vite dev server instead of the packaged build,
  e.g. -UiUrl http://localhost:5173 (start it with: npm run dev --prefix apps/pos-web).
.EXAMPLE
  .\scripts\run.ps1
  .\scripts\run.ps1 -NoBuild
  .\scripts\run.ps1 -UiUrl http://localhost:5173
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [string]$UiUrl
)

. "$PSScriptRoot\common.ps1"

if (-not $NoBuild) { & "$PSScriptRoot\build.ps1" -Configuration $Configuration }

$exe = Get-DesktopExe $Configuration
if (-not (Test-Path $exe)) { throw "Not built yet: $exe. Run scripts\build.ps1 first." }

$arguments = @()
if ($UiUrl) { $arguments += "--ui-url=$UiUrl" }

Write-Host "Starting $exe" -ForegroundColor Green
Write-Host "Data and logs: $(if ($env:POS_DATA_DIR) { $env:POS_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'PosSaaS' })"
if ($arguments.Count -gt 0) { Start-Process -FilePath $exe -ArgumentList $arguments } else { Start-Process -FilePath $exe }
