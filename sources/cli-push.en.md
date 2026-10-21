# Push gateway command line

`dotnet FreeSpeech.Push.dll admin <command>` administers a running gateway, with no web interface and no restart: every change applies at once and the gateway saves it.
Concepts: [ADMINISTRATION.en.md](ADMINISTRATION.en.md) §4; installation: [INSTALL_PUSH.en.md](INSTALL_PUSH.en.md).
Without `admin`, the same program starts the gateway (`--urls http://127.0.0.1:5291`); `check` checks the installation.

## 1. Principles

**Where to run.** On the gateway's machine, in the program's folder, or from another computer through an SSH tunnel The script `sources/deploy/install-shortcuts.sh` creates the shortcut `fspush` (runs `admin` as `www-data`, from any folder): `fspush status`, or `fspush` alone for the `push>` prompt.
(`ssh -N -L 15291:127.0.0.1:5291 user@server`, then `--url http://127.0.0.1:15291`). The admin API only answers calls made from the machine itself and requires the
admin secret; nginx does not publish it (`/admin` is closed).

| Setting | Role |
|---|---|
| `--url <address>` or `FS_ADMIN_URL` | the gateway's local address (default: `ASPNETCORE_URLS`, otherwise `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` | the admin secret. When absent, read from `Admin:Secret` in `appsettings.Local.json` in the program's folder |

| Exit code | Meaning |
|---|---|
| 0 | succeeded |
| 1 | wrong usage (unknown command, missing argument, confirmation missing in a script) |
| 2 | no admin secret available |
| 3 | gateway unreachable |
| 4 | refused or failed (wrong secret, call not made from the machine, unknown or ambiguous id, check refused) |

**Identifiers.** A relay identifier is the 64-character hexadecimal fingerprint of its key. Commands that handle requests accept a **prefix of 6 characters or more** if it is
unique ("ambiguous" otherwise, code 4).

**Bulk handling** (`accept`, `reject`). With neither ids nor `--all`, nothing is done. `--all` takes the whole queue; `--name t` (name or address contains *t*) and
`--older-than N` (days, `7` or `7d`) narrow it. A `[y/N]` confirmation is asked at the keyboard; in a script (standard input redirected), `--yes` is required, otherwise
the command refuses (code 1).

**Interactive prompt.** `dotnet FreeSpeech.Push.dll admin` with no command opens a `push>` prompt (like `asterisk -r` or `mysql`): type the same commands there,
without repeating `admin`. `help` lists them; `exit`, `quit` or Ctrl+D leave. Up/Down walk through the history (200 lines, kept in `~/.freespeech_admin_history`); Left/Right, Home/End, Backspace and Delete edit the line, Ctrl+U clears it. An error does not end the session. Ctrl+C stops the running command (a followed log, an identity
trace) and keeps the prompt. At the prompt, `trace <id>` shows that identity's traffic live until Ctrl+C, which also ends the trace. In a script (input or output
redirected), the command with no argument prints the help and exits with code 1; `--shell` forces the prompt, reading the commands from standard input, one per line.

## 2. State and settings

| Command | Effect |
|---|---|
| `version` | version, runtime and uptime of the **running** gateway (not of the files on disk) |
| `status` | providers (fcm, apns), counters, limits, maintenance |
| `set min-interval <seconds>` | one wake-up per device token at most every *N* seconds (default 15) |
| `set max-concurrent <n>` | beyond *n* simultaneous wake-ups, answers "busy" (default 100) |
| `maintenance on` / `maintenance off` | answers "busy" to everything: relays use another gateway, a restart loses nothing |

## 3. Allowed relays

| Command | Effect |
|---|---|
| `relays` | linked relays, with their counters (sent, failed, busy, limited) |

## 4. Linking with a relay

Each server has its own key pair (`peer-identity.json`). A link exists only if **both** administrators accepted it; only the public key is exchanged.
A gateway links only with relays: the kind `relay` is optional in these commands.

| Command | Effect |
|---|---|
| `peer-id` | this gateway's fingerprint, to read out to the relay's administrator |
| `add relay <url> [--fingerprint f] [--insecure]` | asks a relay for a link; its fingerprint is shown (`--fingerprint` checks it). `https` required; `http` for this machine or a private network, or with `--insecure`. `Peer:PublicUrl` must be set |
| `list [kind]` | every server linked (`linked`), asked (`asked`), refused (`refused`) or banned (`banned`), all kinds when none is given; the contact its administrator declared is shown between `<` `>` |
| `requests [--name t] [--limit N] [--offset N] [--json]` | requests received (12-character id, date, name, address) Each request shows the contact the sender declares and the address it came from. |
| `accept <id…>` or `accept --all [--name t] [--older-than D] [--yes]` | accepts; refuses if the announced address does not show the key of the request (code 4) |
| `reject <id…>` or `reject --all … [--ban]` | turns down; `--ban` forbids asking again |
| `remove <id>` | forgets a relay (it may ask again) |
| `ban <id>` / `unban <id>` | bans / lifts the ban |
| `peer-requests open` / `close` | takes incoming requests or not |
| `peer-refresh` | asks now where the requests you sent stand, and whether linked relays still know this gateway |

