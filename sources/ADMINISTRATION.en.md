# Administering your FreeSpeech servers (relay and push gateway)

*Version française : [ADMINISTRATION.fr.md](ADMINISTRATION.fr.md). Installation: [INSTALL_RELAY.en.md](INSTALL_RELAY.en.md) and [INSTALL_PUSH.en.md](INSTALL_PUSH.en.md). Command lines: [cli-relay.en.md](cli-relay.en.md) and [cli-push.en.md](cli-push.en.md). Protocol: [PROTOCOL.en.md](PROTOCOL.en.md) §10.*

This document describes **the current configuration** and how to change it **without a web interface and without restarting**:
an `admin` command shipped with every server. It is enough to run a public server, a private server or a push gateway.

## 1. Principle

- A server stores no message, but it keeps **in memory** who is connected, the waiting markers and the push tokens. Restarting it
  cuts every connection and erases that state, so it is avoided. Everything that is administered is changed **at run time**
  and saved to a small JSON file (atomic write).
- A restart is only needed for start-up settings: port, file paths, admin secret. Before it: `admin drain 100` (the relay invites
  100 clients to move to a sibling) or `admin maintenance on` (the gateway answers "busy", relays use another gateway).
- The admin API is **never** public: it requires the secret **and** a call coming from the machine itself.

## 2. Connecting to the administration

The complete list of commands, their options and exit codes is in [cli-relay.en.md](cli-relay.en.md) (relay) and [cli-push.en.md](cli-push.en.md) (push gateway).

Do this **on the server** (ssh), or from your computer through an SSH tunnel (`ssh -L 5290:127.0.0.1:5290 ubuntu@fs.example.org`).

```bash
export FS_ADMIN_URL=http://127.0.0.1:5290         # relay; the push gateway is on 5291
dotnet /var/www/freespeech_relay/FreeSpeech.Relay.dll admin status      # run in that folder, it reads the secret from appsettings.Local.json
```

The secret (`Admin:Secret`) goes **in `appsettings.Local.json`**, next to the program, like every FreeSpeech secret (see
[INSTALL_RELAY.en.md](INSTALL_RELAY.en.md)). From **another computer** (SSH tunnel), give it through `FS_ADMIN_SECRET` in
that terminal's environment (never as an argument).

