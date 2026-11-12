# Installing a FreeSpeech relay server (blank Debian / Ubuntu)

*Version française : [INSTALL_RELAY.fr.md](../fr/INSTALL_RELAY.fr.md). Other servers: [INSTALL_PUSH.en.md](INSTALL_PUSH.en.md). Several servers on one machine: [INSTALL_HOST.en.md](INSTALL_HOST.en.md). Protocol: [PROTOCOL.en.md](PROTOCOL.en.md).*

This guide installs **one** relay server on a **blank** machine (Debian 12 or 13, Ubuntu 22.04 or 24.04), for any domain (below `fs.example.org`). The relay stores no message: it connects clients that are online and
keeps in memory only who is online and a few waiting markers. The only files worth a backup are the administration ones (§12). Once installed, everything is set **without restarting** with the `admin` command
([ADMINISTRATION.en.md](ADMINISTRATION.en.md), [cli-relay.en.md](cli-relay.en.md)).

| Item | Value in this guide |
|---|---|
| Domain | `fs.example.org` |
| Program | `/var/www/freespeech_relay` |
| State | `/var/lib/freespeech` (`siblings.json`, `access.json`, `peers.json`, `peer-identity.json`) |
| Local listening address | `127.0.0.1:5290` (nginx does the HTTPS) |
| Service | `freespeech-relay.service`, user `www-data` |
| Deposit folder for the files to install | `~/ftp/relay/publish` and `~/ftp/relay/deploy` (in the home of the administering user) |

## Installing from the downloaded archive

This path does in one command what § 2 to 6 describe; the rest of the guide details every step, for those who prefer to install by hand or want to understand what the script does. Both paths give the same result.

