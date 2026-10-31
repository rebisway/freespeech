# FreeSpeech - AGPL-3.0-or-later - Copyright (c) 2024-2026 Thomas Gourgnier
#
# Writes and signs the update manifest the applications check (Settings > Check for updates): latest.json and latest.json.sig, from the versions
# in sources\version.props, into the website (freespeech-website\freespeech.website\wwwroot\update). Publish the website afterwards.
#   scripts\make-update-manifest.ps1 -NewKey      creates the signing key once and prints the public key to put in FsUpdateKey (freespeech.defaults.props)
#   scripts\make-update-manifest.ps1              writes the manifest (run it after raising the versions with bump-version.ps1, before publishing the site)
# The signing key lives outside the repository: %USERPROFILE%\.freespeech\update-signing.key. Back it up: the applications trust its public half for good.
# The site address comes from FsUpdateUrl in sources\freespeech.defaults.props (not versioned).
param([switch] $NewKey)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$keyFile = Join-Path (Join-Path $env:USERPROFILE '.freespeech') 'update-signing.key'
$tool = Join-Path $root 'sources\tools\updatesign\updatesign.csproj'

if ($NewKey) {
    New-Item -ItemType Directory -Force (Split-Path $keyFile) | Out-Null
    & dotnet run --project $tool -c Release -- newkey $keyFile
    exit $LASTEXITCODE
}

if (-not (Test-Path $keyFile)) { throw "No signing key at $keyFile: run scripts\make-update-manifest.ps1 -NewKey first." }
$defaults = Join-Path $root 'sources\freespeech.defaults.props'
if (-not (Test-Path $defaults)) { throw "sources\freespeech.defaults.props is missing (FsUpdateUrl is read from it)." }
$site = ([xml](Get-Content $defaults -Raw)).Project.PropertyGroup.FsUpdateUrl | Where-Object { $_ } | Select-Object -First 1
if (-not $site) { throw 'FsUpdateUrl is empty in sources\freespeech.defaults.props.' }

$out = Join-Path $root 'freespeech-website\freespeech.website\wwwroot\update'
& dotnet run --project $tool -c Release -- make $keyFile (Join-Path $root 'sources\version.props') $site $out
if ($LASTEXITCODE -ne 0) { throw 'updatesign failed' }
