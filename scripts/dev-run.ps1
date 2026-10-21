# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Launches the server and one console client per requested profile, each
# in its own terminal window, for manual multi-instance testing.
#
# USAGE:
#   scripts\dev-run.ps1                          # server + alice + bob
#   scripts\dev-run.ps1 -Profiles alice,alice2,bob
#   scripts\dev-run.ps1 -Profiles alice -Port 5400

param(
    [string[]] $Profiles = @("alice", "bob"),
    [int] $Port = 5299
)

$root = Split-Path -Parent $PSScriptRoot
$serverUrl = "http://localhost:$Port"

Write-Host "Starting server on $serverUrl ..."
Start-Process powershell -ArgumentList @(
    "-NoExit", "-Command",
    "cd '$root\sources\freespeech.relay'; `$env:ASPNETCORE_URLS='$serverUrl'; dotnet run --no-launch-profile"
)

Start-Sleep -Seconds 3

foreach ($profile in $Profiles) {
    Write-Host "Starting console client: $profile"
    Start-Process powershell -ArgumentList @(
        "-NoExit", "-Command",
        # chcp 65001 first: the console host's own code page decodes each
        # keystroke before .NET ever sees it — without this, accented
        # characters are silently dropped regardless of Console.InputEncoding.
        "chcp 65001 | Out-Null; cd '$root\sources\freespeech.console'; dotnet run $profile $serverUrl"
    )
    Start-Sleep -Milliseconds 500
}

Write-Host "`nAll windows launched. Close them individually or 'quit' each console client when done."
