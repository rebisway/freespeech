# Installer un annuaire FreeSpeech (Debian / Ubuntu vierge)

*English version: [INSTALL_DIRECTORY.en.md](INSTALL_DIRECTORY.en.md). Autres serveurs : [INSTALL_RELAY.fr.md](INSTALL_RELAY.fr.md), [INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md). Plusieurs serveurs sur une machine : [INSTALL_HOST.fr.md](INSTALL_HOST.fr.md). Protocole : [PROTOCOL.fr.md](PROTOCOL.fr.md) §10 « Annuaire ». Commandes : [cli-directory.fr.md](cli-directory.fr.md).*

Un annuaire (`freespeech.directory`) indique à un client qui ne connaît aucun relais quels relais acceptent de nouveaux comptes, et répartit les nouveaux arrivants entre eux. Il ne stocke rien sur les clients et ne voit passer aucun message.
Ce guide l'installe sur une machine **vierge** (Debian 12 ou 13, Ubuntu 22.04 ou 24.04), pour n'importe quel domaine (ci-dessous `directory1.exemple.org`).

| Élément | Valeur dans ce guide |
|---|---|
| Domaine | `directory1.exemple.org` |
| Programme | `/var/www/freespeech_directory` (avec `appsettings.Local.json`) |
| État | `/var/lib/freespeech-directory` (`peer-identity.json`, `peers.json`, `settings.json`) |
| Écoute locale | `127.0.0.1:5294` (nginx fait le HTTPS) |
| Service | `freespeech-directory.service`, utilisateur `www-data` |
| Dépôt des fichiers à installer | `~/ftp/directory/publish` et `~/ftp/directory/deploy` |

## 1. Avant de commencer

- Une machine Debian ou Ubuntu avec un utilisateur disposant de `sudo` ; ports **80 et 443** joignables depuis internet.
- Un nom de domaine dont l'enregistrement DNS (A, et AAAA si la machine a une IPv6) pointe vers la machine. **Le domaine vous appartient et ne dépend pas de l'hébergeur** : l'identité de l'annuaire est sa clé et son adresse, que les applications connaissent ; pour déménager, on copie la clé et on change le DNS.
- Sur le poste de développement : le SDK .NET 10, le dépôt du projet et `scp`.
- **Plusieurs annuaires** : installer le même guide sur une autre machine, dans un autre pays si possible, avec son propre domaine (`directory2.exemple.org`) et sa propre clé. Les annuaires ne se synchronisent pas : chaque relais se lie à chacun.

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

Le port 5294 reste local : ne jamais l'ouvrir.

**Runtime ASP.NET Core 10** (`dotnet --list-runtimes` doit montrer `Microsoft.AspNetCore.App 10.x`) :

```bash
# Ubuntu
sudo add-apt-repository -y ppa:dotnet/backports && sudo apt install -y aspnetcore-runtime-10.0
# Debian : dépôt Microsoft (remplacer 12 par 13 sur Debian 13), puis le paquet
curl -sSL -O https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && sudo apt update && sudo apt install -y aspnetcore-runtime-10.0
dotnet --list-runtimes
```

**Dossier de dépôt** :

```bash
mkdir -p ~/ftp/directory/publish ~/ftp/directory/deploy
```

## 3. Publier et copier (poste de développement)

```bash
dotnet publish sources/freespeech.directory/FreeSpeech.Directory.csproj -c Release -o sources/deploy/directory/publish
scp -r sources/deploy/directory/publish/* utilisateur@machine:ftp/directory/publish/
scp -r sources/deploy/directory/deploy utilisateur@machine:ftp/directory/
scp sources/deploy/install-shortcuts.sh utilisateur@machine:ftp/
```


## 4. Installer le service et le secret

