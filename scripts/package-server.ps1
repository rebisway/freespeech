# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Builds the archive of a server to download and install: freespeech-<program>-<version>.tar.gz, plus SHA256SUMS.
# Each archive holds publish/ (the program, as `dotnet publish -c Release`), deploy/ (service, nginx example, configuration model, update script)
# and install.sh (scripts\... sources\deploy\install-server.sh). The version is the one of sources\version.props.
# The archives go to sources\deploy\release, never to sources\deploy\<program>\publish (the folder of the deployment to the VM).
#   scripts\package-server.ps1                      all three servers
#   scripts\package-server.ps1 -Component relay     one server (relay | push | directory)
param([ValidateSet('all', 'relay', 'push', 'directory')][string]$Component = 'all')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Per server: the project, the key of its version in sources\version.props, the archive name.
$servers = [ordered]@{
    relay     = @{ Project = 'sources\freespeech.relay\FreeSpeech.Relay.csproj';         Key = 'FsRelayVersion';     Dll = 'FreeSpeech.Relay.dll' }
    push      = @{ Project = 'sources\freespeech.push\FreeSpeech.Push.csproj';           Key = 'FsPushVersion';      Dll = 'FreeSpeech.Push.dll' }
    directory = @{ Project = 'sources\freespeech.directory\FreeSpeech.Directory.csproj'; Key = 'FsDirectoryVersion'; Dll = 'FreeSpeech.Directory.dll' }
}
$props = [xml](Get-Content (Join-Path $root 'sources\version.props'))
$releaseDir = Join-Path $root 'sources\deploy\release'
$work = Join-Path $releaseDir 'work'
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$installer = Join-Path $root 'sources\deploy\install-server.sh'
$shortcuts = Join-Path $root 'sources\deploy\install-shortcuts.sh'
if (-not (Test-Path $installer)) { throw "$installer not found" }

$names = if ($Component -eq 'all') { $servers.Keys } else { @($Component) }
$sums = New-Object System.Collections.Generic.List[string]
foreach ($name in $names) {
    $s = $servers[$name]
    $version = $props.Project.PropertyGroup.($s.Key) | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "$($s.Key) not found in sources\version.props" }
    $folder = "freespeech-$name-$version"
    $stage = Join-Path $work $folder
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    Write-Host "== $name $version"
    dotnet publish $s.Project -c Release -o (Join-Path $stage 'publish')
    if ($LASTEXITCODE -ne 0) { throw "publish of $name failed" }
    Copy-Item (Join-Path $root "sources\deploy\$name\deploy") (Join-Path $stage 'deploy') -Recurse
    Copy-Item $installer (Join-Path $stage 'install.sh')
    Copy-Item $shortcuts (Join-Path $stage 'deploy\install-shortcuts.sh')
    [IO.File]::WriteAllText((Join-Path $stage 'VERSION'), "$name $version`n", (New-Object System.Text.UTF8Encoding($false)))

    # Nothing secret or local may travel in an archive.
    $forbidden = Get-ChildItem $stage -Recurse -Force -File | Where-Object { $_.Name -in 'appsettings.Local.json', 'phrases.txt' -or $_.Name -match '\.(keystore|jks|pfx|p12|pem)$' -or $_.FullName -match '\\secrets\\' }
    if ($forbidden) { throw "forbidden file in the $name archive: $($forbidden[0].FullName)" }
    if (-not (Test-Path (Join-Path $stage "publish\$($s.Dll)"))) { throw "$($s.Dll) missing from the $name publication" }

    $archive = Join-Path $releaseDir "$folder.tar.gz"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    # Relative paths: the tar of Git for Windows takes "C:" for a remote host.
    Push-Location $releaseDir
    try { & tar -C work -czf "$folder.tar.gz" $folder } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "tar of $name failed" }
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLower()
    $sums.Add("$hash  $folder.tar.gz")
    "{0} ({1:N1} MB)" -f $archive, ((Get-Item $archive).Length / 1MB)
}
Remove-Item $work -Recurse -Force

# SHA256SUMS keeps the lines of the archives that were not rebuilt in this run (sha256sum -c reads it as is).
$sumFile = Join-Path $releaseDir 'SHA256SUMS'
$kept = @()
if (Test-Path $sumFile) { $kept = Get-Content $sumFile | Where-Object { $line = $_; $line -and -not ($sums | Where-Object { $_.Split(' ')[-1] -replace '-[0-9.]+\.tar\.gz$', '' -eq ($line.Split(' ')[-1] -replace '-[0-9.]+\.tar\.gz$', '') }) } }
[IO.File]::WriteAllText($sumFile, ((@($kept) + $sums) -join "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "SHA256SUMS: $sumFile"
