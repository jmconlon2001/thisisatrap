<#
.SYNOPSIS
    Points git at the versioned hooks in .githooks (run once per clone).
    pre-commit: build + unit tests, and blocks secrets/local data. pre-push: full tests + coverage gate + vulnerability scan.
#>
$root = Split-Path $PSScriptRoot -Parent
git -C $root config core.hooksPath .githooks
if (Get-Command chmod -ErrorAction SilentlyContinue) { chmod +x "$root/.githooks/pre-commit" "$root/.githooks/pre-push" }
Write-Host "Git hooks installed (core.hooksPath = .githooks)."
