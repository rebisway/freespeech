# FreeSpeech protocol

*Version française : [PROTOCOL.fr.md](PROTOCOL.fr.md). Call-by-call reference: [PROTOCOL_API.en.md](PROTOCOL_API.en.md). Architecture, cryptography and local storage: [ARCHITECTURE.en.md](ARCHITECTURE.en.md).*

Design document of the protocol.
The server never knows the content — only the routing metadata that is strictly
necessary (from/to, presence, key epoch).

---

## 1. Identity

```
uid = f(phrase1, phrase2, phrase3)   deterministic, reproducible, anonymous
keypair = derive(uid)                ECDH X25519 (encryption) + Ed25519 (signature)
discovery_id = SHA256(enc_pub_key + sign_pub_key)
```

**Derivation (normative)** — the same three phrases must give the same identity in any implementation:
```
seed         = Argon2id( password = p1 ‖ 0x00 ‖ p2 ‖ 0x00 ‖ p3   (each phrase trimmed, UTF-8),
                         salt = "FreeSpeech-v3-seed", memory 32 MiB, t = 3, p = 1, output 32 B )
enc_seed     = HKDF-SHA256(seed, salt="FreeSpeech-v2", info="encryption")
sign_seed    = HKDF-SHA256(seed, salt="FreeSpeech-v2", info="signing")
db_key       = HKDF-SHA256(seed, salt="FreeSpeech-v2", info="database-v1")   (local database encryption)
```
Each phrase: at least 75 characters, all three different. The salt cannot be per user (the derivation
must work from the phrases alone): it is a fixed public label that stops pre-computed tables shared with
other applications. Argon2id makes enumerating hand-typed phrases expensive; for phrases drawn by the app
(≈ 150 bits in all) it does not change the order of magnitude of the security. Fixed test vectors
(`CryptoCompatibilityTests`) let another implementation check itself. Only identities created by this derivation are found again
by re-entering their phrases; an identity of any other origin stays valid only as long as the app keeps its profile.

- No recovery if the 3 phrases are lost (accepted security choice).
- No server needed to create an identity — 100% local generation.
- Losing the phrases means losing the identity for good; there is no alternative mechanism.

**Multi-device**: nothing prevents recreating the same identity on another client
by typing the 3 phrases again. Consequence: the same identity can be active on
several devices at the same time.
- **No pairing between devices**: since `uid`/`keypair` are derived
  *deterministically* from the 3 phrases, typing the same phrases on a 2nd
  device computes exactly the same `discovery_id` and the same keys — without
  any exchange, confirmation or dedicated channel between devices. The server
  never tells devices apart: it only sees several connections under one
  `discovery_id` (see the registry below). The protocol does not need to know
  the notion of a "device".
- Server registry: `discovery_id → list of connections` (not 1:1).
- Broadcast: every incoming message is relayed to **all** active connections
  of the identity.
- **Conditional live self-sync (§5)**: with the real asymmetric encryption of
  crypto=1, the envelope of a `send` is encrypted for the **recipient's** public
  key — a plain server echo of the same ciphertext to the sender's other
  connections would be unreadable by them (wrong private
  key). The server therefore does not echo: it only indicates, in
  `send_result.self_multi_device`, whether the sender has at least one other
  active connection right now (`RelaySocketHandler.HandleSendAsync`). If so, the
  **client** additionally sends a second envelope, addressed to itself (`to` =
  its own `discovery_id`), encrypted with its own public key, carrying the real
  recipient (`orig_to`) inside the decrypted content — this is what lets A2 see
  live a message sent by A1 (same identity, different device). Cost: one more
  network send, but only when another device is actually connected (not
  systematic). It only covers the live case; the offline case is covered by the
  relay-on-receipt mechanism below.
- Inter-device sync: treat "oneself" as a pseudo-contact — the same Letter types
  (message, identity_update...) addressed to one's own discovery_id propagate
  state (read/unread, added contacts) between one's own devices. No dedicated
  channel.
  - **Systematic trigger on receipt**: as soon as a device receives a message
    (live or via a WAITING/READY rendezvous), it immediately relays that message
    to its own `discovery_id`, as if sending it to a contact. This is what solves
    multi-device sync for the offline rendezvous (see the note below) — there is
    no need to extend the WAITING/READY flag so that it knows every device.
  - **Limit of the WAITING/READY rendezvous in multi-device**: the server flag is
    stored per `(From, To)` pair of `discovery_id`, a single entry — not per
    connection (see §9, same registry as notifications). If B has a message for A
    and A has 2 devices, only the device that answers READY first schedules the
    rendezvous with B; A's other device is not informed directly by this flag. It
    still receives the message through the "oneself" propagation above, as soon
    as the first device has received it and is itself reachable (live or through
    its own WAITING/READY cycle with its own identity).
- OTC (*One-Time Code*) for making contact: a short random string used as a
  shared secret to symmetrically encrypt the ContactCard exchange (see §3).
  Invalidated globally after a single use, whichever device consumed it.

### Mirroring a restored device (another device of the same identity is connected)

A new device A (the three phrases entered, possibly B's backup imported) can be **mirrored** by another device B of the same identity, connected right
now: B is the reference. No pairing: the letters are addressed to one's own identity (like `wipe`), sealed and signed with the identity's keys; the
relay, which never sends a letter back to the connection that emitted it, delivers them only to the **other** devices.

- **`device_sync_request { id, device, have, contacts, ts, code }`** (A → itself): `id` random (128 bits); `code` = **six digits** drawn at random, shown
  **on A only**; `device` = random identifier of this device for this run (the answer is addressed to it, a device never answers itself); `have` = for each
  **non-group** conversation, the digest of what A holds (SHA-256 truncated to 16 hexadecimal characters over `id|state|edit time;` of each message, in order);
  `contacts` = for each contact, the digest of its card. Valid for **5 minutes**. If no other device received it (`send_result.delivered` false), A sends it again
  at every tick during that time.
- **B's agreement: the code is typed on B.** B only shows "synchronization requested" (an internal notice, the phone's notice in the background, a bar on the home
  screen while the request lives); the user touches it and **types the code shown on A** (spaces and dashes allowed). B sends **nothing** before the right code.
  At most three wrong codes: at the third the request is cancelled and A receives a `refused` answer. As the code is only on A's screen, only whoever holds A can
  give it: someone with the three phrases but not B's owner gets nothing, and an unexpected request on B reveals stolen phrases. When one device of the identity
  answers, the others drop the request.
- **`device_sync_data { request, to_device, contacts[], replace[], remove[], history{conv: entries[]}, attachments[], last, refused }`** (B → itself, in
  several parts of about 150 KiB, the last one with `last`): the contact cards that differ; the **conversations whose digest differs**, **whole** (messages
  and states: edited, deleted for everyone, file removed) with their name in `replace`; `remove` = the conversations A holds and B does not. A only takes the answer
  if `request` is its current request and `to_device` its identifier.
