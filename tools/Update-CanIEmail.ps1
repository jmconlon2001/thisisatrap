<#
.SYNOPSIS
    Refreshes the bundled email-client support data used by the HTML check.

.DESCRIPTION
    Downloads https://www.caniemail.com/api/data.json (Can I email, MIT License,
    https://github.com/hteumeuleu/caniemail) and writes a trimmed copy to
    src/ThisIsATrap/Data/caniemail.json: for every feature it keeps the title, category,
    URL, notes and only the most recent test result per client platform.
#>
param(
    [string]$Source = 'https://www.caniemail.com/api/data.json',
    [string]$OutFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/ThisIsATrap/Data/caniemail.json')
)
$ErrorActionPreference = 'Stop'

$raw = Invoke-RestMethod $Source
$features = foreach ($f in $raw.data) {
    $stats = [ordered]@{}
    foreach ($family in $f.stats.PSObject.Properties) {
        $platforms = [ordered]@{}
        foreach ($platform in $family.Value.PSObject.Properties) {
            $versions = @($platform.Value.PSObject.Properties)
            if ($versions.Count -gt 0) { $platforms[$platform.Name] = [string]$versions[-1].Value }   # newest test last
        }
        if ($platforms.Count) { $stats[$family.Name] = $platforms }
    }
    $notes = [ordered]@{}
    if ($f.notes_by_num) { foreach ($n in $f.notes_by_num.PSObject.Properties) { $notes[$n.Name] = [string]$n.Value } }
    [ordered]@{
        slug     = $f.slug
        title    = $f.title
        category = $f.category
        url      = $f.url
        notes    = $notes
        stats    = $stats
    }
}

$out = [ordered]@{
    source     = 'https://www.caniemail.com (Can I email, MIT License, https://github.com/hteumeuleu/caniemail)'
    lastUpdate = $raw.last_update_date
    families   = $raw.nicenames.family
    platforms  = $raw.nicenames.platform
    features   = @($features)
}

New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
[IO.File]::WriteAllText($OutFile, ($out | ConvertTo-Json -Depth 8 -Compress), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($features.Count) features ($([math]::Round((Get-Item $OutFile).Length / 1KB)) KB) to $OutFile"
