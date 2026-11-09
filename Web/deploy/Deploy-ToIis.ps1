<#
.SYNOPSIS
    Publishes the app and copies it into the IIS site folder without touching the database.

.DESCRIPTION
    1. dotnet publish (Release) into .\publish
    2. Writes app_offline.htm. ANCM then shuts the app down cleanly, which releases the file locks
       on the DLLs and the SQLite database.
    3. Mirrors the publish output into the site, skipping App_Data (the live database) and logs.
    4. Deletes app_offline.htm. The next request (or preload) starts the new build.

    Writing under C:\inetpub usually needs an elevated prompt.

.EXAMPLE
    .\Deploy-ToIis.ps1 -PhysicalPath C:\inetpub\MsfsTelemetryWeb
#>
[CmdletBinding()]
param(
    [string] $PhysicalPath  = 'C:\inetpub\wwwroot\FSFlights',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectDir = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $projectDir 'publish'

Write-Host "Publishing to $publishDir"
dotnet publish (Join-Path $projectDir 'FSFlightsWeb.csproj') -c $Configuration -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

if (-not (Test-Path $PhysicalPath)) {
    throw "Site folder '$PhysicalPath' does not exist. Run Install-IisSite.ps1 first."
}

$offline = Join-Path $PhysicalPath 'app_offline.htm'
Set-Content -Path $offline -Encoding ASCII -Value '<!DOCTYPE html><html><body><h1>Updating, back in a moment...</h1></body></html>'

try {
    # Give ANCM a moment to notice app_offline.htm and stop the app.
    Start-Sleep -Seconds 3

    # /MIR mirrors the folder (and deletes stale files). /XD and /XF protect the database, the logs
    # and app_offline.htm from being deleted.
    robocopy $publishDir $PhysicalPath /MIR /XD App_Data logs /XF app_offline.htm /R:5 /W:2 /NP /NFL /NDL /NJH
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit code $LASTEXITCODE)." }
}
finally {
    Remove-Item $offline -ErrorAction SilentlyContinue
}

Write-Host "Deployed to $PhysicalPath"
