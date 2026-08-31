<#
.SYNOPSIS
    Scaffolds a new Architecture Decision Record from docs/adr/adr-template.md.

.DESCRIPTION
    Finds the highest existing ADR number under docs/adr/, copies the template to
    docs/adr/NNNN-<slug>.md with the next number, and fills in the title and today's date.

.PARAMETER Title
    The decision's title, e.g. "Use git blob SHA-1 checksums". Used for both the heading and the
    kebab-case filename slug.

.EXAMPLE
    ./scripts/new-adr.ps1 -Title "Use git blob SHA-1 checksums"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Title
)

$ErrorActionPreference = "Stop"

$adrDir = Join-Path $PSScriptRoot "..\docs\adr"
$templatePath = Join-Path $adrDir "adr-template.md"

if (-not (Test-Path $templatePath)) {
    throw "Template not found at $templatePath"
}

$existing = Get-ChildItem -Path $adrDir -Filter "*.md" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.BaseName -match "^\d{4}-" }
$nextNumber = 1
if ($existing) {
    $maxNumber = ($existing | ForEach-Object { [int]($_.BaseName.Substring(0, 4)) } | Measure-Object -Maximum).Maximum
    $nextNumber = $maxNumber + 1
}
$numberText = "{0:0000}" -f $nextNumber

$slug = $Title.ToLowerInvariant()
$slug = $slug -replace "[^a-z0-9]+", "-"
$slug = $slug.Trim("-")

$fileName = "$numberText-$slug.md"
$outPath = Join-Path $adrDir $fileName

if (Test-Path $outPath) {
    throw "$outPath already exists"
}

$content = Get-Content -Path $templatePath -Raw
$content = $content -replace "^# NNNN\. Title of the decision", "# $numberText. $Title"
$content = $content -replace "\*\*Date:\*\* YYYY-MM-DD", ("**Date:** " + (Get-Date -Format "yyyy-MM-dd"))

Set-Content -Path $outPath -Value $content -NoNewline

Write-Output "Created $outPath"
Write-Output "Remember to add a row to docs/adr/README.md's index once it's ready for review."
