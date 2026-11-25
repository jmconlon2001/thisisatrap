#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Builds ThisIsATrap and deploys it to the local IIS as an always-running site.

.DESCRIPTION
    - Enables the IIS features ThisIsATrap needs (incl. Application Initialization so the SMTP listener
      starts with IIS and isn't stopped when the site is idle).
    - Checks for the ASP.NET Core 10 Hosting Bundle (installs it with winget if missing).
    - Publishes the app, creates a dedicated app pool + site, and configures the pool to never
      idle-out or recycle on a schedule, so the SMTP listener stays up.
    - Grants the app pool write access to the data folder and opens the firewall for HTTP + SMTP.
    Re-running the script upgrades in place; stored mail and purge settings are kept.

.EXAMPLE
    .\Deploy-ThisIsATrap.ps1                              # http://<host>:8025, SMTP on 25
.EXAMPLE
    .\Deploy-ThisIsATrap.ps1 -HttpPort 80 -HostName thisisatrap.dev.local -SmtpPort 2525
#>
[CmdletBinding()]
param(
    [string]$SiteName = 'ThisIsATrap',
    [string]$SitePath = 'C:\inetpub\ThisIsATrap',
    [string]$DataPath = 'C:\inetpub\ThisIsATrapData',
    [int]$HttpPort = 8025,
    [string]$HostName = '',
    [int]$SmtpPort = 25,
    [int]$MaxMessageSizeMb = 50,
    [switch]$SkipFirewall,
    # Installs from before the rename to ThisIsATrap are migrated automatically (site removed, mail kept).
    [string]$LegacySiteName = 'MailTrap',
    [string]$LegacySitePath = 'C:\inetpub\MailTrap',
    [string]$LegacyDataPath = 'C:\inetpub\MailTrapData'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ThisIsATrap\ThisIsATrap.csproj'
$poolName = $SiteName
$poolIdentity = "IIS AppPool\$poolName"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
Step 'Checking IIS features'
$features = 'IIS-WebServerRole', 'IIS-WebServer', 'IIS-CommonHttpFeatures', 'IIS-StaticContent', 'IIS-DefaultDocument',
    'IIS-HttpErrors', 'IIS-ApplicationDevelopment', 'IIS-ApplicationInit', 'IIS-WebSockets', 'IIS-HealthAndDiagnostics',
    'IIS-HttpLogging', 'IIS-Security', 'IIS-RequestFiltering', 'IIS-Performance', 'IIS-WebServerManagementTools', 'IIS-ManagementConsole'
$isServer = (Get-CimInstance Win32_OperatingSystem).ProductType -ne 1
if ($isServer) {
    Install-WindowsFeature Web-Server, Web-Static-Content, Web-Default-Doc, Web-Http-Errors, Web-AppInit, Web-WebSockets, Web-Http-Logging, Web-Filtering, Web-Mgmt-Console | Out-Null
} else {
    $missing = $features | Where-Object { (Get-WindowsOptionalFeature -Online -FeatureName $_).State -ne 'Enabled' }
    if ($missing) {
        Write-Host "    Enabling: $($missing -join ', ')"
        Enable-WindowsOptionalFeature -Online -FeatureName $missing -All -NoRestart | Out-Null
    }
}

# ---------------------------------------------------------------------------
Step 'Checking ASP.NET Core Hosting Bundle (ASP.NET Core Module V2 + .NET 10 runtime)'
$ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
$hasRuntime = (& dotnet --list-runtimes 2>$null) -match '^Microsoft\.AspNetCore\.App 10\.'
if (-not (Test-Path $ancm) -or -not $hasRuntime) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host '    Installing Microsoft.DotNet.HostingBundle.10 via winget...'
        & winget install --id Microsoft.DotNet.HostingBundle.10 -e --silent --accept-package-agreements --accept-source-agreements
    }
    if (-not (Test-Path $ancm)) {
        throw "The ASP.NET Core 10 Hosting Bundle is required. Download it from https://dotnet.microsoft.com/download/dotnet/10.0 (Hosting Bundle), install it, then re-run this script."
    }
    Write-Host '    Restarting IIS so it picks up the ASP.NET Core Module...'
    & iisreset /restart | Out-Null
}

