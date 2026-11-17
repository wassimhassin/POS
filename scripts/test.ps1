<#
.SYNOPSIS
  Runs all checks: UI typecheck + unit tests, .NET unit/integration tests, and the
  end-to-end restart-persistence test against the real desktop app.
.EXAMPLE
  .\scripts\test.ps1
  .\scripts\test.ps1 -SkipE2E
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipE2E
)

. "$PSScriptRoot\common.ps1"

& "$PSScriptRoot\build.ps1" -Configuration $Configuration

Push-Location $WebDir
try {
    Invoke-Step 'UI typecheck' { npm run typecheck }
    Invoke-Step 'UI unit tests (Vitest)' { npm test }
}
finally { Pop-Location }

Invoke-Step '.NET tests (xUnit)' { dotnet test $Solution -c $Configuration --no-build }

if (-not $SkipE2E) {
    Invoke-Step 'E2E restart persistence' { node (Join-Path $RepoRoot 'tests\e2e\restart-persistence.mjs') (Get-DesktopExe $Configuration) }
}

Write-Host "`nAll checks passed." -ForegroundColor Green
