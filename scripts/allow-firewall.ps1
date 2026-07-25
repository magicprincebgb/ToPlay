<#
.SYNOPSIS
  Makes ToPlay reachable from your phone over the LAN by:
    1. Making sure your connected Wi-Fi/Ethernet is on a "Private" profile
       (Windows blocks most inbound traffic on "Public" networks).
    2. Opening the Windows Firewall for ToPlay's HTTP/HTTPS ports.
    3. Printing the exact URL to open on your phone.

.NOTES
  Just run it — it asks for Administrator rights automatically:
    powershell -ExecutionPolicy Bypass -File scripts\allow-firewall.ps1

  Ports are read from data\config.json if it exists (defaults: 8080 / 8443).
#>
param(
    # Set by the self-elevation relaunch so the elevated window doesn't pause.
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'

# --- self-elevate ----------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting Administrator rights (approve the UAC prompt)..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    )
    exit
}

# --- figure out the ports (read config.json if we can find it) -------------
$ports = @(8080, 8443)
$cfgCandidates = @(
    (Join-Path $PSScriptRoot '..\src\ToPlay.Host\bin\Release\net8.0\data\config.json'),
    (Join-Path $PSScriptRoot '..\src\ToPlay.Host\bin\Debug\net8.0\data\config.json')
)
foreach ($c in $cfgCandidates) {
    if (Test-Path $c) {
        try {
            $j = Get-Content $c -Raw | ConvertFrom-Json
            $p = @($j.HttpPort, $j.HttpsPort) | Where-Object { $_ -gt 0 }
            if ($p.Count -gt 0) { $ports = $p }
            break
        }
        catch { }
    }
}

# --- switch any connected "Public" network to "Private" --------------------
# Home Wi-Fi shows up as "Public" surprisingly often; firewall rules for
# Private/Domain then never apply. A home network should be Private anyway.
$publicProfiles = Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' }
foreach ($p in $publicProfiles) {
    try {
        Set-NetConnectionProfile -InterfaceIndex $p.InterfaceIndex -NetworkCategory Private
        Write-Host "Set network '$($p.Name)' to Private." -ForegroundColor Green
    }
    catch {
        Write-Warning "Could not set '$($p.Name)' to Private: $($_.Exception.Message)"
    }
}

# --- open the firewall -----------------------------------------------------
foreach ($port in $ports) {
    $name = "ToPlay ($port)"
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $port -Profile Private, Domain, Public | Out-Null
    Write-Host "Opened inbound TCP $port." -ForegroundColor Green
}

# --- allow the stream itself (UDP on random ports) -------------------------
# The web pages travel over TCP, but video, sound and touches travel over UDP
# on ports Windows picks at random, so a port rule can never cover them. A rule
# for the host program does — and it must apply to "Public" too, because that is
# what a phone's hotspot always looks like to Windows.
$hostExe = @(
    (Join-Path $PSScriptRoot '..\src\ToPlay.Host\bin\Release\net8.0\ToPlay.Host.exe'),
    (Join-Path $PSScriptRoot '..\src\ToPlay.Host\bin\Debug\net8.0\ToPlay.Host.exe'),
    'C:\Program Files\ToPlay\ToPlay.Host.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($hostExe) {
    $hostExe = (Resolve-Path $hostExe).Path
    foreach ($proto in 'UDP', 'TCP') {
        $name = "ToPlay stream ($proto)"
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow `
            -Protocol $proto -Program $hostExe -Profile Private, Domain, Public | Out-Null
    }
    Write-Host "Allowed the ToPlay stream (UDP + TCP) for $hostExe." -ForegroundColor Green
}
else {
    Write-Warning "ToPlay.Host.exe not found - build the host (or install ToPlay), then re-run this script."
}

# --- tell the user the correct URL -----------------------------------------
# Prefer the adapter that actually has a default gateway (the real LAN NIC),
# skipping Hyper-V / VirtualBox / VMware virtual adapters.
$ip = Get-NetIPConfiguration |
    Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
    Select-Object -First 1 -ExpandProperty IPv4Address |
    Select-Object -ExpandProperty IPAddress
$httpsPort = ($ports | Measure-Object -Maximum).Maximum

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Cyan
if ($ip) {
    Write-Host "  On your phone (same Wi-Fi) open:  https://$ip`:$httpsPort/" -ForegroundColor Cyan
}
else {
    Write-Host "  Could not auto-detect your LAN IP. Run 'ipconfig' and use the" -ForegroundColor Yellow
    Write-Host "  IPv4 address of your Wi-Fi/Ethernet adapter with port $httpsPort." -ForegroundColor Yellow
}
Write-Host "  (First time on iPhone: accept/trust the self-signed certificate.)" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host ""

if (-not $NoPause) { Read-Host "Done. Press Enter to close" | Out-Null }
