# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Launches two freespeech.relay instances (each in its own window, via
# run-server.ps1) and fully configures S2S between them (protocol §10):
# both set public, linked as siblings of each other (A asks, B accepts, as two
# administrators would with "admin add relay" / "admin accept relay"). Hands you two ready-to-use servers for a cross-server test
# (OTC contact exchange, send, identity_update) without the usual manual
# Invoke-RestMethod dance.
#
# USAGE:
#   scripts\run-s2s-pair.ps1
#   scripts\run-s2s-pair.ps1 -PortA 5401 -PortB 5402

param(
    [int] $PortA = 5401,
    [int] $PortB = 5402,
    [string] $AdminSecretA = "secretA",
    [string] $AdminSecretB = "secretB"
)

$root = Split-Path -Parent $PSScriptRoot
$urlA = "http://localhost:$PortA"
$urlB = "http://localhost:$PortB"

function Wait-ServerUp([string]$url, [int]$timeoutSeconds = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-RestMethod -Uri "$url/api/time" -TimeoutSec 2 | Out-Null
            return
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Server at $url did not come up within $timeoutSeconds s."
}

Write-Host "=== Starting server A ($urlA) and server B ($urlB) ==="
& "$PSScriptRoot\run-server.ps1" -Port $PortA -AdminSecret $AdminSecretA
& "$PSScriptRoot\run-server.ps1" -Port $PortB -AdminSecret $AdminSecretB

Write-Host "Waiting for both servers to respond..."
Wait-ServerUp $urlA
Wait-ServerUp $urlB

Write-Host "=== Configuring S2S: both public, linked as siblings (A asks, B accepts) ==="
Invoke-RestMethod -Uri "$urlA/api/admin/status" -Method Post -Headers @{ "X-Admin-Secret" = $AdminSecretA } -ContentType "application/json" -Body '{"is_public": true}' | Out-Null
Invoke-RestMethod -Uri "$urlB/api/admin/status" -Method Post -Headers @{ "X-Admin-Secret" = $AdminSecretB } -ContentType "application/json" -Body '{"is_public": true}' | Out-Null

Invoke-RestMethod -Uri "$urlA/api/admin/peers" -Method Post -Headers @{ "X-Admin-Secret" = $AdminSecretA } -ContentType "application/json" -Body (@{kind = "relay"; url = $urlB } | ConvertTo-Json) | Out-Null
Invoke-RestMethod -Uri "$urlB/api/admin/peers/accept" -Method Post -Headers @{ "X-Admin-Secret" = $AdminSecretB } -ContentType "application/json" -Body (@{kind = "relay"; all = $true } | ConvertTo-Json) | Out-Null
Invoke-RestMethod -Uri "$urlA/api/admin/peers/refresh" -Method Post -Headers @{ "X-Admin-Secret" = $AdminSecretA } -ContentType "application/json" -Body "{}" | Out-Null
Write-Host "`n=== Status ==="
Write-Host "A ($urlA):"
Invoke-RestMethod -Uri "$urlA/api/admin/status" -Headers @{ "X-Admin-Secret" = $AdminSecretA } | Format-List
Write-Host "B ($urlB):"
Invoke-RestMethod -Uri "$urlB/api/admin/status" -Headers @{ "X-Admin-Secret" = $AdminSecretB } | Format-List

Write-Host "Ready. Point clients at $urlA and $urlB, e.g.:"
Write-Host "  cd sources\freespeech.console; dotnet run alice $urlA"
Write-Host "  cd sources\freespeech.console; dotnet run bob $urlB"
