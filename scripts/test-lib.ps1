# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Shared harness for the automated console tests (dot-source it):
#   . "$PSScriptRoot\test-lib.ps1"
# Starts servers/clients as background processes with piped stdin/stdout,
# pumps their output into per-process logs, and gives Say / Wait-S / Mark /
# Since / Has / Assert / Show. Also defines $root, $cli, $srv, $alicePh, $bobPh
# (from scripts\phrases.txt) and New-Phrase for throw-away identities.
# NOTE: test-multiserver.ps1 and test-attachments.ps1 still carry their own copy.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cli = "$root\sources\freespeech.console\bin\Debug\net10.0\FreeSpeech.Console.dll"
$srv = "$root\sources\freespeech.relay\bin\Debug\net10.0\FreeSpeech.Relay.dll"

$lines = Get-Content "$root\scripts\phrases.txt" | Where-Object { $_.Trim() -ne '' }
$ai = [array]::IndexOf($lines, ($lines | Where-Object { $_ -like 'alice*' } | Select-Object -First 1))
$bi = [array]::IndexOf($lines, ($lines | Where-Object { $_ -like 'bob*' } | Select-Object -First 1))
$alicePh = $lines[($ai+1)..($ai+3)]
$bobPh = $lines[($bi+1)..($bi+3)]

$script:procs = @{}
$script:out = @{}
function Start-Proc($name, $pargs, $env) {
  $psi = New-Object Diagnostics.ProcessStartInfo 'dotnet'
  $psi.Arguments = $pargs
  $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
  foreach ($k in $env.Keys) { $psi.Environment[$k] = $env[$k] }
  $p = New-Object Diagnostics.Process; $p.StartInfo = $psi
  $null = $p.Start()
  # UTF-8 WITHOUT BOM on stdin: the default writer prefixes a BOM to the first line sent, corrupting that command.
  $p | Add-Member -NotePropertyName W -NotePropertyValue (New-Object IO.StreamWriter($p.StandardInput.BaseStream, (New-Object Text.UTF8Encoding($false))))
  $script:procs[$name] = @{ P = $p; T = $p.StandardOutput.ReadLineAsync(); E = $p.StandardError.ReadLineAsync() }
  $script:out[$name] = New-Object Collections.ArrayList
  return $p
}
function Pump {
  foreach ($n in @($script:procs.Keys)) {
    $s = $script:procs[$n]
    while ($s.T.IsCompleted) { if ($null -eq $s.T.Result) { break }; [void]$script:out[$n].Add($s.T.Result); $s.T = $s.P.StandardOutput.ReadLineAsync() }
    while ($s.E.IsCompleted) { if ($null -eq $s.E.Result) { break }; [void]$script:out[$n].Add("ERR " + $s.E.Result); $s.E = $s.P.StandardError.ReadLineAsync() }
  }
}
function Wait-S($sec) { $end = (Get-Date).AddSeconds($sec); while ((Get-Date) -lt $end) { Pump; Start-Sleep -Milliseconds 200 } }
function Say($p, $t) { $p.W.WriteLine($t); $p.W.Flush() }
function Show($n) { "----- $n"; $script:out[$n] | ForEach-Object { if ($_ -match '^[A-Za-z0-9+/=]{200,}$') { '<base64 card>' } else { $_ } } }

$script:results = New-Object Collections.ArrayList
function Since($n, $mark) { @($script:out[$n] | Select-Object -Skip $mark) }
function Mark($n) { $script:out[$n].Count }
function Assert($name, $cond) { [void]$script:results.Add(("{0}  {1}" -f $(if ($cond) { 'PASS' } else { 'FAIL' }), $name)) }
function Has($n, $mark, $rx) { [bool](Since $n $mark | Where-Object { $_ -match $rx }) }
function Count($n, $mark, $rx) { @(Since $n $mark | Where-Object { $_ -match $rx }).Count }


function New-Phrase { -join (1..80 | ForEach-Object { [char](97 + (Get-Random -Maximum 26)) }) }
function Finish-Test([object[]]$processes) {
  "`n===== RESULTS ====="
  $script:results
  foreach ($p in $processes) { try { $p.Kill() } catch {} }
  if (@($script:results | Where-Object { $_ -like 'FAIL*' }).Count -gt 0) { exit 1 }
}