| Setting | Role |
|---|---|
| `--url …` or `FS_ADMIN_URL` | local address of the server (default: `ASPNETCORE_URLS`, else `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` or `Admin:Secret` | admin secret (never as a command-line argument) |
| `Admin__AllowRemote=true` | accepts a call that does not come from the machine: **private network only** |

Exit codes: `0` ok, `1` usage, `2` no secret, `3` server unreachable, `4` refused or failed. The `help` command lists everything.

## 3. Relay server: `FreeSpeech.Relay.dll admin …`

### 3.1 Public or private, and who may register

Two **separate** notions:

- **Public / private** (`public`, `private`): only concerns exchanges **between servers**. A private server does none.
- **Access mode** (`access open|allowlist`): who may **register** on this server. `open` = everybody except the banned;
  `allowlist` = only allowed or invited identities.

A "private" server in the usual sense (family, association, company) therefore combines `private` **and** `access allowlist`.

| Command | Effect |
|---|---|
| `status` | public/private mode, access mode, counters |
| `public` / `private` | takes part in server-to-server traffic or not |
| `access open` / `access allowlist` | who may register |
| `clients` | allowed, banned, pending invitations |
| `allow <id> [note]` | allows an identity (64 hexadecimal characters) |
| `remove <id>` | removes it from the list and **cuts its connections** |
| `ban <id> [reason]` / `unban <id>` | bans: refused everywhere, **even in open mode**, connections cut |
| `invite [note] [--days N]` | creates a **single-use invitation** (token `fs-inv-…`, 7 days by default, 90 at most) |
| `revoke <invitation-id>` | revokes an unused invitation |
| `pending [--name t] [--limit N] [--offset N] [--json]` | **access requests** waiting, oldest first |
| `accept <id…>` or `accept --all [--name t] [--older-than D] [--yes]` | allows the requesters (see below) |
| `reject <id…>` or `reject --all [--name t] [--older-than D] [--ban] [--reason t] [--yes]` | turns requests down, and bans with `--ban` |
| `requests open` / `requests close` | take new requests or not (those waiting stay) |

**Joining a server (user side).** The user opens *Settings → Servers* and adds the address: it is the **server that tells the app**
whether it is open or private, the user has nothing to choose. An open server connects at once. A private server is shown as "private"
with two buttons, two ways. **Invitation** (*I have an invitation*): the administrator creates the invitation and passes it on
(message, voice, paper); the user types the token. The token is spent at the first successful registration; afterwards the identity is
"allowed" and reconnects without it. **Access request** (*Request access*): after a confirmation message (their name goes to the
server) the request is sent and the app shows "waiting for approval". If the server answers "access refused", the app says so too.
When the administrator changes the sign-up rule, the connected devices learn it at once (`server_state` event) and the app shows the
server accordingly; the others learn it at their next connection.

**Handling access requests.** `pending` shows, for each request, an id (12 characters), the date, and the name and note the
person left; `--json` gives everything, for scripts. `accept` and `reject` take one or several ids: **a prefix of 6 characters or
more is enough** if it is unique ("ambiguous" otherwise: give more). In bulk, `--all` takes the whole queue, which `--name` (name
or note contains…) and `--older-than 7d` narrow down. Safeguards: with neither ids nor `--all`, nothing is done; `--all` asks for
confirmation at the keyboard and **requires `--yes` in a script**; an unknown id (already handled, expired, mistyped) is reported
and the exit code is 4, the others being handled anyway. `reject --ban` also bans the identity: it cannot ask again. Without
`--ban` the person may ask again.

**Knowing who asks: the verification code.** The name and the note of a request are **declared** by the person; the server does not check
them (identities are anonymous). What is proven is possession of the key. To tie a request to the person holding the phone, each request
carries a **verification code**: the first 12 characters of the id, upper case, in groups of four (`3FA9-C1B2-7E40`). The app shows it to
the person when they send the request; they read it out to the administrator through **another channel** (phone, in person), who
compares it with the one from `pending` (or from the *Clients* page of the console) before accepting. An impostor who wrote the same name
cannot know the code of an identity they do not hold. `accept` and `reject` take the code as shown (dashes and case do not matter). For a
person chosen in advance, the **invitation** is more direct: the token handed over through a safe channel is enough.

Good to know: a request is only taken **after the proof of key possession** (nobody can ask in someone else's name); a person who
asks again (app closed then reopened, name corrected) **updates their request** without doubling it; received text is cleaned (no
control characters, 64 and 200 characters at most); the queue is bounded (`Server__MaxAccessRequests`, 500 by default) and
requests **expire** (`Server__AccessRequestDays`, 14 days by default; asking again renews the delay). If the queue fills with
unwanted requests, `requests close` shuts it: strangers then simply get "private server". **On the user side** the app retries at
every start and at regular intervals: as soon as the administrator has accepted, the connection succeeds, the server turns
"online" in the list and the app shows a notice (console); there is no other signal to wait for.

**Identifiers.** An identifier is the fingerprint of the person's public keys (64 hexadecimal characters): it reveals nothing
else. `allow` and `ban` need the **full** identifier, found in the person's contact card. In `allowlist` mode, `clients` lists the
allowed identities (including those who came with an invitation) with their full identifier: take the one to ban from there. In
`open` mode the server **does not remember** who connected (by design): banning someone needs their full identifier, for
instance the one a contact of theirs reports to you. That is why the invitation is the simple way to keep a server closed.

**Groups for the members.** A private server can **name** a FreeSpeech identity as its administration tool: an ordinary client (name, picture, description)
that creates groups for the people allowed on that server. People receive an invitation "from the administrator" and accept it with one gesture; they are
never added without their agreement. The server keeps an allowed person's public keys at their first connection (`GET /api/admin/clients/keys`); a person who
has not connected yet cannot be put in a group yet. For the tool to do so, the server must state its public address (the one in the app's settings):
`Peer:PublicUrl` in `appsettings.Local.json`, repeated in `GET /api/admin/status` (`public_url`). The tool is named by `POST /api/admin/provisioners`
(see PROTOCOL.en.md, "Administration tool of a private server") and removed by `DELETE /api/admin/provisioners?id=`. This only concerns private servers.
### 3.2 Sibling servers (public relay)

`add relay <url>` / `requests relay` / `accept relay <id>` (§3.4, the same commands as with a gateway) link two **public** relays by their keys; they are then
siblings. Both set `Peer:PublicUrl`.

| Command | Effect |
|---|---|
| `siblings` | linked relays (siblings) |
| `candidates` / `dismiss <url>` | siblings **suggested** by a trusted sibling (never promoted on their own) |
| `blacklist` | banned servers |
| `server-ban <url> [reason]` / `server-unban <url>` | bans a server (sibling **or** push gateway): removed from the lists and refused afterwards |
| `drain <n>` | invites up to n clients to move to a sibling that has room (maintenance) |
| `limits` / `limits set …` / `limits reset` | limits on the volume sent per identity and per address: show, change at once, go back to the settings (details: `cli-relay.en.md`) |
| `traffic [--limit N]` | who sent the most in the last 24 hours |

### 3.3 Files and backup

| File | Content | Path setting |
|---|---|---|
| `siblings.json` | public/private, candidates, banned | `Server__SiblingsFilePath` |
| `access.json` | access mode, allowed, banned, invitation hashes, access requests | `Server__AccessFilePath` |
| `peer-identity.json` | this server's **private key** (linking with gateways, §3.4) | same folder as `access.json` (`Peer__StatePath`) |
| `peers.json` | linked servers, requests sent and received, banned | same |
| `limits.json` | the volume limits changed by the administrator (absent = those of the settings) | `Server__LimitsFilePath` |

Back them up (`peer-identity.json` is a private key: mode `600`). An unreadable `access.json` puts the server in `allowlist`: it never opens by
accident. `Server__ClientAccess=allowlist` sets the mode of a new server, before any file exists.

### 3.4 Linking a push gateway or a sibling relay

Two servers trust each other only if **both administrators** said yes, whoever asked. Each server has its own key pair
(`peer-identity.json`, created at the first start): only the **public** key is exchanged, and its fingerprint (64 hexadecimal characters)
is the identifier. Nothing secret passes from one administrator to the other.

| Command (relay) | Effect |
|---|---|
| `peer-id` | this server's fingerprint: read it to the other administrator so they can check it |
| `add push <url> [--fingerprint f] [--insecure]` (or `add relay <url>` for a public sibling relay) | asks a gateway for a link. The gateway's fingerprint is shown (`--fingerprint` checks it: refused if it differs). `https` required; `http` only for this machine or a private network, or with `--insecure` |
| `list push` | the gateways linked (`linked`), asked (`asked`, waiting for their administrator), refused, banned |
| `requests push [--name t] [--limit N] [--json]` | the requests **received** from a gateway that wants to serve this relay |
| `accept push <id…>` or `--all [--name t] [--older-than D] [--yes]` | accepts (a prefix of 6 hexadecimal characters or more; same safeguards as for clients) |
| `reject push <id…>` or `--all … [--ban]` | turns down, and bans with `--ban` |
| `remove push <id>` / `ban push <id>` / `unban push <id>` | forgets, or bans, a server |
| `peer-requests open` / `close` | takes new requests or not |
| `peer-refresh` | asks now where the requests you sent stand |

The gateway has the same commands (§4), for relays. **How it goes**: the relay's administrator runs `add push https://push.example.org`;
the gateway's administrator sees the request (`requests`), compares the fingerprint with the one the relay's administrator reads out
over the phone, then `accept <id>`; the relay learns it by itself in under a minute (or `peer-refresh`). If each asks the other, the
link is made at once. Wake-ups are then **signed** by the relay (timestamp and replay protection); a linked gateway is used for
the relay's clients. A request carries the name, address and fingerprint announced, trusted
for nothing until the administrator accepts; on acceptance the server checks that **the announced address really shows the key of the
request** (otherwise it refuses: nobody can ask in the name of an address they do not hold). The queue is bounded (`Peer__MaxRequests`,
200), requests expire (`Peer__RequestDays`, 14), received text is cleaned. Non-secret settings: `Peer:Name` (the name presented) and
`Peer:PublicUrl` (this server's address, needed when it is the **gateway** that asks a relay). An unreadable `peers.json` is copied to
`.corrupt` and no server is recognised until it is repaired.

## 4. Push gateway: `FreeSpeech.Push.dll admin …`

Every **relay** that calls the gateway has **its own key pair**: one can be banned without touching the others.

| Command | Effect |
|---|---|
| `status` | providers (fcm, apns), counters, limits |
| `relays` | linked relays, with their counters (sent, failed, busy, limited) |
| `set min-interval <s>` | one wake-up per device token at most every s seconds (default 15) |
| `set max-concurrent <n>` | beyond it, answers "busy" (default 100) |
| `maintenance on` / `off` | answers "busy" to everybody: relays switch to another gateway |

`add relay <url>` asks a relay for a link (it has to accept; `Peer:PublicUrl` must be set), and
`requests`, `accept <id>`, `reject`, `list`, `remove`, `ban`, `peer-id` handle the relays' requests, exactly as in §3.4 (the word `relay`
is optional on the gateway). Linked relays are recognised by the signature of their calls.

Files: `settings.json`, `peer-identity.json` (the gateway's private key) and `peers.json` in `Push__StatePath` (`/var/lib/freespeech-push`).
A gateway with **no linked relay** refuses everything (outside development).

## 5. Logs and tracing

Common to the relay and the gateway (`log …`, `trace …`).

| Command | Effect |
|---|---|
| `log` | current levels and categories |
| `log level <category\|all> <level>` | changes a level **now** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`) |
| `log reset` | back to the configuration's levels |
| `log tail [-n 100] [--level debug] [-f]` | last lines; `-f` follows live (Ctrl+C to end) |
| `trace <id> [--minutes 15]` | follows **one** identity (8 or more hexadecimal characters); stops by itself, 60 minutes at most |
| `trace list` / `trace stop <id\|all>` | lists, stops |

Relay categories: `relay` (connections, sends), `s2s`, `push`, `access`, `flags`, `admin`, `http`. Gateway: `push`, `relays`, `fcm`,
`apns`, `http`. `all` sets the default level.

Production keeps `Logging__LogLevel__FreeSpeech=Warning`: `log tail` then only shows warnings. To diagnose:
`admin log level relay information`, observe, then `admin log reset`.

### Privacy rules (non-negotiable)

- A log holds **metadata** only. Never message content, never a key, never a **device token**, never the encrypted payload
  (`debug` level gives only the cipher mode and the size).
- Identifiers are **cut to 8 characters**.
- "Who writes to whom" does **not** appear at Information level: only at `debug` (shortened) or in a requested `trace`.
- A `trace` is limited to 60 minutes, stops by itself and leaves a line in the log. Lines stay **in memory** (2000).
- Tell your users if you switch on a trace: it is good practice, and for a public server a duty of transparency.

## 6. Security of the administration

- Long random secret (`openssl rand -hex 32`), specific to each server. Empty = **everything refused**.
- Calls **from the machine itself** only; nginx does not publish the API (`location /api/admin { return 404; }`). Even if that block
  were missing, the address forwarded by nginx is not local: the call would be refused.
- The administration secret is never read back by the command or the API.
- An invitation token is shown once: note it right away.
- Everything is changed at run time; a change is saved at the same instant (atomic write: no half-written file).

## 7. Local API reference

Useful for scripts; the `admin` command is only a wrapper. `X-Admin-Secret` header, JSON.

**Relay** (`/api/admin/…`): `GET status`, `POST status {is_public}`, `GET access`, `POST access/mode {mode}`,
`POST clients {id,note}`, `DELETE clients?id=`, `POST clients/ban {id,reason}`, `DELETE clients/ban?id=`,
`POST invitations {note,days}` → `{token}`, `DELETE invitations?id=`, `GET siblings`, `GET/DELETE candidates`,
`GET/POST/DELETE blacklist`, `POST drain {count}`, `GET/POST/DELETE limits {identity_per_day,identity_per_10_minutes,address_per_day}`, `GET traffic?limit=`, `GET/POST/DELETE peers…`, `GET log`, `POST log/level {category,level}`,
`DELETE log/level`, `GET log/tail?lines=&level=&follow=`, `GET/POST/DELETE trace`.

**Gateway** (`/admin/…`): `GET status`, `GET relays`, `GET/POST/DELETE peers…`, `GET settings`, `POST settings {minIntervalSeconds,maxConcurrent,maintenance}`,
and the same `log…` and `trace…`.

Example:

```bash
curl -s -H "X-Admin-Secret: $FS_ADMIN_SECRET" -H "Content-Type: application/json" \
     -d '{"note":"Anna","days":7}' http://127.0.0.1:5290/api/admin/invitations
```

## 8. Troubleshooting

| Symptom | Lead |
|---|---|
| `Refused: wrong or missing admin secret, or not called from the machine itself` | secret missing or wrong, or the call does not come from the machine (SSH tunnel, not nginx) |
| `Cannot reach … at http://127.0.0.1:5000` | wrong address: set `--url` or `FS_ADMIN_URL` (the service listens on 5290 / 5291) |
| `Not found` when deleting | nothing to remove, or nginx hides `/api/admin` (use the local address) |
| A user says "private server" | they are not in `clients`: `allow <id>`, `invite`, or have them **request access** in the app then `pending` / `accept <id>` |
| "Waiting for approval" does not change | the administrator has not handled the request yet: `pending`, then `accept <id>`; the person is connected at the app's next attempt |
| A user says "access refused" | they are banned: `clients`, then `unban <id>` |
| `log tail` is empty | `Warning` level in production: `log level relay information` |
| After a restart the relay forgot its siblings or refuses everybody | `siblings.json` or `access.json` unreadable (hand-edited with a syntax error): the log says `Could not parse`. An unreadable `access.json` puts the server in `allowlist`; an unreadable `siblings.json` starts empty. Fix or restore the file, then restart **outside usage hours**. Prefer the `admin` command to hand editing |