Import-Module WebAdministration

# ---------------------------------------------------------------------------
# Migrate a pre-rename "MailTrap" install: it holds the same ports, so it has to go first.
if ($LegacySiteName -and $LegacySiteName -ne $SiteName -and (Get-Website -Name $LegacySiteName)) {
    Step "Migrating legacy '$LegacySiteName' site"
    Stop-Website -Name $LegacySiteName -ErrorAction SilentlyContinue
    if (Test-Path "IIS:\AppPools\$LegacySiteName") {
        if ((Get-WebAppPoolState -Name $LegacySiteName).Value -ne 'Stopped') { Stop-WebAppPool -Name $LegacySiteName }
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-WebAppPoolState -Name $LegacySiteName).Value -ne 'Stopped' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    }
    Remove-Website -Name $LegacySiteName
    if (Test-Path "IIS:\AppPools\$LegacySiteName") { Remove-WebAppPool -Name $LegacySiteName }
    Get-NetFirewallRule -DisplayName "$LegacySiteName SMTP", "$LegacySiteName Web" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    # The old app folder only holds published binaries; remove it if it is recognisably ours.
    if ((Test-Path (Join-Path $LegacySitePath 'MailTrap.dll')) -and $LegacySitePath -ne $SitePath) {
        Remove-Item $LegacySitePath -Recurse -Force
    }
}
if ((Test-Path $LegacyDataPath) -and $LegacyDataPath -ne $DataPath -and -not (Test-Path (Join-Path $DataPath 'thisisatrap.db'))) {
    Step "Moving stored mail from $LegacyDataPath to $DataPath"
    New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
    & robocopy $LegacyDataPath $DataPath /E /MOVE /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE while moving $LegacyDataPath" }
    # The app renames mailtrap.db to thisisatrap.db on first start.
}

# ---------------------------------------------------------------------------
Step 'Checking SMTP port'
$owner = Get-NetTCPConnection -State Listen -LocalPort $SmtpPort -ErrorAction SilentlyContinue | Select-Object -First 1
if ($owner) {
    $proc = Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue
    $isOurs = $proc -and $proc.ProcessName -eq 'w3wp' -and (Get-WebAppPoolState -Name $poolName -ErrorAction SilentlyContinue)
    if (-not $isOurs) {
        $svc = Get-Service SMTPSVC -ErrorAction SilentlyContinue
        $hint = if ($svc -and $svc.Status -eq 'Running') { ' The IIS 6 SMTP service (SMTPSVC) is running; stop/disable it or choose another -SmtpPort.' } else { '' }
        throw "Port $SmtpPort is already in use by $($proc.ProcessName) (PID $($owner.OwningProcess)).$hint"
    }
}

# ---------------------------------------------------------------------------
Step 'Publishing app'
$publishDir = Join-Path $env:TEMP "ThisIsATrap-publish-$([guid]::NewGuid().ToString('N'))"
& dotnet publish $project -c Release -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Production settings for this server.
$settingsPath = Join-Path $publishDir 'appsettings.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$settings.ThisIsATrap.SmtpPort = $SmtpPort
$settings.ThisIsATrap.DataPath = $DataPath
$settings.ThisIsATrap.MaxMessageSizeMb = $MaxMessageSizeMb
$settings.ThisIsATrap.ServerName = if ($HostName) { $HostName } else { [System.Net.Dns]::GetHostEntry('').HostName }
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8

# ---------------------------------------------------------------------------
Step 'Configuring app pool'
if (-not (Test-Path "IIS:\AppPools\$poolName")) { New-WebAppPool -Name $poolName | Out-Null }
$pool = "IIS:\AppPools\$poolName"
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''                 # No managed code (ASP.NET Core)
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'                # Start with IIS so SMTP is listening right away
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero) # Never shut down when idle
Set-ItemProperty $pool -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero) # No 29-hour recycle
try { Clear-ItemProperty $pool -Name recycling.periodicRestart.schedule } catch { }
Set-ItemProperty $pool -Name recycling.disallowOverlappingRotation -Value $true  # Old worker releases the SMTP port before the new one starts
Set-ItemProperty $pool -Name failure.rapidFailProtection -Value $false

