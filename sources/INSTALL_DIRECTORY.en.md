# Installing a FreeSpeech directory (blank Debian / Ubuntu)

*Version française : [INSTALL_DIRECTORY.fr.md](INSTALL_DIRECTORY.fr.md). Other servers: [INSTALL_RELAY.en.md](INSTALL_RELAY.en.md), [INSTALL_PUSH.en.md](INSTALL_PUSH.en.md). Several servers on one machine: [INSTALL_HOST.en.md](INSTALL_HOST.en.md). Protocol: [PROTOCOL.en.md](PROTOCOL.en.md) §10 "Directory". Commands: [cli-directory.en.md](cli-directory.en.md).*

A directory (`freespeech.directory`) tells a client that knows no relay which relays take new accounts, and spreads newcomers over them. It stores nothing about clients and sees no message.
This guide installs it on a **blank** machine (Debian 12 or 13, Ubuntu 22.04 or 24.04), for any domain (below `directory1.example.org`).

| Item | Value in this guide |
|---|---|
| Domain | `directory1.example.org` |
| Program | `/var/www/freespeech_directory` (with `appsettings.Local.json`) |
| State | `/var/lib/freespeech-directory` (`peer-identity.json`, `peers.json`, `settings.json`) |
| Local listening address | `127.0.0.1:5294` (nginx does the HTTPS) |
| Service | `freespeech-directory.service`, user `www-data` |
| Deposit folder for the files to install | `~/ftp/directory/publish` and `~/ftp/directory/deploy` |

## 1. Before starting

- A Debian or Ubuntu machine with a user that has `sudo`; ports **80 and 443** reachable from the internet.
- A domain name whose DNS record (A, and AAAA if the machine has an IPv6 address) points to the machine. **The domain belongs to you and does not depend on the host**: the directory's identity is its key and its address, which the applications know; to move it, copy the key and change the DNS.
- On the development computer: the .NET 10 SDK, the project repository and `scp`.
- **Several directories**: follow this guide on another machine, in another country if possible, with its own domain (`directory2.example.org`) and its own key. Directories do not synchronise: each relay links to each of them.

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

