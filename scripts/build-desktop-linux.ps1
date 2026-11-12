# Builds the Linux packages of the desktop client from Windows: publishes it (Release, self-contained linux-x64), then packs
# freespeech-desktop-<version>-linux-x64.tar.gz and freespeech-desktop_<version>_amd64.deb (+ SHA256SUMS) in sources\deploy\release\desktop-linux.
# The AppImage is built on Linux from the tar.gz: sources/deploy/desktop/linux/build-appimage.sh.
# Usage: scripts\build-desktop-linux.ps1 [-Version 1.3.4]   (default: FsDesktopVersion of sources\version.props)
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'sources\version.props'))).Project.PropertyGroup.FsDesktopVersion | Where-Object { $_ } | Select-Object -First 1 }
$publish = Join-Path $root 'sources\deploy\desktop\publish-linux'
$out = Join-Path $root 'sources\deploy\release\desktop-linux'
dotnet publish sources\freespeech.desktop\FreeSpeech.Desktop.csproj -c Release -r linux-x64 --self-contained true "-p:Version=$Version" -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
$icon = Join-Path $root 'sources\freespeech.mobile\Rev2\AppIcon\appicon.png'
dotnet run scripts\pack-desktop-linux.cs -- $Version $publish $icon $out
if ($LASTEXITCODE -ne 0) { throw 'packing failed' }
Copy-Item sources\deploy\desktop\linux\install.sh, sources\deploy\desktop\linux\build-appimage.sh $out -Force
Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