1. Download to the machine, from the releases page of the repository (https://git.rebisway.com/rebisway/freespeech/releases), the archive `freespeech-relay-<version>.tar.gz` and the `SHA256SUMS` file.
2. Check the archive: `sha256sum -c --ignore-missing SHA256SUMS` must answer `OK`.
3. Extract and run the script, with the domain name already pointing to the machine (§ 1):

```bash
tar -xzf freespeech-relay-<version>.tar.gz
cd freespeech-relay-<version>
sudo bash install.sh fs.example.org --email admin@example.org
```

The script installs the packages and the runtime, opens the firewall (SSH, 80, 443), copies the program to `/var/www/freespeech_relay`, installs the service, writes `appsettings.Local.json` with a randomly drawn admin secret, configures nginx, requests the certificate, runs `check` and starts the service only if `check` passes. `--email` is the address Let's Encrypt keeps for expiry notices: giving it accepts the Let's Encrypt subscriber agreement. Options: `--contact <text>` (how another administrator can reach you, written in `Peer:Contact`), `--no-certificate` (no certificate request: a server on a local network, see the end of § 5, or a certificate obtained otherwise) and `--no-firewall`.

Running the script with a newer archive updates the program: `appsettings.Local.json` is never rewritten.

## 1. Before starting

- A Debian or Ubuntu machine with a user that has `sudo` (the "machine" commands are run as that user); ports **80 and 443** reachable from the internet (for a server that stays on the local network, see the end of § 5).
- A domain name whose DNS record (A, and AAAA if the machine has an IPv6 address) points to the machine. Certbot (§5) fails until the DNS has propagated.
- On the development computer (the one that builds):
  - the **.NET 10 SDK**: on Windows `winget install Microsoft.DotNet.SDK.10`, on Ubuntu `sudo apt install -y dotnet-sdk-10.0` (after adding the `ppa:dotnet/backports` repository, § 2). `dotnet --version` must show `10.x`;
  - **Git** (on Windows `winget install Git.Git`, on Ubuntu `sudo apt install -y git`) and the **project sources**:

    ```bash
    git clone https://git.rebisway.com/rebisway/freespeech
    cd freespeech
    ```

    `git clone` downloads the whole repository into a `freespeech` folder; `cd` moves into it. Every `sources/…` command in this guide is run from that folder, the root of the repository. The same repository is published as a mirror at https://github.com/rebisway/freespeech, to use if the first does not answer. To update later: `git pull`;
  - `scp` and `ssh` (OpenSSH clients, included in Windows 10 and later, Linux and macOS).

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

What these commands do:

- `apt update && apt upgrade -y` refreshes the package list, then installs the machine's security updates;
- `nginx` receives the HTTPS connections from the internet and passes them to the relay (§ 5); `certbot` obtains and renews the free Let's Encrypt certificate, and its `python3-certbot-nginx` module writes the TLS part of the nginx configuration itself; `ufw` is the firewall; `curl` is used for the checks;
- `ufw allow OpenSSH` keeps SSH access open, hence the order: enabling the firewall first would cut the session. `80` is used for the certificate validation and the redirect to HTTPS, `443` for the clients' traffic.

Port 5290 stays local: never open it.

**ASP.NET Core 10 runtime**: the relay is a .NET program that the machine runs with `dotnet`. It only needs the runtime, not the SDK (`dotnet --list-runtimes` must show `Microsoft.AspNetCore.App 10.x`):

```bash
# Ubuntu
sudo add-apt-repository -y ppa:dotnet/backports && sudo apt install -y aspnetcore-runtime-10.0
# Debian: Microsoft repository (replace 12 by 13 on Debian 13), then the package
curl -sSL -O https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && sudo apt update && sudo apt install -y aspnetcore-runtime-10.0
dotnet --list-runtimes
```

**Deposit folder** (where the files to install are dropped; `mkdir -p` creates the folders if they do not exist):

```bash
mkdir -p ~/ftp/relay/publish ~/ftp/relay/deploy
```

## 3. Publish and copy (development computer)

`dotnet publish` builds the project and gathers, in one folder, everything the machine has to run:

- `sources/freespeech.relay/FreeSpeech.Relay.csproj` is the project to build;
- `-c Release` is the production configuration;
- `-o sources/deploy/relay/publish` is the output folder. It holds `FreeSpeech.Relay.dll`, its libraries and `appsettings.json`, never `appsettings.Local.json` (§ 4);
- `scp -r` copies a folder to the machine over SSH; `user@machine:ftp/relay/publish/` is a path relative to the home folder of `user`. The three copies drop the program, the deployment files (systemd service, nginx example, update scripts and configuration template) and the script of the `fsrelay`, `fspush`, `fsdir` shortcuts.

```bash
dotnet publish sources/freespeech.relay/FreeSpeech.Relay.csproj -c Release -o sources/deploy/relay/publish
scp -r sources/deploy/relay/publish/* user@machine:ftp/relay/publish/
scp -r sources/deploy/relay/deploy user@machine:ftp/relay/
scp sources/deploy/install-shortcuts.sh user@machine:ftp/
```

## 4. Install the service and the secret

```bash
sudo cp ~/ftp/relay/deploy/freespeech-relay.service /etc/systemd/system/
cp ~/ftp/relay/deploy/update-freespeech-relay.sh ~/ && chmod +x ~/update-freespeech-relay.sh
sudo systemctl daemon-reload && sudo systemctl enable freespeech-relay

# The admin secret: a file next to the program, never in the service file nor in a publication
sudo mkdir -p /var/www/freespeech_relay
sudo tee /var/www/freespeech_relay/appsettings.Local.json >/dev/null <<EOF
{ "Admin": { "Secret": "$(openssl rand -hex 32)" } }
EOF
sudo chown www-data:www-data /var/www/freespeech_relay/appsettings.Local.json && sudo chmod 600 /var/www/freespeech_relay/appsettings.Local.json

sudo ~/update-freespeech-relay.sh      # copies the program, sets the rights, starts the service, shows the log and runs `check`
curl http://127.0.0.1:5290/api/time     # {"utc":"..."}
```

The log must say `Now listening on: http://127.0.0.1:5290`, with no error. `appsettings.Local.json` is **never** overwritten by an update (the publication does not contain it); `appsettings.json` is
replaced: write no secret in it. Without `Admin:Secret`, **every** admin route refuses. A secret that is missing, shorter than 16 characters or left at the example value
(`sources/deploy/relay/deploy/appsettings.Local.relay.example.json`) is reported at every start (log, *Critical* level) and by `dotnet FreeSpeech.Relay.dll check` (exit code 1), which the update script runs.

**No secret is shared between servers.** Links relay ↔ push gateway and relay ↔ relay rest on key pairs and on the agreement of both administrators (§8). The service file provides:
local listening address `5290`, state in `/var/lib/freespeech` (created by systemd), logs reduced to warnings (so that nobody records who talks to whom).

## 5. nginx and HTTPS certificate

Example file: `sources/deploy/relay/deploy/nginx-freespeech-relay` (already copied to `~/ftp/relay/deploy`). It provides: the WebSocket relay (`/ws/relay`, 1 h read timeout — **without it, nginx cuts idle connections after 60 s**),
per-address connection and rate limits, no access log, `/api/admin` closed from the internet and `client_max_body_size 1m`.

```bash
sudo cp ~/ftp/relay/deploy/nginx-freespeech-relay /etc/nginx/sites-available/freespeech-relay
sudo sed -i 's/relay1.exemple.org/fs.example.org/' /etc/nginx/sites-available/freespeech-relay     # the domain of the example file
sudo ln -s /etc/nginx/sites-available/freespeech-relay /etc/nginx/sites-enabled/freespeech-relay
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d fs.example.org
sudo nginx -t && sudo systemctl reload nginx
```

Certbot edits the file (TLS block, http → https redirect) and sets up automatic renewal (`systemctl list-timers | grep certbot`). **Never copy `nginx-freespeech-relay` over it again** after certbot:
the certificate would be lost; to add a block, edit the file in place. Repair: `sudo certbot --nginx -d fs.example.org --reinstall`. On a blank machine, `/etc/nginx/sites-enabled/default` may stay or be removed.

### On a local network (server not exposed to the internet)

A private server meant for a company, association or family network does not have to be reachable from the internet. The applications do require a **valid** HTTPS certificate, though: they refuse a self-signed one, and Android accepts plain HTTP only towards `127.0.0.1`. The way that works without changing anything in the applications is a real domain name with a certificate obtained by **DNS** validation:

1. At the domain's provider, create the `A` record of `fs.example.org` with the machine's **private** address (for example `192.168.1.20`). Some routers block DNS answers that point to a private address: in that case, declare the name in the router's resolver.
2. Obtain the certificate by DNS validation, which needs no access from the internet. With the provider's certbot module (for example `python3-certbot-dns-cloudflare`, with a credentials file in mode `600`), renewal is automatic:

```bash
sudo certbot certonly --dns-cloudflare --dns-cloudflare-credentials /root/cloudflare.ini -d fs.example.org
sudo certbot install --nginx --cert-name fs.example.org
```

3. In the firewall, open 443 to the local network only (80 is not needed): `sudo ufw allow from 192.168.1.0/24 to any port 443 proto tcp`.
4. Set the relay to `private` and `access allowlist` (§ 8).

Only the devices on the local network reach the server; the other servers in the contact's list take over (see [HOW_IT_WORKS.en.md](HOW_IT_WORKS.en.md) § 4).

### If the machine already hosts other web servers

The file is an independent site (one file in `sites-available`, one link in `sites-enabled`): leave the other sites alone. Points to check or add:

- **`http`-level directives**: the `map` and the two zones (`limit_conn_zone`, `limit_req_zone`) at the top of the file are named `freespeech_*`. A `sites-enabled` file is included inside `http {}`, so they are valid there. If this content is merged into an
  existing file, declare them **only once**, before any `server`, under names no other site uses (nginx refuses a duplicate name).
- **`server_name`** specific to this site; at most one `listen … default_server` per port on the machine.
- **Certificate**: one `certbot --nginx -d <domain>` per domain; the renewal timer is shared.
- **Another front end** (Caddy, Traefik, haproxy…) already on 80/443: add for this domain a route to `127.0.0.1:5290` with WebSocket upgrade for `/ws/relay` (1 h read and send timeouts, no buffering), the `Host`,
  `X-Forwarded-For` and `X-Forwarded-Proto` headers, and `/api/admin` closed. The relay only believes these headers when they come from the machine itself.
- **Reserved ports and names** of the other FreeSpeech servers: see [INSTALL_HOST.en.md](INSTALL_HOST.en.md).

## 6. Check

```bash
curl -i https://fs.example.org/api/time          # 200 and {"utc":"..."} (GET: HEAD gives 405)
sudo journalctl -u freespeech-relay -f          # live
```

In the application: *Settings → Servers → add* `https://fs.example.org`; the state becomes "connected".

## 7. Administer: the `admin` command

The admin API is **not** exposed on the internet: it requires the secret **and** a call made from the machine itself (nginx closes `/api/admin`). On the machine, in the program's folder (the secret is read from `appsettings.Local.json`, readable by `www-data` only):

```bash
sudo bash ~/ftp/install-shortcuts.sh   # once per machine: creates fsrelay, fspush and fsdir for the servers installed here
fsrelay status
fsrelay help          # all the commands
```

From a computer: `ssh -L 5290:127.0.0.1:5290 user@machine`, then the same command with `--url http://127.0.0.1:5290` and the secret in `FS_ADMIN_SECRET`. Everything applies at once.

## 8. Public or private, access, links with other servers

Two **separate** settings:

- **Public / private** (exchanges **between servers**). **Private** (default): no exchange with other servers. **Public**: **sibling** servers (S2S) to reach the contacts of another server and share the load.
- **Access mode** (who may **register**). `open` (default): everybody not banned. `allowlist`: only identities that are **allowed** or **invited**.

A company, association or family server combines *private* and *allowlist*:

```bash
fsrelay private
fsrelay access allowlist
fsrelay invite "Anna" --days 7      # single-use token, shown once: give it to Anna
```

Anna adds the server's address in *Settings → Servers*, then enters the token (*I have an invitation*) or chooses *Request access*. For one person: `ban <id>`, `allow <id>`, `clients`.

Public server, with a push gateway and sibling relays:

```bash
fsrelay public
fsrelay peer-id                                        # fingerprint to read to the other administrator
fsrelay add push https://push.example.org              # the gateway accepts (admin accept <id>): see INSTALL_PUSH
fsrelay settings set country FR                        # the country the directory offers to clients
fsrelay add directory https://directory1.example.org   # the directory accepts (admin accept <id>): see INSTALL_DIRECTORY
fsrelay add relay https://other.example.net            # the other administrator accepts
fsrelay server-ban https://bad.example.net "abuse"     # ban a server
```

A link exists only if **both** administrators accepted; the address must be `https` and not local; every request between servers is signed. `Peer:Name`, `Peer:PublicUrl` and `Peer:Contact` (how the other administrator can reach you to check who asks for a link) are set in `appsettings.Local.json`
(template: `sources/deploy/relay/deploy/appsettings.Local.relay.example.json`). Full commands: [cli-relay.en.md](cli-relay.en.md).

## 9. Useful settings (service variables, `Environment=…`)

| Variable | Default | Role |
|---|---|---|
| `Admin:Secret` (in `appsettings.Local.json`) | empty (admin refused) | secret of the admin API |
| `Server__MaxConnections` | 0 (unlimited) | beyond it, the server offers clients a sibling server that has room |
| `Server__MaxMessageBytes` | 524288 | maximum size of a message (512 KiB) |
| `Server__MaxBytesPerIdentityPerDay` | 1073741824 (1 GiB) | bytes an identity may send over a sliding 24 hours (0 = no limit) |
| `Server__MaxBytesPerIdentityPer10Minutes` | 314572800 (300 MiB) | rate of an identity: faster is slowed down (0 = no limit) |
| `Server__MaxBytesPerAddressPerDay` | 5368709120 (5 GiB) | bytes all the identities behind one address may send over a sliding 24 hours (0 = no limit) |
| `Server__SettingsFilePath` | `settings.json` | where the settings changed with `admin settings set` are kept (the variables of this table are the starting values; back up) |
| `Server__LimitsFilePath` | `limits.json` | where the limits changed with `admin limits set` are kept (the three settings above are the starting values) |
| `Server__S2SRatePerMinute` | 1200 | request quota per sibling server and per minute |
| `Server__SiblingsFilePath` | `siblings.json` | public/private status, suggested neighbours, banned addresses (back up) |
| `Server__AccessFilePath` | `access.json` | access mode, allowed, banned, invitations (back up) |
| `Server__ClientAccess` | `open` | starting mode of a new server: `open` or `allowlist` (then: `admin access`) |
| `Server__SourceUrl` | empty | address of the source code, served by `GET /api/info`: the AGPL (art. 13) requires offering it to the users of a modified version run as a network service |
| `Admin__AllowRemote` | false | accept administration from somewhere other than the machine: **private network only** |
| `Logging__LogLevel__FreeSpeech` | `Warning` | starting level; to diagnose: `admin log level relay information` **live**, then `admin log reset` |
| `Server__AllowAnonymousRegister`, `Server__AllowInsecureSiblings` | false | **development and tests only, never in production** |

After editing the service file: `sudo systemctl daemon-reload && sudo systemctl restart freespeech-relay`.

## 10. Update

```bash
# development computer
dotnet publish sources/freespeech.relay/FreeSpeech.Relay.csproj -c Release -o sources/deploy/relay/publish
scp -r sources/deploy/relay/publish/* user@machine:ftp/relay/publish/
# machine
sudo cp -r /var/www/freespeech_relay /var/www/freespeech_relay.bak      # to be able to go back
sudo ~/update-freespeech-relay.sh
```

Waiting markers and wake-up tokens are in memory: a restart erases them, clients reconnect and renew them, and the sender's outbox resends what was waiting. Update outside busy hours.
**Order**: install the new application on the phones first, then update the server; two sibling servers are updated together.
Rollback: `sudo systemctl stop freespeech-relay`, put the `.bak` folder back, `sudo systemctl start freespeech-relay`.

## 11. Push gateway

Waking phones goes through a push gateway, installed separately ([INSTALL_PUSH.en.md](INSTALL_PUSH.en.md)) and then linked to this relay (§8).

## 12. Backup

Only `/var/lib/freespeech/siblings.json` (public/private status, neighbours, banned addresses), `/var/lib/freespeech/settings.json` (settings changed while it runs), `/var/lib/freespeech/access.json` (access mode, allowed, banned, invitation fingerprints), `peers.json` and `peer-identity.json` (this relay's key:
losing it means linking the relay to its partners again) are worth a copy; rights `600`. The admin secret is in `/var/www/freespeech_relay/appsettings.Local.json`. To migrate or back up in one go: `admin export` and `import` ([cli-relay.en.md](cli-relay.en.md) §11).

## Troubleshooting

| Symptom | Lead |
|---|---|
| Phones reconnecting in a loop | `/ws/relay` block missing or overwritten in the nginx file |
| HTTPS with a wrong certificate | nginx file copied over certbot's: `certbot --nginx -d … --reinstall` |
| `nginx -t`: "duplicate … zone" or "duplicate map" | the name `freespeech_*` is declared twice in the `sites-enabled` files |
| nginx 502 | the service is stopped: `systemctl status freespeech-relay`, `journalctl -u freespeech-relay -n 50` |
| 404 on `/api/admin/...` from the internet | expected: use the `admin` command or the SSH tunnel |
| 401 on `/api/admin/...` | `Admin:Secret` missing from `appsettings.Local.json` (run `check`) or different, or a call that does not come from the machine |
| A user sees "private server" | `allowlist` mode: give an invitation (`admin invite`), allow them (`admin allow <id>`), or approve their access request (`admin pending`, `admin accept <id>`) |
| A sibling server always answers 404 | server not public, clocks more than 2 minutes apart, or link not accepted on both sides |
