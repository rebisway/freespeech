# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Automated multi-server test (protocol §8, client side). Starts two
# UNLINKED servers (no S2S), alice on A only and bob on B only (identities
# from scripts\phrases.txt, fresh throw-away profiles), then checks: OTC
# exchange across servers, send, `server add` + identity_update, and
# fail-over after B is killed, outbox with all servers down, own-server
# reconnection + outbox flush, cross-server de-duplication. Prints PASS/FAIL
# per check (exit 1 on any FAIL); set $env:SHOW_LOGS=1 to dump both consoles.
#
# USAGE:  scripts\test-multiserver.ps1     (needs a prior `dotnet build sources`)
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

$sa = Start-Proc 'srvA' "`"$srv`"" @{ ASPNETCORE_URLS='http://localhost:5411' }
$sb = Start-Proc 'srvB' "`"$srv`"" @{ ASPNETCORE_URLS='http://localhost:5412' }
Wait-S 5
$prof = "t8" + (Get-Random -Maximum 9999)
$alice = Start-Proc 'alice' "`"$cli`" ${prof}a http://localhost:5411" @{}
$bob = Start-Proc 'bob' "`"$cli`" ${prof}b http://localhost:5412" @{}
Wait-S 2
foreach ($p in $alicePh) { Say $alice $p }; Say $alice 'alice'
foreach ($p in $bobPh) { Say $bob $p }; Say $bob 'bob'
Wait-S 4
Say $bob 'export qr'
Wait-S 2
$b64 = ($script:out['bob'] | Where-Object { $_ -match '^[A-Za-z0-9+/=]{200,}$' } | Select-Object -Last 1)
Say $alice "import $b64"
Wait-S 6
$script:results = New-Object Collections.ArrayList
function Since($n, $mark) { @($script:out[$n] | Select-Object -Skip $mark) }
function Mark($n) { $script:out[$n].Count }
function Assert($name, $cond) { [void]$script:results.Add(("{0}  {1}" -f $(if ($cond) { 'PASS' } else { 'FAIL' }), $name)) }
function Has($n, $mark, $rx) { [bool](Since $n $mark | Where-Object { $_ -match $rx }) }
function Count($n, $mark, $rx) { @(Since $n $mark | Where-Object { $_ -match $rx }).Count }

$aliceId = ($script:out['alice'] | Where-Object { $_ -match 'Identity created: ([0-9a-f]{64})' } | ForEach-Object { $Matches[1] } | Select-Object -First 1)
$bobId = ($script:out['bob'] | Where-Object { $_ -match 'Identity created: ([0-9a-f]{64})' } | ForEach-Object { $Matches[1] } | Select-Object -First 1)

# 1. OTC exchange across two unlinked servers
Assert 'OTC exchange: alice confirmed bob'  (Has 'alice' 0 'contact confirmed\] bob')
Assert 'OTC exchange: bob confirmed alice'  (Has 'bob' 0 'contact confirmed\] alice')

# 2. send via the contact's server (alice is only registered on A)
Say $alice 'send bob hello-from-alice-via-B'
Wait-S 3
Assert 'send via contact server (B)' (Has 'bob' 0 'hello-from-alice-via-B')

# 3. server add -> identity_update
$mAlice = Mark 'alice'
Say $bob 'server add http://localhost:5411'
Wait-S 5
Assert 'server add: bob connected on A'      (Has 'bob' 0 'Added http://localhost:5411 \(priority 1\), connected')
Assert 'server add: alice got identity_update' (Has 'alice' $mAlice 'contact updated\] bob')

# 4. fail-over: kill B, alice must reach bob through A
$mBob = Mark 'bob'
try { $sb.Kill() } catch {}
Wait-S 1
Say $alice 'send bob hello-after-B-died'
Wait-S 6
Assert 'fail-over to A after B died' (Has 'bob' $mBob 'hello-after-B-died')

# 5. outbox: kill A too -> no server reachable, message queued
try { $sa.Kill() } catch {}
Wait-S 2
$mAlice = Mark 'alice'
Say $alice 'send bob msg-while-all-down'
Wait-S 4
Say $alice 'outbox'
Wait-S 1
Assert 'no server reachable -> queued'   (Has 'alice' $mAlice 'No server reachable')
Assert 'outbox lists the queued message' (Has 'alice' $mAlice 'msg-while-all-down \(queued')

# 6. own-server reconnection + outbox flush: restart both servers, wait for the 30 s ticks
$mAlice = Mark 'alice'; $mBob = Mark 'bob'
# Step 7 registers raw sockets as alice without signing: these test servers accept anonymous registration (development switch,
# off by default; the real clients still sign).
$sa = Start-Proc 'srvA2' "`"$srv`"" @{ ASPNETCORE_URLS='http://localhost:5411'; Server__AllowAnonymousRegister='true' }
$sb = Start-Proc 'srvB2' "`"$srv`"" @{ ASPNETCORE_URLS='http://localhost:5412'; Server__AllowAnonymousRegister='true' }
Wait-S 75
Assert 'alice reconnected to her own server A' (Has 'alice' $mAlice 'reconnected to http://localhost:5411')
Assert 'bob reconnected to A'                  (Has 'bob' $mBob 'reconnected to http://localhost:5411')
Assert 'bob reconnected to B'                  (Has 'bob' $mBob 'reconnected to http://localhost:5412')
Assert 'outbox flushed after reconnection'     (Has 'bob' $mBob 'msg-while-all-down')

# 7. de-duplication: the same envelope pushed through both servers must surface once.
#    Raw sockets register as alice on A and on B and send an identical (undecryptable) envelope to bob.
$mBob = Mark 'bob'
function Ws-Roundtrip($ws, $json) {
  $ws.SendAsync([ArraySegment[byte]][Text.Encoding]::UTF8.GetBytes($json), 'Text', $true, [Threading.CancellationToken]::None).Wait()
  $buf = New-Object byte[] 8192
  $null = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None).Result
}
$env = @{ from = $aliceId; to = $bobId; crypto = 1; msg = 'Z2FyYmFnZQ=='; ts = 638000000000000001 }
$sockets = @(); try { foreach ($port in 5411, 5412) {
  $ws = New-Object Net.WebSockets.ClientWebSocket
  $ws.ConnectAsync([uri]"ws://localhost:$port/ws/relay", [Threading.CancellationToken]::None).Wait()
  Ws-Roundtrip $ws (@{ type = 'register'; request_id = 'r1'; discovery_id = $aliceId } | ConvertTo-Json -Compress)
  $sockets += $ws
}
foreach ($ws in $sockets) { Ws-Roundtrip $ws (@{ type = 'send'; request_id = 'r2'; envelope = $env } | ConvertTo-Json -Compress -Depth 4) }
} catch { Assert "de-duplication: raw socket setup failed: $($_.Exception.GetBaseException().Message)" $false }
Wait-S 3
$n = Count 'bob' $mBob 'Could not decrypt message from'
Assert "de-duplication: envelope seen twice by transport, surfaced once (got $n)" ($n -eq 1)
foreach ($ws in $sockets) { try { $ws.Dispose() } catch {} }

"`n===== RESULTS ====="
$script:results
if ($env:SHOW_LOGS) { Show 'alice'; Show 'bob' }
foreach ($p in @($alice, $bob, $sa, $sb)) { try { $p.Kill() } catch {} }
if (@($script:results | Where-Object { $_ -like 'FAIL*' }).Count -gt 0) { exit 1 }



