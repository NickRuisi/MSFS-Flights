#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time IIS setup: creates (or updates) the site and app pool, and configures the pool so
    the app stays loaded.

.DESCRIPTION
    Settings applied to the app pool, and why:
      managedRuntimeVersion = ''   "No Managed Code". ASP.NET Core doesn't use the .NET Framework CLR.
      enable32BitAppOnWin64 = off  The bundled native SQLite library is x64.
      startMode = AlwaysRunning    The worker process starts with IIS, not on the first request.
      idleTimeout = 0              Never shut down after 20 idle minutes (the IIS default).
      periodicRestart.time = 0     No forced recycle every 29 hours (the IIS default).
      loadUserProfile = true       Gives ASP.NET Core Data Protection a place to keep its keys.
    On the site:
      preloadEnabled = true        With the Application Initialization feature, IIS sends a warm-up
                                   request so the app (and its database init) loads before any user arrives.
    On the file system:
      The pool identity gets Modify on App_Data (SQLite writes airports.db plus the -wal and -shm
      side files there) and on logs (stdout logs).

    Idempotent: safe to run again.

.EXAMPLE
    .\Install-IisSite.ps1 -SiteName MsfsTelemetryWeb -PhysicalPath C:\inetpub\MsfsTelemetryWeb -Port 8080
#>
[CmdletBinding()]
param(
    [string] $SiteName     = 'MsfsTelemetryWeb',
    [string] $AppPoolName  = $SiteName,
    [string] $PhysicalPath = 'C:\inetpub\MsfsTelemetryWeb',
    [int]    $Port         = 8080
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

# --- Prerequisite: the ASP.NET Core Module, installed by the .NET Hosting Bundle ---------------
if (-not (Get-WebGlobalModule -Name 'AspNetCoreModuleV2' -ErrorAction SilentlyContinue)) {
    throw "AspNetCoreModuleV2 is not registered in IIS. Install the .NET 10 Hosting Bundle " +
          "(https://dotnet.microsoft.com/download/dotnet/10.0), then run 'iisreset' and try again."
}

# --- App pool ------------------------------------------------------------------------------------
$poolPath = "IIS:\AppPools\$AppPoolName"
if (-not (Test-Path $poolPath)) {
    Write-Host "Creating app pool '$AppPoolName'"
    New-WebAppPool -Name $AppPoolName | Out-Null
}

Set-ItemProperty $poolPath -Name managedRuntimeVersion          -Value ''
Set-ItemProperty $poolPath -Name enable32BitAppOnWin64          -Value $false
Set-ItemProperty $poolPath -Name startMode                      -Value 'AlwaysRunning'
Set-ItemProperty $poolPath -Name processModel.idleTimeout       -Value ([TimeSpan]::Zero)
Set-ItemProperty $poolPath -Name processModel.loadUserProfile   -Value $true
Set-ItemProperty $poolPath -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)

# --- Site ------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $PhysicalPath | Out-Null

$sitePath = "IIS:\Sites\$SiteName"
if (-not (Test-Path $sitePath)) {
    Write-Host "Creating site '$SiteName' on port $Port"
    New-Website -Name $SiteName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName -Port $Port | Out-Null
} else {
    Write-Host "Site '$SiteName' exists; updating its path and app pool (bindings left unchanged)"
    Set-ItemProperty $sitePath -Name physicalPath    -Value $PhysicalPath
    Set-ItemProperty $sitePath -Name applicationPool -Value $AppPoolName
}

# Preload needs the optional "Application Initialization" IIS feature. Without it, the app loads
# on the first request instead (the pool still never idles out).
if (Get-WebGlobalModule -Name 'ApplicationInitializationModule' -ErrorAction SilentlyContinue) {
    Set-ItemProperty $sitePath -Name applicationDefaults.preloadEnabled -Value $true
} else {
    Write-Warning ("IIS 'Application Initialization' is not installed, so preload is off. Install it with:`n" +
        "  Windows 10/11 : Enable-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit`n" +
        "  Windows Server: Install-WindowsFeature Web-AppInit`n" +
        "then run this script again.")
}

# --- File system permissions -------------------------------------------------------------------
$identity = "IIS AppPool\$AppPoolName"

icacls $PhysicalPath /grant "${identity}:(OI)(CI)RX" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed on $PhysicalPath" }

foreach ($folder in 'App_Data', 'logs') {
    $dir = Join-Path $PhysicalPath $folder
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    icacls $dir /grant "${identity}:(OI)(CI)M" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $dir" }
}

Write-Host ""
Write-Host "IIS is configured. Next: run .\Deploy-ToIis.ps1 -PhysicalPath '$PhysicalPath', then open http://localhost:$Port/"