- **Mirror**: A **replaces** each conversation of `replace` (erased once per request, the next parts add to it), deletes those of `remove`, and takes B's
  contact cards (B's wins). Messages B deleted for itself no longer exist on A. **Groups** do not go through this: A catches up with their members (§7). **Files** of
  the messages taken over (pictures, documents, received or sent) are real attachments: A gets their records ("offered", `from_my_device`) and downloads them
  **from B** with the `attachment_request` / `attachment_chunk` letters (§6) addressed to its own identity, chunks checked by their Merkle proof. B only serves
  another device of its identity the files named in an answer its owner accepted (by the code): a request naming another is ignored. The app starts the downloads
  according to its automatic download setting, the rest with a tap.
- **Trigger** (app): at the end of the identity import on A (backup imported, Backup screen or restoration at first launch), A asks "Synchronize with another device?";
  on yes the request goes out and the code is shown. Nothing happens if no other device is online (A says so).

### Creating an identity (mobile) — three steps

1. **New connection**: the user only types a name. The app draws the three phrases (above) but **does not create the identity
   yet**; phrases, name and server stay in memory (the "draft").
2. **Saving the phrases** (a screen with no way back, warning "once you leave this window, these phrases can never be shown
   again"). Three ways, which can be combined:
   - **Show**: the three phrases on screen, in a read-only field whose text can be selected; a button copies them (one phrase per
     line, no numbering, so that pasting into the restore screen works as is).
   - **Print**: a sheet printed through the system (Android's print dialog, which also offers "Save as PDF"), with the phrases and a
     **QR code** ("sheet" format below). **Paper only**: the QR code gives the three phrases to whoever reads it.
   - **Protected file** ("key"): the phrases encrypted with a password the user chooses ("key file" format below), handed to the
     system share sheet.
   Going on with nothing done needs an explicit confirmation ("without your phrases, losing or replacing this phone will lose your
   identity for good").
3. **Contact details**: on leaving step 2 the identity is **created** (Argon2id derivation, a few seconds), then the app opens the
   contact-details screen while no contact or group exists. If the app is closed during step 2, the draft is gone and **nothing was
   created**: the next start draws new phrases.

The app's Backup page **only concerns data** (contacts, groups, conversations, attachments, §2): it never offers to show the phrases,
so that a third party holding the phone cannot read them.

### Key file (`.fskey`) and printed sheet

**Key file** — `"FSKEY1" ‖ memoryKiB(4) ‖ iterations(4) ‖ salt(16) ‖ nonce(12) ‖ ChaCha20-Poly1305(key, nonce, AD = everything before
the ciphertext, "phrase1\nphrase2\nphrase3")`, big-endian integers, with `key = Argon2id(UTF-8 password, salt, memory, iterations,
p = 1)` over 32 bytes. Written cost: **64 MiB, 3 passes** (about a second on a phone); it is read from the file, so it can grow
without breaking old files. The password is chosen by the user (**at least 10 characters**, typed twice, can be shown on screen).
Salt and nonce are random for each file (two files of the same phrases differ). The file is written to the app's cache then shared;
it is named `freespeech-key-YYYYMMDD.fskey`. On opening: header checked, **cost bounded** (memory ≤ 256 MiB, iterations ≤ 10) before
any allocation, then authenticated decryption (a wrong password, a modified file or a forged cost — the header is authenticated —
all give the same "wrong password, or the file was changed" error). Code: `Identity/PhraseVault.cs`.

**Printed sheet** — QR code (error correction L) of the text `freespeech-phrases-v1\nphrase1\nphrase2\nphrase3`. The first line is a
label: the app's scanner tells a sheet from a contact card and refuses any other QR code in the restore screen. Code:
`Identity/PhraseVault.cs` (`PhraseSheet`).

### Restoring an identity

"Restore my identity" (first connection) offers, to fill the three phrases:

| Way | Detail |
|---|---|
| Typing | three fields, each phrase at least 75 characters, all three different |
| Paste | three lines (one per phrase) copied from a password manager or a note; refused unless the text is exactly three lines |
| Key file | pick the `.fskey` file, then the password (can be shown); decryption fills the three fields |
| Printed sheet | scan the QR code with the camera; fills the three fields |

Then, **optionally**, a data **backup file** (§2) to get contacts, groups and conversations back too. Before creating anything the app
checks that the phrases really open the chosen backup (a typo or the wrong file would otherwise create a different, empty identity):
on failure, an error message and **nothing is created**. Restoration also puts back the user's name, description and picture (§2).
The same entry screen is used when the phone lost its profile's local key ("phrases to re-enter" state).

### App lock by PIN (optional, local)

Nothing to do with the network protocol: it is a setting of the mobile app (`AppLock`, pure logic in `PinLock`).

- **Off by default**, switched on in the settings ("Code lock"). The PIN is 4 to 8 digits, typed twice.
- **When**: when the app comes back (or starts) after it was **unused** for longer than the chosen delay: right away (15 s grace, so the
  file picker does not lock it), 5 min, 1 h, 1 day or **1 week (default)**. The delay counts from the last time the app left the
  foreground, not from the last network connection. A clock set back counts as "locked".
- **Storage**: never the PIN, but `salt(16) ‖ PBKDF2-SHA256(200,000 iterations)` in the system's secure store (Android Keystore).
  Constant-time comparison.
- **What it guards**: the screen. The database stays encrypted by the vault key (§1, unchanged) and receiving goes on in the background
  (push wake-up, periodic check): a locked phone still gets its messages. While the code screen is up, nothing is marked read.
- **Erase after N failures** (option, **off by default**, 5 or 10): wrong codes are counted; at the N-th in a row the app erases
  everything it keeps on the phone (profile, vault, preferences, cache) and starts over. The identity comes back with the three phrases.
  Switching it on asks for confirmation.
- **Switching off or changing the code** asks for the current code (a thief in front of an unlocked phone cannot turn it off).
- **Forgotten code**: type the three phrases; they must give back the database key kept on the phone. The lock is then switched off
  (to switch on again with a new code).
- **Limits**: an app-level lock, not an encryption layer; an attacker with root or a Keystore extraction goes round it. The counters and
  the delay are preferences: editable with root. The recent-apps preview is not masked unless the **protected screen** setting (Android, off by default, `FLAG_SECURE`) is on.

### Stolen phone: remote wipe (implemented)

- **Revocation key**: Ed25519 derived from the phrases' seed (HKDF, info `revocation-v1`), like the database key. Devices keep only its
  **public** half (`revocation_public_key` of the identity); the private half exists while the phrases are typed. A signature by the
  identity key would prove nothing: the thief has those keys in the phone's database.
- **`wipe` order** (§5): a letter to oneself `{ ts, sig }`, `sig` = the revocation key's signature over
  `"FreeSpeech-wipe-v1|<discovery_id>|<ts>"` (bound to the identity and to the moment). Sent from another device, **with the three
  phrases** (typed, never kept).
- **On receipt** (device of the same identity): ignored if it does not come from oneself, if the identity has no revocation public key
  (nothing to verify: never obeyed on faith), if the signature is wrong, or if the device was installed **after** `ts` (an old order
  cannot erase a phone restored later). The sending device does not erase itself. Otherwise the app erases everything it keeps
  (profile, vault, preferences, cache) and starts over; the identity comes back with the phrases.
- **Best-effort delivery**: the relay stores nothing. The order goes at once to the devices connected, then is **offered again every 2
  minutes for 30 days** by the sending device's periodic check. A stolen device is reached only if it connects while the sender is
  connected too (or at the sender's push wake-up); cut off from the network it is never reached.
- **Enabling**: an identity created before this feature has no public key: on each phone, type the three phrases once (Settings → Stolen
  or lost phone → Enable). An identity created or restored from this version on has it from the start.
- **Limit**: the wipe destroys the stolen phone's copy but does **not revoke** the identity: a thief who already read the keys can use
  them elsewhere. The follow-up (`identity_moved` letter, §12) would move contacts and groups.

### Stolen phone: migration to a new identity `identity_moved` (implemented)

The wipe destroys the stolen copy but revokes nothing: the keys stay valid for whoever read them. The owner (who still has their phrases) therefore
moves to a **new identity** (new phrases) and tells contacts and group moderators; each one decides whether to follow.

- **Pinning the revocation key**: `revocation_public_key` is in the contact card, in `identity_update` and in `member_profile` (fields
  `revocation_public_key` and `revocation_sig` = the member's signature over `revpin|id|key`, **outside** `sig`: versions that do not know them verify the
  profile as before). The first copy seen is **pinned** (`RevocationPinStore`) and never changes; a different key for an id already pinned is refused.
  Never taken from the `GroupState` (the identifier does not cover it). Pins are in the backup. `AnnounceRevocationKeyAsync` announces it to the groups,
  once per group.
- **Sender side** (device holding the old identity; Settings → "Change identity", `MigratePage`):
  1. **hand-over of roles**: creator of a group → a chosen successor (moderators first) gets the role, then I step down as moderator; mere moderator →
     I step down (`RolesToHandOver`, `HandOverRolesAsync`); a creator with no successor blocks the step;
  2. the old phrases are typed and **checked** before anything changes;
  3. new phrases are drawn, the usual save screen (§1);
  4. on leaving that screen, `SendMovedAsync` sends `identity_moved { old_id, new_card, ts, sig }` — `sig` = the old identity's revocation key over
     `"FreeSpeech-moved-v1|<old_id>|<new_id>|<ts>"` — **from the old identifier**, to every contact and to the moderators of every group (queued when the
     person is offline);
  5. "Switch to the new identity": the new phrases typed must give the announced identifier (proof they were noted right). `SwitchToNewIdentity` puts the
     new identity **in the same profile**: contacts, history, servers and profile are kept, groups are archived (the new identifier is not a member),
     undelivered messages are dropped, the database is re-keyed with the new phrases' key, then the app restarts. Notices still waiting will not go out
     any more (signed by the old identity): the wizard says so and asks for confirmation.
- **Receiver side** (`HandleMoved`): the notice is ignored with no pinned key for the sender, with a wrong signature, if `From` ≠ `old_id`, if the new card is
  inconsistent (`discovery_id` ≠ SHA-256 of the keys), or if that identifier is **banned** from a group I moderate. One notice per old identifier (a
  second, different one is flagged as a conflict). A **moderator** excludes the old identifier with a **key rotation** in one state (`AutoExcludeMigrated`
  setting, on by default; the creator cannot be, hence the hand-over). The notice lands in "Requests": **Accept** creates the new contact (private name kept,
  ✔ mark not carried over), marks the old one (`moved_to`: nothing is sent to it any more, error `contact_moved`) and invites the new identifier into the
  groups I excluded it from; **Decline** changes nothing else. `AutoAcceptMigration` (off by default) follows a contact's notice without asking, **never**
  for a contact marked ✔.
- **Tests**: `MigrationTests` (12, real server: contact, forged notice signed by the identity key alone, no pin, immutable pin, ✔ vs automatic, moderator
  and plain member, creator who hands over, creator who does not, banned, announcement to groups, full switch with re-invitation) and a run on three
  emulators (two groups, creator and plain member).
- **Limits**: a contact from before the feature has no pin until it receives an `identity_update` (sent when the protection is enabled in Settings → Stolen or
  lost phone); a moderator who is **not a contact** of the person cannot re-invite them (the invitee only accepts contacts, §7); if the **phrases** leaked
  nothing is protected; best-effort delivery (the relay stores nothing); the thief can act before the wizard (same identity keys); a ban does not follow a
  successor who arrives another way.

### Precautions and possible weaknesses (identity)

- **The phrases are the identity**: whoever holds them *is* the user, on as many devices as they like (§1, multi-device), and
  **nothing can detect or revoke it**. Keep them on paper, off the phone, off the cloud, never in a message.
- **No recovery**: phrases lost and no copy = identity lost (accepted). Hence the mandatory save screen at creation and the
  confirmation before leaving it with nothing done.
- **Nothing written on the phone**: no phrases, no QR code, no PDF is created by the app; only the (encrypted) key file and the draft
  in memory are. Limit: the draft sits in managed memory (copies of strings can linger until garbage collection); accepted.
- **Display**: the save screen is protected against screenshots and screen recording only if the **protected screen** setting
  (Android, off by default, `FLAG_SECURE`) is on, and someone behind the shoulder can read it.
- **Clipboard**: the Copy button puts the phrases on the clipboard, readable by keyboards and clipboard apps; paste them at once into
  a password manager, then clear it.
- **Checking a key file** (Settings → "Check my key file"): the file is opened with its password, the identity is derived from it and
  compared with the device's (✔ same identity / ✘ another / wrong password or altered file). Nothing is restored or saved; the phrases only
  live in memory for the length of the calculation (a few seconds: Argon2id twice).
- **Printing**: the spooler or a cloud print service may keep a copy, and "Save as PDF" leaves a plain file; prefer a local printer.
  Destroy or lock away the sheet: the QR code is read without a password.
- **Key file**: the file **and** the password are needed, to be kept **apart** (the password cannot be recovered). Protection is that of
  the password: Argon2id (64 MiB) slows each guess but does not save a short or common password; advise a long passphrase (the
  10-character minimum is a floor, not an entropy measure). The file stays in the app's cache (encrypted) until the system purges it.
- **Hostile file**: a forged key or backup file cannot make the app allocate more than 256 MiB; any tampering is detected by the AEAD.
- **Hand-typed phrases**: their strength is the user's; Argon2id slows enumeration but does not replace 75 truly random characters
  (hence the drawing by the app).
- **Android backup**: `allowBackup=false`, so Keystore secrets do not leave in the system backup to another phone.

## 2. Export / Import (backup)

- Encrypted backup: confirmed contacts + aliases + local history + groups
  (states, keys, log).
  **Not the identity itself**: it is fully re-derivable from the 3 phrases on any
  device, so no private key has to travel through this file. **Nor the phrases**: the backup only concerns data.
- **Read marks** (`read_marks`: conversation, side, `ts`, `id`): included, mine and the peer's. On restoration they only move forward; a backup that carries none marks each restored conversation as read up to its last received message.
- **The identity's servers** (`servers`: addresses in priority order): included. On restoration, when the identity still has the servers the application gave it at creation (§4 d) the client tests those of the backup; if at least one answers, the whole list replaces the initial servers, otherwise nothing changes. An identity whose user chose the server, or whose list was edited, is not touched.
- **The user's profile** (`profile`: name, description, picture as base64): included, because keys are re-derived but the profile is
  not. On restoration at **first connection** (identity just recreated, empty profile) it is put back; on an identity already in use
  (Backup page) it is **not** applied, so an old backup does not overwrite a newer profile. An empty field in the file never replaces
  a filled one. Contacts and groups already know the profile: nothing is re-sent. A backup made before the field existed restores
  without a profile.
- Encryption key = HKDF-SHA256(the identity's encryption private key,
  salt="FreeSpeech-Backup", info="backup-key") → ChaCha20-Poly1305,
  deterministic. Accepted consequence: a backup is only readable by re-deriving
  the same identity (same 3 phrases) — intended use: transfer between devices of
  the same identity, not sharing with a third party.
- Import = merge, never a replacement: contacts are upserted (the imported backup
  wins on conflict), aliases are merged, history is merged by `message_id` (the
  existing de-duplication mechanism of §5 makes a repeated import harmless).
- **Attachments (§6), three modes**: `none` (no file), `Nm` (files of the last N
  months, **default 6m**), `all`. History and metadata are always included. The
  filter applies to the date of the **message** (ts of the `ref`), not to the
  file's date. On restore: file present → restored (Merkle root re-verified;
  stored under `blobs/{root}` — a file already present under that root is not
  rewritten — so it can be served again if it was sent); file left out and
  **received** → the offer becomes "offered" again, re-downloadable with
  `download` if the sender still has it; file left out and **sent** → lost (the
  contact will no longer be able to download it from us) — the command warns
  about it explicitly.
- **Format**: `"FSBK"`‖version‖blocks. Content = zip (`manifest.json` +
  `history/N` + `files/N`); the history is written in blocks of 1,000 lines of one
  conversation (`history/N` = `{ conversation, entries }`), read from the database as the
  file is written, so a long history is never loaded whole; restoring reads them the same
  way. All of it is encrypted in 1 MiB blocks, ChaCha20-Poly1305, AD = block index ‖
  "last" flag (reordering, block removal or truncation are detected; bounded
  memory). A single base64 JSON blob also remains restorable.
- Implemented: `backup [path] [none|6m|all]` / `restore <path>` in the console,
  logic in `Backup/BackupService.cs` (no console I/O, reusable), primitives in
  [BackupCrypto.cs](freespeech.client/Backup/BackupCrypto.cs).

### Archive of attachment files (free space)

Distinct from the backup: it moves **downloaded files** off the device and leaves everything else where it is.

- **What stays.** Every message, text included. An archived attachment keeps its record (state `Archived`, no local path) and, for a
  picture or a video, its thumbnail (table `thumbnails`, ARCHITECTURE §5.1 quinquies). The chat shows the thumbnail, or the name and size
  for any other file, with a small "archived" icon; a tap says where to find it.
- **Selection.** Every criterion that is set must hold, none set = everything: messages older than N months (the usual choice, **12 by
  default**), a stretch of time (from / to), one conversation, one kind (photos, videos, audio, other), a minimum size. The date is the
  message's (ts of the `ref`). A content held by several records (the same file in two conversations) is archived only if **all** of them
  match: the space is freed or the file is left alone. A preview gives the number of files and the space freed before anything is done.
- **Format.** The format of a backup (`"FSBK"`, 1 MiB blocks, ChaCha20-Poly1305, key derived from the identity as in this section), a zip
  inside holding `archive.json` (`{ created_at, items: [ { root, size, file, records[] } ] }`: one item per content, with the records that
  pointed at it, without their local path) and one `files/N` per content. `archive.json` replaces `manifest.json`, so a backup cannot be
  taken for an archive nor the other way round. It is readable only with the same identity.
- **Writing.** Zipping and encrypting run side by side through a pipe: no clear copy and no temporary file as big as the archive. The
  destination is any seekable stream the person chose (on Android the system document picker, so a memory card, a USB key or a cloud
  drive that offers its files to apps): a phone short of space needs no room for the archive. **Before a single file is removed** the whole
  archive is read back, every block authenticated; on any failure the archive is deleted and nothing has moved. Then each record becomes
  `Archived`, its file, its received chunks and its blob (once no other record needs it) are deleted.
- **Loading.** The archive is decrypted (to a temporary zip, removed afterwards), each content is written once under `blobs/{root}`
  unless it is already there, its Merkle root is checked against `root` (a damaged or forged entry is skipped), and every record that
  points at it becomes `Complete` again; a record the device does not have (an archive made on another device of the identity) is added.
  Loading twice, or loading an archive whose files are already here, duplicates nothing.
- **When the lock is on** (PIN), creating an archive asks for the PIN again, like a backup.
- Implemented: `Archive/ArchiveService.cs` (`Preview`, `Create`, `Restore`, no I/O of its own beyond the streams it is given), screen
  "Archive files" on the mobile app (Backup / Restore page), `FreeSpeechClient.Archive`.

## 3. Contact card (ContactCard)

```json
{
  "nickname": "...",
  "photo": "base64 JPEG, <100KB",       // optional
  "description": "free text, ≤ 300 chars.",   // optional
  "discovery_id": "...",
  "encryption_public_key": "...",
  "signing_public_key": "...",
  "servers": [                          // list ORDERED by preference
    { "url": "https://public.freespeech.org", "priority": 0 },
    { "url": "https://circle.invalid", "priority": 1, "restricted": true, "circle": "base64" }
  ],
  "enable_push": false,                 // advertised capability, not the token
  "created_at": ...,
  "version": "2.0"
}
```

- Two exchange modes, through an OTC (*One-Time Code*, a
  16-character alphanumeric string) embedded in the first card and used as a
  symmetric key (HKDF-SHA256 → ChaCha20-Poly1305) to encrypt the reply card:
  - **qr** (auto-validated) — QR/Bluetooth in person, OTC valid **10 min**,
    import without confirmation.
  - **manual** — remote sharing (copy/paste, email/cloud), OTC valid **1 month**,
    manual validation mandatory in every case.
- `servers`: the identity's list of servers (see §8 multi-server routing).
- **A name given to a contact (two "Thomas").** Besides the `nickname` the contact announces, the address book keeps
  a `local_name` that **only I** see (never sent): it wins for display (`DisplayName`) and in name lookup. An
  `identity_update` that changes the contact's nickname does not erase it. `local_name` and `verified_at` are part of
  the backup (§2) and restored with the contacts. Empty = back to the contact's own nickname.
- **Safety number (verification against interception).** An interceptor present while the cards are
  exchanged can slip in his own keys; afterwards everything looks normal. The remedy is to compare, out
  of band, a number only two legitimate parties share: `SHA-512` iterated 5,200 times over
  `"FreeSpeech-safety-v1" ‖ A ‖ B`, where `A` and `B` are the two public key pairs (`enc ‖ sign`, 64 bytes)
  **sorted bytewise**, so both sides compute the same value; 12 groups of 5 digits (the group's 5 bytes
  modulo 100,000), shown on four lines. If someone substituted keys, each side holds a different key for
  the other and the two numbers differ. The compared contact is marked `verified_at` in the address book.
  An `identity_update` whose keys change is still rejected (§5).

## 4. First connection

- a) Import of a contact card → learning its server(s) → registering on them if relevant.
- b) Manual addition of a known server (notably any **private** server, which
  cannot be discovered in any other way).
- c) No known server → `localhost` by default (dev placeholder; the production
  value remains to be defined at deployment, outside the protocol).
- d) Looking for a relay (a new identity, or none of the identity's own can be reached). A **new identity** on an application that knows directories asks them (§10 "Directory"):
  the first relays they offer become its servers; when none answers, one of the application's built-in fallback relays (`FsFallbackServers`), drawn at random, takes their place. The application's default server is kept only when it has neither directory nor fallback.
  These initial servers can still change once: a backup of the identity (§2) that lists servers replaces them when one of them answers, and so does the very first contact when it is on other servers and one of them answers.
  Neither happens once the user has chosen a server or edited the list (an application started with a server of the user's own choice gives neither directories nor fallback). When none of an identity's own relays can be
  reached, the client library offers the relays of its contacts first, in random order, and the directories only when every contact is on a relay already tried (or there is no contact).
  A client that has a relay never contacts a directory.
- The client works without any configured server: the identity exists
  independently of any connectivity.

## 5. 1-to-1 communications

### Message identifier (unified uid + causality)
```
message_id = sender_discovery_id ":" ts        (text)
ts         = UTC ticks (100 ns), strictly increasing on the installation; the 12 low bits are a
             random tag drawn once per run
```
- No shared global counter → no collision between different senders.
- **Several devices of one identity**: two devices sending at the same instant would need the same
  0.4 ms slot **and** the same tag (1 chance in 4096) to produce the same `message_id` — negligible,
  and the format does not change. A client advances its `ts` by 4096 if it sends faster than one message
  per 0.4 ms (the date stays readable).
- Serves both as unique id and as causal order (no server clock required). `ts` is a **tie-break**,
  not proof of age: a far-future `ts` is possible, which is why concurrent operations (profiles, bans,
  §7) do not rely on it alone for security.
- Timestamp: time server if internet access, otherwise local clock. The server exposes its UTC clock (`TimeController`,
  `GET /api/time`) to set `NextConnectAt` (§9) without depending on the device's local clock,
  which only serves as the causal order above.

### Encryption (`MessageCrypto.cs`)
```
Envelope { from, to, crypto, to_server?, msg: base64 }
  crypto = 0 : OTC symmetric — key = HKDF-SHA256(otc) → ChaCha20-Poly1305
               (ContactCard exchange, §3)
  crypto = 1 : asymmetric — ephemeral X25519 ECDH (new pair per message)
               → HKDF-SHA256(shared secret) → ChaCha20-Poly1305
               (random 12-byte nonce, Poly1305 tag = integrity)
               output = ephemeral_pub(32B) ‖ nonce(12B) ‖ ciphertext
Letter (decrypted content, crypto=1) { type, payload, sig, orig_to? }
  type    : the Letter type (list below)
  payload : a string: the text itself for "message", otherwise a JSON string (list below)
  sig     = Ed25519("freespeech-letter-v1|" + from + "|" + to + "|" + type + "|" + payload + "|" + ts),
            verified with the contact's signing_public_key stored locally (or ours for a
            self-sync copy of a message, see the note above). from/to = the envelope's
            discovery ids; for a self-sync copy (envelope addressed to oneself) `to` =
            `orig_to`, the real recipient.
  orig_to : present only on a self-sync copy of a "message" — real
            recipient, the envelope itself being addressed to oneself
```
- **Why `from` and `to` are signed** (change of 2026-10-05): without them a recipient B could decrypt A's
  letter, re-encrypt it for a third party C and show it as coming from A (the signature stayed valid). Bound
  to `to`, it is only valid for its real recipient. `orig_to` only counts on an envelope addressed to its
  own sender; elsewhere the envelope's own `to` is the one verified. Domain tag `freespeech-letter-v1`: a
  letter signature cannot be mistaken for a signature of another kind (groups, profiles, etc.).
- Signature **before** encryption (sign-then-encrypt): it travels inside the
  encrypted content rather than in a separate envelope field — the envelope stays
  `{ from, to, crypto, msg, ts, reply_to }`, unchanged. The payload of each type is given in the list below and in [PROTOCOL_API.en.md](PROTOCOL_API.en.md).
- **Sending to an unconfirmed contact is impossible**: without its
  `encryption_public_key` (obtained through the OTC exchange, §3), the client
  refuses to send (no clear-text fallback) — explicit error message inviting the
  user to do the contact exchange first.

### Letter types
```
message         text — the payload is the text itself; reply_to is a field of the envelope
read_ack         { target } — read receipt: "I have read up to this message (included)" (1-to-1, §5)
typing           { group? } — "typing…" indicator (see below); throw-away letter
wipe             { ts, sig } — remote erase order, to oneself only (see §1); sig = Ed25519 by the revocation key over
                 "FreeSpeech-wipe-v1|<discovery_id>|<ts>" (ts = Unix ms)
identity_moved   { old_id, new_card, ts, sig } — migration to a new identity (see §1); sig = Ed25519 by the OLD identity's revocation key over
                 "FreeSpeech-moved-v1|<old_id>|<new_id>|<ts>"; sent from the old identifier to contacts and group moderators; accepted from a
                 contact or a member of a shared group; checked against the pinned key
identity_update { updated ContactCard — crypto keys immutable }
route_policy     { open_fallback } — allows (true) or not an open server for my messages with this contact when our shared restricted servers do not answer (§ 8)
attachment       { name, mime, size, root, thumbnail? } — thumbnail sent directly (mobile)
attachment_request { ref, missing? } — ref = message_id of the attachment Letter
attachment_chunk { ref, chunk_id, total_chunks, data, proof[] } — proof = Merkle proof
group_state      { state, signer, sig } — composition + epoch key (§7)
group_sync       { group_id, epoch, have{sender: seq} } — p2p catch-up (§7)
group_relay      { states[], keys[], messages[], more } — answer to the catch-up (§7)
group_leave      { group_id } — a member asks the moderators to remove them (§7)
blacklist_update { group_id, ops[] } — signed blacklist operations (§7)
group_pin        { group_id, ops[] } — pinned or released messages, signed by a moderator (§7)
group_profile    { group_id, name, photo?, description?, ts, signer, sig } — group name/picture, by the creator (§7)
member_profile   { group_id, id, nickname, photo?, description?, ts, sig } — a member's name/picture, signed by them (§7)
attachment_have  { ref, have, complete, busy } — what a member holds of a group file (§7)
device_sync_request { id, device, have, contacts, ts, code } — a restored device asks another of the same identity to mirror it (§1)
device_sync_data { request, to_device, contacts[], replace[], remove[], history, attachments[], last, refused } — the answer, in parts (§1)
message_edit     { target, text } — the author replaces the text of one of their messages (§5)
message_retract  { target, seq? } — the author deletes one of their messages for everyone (§5)
attachment_retract { target, seq? } — the author removes the file of one of their messages for everyone (§5)
group_suggest    { group_id, candidate, ts, sig } — a member proposes one of their contacts to the moderators (§7)
message_react    { target, text } — emoji reaction to a message or a file; empty text = reaction taken back (§5)
ack              { ids } — confirms that Letters were kept by the recipient (§5, "Confirmed delivery")
```

### Text formatting
The text of a `message` travels as typed. It is **formatted at display time only when its first line is exactly `md`** (any case, trailing spaces allowed) and another
line follows it; the `md` line is not shown, and any other message is shown as it is. A client that does not know this convention shows the raw text, which stays
readable. The subset of Markdown that is read (at most 8,000 characters):
- titles `#`, `##`, `###` followed by a space (beyond three hashes: the smallest);
- lists: `-`, `*` or `+` followed by a space, or a number followed by `.` or `)` and a space; shown **flush left** with a bullet `•` or the number (the typed
  indentation of a nested item is kept), without a hanging indent;
- quote `>` (a `▎` bar in front of the line), a `---` line (rule); a little air (a short empty line) separates a text line from a list line or a title, and a list from a title;
- inline: `**bold**`, `*italic*` or `_italic_`, `~~strike~~`, `code` between backticks (nothing is read inside), `[text](https://…)` and bare `http(s)://…` addresses; a single star or
  underscore never splits a word (`snake_case`, `2*3*4`); a backslash shows the next sign as it is;
- no code block, table, image or HTML; only `http` and `https` links are recognised.
Previews (list of conversations, notification, quoted reply) show the text without the signs. A link opens only after a confirmation that shows the real address.

**Compatibility between versions (rule)**: a client that receives a Letter type it does not know **ignores it in silence** (nothing in the
history, no notification, no alert); the signature is checked first, so a forged unknown type has no effect. This lets types be added
without disturbing the versions already installed (beta test). Consequence: a feature that relies on a new type (for example `typing`)
only works between clients that know it. The server relays Letters without reading their type.

### Read / unread and read receipts (1-to-1)
- Each device keeps, per conversation, a **read mark**: the position (origin `ts`, then `message_id`) up to which I have
  read what the other side sent. The **unread count** = the messages received after that mark; it drops to 0 when the
  conversation is shown (app in the foreground).
- When the conversation opens (and on each message received while it is open), the client sends **one**
  `read_ack { target }` to the contact, `target` = the last message received. One receipt covers every earlier
  message. **Best effort**: not queued, not in the history; if it is lost, the next one catches up.
- The receiver honours the receipt only if `target` is **a message it wrote**; its "read by the other side" mark only
  moves forward. The UI shows ✓ (sent) then gold ✓✓ (read).
- The user can **turn sending off** (setting "Read receipts"). No receipts in a group (privacy, O(N) cost): only the
  unread counter exists there. The mark is not synchronised between a user's own devices (deferred).

### "Typing…" indicator (`typing`)

A **throw-away** letter, encrypted and signed like the others (§5) but: **never** stored in the history, **never** put in the outbox,
**never** a WAITING flag — so it **cannot wake** a phone, and if the recipient is not connected it is lost (intended). Content: `{}`
in 1-to-1; `{ "group": groupId }` in a group.
- **Sender**: while the user types (non-empty field, app in the foreground), **at most one signal every 4 s** per conversation. In a
  group, one letter per member: only for groups of **8 members at most** (beyond that, nothing).
- **Receiver**: shows "typing…" (in a group: "Marie is typing…") under the name in the **open** conversation, clears it **6 s** after the last
  signal or when the message arrives. A signal only counts from a **confirmed contact** or, with `group`, from a **member** of that group
  (the letter's signature names the author).
- **Reciprocal**: the "Typing indicator" setting (on by default) turns off sending **and** showing; when off, a received signal is ignored.
- **Privacy**: the indicator says *when* the user is writing; the server sees one more letter go by, with no content (same metadata as
  other letters, §13). Code: `FreeSpeechClient.Typing.cs`.

### Reactions (emoji)
- `message_react { target, text }`: `text` is **a single emoji** (4 text elements at most: flags, skin tones,
  families); empty = take the reaction back. A text containing a letter or a digit is refused: a word is not a
  reaction.
- Unlike an edit, **any participant** may react to any message or file. In a group it is a group message (§7) of the
  same type, in 1-to-1 an encrypted, signed Letter (§5).
- A person has at most **one reaction per message**: the most recent (the Letter's `ts`) wins, an older one arriving
  late is ignored. It may arrive before the message it targets: it is kept as it is. Reacting again with the same
  emoji takes it back.
- Same "best effort" guarantee as the other changes; reactions are not synchronised between a user's devices
  (postponed).

### identity_update rule
```
if updated.encryption_public_key != existing.encryption_public_key
   OR updated.signing_public_key  != existing.signing_public_key
then REJECT (key change = potential impersonation)
else: nickname / photo / servers may change freely
```
**Wired in the client library** ([FreeSpeechClient.cs](freespeech.client/FreeSpeechClient.cs)
`HandleIncomingIdentityUpdate`, [FreeSpeechClient.Account.cs](freespeech.client/FreeSpeechClient.Account.cs)
`SendIdentityUpdateAsync`): any change of the server list (`server add|remove`,
§8) or of the profile (`profile name|about|photo`) sends an `identity_update` to
every confirmed contact — each send goes through the normal path (live, or queued in the outbox like a chat message; the most recent card replaces the one still waiting).

### Confirmed delivery (acknowledgement)

The relay calls a Letter "delivered" as soon as it wrote it to the recipient's connection, without an acknowledgement: if that connection is already dead (network lost, process
killed), the Letter is lost and nobody knows. The recipient therefore confirms what it keeps:
- **`ack { ids }`**: a Letter signed and encrypted like the others, sent by the recipient to the sender; `ids` = the `message_id`s (`{sender}:{ts}`) of the Letters kept, gathered over
  300 ms. It is throw-away (no outbox, no history) and is not itself confirmed.
- **Confirmed types**: `message`, `attachment`, `message_edit`, `message_retract`, `attachment_retract`, `message_react`, `identity_update`, `route_policy` — all those that go through the outbox,
  from a confirmed contact. A message, an edit and a deletion thus follow the same path.
- **Sender**: a delivered Letter stays in the outbox, marked "delivered, not confirmed", until the `ack`. Without an `ack` it is sent again as it was (same `ts`, so same identifier)
  after 30 s, 2 min, 10 min, 1 h and 6 h; after the last wait it is dropped (a version that does not confirm is not sent the Letter for ever). An unreachable contact puts it back to
  waiting like any outbox Letter (WAITING flag). A message deleted before its confirmation is not sent again: only the deletion goes.
- **Recipient**: a Letter already seen is not handled twice (deduplicated on `{sender}|{recipient}|{ts}|{crypto}`) but it is **confirmed again**, so that a lost confirmation does not
  make the Letter be sent for ever.
- Groups have no `ack`: a member that missed a message catches up from the others (§7).
### Editing and deleting a message (contact and group)

| Action | Scope | Mechanism |
|---|---|---|
| Edit | everyone | Letter `message_edit { target, text }` |
| Delete for me | this device | local, nothing sent |
| Delete for everyone | everyone | Letter `message_retract { target, seq? }` |
| Remove the file for me / for everyone | this device / everyone | local / Letter `attachment_retract { target, seq? }` |

In a group these Letters are group messages (§7) of the same type; in 1-to-1 they are ordinary Letters
(encrypted, signed, §5).


- `target` is the targeted `message_id` (`{author}:{ts}`). **Every receiver honours a change only if the Letter's
  sender is the author of the targeted message** (prefix of the `message_id`): nobody can edit or delete someone
  else's message — not even a group moderator (deferred, §12). The signature of the Letter (§5) or of the group
  message (§7) is the proof.
- **Edit**: the Letter's `ts` is the version; the most recent wins, an older edit arriving late is ignored. The
  message id does not change: the `reply_to`s that target it stay valid. The message is shown as "edited". Only a
  text message can be edited.
- **Delete for everyone**: the content is **erased**; the line stays ("message deleted") so replies to it stay
  attached. A deletion beats any edit, whatever the order of arrival. For a file message, the local file is deleted
  too; **removing the file alone** leaves the line ("file removed").
- **Order is not guaranteed**: a change can arrive before the message (catch-up, several servers). It is then parked
  (table `message_ops`) and applied the moment the message is stored; a message already deleted when it arrives is
  not announced.
- **Offline (1-to-1)**: the Letter goes through the outbox like a message. A message **not yet delivered** is simply
  edited, or removed, in the outbox: nothing is sent. Changes are not synchronized between devices of the same
  identity (deferred, §12).
- **Delete for me**: the content is erased and the line hidden. The row stays in the database, hidden, so that a
  new delivery (group catch-up) does not bring it back; it is excluded from the backup. A file message loses its
  file, unless it is shared (same Merkle root, §6) with another conversation.
- **Best-effort guarantee**: a modified client can ignore an edit or a deletion, and the recipient may have copied,
  forwarded or backed up the message beforehand. No time limit is imposed.

## 6. Attachments (on demand, no automatic push)

```
A sends: attachment (light thumbnail, like a normal message)
B decides: wants the full file?
  no  → nothing more, B could sort without downloading
  yes → B sends attachment_request { ref }
        a holder answers with attachment_chunk[] (chunked, each chunk encrypted
        individually, AEAD ChaCha20-Poly1305, and accompanied by its Merkle proof)
        B verifies EVERY chunk on receipt against `root`, then reassembles
```

### Content identity (Merkle root)

- Fixed 48 KiB chunks; leaf = SHA-256(0x00 ‖ chunk), node = SHA-256(0x01 ‖
  left ‖ right); a node without a sibling (odd count) is **promoted as is**,
  never duplicated. The number of chunks is **deduced from `size`**, it is not
  transmitted.
- `root` (in the offer) = SHA-256(0x02 ‖ size on 8 bytes ‖ tree root): the size
  is bound to the identifier, so the same tree cannot be "verified" with another
  number of chunks, and two equal `root`s imply equal sizes (safe de-duplication).
- Each chunk carries its proof (sibling hashes, leaf → root, about 10 for 25 MiB and 16 for 2 GiB): any
  holder can serve, a corrupted chunk is rejected immediately (with attribution
  of the faulty peer) instead of failing at the end.
- **One content = one local file**: stored under `blobs/{root}.ext` whatever the
  conversation. Forwarding a file to another conversation (`forward`) is a new
  offer with the same `root`: nothing is copied or re-hashed; a recipient who
  already holds that content has nothing to download. The tree (leaves) is kept
  alongside to serve without re-hashing.

- Resume possible on an unstable connection (explicit chunk_id).
- If A is offline when the request is made → same WAITING/READY negotiation as
  for a text message, triggered specifically for this request.
- Groups (p2p): only the flow between requester and file holder is chunked — no
  automatic broadcast to the whole mesh.
- **Network setting** ("files only on Wi-Fi"), per device and outside the protocol: on mobile data, according to the user's choice (always / Wi-Fi only /
  Wi-Fi except files of 1 MiB or less), neither the chunks asked for nor the chunks served leave the device; messages, receipts, group states and
  `attachment_have` always go through. A held-back download stays `Receiving` with the `waiting_network` flag ("waiting for Wi-Fi"), restarts by itself
  when Wi-Fi returns, or at once with "download anyway" (that file only). A holder on mobile data answers `busy` to the members of a group (silence in
  1-to-1). The app tells the library the kind of network; a console client ignores it (no restriction).
- **Sizes.** The protocol carries a file of up to 2 GiB; a larger offer is ignored. Below that each side
  decides, outside the protocol. Before sending, the app asks for a confirmation for any file larger than
  a size the user sets (25 MiB by default, 0 = never asked), counted as the file will leave, after pictures
  and videos have been reduced; it shows the size and, when the servers allow less than that today, what is
  left of the daily quota (§11 "Volume limits"). A received file shows its size until the user asks for it;
  only a file under the automatic download size (a setting) comes by itself. A download starts only if the
  device can hold the missing chunks and, beside them, the assembled file (`not_enough_space`, with the
  space needed and the space free). When a relay refuses to carry more for now, sending to it pauses until
  its `retry_after`; the letters wait in the outbox, and the receiver asks again for the missing chunks.

### Client implementation (client library, `Attachments/`)

- `attachment`, `attachment_request`, `attachment_chunk` Letters: JSON payload in
  the usual Letter (encrypted + signed). The file's `uid` is the `message_id` of
  the `attachment` Letter (`from:ts`), not a repeated field.
  `attachment` = `{name, mime, size, root, thumbnail?}`,
  `attachment_request` = `{ref, missing?}` (`missing` absent = everything),
  `attachment_chunk` = `{ref, chunk_id, total_chunks, data(base64), proof[]}`.
- Raw 48 KiB chunks, one envelope per chunk (strictly increasing ts: it is part
  of the message_id, of the signature and of the pool's de-duplication key).
  2 GiB ceiling (`AttachmentService.MaxSize`). Receiver-side validation: size within that ceiling, number of chunks deduced from `size`,
  exact size of each chunk, known refs, valid Merkle proof; file name sanitized
  (no path). Only a proof failure counts as a "strike" (3 = download abandoned,
  retryable); a merely malformed chunk is ignored. The sender no longer has to
  be the original contact: the proof, not the sender, is authoritative.
- A holder refuses to serve a chunk whose own copy no longer matches the tree
  (damaged local file). The "who may ask" policy is injected (`authorize`): in
  1-to-1, only the recipient of a sent file; §7 plugs in "the requester is a
  group member".
- The server stores nothing: the sender keeps the file (`blobs/`) and must be
  online (or another holder, in a group). Resume = presence of
  `parts/{ref}/{n}.bin`; if there is no progress for 20 s (30 s tick), the
  receiver re-requests only the missing chunks. A request with no reachable
  sender → WAITING flag. `attachment_request` and chunks do **not** go through
  the outbox (the receiver's resume timer acts as the retry); the `attachment`
  offer does.
- No self-sync of attachments (the other device does not have the file); the
  backup covers them according to the mode (§2); no thumbnail in the console
  (reserved for mobile, no image library).
- Commands: `upload <contact> <path>`, `download <contact> <#>` (index from
  `history`), `forward <source> <#> <recipient>` (text: re-sent as is; file: new
  offer with the same `root`, refused if the file is not already downloaded),
  `files`. Tests: `sources/freespeech.tests` (unit) and `scripts\test-attachments.ps1`
  (end-to-end, including `forward`).

## 7. Groups

### Model
- No administrator for **routing** messages (any member can relay to any other).
- A moderator exists for the group's **composition**: the creator by default,
  who can appoint others.
- `group_id` = 16 random bytes (hex), chosen by the creator.
- Members are **not necessarily contacts of each other**: the group state embeds,
  for each member, its public keys and its servers (self-certifying:
  `discovery_id` = SHA-256(enc_pub ‖ sign_pub), so a moderator cannot substitute
  the keys of an existing id).
- **No history in the GroupState**: it only describes the current state. Past
  states/epochs and old keys are kept only **locally** by each client (to read
  history and validate messages of an old epoch). Same for the message log used
  for catch-up.
- Target size: several hundred members (O(N) sending per message, §12).

### GroupState — the signed unit (card + epoch key, a single message)
```
state = JSON serialized ONCE; the signature covers these exact bytes
        (no canonicalization to re-implement). The Letter's `state` field is a STRING: an
        opaque blob. A receiver never rebuilds or re-serialises it (it relays and verifies
        it as is); two equivalent JSON texts serialised differently would have different
        signatures.
{ group_id, epoch, prev_hash, created_at,
  name, photo?, created_by, moderators[],
  members[ { discovery_id, nickname, encryption_public_key,
             signing_public_key, servers[] } ],
  key_check }                    ← HKDF-SHA256(group_key, info="grp-check"), 32 B
Letter "group_state" { state: "<json>", signer, sig: Ed25519(signer, state),
                       key_envelope }   ← group_key encrypted for THIS recipient only,
                                          outside the signature (verified by key_check)
```
- `group_key` = 32 random bytes **per epoch**. `prev_hash` = SHA-256 of the
  previous `state` (null at epoch 1). `key_envelope` in crypto=1 format
  (ephemeral X25519, HKDF, ChaCha20-Poly1305): each recipient only receives its
  own, never those of the others.
- **Rotation and card update are atomic**: the key is in the same message as the
  composition. An excluded or departed member does not appear in `members` and
  receives **no key**; it does receive the signed state **without a
  `key_envelope`** to learn that it is out (it stops sending), without being able
  to decrypt anything that follows.
- **No envelope is kept or re-broadcast.** A member absent during the rotation
  fetches the signed state from any peer, which **re-encrypts** the key for it
  itself (it knows it, and the latecomer's public key is in `members`). The
  latecomer verifies the received key with `key_check` (signed by the moderator):
  a peer therefore cannot slip it a wrong key. A peer only hands an epoch key to
  a member of that epoch (states kept locally). Cost of the rotation: O(N), one
  state + one envelope per member.
- The moderator does not have to stay online: delivery to absentees is entirely p2p.

### Adoption rules for a received state S (by any path)
1. **Creation** (epoch 1): accepted only as an invitation from a confirmed
   contact, signed by `created_by`, listing the recipient. Otherwise ignored. **The invitee decides**: the
   invitation (signed state + key envelope, kept as received) waits in an invitation list; nothing is joined, no key
   is adopted, the inviter learns nothing before the answer. **Accepting** adopts the state as if it had just arrived
   (then p2p catch-up); **declining** deletes the invitation and keeps a mark so the same group does not ask again.
   (Library setting `RequireInvitationApproval`, on for clients; off, it joins at once, for tests.)
   **Re-invitation**: a state that adds back someone who had left the group (or been excluded) waits for their answer
   in the same way; accepting makes it live again (the group archived locally is back, with its history).
   **Proposing a contact (sponsoring)**: a member who is not a moderator sends every moderator a
   `group_suggest { group_id, candidate, ts, sig }` (`candidate` = the member entry of their contact, `sig` = their
   signature over `gsponsor|{group}|{candidate}|{ts}`); the moderator checks it (sender is a member, signature,
   candidate keys match its id, not already a member nor banned) and keeps it pending. **Inviting** the proposal adds
   the candidate (new epoch) and attaches the sponsor's proof `{ id, ts, sig }` to their invitation. The invitee, who
   may not know that moderator, **accepts an invitation from a non-contact if** the proof comes from a member of the
   state who is **their** confirmed contact and the signature covers this group and themselves. They then answer as
   for any invitation. Without a proposal, a moderator directly invites only one of their own contacts.
2. **Successor**: `S.epoch = current + 1`, `S.prev_hash = hash(current state)`,
   signer ∈ `moderators` of the **current** state.
3. **Epoch ahead** (`S.epoch > current + 1`): put on hold; the intermediate
   states are requested from peers (`group_sync`) and applied in order — authority
   is always verified step by step.
4. **Concurrency** (two distinct valid states for the same epoch, e.g. two
   moderators excluding at the same time): the smallest SHA-256(state) wins. The
   loser is **kept** (its key stays readable: messages may have been sealed with
   it), and its moderator re-issues their change on top of the winner (next epoch).
5. A client absent from the `members` of the new state is excluded: it stops
   sending; it keeps locally what it already has.

### Group messages
```
Envelope { from, to = each member, crypto = 2, ts,
           msg = base64( tag(8) ‖ nonce(12) ‖ ChaCha20-Poly1305(group_key, Letter) ) }
tag    = HKDF-SHA256(group_key, info="grp-tag")[0:8]
         — identifies the key (hence group + epoch, including two concurrent
         keys of the same epoch) without revealing group_id to the relay
Letter { type, payload, group_id, epoch, seq, sig }
sig    = Ed25519(sender, type|payload|ts|group_id|epoch|seq)
message_id = (from, ts), unchanged (§5); seq = counter per (group, sender),
         only used to detect gaps for the catch-up
```
- Sending: one envelope (same ciphertext) per current member, O(N) sends. **No
  per-recipient outbox**: an offline member catches up from peers (see below) —
  the message is also in the sender's local log, which serves it like any peer.
  Only **invitations** (`group_state` to a new member) are queued and retried,
  since the invitee does not know the group yet. A multi-recipient server `send`
  is a deferred optimization (§12).
- Receiving: find the key by `tag`, decrypt, verify `sig` with the member's
  `signing_public_key` in the state of that epoch.
  - Sender ∈ current `members` → accepted.
  - Sender **removed**: rejected on direct delivery; accepted only if it arrives
    relayed (`group_relay`) by a current member **and** the sender was a member at
    the message's epoch. An excluded member therefore cannot inject messages
    under the old key.

### p2p catch-up (offline-first, relieves the server and the senders)
```
B connects, sends to ≤ 3 connected peers (1-to-1 Letter):
  group_sync { group_id, epoch, have: { sender_id: last contiguous seq } }
each peer answers (1-to-1 Letter):
  group_relay { states: [missing states, in order],
                keys: { epoch: key_envelope re-encrypted for the requester },
                messages: [ { from, ts, msg } ] }   ← group-encrypted, as is
batches of 50 messages; continue on a new request; de-duplication by message_id
```
- Example: A writes messages 3 to 6 then disconnects. B has 4-6, C has 3-6. D
  connects with `have {A: 2}`: B sends back 4-6, C sends back 3-6 (duplicates are
  ignored), D is complete.
- **Convergence**: a `group_sync` whose `have` exceeds what we hold triggers our
  own `group_sync` towards that peer in return. So B, which lacks 3, gets it as
  soon as it meets D or C again: members converge without depending on the author.
- The author's signature, inside the ciphertext, proves origin: a peer cannot
  fabricate someone else's message, only replay it.
- Works even if the original author is disconnected (consistent with the
  composite id (sender, sequence) of §5).
- No total order required: causal order per sender + explicit `reply_to`.

### Group attachments
The `attachment` offer is a group message carrying the content identifier
(`root`, defined in §6: `SHA-256(0x02 ‖ size ‖ Merkle root of the chunks)`), the size (the number of chunks follows
from it: fixed 48 KiB chunk) and the name/type. Request and chunks remain
**1-to-1** (§6), encrypted for the requester alone, but **any member holding the
file, whole or in part**, can serve: the author, or any member who downloaded or is downloading it.
The requester can therefore spread the chunks over several connected holders
(swarm), without the author being online.
- A holder checks that the requester is a member of the current epoch.
- **Per-chunk integrity**: with a single file hash, a corrupted chunk supplied by
  a peer would only be detected at the end, without knowing which one. The offer
  therefore carries `root`, which commits to the root of a Merkle tree (leaves = SHA-256 of the
  chunks); each served chunk comes with its proof (about 10 hashes for 25 MiB). Each chunk is verified
  on receipt and a faulty peer is discarded. The chunk **index** is part of the verification (it fixes,
  at each level, on which side the sibling lies): a valid chunk presented under another index fails.
- **The hash is the content identity**: a file whose root is already present
  locally (downloaded before, or already received in another conversation) is
  neither re-downloaded nor re-hashed; a holder remembers the root and the tree
  once and for all instead of recomputing them on every request.

### Implementation (client library, `Groups/`) — milestone 1

Coded and tested: creation, invitation, add/remove/promote/leave with key
rotation, sending/receiving, p2p catch-up, moderator concurrency.
Commands: `group create|list|members|add|remove|promote|leave|send|history|sync`.

**Milestone 2** (coded and tested): swarm group attachments and group blacklist,
see the next two sections. Commands: `group upload|download`,
`group ban|unban|bans`.

Details that appeared during implementation:
- **Invitee = contact of the moderator**: an invitation (state for an unknown
  group) is only accepted from a confirmed contact, signed by a listed moderator,
  and only if the envelope's key matches `key_check`. The other group exchanges
  (states, `group_sync`, `group_relay`) are accepted from any member of a common
  group, including a non-contact: its keys come from the signed state.
- **Concurrent branches kept**: an authentic state whose parent is known is kept
  with its key even if it loses the tie-break or arrives after a more recent
  epoch — messages may have been sealed with it and must remain readable. Peers
  supply it (`states` + `keys`) during the catch-up.
- **Replay of losing changes**: a moderator remembers the changes it issued
  (`add`/`remove`/`promote`) until their epoch is settled; if it loses the
  tie-break, it replays them on top of the winner (no effect if already applied).
- **Leaving**: a moderator issues the state without themselves (and promotes the
  first remaining member if they were the only moderator); a plain member sends
  `group_leave` to the moderators. The creator cannot be excluded.
- **Demotion** (`group demote`): only the creator takes the moderator role away
  from someone else (who remains a member and keeps the key: this is not an
  exclusion); a moderator can also step down themselves. Checked by each
  receiver: a state that removes a moderator who stayed a member is only valid if
  signed by the creator (or by that moderator); excluding a member naturally
  removes their role. The creator cannot be demoted: they must first hand over the
  role. The "vote to remove a moderator" remains deferred (§12).
- **Forgetting a group** (`group forget`): deletes the local copy (states, keys,
  log) of a group one has left or been excluded from; refused while still in it.
- **Targeted reply** (`group reply`): `reply_to` (= author:ts `message_id`) is a
  field of the group message, **covered by the signature**; the display
  recomputes the local index (as in 1-to-1, §5).
- **Transferable creator role** (`group transfer`): only the current creator can
  hand over the role, to a member (promoted to moderator if they were not). The
  former creator stays a moderator but loses the creator's own powers (lifting a
  ban) and the protection against exclusion; the new one acquires them. A creator
  who **leaves** automatically hands the role to the first remaining moderator:
  the group always has a present creator. Rules checked by each receiver on every
  new state: the creator is always a member of the state they lead, and
  `created_by` only changes if the state is signed by the previous creator in
  favour of a moderator of the new state (a moderator can therefore neither crown
  themselves nor oust the creator).
- **A recent member does not read the past**: they only receive the keys of the
  epochs in which they were a member; older messages remain unreadable to them
  (and are sent again at every synchronization as long as they lack the key).
- Tests: `sources/freespeech.tests/GroupTests.cs` (27, in-memory network) and
  `scripts\test-groups.ps1` (end-to-end, 4 clients, including the catch-up of an
  offline member while the author has disappeared).

### Group attachments — implementation (swarm)

- **Offer**: `attachment` is a group message (Letter `type = "attachment"`,
  payload = the offer of §6). Its `ref` is `author:ts`, **identical for every
  member**: this is what lets anyone ask anyone for "the file `ref`".
- **Who serves**: any **current** member holding the file, **whole or in part** (the
  author, then each of those who downloaded or are downloading it). The policy is
  injected (`authorize`): in a group, "the requester is a member of the current
  epoch". A non-member gets neither the file nor even a refusal (silence).
- **Partial chunks**: a member still downloading the file serves the chunks it has
  already received. It keeps the Merkle proof received with each chunk
  (`parts/{ref}/{n}.proof`) and replays it; it checks the chunk again before sending it.
  For a request it cannot satisfy in full, it answers `attachment_have`.
- **`attachment_have { ref, have, complete, busy }`** (Letter, groups, between members):
  `have` = base64 bitmap (bit `i % 8` of byte `i / 8` = chunk `i` held); `complete` =
  everything is held (`have` empty); `busy` = "I serve nobody right now" (network
  setting, §6). Only a *member* receives this answer. The requester keeps the bitmap
  as a **lower bound**: it asks that member only for what it announced, and asks it
  again for the rest after 8 s (it keeps receiving). A member that holds nothing, or is
  `busy`, is not asked again for 30 s; its chunks are reassigned **at once**.
- **Dynamic distribution**: each holder is given a **window** of chunks (8 at first,
  then about 3 s of data from its observed speed, 2 to 64 chunks); when half a window
  is consumed, the requester gives more. A fast holder is therefore never left idle
  behind a slow one. A holder that delivers nothing for 15 s loses its window to the
  others (and is left aside for 30 s). When everything missing is already requested,
  an idle holder asks again for the chunks that have been waiting for more than 3 s at
  another one: the first answer wins, the duplicate is dropped. An offline holder's
  share is given to the others at once. The download is followed every 3 s while it
  progresses (at most 2 min without a chunk; then the 30 s tick carries on).- **Lying holder**: the Merkle proofs (§6) are verified chunk by chunk, whoever
  serves. Failures are counted **per holder**; at 3, that holder is discarded
  *for this file* and the download continues with the others. It only fails if all
  holders are discarded (in 1-to-1 there is only one: behaviour unchanged).
- **Non-contact participants**: group members do not have to be contacts to
  exchange `attachment_request`, `attachment_chunk` and `attachment_have`; their
  keys and servers come from the signed state.

### Edit / delete in a group: catch-up log and gap markers

`message_edit`, `message_retract`, `attachment_retract` and `message_react` (§5) are ordinary group messages: they enter the log and the
p2p catch-up serves them like the others. Group-specific rules:
- **Delete for everyone**: every holder **empties the sealed copy** of the message in its log (`LogEntry.Msg = ""`),
  and those of the author's `message_edit` aimed at it (they carry its text) — so none is handed to a late joiner. The entry stays: its `seq` keeps the log contiguous and a new delivery is not
  processed again. The author attaches their message's `seq` to the Letter (`seq`) so that even a member who never
  received it knows which gap to account for.
- **Gap marker**: an emptied entry is served in `group_relay.messages` as `{ from, ts, seq, msg: "" }`. The receiver
  records it in `covered[from]`; `have` (§7) counts these `seq`s as received, otherwise a member who arrived after the
  deletion would stay stuck below the hole and the catch-up would loop. A marker has **no content effect** and never
  prevents a real message from arriving: at worst a malicious peer makes a member believe it already has a `seq`,
  which that peer could already achieve by not relaying.
- **Edit**: the original copy stays in the logs; a late joiner receives the original then the edit, applied in
  order (the most recent edit wins).
- **Delete for me**: the sealed copy **stays** in my log (the other members may still need it from me). The content
  disappears from the history, the display and the backup, not from the encrypted log.

### Blacklist — two distinct mechanisms, not to be confused
| | Personal blacklist | Group blacklist |
|---|---|---|
| Scope | local to the user | group |
| Propagated | **never** | yes, via `blacklist_update` |
| Who decides | anyone, for themselves | moderator(s) only |
| Signed | not necessary | yes (Ed25519, verified against the GroupCard's moderator list) |
| Conflict resolution | n/a | union of the moderators' additions; only the creator can remove |
| Guarantee | social (no longer displays/relays) | social — **not cryptographic** without key rotation |

### Group blacklist — implementation

- **Signed operation** `BanOp { group_id, kind: add|remove, id, ts, signer, sig }`,
  `sig = Ed25519(signer, "ban|group_id|kind|id|ts")`. `blacklist_update` Letter
  `{ group_id, ops[] }`, also embedded in full in `group_relay.bans` (a short
  list: an offline member learns it from any peer).
- **Validity** (checked by everyone, against the current state): `add` signed by a
  current moderator, `remove` signed **by the creator only**. Moderators and the
  creator cannot be banned. An operation from a non-member, badly signed or
  unauthorized is ignored.
- **Effective state** of an id = its last operation (by `ts`); on equal `ts`,
  `add` wins. A replayed old operation therefore resurrects nothing.
- **Effect**: messages from a banned author are neither displayed, nor stored,
  nor relayed (the local log is purged of their messages, `group_relay` no longer
  supplies them). **No key rotation**: they remain a member, hold the key and can
  write — it is a social guarantee, not a cryptographic one (for that: exclude,
  which rotates the key). A banned member cannot be re-added until the creator
  lifts the entry.
- Accepted limit: a member who ignored the blacklist keeps displaying or
  relaying; everyone applies their own copy.

### Pinned messages

- **Signed operation** `PinOp { group_id, kind: pin|unpin, target, ts, signer, sig }`, `target` = message id (`author:ts`),
  `sig = Ed25519(signer, "pin|group_id|kind|target|ts")`. `group_pin` Letter `{ group_id, ops[] }`, also embedded in `group_relay.pins`: an offline member or a
  latecomer learns it from any peer. Outside the group state: no key rotation.
- **Validity** (checked by everyone, against the current state): signed by a **current moderator** (the creator included), for any message of the group, whoever
  wrote it; newer (`ts`) than the operation held for that message. An operation from a non-member, a non-moderator or badly signed is ignored.
- **Effective state**: the latest operation of each message decides; **at most 3 messages** are pinned, the most recently pinned ones (at the fourth, the
  oldest is released, on every device). The pins of a demoted moderator stay; another moderator can release them.
- **Missing message**: a device that receives a pin on a message it does not hold asks a member for what it lacks (the `group_sync` catch-up).
- A message deleted for everyone no longer shows as pinned. Best-effort guarantee: a modified client can ignore pins.

### Name and picture: of the group (creator) and of each user

- **Group**: `GroupProfileOp { group_id, name, photo?, ts, signer, sig }`,
  `sig = Ed25519(signer, "gprofile|group_id|name|SHA256(photo)|ts")`.
  `group_profile` Letter. Valid if signed by the **current creator**, more recent
  (`ts`) than the one held, name of 1 to 100 characters, picture of at most
  100 KiB (JPEG/PNG/WebP in base64). Like the blacklist, it lives **outside the
  GroupState**: renaming a group or changing its picture **does not rotate the
  key**. The displayed name is that of the last valid profile (it survives epoch
  changes, whose states always carry the original name). It follows the creator
  role: after a transfer, the former creator can no longer change it.
- **User**: `profile name|photo`. The change goes (1) to every confirmed contact
  through `identity_update` (§5, already planned for nickname/photo), (2) to every
  group through a **`member_profile`** `{ group_id, id, nickname, photo?, ts, sig }`
  **signed by the member themselves** (`"mprofile|group_id|id|nickname|SHA256(photo)|ts"`).
  It can be relayed by any peer: a late arrival learns it in
  `group_relay.member_profiles`, without its author being online. Received
  directly, it must come from its author. The group's signed state keeps the
  original nickname; the display uses the member's profile when it exists (with no
  authority over keys or rights).
- **Name and picture per group.** A member can use a name of their own (1 to 64
  characters) and a picture of their own in one group, different from their global ones; they
  travel in that group's `member_profile` only, so the groups cannot be linked by the name or the picture. Both are chosen when
  the invitation is accepted (the global ones are offered) and can be changed in the group's
  information; a change is sent 30 seconds after the last edit, so successive edits go out as one. An empty
  name, or the choice "my usual picture", means the global one again. A change of the global name still goes to the groups
  without a name of their own and leaves the others alone. Everyone sees the name
  the member gave to the group, including moderators; a member's name for a contact
  (`local_name`) prevails on the screens of that contact. Keys, rights and the signed state do not depend on it.
- **Description** (free text, 1 to 300 characters, absent by default):
  - *Group*: `description` field of `GroupProfileOp` (`group about`), hence signed
    by the creator, no rotation; kept when only the name or picture is changed,
    explicitly cleared by `clear`.
  - *User*: `description` field of the `ContactCard` (§3, hence also of
    `identity_update`) and of `member_profile`. Visible to confirmed contacts
    **and** to members of a common group, like the name and picture.
  - The description is included in the signed data (`…|ts|description`): changing
    it in a captured operation invalidates the signature. A profile without a
    description remains valid (null field).
  - Commands: `profile about <text>|clear`, `group about <g> <text>|clear`,
    `card <contact>`, `group info <g>`, `group members <g>`.
- Both carry the same picture limit (100 KiB) as the ContactCard (§3).
- Client: a single list of group Letter types (`GroupService.GroupLetterTypes`)
  is used for routing — two console defects came from a type handled by the
  service but never forwarded by the client.

### Key rotation — when
- At every **exclusion**, every voluntary **departure**, and on a moderator's
  request (compromised device). Periodic rotation remains deferred (§12).
- Guarantee: an excluded member can no longer decrypt **future** messages, even
  intercepted ones (relay, network), since they never received the new key.
- Accepted limits, not to be presented as guarantees:
  - an excluded member keeps everything they already received;
  - no forward secrecy *within* an epoch: a leaked key exposes its whole epoch.
- No group deletion: it fades away when everyone abandons it.

## 8. Multi-server per contact (routing)

- Use case: an employee at work (private server, not exposed to the internet) +
  friends outside (public server). A non-exposed private server **cannot** be
  reached through S2S relay from the public server — resolution must therefore be
  on the client side, not the server side.
- The client registers simultaneously on every server of its list that it can
  actually reach (same mechanics as multi-device §1) — with no limit on the
  number: N private servers (several companies, etc.) + M public servers, each
  with its own priority in `servers[]`.
- Sender: tries the recipient's servers in the order of `servers[]`, then our own
  servers (S2S cascade §10). For each one:
  - **Network unreachable** (timeout/DNS) → next server, at once.
  - **Reachable, recipient present** → delivered, done.
  - **Reachable, recipient absent** → next server (the recipient may be registered
    on only a subset of its servers).
  - **End of the list without delivery** → the envelope stays in the local outbox and a
    WAITING flag is set on **every reachable server** (we do not know where the
    recipient will come to read its flags). No reachable server → outbox only, no flag.

### Client implementation (client library, `Relay/RelayPool.cs`)

- One `RelayClient` per URL. **Own** servers (`UserIdentity.Servers`, advertised
  in the card): permanent registration + reconnection on the 30 s tick.
  **Contact** servers: connected on demand at the first send, abandoned (not
  reconnected) if they drop; an unreachable server is ignored for 30 s (5 s
  connection timeout) so as not to pay a timeout on every send.
- Sending: candidate URLs = the recipient's `servers[]` by priority, then our own
  servers as a last resort (the §10 S2S cascade may still reach it). Same
  algorithm as above.
- Receiving: de-duplication `(from, to, ts, crypto)` on envelopes arriving
  through several servers. Flags addressed to oneself: read on each of our servers.
- Self-sync (§1/§5): the copy goes to the server that delivered (the one that
  signalled `self_multi_device`).
- Console commands: `server list | add <url> [prio] | remove <url>`; each change
  sends an `identity_update` to the confirmed contacts.

### Restricted servers: who may sign up, and how to write to a contact

A server whose access mode is `allowlist` (§ 10, "Client access control") is called restricted: only the identities its administrator allowed sign up there. It must not be confused with the private status between servers, which forbids calls from another server and which a client cannot see.

Each entry of `servers` in the card carries `restricted`, a boolean absent when false, true for a restricted server. The owner knows the mode of their servers, because the server states it at registration and each time the administrator changes it. They send their card to their contacts when the mode changes. The open servers are listed before the restricted ones, each group in the owner's order of preference, and the `priority` values are renumbered to match. A client that does not know `restricted` ignores the field and follows the order of the list.

A restricted server is not written in the card by its address. At its first need the server draws a 32-byte key, the **circle key**, and gives it to every identity that signs up there (`circle_key` in `registered`, then in `server_state` when the administrator renews it). In place of the address the card carries `circle = base64(HMAC-SHA256(circle key, "freespeech-circle|" + discovery id of the card's owner))`, with `url` set to `https://circle.invalid`, an address that never resolves. A recipient who is a member of the same server computes the same value for each of their restricted servers: a match names a shared server, which they reach at their own address for it. Without a match the entry names an unknown restricted server and is never contacted. The identifier depends on the owner of the card, so two cards of the same server cannot be linked. The contact keeps the identifier as it is and the match is computed at each send, so a server added later recognises every contact already imported at once. After a renewal the client keeps the old key for 30 days to recognise the cards already sent, and the owner sends their card again to their contacts with the identifier made from the new key. A server whose key the client does not know yet is written by its address. The servers in the state of a group follow the same rule.

A restricted server on which I am not allowed is never contacted: no connection, no refusal at the administrator, no waiting marker. A card that wrongly marks a server as restricted only hinders its author.

A shared restricted server is a server of the contact's card on which I am myself registered with the `allowlist` mode, this mode being kept with the list of my servers. When there is one, sending uses only the shared servers, in the order of the card. There is no open server, no waiting marker on an open server and no fallback on my own servers. Without a shared restricted server, sending is done as in § 8.

If the restricted server does not answer, the message stays in the outbox with the mention "waiting for a private server". A restricted server that answers while the contact is not connected there is not down: the contact is simply absent.

Falling back on an open server is possible only if both sides accepted it and no shared restricted server answers. The setting belongs to each contact and each side makes it for its own sends. `Never` is the default and the message waits. With `Ask`, the application asks at the first failure, once for the outage, and the answer "yes" holds until a shared restricted server answers again, with no new question. With `Auto`, the fallback starts after `OpenFallbackDelay`, 2 minutes by default. Each side makes its choice known with the Letter `route_policy { open_fallback }`, true for `Ask` and `Auto`. It follows the normal path, so a shared restricted server when there is one, goes through the outbox and is confirmed like `identity_update`. The agreement is cooperative: it protects against a silent move to an open server, not against a modified client.

The fallback is not remembered. Every send, and every resend of the outbox, starts with the shared restricted servers, and the fallback ends as soon as one of them delivers a message.

For a group member who is not a contact, the servers of the group's state are used: the restricted servers that are not allowed are left out and the shared ones come first. The strict mode does not apply to groups.

Waking the phone (§ 9) goes through the server that holds the waiting marker. A restricted server must therefore be linked to a push gateway.

## 9. Push notifications (mobile, app not running)

**Principle: a push never carries a message, it wakes.** No server mailbox: the
message stays in the sender's outbox (§5) and is delivered only when both parties
are awake at the same time.

### Wake-up sequence (A writes to B, B closed)

1. A's message cannot be delivered: B's server sets the `(A, B)` flag (WAITING,
   unchanged) and, if B has a token there and is disconnected, calls the **push
   gateway** (below).
2. The gateway sends B a **high-priority data push** (FCM); the OS wakes the app
   with no user action. B opens a session of a few dozen seconds: connects to its
   servers, declares READY.
3. The server relays READY to A (event if connected; otherwise a symmetric wake-up
   push to A if A has a token). A resends its outbox right away instead of waiting
   for its 30 s tick.
4. B receives, decrypts and shows a **local notification** (sender and preview stay
   on the device; Apple/Google only see "wake up").

Accepted limit: if A is switched off or force-stopped right after sending, the
message waits for A's return. B must stay connected longer than A's retry tick
(≥ 45 s in practice); A keeps its retry alive a few minutes after a send.

### Token registration

- Push token (FCM/APNs) registered by the client with **each server it is registered
  on** (§8): a flag can be set on any of them. Never in the ContactCard (which only
  carries `enable_push`).
- Client → server messages (authenticated like the rest of the session):
  - `push_register { platform: "fcm"|"apns", token }` — the client names no
    gateway: the relay picks among the ones it has linked; replaces any previous
    registration of this identity.
  - `push_unregister {}` — removes the token (notifications off, logout, account
    deletion).
  - `push_refresh { token }` — new token from the OS; same effect as `push_register`.
- Reply: `push_result { ok, message? }`. The token is stored per identity on the
  server, **in memory only** (like the flags): the client re-registers on every
  connection. Never relayed to another server or a contact.
- The server only calls **linked** gateways (accepted by both administrators, see
  "Linking servers by mutual consent"): no
  address comes from the client. Without a linked gateway push is off (`push_result.ok = false`) and
  only polling remains.
- **Several gateways**: several may be linked (identical: same credentials,
  no state to keep). For a wake-up the server tries the linked
  gateways in random order, **at most 3**. It moves on at 5xx (503
  included: no credentials for this platform here), timeout, or `429` with
  `X-Push-Reason: busy`. A `429` with `X-Push-Reason: token` (this token was just woken)
  is **not** saturation: it stops, another gateway would only wake the device twice.
  `410`: token dropped. Any other 4xx: invalid request, no retry.
- **Circuit breaker**: a gateway failing 3 times in a row is set aside for 45 s; one that
  answers "busy" with `Retry-After: N` is set aside at once for N s (120 s at most). A
  failure therefore costs one timeout, not one per wake-up. Wake-ups stay best effort:
  polling is the fallback.- `peer_online { discovery_id }` (server → client): a contact we have a WAITING flag
  towards just connected; the client flushes its outbox at once **and immediately re-requests
  the attachment downloads** that were waiting for this contact (§6): a sender that was off,
  woken by a push, has only a few seconds to serve a photo. If A is offline and
  has a token, A gets a wake-up instead.
- Rate limit: at most one wake-up per identity every 30 s. An attempt that **no gateway** took (outage, saturation) does not count: the next
  one may go out at once. The gateway applies the same rule to its per-token delay.
- **Presence**: an identity is "online" while one of its WebSocket connections answers. The relay pings every 30 s and closes a connection
  that has not answered within 45 s (phone switched off, network lost); the identity is then offline. A WAITING flag raised **during** a
  connection that ends this way (newer than the connection's opening) triggers the wake-up that presence had suppressed.
- FCM message: high priority, `collapse_key` `wake` (wake-ups waiting for an unreachable phone merge into one) and a 7-day lifetime.

### Push gateway (`freespeech.push`, separate project)

- Sole holder of the FCM/APNs credentials (never in the public repo: local config
  files, versioned `.example`). Relay servers hold none.
- `POST /push { platform, token, kind: "wake" }`; rejects unknown callers,
  rate-limits per token (`X-Push-Reason: token`) and total load (`busy`).
- **One key pair per relay server** (signed calls, see "Linking servers by mutual consent"):
  the gateway's operator accepts a relay (`admin accept <id>`), and can **ban** it (`403`),
  **forget** it (`401`) or read its counters (sent, failed, busy, rate limited, dead tokens)
  without touching the other relays. With no linked relay the gateway refuses
  everything (except in development).
- **Settings changed at run time** (`settings.json`): minimum delay per token,
  simultaneous wake-ups, and **maintenance** (answers `429 busy` to everybody: relays
  try another gateway, a restart loses nothing). Local `/admin/...` API and `admin ...`
  command, same rules as the relay server (see "Operations" in §10).
- **Generic, constant** payload: no sender, preview or identity.
- `410` / invalid token → the relay server deletes the token; the client re-registers
  on its next connection.
- A forked server or client may use the official gateway or its own; without push
  config files the code still compiles and only polling remains.

### Notifications on the phone — how they work and settings (Android)

**Two independent settings** (app Settings, both on by default):

| Setting | What it decides | When off |
|---|---|---|
| **System notifications** | showing an alert in the phone's notification shade when a message arrives and the app is not open; the app asks for Android's permission (Android 13 and up) | messages still arrive (app open or woken), with no alert; if the **phone** blocks the app's notifications, Settings says so and leads to the system's |
| **Network wake-up (push)** | receiving while the app is closed: the push token is given to the servers, and a periodic check is scheduled | the token is removed from the servers (`push_unregister`), the periodic check is cancelled: messages only arrive when the app is opened |

**Three situations.**
1. **App open**: no system notification; the conversation list shows counters and dots.
2. **App in the background, process alive and connected**: the app receives live and builds a local notification itself ("messages"
   channel, high importance): title = sender (and group), text = preview. One notification per conversation, replaced by the next;
   it is **removed as soon as the message is read** in the app. A muted conversation stays silent.
3. **App closed or process killed**: see below.

**App closed — push wake-up.** B's server calls the gateway (sequence above); the FCM data message starts B's process, which runs a
**check** (`BackgroundPoll`): loads the identity, opens a client **separate** from the UI, connects to its servers and listens for
**25 s** from the moment a server is actually connected (the network of a phone that has just started is not always ready: the connection is retried every 3 s, 60 s at most) (45 s for the periodic check); meanwhile the sender sees B online (`peer_online`, above) and resends; B decrypts **locally**
and builds its notification (sender and preview never leave the device). A **lock** allows one check at a time (the profile database
is not opened twice): if the periodic check already holds the lock when the push arrives, the push handler **waits for it to finish**
(60 s at most) instead of returning, otherwise the system freezes the process right after it connected. The periodic check
(`WorkManager`, ~15 min, network required) is the **fallback** when push is refused, absent or blocked.

**Keeping the process alive (foreground service).** A few-second session is not enough for a large file or for a message waiting for
its recipient. The app then starts a **foreground service** (`dataSync` type, discreet notification, "transfers" channel):
- **a woken sender serving a file**: "Sending a file", until the transfer ends, **10 min** at most;
- **the app going to the background with messages waiting** (non-empty outbox) **or a file being sent**: "Waiting to send…", until
  delivered, back in the app, or **3 min**; swiping the app away no longer interrupts the send.
No service notification when there is nothing to send: a plain wake-up stays silent. Permissions: `FOREGROUND_SERVICE`,
`FOREGROUND_SERVICE_DATA_SYNC`.

**Known limits.**
- The server sends **no more than one wake-up per identity every 30 s**; a sender who closes the app without the foreground service
  leaves the message in the outbox until the app is next opened.
- **Outbox retries**: a letter nobody has taken is retried on every tick (30 s) for 10 minutes, then every 15 minutes up to 24 hours, then
  every 6 hours, and **not at all after 3 days** (a contact who no longer exists does not keep the app busy). It stays in the outbox: it
  goes out, whatever its age, as soon as the relay reports the contact online (`peer_online`), as long as the WAITING flag left by the last
  try lives on the relay (30 days, in memory). At every connection the client sends its outbox at once, without waiting for the first 30 s tick: a sender woken
  by push while its contact is already online gets no `peer_online`, and the woken contact listens for a few seconds only. The sending foreground
  service (Android) only starts for letters younger than 3 days.
  A delivered letter (the relay wrote it to the contact's connection) that is **not confirmed** raises the WAITING flag too, after a few seconds (3 s): a dead connection
  (phone in airplane mode) accepts the write without the letter arriving. When the contact is back on a new connection, peer_online (or the sender's push wake-up) sends
  every unconfirmed letter of that contact again at once (the receiver deduplicates and confirms again). The flag is removed (DELETE /api/flags/{from}/{to}) when the last waiting letter is confirmed;
  the mobile raises it at once when the app goes to the background.
  The user can also **stop waiting** for an undelivered letter (mobile: "Outgoing" menu, shown by a setting): the letter stays in the outbox
  and goes out like the others on `peer_online`, but it is no longer retried on every tick and no longer starts the foreground service;
  "Resume" restores the wait.
- A manufacturer may kill the app (for example MIUI's "OneKeyClean" cleaner, battery saver): set the battery to "Unrestricted" for the
  app. An app **force-stopped** by the user receives no push until it is reopened (on MIUI, the "close all" cross of the recent apps is a
  force-stop).
- On Android, when the battery is optimised for the app, it offers the optimisation exemption (system Yes/No box) in a box the first time the home
  screen shows. The box is shown once, and can be reopened from Settings → "Check the phone's settings".
- Push needs Google Play services and a build with the Firebase configuration; without them only the periodic check runs.
- iOS: no foreground service and no reliable silent wake-up.

**Privacy and precautions.**
- Google (FCM) sees: the device token, the app package, the time of each wake-up — **never** a content, a name or a FreeSpeech identifier
  (constant payload). The gateway only sees the token; the relay server keeps the token **in memory** only.
- A notification's text (sender, preview) is shown according to the phone's lock-screen setting; **the app does not force the preview to
  be hidden** (lead: a "private" visibility notification). Advice: set Android to "hide sensitive content".
- A foreground service's notification reveals neither a contact nor a content.
- The wake-up diagnostic traces (`adb logcat`, tag `FreeSpeech`) only hold steps ("wake-up received", "connected"), never content; they
  are only readable with a debugging cable.
- §9 trade-off unchanged: the server knows who writes to whom (the flag), never the content.

### Fallback and iOS

- Mandatory fallback: native OS polling (~15 min) if push is refused/absent/blocked
  by the vendor.
- iOS: silent pushes (`content-available`) are throttled and never delivered after
  a swipe-close; the reliable way is a generic visible push with a notification
  service extension (≈ 30 s) running the wake session. Deferred (§12).
- Accepted trade-off: the server knows the social graph (who
  talks to whom) by construction of the flag, never the content.
## 10. Server side

- Registry: declared / connected users (multi-connection, see §1/§8).
- **No auto-learning of siblings from client traffic.** The `to_server` field of
  an envelope (§8) only serves to route *that precise envelope* — it must never
  feed a server's discovery cascade list (`KnownServers`/siblings), private **or
  public**. That list is only modified manually by the admin (see the rule
  below).
- **Private** server: only accepts S2S requests (discovery cascade, envelope
  forward, flag propagation) from a manually configured whitelist of siblings —
  never from an unknown server that contacts it spontaneously. A refusal must
  reveal nothing (same generic answer as a non-existent server) so as not to
  confirm its existence by elimination. Goal: a stranger must never be able to
  learn the address of a private server, nor even its existence, through the
  discovery cascade.
- **Public** server: may query another public server to synchronize the client
  directory + sibling directory (S2S `relay` / `sync_whitelist`). Remains subject
  to the "no auto-learning" rule above.
- Admin web interface (add/remove sibling, private/public status) — outside the
  protocol: an interface only uses the admin APIs
  described below, nothing more.
- Voluntary unregistration command (removal from the directory): not done. There
  is no server-side directory; only push tokens can be withdrawn (`push_unregister`).

### Client access control (private server)

Distinct from the public/private status (which only concerns traffic **between
servers**): the **access mode** says who may register on this server.

- `open` (default): everybody, except banned identities.
- `allowlist`: only identities the administrator **allowed**, or that present a valid
  **invitation**. This is a private server for a family, an association, a company.
- **A ban always wins**, also in `open` mode, and cuts the identity's live connections
  at once. Removing an allowed identity cuts them too.
- **Invitation**: token `fs-inv-<20 hex>`, **single use**, valid 1 to 90 days (7 by
  default), created by the admin and shown once (the server keeps only its SHA-256
  hash, `id` = first 8 hex, to list or revoke it). The client attaches it to
  `register` (`invitation` field). It is consumed **only after** the proof of key
  possession (§11): the identity then becomes allowed and its later connections no
  longer need the token.
- **Refusal**: `error` whose message is the stable code `private_server` (unknown
  identity: ask for an invitation) or `access_refused` (banned). Nothing else is said.
  The client shows the matching text (settings → servers) and keeps retrying quietly.
- **Access request** (`allowlist` server): an unknown identity may ask to be allowed. The
  client adds `request_access: true`, `name` (64 characters at most) and `note` (200 at
  most) to `register` — **only after the user's consent**, since their name leaves the
  device. After the proof of key possession the server queues the request and answers
  `error` / `access_pending`; from then on **every** connection of that identity gets
  `access_pending` (the name is not sent again) until the administrator decides. A waiting
  identity that asks again **updates** its request (name, note, date): never two lines. The
  queue is bounded (`Server:MaxAccessRequests`, 500), one entry per id, entries expire
  (`Server:AccessRequestDays`, 14), text is cleaned (control characters removed). `accept`
  allows the identity (the name becomes the note); `reject` removes it, with or without a
  ban. A banned identity cannot queue (`access_refused`). The client learns it was accepted
  because its next reconnection succeeds (retry at start and at every tick). The
  administrator can close the queue (`requests close`): new strangers get `private_server`.
- Persisted in `access.json` (`Server:AccessFilePath`), atomic write, applied
  **without restart**. An unreadable file puts the server in `allowlist` (never open by
  accident). `Server:ClientAccess` sets the starting mode of a server with no file yet.
- Routes (see "Operations"): `GET /api/admin/access`, `POST /api/admin/access/mode`,
  `POST/DELETE /api/admin/clients`, `POST/DELETE /api/admin/clients/ban`,
  `POST/DELETE /api/admin/invitations`, `GET /api/admin/access/requests?name=&older_than_days=&limit=&offset=`,
  `POST /api/admin/access/requests/accept|reject` (`ids` — prefixes of 6 hex or more —, or `all` with
  `name` / `older_than_days`; `ban`, `reason`) and `POST /api/admin/access/requests/open`, and `GET /api/admin/siblings` (linked relays: `url`).

### Administration tool of a private server (groups created for the members)

A private server (`allowlist`, which accepts no other relay) can **name** FreeSpeech identities as its **administration tools** (`provisioners`): a tool,
plugged into the local administration API, creates groups there for the people allowed on that server.

- **An ordinary identity**: a FreeSpeech client (name, picture and description like a contact card), specific to this server. The server lets it register
  without an invitation or an authorization; it only sees the groups it created.
- **Members' public keys**: when an allowed identity registers, the server keeps its public keys (`enc_key`, `sign_key`, those of the contact card), after
  checking that the identifier is their fingerprint. They are never kept for an identity that is not allowed, and are erased with the authorization.
  `GET /api/admin/clients/keys` → `[ { id, note, enc_key, sign_key } ]` for the allowed identities whose keys it knows (an allowed person who has not
  connected yet cannot be put in a group).
- **Naming the tool**: `GET /api/admin/provisioners`, `POST /api/admin/provisioners` `{ id, enc_key?, sign_key? }` (the keys are only taken if they match the
  identifier), `DELETE /api/admin/provisioners?id=`. A ban removes the name.
- **What clients see**: `registered` and the `server_state` event (see "Moving a client") carry `provisioners` `[ { id, enc_key, sign_key } ]`; the client keeps
  only the entries whose keys match the identifier, and only for the servers it is connected to.
- **Trust rule**: a letter from an identity named by one of **its** servers, which is not in its contacts, is accepted only if it is of type `group_state`
  (an invitation), with the signature checked against the announced keys. The invitation still waits for the user's agreement (one gesture) and is shown as
  coming from the administrator; the group is joined only after that. Any other type from that identity is ignored.
- **Public address**: the tool needs the address phones use to reach the relay (the one in the app's settings): the relay states it in
  `GET /api/admin/status` (`public_url`, setting `Peer:PublicUrl`, also used for the link between relays).
### Linking servers by mutual consent (relay ↔ push gateway, relay ↔ relay)

No secret is shared: a link exists only if **both administrators** accepted, whoever asked.

- **Identity**: each server creates an ECDSA P-256 key pair at its first start, kept in `peer-identity.json` (mode 600). Only the public
  key travels; its **fingerprint** (SHA-256 of the key, 64 hexadecimal characters) is the server's identifier.
- **Public endpoints** (reachable by other servers, like `/api/time`):
  - `GET /peer/identity` → `{ software: "FreeSpeech", kind: "relay"|"push", name, public_key, fingerprint }`.
  - `POST /peer/request` `{ kind, name, url, public_key, ts, sig }`: a request for a link. `sig` signs `peer-request\n<kind>\n<name>\n<url>\n<ts>`
    with the key presented (so nobody can ask in another key's name), `ts` within 5 minutes. Answers: `pending` (202), `accepted` (200),
    `banned` / `closed` (403), `full` (429), or 400 (`bad_signature`, `bad_time`, `bad_url`, `bad_key`, `kind_not_accepted`).
    A relay only takes requests from gateways (`push`), a gateway only from relays.
  - `POST /peer/status` (signed call) → `{ state: "pending" | "accepted" | "banned" | "unknown" }`: where my request stands.
- **Signed call**: headers `X-Peer-Id` (fingerprint), `X-Peer-Ts` (Unix seconds, within ±2 minutes) and `X-Peer-Sig` (signature of
  `METHOD\nPATH\nTS\nSHA-256(body)`). The receiver checks that the caller is an **accepted** peer, the time, the signature against the
  pinned key, and that it has not seen it before (replay protection). On the gateway, `POST /push` only takes a signed call from a
  linked relay. A banned peer gets 403, an unknown one or a wrong signature 401.
- **Peer states**: `pending_out` (I asked), `pending_in` (it asked me), `accepted`, `refused` (it answered no) and `banned`. If each asks the
  other, the link is made at once. The asker learns the answer by querying `/peer/status` (every 20 s while a request waits; a request that
  did not get through is sent again). The same call checks the links that exist: each server asks its neighbours every 60 seconds by default (`Link:CheckSeconds`, `peer-check`), except those it had a successful exchange with within that time. Three failures in a row warn the administrator (kind `peers`), as does the server's return. A call signed by a known server is never limited; the limit of 30 calls per address per 10 minutes only concerns strangers.
- **Who is asking**: a request carries the name, the address and an optional **contact** (`Peer:Contact`: a mail address, a phone number, a page), all signed by the key that asks, and the receiver adds the address the request came from. None of this proves anything: the name, the address and the contact are what the sender declares, so the administrator reaches the sender by that contact (or any other way he trusts), has the fingerprint read out with `peer-id` and compares it. The same lines are in the mail sent for a request (notification kind `peers`).
- **Acceptance**: the administrator sees name, address, contact, origin and fingerprint (to compare with what the other reads out: `peer-id`); on acceptance
  the server queries the announced address and refuses if it does not show the key of the request (no call is ever made to an address before the
  administrator chose to accept). An unreachable address is accepted "unverified".
- **Safeguards**: bounded requests (`Peer:MaxRequests`, 200), one entry per key (asking again updates it), expiry (`Peer:RequestDays`, 14),
  per-address throttling, cleaned text, a closable queue. `https` required for `add` (http only for this machine or a private network, or `--insecure`).
- **Persistence**: `peers.json` (atomic write) next to the server's state; a restart loses no link. An unreadable file is copied to `.corrupt`
  and no peer is recognised until it is repaired.
- **Local admin routes**: `GET /api/admin/peers[?kind=]`, `GET /api/admin/peers/requests`, `POST /api/admin/peers` (add),
  `POST /api/admin/peers/accept|reject`, `DELETE /api/admin/peers?id=`, `POST|DELETE /api/admin/peers/ban`, `POST /api/admin/peers/open`,
  `POST /api/admin/peers/refresh`, `GET /api/admin/peers/identity` (prefix `/admin` on the gateway).
- **Sibling relays**: `add relay <url>` / `accept relay <id>` between two **public** relays (a private
  relay refuses: it takes no part in server-to-server traffic). A linked relay becomes a **sibling**; its `/s2s/...` calls carry
  `X-Peer-Id` / `X-Peer-Ts` / `X-Peer-Sig`, and
  `S2SAuthMiddleware` checks them (404 revealing nothing on failure, per-sibling quota, banned address refused). Both relays must have set
  `Peer:PublicUrl` (each calls the other); a relay that gave no address cannot be accepted. A banned address (`server-ban`) wins over a link.
- **A link forgotten or banned on the other side** is detected: every 5 minutes (or at `peer-refresh`), each server asks its linked peers whether they still
  know it; a peer that answers "unknown" or "banned" becomes `refused` (no call could succeed). A network failure changes nothing.

### Directory (`freespeech.directory`, separate project)

A directory tells a client that knows no relay which relays take new accounts, and spreads newcomers over them. It takes no part in the exchange of messages and stores nothing about clients.

- **Link**: a relay is linked to a directory like to any server — by keys, with the agreement of **both** administrators (`add directory <url>` on the relay, `accept <id>` on the directory); no secret is shared and nothing is automatic, so that a relay is offered to newcomers only when a directory's administrator decided so, and a relay talks only to the directories its administrator chose. Several directories are independent: each relay links to each of them.
- **Heartbeat**: every `Directory:HeartbeatSeconds` (120) the relay sends `POST /directory/heartbeat`, a signed call (see "Linking servers"), body `{ "country": "FR", "free": 90, "accepts_new": true, "version": "1.2.0", "date": "2026-10-17" }`. `country` is ISO 3166 alpha-2, declared by the relay's administrator (`settings set country`); `free` is the number of places left before the relay offers its clients another server (`max-connections` minus the accounts connected; -1 when it states no limit); `accepts_new` is true while the relay is public, its client access is `open` and it has room; `version` and `date` are those of the relay program, for the directory's administrator. The number of users is never sent, and the address offered is the one the directory verified when its administrator accepted the relay. A private relay sends nothing.
- **Address check**: at the first heartbeat of a relay and then every `Directory:ProbeMinutes` (30) the directory asks the relay's address for `GET /peer/identity`; the relay is offered only while the address answers with the key that was accepted.
- **Offered relays**: a relay is offered when it is linked, spoke within `Directory:ExpiryMinutes` (10), was seen at its address, takes new accounts and has room.
- **List**: `GET /directory/servers?country=FR&count=3` (count 1 to 10, `Directory:ListSize` (3) by default) → `{ "ts": 1790000000, "servers": [ { "url": "https://r1.example.org", "country": "FR" } ], "sig": "…" }`. The relays of the country come first and the others fill what is missing; inside each group the draw is random, weighted by the places left (a fixed weight when the relay states no limit), so that newcomers do not all land on one relay. `sig` signs `directory-list` + a line break + `<ts>` + a line break + one `<url>|<country>` line per server, in order, with the directory's ECDSA P-256 key. More than 30 requests a minute from one address: 429.
- **Client**: the application has the directories built in, each with its public key (`FsDirectories`: `url|key`, separated by semicolons). A new identity asks them in a random order, takes the first answer that verifies (signature good, at most a day old, addresses `https` — `http` only for the local machine) and makes the first relays its servers. A list that does not verify, or that is empty, is ignored and the next directory is asked. Without an answer the identity takes one of the built-in fallback relays (`FsFallbackServers`, separated by semicolons), at random.
- **Privacy**: the directory keeps nothing about the clients that ask and logs no address. A client sends it, at most, its country.
- **Administration**: `servers` (relays known, with country, places left, version, whether they are offered), `settings` and the commands every server has: [cli-directory.en.md](cli-directory.en.md). Installation: [INSTALL_DIRECTORY.en.md](INSTALL_DIRECTORY.en.md).

### Operations: local administration, logs, tracing

- **Why at run time.** The server keeps presence, waiting markers and push tokens in
  memory: a restart cuts every connection and erases them. Everything that is
  administered (access, siblings, bans, gateways, limits, log levels) therefore
  applies **without a restart**. Only start-up settings (ports, paths, admin secret)
  need one; it is preceded by `admin drain` (relay) or `admin maintenance on` (gateway).
- **Who may administer.** The secret (`Admin:Secret`, empty = everything refused)
  **and** a call from the machine itself (loopback; the address forwarded by nginx is
  the one that counts, so nothing is reachable from outside even if the proxy lets it
  through; `Admin:AllowRemote=true` only for a private network). Secrets are write-only.
- **`admin` command** (relay: `dotnet FreeSpeech.Relay.dll admin ...`; gateway:
  `dotnet FreeSpeech.Push.dll admin ...`): talks to the running server's local API,
  no web interface. Address: `--url` / `FS_ADMIN_URL` (default `ASPNETCORE_URLS`);
  secret: `FS_ADMIN_SECRET` / `Admin:Secret`. Exit codes: 0 ok, 1 usage, 2 no secret,
  3 server unreachable, 4 refused or failed.
- **Log levels at run time**: per category (`relay`, `s2s`, `push`, `access`, `flags`,
  `admin`, `http`, or `all`), `admin log level relay debug`, back to the configuration
  with `admin log reset`. `admin log tail [-n N] [--level L] [-f]` reads an
  **in-memory** buffer (2000 lines, never written to disk by this code).
- **Tracing one identity** (like `sip set debug peer`): `admin trace <8+ hex>
  [--minutes N]` writes to the buffer what happens to that identity (connection,
  refusals, sends, flags, push wake-up). **60 minutes at most**, stops by itself,
  leaves a line in the log.
- **Log privacy rules** (non-negotiable): metadata only; never content, keys, device
  tokens, nor the encrypted payload (debug level gives only the cipher mode and the
  size); identifiers are cut to 8 characters; "who writes to whom" does not appear at
  Information level, only at debug (shortened) or in a requested trace; production
  keeps `Logging__LogLevel__FreeSpeech=Warning`.
- **Push gateway**: `GET /admin/status`, `GET /admin/relays`, `/admin/peers…`,
  `GET/POST /admin/settings`; `log` and `trace` as
  above (`/admin/log...`).
- **Source-code offer (AGPL section 13)**: `GET /api/info` (public, no secret) returns
  `{ software, license: "AGPL-3.0-or-later", version, source }`; `source` comes from `Server:SourceUrl`.
  This endpoint says nothing about the server's state.

### Implementation (S2S session)

- **Public/private status, candidates and banned addresses** persisted in `siblings.json`
  (configurable path, `Server:SiblingsFilePath`) —
  [Services/SiblingRegistryService.cs](freespeech.relay/Services/SiblingRegistryService.cs).
  A sibling = a relay linked by keys (`{ url }`, see "Linking servers by mutual consent").
- **Candidates — never auto-promoted** (explicit decision taken during design,
  counter-proposal to "learning through clients", judged too risky: private server
  address leak + SSRF). An already whitelisted sibling can *suggest* URLs through
  `POST /s2s/siblings/sync`; they land in a queue (`GET /api/admin/candidates`)
  for admin review — never promoted on their own. Clients cannot suggest a sibling
  at all.
- **Admin** ([Controllers/AdminController.cs](freespeech.relay/Controllers/AdminController.cs)):
  plain JSON/HTTP (portable, no web UI for now), protected by the `X-Admin-Secret`
  header (config `Admin:Secret`, empty = everything refused by default):
  `GET/POST /api/admin/status` (public/private),
  `GET /api/admin/siblings`,
  `GET/DELETE /api/admin/candidates`,
  `GET/POST/DELETE /api/admin/blacklist` (a ban also removes from the
  whitelist/candidates; the ban itself remains manual or triggered by throttling —
  only the throttling remains to be wired, out of scope for this session).
- **Incoming S2S** ([Controllers/S2SController.cs](freespeech.relay/Controllers/S2SController.cs)),
  protected by a **request signature** (see "S2S authentication" below) — a
  refusal (invalid or stale signature, replay, private server, banned URL,
  oversized body) systematically returns a generic `404`, never any detail:
  `POST /s2s/lookup` (local presence, **a single hop** — never restarts the
  cascade towards its own siblings, to stay loop-free). The WebSocket transport's
  `is_online` (§11) also cascades to siblings in the same way — without that, a
  client considers `is_online=false` for a contact reachable only through a
  sibling and gives up before even trying to send (bug found in manual testing:
  the OTC contact exchange and the message outbox flush check `is_online` before
  sending).
  `POST /s2s/relay` (envelope forward, delivered through the same
  `IMessageDeliveryService` as the live WebSocket),
  `POST /s2s/flag` (WAITING/READY propagation — trust is at the level of the
  whitelisted sibling, not re-verified at the level of the client `from`),
  `POST /s2s/siblings/sync` (reception of suggestions, see above).
- **Outgoing S2S** ([Services/S2SClient.cs](freespeech.relay/Services/S2SClient.cs)):
  each call is best-effort (5 s timeout, errors swallowed) — a dead sibling must
  never block the send/flag that triggered the attempt. Triggered from
  `RelaySocketHandler.HandleSendAsync` (cascade on `send` without a local
  recipient, in whitelist order, first sibling that answers present) and
  `FlagsController.SetFlag` (systematic propagation to all siblings when this
  server is public).
- Verified over direct HTTP (2 server instances, admin + S2S): refusal by default
  without an accepted link, generic refusal with a wrong signature, lookup/relay/flag working
  between linked relays, candidate suggestion never auto-promoted, ban that does
  remove from the siblings/candidates. End-to-end test with real chat clients
  (OTC contact + `send` across two sibling servers) **not done yet** — requires
  several interactive windows, to be validated manually.

#### S2S authentication

Every `POST /s2s/*` request is **signed by the calling relay's key** (headers `X-Peer-Id`, `X-Peer-Ts`, `X-Peer-Sig`: see "Linking servers by mutual consent").
No secret travels.

The receiver checks, in this order, answering `404` on any failure: server is
public; headers present; body ≤ `Server:MaxMessageBytes` + 16 KiB; caller linked
and not banned; signature; |clock − timestamp| ≤ 120 s; signature never seen
(in-memory cache, replay refused). An authenticated
sibling exceeding `Server:S2SRatePerMinute` (default 1200) gets `429` +
`Retry-After: 60` (logged).

When a sibling relay is added: URL must be **`https`**, not
loopback nor a private network (anti-SSRF).
`Server:AllowInsecureSiblings=true` lifts these rules — development
and tests only. The S2S client does not follow redirects.

#### Moving a client to a sibling

A server that is full — or that an administrator is emptying before
maintenance — **advises** the client of siblings that have room; the client
decides, and proves its identity (§11) to the new server. Nothing is moved by
force.

- **Capacity**: `Server:MaxConnections` (0 = unlimited). `POST /s2s/capacity`
  (signed, §10) → `{ "free_slots": n }` (`-1` unlimited, `0` full).
- **Choosing siblings**: the server asks its whitelisted siblings (answer
  cached for 15 s, so a load spike does not become an S2S storm), drops the
  full and the silent ones, draws at random weighted by free room, keeps at
  most 3.
- **Server → client event** `server_redirect`
  `{ "servers": ["https://…", …], "reason": "load" | "admin" }`, sent right
  after `registered` when the server is over `MaxConnections` (if no sibling
  has room the client stays: refusing it would leave it with nothing), or on
  demand: `POST /api/admin/drain { "count": n }` (X-Admin-Secret).
- **Server → client event** `server_state` `{ "access": "open" | "allowlist", "provisioners": [...] }`, sent to every
  connected client when the administrator changes who may sign up (`POST /api/admin/access/mode`): the app then shows the server as
  public (`open`) or private (`allowlist`) without waiting for a reconnection. Clients that are offline, waiting or refused learn it at
  their next connection (`registered` already carries `access`).
- **Client side**: follows the suggestion only if it comes from one of **its**
  servers, to an `https` URL (http only if the origin is http), at most **3
  times per 24 h** (stops bouncing between two full servers). It registers on
  the new server (proof §11), replaces the old one in its server list (same
  priority), records it in `retired_servers` (the default server is not
  added back at the next start), then sends an `identity_update` to all its
  contacts and groups (queued if it is offline).
- A contact still writing to the old server finds the client through the S2S
  cascade (§10) as long as both servers are siblings.

#### HTTP session token (`/api/flags`)

The `registered` answer carries `http_token` (192 random bits, valid while the
WebSocket connection lives). `GET/POST/DELETE /api/flags/…` require
`X-Discovery-Id` **and** `X-Flag-Token`: knowing an online identifier is no
longer enough to read or set its flags. The client adds it to every request
(it changes at each reconnection).
> **Rule**: `to_server` must never touch `KnownServers`. Adding it there
> automatically would leak the address of private company servers (§8) to the
> public discovery cascade as soon as one of their employees sends a message to an
> external contact.

## 11. Real-time transport

**Principle: no proprietary dependency.** The whole protocol must remain
re-implementable in any language without a library tied to a specific framework.
**Raw WebSocket (RFC 6455)** is used — no higher-layer protocol such as SignalR:
any client, in any language, with just a standard WebSocket library (`ws` in JS,
`websockets` in Python, `gorilla/websocket` in Go, `System.Net.WebSockets` in
.NET...), can talk to this server.

- **Endpoint**: `GET /ws/relay` (WebSocket upgrade).
- **Framing**: each text frame carries a single JSON object with a `type` field
  that determines the rest of the schema. No binary format, no specific
  serializer — plain, readable JSON, debuggable with any generic WebSocket client
  (e.g. `websocat`, a browser's network tab).

**Client → server**:
```json
{ "type": "register", "request_id": "...", "discovery_id": "...", "enc_key": "base64", "sign_key": "base64", "invitation": "fs-inv-...", "request_access": true, "name": "Anna", "note": "..." }   // invitation: optional, private server (§10); request_access/name/note: access request, after consent (§10)
{ "type": "register_proof", "request_id": "...", "signature": "base64" }
{ "type": "send", "request_id": "...", "envelope": { "from": "...", "to": "...", "crypto": 1, "msg": "..." } }
{ "type": "is_online", "request_id": "...", "discovery_id": "..." }
```

**Server → client**:
```json
{ "type": "challenge",        "request_id": "...", "nonce": "base64", "discovery_id": "..." }
{ "type": "registered",       "request_id": "..." }
{ "type": "send_result",      "request_id": "...", "delivered": true, "self_multi_device": false }
{ "type": "is_online_result", "request_id": "...", "discovery_id": "...", "online": true }
{ "type": "message",          "envelope": { ... } }   // push, no request_id
{ "type": "error",            "request_id": "...", "message": "..." }
```

- `request_id` is chosen by the client (e.g. a UUID) and copied as is into
  **every** answer to a request (including `registered` and `error`) — this is
  what lets the client correlate a request and its answer on a connection that
  also receives asynchronous pushes (`message`) in between. Only `message` (a
  push, never an answer) has no `request_id`. No notion of "channel" or "RPC
  invocation" at the transport level: just JSON messages in both directions.
- **Registration with proof of identity.** `register` must be the first message sent after
  connecting; any other command before it receives `error`. Without proof, anyone could register
  under another person's id, receive their envelopes (metadata) and make their senders believe a
  message was delivered (so it is lost). It takes two steps:
  1. The client sends `register` with its `discovery_id` and its **two public keys** (`enc_key`
     X25519, `sign_key` Ed25519, 32 bytes each, base64). The server checks that
     `SHA-256(enc_key ‖ sign_key)` (lowercase hex) is that `discovery_id` (§1), then answers
     `challenge` with a random 32-byte `nonce` (base64, single use).
  2. The client signs, with Ed25519 and its signing key, the UTF-8 string
     `freespeech-register-v1|<nonce>|<discovery_id>` and sends it as `register_proof.signature`.
     The server verifies it with `sign_key`: only then is the connection registered and
     `registered` returned. Any failure (keys that do not match the id, a wrong signature, a
     `register_proof` without `register`) returns `error` and cancels the registration in progress;
     a nonce is used once.
  3. **Access control** (§10), **after** the proof: a server that restricts registrations
     answers `error` with the message `private_server` (unknown identity, an invitation is
     needed: `invitation` field of `register`), `access_refused` (banned identity) or
     `access_pending` (access request filed, waiting for the administrator; `request_access`,
     `name`, `note` fields of `register`, §10). These three codes are stable; the client derives the text to display from them.
  Public keys are public: only the holder of the signing private key can answer the challenge.
  A server-side **development** option (`Server:AllowAnonymousRegister`, off by default) accepts a
  `register` without keys, for test scripts that cannot sign; never enable it on a public server.
- **Size limits.** A frame larger than `Server:MaxMessageBytes` (512 KiB by default; a 48 KiB file
  chunk or a 100 KiB profile photo fits easily) closes the connection (WebSocket code 1009) instead
  of filling the server's memory. On the client a message text is limited to 8,000 characters
  (`msg_too_long`); a received message beyond four times that is ignored. Pictures sent from the mobile
  app are reduced (1,600 px, JPEG) before sending, like videos (720p). In nginx: per-address limits on
  connections and requests.
- **Volume limits.** The relay cannot tell a file chunk from a message (opaque envelopes), so it
  limits **bytes sent**, per identity and per network address, to keep one client from saturating
  it. A daily quota per identity (`Server:MaxBytesPerIdentityPerDay`, 1 GiB over a rolling 24 hours),
  a throughput per identity (`Server:MaxBytesPerIdentityPer10Minutes`, 300 MiB: faster is slowed
  down, held back at most 20 s, not cut) and a daily quota per address
  (`Server:MaxBytesPerAddressPerDay`, 5 GiB, for the identities behind one address); `0` switches a
  limit off, which a private server does to serve large files. A refused `send` answers `error` with
  the stable code `quota_exceeded` or `rate_limited` and `retry_after` (seconds); the connection stays
  open. `registered.daily_quota` and `send_result.quota_left` let the client show what is left.
  The limit of one file is the client's: it is a setting of the user, who confirms before sending a
  larger one (§6).
- `send` fails with `error` if `envelope.from` does not match the `discovery_id`
  registered on that connection (anti-impersonation, see §5); otherwise answers
  `send_result.delivered = true` if at least one of the recipient's connections
  received the `message` push, `false` otherwise (the client then falls back to
  WAITING/READY, §9). `send_result.self_multi_device = true` if the sender has at
  least one other active connection than the one that just sent — a signal used
  client-side for the conditional self-sync (§1/§5), the server not
  replaying the encrypted envelope itself to the sender's other devices
  (unreadable without the right private key).
- An identity may register several simultaneous connections (multi-device, §1); a
  `message` push is sent to **all** of the recipient's active connections.
- The HTTP endpoints (`/api/flags`, `/api/time`) remain JSON over classic HTTP —
  already natively portable, no change.
- **Reconnection (client library)**: a loss of connection to the relay itself
  (not only an offline recipient — network cut, server restart, see §9 mobile) is
  treated like the offline-recipient case: the message goes into the local queue
  (outbox), the WAITING flag is attempted best-effort (silently ignored if that
  fails too). The periodic tick (§9) retries the WebSocket reconnection first,
  before retrying pending contacts and the outbox — `RelayClient.TryReconnectAsync`.

## 12. Deferred points

- Periodic group key rotation (besides exclusion/departure/compromise, already covered in §7).
- Multi-recipient server `send` (a single copy sent, the server does the fan-out) to relieve group sending (§7).
- Groups: per-sender-key encryption with a ratchet / MLS if groups of several thousand members appear.
- Forward secrecy (ratchet) for 1-to-1 messages; encryption of attachments at rest.
- **Stolen phone: remote erase and revocation** (design settled; **(1) and (2) are implemented**, see §1; (3) and (4) remain). (1) A **revocation key** derived from the phrases'
  seed with another HKDF label; devices keep only its public key, the private one exists only while the phrases are typed. (2) A `wipe`
  letter to oneself, signed by that key (a signature by the identity key would not do: the thief has it in the phone's database); devices
  installed **before** its timestamp erase themselves, the others ignore it. Best-effort delivery: the relay stores nothing, so it only
  arrives if the sender is reachable when the stolen device connects. (3) `identity_moved { new_discovery_id }` signed by the revocation
  key (published in the contact card), to move contacts and groups to a new identity (new phrases): the `discovery_id` derives from the
  keys, so a key rotation *is* an identity change. If the phrases leaked, no automatic rotation is safe. (4) Periodic rotation of the
  **receiving** keys (announced, signed sub-keys) = partial forward secrecy; needs sync between devices. PIN lock: see §1.
- Ephemeral messages: not retained ("delete for everyone" is enough; a timer does not bind a hostile recipient).
- **Identity migration `identity_moved`** (**implemented on 2026-10-06**, see §1 "Stolen phone: migration"; the detail below is the original design, §1 is authoritative).
  - **Principle**: after a wipe the user moves to a **new identity** (new phrases, to be saved as at creation). Letter
    `identity_moved { old_id, new_card, ts, sig }`, `sig` = signature by the **old identity's revocation key** over
    `"FreeSpeech-moved-v1|<old_id>|<new_id>|<ts>"`, sent **from the old identifier** (restored with the old phrases on the new device).
    An ordinary `identity_update` does not fit: the keys are immutable in it, and it is signed by the signing key the thief holds.
  - **Revocation public key**: **pinned on first receipt, immutable** (contact card, `identity_update`, and for group members who are not
    contacts the `member_profile`, signed by the member). **Never** taken from the `GroupState`: the identifier (`SHA-256(enc ‖ sign)`)
    does not cover it, a moderator could substitute it. With no pinned key (contacts from before the feature): no migration, never accepted
    on faith; a new card is exchanged.
  - **Contacts**: automatic mechanics (card imported, contact created, history linked), **manual decision** in one tap, with the safety number.
    An "accept automatically" setting, off by default, never applied to a contact marked ✔. One `moved` per old identifier (the next one is
    flagged as a conflict); bound to `old_id` and `ts` against replay. No automatic `identity_revoked`: whoever holds the phrases could use it
    to block their owner; at most a warning.
  - **Groups — before the `moved`, the wizard**: for each group where one is **creator**, asks for a successor (moderators first, else a
    member; warning: they will hold all the powers) and issues the transfer; for each group where one is **moderator**, drops the role;
    without waiting for an acknowledgement (states travel peer to peer). The old identifier becomes a plain member. Order: `wipe`, then
    transfers, then `moved`. A group left aside stays exposed (the stolen phone keeps its rights).
  - **Groups — on receipt**: a moderator who receives a `moved` whose signature is **verified** excludes the old identifier with a **key
    rotation** in a single state (setting "exclude a migrated account automatically", on by default): the new key is not handed to the old
    identifier, which loses the later messages (a rotation alone would not do: it would still be in `members`). Signature not verifiable:
    manual request in "Requests". The **new account is re-invited only after validation**, and accepts the invitation (§7, rule 1); roles
    are not carried over. A **banned** identifier cannot migrate (the notice is refused). The letter type is added to
    `GroupService.GroupLetterTypes`. Display: "former account" on the old identifier.
  - **Limits**: if the **phrases** leaked nothing is protected (same valid signature); the thief can act before the wizard (same identity
    keys); best-effort delivery (the relay stores nothing); an exclusion is repaired by a re-invitation, hence the automatic exclusion
    accepted despite the risk of blocking by a phrase holder.
  - **Planned tests**: migrating a contact (valid signature, wrong one, no pinned key, replay, conflict); creator transfer then role
    drop; automatic exclusion with rotation; lone creator; a banned identifier migrating; unverifiable signature.
- Vote to remove a moderator.
- A moderator deleting someone else's message in a group; a time limit for editing or deleting for everyone; synchronizing edits/deletions between devices of the same identity.
- Exact format of the default public "trusted" servers in production.

## 13. Security: precautions and known weaknesses

Index of the risks, pointing to the section that details the measure. "Left" = what is not covered today.

| Risk | Measure | Left |
|---|---|---|
| Someone takes the unlocked phone and looks for the phrases | the phrases are never stored; the Backup page does not offer them (§1, §2) | **no app lock** (code, biometrics): conversations stay readable; lead |
| Someone sees the save screen, or the app in the list of recent apps | a single screen, no way back (§1); **protected screen** setting (Android, off by default): no screenshot, no preview in the recent apps (`FLAG_SECURE`) | with the setting off, screenshots are possible; shoulder surfing |
| Phrases leaking through clipboard, spooler, PDF, cloud | warnings on screen and on the sheet (§1) | up to the user |
| Theft of the phrases or the paper QR code | paper in a safe place | **no detection or revocation**: the same identity can run on another device with nothing showing it |
| Stolen key file | password-encrypted, Argon2id 64 MiB, bounded cost, AEAD (§1) | weak or short password; password not recoverable |
| Stolen backup file | encrypted with a key derived from the identity (§2) | whoever has the phrases reads it; copy in the app's cache |
| Hand-typed phrases too weak | at least 75 characters, Argon2id (§1) | real strength left to the user |
| Identifier spoofing on a server | registration with proof of possession of the signing key (§11) | — |
| Replay, forgery between servers | signed S2S, 120 s window, nonce, quota (§10) | automatic blacklisting of faulty servers (deferred) |
| Push provider observation | constant wake-up message with no content (§9) | Google sees token and wake-up times |
| Preview readable on the lock screen | the wake-up notification has no content (§9); **locked screen** setting (Android, on by default): a notification made by the app shows only "New message" until the phone is unlocked | with the setting off, sender and text show on the lock screen |
| Keyboard that suggests or learns from what is typed | **discreet keyboard** setting (Android, off by default): no suggestions, no personalised learning (`IME_FLAG_NO_PERSONALIZED_LEARNING`) in the app's text fields | the keyboard decides whether to obey; a malicious keyboard reads everything typed, whatever the flag says |
| Spyware or a compromised phone reading what the app stores | the database content is encrypted field by field (`Vault`; identifiers and timestamps stay readable), its key lives in the platform's secure storage, no cloud backup of the app's data | attachment files and their caches are **in clear** in the app's private folder; a compromised system, or a root access, reads what the app itself can read |
| Curious server | never sees content or phrases (§1, §5) | sees **who writes to whom** and when (flags) |
| Forward secrecy | — | no ratchet (Double Ratchet deferred, §12) |
| Stolen or lent phone, unlocked | optional PIN lock, optional erase after N failures, remote wipe then migration to a new identity with exclusion from groups (§1) | PIN off by default and bypassed by root; the old identity stays valid for whoever read the keys until contacts follow the migration; nothing protects if the phrases leaked |

