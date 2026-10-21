# Installer un serveur relais FreeSpeech (Debian / Ubuntu vierge)

*English version: [INSTALL_RELAY.en.md](INSTALL_RELAY.en.md). Autres serveurs : [INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md). Plusieurs serveurs sur une machine : [INSTALL_HOST.fr.md](INSTALL_HOST.fr.md). Protocole : [PROTOCOL.fr.md](PROTOCOL.fr.md).*

Ce guide installe **un** serveur relais sur une machine **vierge** (Debian 12 ou 13, Ubuntu 22.04 ou 24.04), pour n'importe quel domaine (ci-dessous `relay.exemple.org`). Le relais ne stocke
aucun message : il met en relation des clients connectés et garde en mémoire seulement qui est en ligne et quelques marqueurs d'attente. Les deux seuls fichiers à sauvegarder sont ceux de
l'administration (§12). Une fois installé, tout se règle **sans redémarrer** avec la commande `admin` ([ADMINISTRATION.fr.md](ADMINISTRATION.fr.md), [cli-relay.fr.md](cli-relay.fr.md)).

| Élément | Valeur dans ce guide |
|---|---|
| Domaine | `relay.exemple.org` |
| Programme | `/var/www/freespeech_relay` |
| État | `/var/lib/freespeech` (`siblings.json`, `access.json`, `peers.json`, `peer-identity.json`) |
| Écoute locale | `127.0.0.1:5290` (nginx fait le HTTPS) |
| Service | `freespeech-relay.service`, utilisateur `www-data` |
| Dépôt des fichiers à installer | `~/ftp/relay/publish` et `~/ftp/relay/deploy` (dans le dossier personnel de l'utilisateur qui administre) |

## 1. Avant de commencer

- Une machine Debian ou Ubuntu avec un utilisateur disposant de `sudo` (les commandes « machine » se lancent avec cet utilisateur) ; ports **80 et 443** joignables depuis internet.
- Un nom de domaine dont l'enregistrement DNS (A, et AAAA si la machine a une IPv6) pointe vers la machine. Certbot (§5) échoue tant que le DNS n'est pas propagé.
- Sur le poste de développement : le SDK .NET 10, le dépôt du projet et `scp`.

## 2. Préparer la machine

À faire **une fois par machine**. Chaque commande peut être relancée sans dommage ; sur une machine qui a déjà servi à un autre serveur FreeSpeech, vérifier seulement ce qui manque.

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y nginx certbot python3-certbot-nginx ufw curl
```

**Pare-feu** (autoriser SSH *avant* d'activer, sinon la session est coupée) :

```bash
sudo ufw allow OpenSSH && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
sudo ufw enable && sudo ufw status
```

Le port 5290 reste local : ne jamais l'ouvrir.

**Runtime ASP.NET Core 10** (`dotnet --list-runtimes` doit montrer `Microsoft.AspNetCore.App 10.x`) :

```bash
# Ubuntu
sudo add-apt-repository -y ppa:dotnet/backports && sudo apt install -y aspnetcore-runtime-10.0
# Debian : dépôt Microsoft (remplacer 12 par 13 sur Debian 13), puis le paquet
curl -sSL -O https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && sudo apt update && sudo apt install -y aspnetcore-runtime-10.0
dotnet --list-runtimes
```

**Dossier de dépôt** (là où l'on dépose les fichiers à installer) :

```bash
mkdir -p ~/ftp/relay/publish ~/ftp/relay/deploy
```

## 3. Publier et copier (poste de développement)

```bash
dotnet publish sources/freespeech.relay/FreeSpeech.Relay.csproj -c Release -o sources/deploy/relay/publish
scp -r sources/deploy/relay/publish/* utilisateur@machine:ftp/relay/publish/
scp -r sources/deploy/relay/deploy utilisateur@machine:ftp/relay/
scp sources/deploy/install-shortcuts.sh utilisateur@machine:ftp/
```

## 4. Installer le service et le secret

```bash
sudo cp ~/ftp/relay/deploy/freespeech-relay.service /etc/systemd/system/
cp ~/ftp/relay/deploy/update-freespeech-relay.sh ~/ && chmod +x ~/update-freespeech-relay.sh
sudo systemctl daemon-reload && sudo systemctl enable freespeech-relay

# Le secret d'administration : un fichier à côté du programme, jamais dans le fichier de service ni dans une publication
sudo mkdir -p /var/www/freespeech_relay
sudo tee /var/www/freespeech_relay/appsettings.Local.json >/dev/null <<EOF
{ "Admin": { "Secret": "$(openssl rand -hex 32)" } }
EOF
sudo chown www-data:www-data /var/www/freespeech_relay/appsettings.Local.json && sudo chmod 600 /var/www/freespeech_relay/appsettings.Local.json

sudo ~/update-freespeech-relay.sh      # copie le programme, règle les droits, démarre le service, affiche le journal et lance `check`
curl http://127.0.0.1:5290/api/time     # {"utc":"..."}
```

Le journal doit dire `Now listening on: http://127.0.0.1:5290`, sans erreur. `appsettings.Local.json` n'est **jamais** écrasé par une mise à jour (la publication ne le contient pas) ; `appsettings.json`, lui, est
remplacé : n'y écrire aucun secret. Sans `Admin:Secret`, **toutes** les routes d'administration refusent. Un secret absent, de moins de 16 caractères ou resté à la valeur d'exemple
(`sources/deploy/relay/deploy/appsettings.Local.relay.example.json`) est signalé à chaque démarrage (journal, niveau *Critical*) et par `dotnet FreeSpeech.Relay.dll check` (code de sortie 1), que le script de mise à jour lance.

**Aucun secret n'est partagé entre serveurs.** Les liens relais ↔ passerelle push et relais ↔ relais reposent sur des paires de clés et l'accord des deux administrateurs (§8). Le fichier de service fournit :
écoute locale `5290`, état dans `/var/lib/freespeech` (créé par systemd), journaux réduits aux avertissements (pour ne pas enregistrer qui parle à qui).

## 5. nginx et certificat HTTPS

Fichier d'exemple : `sources/deploy/relay/deploy/nginx-freespeech-relay` (déjà copié dans `~/ftp/relay/deploy`). Il fournit : le relais WebSocket (`/ws/relay`, délai de lecture 1 h — **sans lui, nginx coupe les connexions inactives au bout de 60 s**),
des limites de connexions et de débit par adresse, l'absence de journal d'accès, `/api/admin` fermé depuis internet et `client_max_body_size 1m`.

```bash
sudo cp ~/ftp/relay/deploy/nginx-freespeech-relay /etc/nginx/sites-available/freespeech-relay
sudo sed -i 's/relay1.exemple.org/relay.exemple.org/' /etc/nginx/sites-available/freespeech-relay     # le domaine du fichier d'exemple
sudo ln -s /etc/nginx/sites-available/freespeech-relay /etc/nginx/sites-enabled/freespeech-relay
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d relay.exemple.org
sudo nginx -t && sudo systemctl reload nginx
```

Certbot modifie le fichier (bloc TLS, redirection http → https) et installe le renouvellement automatique (`systemctl list-timers | grep certbot`). **Ne jamais recopier `nginx-freespeech-relay` par-dessus** après certbot :
on perdrait le certificat ; pour ajouter un bloc, éditer le fichier en place. Réparation : `sudo certbot --nginx -d relay.exemple.org --reinstall`. Sur une machine vierge, `/etc/nginx/sites-enabled/default` peut rester ou être supprimé.

### Si la machine héberge déjà d'autres serveurs web

Le fichier est un site indépendant (un fichier dans `sites-available`, un lien dans `sites-enabled`) : ne rien toucher aux autres sites. Points à vérifier ou à ajouter :

- **Directives de niveau `http`** : le `map` et les deux zones (`limit_conn_zone`, `limit_req_zone`) en tête du fichier sont nommés `freespeech_*`. Un fichier de `sites-enabled` est inclus dans `http {}`, elles y sont donc valides. Si on fusionne ce contenu dans un
  fichier existant, ne les déclarer **qu'une fois**, avant tout `server`, sous des noms qu'aucun autre site n'utilise (nginx refuse un nom en double).
- **`server_name`** propre à ce site ; au plus un `listen … default_server` par port sur la machine.
- **Certificat** : un `certbot --nginx -d <domaine>` par domaine ; le minuteur de renouvellement est commun.
- **Autre frontal** (Caddy, Traefik, haproxy…) déjà sur 80/443 : ajouter pour ce domaine une route vers `127.0.0.1:5290` avec mise à niveau WebSocket pour `/ws/relay` (délais de lecture et d'envoi de 1 h, pas de tampon), les en-têtes `Host`,
  `X-Forwarded-For`, `X-Forwarded-Proto`, et `/api/admin` fermé. Le relais ne croit ces en-têtes que s'ils viennent de la machine elle-même.
- **Ports et noms réservés** par les autres serveurs FreeSpeech : voir [INSTALL_HOST.fr.md](INSTALL_HOST.fr.md).

## 6. Vérifier

```bash
curl -i https://relay.exemple.org/api/time          # 200 et {"utc":"..."} (GET : HEAD donne 405)
sudo journalctl -u freespeech-relay -f          # en direct
```

Dans l'application : *Réglages → Serveurs → ajouter* `https://relay.exemple.org` ; l'état passe à « connecté ».

## 7. Administrer : la commande `admin`

L'API d'administration n'est **pas** exposée sur internet : elle exige le secret **et** un appel venant de la machine elle-même (nginx ferme `/api/admin`). Sur la machine, dans le dossier du programme (le secret est lu dans `appsettings.Local.json`, lisible par `www-data` seulement) :

```bash
sudo bash ~/ftp/install-shortcuts.sh   # une fois par machine : crée fsrelay, fspush et fsdir pour les serveurs installés ici
fsrelay status
fsrelay help          # toutes les commandes
```

Depuis un poste : `ssh -L 5290:127.0.0.1:5290 utilisateur@machine`, puis la même commande avec `--url http://127.0.0.1:5290` et le secret dans `FS_ADMIN_SECRET`. Tout s'applique immédiatement.

## 8. Public ou privé, accès, liens avec d'autres serveurs

Deux réglages **distincts** :

- **Public / privé** (échanges **entre serveurs**). **Privé** (par défaut) : aucun échange avec d'autres serveurs. **Public** : serveurs **frères** (S2S) pour atteindre les contacts d'un autre serveur et se répartir la charge.
- **Mode d'accès** (qui peut **s'inscrire**). `open` (par défaut) : tout le monde sauf les bannis. `allowlist` : seulement les identités **autorisées** ou **invitées**.

Un serveur d'entreprise, d'association ou de famille combine *privé* et *allowlist* :

```bash
fsrelay private
fsrelay access allowlist
fsrelay invite "Anna" --days 7      # jeton à usage unique, affiché une fois : le transmettre à Anna
```

Anna ajoute l'adresse du serveur dans *Réglages → Serveurs*, puis saisit le jeton (*J'ai une invitation*) ou choisit *Demander l'accès*. Pour une personne précise : `ban <id>`, `allow <id>`, `clients`.

Serveur public, avec passerelle push et relais frères :

```bash
fsrelay public
fsrelay peer-id                                        # empreinte à lire à l'autre administrateur
fsrelay add push https://push.exemple.org              # la passerelle accepte (admin accept <id>) : voir INSTALL_PUSH
fsrelay settings set country FR                        # le pays que l'annuaire propose aux clients
fsrelay add directory https://directory1.exemple.org   # l'annuaire accepte (admin accept <id>) : voir INSTALL_DIRECTORY
fsrelay add relay https://autre.exemple.net            # l'autre administrateur accepte
fsrelay server-ban https://mauvais.exemple.net "abus"  # bannir un serveur
```

Un lien n'existe que si **les deux** administrateurs ont accepté ; l'adresse doit être en `https` et non locale ; chaque requête entre serveurs est signée. `Peer:Name`, `Peer:PublicUrl` et `Peer:Contact` (comment l'autre administrateur peut vous joindre pour vérifier qui demande un lien) se règlent dans `appsettings.Local.json`
(modèle : `sources/deploy/relay/deploy/appsettings.Local.relay.example.json`). Commandes complètes : [cli-relay.fr.md](cli-relay.fr.md).

## 9. Réglages utiles (variables du service, `Environment=…`)

| Variable | Défaut | Rôle |
|---|---|---|
| `Admin:Secret` (dans `appsettings.Local.json`) | vide (admin refusée) | secret de l'API d'administration |
| `Server__MaxConnections` | 0 (illimité) | au-delà, le serveur propose aux clients un serveur frère qui a de la place |
| `Server__MaxMessageBytes` | 524288 | taille maximale d'un message (512 Kio) |
| `Server__MaxBytesPerIdentityPerDay` | 1073741824 (1 Gio) | octets qu'une identité peut envoyer sur 24 heures glissantes (0 = pas de limite) |
| `Server__MaxBytesPerIdentityPer10Minutes` | 314572800 (300 Mio) | débit d'une identité : plus vite est ralenti (0 = pas de limite) |
| `Server__MaxBytesPerAddressPerDay` | 5368709120 (5 Gio) | octets que toutes les identités derrière une même adresse peuvent envoyer sur 24 heures glissantes (0 = pas de limite) |
| `Server__SettingsFilePath` | `settings.json` | où sont gardés les réglages changés par `admin settings set` (les variables de ce tableau sont les valeurs de départ ; à sauvegarder) |
| `Server__LimitsFilePath` | `limits.json` | où sont gardées les limites changées par `admin limits set` (les trois réglages ci-dessus sont les valeurs de départ) |
| `Server__S2SRatePerMinute` | 1200 | quota de requêtes par serveur frère et par minute |
| `Server__SiblingsFilePath` | `siblings.json` | statut public/privé, voisins suggérés, adresses bannies (à sauvegarder) |
| `Server__AccessFilePath` | `access.json` | mode d'accès, autorisés, bannis, invitations (à sauvegarder) |
| `Server__ClientAccess` | `open` | mode de départ d'un serveur neuf : `open` ou `allowlist` (ensuite : `admin access`) |
| `Server__SourceUrl` | vide | adresse du code source, servie par `GET /api/info` : l'AGPL (art. 13) oblige à l'offrir aux utilisateurs d'une version modifiée exploitée en service réseau |
| `Admin__AllowRemote` | faux | admettre l'administration d'ailleurs que de la machine : **réseau privé seulement** |
| `Logging__LogLevel__FreeSpeech` | `Warning` | niveau de départ ; pour diagnostiquer : `admin log level relay information` **à chaud**, puis `admin log reset` |
| `Server__AllowAnonymousRegister`, `Server__AllowInsecureSiblings` | faux | **développement et tests seulement, jamais en production** |

Après avoir modifié le fichier de service : `sudo systemctl daemon-reload && sudo systemctl restart freespeech-relay`.

## 10. Mettre à jour

```bash
# poste de développement
dotnet publish sources/freespeech.relay/FreeSpeech.Relay.csproj -c Release -o sources/deploy/relay/publish
scp -r sources/deploy/relay/publish/* utilisateur@machine:ftp/relay/publish/
# machine
sudo cp -r /var/www/freespeech_relay /var/www/freespeech_relay.bak      # retour arrière possible
sudo ~/update-freespeech-relay.sh
```

Les marqueurs d'attente et les jetons de réveil sont en mémoire : un redémarrage les efface, les clients se reconnectent et les renouvellent, et l'outbox de l'expéditeur renvoie ce qui était en attente. Mettre à jour hors des heures d'usage.
**Ordre** : installer d'abord la nouvelle application sur les téléphones, puis mettre à jour le serveur ; deux serveurs frères se mettent à jour ensemble.
Retour arrière : `sudo systemctl stop freespeech-relay`, remettre le dossier `.bak`, `sudo systemctl start freespeech-relay`.

## 11. Passerelle push

Le réveil des téléphones passe par une passerelle push, installée séparément ([INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md)) puis liée à ce relais (§8).

## 12. Sauvegarde

Seuls `/var/lib/freespeech/siblings.json` (statut public/privé, voisins, adresses bannies), `/var/lib/freespeech/settings.json` (réglages changés à chaud), `/var/lib/freespeech/access.json` (mode d'accès, autorisés, bannis, empreintes des invitations), `peers.json` et `peer-identity.json` (la clé de ce relais :
la perdre oblige à relier le relais à ses partenaires) méritent une copie ; droits `600`. Le secret d'administration est dans `/var/www/freespeech_relay/appsettings.Local.json`. Pour migrer ou sauvegarder en une seule fois : `admin export` et `import` ([cli-relay.fr.md](cli-relay.fr.md) §11).

## En cas de problème

| Symptôme | Piste |
|---|---|
| Téléphones qui se reconnectent en boucle | bloc `/ws/relay` absent ou écrasé dans le fichier nginx |
| HTTPS avec un mauvais certificat | fichier nginx recopié sur celui de certbot : `certbot --nginx -d … --reinstall` |
| `nginx -t` : « duplicate … zone » ou « duplicate map » | le nom `freespeech_*` est déclaré deux fois dans les fichiers de `sites-enabled` |
| 502 de nginx | le service est arrêté : `systemctl status freespeech-relay`, `journalctl -u freespeech-relay -n 50` |
| 404 sur `/api/admin/...` depuis internet | normal : passer par la commande `admin` ou le tunnel SSH |
| 401 sur `/api/admin/...` | `Admin:Secret` absent de `appsettings.Local.json` (lancer `check`) ou différent, ou appel qui ne vient pas de la machine |
| Un utilisateur voit « serveur privé » | mode `allowlist` : lui donner une invitation (`admin invite`), l'autoriser (`admin allow <id>`), ou valider sa demande d'accès (`admin pending`, `admin accept <id>`) |
| Un serveur frère répond toujours 404 | serveur non public, horloges décalées de plus de 2 minutes, ou lien non accepté des deux côtés |
