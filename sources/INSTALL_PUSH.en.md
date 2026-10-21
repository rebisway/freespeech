# Installing a FreeSpeech push gateway (blank Debian / Ubuntu)

*Version française : [INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md). Other servers: [INSTALL_RELAY.en.md](INSTALL_RELAY.en.md). Several servers on one machine: [INSTALL_HOST.en.md](INSTALL_HOST.en.md). Protocol: [PROTOCOL.en.md](PROTOCOL.en.md) §9.*

The gateway (`freespeech.push`) is the only component that holds the Firebase (FCM) and Apple (APNs) keys. Relay servers call it to "wake" a phone; it receives neither identity nor content.
This guide installs it on a **blank** machine (Debian 12 or 13, Ubuntu 22.04 or 24.04), for any domain (below `push.example.org`).

| Item | Value in this guide |
|---|---|
| Domain | `push.example.org` |
| Program | `/var/www/freespeech_push` (with `appsettings.Local.json` and `secrets/`) |
| State | `/var/lib/freespeech-push` (`peer-identity.json`, `peers.json`, `settings.json`) |
| Local listening address | `127.0.0.1:5291` (nginx does the HTTPS) |
| Service | `freespeech-push.service`, user `www-data` |
| Deposit folder for the files to install | `~/ftp/push/publish` and `~/ftp/push/deploy` |

## 1. Before starting

- A Debian or Ubuntu machine with a user that has `sudo`; ports **80 and 443** reachable from the internet.
- A domain name whose DNS record (A, and AAAA if the machine has an IPv6 address) points to the machine.
- **Firebase**: project created, Android application `org.freespeech.mobile`, `google-services.json` in `sources/freespeech.mobile/` (not versioned), **Firebase Cloud Messaging API (V1)** enabled (Project settings → Cloud Messaging).
  Service account key: *Project settings → Service accounts → Generate new private key*. **Never version or share it**; if it was exposed, generate a new one and delete the old one.
- On the development computer: the .NET 10 SDK, the project repository and `scp`.
- **Order**: install the recent APK on the phones first, then update the relays.

## 2. Prepare the machine

