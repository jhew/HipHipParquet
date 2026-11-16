<#
.SYNOPSIS
    Writes the winget manifests for a published Hip Hip Parquet release.

.DESCRIPTION
    Reads the release's SHA256SUMS.txt from GitHub, then writes the three manifest
    files winget-pkgs expects under winget\manifests\j\jhew\HipHipParquet\<version>.
    Submit them by copying that folder into a fork of microsoft/winget-pkgs and
    opening a pull request, or with: wingetcreate submit <folder>

.EXAMPLE
    .\winget\New-WingetManifest.ps1 -Version 1.15.0
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repo        = 'https://github.com/jhew/HipHipParquet'
$identifier  = 'jhew.HipHipParquet'
$schema      = '1.10.0'
$setupName   = "HipHipParquet-$Version-Setup.exe"
$installerUrl = "$repo/releases/download/v$Version/$setupName"
$sumsUrl      = "$repo/releases/download/v$Version/HipHipParquet-$Version-SHA256SUMS.txt"

# GitHub serves release assets as application/octet-stream, which Windows
# PowerShell returns as bytes rather than text.
$content = (Invoke-WebRequest $sumsUrl -UseBasicParsing).Content
$sums = if ($content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($content).TrimStart([char]0xFEFF) } else { $content }
$line = ($sums -split "`r?`n") | Where-Object { $_ -match "^\s*([0-9A-Fa-f]{64})\s+\*?$([regex]::Escape($setupName))\s*$" } | Select-Object -First 1
if (-not $line) { throw "No checksum for $setupName in $sumsUrl" }
$sha256 = ($line -split '\s+')[0].ToUpperInvariant()

$outDir = Join-Path $PSScriptRoot "manifests\j\jhew\HipHipParquet\$Version"
New-Item -ItemType Directory -Force $outDir | Out-Null

$header = "# yaml-language-server: `$schema=https://aka.ms/winget-manifest"

$versionManifest = @"
$header.version.$schema.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

# The installer is Inno Setup with PrivilegesRequired=lowest, so a silent install
# goes to the current user's profile. The ProductCode is the Inno AppId plus _is1.
$installerManifest = @"
$header.installer.$schema.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
InstallerType: inno
Scope: user
UpgradeBehavior: install
MinimumOSVersion: 10.0.19041.0
Dependencies:
  PackageDependencies:
  - PackageIdentifier: Microsoft.DotNet.DesktopRuntime.10
ProductCode: '{F3A5C8D2-1B6E-4F9A-B3D7-8E2C5A4F1B9D}_is1'
Installers:
- Architecture: x64
  InstallerUrl: $installerUrl
  InstallerSha256: $sha256
ManifestType: installer
ManifestVersion: $schema
"@

$localeManifest = @"
$header.defaultLocale.$schema.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: jhew
PublisherUrl: https://github.com/jhew
PublisherSupportUrl: $repo/issues
PackageName: Hip Hip Parquet
PackageUrl: https://hiphipparquet.com
License: MIT
LicenseUrl: $repo/blob/main/LICENSE
ShortDescription: Open, query, score and fix Parquet, CSV, JSON and Excel files locally.
Description: |-
  A free Windows desktop app that opens Parquet, CSV, JSON and Excel files, runs SQL
  across them with DuckDB, scores their quality, and lets you fix problems in the grid
  and save back, all on your own machine with no account and no service behind it.
Tags:
- csv
- data-quality
- duckdb
- excel
- json
- parquet
- sql
ReleaseNotesUrl: $repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@

$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $outDir "$identifier.yaml"), $versionManifest + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $outDir "$identifier.installer.yaml"), $installerManifest + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $outDir "$identifier.locale.en-US.yaml"), $localeManifest + "`n", $utf8)

Write-Host "Wrote manifests for $Version to $outDir"
Write-Host "Installer SHA256: $sha256"
