<#
.SYNOPSIS
    Runs the unit and integration tests with code coverage, merges the results and enforces a minimum line coverage.
.EXAMPLE
    ./tools/Test-Coverage.ps1                # all tests, fail under 80%
    ./tools/Test-Coverage.ps1 -Threshold 85
    ./tools/Test-Coverage.ps1 -NoBuild
#>
param(
    [int]$Threshold = 80,
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $root 'TestResults'
if (Test-Path $results) { Remove-Item $results -Recurse -Force }

Push-Location $root
try {
    if (-not $NoBuild) {
        dotnet build ThisIsATrap.sln -c $Configuration -warnaserror -nologo -v q
        if ($LASTEXITCODE) { throw 'Build failed.' }
    }
    dotnet tool restore | Out-Null

    $failed = $false
    foreach ($project in 'ThisIsATrap.UnitTests', 'ThisIsATrap.IntegrationTests') {
        dotnet test --project "tests/$project" -c $Configuration --no-build `
            --results-directory (Join-Path $results $project) `
            --report-trx --coverage --coverage-output-format cobertura --coverage-output "$project.cobertura.xml"
        if ($LASTEXITCODE) { $failed = $true }
    }
    if ($failed) { throw 'Tests failed.' }

    $reports = (Get-ChildItem $results -Recurse -Filter '*.cobertura.xml').FullName -join ';'
    dotnet reportgenerator "-reports:$reports" "-targetdir:$(Join-Path $results 'coverage')" `
        '-reporttypes:TextSummary;MarkdownSummaryGithub;Html' '-assemblyfilters:+ThisIsATrap' `
        '-classfilters:-System.Text.RegularExpressions.Generated*' | Out-Null   # compiler-generated [GeneratedRegex] code

    $summary = Get-Content (Join-Path $results 'coverage/Summary.txt') -Raw
    $line = [regex]::Match($summary, 'Line coverage:\s*([\d.]+)%').Groups[1].Value
    if (-not $line) { throw 'Could not read line coverage from the report.' }
    $pct = [double]::Parse($line, [Globalization.CultureInfo]::InvariantCulture)

    Write-Host ''
    Write-Host ($summary -split "`n" | Select-Object -First 12 | Out-String)
    if ($env:GITHUB_STEP_SUMMARY) { Get-Content (Join-Path $results 'coverage/SummaryGithub.md') | Add-Content $env:GITHUB_STEP_SUMMARY }

    if ($pct -lt $Threshold) { throw "Line coverage $pct% is below the $Threshold% threshold." }
    Write-Host "Line coverage $pct% (threshold $Threshold%)." -ForegroundColor Green
}
finally { Pop-Location }