Do this **once per machine**. Every command can be run again safely; on a machine that already hosts another FreeSpeech server, only check what is missing.

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y nginx certbot python3-certbot-nginx ufw curl
```

**Firewall** (allow SSH *before* enabling, or the session is cut):

```bash
sudo ufw allow OpenSSH && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
sudo ufw enable && sudo ufw status
```

Port 5291 stays local: never open it.

**ASP.NET Core 10 runtime** (`dotnet --list-runtimes` must show `Microsoft.AspNetCore.App 10.x`):

```bash
# Ubuntu
sudo add-apt-repository -y ppa:dotnet/backports && sudo apt install -y aspnetcore-runtime-10.0
# Debian: Microsoft repository (replace 12 by 13 on Debian 13), then the package
curl -sSL -O https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && sudo apt update && sudo apt install -y aspnetcore-runtime-10.0
dotnet --list-runtimes
```

**Deposit folder**:

```bash
mkdir -p ~/ftp/push/publish ~/ftp/push/deploy ~/ftp/push/secrets
```

## 3. Prepare the package (development computer)

```bash
dotnet publish sources/freespeech.push/FreeSpeech.Push.csproj -c Release -o sources/deploy/push/publish
```

In `sources/deploy/push/` (ignored by SVN: the package and the secrets are never versioned), next to `publish/` — never inside it, so that publishing again cannot overwrite anything:

| File | Role |
|---|---|
| `publish/` | the program |
| `secrets/firebase-service-account.json` | the service account key |
| `appsettings.Local.json` | **the only place for secrets** (template: `sources/deploy/push/deploy/appsettings.Local.push.example.json`) |

```json
{
  "Admin": { "Secret": "<openssl rand -hex 32>" },
  "Fcm":   { "ServiceAccountFile": "/var/www/freespeech_push/secrets/firebase-service-account.json" },
  "Peer":  { "PublicUrl": "https://push.example.org" }
}
```

`Admin:Secret` is the secret of the local admin API (without it, the `admin` command refuses everything); it belongs to this gateway alone, different from a relay's. An Apple key, if used, is set the same way (`Apns:*`, see the template).
A secret that is missing or left at the example value is reported at every start (log, *Critical* level) and by `dotnet FreeSpeech.Push.dll check`, which the update script runs.

## 4. Copy to the machine

```bash
scp -r sources/deploy/push/publish sources/deploy/push/secrets sources/deploy/push/appsettings.Local.json user@machine:ftp/push/
scp -r sources/deploy/push/deploy user@machine:ftp/push/
scp sources/deploy/install-shortcuts.sh user@machine:ftp/
```

## 5. Install the gateway

```bash
sudo mkdir -p /var/www/freespeech_push
sudo cp -r ~/ftp/push/secrets ~/ftp/push/appsettings.Local.json /var/www/freespeech_push/
sudo shred -u ~/ftp/push/secrets/* ~/ftp/push/appsettings.Local.json     # the secrets do not stay in the deposit folder
sudo cp ~/ftp/push/deploy/freespeech-push.service /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable freespeech-push
cp ~/ftp/push/deploy/update-freespeech-push.sh ~/ && chmod +x ~/update-freespeech-push.sh
sudo ~/update-freespeech-push.sh
```

The script reads the program from `~/ftp/push/publish`, copies it, makes `www-data` the owner, sets rights `600` on the configuration and `700` on `secrets/`, starts the service (local port `5291`), shows the log, then runs `check`.
The service file provides `Push__StatePath=/var/lib/freespeech-push` (created by systemd). **No secret goes in the service file.** With **no linked relay at all**, the gateway refuses every request.

## 6. nginx and HTTPS certificate

Example file: `sources/deploy/push/deploy/nginx-freespeech-push` (already copied to `~/ftp/push/deploy`). It limits the rate (the gateway is only called by relays), closes `/admin` and lets `/peer/` through.

```bash
sudo cp ~/ftp/push/deploy/nginx-freespeech-push /etc/nginx/sites-available/freespeech-push
sudo sed -i 's/push1.exemple.org/push.example.org/' /etc/nginx/sites-available/freespeech-push     # the domain of the example file
sudo ln -s /etc/nginx/sites-available/freespeech-push /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx && sudo certbot --nginx -d push.example.org
```

Certbot edits the file (TLS, redirect) and sets up automatic renewal. **Never copy the example file over it again** after certbot; repair: `sudo certbot --nginx -d push.example.org --reinstall`.

### If the machine already hosts other web servers

The file is an independent site: leave the other sites alone. To check or add:

- **`http`-level directive**: `limit_req_zone … zone=freespeech_push` at the top of the file; valid because a `sites-enabled` file is included inside `http {}`. If this content is merged into an existing file, declare it **only once**, before any `server`, under a name no other site uses.
- **`server_name`** specific to this site; at most one `listen … default_server` per port on the machine.
- **Certificate**: one `certbot --nginx -d <domain>` per domain; the renewal timer is shared.
- **Another front end** (Caddy, Traefik, haproxy…) already on 80/443: route this domain to `127.0.0.1:5291`, with `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`, and `/admin` closed (the gateway also refuses any call whose forwarded address is not local).
- **Reserved ports and names** of the other FreeSpeech servers: see [INSTALL_HOST.en.md](INSTALL_HOST.en.md).

## 7. Check

```bash
curl -s https://push.example.org/health              # {"ok":true}
sudo journalctl -u freespeech-push -n 20 --no-pager   # must say: Providers: fcm
```

## 8. Link a relay

No secret is shared: each server has its own key pair and the link needs the agreement of both administrators. On the **relay**: `admin add push https://push.example.org` (the gateway's fingerprint is shown).
On the **gateway** (in its folder, `www-data` reads the configuration):

```bash
sudo bash ~/ftp/install-shortcuts.sh   # once per machine: creates fsrelay, fspush and fsdir for the servers installed here
fspush requests        # the relay: name, address, fingerprint (compare with what its administrator reads)
fspush accept <id>
```

The relay knows within a minute (`admin list push` on the relay) and then uses the gateway, with signed calls. A third party deploying their own relay does the same, without sending you anything.
The gateway's public port must let `/peer/` through (nginx: nothing to change, only `/admin` is closed). Full commands: [cli-push.en.md](cli-push.en.md).

**Real test**: phones A and B on the relay; close the application on B (swipe it from recent applications); send a message from A: B shows the notification within seconds, without waiting for the 15-minute polling.

**Several gateways** (load, failure): install the same one on other machines (same Firebase credentials) and link each to the relay (`admin add push <url>`). The relay switches by itself (3 tries) and sets aside a failed or saturated gateway for a few tens of seconds.
`Push:MaxConcurrent` (default 100) limits a gateway's simultaneous requests.

## 9. Administer (without restarting)

```bash
fspush status                  # providers, counters, limits
fspush relays                  # linked relays and their counters
fspush ban <id>                # ban a relay (403); unban to bring it back
fspush maintenance on          # answers "busy": relays use another gateway, then restart without loss
fspush log tail -f             # live log (never a device token)
```

The admin API (`/admin/…`) is not published by nginx and the gateway refuses any call whose forwarded address is not local. See [ADMINISTRATION.en.md](ADMINISTRATION.en.md) §4.

## 10. Update

```bash
# development computer
dotnet publish sources/freespeech.push/FreeSpeech.Push.csproj -c Release -o sources/deploy/push/publish
scp -r sources/deploy/push/publish user@machine:ftp/push/
# machine
sudo ~/update-freespeech-push.sh
```

`appsettings.Local.json` and `secrets/` are not touched by an update.

## 11. Backup

`/var/lib/freespeech-push/peer-identity.json` (the gateway's key: losing it means linking each relay again), `peers.json` and `settings.json`, rights `600`; and `/var/www/freespeech_push/appsettings.Local.json` with `secrets/` (the Firebase/Apple keys can also be found at their consoles). To migrate or back up the state in one go: `admin export` and `import` ([cli-push.en.md](cli-push.en.md) §9).

## Troubleshooting

| Symptom | Lead |
|---|---|
| `Providers: none` | `appsettings.Local.json` missing or wrong key path; `www-data` rights |
| Every request refused (401) | the relay is not linked to the gateway (`admin list`, `admin requests`, `admin accept <id>`) |
| Requests refused (403) | this relay is banned: `admin unban <id>`, then a new request |
| Many "busy" (429) | maintenance enabled (`admin status`), or `max-concurrent` too low |
| `push_result.ok = false` at registration | the relay has no linked gateway (`admin list push` on the relay) |
| 503 from the gateway | platform without credentials (for example APNs not configured) |
| 410 | expired token: the relay deletes it, the phone sends a new one |
| `nginx -t`: "duplicate … zone" | the name `freespeech_push` is declared twice in the `sites-enabled` files |
| No notification, no error | the phone saves battery: exclude FreeSpeech from optimisation, check the notifications |
