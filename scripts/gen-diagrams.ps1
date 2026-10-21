# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Regenerates diagrams 11-14 (sources\diagrams, -fr and -en): signed registration, server move,
# multi-gateway push, Argon2id derivation. Edit the texts here, then run: scripts\gen-diagrams.ps1
# (file saved as UTF-8 with BOM: Windows PowerShell 5.1 needs it for the accents)
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'sources\diagrams'
function Esc($s) { $s.Replace('~',[string][char]0x2019).Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;') }
$script:b = ''
function Add($s) { $script:b += "  $s`n" }
function Actor($x, $t) { Add "<text x=`"$x`" y=`"30`" class=`"actor`" text-anchor=`"middle`">$(Esc $t)</text>"; }
function Life($x, $y2) { Add "<line x1=`"$x`" y1=`"45`" x2=`"$x`" y2=`"$y2`" stroke=`"#1c1a17`" opacity=`"0.25`"/>" }
function Arrow($x1, $x2, $y, $t, $t2, $kind) {
  $col = switch ($kind) { 'warn' { '#a4402f' } 'ink' { '#1c1a17' } default { '#2f6f6e' } }
  $mk = switch ($kind) { 'warn' { 'arrowWarn' } 'ink' { 'arrowInk' } default { 'arrowAcc' } }
  $e = if ($x2 -gt $x1) { $x2 - 4 } else { $x2 + 4 }
  $s = if ($x2 -gt $x1) { $x1 + 4 } else { $x1 - 4 }
  $mid = ($x1 + $x2) / 2
  Add "<path d=`"M$s $y L$e $y`" fill=`"none`" stroke=`"$col`" stroke-width=`"1.6`" marker-end=`"url(#$mk)`"/>"
  Add "<text x=`"$mid`" y=`"$($y - 7)`" class=`"lbl`" text-anchor=`"middle`">$(Esc $t)</text>"
  if ($t2) { Add "<text x=`"$mid`" y=`"$($y + 14)`" class=`"lbl`" text-anchor=`"middle`" opacity=`"0.75`">$(Esc $t2)</text>" }
}
function Note($x, $y, $t) {
  $w = [int]($t.Length * 6.6 + 18)
  Add "<rect x=`"$([int]($x - $w / 2))`" y=`"$y`" width=`"$w`" height=`"24`" rx=`"4`" fill=`"#dff0ef`"/>"
  Add "<text x=`"$x`" y=`"$($y + 16)`" class=`"lbl-strong`" text-anchor=`"middle`">$(Esc $t)</text>"
}
function Box($x, $y, $w, $h, $t, $fill) {
  Add "<rect x=`"$x`" y=`"$y`" width=`"$w`" height=`"$h`" rx=`"5`" fill=`"$fill`" stroke=`"#1c1a17`" stroke-opacity=`"0.35`"/>"
  $lines = $t -split '\|'
  $n = $lines.Count
  for ($i = 0; $i -lt $n; $i++) {
    $ty = $y + $h / 2 + 4 + ($i - ($n - 1) / 2) * 15
    Add "<text x=`"$($x + $w / 2)`" y=`"$ty`" class=`"lbl-strong`" text-anchor=`"middle`">$(Esc $lines[$i])</text>"
  }
}
function VArrow($x, $y1, $y2) { Add "<path d=`"M$x $y1 L$x $($y2 - 4)`" fill=`"none`" stroke=`"#2f6f6e`" stroke-width=`"1.6`" marker-end=`"url(#arrowAcc)`"/>" }
function Foot($y, $t) { Add "<text x=`"20`" y=`"$y`" font-family=`"'IBM Plex Sans',sans-serif`" font-size=`"12`" fill=`"#6b665d`">$(Esc $t)</text>" }
function Save($name, $lang, $w, $h, $aria) {
  $svg = "<svg viewBox=`"0 0 $w $h`" xmlns=`"http://www.w3.org/2000/svg`" font-family=`"'IBM Plex Mono',ui-monospace,Consolas,monospace`" role=`"img`" aria-label=`"$(Esc $aria)`">`n" +
    "  <rect x=`"0`" y=`"0`" width=`"$w`" height=`"$h`" fill=`"#ffffff`"/>`n" +
    "  <style>`n    .lbl{font-size:11px;fill:#6b665d;font-family:'IBM Plex Mono',monospace}`n    .lbl-strong{font-size:11.5px;fill:#1c1a17;font-weight:600;font-family:'IBM Plex Mono',monospace}`n    .actor{font-size:12.5px;fill:#1c1a17;font-weight:600;font-family:'IBM Plex Mono',monospace}`n  </style>`n" +
    $script:b +
    "  <defs>`n    <marker id=`"arrowInk`" markerWidth=`"8`" markerHeight=`"8`" refX=`"6`" refY=`"3`" orient=`"auto`"><polygon points=`"0,0 7,3 0,6`" fill=`"#1c1a17`"/></marker>`n    <marker id=`"arrowAcc`" markerWidth=`"8`" markerHeight=`"8`" refX=`"6`" refY=`"3`" orient=`"auto`"><polygon points=`"0,0 7,3 0,6`" fill=`"#2f6f6e`"/></marker>`n    <marker id=`"arrowWarn`" markerWidth=`"8`" markerHeight=`"8`" refX=`"6`" refY=`"3`" orient=`"auto`"><polygon points=`"0,0 7,3 0,6`" fill=`"#a4402f`"/></marker>`n  </defs>`n</svg>`n"
  [IO.File]::WriteAllText("$out\$name-$lang.svg", $svg, (New-Object Text.UTF8Encoding($false)))
  $script:b = ''
}

foreach ($lang in 'fr', 'en') {
  $fr = $lang -eq 'fr'

  # ---------- 11 signed registration ----------
  Actor 150 ($(if ($fr) { 'Client' } else { 'Client' })); Actor 710 ($(if ($fr) { 'Serveur' } else { 'Server' }))
  Life 150 300; Life 710 300
  Arrow 150 710 70 'register {discovery_id, enc_key, sign_key}'
  Note 710 85 'SHA-256(enc_key ‖ sign_key) = discovery_id ?'
  Arrow 710 150 135 $(if ($fr) { 'challenge {nonce 32 o, usage unique}' } else { 'challenge {nonce 32 B, single use}' })
  Note 330 150 $(if ($fr) { 'signe (Ed25519) : freespeech-register-v1|nonce|discovery_id' } else { 'signs (Ed25519): freespeech-register-v1|nonce|discovery_id' })
  Arrow 150 710 215 'register_proof {signature}'
  Note 600 228 $(if ($fr) { 'vérifie avec sign_key' } else { 'verifies with sign_key' })
  Arrow 710 150 275 $(if ($fr) { 'registered {http_token (192 bits)}' } else { 'registered {http_token (192 bits)}' })
  Foot 322 $(if ($fr) { 'Échec (clés ≠ identifiant, signature fausse, preuve sans register) : error, inscription annulée. Le nonce sert une seule fois.' } else { 'Failure (keys do not match the id, wrong signature, proof without register): error, registration cancelled. A nonce is used once.' })
  Save '11-signed-registration' $lang 860 340 $(if ($fr) { 'Inscription avec preuve d~identité : register, challenge, signature du nonce, vérification, registered avec le jeton HTTP' } else { 'Registration with proof of identity: register, challenge, signed nonce, verification, registered with the HTTP token' })

  # ---------- 12 server move ----------
  Actor 110 'Client'; Actor 360 $(if ($fr) { 'Serveur plein' } else { 'Full server' }); Actor 610 $(if ($fr) { 'Serveur frère' } else { 'Sibling server' }); Actor 860 $(if ($fr) { 'Contacts / groupes' } else { 'Contacts / groups' })
  foreach ($x in 110, 360, 610, 860) { Life $x 440 }
  Arrow 360 110 70 'registered'
  Note 360 82 $(if ($fr) { 'au-delà de MaxConnections' } else { 'over MaxConnections' })
  Arrow 360 610 135 $(if ($fr) { 'POST /s2s/capacity (signé)' } else { 'POST /s2s/capacity (signed)' })
  Arrow 610 360 165 '{free_slots: n}'
  Note 360 180 $(if ($fr) { 'écarte les pleins, tirage pondéré, 3 au plus (cache 15 s)' } else { 'drops the full ones, weighted draw, 3 at most (15 s cache)' })
  Arrow 360 110 245 'server_redirect {servers, reason}'
  Note 330 258 $(if ($fr) { 'suit si : l~un de MES serveurs, https, 3 fois / 24 h au plus' } else { 'follows if: one of MY servers, https, 3 times / 24 h at most' })
  Arrow 110 610 320 $(if ($fr) { 'register + preuve (§11)' } else { 'register + proof (§11)' })
  Arrow 610 110 350 'registered'
  Arrow 110 860 400 $(if ($fr) { 'identity_update (nouveau serveur, mis en file si hors ligne)' } else { 'identity_update (new server, queued if offline)' })
  Foot 462 $(if ($fr) { 'Rien n~est déplacé de force : sans frère disponible, le client reste. Un contact qui écrit encore à l~ancien serveur retrouve le client par la cascade S2S.' } else { 'Nothing is moved by force: with no sibling available, the client stays. A contact still writing to the old server finds the client through the S2S cascade.' })
  Save '12-server-move' $lang 980 480 $(if ($fr) { 'Déplacement d~un client : le serveur plein interroge ses frères, suggère server_redirect, le client s~inscrit sur le nouveau serveur puis prévient ses contacts' } else { 'Moving a client: the full server asks its siblings, suggests server_redirect, the client registers on the new server then tells its contacts' })

  # ---------- 13 multi-gateway push ----------
  Actor 110 $(if ($fr) { 'Serveur relais' } else { 'Relay server' }); Actor 360 $(if ($fr) { 'Passerelle A (celle de l~appareil)' } else { 'Gateway A (the device~s)' }); Actor 610 $(if ($fr) { 'Passerelle B' } else { 'Gateway B' }); Actor 860 $(if ($fr) { 'FCM / appareil' } else { 'FCM / device' })
  foreach ($x in 110, 360, 610, 860) { Life $x 400 }
  Arrow 110 360 70 'POST /push {wake}'
  Arrow 360 110 105 $(if ($fr) { '503 / délai / 429 busy' } else { '503 / timeout / 429 busy' }) '' 'warn'
  Note 250 120 $(if ($fr) { 'suivante, ordre aléatoire, 3 au plus' } else { 'next one, random order, 3 at most' })
  Arrow 110 610 175 'POST /push {wake}'
  Arrow 610 860 215 $(if ($fr) { 'message de données FCM' } else { 'FCM data message' }) $(if ($fr) { '(réveil seul, sans contenu)' } else { '(wake-up only, no content)' })
  Arrow 610 110 255 '200 OK'
  Note 780 268 $(if ($fr) { 'se réveille, se connecte, READY' } else { 'wakes up, connects, READY' })
  Note 480 315 $(if ($fr) { '429 token : arrêt (déjà réveillé) · 410 : jeton supprimé · autre 4xx : pas de reprise' } else { '429 token: stop (already woken) · 410: token dropped · other 4xx: no retry' })
  Note 480 347 $(if ($fr) { 'Disjoncteur : 3 échecs de suite → 45 s ; busy + Retry-After N → N s (120 s au plus)' } else { 'Circuit breaker: 3 failures in a row → 45 s; busy + Retry-After N → N s (120 s at most)' })
  Foot 392 $(if ($fr) { 'Au plus un réveil par identité toutes les 30 s ; sans passerelle disponible, il reste le sondage du système.' } else { 'At most one wake-up per identity every 30 s; with no gateway available, OS polling remains.' })
  Save '13-push-multi-gateway' $lang 980 410 $(if ($fr) { 'Push multi-passerelles : le relais essaie la passerelle de l~appareil puis une autre, avec disjoncteur' } else { 'Multi-gateway push: the relay tries the device~s gateway then another one, with a circuit breaker' })

  # ---------- 14 Argon2id derivation ----------
  $ph = if ($fr) { 'phrase' } else { 'phrase' }
  Box 60 15 200 32 "$ph 1" '#f3efe6'; Box 330 15 200 32 "$ph 2" '#f3efe6'; Box 600 15 200 32 "$ph 3" '#f3efe6'
  foreach ($x in 160, 430, 700) { VArrow $x 47 78 }
  Box 60 78 740 32 $(if ($fr) { 'p1 ‖ 0x00 ‖ p2 ‖ 0x00 ‖ p3   (chacune sans espaces de bord, UTF-8)' } else { 'p1 ‖ 0x00 ‖ p2 ‖ 0x00 ‖ p3   (each trimmed, UTF-8)' }) '#f3efe6'
  VArrow 430 110 135
  Box 60 135 740 44 $(if ($fr) { 'Argon2id|sel fixe « FreeSpeech-v3-seed » · 32 Mio · t = 3 · p = 1 → 32 o' } else { 'Argon2id|fixed salt "FreeSpeech-v3-seed" · 32 MiB · t = 3 · p = 1 → 32 B' }) '#dff0ef'
  VArrow 430 179 204
  Box 330 204 200 30 'seed (32 B)' '#f3efe6'
  foreach ($x in 160, 430, 700) { VArrow $x 234 262 }
  Add "<text x=`"442`" y=`"253`" class=`"lbl`" text-anchor=`"start`" opacity=`"0.75`">HKDF-SHA256, salt « FreeSpeech-v2 »</text>"
  Box 60 262 200 44 "enc_seed|info = encryption" '#dff0ef'; Box 330 262 200 44 "sign_seed|info = signing" '#dff0ef'; Box 600 262 200 44 "db_key|info = database-v1" '#dff0ef'
  foreach ($x in 160, 430, 700) { VArrow $x 306 332 }
  Box 60 332 200 44 $(if ($fr) { 'X25519|chiffrement' } else { 'X25519|encryption' }) '#f3efe6'; Box 330 332 200 44 $(if ($fr) { 'Ed25519|signature' } else { 'Ed25519|signature' }) '#f3efe6'; Box 600 332 200 44 $(if ($fr) { 'base locale|chiffrée' } else { 'local database|encrypted' }) '#f3efe6'
  Add "<path d=`"M160 376 L160 396 L430 396`" fill=`"none`" stroke=`"#2f6f6e`" stroke-width=`"1.6`"/>"
  Add "<path d=`"M430 376 L430 396`" fill=`"none`" stroke=`"#2f6f6e`" stroke-width=`"1.6`"/>"
  VArrow 430 396 420
  Box 200 420 460 30 'discovery_id = SHA-256(enc_pub ‖ sign_pub)' '#dff0ef'
  Foot 478 $(if ($fr) { 'Mêmes trois phrases → même identité sur tout appareil, sans serveur. Perdues, elles ne se récupèrent pas.' } else { 'Same three phrases → same identity on any device, no server. Lost, they cannot be recovered.' })
  Save '14-argon2id-derivation' $lang 860 495 $(if ($fr) { 'Dérivation de l~identité : trois phrases, Argon2id, seed, trois HKDF, clés X25519 et Ed25519, clé de base locale, discovery_id' } else { 'Identity derivation: three phrases, Argon2id, seed, three HKDF, X25519 and Ed25519 keys, local database key, discovery_id' })
}
Get-ChildItem $out -Filter '1[1-4]-*' | Select-Object Name, Length
