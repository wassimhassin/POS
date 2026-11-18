<#
.SYNOPSIS
  Runs all checks: UI typecheck + unit tests, .NET tests (Debug and Release), and the end-to-end
  tests against the real desktop app.
.DESCRIPTION
  E2E tests:
    restart-persistence / print-queue  Debug build (drives the UI over WebView2 remote debugging)
    release-no-remote-debugging         Release build (verifies remote debugging cannot be enabled)
    lan-gateway                         Debug build: two POS instances + a real gateway process
.EXAMPLE
  .\scripts\test.ps1
  .\scripts\test.ps1 -SkipE2E
#>
param([switch]$SkipE2E)

. "$PSScriptRoot\common.ps1"

& "$PSScriptRoot\build.ps1" -Configuration Debug

Push-Location $WebDir
try {
    Invoke-Step 'UI typecheck' { npm run typecheck }
    Invoke-Step 'UI unit tests (Vitest)' { npm test }
}
finally { Pop-Location }

Invoke-Step '.NET tests, Debug (xUnit)' { dotnet test $Solution -c Debug --no-build }
Invoke-Step 'Build .NET solution (Release)' { dotnet build $Solution -c Release }
Invoke-Step '.NET tests, Release (xUnit)' { dotnet test $Solution -c Release --no-build }

if (-not $SkipE2E) {
    $e2e = Join-Path $RepoRoot 'tests\e2e'
    Get-ChildItem $e2e -Filter '*.mjs' | Where-Object Name -ne 'lib.mjs' | ForEach-Object {
        $test = $_.FullName
        Invoke-Step "E2E $($_.BaseName)" { node $test }
    }
}

Write-Host "`nAll checks passed." -ForegroundColor Green