```bash
sudo cp ~/ftp/directory/deploy/freespeech-directory.service /etc/systemd/system/
cp ~/ftp/directory/deploy/update-freespeech-directory.sh ~/ && chmod +x ~/update-freespeech-directory.sh
sudo systemctl daemon-reload && sudo systemctl enable freespeech-directory

# Le secret d'administration et l'adresse publique : un fichier à côté du programme, jamais dans le fichier de service ni dans une publication
sudo mkdir -p /var/www/freespeech_directory
sudo tee /var/www/freespeech_directory/appsettings.Local.json >/dev/null <<EOF
{ "Admin": { "Secret": "$(openssl rand -hex 32)" }, "Peer": { "PublicUrl": "https://directory1.exemple.org", "Name": "directory1" } }
EOF
sudo chown www-data:www-data /var/www/freespeech_directory/appsettings.Local.json && sudo chmod 600 /var/www/freespeech_directory/appsettings.Local.json

sudo ~/update-freespeech-directory.sh      # copie le programme, règle les droits, démarre le service, affiche le journal et lance `check`
curl http://127.0.0.1:5294/health          # {"ok":true}
```

`appsettings.Local.json` n'est **jamais** écrasé par une mise à jour. Sans `Admin:Secret`, toutes les routes d'administration refusent ; un secret absent, trop court ou resté à la valeur d'exemple (`sources/deploy/directory/deploy/appsettings.Local.directory.example.json`) est signalé à chaque démarrage (journal, niveau *Critical*) et par `dotnet FreeSpeech.Directory.dll check`, que le script de mise à jour lance.
`Peer:PublicUrl` est l'adresse https de l'annuaire : les relais s'en servent pour vérifier qui leur demande un lien.
**Aucun secret n'est partagé entre serveurs** : les relais se lient à l'annuaire par clés et par l'accord des deux administrateurs (§8).

**La clé de l'annuaire** (`/var/lib/freespeech-directory/peer-identity.json`) est créée au premier démarrage. **Sauvegardez-la hors ligne tout de suite** : les applications sont compilées avec sa clé publique, et une nouvelle clé obligerait à republier les applications. Sa clé publique s'affiche par :

```bash
dotnet FreeSpeech.Directory.dll admin key          # la clé publique seule : la valeur à donner au build des applications (FsDirectories)
```

## 5. nginx et certificat HTTPS

Fichier d'exemple : `sources/deploy/directory/deploy/nginx-freespeech-directory` (déjà copié dans `~/ftp/directory/deploy`). Il limite le débit par adresse, **n'écrit aucun journal d'accès** (l'annuaire ne garde aucune adresse de client) et ferme `/admin`.

```bash
sudo cp ~/ftp/directory/deploy/nginx-freespeech-directory /etc/nginx/sites-available/freespeech-directory
sudo sed -i 's/directory1.exemple.org/directory1.exemple.org/' /etc/nginx/sites-available/freespeech-directory     # le domaine du fichier d'exemple
sudo ln -s /etc/nginx/sites-available/freespeech-directory /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx && sudo certbot --nginx -d directory1.exemple.org
```

Certbot modifie le fichier (TLS, redirection) et installe le renouvellement automatique. **Ne jamais recopier le fichier d'exemple par-dessus** après certbot ; réparation : `sudo certbot --nginx -d directory1.exemple.org --reinstall`.

### Si la machine héberge déjà d'autres serveurs web

Le fichier est un site indépendant : ne rien toucher aux autres sites. À vérifier ou à ajouter :

- **Directive de niveau `http`** : `limit_req_zone … zone=freespeech_directory` en tête du fichier ; valide car un fichier de `sites-enabled` est inclus dans `http {}`. Si on fusionne ce contenu dans un fichier existant, la déclarer **une seule fois**, avant tout `server`, sous un nom qu'aucun autre site n'utilise.
- **`server_name`** propre à ce site ; au plus un `listen … default_server` par port sur la machine.
- **Certificat** : un `certbot --nginx -d <domaine>` par domaine ; le minuteur de renouvellement est commun.
- **Journal d'accès** : ne pas le réactiver pour ce site, il enregistrerait l'adresse de chaque client qui demande des relais.
- **Autre frontal** (Caddy, Traefik, haproxy…) déjà sur 80/443 : route de ce domaine vers `127.0.0.1:5294`, avec `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`, et `/admin` fermé (l'annuaire refuse aussi tout appel d'administration dont l'adresse transmise n'est pas locale).
- **Ports et noms réservés** par les autres serveurs FreeSpeech : voir [INSTALL_HOST.fr.md](INSTALL_HOST.fr.md).

## 6. Vérifier

