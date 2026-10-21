# Several servers on one machine

*Version française : [INSTALL_HOST.fr.md](INSTALL_HOST.fr.md). One guide per server, each complete for a blank machine: [INSTALL_RELAY.en.md](INSTALL_RELAY.en.md), [INSTALL_PUSH.en.md](INSTALL_PUSH.en.md), [INSTALL_DIRECTORY.en.md](INSTALL_DIRECTORY.en.md).*

Several FreeSpeech servers (relay, push gateway, website…) and other software can share one Debian or Ubuntu machine. Each installation guide stands on its own; this document gathers what must stay
**consistent between them**.

## 1. Reserved ports and names

| Server | Local port | systemd service | Program | State | nginx file (`sites-available`) | `http`-level nginx names |
|---|---|---|---|---|---|---|
| Relay | 5290 | `freespeech-relay` | `/var/www/freespeech_relay` | `/var/lib/freespeech` | `freespeech` | `freespeech_connection_upgrade`, `freespeech_conn`, `freespeech_req` |
| Push gateway | 5291 | `freespeech-push` | `/var/www/freespeech_push` | `/var/lib/freespeech-push` | `freespeech-push` | `freespeech_push` |
| Directory | 5294 | `freespeech-directory` | `/var/www/freespeech_directory` | `/var/lib/freespeech-directory` | `freespeech-directory` | `freespeech_directory` |
| Website | 5292 | `freespeech-website` | `/var/www/freespeech_site_app` | PostgreSQL database `freespeech` | `freespeech-site` | — |

Other software on the machine (for example a Git forge such as Forgejo, whose default HTTP port is 3000) picks its port among those still free (`sudo ss -ltnp` lists the busy ones) and keeps its own nginx file, its own `server_name`
and its own certificate. Its SSH clones use the machine's SSH service or the SSH port it offers: see its documentation, and open that port in `ufw` if it is not 22.

## 2. nginx: what is shared

- One site = one file in `/etc/nginx/sites-available/`, one link in `sites-enabled/`. No guide edits `nginx.conf` or another site's file.
- The `sites-enabled` files are included inside the `http {}` block: an `http`-level directive (`map`, `limit_req_zone`, `limit_conn_zone`) at the top of a site file is valid. Its **name must be unique** on the whole machine
  (right-hand column of the table); nginx refuses a duplicate (`nginx -t`).
- A distinct `server_name` for every site; at most one `listen … default_server` per port on the machine.
- One certificate per domain (`certbot --nginx -d <domain>`), a single renewal timer for all (`systemctl list-timers | grep certbot`). Certbot rewrites each site's file: never copy an example file over it after certbot.
- If another front end (Caddy, Traefik, haproxy) already holds 80/443, each guide says what the route of its server must contain.

## 3. The rest

- **Packages**: nginx, certbot, ufw and the ASP.NET Core 10 runtime are installed once per machine; each guide installs them if needed, harmlessly if they are already there.
- **Firewall**: only SSH, 80 and 443 are open for the FreeSpeech servers; the local ports of the table never are.
- **User**: the services run as `www-data`. The files to install go through `~/ftp` of the administering user; the `update-*.sh` scripts find it from `sudo`.
- **Secrets**: one `appsettings.Local.json` per server, next to its program, with an `Admin:Secret` of its own. No secret is shared between servers: links are made by keys and agreement of both administrators.
- **Suggested order on a new machine**: the DNS of each domain, then the relay, then the push gateway, then `admin add push` on the relay and `admin accept` on the gateway.
