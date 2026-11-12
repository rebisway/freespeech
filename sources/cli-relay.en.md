# Relay server command line

`dotnet FreeSpeech.Relay.dll admin <command>` administers a running relay, with no web interface and no restart: every change applies at once and the server saves it.
Concepts: [ADMINISTRATION.en.md](ADMINISTRATION.en.md); installation: [INSTALL_RELAY.en.md](INSTALL_RELAY.en.md).
Without `admin`, the same program starts the relay (`--urls http://127.0.0.1:5290` sets the listening address); `check` checks the installation.

## 1. Principles

**Where to run.** On the relay's machine, in the program's folder, or from another computer through an SSH tunnel The script `sources/deploy/install-shortcuts.sh` creates the shortcut `fsrelay` (runs `admin` as `www-data`, from any folder): `fsrelay status`, or `fsrelay` alone for the `relay>` prompt.
(`ssh -N -L 15290:127.0.0.1:5290 user@server`, then `--url http://127.0.0.1:15290`). The admin API only answers calls made from the machine itself and requires the
admin secret; nginx does not publish it.

| Setting | Role |
|---|---|
| `--url <address>` or `FS_ADMIN_URL` | the relay's local address (default: `ASPNETCORE_URLS`, otherwise `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` | the admin secret. When absent, read from `Admin:Secret` in `appsettings.Local.json` in the program's folder |

| Exit code | Meaning |
|---|---|
| 0 | succeeded |
| 1 | wrong usage (unknown command, missing argument, confirmation missing in a script) |
| 2 | no admin secret available |
| 3 | server unreachable |
| 4 | refused or failed (wrong secret, call not made from the machine, unknown or ambiguous id, check refused) |

**Identifiers.** A user or server identifier is a 64-character hexadecimal fingerprint. Commands that handle requests accept a **prefix of 6 characters or more** if it is
unique ("ambiguous" otherwise, code 4). A **verification code** as `pending` shows it (`3FA9-C1B2-7E40`, dashes and case do not matter) is accepted too.

**Bulk handling** (`accept`, `reject`). With neither ids nor `--all`, nothing is done. `--all` takes the whole queue; `--name t` (name or note contains *t*) and `--older-than N`
(days, `7` or `7d`) narrow it. A `[y/N]` confirmation is asked at the keyboard; in a script (standard input redirected), `--yes` is required, otherwise the command
refuses (code 1).

**Output.** Lists print as a table; `--json` (where it exists) gives the raw answer for a script. `status`, `clients`, `siblings` print JSON.

**Interactive prompt.** `dotnet FreeSpeech.Relay.dll admin` with no command opens a `relay>` prompt (like `asterisk -r` or `mysql`): type the same commands there,
without repeating `admin`. `help` lists them; `exit`, `quit` or Ctrl+D leave. Up/Down walk through the history (200 lines, kept in `~/.freespeech_admin_history`); Left/Right, Home/End, Backspace and Delete edit the line, Ctrl+U clears it. An error does not end the session. Ctrl+C stops the running command (a followed log, an identity
trace) and keeps the prompt. At the prompt, `trace <id>` shows that identity's traffic live until Ctrl+C, which also ends the trace. In a script (input or output
redirected), the command with no argument prints the help and exits with code 1; `--shell` forces the prompt, reading the commands from standard input, one per line.

## 2. State and status

| Command | Effect |
|---|---|
| `version` | version, runtime and uptime of the **running** server (not of the files on disk) |
| `status` | public or private, access mode, counters (siblings, allowed clients, pending requests…) |
| `public` / `private` | takes part in server-to-server traffic or not |
| `drain <n>` | invites up to *n* clients to move to a sibling that has room (maintenance) |
| `limits` | the volume limits in force: per identity per 24 h, per identity per 10 min, per address per 24 h, and where each comes from (setting, or changed by the administrator) |
| `limits set [--identity-day S] [--identity-10min S] [--address-day S]` | changes the given limits at once and durably; *S* = `1G`, `300M`, `500K` or bytes (binary units), `0` or `off` = no limit |
| `limits reset` | goes back to the limits of the settings (`Server__MaxBytes…`) |
| `traffic [--limit N]` | who sent the most in the last 24 hours: identities and addresses, biggest first (10 by default) |

The relay cannot tell a file chunk from a message, so the limits count **bytes sent**. They are kept in `limits.json` (`Server__LimitsFilePath`) and apply without a restart; the running counters are kept.

## 3. Who may register

