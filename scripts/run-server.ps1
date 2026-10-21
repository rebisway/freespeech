# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Launches a single freespeech.relay instance on a given port, in its own
# PowerShell window (like scripts\dev-run.ps1 does for its own instances),
# with its own state files (siblings.json, access.json, peers.json: S2S, protocol §10) so it
# doesn't collide with another instance's.
#
# NOTE: "dotnet run --project X" always uses X's own directory as the
# launched app's working directory — it ignores the shell's cwd, so a plain
# "cd" before it does NOT isolate a relative siblings.json path (confirmed
# the hard way: every instance was silently sharing and accumulating state
# in sources\freespeech.relay\siblings.json). The fix is to pass the path
# explicitly as a command-line configuration override, which always wins
# regardless of working directory.
#
# USAGE:
#   scripts\run-server.ps1 -Port 5401
#   scripts\run-server.ps1 -Port 5402 -AdminSecret "secretB"
#   scripts\run-server.ps1 -Port 5401 -WorkingDir C:\temp\fs-srvA -AdminSecret "secretA"

param(
    [Parameter(Mandatory = $true)]
    [int] $Port,

    [string] $AdminSecret = "",

    # Defaults to a per-port folder under the repo's own scripts\.run dir —
    # keeps siblings.json isolated per instance without polluting the repo.
    [string] $WorkingDir
)

$root = Split-Path -Parent $PSScriptRoot

if (-not $WorkingDir) {
    $WorkingDir = Join-Path $root "scripts\.run\server-$Port"
}
New-Item -ItemType Directory -Force -Path $WorkingDir | Out-Null
$siblingsPath = Join-Path $WorkingDir "siblings.json"

$adminEnvSet = ""
if ($AdminSecret) {
    $adminEnvSet = "`$env:Admin__Secret='$AdminSecret'; "
}

Write-Host "Starting server on http://localhost:$Port (siblings file: $siblingsPath) ..."
Start-Process powershell -ArgumentList @(
    "-NoExit", "-Command",
    "`$env:ASPNETCORE_URLS='http://localhost:$Port'; `$env:Server__AllowInsecureSiblings='true'; `$env:Peer__PublicUrl='http://localhost:$Port'; $adminEnvSet" +
    "dotnet run --no-launch-profile --project '$root\sources\freespeech.relay' -- --Server:SiblingsFilePath='$siblingsPath' --Server:AccessFilePath='$WorkingDir\access.json' --Peer:StatePath='$WorkingDir'"
)
