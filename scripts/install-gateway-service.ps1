<#
.SYNOPSIS
  Installs the branch gateway as a Windows Service on the designated gateway PC (prototype).
.DESCRIPTION
  - Publishes Pos.Gateway (Release) to -InstallDir.
  - Creates the data folder with an ACL limited to SYSTEM, Administrators and the service's virtual account.
  - Initializes the branch identity (once).
  - Registers the service under the virtual account "NT SERVICE\PosSaaSGateway" (no password, least privilege),
    delayed automatic start, restart on failure.
  - Opens the port in Windows Firewall for the PRIVATE profile and the local subnet only.
  The TLS certificate is created by the service account on first start (DPAPI-protected for that account).
  Then pair each terminal:  & "$InstallDir\Pos.Gateway.exe" enroll --name "Caisse 1" --data-dir $DataDir

  NOT EXECUTED during prototype validation (requires an elevated session); see docs/06-LAN-GATEWAY-PROTOTYPE.md §8.
.EXAMPLE
  .\scripts\install-gateway-service.ps1 -BranchId 3f2c…-… -BranchName "Centre-ville"
#>
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)] [string]$BranchId,
    [Parameter(Mandatory)] [string]$BranchName,
    [string]$InstallDir = "$env:ProgramFiles\PosSaaS\Gateway",
    [string]$DataDir = "$env:ProgramData\PosSaaS\Gateway",
    [int]$Port = 7443
)

. "$PSScriptRoot\common.ps1"
$service = 'PosSaaSGateway'
$account = "NT SERVICE\$service"

Invoke-Step 'Publish gateway (Release)' {
    dotnet publish (Join-Path $RepoRoot 'apps\branch-gateway\Pos.Gateway\Pos.Gateway.csproj') -c Release -o $InstallDir
}
$exe = Join-Path $InstallDir 'Pos.Gateway.exe'

New-Item -ItemType Directory -Force $DataDir | Out-Null
Invoke-Step 'Initialize branch identity' { & $exe init --branch-id $BranchId --branch-name $BranchName --data-dir $DataDir }

Invoke-Step 'Register service' {
    sc.exe create $service binPath= "`"$exe`" run --data-dir `"$DataDir`" --listen https://0.0.0.0:$Port" start= delayed-auto obj= $account DisplayName= 'PosSaaS Branch Gateway'
}
sc.exe failure $service reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null

# Data folder: no inheritance; SYSTEM + Administrators + the service account only.
icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' "${account}:(OI)(CI)M" | Out-Null

New-NetFirewallRule -DisplayName 'PosSaaS Branch Gateway (HTTPS)' -Direction Inbound -Protocol TCP -LocalPort $Port `
    -Profile Private -RemoteAddress LocalSubnet -Action Allow | Out-Null

Invoke-Step 'Start service' { sc.exe start $service }
Write-Host "Gateway installed. Pair terminals with: `"$exe`" enroll --name `"Caisse 1`" --data-dir `"$DataDir`"" -ForegroundColor Green