| Command | Effect |
|---|---|
| `access open` / `access allowlist` | everybody not banned / only allowed or invited identities |
| `circle-key renew` | draws a new circle key (`allowlist` only): the members receive it at once and send their card again to their contacts; clients keep recognising the old one for 30 days. This key has nothing to do with the server's identity key: the links with other servers do not change |
| `clients` | allowed, banned, pending invitations |
| `connected` | the identities connected **right now** (memory only: nothing is kept about those that connected earlier), with their state here: allowed, banned, or neither |
| `allow-connected` | allows every identity connected right now (except banned ones): run it before `access allowlist`, so nobody who is here is refused at their next connection |
| `allow <id> [note]` | allows an identity (64 characters) |
| `remove <id>` | removes it from the list and cuts its connections |
| `ban <id> [reason]` / `unban <id>` | bans (refused everywhere, even in open mode, connections cut) / lifts the ban |
| `invite [note] [--days N]` | creates a single-use invitation `fs-inv-…` (7 days by default, 90 at most; the token is shown once) |
| `revoke <invitation-id>` | revokes an unused invitation |

## 4. Access requests (private server)

| Command | Effect |
|---|---|
| `pending [--name t] [--limit N] [--offset N] [--json]` | waiting requests, oldest first (**verification code** to compare with the person's phone, date, declared name and note) |
| `accept <id…>` or `accept --all [--name t] [--older-than D] [--yes]` | allows the requesters |
| `reject <id…>` or `reject --all [--name t] [--older-than D] [--ban] [--reason t] [--yes]` | turns down; `--ban` also bans the identity |
| `requests open` / `requests close` | takes new requests or not (those waiting stay) |

## 5. Suggested siblings and banned addresses

| Command | Effect |
|---|---|
| `siblings` / `candidates` / `blacklist` | linked relays (siblings) / suggested siblings / banned addresses |
| `dismiss <url>` | forgets a suggested sibling |
| `server-ban <url> [reason]` / `server-unban <url>` | bans an address (sibling or gateway): removed from the lists, refused afterwards |

## 6. Linking with a push gateway, a sibling relay or a directory

Each server has its own key pair (`peer-identity.json`). A link exists only if **both** administrators accepted it; only the public key is exchanged.
The kind (`push`, `relay` or `directory`) is required.

| Command | Effect |
|---|---|
| `peer-id` | this relay's fingerprint, to read out to the other administrator |
| `add <push\|relay\|directory> <url> [--fingerprint f] [--insecure]` | asks for a link; the fingerprint of the server asked is shown (`--fingerprint` checks it). `https` required; `http` for this machine or a private network, or with `--insecure`. A relay asks a gateway (`push`) or a public relay (`relay`) |
| `list [kind]` | every server linked (`linked`), asked (`asked`), refused (`refused`) or banned (`banned`), all kinds when none is given; the contact its administrator declared is shown between `<` `>`; the `LINK` column gives the state of the exchanges with each directory (heartbeats), linked relay (calls in either direction) and push gateway (wake-ups): `ok 35s` (last exchange worked), `failing 12min (unreachable)`, `(refused 401)`, `(busy)` or `(error 503)` (failing for that long), `none yet` (no exchange since the start); the state is kept in memory |
| `requests <push\|relay> [--name t] [--limit N] [--offset N] [--json]` | requests received Each request shows the contact the sender declares and the address it came from. |
| `accept <push\|relay> <id…>` or `--all [--name t] [--older-than D] [--yes]` | accepts; refuses if the announced address does not show the key of the request (code 4) |
| `reject <push\|relay> <id…>` or `--all … [--ban]` | turns down; `--ban` forbids asking again |
| `remove <push\|relay> <id>` | forgets a server (it may ask again) |
| `ban <push\|relay> <id>` / `unban <push\|relay> <id>` | bans / lifts the ban |
| `peer-requests open` / `close` | takes incoming requests or not |
| `peer-refresh` | asks now where the requests you sent stand, and whether linked servers still know this relay |
| `peer-check [seconds]` | how often the linked servers are checked (10 to 3600; 60 by default, `Link:CheckSeconds`); without a value, shows it. A check is not sent when an exchange with that server worked within that time |

Settings (not secrets, in `appsettings.Local.json`): `Peer:Name` (the name presented), `Peer:Contact` (how another administrator can reach yours to check who is asking) and `Peer:PublicUrl` (this relay's address: required to ask another relay).
A link between relays requires both to be public (`admin public`).

A **directory** (`add directory <url>`, accepted by the directory's administrator with `accept relay <id>`) is told every `Directory:HeartbeatSeconds` seconds (120) how this relay is: its country (`settings set country`), the places it has left (`settings set max-connections`, `-1` when it states no limit) and whether it takes new accounts, plus its version and date. It then offers the relay to clients who know no relay. A relay is offered only while it is **public**, its client access is `open` and it has room; a private relay says nothing. `Peer:PublicUrl` must be set (`self set public-url`) before asking a directory: the directory checks the relay at that address. The number of users is never sent.

```bash
# Relay → gateway: the relay asks, the gateway accepts (gateway side: see cli-push.en.md)
dotnet FreeSpeech.Relay.dll admin add push https://push.example.org

# Two neighbouring public relays
dotnet FreeSpeech.Relay.dll admin public
dotnet FreeSpeech.Relay.dll admin add relay https://other-relay.example.org
dotnet FreeSpeech.Relay.dll admin requests relay       # on the other relay
dotnet FreeSpeech.Relay.dll admin accept relay 3fa9c1
```

## 7. Logs and tracing

| Command | Effect |
|---|---|
| `log` | current log levels |
| `log level <category\|all> <level>` | changes a level **at run time** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`). Categories: `relay`, `s2s`, `push`, `access`, `flags`, `admin`, `http` |
| `log reset` | back to the levels of the configuration |
| `log tail [-n 100] [--level debug] [-f]` | last lines from memory; `-f` keeps following, like a console |
| `trace <id> [--minutes 15]` | follows **one** identity (8 hexadecimal characters or more); ends by itself, 60 minutes at most |
| `trace list` / `trace stop <id\|all>` | active traces / stop |

An identity's trace also tells **every step of a wake-up**: WAITING flag raised or received, a contact back online whom someone is waiting for (and the wake-up of the absent sender), gateway chosen and result, wake-up held back (one per 30 s and per identity), no token, no linked gateway, token dropped because the push network no longer knows it. With the trace of both identities of a conversation, the chain "letter waiting → wake-up → resend" reads in one go.

Logs hold metadata only: identifiers shortened to 8 characters, never content, keys or device tokens. "Who writes to whom" appears only at `debug` level or in a requested trace.

## 8. Checking the installation

`dotnet FreeSpeech.Relay.dll check` (without `admin`) says what is missing: `Admin:Secret` absent, too short or still the example value. Exit code 1 when something is
missing; the update script runs it at the end.

## 9. Common procedures

```bash
# Private server for a family
dotnet FreeSpeech.Relay.dll admin access allowlist
dotnet FreeSpeech.Relay.dll admin invite "Anna" --days 7      # give Anna the token, once

# Approve the people who asked for access
dotnet FreeSpeech.Relay.dll admin pending
dotnet FreeSpeech.Relay.dll admin accept 3fa9c1 b27e40
dotnet FreeSpeech.Relay.dll admin reject --all --older-than 7d --yes

# Diagnose without restarting
dotnet FreeSpeech.Relay.dll admin log level relay information
dotnet FreeSpeech.Relay.dll admin trace 3fa9c1d2 --minutes 10
dotnet FreeSpeech.Relay.dll admin log tail -f
dotnet FreeSpeech.Relay.dll admin log reset
```

## 10. Mail: SMTP and notifications

The server can write to its administrator. Everything is set by command (the same routes of the admin API serve any console) and applies at once. The account is saved in `appsettings.Local.json`
(`Smtp:*`, `Notify:*`), password included: it is never read back or shown. The file is rewritten at every change: its other keys are kept, its `//` comments are lost.

| Command | Effect |
|---|---|
| `smtp show` | current account and switches, without the password (`password_set`), with an example subject |
| `smtp set host\|port\|tls\|user\|from\|to\|prefix <value>` | `tls`: `starttls` (port 587), `implicit` (port 465) or `none`; `to`: one or more addresses separated by commas; empty `user`: no authentication |
| `smtp set password` | typed without echo, or read from standard input, or from `FS_SMTP_PASSWORD`; never as an argument |
| `smtp clear` | forgets the account and turns the notifications off |
| `smtp test [--to address]` | sends a mail now and **prints the answer of the SMTP server**: `ok: sent to …`, or `FAILED at <stage>: <message>` (stages `settings`, `connect`, `authenticate`, `send`). Exit code 0, or 4 when the mail did not go. An attempt takes `Smtp:TimeoutSeconds` seconds at most (20 by default): a host that does not answer gives `FAILED at connect: no answer from <host>:<port> within 20 s` |
| `notify` | the notification switches |
| `notify on\|off [peers\|abuse\|access\|system]` | all of them, or one kind. Notifications start off; every kind is active once `notify on` is given |
| `notify send "<message>"` | sends a mail to the administrator now (outside the one-hour limit) and prints the result like `smtp test` |

**Subject.** `<prefix> <event>`. The prefix is free (`smtp set prefix`), with the variables `{kind}` (`RELAY`) and `{host}` (the server: the host of `Peer:PublicUrl`, else the machine name).
Default `[{kind}] {host}`, for example `[RELAY] fs.example.org Link request waiting`; `smtp set prefix "[fs.example.org]"` gives `[fs.example.org] Link request waiting`.

**Kinds of notification** (no mail is sent for a kind that is off):

| Kind | When |
|---|---|
| `peers` | another server asks for a link: name, address, fingerprint to compare, then `accept`; a linked server that fails three checks in a row (`Link down`), then answers again (`Link restored`) |
| `abuse` | abuse or attack detected: an identity or address goes over its traffic limit, a linked server goes over its quota, forged or replayed signed calls, too many link requests from one address |
| `access` | private server in `allowlist` mode: a client asks for access and waits for your decision (`admin pending`, then `accept`) |
| `system` | at start-up, a secret or a setting is missing (the same alarm as the *Critical* log line) |

**Limit.** So that a burst of events does not become a burst of mails, a kind sends at most one mail per hour (`Notify:MinIntervalSeconds`, 3600): the first event goes at once, the
following ones are counted and sent together when the hour is over ("+N more"). A mail holds no client identifier, no client address, no token.

`check` reports an account that is half set (`Smtp:User` without `Smtp:Password`, notifications on without host, sender or recipient).

## 11. Backup and restore: `export` and `import`

| Command | Effect |
|---|---|
| `export <file> [--force]` | saves the server's state in a file **encrypted with a passphrase** (at least 8 characters; typed without echo, or `FS_STATE_PASSPHRASE`). Rights 600. Refuses to overwrite without `--force` |
| `dotnet FreeSpeech.Relay.dll import <file> [--force]` | **without `admin`, on the machine, with the service stopped**: puts the files back. Refuses to replace an existing state without `--force`, which keeps each replaced file next to it as `.before-import`. Exit code 0, 1 (usage) or 4 (wrong passphrase, file of another kind of server, existing state) |

The archive holds `siblings.json`, `access.json`, `settings.json`, `limits.json`, `peers.json` and `peer-identity.json`. It holds the server's private key, hence the encryption. It does **not** hold `appsettings.Local.json` (admin secret, SMTP account): write it again on the new machine.
To migrate: install the server following [INSTALL_RELAY.en.md](INSTALL_RELAY.en.md) without starting it, restore, set `appsettings.Local.json`, start.

```bash
dotnet FreeSpeech.Relay.dll admin export ~/relay.fsstate                    # old machine
sudo systemctl stop freespeech-relay                                  # new machine, service stopped
cd /var/www/freespeech_relay && sudo -u www-data env $(systemctl show freespeech-relay.service -p Environment --value) dotnet FreeSpeech.Relay.dll import ~/relay.fsstate
sudo systemctl start freespeech-relay
```

## 12. Service settings: `settings`

The service settings (`Server__…`) change while the server runs; the configuration gives the starting values, a change applies at once and is kept in `settings.json`
(`Server__SettingsFilePath`), and `settings reset` goes back to the configuration.

| Command | Effect |
|---|---|
| `settings` | the settings in force, those of the configuration and those that were changed |
| `settings set max-connections <n>` | beyond `n` connections, the server offers a sibling server (0: no limit) |
| `settings set max-message-bytes <bytes>` | maximum size of a message (1,024 to 67,108,864) |
| `settings set s2s-rate <n>` | requests per minute and per sibling server (1 to 1,000,000) |
| `settings set country <code>` | the two-letter country (ISO 3166) this relay tells the directories it is linked with (empty: none) |
| `settings set source-url <address>` | address of the source code, served by `GET /api/info` (empty: none) |
| `settings set max-access-requests <n>` / `access-request-days <days>` | number of access requests kept (private server) / days a request is kept |
| `settings reset` | back to the settings of the configuration |

## 13. How the server presents itself: `self`

| Command | Effect |
|---|---|
| `self` | kind, name, public address and fingerprint and public key of this server, as the other servers see it |
| `key` | the public key of this server alone, on one line, to copy where it is asked for |
| `self set name <value>` | the name shown to the other administrators (64 characters at most) |
| `self set public-url <value>` | the public address (`https://`, or `http://` on a private network; empty to clear it) |
| `self set contact <value>` | how another administrator can check who you are when you ask them for a link: a mail address, a phone number or a page (128 characters at most, empty to clear it); sent, signed, with every request |

The change applies at once (links, mail subject, `status`) and is written in `appsettings.Local.json` (`Peer:Name`, `Peer:PublicUrl`, `Peer:Contact`), so it is kept across restarts.
