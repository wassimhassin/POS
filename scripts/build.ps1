<#
.SYNOPSIS
  Builds the React UI and the .NET solution. The UI build is copied into the desktop app output (wwwroot).
.EXAMPLE
  .\scripts\build.ps1
  .\scripts\build.ps1 -Configuration Release
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

. "$PSScriptRoot\common.ps1"

Push-Location $WebDir
try {
    if (-not (Test-Path 'node_modules')) { Invoke-Step 'npm ci' { npm ci } }
    Invoke-Step 'Build React UI' { npm run build }
}
finally { Pop-Location }

Invoke-Step "Build .NET solution ($Configuration)" { dotnet build $Solution -c $Configuration }

Write-Host "`nBuilt: $(Get-DesktopExe $Configuration)" -ForegroundColor Green
