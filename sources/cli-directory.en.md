# Directory command line

`dotnet FreeSpeech.Directory.dll admin <command>` administers a running directory, with no web interface and no restart: every change applies at once and the directory saves it.
Protocol: [PROTOCOL.en.md](PROTOCOL.en.md) §10 "Directory"; installation: [INSTALL_DIRECTORY.en.md](INSTALL_DIRECTORY.en.md).
Without `admin`, the same program starts the directory (`--urls http://127.0.0.1:5294`); `check` checks the installation and `import` restores a state (§6).

## 1. Principles

**Where to run.** On the directory's machine, in the program's folder, or from another computer through an SSH tunnel. The admin API only answers calls made from the machine itself and requires the admin secret; nginx does not publish it (`/admin` is closed). The script `sources/deploy/install-shortcuts.sh` creates the shortcut `fsdir` (runs `admin` as `www-data`, from any folder): `fsdir status`, or `fsdir` alone for the `directory>` prompt.

| Setting | Role |
|---|---|
| `--url <address>` or `FS_ADMIN_URL` | the directory's local address (default: `ASPNETCORE_URLS`, otherwise `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` | the admin secret. When absent, read from `Admin:Secret` in `appsettings.Local.json` in the program's folder |

| Exit code | Meaning |
|---|---|
| 0 | succeeded |
| 1 | wrong usage (unknown command, missing argument, confirmation missing in a script) |
| 2 | no admin secret available |
| 3 | directory unreachable |
| 4 | refused or failed (wrong secret, call not made from the machine, unknown or ambiguous id, check refused) |

**Identifiers.** A relay identifier is the 64-character hexadecimal fingerprint of its key; a **prefix of 6 characters or more** is accepted if it is unique. Without a command, an interactive prompt opens when a person is typing.

## 2. Relays offered

| Command | Effect |
|---|---|
| `status` | how many relays are linked, known (recent heartbeat) and offered to new accounts right now, and the settings in force |
| `servers` | the accepted relays that sent a heartbeat in the last `expiry-minutes` minutes: address, country, places left (`-1`: no limit), whether they take new accounts, relay version and date, address checked (`reachable`), last heartbeat, and `listed` (offered to clients right now) |

A relay is offered if it is linked, spoke recently, answers at its address with the accepted key, takes new accounts and has room. The directory knows neither a relay's number of users nor any client.

## 3. Settings changed while it runs

| Command | Effect |
|---|---|
| `settings` | the settings in force, those of the configuration and those that were changed |
| `settings set expiry-minutes <n>` | minutes without a heartbeat before a relay is dropped (1 to 1,440; 10 by default, `Directory__ExpiryMinutes`) |
| `settings set probe-minutes <n>` | minutes between checks of the relays' addresses (1 to 1,440; 30 by default, `Directory__ProbeMinutes`) |
| `settings set list-size <n>` | relays offered per answer when the client does not ask for a number (1 to 10; 3 by default, `Directory__ListSize`) |
| `settings reset` | back to the settings of the configuration |

Changes are kept in `settings.json` (state folder, `Directory__StatePath`, `directory-state` by default).

## 4. Linking with relays

Each server has its own key pair (`peer-identity.json`); a link exists only if **both** administrators accepted it. The kind (`relay`) may be left out.

| Command | Effect |
|---|---|
| `peer-id` | this directory's fingerprint, to compare with the other administrator. The public key shown by `GET /peer/identity` is the one the applications know (`FsDirectories`) |
| `add relay <url> [--fingerprint f] [--insecure]` | asks a relay for a link; its administrator accepts with `accept directory <id>` |
| `list [kind]` | every server linked (`linked`), asked (`asked`), refused (`refused`) or banned (`banned`), all kinds when none is given; for a relay, the `LINK` column gives the age of the last heartbeat received (`ok 35s`, `none yet` since the start; kept in memory); the country each relay declared in its last heartbeat is shown (`-` when it declared none); the contact its administrator declared is shown between `<` `>` |
| `requests [--name t] [--limit N] [--offset N] [--json]` | requests received from relays (name, address, fingerprint) Each request shows the contact the sender declares and the address it came from. |
| `accept <id…>` or `--all [--name t] [--older-than D] [--yes]` | accepts; refuses if the announced address does not show the key of the request (code 4) |
| `reject <id…>` or `--all … [--ban]` | turns down; `--ban` forbids asking again |
| `remove <id>` | forgets a relay (it leaves the lists; it may ask again) |
| `ban <id>` / `unban <id>` | bans / lifts the ban |
| `peer-requests open` / `close` | takes incoming requests or not |
| `peer-refresh` | asks now where the requests you sent stand |
| `peer-check [seconds]` | how often the linked relays are checked (10 to 3600; 60 by default, `Link:CheckSeconds`); without a value, shows it. A check is not sent when a heartbeat from that relay arrived within that time |

