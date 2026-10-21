# FreeSpeech - AGPL-3.0-or-later - Copyright (c) 2024-2026 Thomas Gourgnier
#
# Raises the version of ONE program in sources\version.props and writes today's date next to it. Every program has its own version
# (relay, push, console, desktop, mobile, and any program that adds its Fs<Name>Version keys to the file): a fix of one does not change
# the others. Run before publishing that program, then commit version.props. A program that has a build number (Fs<Name>Build, the
# Android versionCode of the mobile app: it only goes up) gets it raised by one at every bump.
#
#   scripts\bump-version.ps1 -Component push              1.1.0 -> 1.1.1   (patch: a fix)
#   scripts\bump-version.ps1 -Component mobile -Minor     1.1.1 -> 1.2.0   (a feature)
#   scripts\bump-version.ps1 -Component relay -Major      1.2.0 -> 2.0.0   (a break)
#   scripts\bump-version.ps1 -Component desktop -DateOnly keeps the version, sets today's date
#   scripts\bump-version.ps1 -List                        shows every version

param([string] $Component, [switch] $Major, [switch] $Minor, [switch] $DateOnly, [switch] $List, [string] $File)

# -File: another props file with the same Fs<Name>Version keys (a program kept outside v2 has its own).
$file = if ($File) { $File } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'sources\version.props' }
$text = [IO.File]::ReadAllText($file)

if ($List -or -not $Component) {
    [regex]::Matches($text, '<Fs(\w+)Version>([^<]*)</Fs\1Version>\s*<Fs\1VersionDate>([^<]*)<') | ForEach-Object { '{0,-10} {1}  ({2})' -f $_.Groups[1].Value.ToLower(), $_.Groups[2].Value, $_.Groups[3].Value }
    if (-not $List) { Write-Host 'usage: scripts\bump-version.ps1 -Component <name> [-Minor | -Major | -DateOnly]' }
    return
}

$name = (Get-Culture).TextInfo.ToTitleCase($Component.ToLower())
$key = "Fs${name}Version"; $dateKey = "Fs${name}VersionDate"; $buildKey = "Fs${name}Build"
if ($text -notmatch "<$key>(\d+)\.(\d+)\.(\d+)</$key>") { throw "$key not found in $file (scripts\bump-version.ps1 -List shows the programs)" }
# Not $major / $minor: PowerShell variables ignore case, they would be the switches above.
$vMajor = [int]$Matches[1]; $vMinor = [int]$Matches[2]; $vPatch = [int]$Matches[3]

if (-not $DateOnly) {
    if ($Major) { $vMajor++; $vMinor = 0; $vPatch = 0 }
    elseif ($Minor) { $vMinor++; $vPatch = 0 }
    else { $vPatch++ }
}
$version = "$vMajor.$vMinor.$vPatch"
$date = (Get-Date).ToString('yyyy-MM-dd')

$text = $text -replace "<$key>[^<]*</$key>", "<$key>$version</$key>"
$text = $text -replace "<$dateKey>[^<]*</$dateKey>", "<$dateKey>$date</$dateKey>"
if (-not $DateOnly -and $text -match "<$buildKey>(\d+)</$buildKey>") {
    $build = [int]$Matches[1] + 1
    $text = $text -replace "<$buildKey>\d+</$buildKey>", "<$buildKey>$build</$buildKey>"
    Write-Host "build number $build"
}
[IO.File]::WriteAllText($file, $text)
Write-Host "$Component $version ($date) written to $file"
