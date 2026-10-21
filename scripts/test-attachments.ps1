# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Automated attachment test (protocol §6): one server, alice + bob
# (identities from scripts\phrases.txt, throw-away profiles).
#   1. upload -> offer -> download -> chunks -> sha256 verified, byte-identical
#   2. sender offline at `download` time -> WAITING + automatic resume once the
#      sender is back (stall timer + background tick, ~1 min)
# Prints PASS/FAIL per check (exit 1 on any FAIL); $env:SHOW_LOGS=1 dumps consoles.
# NOTE: the process/console harness below is duplicated from
# test-multiserver.ps1 — factor into a shared lib if a third test appears.
#
# USAGE:  scripts\test-attachments.ps1     (needs a prior `dotnet build sources`)
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

# $env:FS_SERVER (e.g. https://relay.exemple.org) runs the test against an existing server instead of a local one.
$url = if ($env:FS_SERVER) { $env:FS_SERVER } else { 'http://localhost:5421' }
$sa = $null
if (-not $env:FS_SERVER) { $sa = Start-Proc 'srv' "`"$srv`"" @{ ASPNETCORE_URLS='http://localhost:5421' } }
Wait-S 5
$prof = "t9" + (Get-Random -Maximum 9999)
$alice = Start-Proc 'alice' "`"$cli`" ${prof}a $url" @{}
$bob = Start-Proc 'bob' "`"$cli`" ${prof}b $url" @{}
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
Assert 'OTC exchange done' ((Has 'alice' 0 'contact confirmed\] bob') -and (Has 'bob' 0 'contact confirmed\] alice'))

$tmp = Join-Path $env:TEMP "fs-att-$prof"; New-Item -ItemType Directory -Force $tmp | Out-Null
$f1 = Join-Path $tmp 'small file.bin'; $f2 = Join-Path $tmp 'big.bin'
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
foreach ($spec in @(@($f1, 230000), @($f2, 420000))) { $b = New-Object byte[] $spec[1]; $rng.GetBytes($b); [IO.File]::WriteAllBytes($spec[0], $b) }

# 1. straight transfer (path with a space on purpose)
$mBob = Mark 'bob'
Say $alice "upload bob $f1"
Wait-S 3
Assert 'bob got the file offer' (Has 'bob' $mBob '\[file\] small file\.bin \(230000 bytes')
Say $bob 'download alice 1'
Wait-S 6
$done = Since 'bob' $mBob | Where-Object { $_ -match 'file received\]' } | Select-Object -First 1
$recvPath = if ($done -match '-> (.+)$') { $Matches[1].Trim() } else { $null }
Assert 'bob received + sha256 verified' ([bool]$done)
Assert 'received file is byte-identical' ($recvPath -and (Test-Path $recvPath) -and ((Get-FileHash $recvPath).Hash -eq (Get-FileHash $f1).Hash))

# 2. sender offline at download-time, then back
$mBob = Mark 'bob'
Say $alice "upload bob $f2"
Wait-S 3
Assert 'bob got the second offer' (Has 'bob' $mBob '\[file\] big\.bin \(420000 bytes')
try { $alice.Kill() } catch {}
Wait-S 2
$mBob = Mark 'bob'
Say $bob 'download alice 2'
Wait-S 3
Assert 'download with sender offline -> WAITING, no crash' (Has 'bob' $mBob 'Sender is offline')
$alice = Start-Proc 'alice2' "`"$cli`" ${prof}a $url" @{}
Wait-S 100
$done2 = Since 'bob' $mBob | Where-Object { $_ -match 'file received\]' } | Select-Object -First 1
$recvPath2 = if ($done2 -match '-> (.+)$') { $Matches[1].Trim() } else { $null }
Assert 'resumed automatically after sender came back' ([bool]$done2)
Assert 'resumed file is byte-identical' ($recvPath2 -and (Test-Path $recvPath2) -and ((Get-FileHash $recvPath2).Hash -eq (Get-FileHash $f2).Hash))

# 3. forward (§6 content-addressed): text + file re-offered to a third contact without duplicating the blob
$rnd = { -join (1..80 | ForEach-Object { [char](97 + (Get-Random -Maximum 26)) }) }
$carol = Start-Proc 'carol' "`"$cli`" ${prof}c $url" @{}
Wait-S 2
foreach ($i in 1..3) { Say $carol (& $rnd) }; Say $carol 'carol'
Wait-S 4
$mB = Mark 'bob'
Say $bob 'export qr'
Wait-S 2
$b64c = (Since 'bob' $mB | Where-Object { $_ -match '^[A-Za-z0-9+/=]{200,}$' } | Select-Object -Last 1)
Say $carol "import $b64c"
Wait-S 6
Assert 'carol <-> bob contact exchange' ((Has 'carol' 0 'contact confirmed\] bob') -and (Has 'bob' $mB 'contact confirmed\] carol'))

Say $alice 'send bob hello-to-forward'        # bob history with alice: #3
Wait-S 2
Say $alice "upload bob $f1"                   # #4: same content as #1, must not need a download
Wait-S 3
$f3 = Join-Path $tmp 'third.bin'; $b = New-Object byte[] 80000; $rng.GetBytes($b); [IO.File]::WriteAllBytes($f3, $b)
Say $alice "upload bob $f3"                   # #5: offered, never downloaded by bob
Wait-S 3

$blobDir = Join-Path $env:APPDATA "FreeSpeech\${prof}b\attachments\blobs"
$blobsBefore = @(Get-ChildItem $blobDir -File).Count
$mBob = Mark 'bob'; $mCarol = Mark 'carol'

Say $bob 'forward alice 3 carol'
Wait-S 3
Assert 'forward text: carol receives it' (Has 'carol' $mCarol 'hello-to-forward')

Say $bob 'download alice 4'
Wait-S 2
Assert 'same content offered again: nothing to download' (Has 'bob' $mBob 'Already available')

Say $bob 'forward alice 5 carol'
Wait-S 2
Assert 'forward of a file not downloaded is refused' (Has 'bob' $mBob 'Download it first')

Say $bob 'forward alice 1 carol'
Wait-S 3
Assert 'carol got the forwarded file offer' (Has 'carol' $mCarol '\[file\] small file\.bin \(230000 bytes')
Assert 'forward did not duplicate the blob on bob' (@(Get-ChildItem $blobDir -File).Count -eq $blobsBefore)
Say $carol 'download bob 2'
Wait-S 6
$doneC = Since 'carol' $mCarol | Where-Object { $_ -match 'file received\]' } | Select-Object -First 1
$recvC = if ($doneC -match '-> (.+)$') { $Matches[1].Trim() } else { $null }
Assert 'carol downloaded the forwarded file' ([bool]$doneC)
Assert 'forwarded file is byte-identical' ($recvC -and (Test-Path $recvC) -and ((Get-FileHash $recvC).Hash -eq (Get-FileHash $f1).Hash))

# 4. real interruption mid-transfer. Localhost is too fast to cut a transfer, so the sender is slowed
#    down (FREESPEECH_CHUNK_DELAY_MS): ~10 s for an 8 MB file. Cut the sender after ~2 s, bring it back,
#    the receiver must finish from where it stopped.
try { $alice.Kill() } catch {}
$alice3 = Start-Proc 'alice3' "`"$cli`" ${prof}a $url" @{ FREESPEECH_CHUNK_DELAY_MS = '60' }
Wait-S 3
$f4 = Join-Path $tmp 'big8.bin'; $b = New-Object byte[] 8000000; $rng.GetBytes($b); [IO.File]::WriteAllBytes($f4, $b)   # 163 chunks
$mBob = Mark 'bob'
Say $alice3 "upload bob $f4"                  # bob history with alice: #6
Wait-S 4
Assert 'bob got the 8 MB offer' (Has 'bob' $mBob '\[file\] big8\.bin \(8000000 bytes')
Say $bob 'download alice 6'
Wait-S 2
try { $alice3.Kill() } catch {}               # link cut mid-file
Wait-S 1
Say $bob 'files'
Wait-S 1
$partial = Since 'bob' $mBob | Where-Object { $_ -match 'big8\.bin \(8000000 bytes\) Receiving (\d+)/(\d+) chunks' } | Select-Object -Last 1
$got = 0; $total = 0
if ($partial -match 'Receiving (\d+)/(\d+) chunks') { $got = [int]$Matches[1]; $total = [int]$Matches[2] }
Assert "transfer interrupted mid-file ($got/$total chunks held)" ($got -gt 0 -and $got -lt $total)
$alice4 = Start-Proc 'alice4' "`"$cli`" ${prof}a $url" @{}
Wait-S 100
$done4 = Since 'bob' $mBob | Where-Object { $_ -match 'file received\] ''big8' } | Select-Object -First 1
$recv4 = if ($done4 -match '-> (.+)$') { $Matches[1].Trim() } else { $null }
Assert 'receiver finished after the sender came back' ([bool]$done4)
Assert 'file completed after interruption is byte-identical' ($recv4 -and (Test-Path $recv4) -and ((Get-FileHash $recv4).Hash -eq (Get-FileHash $f4).Hash))

"`n===== RESULTS ====="
$script:results
if ($env:SHOW_LOGS) { Show 'alice'; Show 'alice2'; Show 'alice3'; Show 'alice4'; Show 'bob'; Show 'carol' }
foreach ($p in @($alice, $alice3, $alice4, $bob, $carol, $sa)) { try { $p.Kill() } catch {} }
try { [IO.Directory]::Delete($tmp, $true) } catch {}
if (@($script:results | Where-Object { $_ -like 'FAIL*' }).Count -gt 0) { exit 1 }


