# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Automated group test (protocol §7): one server, four console clients.
# alice (moderator) knows bob, carol, dave as contacts; bob/carol/dave do NOT know each other.
#   1. create with bob+carol -> invitations, then messages both ways (bob -> carol are not contacts)
#   2. add dave -> key rotated: dave reads what follows, NOT what came before
#   3. remove carol -> she learns it and cannot read what follows
#   3b. group file: bob downloads it from alice, then dave (not bob's contact) downloads it from BOB while alice is away
#   3c. blacklist: alice bans bob (no rotation) -> nobody shows him; the creator lifts it
#   4. dave offline, then alice ALSO gone: dave catches up from bob alone (p2p)
# Prints PASS/FAIL per check (exit 1 on any FAIL); $env:SHOW_LOGS=1 dumps the consoles.
#
# USAGE:  scripts\test-groups.ps1     (needs a prior `dotnet build sources`; ~4 min)

. "$PSScriptRoot\test-lib.ps1"
# $env:FS_SERVER (e.g. https://relay.exemple.org) runs the test against an existing server instead of a local one.
$url = if ($env:FS_SERVER) { $env:FS_SERVER } else { 'http://localhost:5431' }
$sa = $null
if (-not $env:FS_SERVER) { $sa = Start-Proc 'srv' "`"$srv`"" @{ ASPNETCORE_URLS = 'http://localhost:5431' } }
Wait-S 5
$prof = 'tg' + (Get-Random -Maximum 9999)
$alice = Start-Proc 'alice' "`"$cli`" ${prof}a $url" @{}
$bob = Start-Proc 'bob' "`"$cli`" ${prof}b $url" @{}
$carol = Start-Proc 'carol' "`"$cli`" ${prof}c $url" @{}
$dave = Start-Proc 'dave' "`"$cli`" ${prof}d $url" @{}
Wait-S 2
foreach ($p in $alicePh) { Say $alice $p }; Say $alice 'alice'
foreach ($p in $bobPh) { Say $bob $p }; Say $bob 'bob'
foreach ($n in @(@($carol, 'carol'), @($dave, 'dave'))) { foreach ($i in 1..3) { Say $n[0] (New-Phrase) }; Say $n[0] $n[1] }
Wait-S 4

# alice <-> each of the others: contact exchange (the only contacts that exist)
foreach ($peer in @(@($bob, 'bob'), @($carol, 'carol'), @($dave, 'dave'))) {
  $m = Mark 'alice'
  Say $alice 'export qr'
  Wait-S 2
  $card = (Since 'alice' $m | Where-Object { $_ -match '^[A-Za-z0-9+/=]{200,}$' } | Select-Object -Last 1)
  Say $peer[0] "import $card"
  Wait-S 5
}
Assert 'contacts: alice knows bob, carol and dave' ((Count 'alice' 0 'contact confirmed\]') -eq 3)

# 1. create + messages
$mB = Mark 'bob'; $mC = Mark 'carol'
Say $alice 'group create team bob carol'
Wait-S 4
Assert 'bob is invited, not yet a member' (Has 'bob' $mB "invites you to 'team'")
Assert 'carol is invited, not yet a member' (Has 'carol' $mC "invites you to 'team'")
$mB = Mark 'bob'; $mC = Mark 'carol'
Say $bob 'group accept team'
Say $carol 'group accept team'
Wait-S 4
Assert 'bob joined after accepting' (Has 'bob' $mB "You joined 'team'")
Assert 'carol joined after accepting' (Has 'carol' $mC "You joined 'team'")

$mB = Mark 'bob'; $mC = Mark 'carol'
Say $alice 'group send team hello-group'
Wait-S 3
Assert 'bob receives the group message' (Has 'bob' $mB '\[team\] alice: hello-group')
Assert 'carol receives the group message' (Has 'carol' $mC '\[team\] alice: hello-group')

$mA = Mark 'alice'; $mC = Mark 'carol'
Say $bob 'group send team from-bob'
Wait-S 3
Assert 'alice receives bob''s message' (Has 'alice' $mA '\[team\] bob: from-bob')
Assert 'carol receives bob''s message although they are not contacts' (Has 'carol' $mC '\[team\] bob: from-bob')

# 2. add dave -> rotation
$mD = Mark 'dave'; $mB = Mark 'bob'
Say $alice 'group add team dave'
Wait-S 5
Assert 'dave is invited' (Has 'dave' $mD "invites you to 'team'")
Say $dave 'group accept team'
Wait-S 4
Assert 'dave joined the group' (Has 'dave' $mD "You joined 'team'")
$mD = Mark 'dave'
Say $alice 'group send team after-dave'
Wait-S 3
Assert 'dave reads messages sent after he joined' (Has 'dave' $mD '\[team\] alice: after-dave')
Wait-S 35   # dave also synced once with the others: he must NOT get what predates his key
Assert 'dave cannot read what came before he joined' (-not (Has 'dave' 0 'hello-group|from-bob'))

# 3. remove carol -> she learns it, cannot read on
$mC = Mark 'carol'
Say $alice 'group remove team carol'
Wait-S 4
Assert 'carol learns she was removed' (Has 'carol' $mC 'no longer a member')
$mB = Mark 'bob'; $mD = Mark 'dave'; $mC = Mark 'carol'
Say $alice 'group send team carol-cannot-read'
Wait-S 3
Assert 'bob still reads' (Has 'bob' $mB 'carol-cannot-read')
Assert 'dave still reads' (Has 'dave' $mD 'carol-cannot-read')
Assert 'carol does not read after her removal' (-not (Has 'carol' $mC 'carol-cannot-read'))

# 3b. group file: alice offers 2 MB, bob downloads it, alice disappears briefly, dave (not a contact of bob) fetches it FROM BOB
$tmp = Join-Path $env:TEMP "fs-grp-$prof"; New-Item -ItemType Directory -Force $tmp | Out-Null
$fs = Join-Path $tmp 'swarm.bin'; $rb = New-Object byte[] 2000000; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($rb); [IO.File]::WriteAllBytes($fs, $rb)
$mB = Mark 'bob'; $mD = Mark 'dave'
Say $alice "group upload team $fs"
Wait-S 4
Assert 'bob sees the file offer' (Has 'bob' $mB '\[team\] alice: \[file\] swarm\.bin')
Assert 'dave sees the file offer' (Has 'dave' $mD '\[team\] alice: \[file\] swarm\.bin')

function Index-Of-File($who, $proc) {
  $m = Mark $who; Say $proc 'group history team'; Wait-S 2
  $line = Since $who $m | Where-Object { $_ -match '#(\d+) .*\[file\] swarm\.bin' } | Select-Object -First 1
  if ($line -match '#(\d+) ') { [int]$Matches[1] } else { 0 }
}
$ib = Index-Of-File 'bob' $bob
$mB = Mark 'bob'
Say $bob "group download team $ib"
Wait-S 8
$doneB = Since 'bob' $mB | Where-Object { $_ -match 'file received\]' } | Select-Object -First 1
$pathB = if ($doneB -match '-> (.+)$') { $Matches[1].Trim() } else { $null }
Assert 'bob downloaded the group file' ([bool]$doneB)
Assert 'bob''s copy is byte-identical' ($pathB -and (Test-Path $pathB) -and ((Get-FileHash $pathB).Hash -eq (Get-FileHash $fs).Hash))

$aliceDown = $true
try { $alice.Kill() } catch {}                   # the author is gone: dave can only get it from bob
Wait-S 2
$id = Index-Of-File 'dave' $dave
$mD = Mark 'dave'
Say $dave "group download team $id"
Wait-S 45                                        # first round may hit the absent author's share; the stall timer (20 s) re-spreads it
$doneD = Since 'dave' $mD | Where-Object { $_ -match 'file received\]' } | Select-Object -First 1
$pathD = if ($doneD -match '-> (.+)$') { $Matches[1].Trim() } else { $null }
Assert 'dave got the file from bob while the author was away' ([bool]$doneD)
Assert 'dave''s copy is byte-identical' ($pathD -and (Test-Path $pathD) -and ((Get-FileHash $pathD).Hash -eq (Get-FileHash $fs).Hash))
$alice = Start-Proc 'alice2' "`"$cli`" ${prof}a $url" @{}
Wait-S 3

# 3c. blacklist: alice bans bob (still a member, no rotation): nobody shows him; the creator lifts it
$mA = Mark 'alice2'; $mD = Mark 'dave'
Say $alice 'group ban team bob'
Wait-S 3
Say $bob 'group send team muted-bob'
Wait-S 3
Assert 'banned author is not shown to the moderator' (-not (Has 'alice2' $mA 'muted-bob'))
Assert 'banned author is not shown to the others' (-not (Has 'dave' $mD 'muted-bob'))
$mA = Mark 'alice2'; $mD = Mark 'dave'
Say $alice 'group unban team bob'
Wait-S 3
Say $bob 'group send team unmuted-bob'
Wait-S 3
Assert 'after the creator lifts the ban, bob is heard again' ((Has 'alice2' $mA 'unmuted-bob') -and (Has 'dave' $mD 'unmuted-bob'))
# 3d. forward across conversation kinds
#     group -> contact: bob re-offers the group file to alice (a contact); alice already holds that content
$mA = Mark 'alice2'
Say $bob "forward group:team $ib alice"
Wait-S 3
Assert 'forward group file -> contact: alice sees the offer' (Has 'alice2' $mA '\[file\] swarm\.bin')
#     contact -> group: bob sends alice a private text; alice forwards it to the group
Say $bob 'send alice hello-direct'
Wait-S 2
$mA = Mark 'alice2'; Say $alice 'history bob'; Wait-S 2
$hl = Since 'alice2' $mA | Where-Object { $_ -match '#(\d+) .*hello-direct' } | Select-Object -First 1
$hi = if ($hl -match '#(\d+) ') { [int]$Matches[1] } else { 0 }
$mD = Mark 'dave'
Say $alice "forward bob $hi group:team"
Wait-S 3
Assert 'forward contact text -> group: dave reads it in the group' (Has 'dave' $mD '\[team\] alice: hello-direct')
try { [IO.Directory]::Delete($tmp, $true) } catch {}

# 3e. names: the creator renames the group; bob changes his own name (contacts + groups are told)
$mB = Mark 'bob'
Say $alice 'group rename team Crew'
Wait-S 3
Say $alice 'group send Crew renamed-hello'
Wait-S 3
Assert 'rename: members see the new group name' (Has 'bob' $mB '\[Crew\] alice: renamed-hello')
Say $bob 'group rename Crew nope'                # a plain member is refused (error on bob's console)
Wait-S 2
Assert 'rename: a non-creator is refused' (Has 'bob' $mB 'Only the creator')
Say $alice 'group rename Crew team'
Wait-S 3
$mA = Mark 'alice2'
Say $bob 'profile name Bobby'
Wait-S 4
Assert 'profile: contacts are told of the new name' (Has 'alice2' $mA 'contact updated\] Bobby')
$mA = Mark 'alice2'
Say $bob 'group send team hi-as-bobby'
Wait-S 3
Assert 'profile: group members see the new name' (Has 'alice2' $mA '\[team\] Bobby: hi-as-bobby')

# 3e-bis. descriptions: mine (contact card + groups) and the group's (creator)
$mA = Mark 'alice2'
Say $bob 'profile about Backend dev, loves rust'
Wait-S 4
Say $alice 'card Bobby'                            # bob renamed himself: the contact now carries his new name
Wait-S 2
Assert 'about: the contact card shows the new description' (Has 'alice2' $mA 'About   : Backend dev, loves rust')
$mA = Mark 'alice2'
Say $alice 'group members team'
Wait-S 2
Assert 'about: group members list shows it too' (Has 'alice2' $mA 'Bobby .* Backend dev, loves rust')
$mB = Mark 'bob'
Say $alice 'group about team Weekly ops sync'
Wait-S 3
Say $bob 'group info team'
Wait-S 2
Assert 'about: group description reaches members' (Has 'bob' $mB 'About   : Weekly ops sync')
Say $bob 'group about team hijack'
Wait-S 2
Assert 'about: a non-creator cannot change it' (Has 'bob' $mB 'Only the creator')

# 3f. reply, demote, forget
$m2 = Mark 'alice2'; Say $alice 'group history team'; Wait-S 2
$rl = Since 'alice2' $m2 | Where-Object { $_ -match '#(\d+) .*carol-cannot-read' } | Select-Object -First 1
$ri = if ($rl -match '#(\d+) ') { [int]$Matches[1] } else { 0 }
$mD = Mark 'dave'
Say $alice "group reply team $ri answering-that"
Wait-S 3
Assert 'reply: dave sees which message it answers' (Has 'dave' $mD 'answering-that \(re: #\d+\)')

# 3f-bis. paged history (what a phone screen reads: newest first, then scroll up)
$mP = Mark 'alice2'; Say $alice 'group history team last 3'; Wait-S 2
Assert 'paging: "last 3" shows exactly three messages' ((Count 'alice2' $mP '#\d+ \[\d{4}-') -eq 3)
Assert 'paging: a hint offers the older messages' (Has 'alice2' $mP 'older messages: group history team before #\d+')
$hint = Since 'alice2' $mP | Where-Object { $_ -match 'before #(\d+)' } | Select-Object -First 1
$fi = if ($hint -match 'before #(\d+)') { [int]$Matches[1] } else { 0 }
$mQ = Mark 'alice2'; Say $alice "group history team before #$fi 2"; Wait-S 2
$idx = @(Since 'alice2' $mQ | Where-Object { $_ -match '#(\d+) \[\d{4}-' } | ForEach-Object { [int]([regex]::Match($_, '#(\d+)').Groups[1].Value) })
Assert 'paging: "before #N 2" gives the two messages right before it' ($idx.Count -eq 2 -and ($idx | Measure-Object -Maximum).Maximum -eq ($fi - 1))
$mR = Mark 'alice2'; Say $alice "group history team around #$fi 3"; Wait-S 2
Assert 'paging: "around #N" includes that message' (Has 'alice2' $mR ("#$fi \[\d{4}-"))

# 3f-ter. edit / delete for me / delete for everyone / file removal (groups, then 1-to-1)
function Idx($who, $proc, $cmd, $rx) {
  $m = Mark $who; Say $proc $cmd; Wait-S 2
  $line = Since $who $m | Where-Object { $_ -match $rx } | Select-Object -First 1
  if ($line -match '#(\d+) ') { [int]$Matches[1] } else { 0 }
}
Say $alice 'group send team chg-one'; Wait-S 2
Say $alice 'group send team chg-two'; Wait-S 3
$ia1 = Idx 'alice2' $alice 'group history team' 'chg-one'
$ia2 = Idx 'alice2' $alice 'group history team' 'chg-two'
$mB = Mark 'bob'; $mD = Mark 'dave'
Say $alice "group edit team $ia1 chg-one-edited"; Wait-S 3
Assert 'edit: members are told about the edit' ((Has 'bob' $mB 'edited a message.*chg-one-edited') -and (Has 'dave' $mD 'edited a message.*chg-one-edited'))
$mD = Mark 'dave'; Say $dave 'group history team'; Wait-S 2
Assert 'edit: the history shows the new text, marked edited' (Has 'dave' $mD 'chg-one-edited \(edited\)')
Assert 'edit: the old text is gone' (-not (Has 'dave' $mD 'chg-one( |$)'))

$ib1 = Idx 'bob' $bob 'group history team' 'chg-one-edited'
$mB = Mark 'bob'; Say $bob "group edit team $ib1 hijacked"; Wait-S 2
Assert 'edit: only the author may edit' (Has 'bob' $mB 'only edit your own')
$mB = Mark 'bob'; Say $bob "group retract team $ib1"; Wait-S 2
Assert 'retract: only the author may delete for everyone' (Has 'bob' $mB 'only delete for everyone your own')

$mB = Mark 'bob'; $mD = Mark 'dave'
Say $alice "group retract team $ia2"; Wait-S 3
Assert 'retract: members are told' ((Has 'bob' $mB 'deleted a message') -and (Has 'dave' $mD 'deleted a message'))
$mD = Mark 'dave'; Say $dave 'group history team'; Wait-S 2
Assert 'retract: the line stays as "message deleted"' (Has 'dave' $mD '\[message deleted\]')
Assert 'retract: its content is gone from the history' (-not (Has 'dave' $mD 'chg-two'))

$id1 = Idx 'dave' $dave 'group history team' 'chg-one-edited'
$mD = Mark 'dave'; Say $dave "group delete team $id1"; Wait-S 2
Assert 'delete: confirmed on this device' (Has 'dave' $mD 'deleted on this device')
$mD = Mark 'dave'; Say $dave 'group history team'; Wait-S 2
Assert 'delete: hidden from dave' (-not (Has 'dave' $mD 'chg-one-edited'))
$mB = Mark 'bob'; Say $bob 'group history team'; Wait-S 2
Assert 'delete: still there for the others' (Has 'bob' $mB 'chg-one-edited')

# file: bob downloads it, then the author removes it for everyone
New-Item -ItemType Directory -Force $tmp | Out-Null
$fc = Join-Path $tmp 'chg.bin'; $cb = New-Object byte[] 300000; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($cb); [IO.File]::WriteAllBytes($fc, $cb)
$mB = Mark 'bob'; Say $alice "group upload team $fc"; Wait-S 4
$ibf = Idx 'bob' $bob 'group history team' 'chg\.bin'
$mB = Mark 'bob'; Say $bob "group download team $ibf"; Wait-S 6
$doneF = Since 'bob' $mB | Where-Object { $_ -match 'file received\]' } | Select-Object -First 1
$pathF = if ($doneF -match '-> (.+)$') { $Matches[1].Trim() } else { $null }
Assert 'file: bob holds the file before the removal' ($pathF -and (Test-Path $pathF))
$iaf = Idx 'alice2' $alice 'group history team' 'chg\.bin'
$mB = Mark 'bob'; Say $alice "group retractfile team $iaf"; Wait-S 3
Assert 'file: members are told the file was removed' (Has 'bob' $mB 'removed a file')
Assert 'file: bob''s copy is deleted from disk' ($pathF -and -not (Test-Path $pathF))
$mB = Mark 'bob'; Say $bob 'group history team'; Wait-S 2
Assert 'file: the line stays, the file is marked removed' (Has 'bob' $mB 'file removed by the sender')

# 1-to-1: edit, then delete for everyone
$mB = Mark 'bob'; Say $alice 'send Bobby oto-first'; Wait-S 3
$io = Idx 'alice2' $alice 'history Bobby' 'oto-first'
$mB = Mark 'bob'; Say $alice "edit Bobby $io oto-edited"; Wait-S 3
Assert '1-to-1 edit: bob is told' (Has 'bob' $mB 'edited a message.*oto-edited')
$mB = Mark 'bob'; Say $alice "retract Bobby $io"; Wait-S 3
Assert '1-to-1 retract: bob is told' (Has 'bob' $mB 'deleted a message')
$mA = Mark 'alice2'; Say $alice 'history Bobby'; Wait-S 2
Assert '1-to-1 retract: the line stays on the author''s side too' (Has 'alice2' $mA '\[message deleted\]')
$mA = Mark 'alice2'
Say $alice 'group promote team bob'
Wait-S 3
Say $alice 'group demote team bob'
Wait-S 3
Assert 'demote: the creator takes the moderator role back' (Has 'alice2' $mA 'no longer a moderator')
$mB = Mark 'bob'
Say $bob 'group promote team dave'                # bob is an ordinary member again
Wait-S 2
Assert 'demote: bob can no longer administer the group' (Has 'bob' $mB 'Only a moderator')

$mC = Mark 'carol'
Say $carol 'group forget team'                    # carol was removed earlier: her copy can be deleted
Wait-S 2
Assert 'forget: a group we are out of can be deleted locally' (Has 'carol' $mC 'Local data')
Say $carol 'group list'
Wait-S 2
Assert 'forget: it is gone from her list' (Has 'carol' $mC 'No groups')

# 4. p2p catch-up: dave offline, alice writes then disappears, dave returns and still gets everything from bob
try { $dave.Kill() } catch {}
Wait-S 2
Say $alice 'group send team while-dave-off-1'
Wait-S 2
Say $bob 'group send team while-dave-off-2'
Wait-S 2
try { $alice.Kill() } catch {}                  # the author is gone: only bob can help
Wait-S 2
$dave2 = Start-Proc 'dave2' "`"$cli`" ${prof}d $url" @{}
Wait-S 75                                       # background tick (30 s) -> group_sync to the online members
Assert 'dave caught up alice''s message from bob alone' (Has 'dave2' 0 '\[team\] alice: while-dave-off-1')
Assert 'dave caught up bob''s message' (Has 'dave2' 0 '\[team\] (bob|Bobby): while-dave-off-2')

if ($env:SHOW_LOGS) { Show 'alice'; Show 'alice2'; Show 'bob'; Show 'carol'; Show 'dave'; Show 'dave2' }
Finish-Test @($alice, $bob, $carol, $dave, $dave2, $sa)
