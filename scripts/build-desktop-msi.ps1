# Builds the Windows installer (MSI) of the desktop client: publishes it (Release, self-contained win-x64), then packs the folder with WiX.
# Usage: scripts\build-desktop-msi.ps1 [-Version 1.0.218]   (default: FsDesktopVersion of sources\version.props)
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
# The version is the one of sources\version.props (scripts\bump-version.ps1 -Component desktop raises it).
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'sources\version.props'))).Project.PropertyGroup.FsDesktopVersion | Where-Object { $_ } | Select-Object -First 1 }
$publish = Join-Path $root 'sources\deploy\desktop\publish'
$out = Join-Path $root "sources\deploy\desktop\FreeSpeech-$Version-x64.msi"
if (Get-Process FreeSpeech -ErrorAction SilentlyContinue) { throw 'FreeSpeech is running: quit it first (tray icon > Quit).' }

dotnet publish sources\freespeech.desktop\FreeSpeech.Desktop.csproj -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# The WiX tool is installed once, in a folder of its own (version 5: free of the later licence fee).
$wixDir = Join-Path $env:LOCALAPPDATA 'FreeSpeechBuild\wix'
if (-not (Test-Path (Join-Path $wixDir 'wix.exe'))) { dotnet tool install --tool-path $wixDir wix --version 5.0.2 }
& (Join-Path $wixDir 'wix.exe') build sources\deploy\desktop\installer\Package.wxs -arch x64 -d "Version=$Version" -d "PublishDir=$publish" -d "IconFile=$root\sources\freespeech.desktop\Assets\freespeech.ico" -o $out
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
"{0} ({1:N0} MB)" -f $out, ((Get-Item $out).Length / 1MB)