The directory's address (`Peer:PublicUrl`, see `self` below) is required. `Peer:Contact` is how another administrator can reach yours to check who is asking.

```bash
# Relay side: the relay asks the directory
dotnet FreeSpeech.Relay.dll admin add directory https://directory1.example.org
# Directory side: the directory's administrator accepts
dotnet FreeSpeech.Directory.dll admin requests
dotnet FreeSpeech.Directory.dll admin accept 3fa91c
dotnet FreeSpeech.Directory.dll admin servers
```

## 5. Mail: SMTP and notifications

The `smtp` and `notify` commands are the relay's ([cli-relay.en.md](cli-relay.en.md) §10): `smtp show`, `smtp set host|port|tls|user|from|to|prefix <value>`, `smtp set password`, `smtp clear`, `smtp test [--to address]`, `notify`, `notify on|off [kind]`, `notify send "<message>"`.
The subject is `<start> <event>`; the default start is `[DIRECTORY] <host>` (`{kind}` and `{host}` are replaced). Kinds of notification for the directory:

| Kind | When |
|---|---|
| `peers` | a relay asks for a link: name, address, fingerprint to compare, then `accept`; a linked relay that fails three checks in a row (`Link down`), then answers again (`Link restored`) |
| `abuse` | forged or replayed signed calls, too many link or list requests from one address |
| `system` | at start-up, a secret or a setting is missing |

A kind sends at most one mail per hour (`Notify:MinIntervalSeconds`), with no client address.

## 6. Identity, backup, version

| Command | Effect |
|---|---|
| `self` | kind, name, public address and fingerprint and public key of this directory |
| `key` | the public key of this directory alone, on one line: the value to put after `url|` in `FsDirectories` (`https://directory1.example.org|<key>`) |
| `self set name <value>` / `self set public-url <value>` | the name shown to the relays; the public address (`https://`); written in `appsettings.Local.json` and applied at once |
| `self set contact <value>` | how another administrator can check who you are when you ask them for a link: a mail address, a phone number or a page (128 characters at most, empty to clear it); sent, signed, with every request |
| `export <file> [--force]` | saves the state (key, accepted relays, settings) in a file **encrypted with a passphrase** (at least 8 characters; typed without echo, or `FS_STATE_PASSPHRASE`) |
| `dotnet FreeSpeech.Directory.dll import <file> [--force]` | **without `admin`, on the machine, with the service stopped**: puts the files back (`.before-import` keeps the replaced ones). Codes 0, 1 or 4 |
| `version` | version, release date, runtime and uptime |

The archive does not hold `appsettings.Local.json` (admin secret, SMTP account). To migrate without changing the address or the key: `export`, install on the new machine without starting, `import`, `appsettings.Local.json`, start, DNS.

## 7. Logs and tracing

| Command | Effect |
|---|---|
| `log` | current log levels |
| `log level <category\|all> <level>` | changes a level **at run time** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`); categories: `directory`, `http` |
| `log reset` | back to the levels of the configuration |
| `log tail [-n 100] [--level debug] [-f]` | last lines from memory; `-f` keeps following |
| `trace <id> [--minutes 15]` / `trace list` / `trace stop <id\|all>` | follows an identity (60 minutes at most) |

The directory logs no client address.

## 8. Checking the installation

`dotnet FreeSpeech.Directory.dll check` (without `admin`) lists what is missing: `Admin:Secret` absent, too short or left at the example value, `Peer:PublicUrl` absent, no relay accepted (when `Directory__StatePath` is reachable), a half-set SMTP account. Exit code 1 when something is missing. The same list is logged at every start at *Critical* level and mailed (kind `system`) when notifications are on. The update script runs it.
