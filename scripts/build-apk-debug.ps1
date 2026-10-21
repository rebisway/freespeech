# FreeSpeech - AGPL-3.0-or-later - Copyright (c) 2024-2026 Thomas Gourgnier
#
# Builds the Debug APK for the Android EMULATORS (sources\apk\debug\org.freespeech.mobile-Signed.apk): x86_64 only, no trimming, no AOT, signed with the
# Android debug key. Much faster than the Release build, which is for phones (scripts\build-apk-release.ps1).
#   scripts\build-apk-debug.ps1             build only
#   scripts\build-apk-debug.ps1 -Install    also installs it on every running EMULATOR (phones are left alone) and forwards the device's
#                                           127.0.0.1:5299 to the PC's relay (adb reverse), as a Debug run from the IDE does
# The Debug APK is signed with another key than the Release one: an emulator that has the Release app must uninstall it first
# (adb -s <emulator> uninstall org.freespeech.mobile).
param([switch] $Install)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$build = (& svnversion -c $root | Select-String '\d+' -AllMatches).Matches | ForEach-Object { [int]$_.Value } | Sort-Object -Descending | Select-Object -First 1
$out = Join-Path $root 'sources\apk\debug'
New-Item -ItemType Directory -Force $out | Out-Null

$publish = @(
    (Join-Path $root 'sources\freespeech.mobile'), '-c', 'Debug', '-f', 'net10.0-android36.0',
    '-p:AndroidPackageFormat=apk', "-p:ApplicationVersion=$build", '-p:FsRuntimeIds=android-x64',
    '-p:EmbedAssembliesIntoApk=true', '-p:AndroidUseSharedRuntime=false', '-p:PublishTrimmed=false', '-p:RunAOTCompilation=false',
    '-p:AndroidEnableProfiledAot=false', '-o', $out, '-v', 'q', '--nologo')

& dotnet publish @publish --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Retrying with a restore...'
    & dotnet publish @publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}

$apk = Join-Path $out 'org.freespeech.mobile-Signed.apk'
if (-not (Test-Path $apk)) { $apk = (Get-ChildItem $out -Filter '*.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
Write-Host ("APK: {0} ({1:N1} MB), build number {2}, Debug x86_64" -f $apk, ((Get-Item $apk).Length / 1MB), $build)

if ($Install) {
    $adb = Join-Path $env:ProgramFiles 'Android\android-sdk\platform-tools\adb.exe'
    if (-not (Test-Path $adb)) { $adb = "${env:ProgramFiles(x86)}\Android\android-sdk\platform-tools\adb.exe" }
    $found = $false
    foreach ($line in (& $adb devices | Select-Object -Skip 1)) {
        if ($line -match '^(emulator-\d+)\s+device$') {
            $found = $true
            Write-Host "== $($Matches[1])"
            & $adb -s $Matches[1] install -r $apk
            & $adb -s $Matches[1] reverse tcp:5299 tcp:5299 | Out-Null
        }
    }
    if (-not $found) { Write-Host 'No running emulator: start one, then run the script again with -Install.' }
}