Settings (not secrets, in `appsettings.Local.json`): `Peer:Name` (the name presented), `Peer:Contact` (how another administrator can reach yours to check who is asking) and `Peer:PublicUrl` (this gateway's address: required to ask a relay).

```bash
# A relay asks; the gateway accepts
dotnet FreeSpeech.Push.dll admin requests
dotnet FreeSpeech.Push.dll admin accept 3fa9c1          # prefix of the fingerprint read in "requests"

# The gateway asks a relay; the relay accepts (relay side: see cli-relay.en.md)
dotnet FreeSpeech.Push.dll admin add relay https://relay.example.org
```

## 5. Logs and tracing

| Command | Effect |
|---|---|
| `log` | current log levels |
| `log level <category\|all> <level>` | changes a level **at run time** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`). Categories: `push`, `relays`, `fcm`, `apns`, `http` |
| `log reset` | back to the levels of the configuration |
| `log tail [-n 100] [--level debug] [-f]` | last lines from memory; `-f` keeps following |
| `trace <id> [--minutes 15]` / `trace list` / `trace stop <id\|all>` | follows an identity (60 minutes at most) / active traces / stop |

Logs hold metadata only: no device token, no content.

## 6. Checking the installation

`dotnet FreeSpeech.Push.dll check` (without `admin`) says what is missing: `Admin:Secret` absent, too short or still the example value; the Firebase service-account file
(`Fcm:ServiceAccountFile`) absent or not found; no relay allowed. Exit code 1 when something is missing; the update script runs it at the end.

## 7. Common procedures

```bash
# Allow a relay
dotnet FreeSpeech.Push.dll admin peer-id                 # read the fingerprint to the relay's administrator
dotnet FreeSpeech.Push.dll admin requests
dotnet FreeSpeech.Push.dll admin accept 3fa9c1

# Put the gateway in maintenance, then back
dotnet FreeSpeech.Push.dll admin maintenance on
dotnet FreeSpeech.Push.dll admin maintenance off
```

## 8. Mail: SMTP and notifications

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

**Subject.** `<prefix> <event>`. The prefix is free (`smtp set prefix`), with the variables `{kind}` (`PUSH`) and `{host}` (the server: the host of `Peer:PublicUrl`, else the machine name).
Default `[{kind}] {host}`, for example `[PUSH] push.example.org Link request waiting`; `smtp set prefix "[fs.example.org]"` gives `[fs.example.org] Link request waiting`.

**Kinds of notification** (no mail is sent for a kind that is off):

| Kind | When |
|---|---|
| `peers` | another server asks for a link: name, address, fingerprint to compare, then `accept` |
| `abuse` | abuse or attack detected: forged or replayed signed calls, too many link requests from one address |
| `access` | not applicable to a push gateway (it has no clients) |
| `system` | at start-up, a secret or a setting is missing (the same alarm as the *Critical* log line) |

**Limit.** So that a burst of events does not become a burst of mails, a kind sends at most one mail per hour (`Notify:MinIntervalSeconds`, 3600): the first event goes at once, the
following ones are counted and sent together when the hour is over ("+N more"). A mail holds no client identifier, no client address, no token.

`check` reports an account that is half set (`Smtp:User` without `Smtp:Password`, notifications on without host, sender or recipient).

## 9. Backup and restore: `export` and `import`

| Command | Effect |
|---|---|
| `export <file> [--force]` | saves the server's state in a file **encrypted with a passphrase** (at least 8 characters; typed without echo, or `FS_STATE_PASSPHRASE`). Rights 600. Refuses to overwrite without `--force` |
| `dotnet FreeSpeech.Push.dll import <file> [--force]` | **without `admin`, on the machine, with the service stopped**: puts the files back. Refuses to replace an existing state without `--force`, which keeps each replaced file next to it as `.before-import`. Exit code 0, 1 (usage) or 4 (wrong passphrase, file of another kind of server, existing state) |

The archive holds `settings.json`, `peers.json` and `peer-identity.json`. It holds the server's private key, hence the encryption. It does **not** hold `appsettings.Local.json` (admin secret, SMTP account): write it again on the new machine.
To migrate: install the server following [INSTALL_PUSH.en.md](INSTALL_PUSH.en.md) without starting it, restore, set `appsettings.Local.json`, start.

```bash
dotnet FreeSpeech.Push.dll admin export ~/push.fsstate                    # old machine
sudo systemctl stop freespeech-push                                  # new machine, service stopped
cd /var/www/freespeech_push && sudo -u www-data env $(systemctl show freespeech-push.service -p Environment --value) dotnet FreeSpeech.Push.dll import ~/push.fsstate
sudo systemctl start freespeech-push
```

## 10. How the server presents itself: `self`

| Command | Effect |
|---|---|
| `self` | kind, name, public address and fingerprint and public key of this server, as the other servers see it |
| `key` | the public key of this server alone, on one line, to copy where it is asked for |
| `self set name <value>` | the name shown to the other administrators (64 characters at most) |
| `self set public-url <value>` | the public address (`https://`, or `http://` on a private network; empty to clear it) |
| `self set contact <value>` | how another administrator can check who you are when you ask them for a link: a mail address, a phone number or a page (128 characters at most, empty to clear it); sent, signed, with every request |

The change applies at once (links, mail subject, `status`) and is written in `appsettings.Local.json` (`Peer:Name`, `Peer:PublicUrl`, `Peer:Contact`), so it is kept across restarts.