Port 5294 stays local: never open it.

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
mkdir -p ~/ftp/directory/publish ~/ftp/directory/deploy
```

## 3. Publish and copy (development computer)

```bash
dotnet publish sources/freespeech.directory/FreeSpeech.Directory.csproj -c Release -o sources/deploy/directory/publish
scp -r sources/deploy/directory/publish/* user@machine:ftp/directory/publish/
scp -r sources/deploy/directory/deploy user@machine:ftp/directory/
scp sources/deploy/install-shortcuts.sh user@machine:ftp/
```

## 4. Install the service and the secret

```bash
sudo cp ~/ftp/directory/deploy/freespeech-directory.service /etc/systemd/system/
cp ~/ftp/directory/deploy/update-freespeech-directory.sh ~/ && chmod +x ~/update-freespeech-directory.sh
sudo systemctl daemon-reload && sudo systemctl enable freespeech-directory

# The admin secret and the public address: a file next to the program, never in the service file nor in a publication
sudo mkdir -p /var/www/freespeech_directory
sudo tee /var/www/freespeech_directory/appsettings.Local.json >/dev/null <<EOF
{ "Admin": { "Secret": "$(openssl rand -hex 32)" }, "Peer": { "PublicUrl": "https://directory1.example.org", "Name": "directory1" } }
EOF
sudo chown www-data:www-data /var/www/freespeech_directory/appsettings.Local.json && sudo chmod 600 /var/www/freespeech_directory/appsettings.Local.json

sudo ~/update-freespeech-directory.sh      # copies the program, sets the rights, starts the service, shows the log and runs `check`
curl http://127.0.0.1:5294/health          # {"ok":true}
```

`appsettings.Local.json` is **never** overwritten by an update. Without `Admin:Secret`, every admin route refuses; a secret that is missing, too short or left at the example value (`sources/deploy/directory/deploy/appsettings.Local.directory.example.json`) is reported at every start (log, *Critical* level) and by `dotnet FreeSpeech.Directory.dll check`, which the update script runs.
`Peer:PublicUrl` is the https address of the directory: relays use it to check who asks them for a link.
**No secret is shared between servers**: relays link to the directory by keys and by the agreement of both administrators (§8).

**The directory's key** (`/var/lib/freespeech-directory/peer-identity.json`) is created at the first start. **Back it up offline right away**: the applications are built with its public key, and a new key would mean republishing the applications. Its public key is shown by:

```bash
dotnet FreeSpeech.Directory.dll admin key          # the public key alone: the value to give to the applications' build (FsDirectories)
```

## 5. nginx and HTTPS certificate

Example file: `sources/deploy/directory/deploy/nginx-freespeech-directory` (already copied to `~/ftp/directory/deploy`). It limits the rate per address, **writes no access log** (the directory keeps no client address) and closes `/admin`.

```bash
sudo cp ~/ftp/directory/deploy/nginx-freespeech-directory /etc/nginx/sites-available/freespeech-directory
sudo sed -i 's/directory1.exemple.org/directory1.example.org/' /etc/nginx/sites-available/freespeech-directory     # the domain of the example file
sudo ln -s /etc/nginx/sites-available/freespeech-directory /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx && sudo certbot --nginx -d directory1.example.org
```

Certbot edits the file (TLS, redirect) and sets up automatic renewal. **Never copy the example file over it again** after certbot; repair: `sudo certbot --nginx -d directory1.example.org --reinstall`.

### If the machine already hosts other web servers

The file is an independent site: leave the other sites alone. To check or add:

- **`http`-level directive**: `limit_req_zone … zone=freespeech_directory` at the top of the file; valid because a `sites-enabled` file is included inside `http {}`. If this content is merged into an existing file, declare it **only once**, before any `server`, under a name no other site uses.
- **`server_name`** specific to this site; at most one `listen … default_server` per port on the machine.
- **Certificate**: one `certbot --nginx -d <domain>` per domain; the renewal timer is shared.
- **Access log**: do not turn it on for this site, it would record the address of every client that asks for relays.
- **Another front end** (Caddy, Traefik, haproxy…) already on 80/443: route this domain to `127.0.0.1:5294`, with `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`, and `/admin` closed (the directory also refuses any admin call whose forwarded address is not local).
- **Reserved ports and names** of the other FreeSpeech servers: see [INSTALL_HOST.en.md](INSTALL_HOST.en.md).

## 6. Check

```bash
curl -s https://directory1.example.org/health                       # {"ok":true}
curl -s "https://directory1.example.org/directory/servers?country=FR"    # a signed list; empty until a relay is linked
sudo journalctl -u freespeech-directory -n 20 --no-pager
```

## 7. Administer: the `admin` command

The admin API is **not** exposed on the internet: it requires the secret **and** a call made from the machine itself. On the machine, in the program's folder (the secret is read from `appsettings.Local.json`, readable by `www-data` only):

```bash
sudo bash ~/ftp/install-shortcuts.sh   # once per machine: creates fsrelay, fspush and fsdir for the servers installed here
fsdir servers          # the relays it knows: country, places left, version, whether they are offered
fsdir help             # all the commands
```

From a computer: `ssh -L 5294:127.0.0.1:5294 user@machine`, then the same command with `--url http://127.0.0.1:5294` and the secret in `FS_ADMIN_SECRET`. Settings changed while it runs (delay without a heartbeat, address checks, number of relays per answer): `fsdir settings`, see [cli-directory.en.md](cli-directory.en.md).

## 8. Accept a relay

No relay is offered without the agreement of **both administrators**. The relay's administrator runs `admin add directory https://directory1.example.org` (the directory's fingerprint is shown); on the directory:

```bash
fsdir requests                 # the relay: name, address, fingerprint (compare with what its administrator reads)
fsdir accept <id>
```

The relay knows within a minute, then sends a heartbeat every two minutes (country, places left, version); the directory looks at the relay's address at its first heartbeat (it must answer with the key that was accepted) and every 30 minutes. The relay is offered to clients when it is linked, spoke recently, answers at its address, **is public**, takes new accounts (access `open`) and has room (`max-connections`). A private relay says nothing.
To be mailed at each request: `fsdir smtp set …`, `fsdir smtp test`, `fsdir notify on` ([cli-directory.en.md](cli-directory.en.md) §5).

## 9. Update

```bash
# development computer
dotnet publish sources/freespeech.directory/FreeSpeech.Directory.csproj -c Release -o sources/deploy/directory/publish
scp -r sources/deploy/directory/publish/* user@machine:ftp/directory/publish/
# machine
sudo ~/update-freespeech-directory.sh
```

A restart erases the heartbeats kept in memory: relays send them again within two minutes (the directory offers nothing until then).

## 10. Backup and migration

`/var/lib/freespeech-directory/peer-identity.json` (the key: losing it means republishing the applications), `peers.json` (the accepted relays) and `settings.json`, rights `600`; and `/var/www/freespeech_directory/appsettings.Local.json`.
To migrate without changing the address or the key: `fsdir export <file>` on the old machine, then on the new one (service stopped) the `import` command, and the DNS pointed to the new machine ([cli-directory.en.md](cli-directory.en.md) §6).

## Troubleshooting

| Symptom | Lead |
|---|---|
| The list is empty | no relay accepted (`fsdir servers`, `fsdir requests`), or the relay is private, in `allowlist` mode, full, silent for over 10 minutes, or its address does not answer with its key |
| A relay is "not checked" (`reachable` false) | its address (the relay's `Peer:PublicUrl`) does not answer or answers with another key: `fsdir servers`, `curl <relay address>/peer/identity` |
| A relay gets 401 on its heartbeat | it is not accepted, or the clocks of the two machines differ by more than 2 minutes |
| `nginx -t`: "duplicate … zone" | the name `freespeech_directory` is declared twice in the `sites-enabled` files |
| The applications ignore the directory | the compiled key (`FsDirectories`) is not the one of `GET /peer/identity` |