# Stop before swapping files (releases DLL locks and the SMTP port).
if ((Get-WebAppPoolState -Name $poolName).Value -ne 'Stopped') {
    Stop-WebAppPool -Name $poolName
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-WebAppPoolState -Name $poolName).Value -ne 'Stopped' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}

# ---------------------------------------------------------------------------
Step "Copying files to $SitePath"
New-Item -ItemType Directory -Force -Path $SitePath, $DataPath | Out-Null
& robocopy $publishDir $SitePath /MIR /NFL /NDL /NJH /NJS /NP /XD App_Data logs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
Remove-Item $publishDir -Recurse -Force

# Data folder: the app pool identity needs to create/delete .eml files and the SQLite database.
& icacls $DataPath /grant "${poolIdentity}:(OI)(CI)M" /T /Q | Out-Null
& icacls $SitePath /grant "${poolIdentity}:(OI)(CI)RX" /T /Q | Out-Null

# ---------------------------------------------------------------------------
Step 'Configuring site'
if (-not (Get-Website -Name $SiteName)) {
    New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $poolName -Port $HttpPort -HostHeader $HostName | Out-Null
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SitePath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $poolName
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name bindings -Value @{ protocol = 'http'; bindingInformation = "*:${HttpPort}:$HostName" }
}
Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true  # Warm up on start (Application Initialization)
# Application Initialization requests the liveness endpoint when the pool starts, so the app (and SMTP) come up before any user visits.
$initFilter = 'system.webServer/applicationInitialization'
Clear-WebConfiguration -PSPath "IIS:\Sites\$SiteName" -Filter "$initFilter/add" -ErrorAction SilentlyContinue
Add-WebConfigurationProperty -PSPath "IIS:\Sites\$SiteName" -Filter $initFilter -Name '.' -Value @{ initializationPage = '/healthz' }

# ---------------------------------------------------------------------------
if (-not $SkipFirewall) {
    Step 'Opening firewall'
    foreach ($rule in @(@{ Name = 'ThisIsATrap SMTP'; Port = $SmtpPort }, @{ Name = 'ThisIsATrap Web'; Port = $HttpPort })) {
        Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol TCP -LocalPort $rule.Port -Action Allow -Profile Domain, Private | Out-Null
    }
}

# ---------------------------------------------------------------------------
Step 'Starting'
Start-WebAppPool -Name $poolName
if ((Get-WebsiteState -Name $SiteName).Value -ne 'Started') { Start-Website -Name $SiteName }
$url = "http://$(if ($HostName) { $HostName } else { 'localhost' }):$HttpPort/"

$ok = $false
$ready = $null
for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
    try {
        # /readyz returns 200 once SMTP is listening, the database answers and the data folder is writable (503 otherwise).
        $ready = Invoke-RestMethod "${url}readyz" -TimeoutSec 5
        $ok = $true
    } catch {
        if ($_.ErrorDetails.Message) { try { $ready = $_.ErrorDetails.Message | ConvertFrom-Json } catch { } }
        Start-Sleep 1
    }
}

Write-Host ''
if ($ok) {
    Write-Host "ThisIsATrap is running." -ForegroundColor Green
    Write-Host "  Web UI : $url"
    Write-Host "  Status : ${url}#status   (probes: ${url}healthz, ${url}readyz, ${url}api/status)"
    if ($ready.status -eq 'Degraded') { Write-Warning "Ready but degraded: $(($ready.checks | Where-Object status -ne 'Healthy' | ForEach-Object { "$($_.name): $($_.description)" }) -join '; ')" }
    Write-Host "  SMTP   : $($env:COMPUTERNAME):$SmtpPort  (no auth / no TLS; any credentials accepted)"
    Write-Host "  Data   : $DataPath"
    Write-Host "  Test   : & '$repoRoot\tools\Send-TestMail.ps1' -Port $SmtpPort -Count 3"
} else {
    $failing = if ($ready.checks) { ($ready.checks | Where-Object status -eq 'Unhealthy' | ForEach-Object { "$($_.name): $($_.description)" }) -join '; ' } else { 'no response' }
    Write-Warning "The site started but is not ready ($failing). Check ${url}#status and the Windows Event Log (Application, source 'IIS AspNetCore Module V2')."
}