```bash
curl -s https://directory1.exemple.org/health                       # {"ok":true}
curl -s "https://directory1.exemple.org/directory/servers?country=FR"    # une liste signée ; vide tant qu'aucun relais n'est lié
sudo journalctl -u freespeech-directory -n 20 --no-pager
```

## 7. Administrer : la commande `admin`

L'API d'administration n'est **pas** exposée sur internet : elle exige le secret **et** un appel venant de la machine elle-même. Sur la machine, dans le dossier du programme (le secret est lu dans `appsettings.Local.json`, lisible par `www-data` seulement) :

```bash
sudo bash ~/ftp/install-shortcuts.sh   # une fois par machine : crée fsrelay, fspush et fsdir pour les serveurs installés ici
fsdir servers          # les relais connus : pays, places restantes, version, s'ils sont proposés
fsdir help             # toutes les commandes
```

Depuis un poste : `ssh -L 5294:127.0.0.1:5294 utilisateur@machine`, puis la même commande avec `--url http://127.0.0.1:5294` et le secret dans `FS_ADMIN_SECRET`. Réglages à chaud (délai sans battement, vérification des adresses, nombre de relais par réponse) : `fsdir settings`, voir [cli-directory.fr.md](cli-directory.fr.md).

## 8. Accepter un relais

Aucun relais n'est proposé sans l'accord **des deux administrateurs**. L'administrateur du relais fait `admin add directory https://directory1.exemple.org` (l'empreinte de l'annuaire s'affiche) ; sur l'annuaire :

```bash
fsdir requests                 # le relais : nom, adresse, empreinte (à comparer avec ce que son administrateur lit)
fsdir accept <id>
```

Le relais le sait en moins d'une minute, puis envoie un battement toutes les deux minutes (pays, places restantes, version) ; l'annuaire regarde l'adresse du relais à son premier battement (elle doit répondre avec la clé acceptée) et toutes les 30 minutes. Le relais est proposé aux clients quand il est lié, a parlé récemment, répond à son adresse, **est public**, accepte de nouveaux comptes (accès `open`) et a de la place (`max-connections`). Un relais privé ne dit rien.
Pour recevoir un mail à chaque demande : `fsdir smtp set …`, `fsdir smtp test`, `fsdir notify on` ([cli-directory.fr.md](cli-directory.fr.md) §5).

## 9. Mettre à jour

```bash
# poste de développement
dotnet publish sources/freespeech.directory/FreeSpeech.Directory.csproj -c Release -o sources/deploy/directory/publish
scp -r sources/deploy/directory/publish/* utilisateur@machine:ftp/directory/publish/
# machine
sudo ~/update-freespeech-directory.sh
```

Un redémarrage efface les battements en mémoire : les relais les renvoient dans les deux minutes (l'annuaire ne propose rien d'ici là).

## 10. Sauvegarde et migration

`/var/lib/freespeech-directory/peer-identity.json` (la clé : la perdre oblige à republier les applications), `peers.json` (les relais acceptés) et `settings.json`, droits `600` ; et `/var/www/freespeech_directory/appsettings.Local.json`.
Pour migrer sans changer d'adresse ni de clé : `fsdir export <fichier>` sur l'ancienne machine, puis sur la nouvelle (service arrêté) la commande `import`, et le DNS vers la nouvelle machine ([cli-directory.fr.md](cli-directory.fr.md) §6).

## En cas de problème

| Symptôme | Piste |
|---|---|
| La liste est vide | aucun relais accepté (`fsdir servers`, `fsdir requests`), ou relais privé, en `allowlist`, plein, silencieux depuis plus de 10 minutes, ou dont l'adresse ne répond pas avec sa clé |
| Un relais est « non vérifié » (`reachable` faux) | son adresse (`Peer:PublicUrl` du relais) ne répond pas ou répond avec une autre clé : `fsdir servers`, `curl <adresse du relais>/peer/identity` |
| Un relais reçoit 401 à son battement | il n'est pas accepté, ou l'horloge des deux machines diffère de plus de 2 minutes |
| `nginx -t` : « duplicate … zone » | le nom `freespeech_directory` est déclaré deux fois dans les fichiers de `sites-enabled` |
| Les applications ignorent l'annuaire | la clé compilée (`FsDirectories`) n'est pas celle de `GET /peer/identity` |
