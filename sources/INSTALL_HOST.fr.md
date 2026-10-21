# Plusieurs serveurs sur une même machine

*English version: [INSTALL_HOST.en.md](INSTALL_HOST.en.md). Un guide par serveur, chacun complet pour une machine vierge : [INSTALL_RELAY.fr.md](INSTALL_RELAY.fr.md), [INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md), [INSTALL_DIRECTORY.fr.md](INSTALL_DIRECTORY.fr.md).*

Plusieurs serveurs FreeSpeech (relais, passerelle push, site web…) et d'autres logiciels peuvent partager une machine Debian ou Ubuntu. Chaque guide d'installation se suffit à lui-même ; ce document réunit ce qui doit rester
**cohérent entre eux**.

## 1. Ports et noms réservés

| Serveur | Port local | Service systemd | Programme | État | Fichier nginx (`sites-available`) | Noms nginx de niveau `http` |
|---|---|---|---|---|---|---|
| Relais | 5290 | `freespeech-relay` | `/var/www/freespeech_relay` | `/var/lib/freespeech` | `freespeech` | `freespeech_connection_upgrade`, `freespeech_conn`, `freespeech_req` |
| Passerelle push | 5291 | `freespeech-push` | `/var/www/freespeech_push` | `/var/lib/freespeech-push` | `freespeech-push` | `freespeech_push` |
| Annuaire | 5294 | `freespeech-directory` | `/var/www/freespeech_directory` | `/var/lib/freespeech-directory` | `freespeech-directory` | `freespeech_directory` |
| Site web | 5292 | `freespeech-website` | `/var/www/freespeech_site_app` | base PostgreSQL `freespeech` | `freespeech-site` | — |

Un autre logiciel de la machine (par exemple une forge Git comme Forgejo, dont le port HTTP par défaut est 3000) choisit son port parmi ceux qui restent libres (`sudo ss -ltnp` liste les ports occupés) et garde son propre fichier nginx, son propre `server_name`
et son propre certificat. Ses clones SSH utilisent le service SSH de la machine ou le port SSH qu'il propose : voir sa documentation, et ouvrir ce port dans `ufw` si ce n'est pas le 22.

## 2. nginx : ce qui est partagé

- Un site = un fichier dans `/etc/nginx/sites-available/`, un lien dans `sites-enabled/`. Aucun guide ne modifie `nginx.conf` ni le fichier d'un autre site.
- Les fichiers de `sites-enabled` sont inclus dans le bloc `http {}` : une directive de niveau `http` (`map`, `limit_req_zone`, `limit_conn_zone`) placée en tête d'un fichier de site est valide. Son **nom doit être unique** sur toute la machine
  (colonne de droite du tableau) ; nginx refuse un doublon (`nginx -t`).
- `server_name` distinct pour chaque site ; au plus un `listen … default_server` par port sur la machine.
- Un certificat par domaine (`certbot --nginx -d <domaine>`), un seul minuteur de renouvellement pour tous (`systemctl list-timers | grep certbot`). Certbot réécrit le fichier de chaque site : ne jamais recopier un fichier d'exemple par-dessus après certbot.
- Si un autre frontal (Caddy, Traefik, haproxy) occupe déjà 80/443, chaque guide indique ce que la route de son serveur doit contenir.

## 3. Le reste

- **Paquets** : nginx, certbot, ufw et le runtime ASP.NET Core 10 s'installent une fois par machine ; chaque guide les installe si besoin sans dommage s'ils sont déjà là.
- **Pare-feu** : seuls SSH, 80 et 443 sont ouverts pour les serveurs FreeSpeech ; les ports locaux du tableau ne le sont jamais.
- **Utilisateur** : les services tournent sous `www-data`. Les fichiers à installer passent par `~/ftp` de l'utilisateur qui administre ; les scripts `update-*.sh` le retrouvent à partir de `sudo`.
- **Secrets** : un `appsettings.Local.json` par serveur, à côté de son programme, avec un `Admin:Secret` propre à ce serveur. Aucun secret n'est partagé entre serveurs : les liens se font par clés et acceptation des deux administrateurs.
- **Ordre conseillé sur une machine neuve** : DNS de chaque domaine, puis le relais, puis la passerelle push, puis `admin add push` sur le relais et `admin accept` sur la passerelle.
