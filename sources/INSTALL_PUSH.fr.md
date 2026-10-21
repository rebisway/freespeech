# Installer une passerelle push FreeSpeech (Debian / Ubuntu vierge)

*English version: [INSTALL_PUSH.en.md](INSTALL_PUSH.en.md). Autres serveurs : [INSTALL_RELAY.fr.md](INSTALL_RELAY.fr.md). Plusieurs serveurs sur une machine : [INSTALL_HOST.fr.md](INSTALL_HOST.fr.md). Protocole : [PROTOCOL.fr.md](PROTOCOL.fr.md) §9.*

La passerelle (`freespeech.push`) est le seul composant qui détient la clé Firebase (FCM) et la clé Apple (APNs). Les serveurs relais l'appellent pour « réveiller » un téléphone ; elle ne reçoit ni identité ni contenu.
Ce guide l'installe sur une machine **vierge** (Debian 12 ou 13, Ubuntu 22.04 ou 24.04), pour n'importe quel domaine (ci-dessous `push.exemple.org`).

| Élément | Valeur dans ce guide |
|---|---|
| Domaine | `push.exemple.org` |
| Programme | `/var/www/freespeech_push` (avec `appsettings.Local.json` et `secrets/`) |
| État | `/var/lib/freespeech-push` (`peer-identity.json`, `peers.json`, `settings.json`) |
| Écoute locale | `127.0.0.1:5291` (nginx fait le HTTPS) |
| Service | `freespeech-push.service`, utilisateur `www-data` |
| Dépôt des fichiers à installer | `~/ftp/push/publish` et `~/ftp/push/deploy` |

## 1. Avant de commencer

- Une machine Debian ou Ubuntu avec un utilisateur disposant de `sudo` ; ports **80 et 443** joignables depuis internet.
- Un nom de domaine dont l'enregistrement DNS (A, et AAAA si la machine a une IPv6) pointe vers la machine.
- **Firebase** : projet créé, application Android `org.freespeech.mobile`, `google-services.json` dans `sources/freespeech.mobile/` (non versionné), **Firebase Cloud Messaging API (V1)** activée (Paramètres du projet → Cloud Messaging).
  Clé du compte de service : *Paramètres du projet → Comptes de service → Générer une nouvelle clé privée*. **Ne jamais la versionner ni la partager** ; si elle a été exposée, en générer une nouvelle et supprimer l'ancienne.
- Sur le poste de développement : le SDK .NET 10, le dépôt du projet et `scp`.
- **Ordre** : installer d'abord l'APK récent sur les téléphones, puis mettre à jour les relais.

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

Le port 5291 reste local : ne jamais l'ouvrir.

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
mkdir -p ~/ftp/push/publish ~/ftp/push/deploy ~/ftp/push/secrets
```

## 3. Préparer le paquet (poste de développement)

```bash
dotnet publish sources/freespeech.push/FreeSpeech.Push.csproj -c Release -o sources/deploy/push/publish
```

Dans `sources/deploy/push/` (ignoré par SVN : le paquet et les secrets ne sont jamais versionnés), à côté de `publish/` — jamais dedans, pour que republier ne puisse rien écraser :

| Fichier | Rôle |
|---|---|
| `publish/` | le programme |
| `secrets/firebase-service-account.json` | la clé du compte de service |
| `appsettings.Local.json` | **l'unique endroit des secrets** (modèle : `sources/deploy/push/deploy/appsettings.Local.push.example.json`) |

```json
{
  "Admin": { "Secret": "<openssl rand -hex 32>" },
  "Fcm":   { "ServiceAccountFile": "/var/www/freespeech_push/secrets/firebase-service-account.json" },
  "Peer":  { "PublicUrl": "https://push.exemple.org" }
}
```

`Admin:Secret` est le secret de l'API d'administration locale (sans lui, la commande `admin` refuse tout) ; il est propre à cette passerelle, différent de celui d'un relais. Une clé Apple, si elle sert, se règle de la même façon (`Apns:*`, voir le modèle).
Un secret absent ou resté à la valeur d'exemple est signalé à chaque démarrage (journal, niveau *Critical*) et par `dotnet FreeSpeech.Push.dll check`, que le script de mise à jour lance.

## 4. Copier sur la machine

```bash
scp -r sources/deploy/push/publish sources/deploy/push/secrets sources/deploy/push/appsettings.Local.json utilisateur@machine:ftp/push/
scp -r sources/deploy/push/deploy utilisateur@machine:ftp/push/
scp sources/deploy/install-shortcuts.sh utilisateur@machine:ftp/
```

## 5. Installer la passerelle

```bash
sudo mkdir -p /var/www/freespeech_push
sudo cp -r ~/ftp/push/secrets ~/ftp/push/appsettings.Local.json /var/www/freespeech_push/
sudo shred -u ~/ftp/push/secrets/* ~/ftp/push/appsettings.Local.json     # les secrets ne restent pas dans le dépôt
sudo cp ~/ftp/push/deploy/freespeech-push.service /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable freespeech-push
cp ~/ftp/push/deploy/update-freespeech-push.sh ~/ && chmod +x ~/update-freespeech-push.sh
sudo ~/update-freespeech-push.sh
```

Le script lit le programme dans `~/ftp/push/publish`, le copie, met `www-data` propriétaire, droits `600` sur la configuration et `700` sur `secrets/`, démarre le service (port local `5291`), affiche le journal puis lance `check`.
Le fichier de service fournit `Push__StatePath=/var/lib/freespeech-push` (créé par systemd). **Aucun secret n'est à mettre dans le fichier de service.** Sans **aucun relais lié**, la passerelle refuse toutes les requêtes.

## 6. nginx et certificat HTTPS

Fichier d'exemple : `sources/deploy/push/deploy/nginx-freespeech-push` (déjà copié dans `~/ftp/push/deploy`). Il limite le débit (la passerelle n'est appelée que par les relais), ferme `/admin` et laisse passer `/peer/`.

```bash
sudo cp ~/ftp/push/deploy/nginx-freespeech-push /etc/nginx/sites-available/freespeech-push
sudo sed -i 's/push1.exemple.org/push.exemple.org/' /etc/nginx/sites-available/freespeech-push     # le domaine du fichier d'exemple
sudo ln -s /etc/nginx/sites-available/freespeech-push /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx && sudo certbot --nginx -d push.exemple.org
```

Certbot modifie le fichier (TLS, redirection) et installe le renouvellement automatique. **Ne jamais recopier le fichier d'exemple par-dessus** après certbot ; réparation : `sudo certbot --nginx -d push.exemple.org --reinstall`.

### Si la machine héberge déjà d'autres serveurs web

Le fichier est un site indépendant : ne rien toucher aux autres sites. À vérifier ou à ajouter :

- **Directive de niveau `http`** : `limit_req_zone … zone=freespeech_push` en tête du fichier ; valide car un fichier de `sites-enabled` est inclus dans `http {}`. Si on fusionne ce contenu dans un fichier existant, la déclarer **une seule fois**, avant tout `server`, sous un nom qu'aucun autre site n'utilise.
- **`server_name`** propre à ce site ; au plus un `listen … default_server` par port sur la machine.
- **Certificat** : un `certbot --nginx -d <domaine>` par domaine ; le minuteur de renouvellement est commun.
- **Autre frontal** (Caddy, Traefik, haproxy…) déjà sur 80/443 : route de ce domaine vers `127.0.0.1:5291`, avec `Host`, `X-Forwarded-For`, `X-Forwarded-Proto`, et `/admin` fermé (la passerelle refuse aussi tout appel dont l'adresse transmise n'est pas locale).
- **Ports et noms réservés** par les autres serveurs FreeSpeech : voir [INSTALL_HOST.fr.md](INSTALL_HOST.fr.md).

## 7. Vérifier

```bash
curl -s https://push.exemple.org/health              # {"ok":true}
sudo journalctl -u freespeech-push -n 20 --no-pager   # doit dire : Providers: fcm
```

## 8. Relier un relais

Aucun secret n'est partagé : chaque serveur a sa paire de clés et le lien exige l'accord des deux administrateurs. Sur le **relais** : `admin add push https://push.exemple.org` (l'empreinte de la passerelle s'affiche).
Sur la **passerelle** (dans son dossier, `www-data` lit la configuration) :

```bash
sudo bash ~/ftp/install-shortcuts.sh   # une fois par machine : crée fsrelay, fspush et fsdir pour les serveurs installés ici
fspush requests        # le relais : nom, adresse, empreinte (à comparer avec ce que son administrateur lit)
fspush accept <id>
```

Le relais le sait en moins d'une minute (`admin list push` sur le relais) et utilise alors la passerelle, avec des appels signés. Un tiers qui déploie son propre relais procède de la même façon, sans rien vous transmettre.
Le port public de la passerelle doit laisser passer `/peer/` (nginx : rien à changer, seul `/admin` est fermé). Commandes complètes : [cli-push.fr.md](cli-push.fr.md).

**Essai réel** : téléphones A et B sur le relais ; fermer l'application sur B (la balayer des applications récentes) ; envoyer un message depuis A : B affiche la notification en quelques secondes, sans attendre les 15 minutes du polling.

**Plusieurs passerelles** (charge, panne) : installer la même sur d'autres machines (mêmes identifiants Firebase) et lier chacune au relais (`admin add push <url>`). Le relais bascule seul (3 essais) et écarte une passerelle en panne ou saturée pendant quelques dizaines de secondes.
`Push:MaxConcurrent` (défaut 100) limite les requêtes simultanées d'une passerelle.

## 9. Administrer (sans redémarrer)

```bash
fspush status                  # fournisseurs, compteurs, limites
fspush relays                  # relais liés et leurs compteurs
fspush ban <id>                # bannir un relais (403) ; unban pour revenir
fspush maintenance on          # répond « occupé » : les relais utilisent une autre passerelle, puis redémarrer sans perte
fspush log tail -f             # journal en direct (jamais de jeton d'appareil)
```

L'API d'administration (`/admin/…`) n'est pas publiée par nginx et la passerelle refuse tout appel dont l'adresse transmise n'est pas locale. Voir [ADMINISTRATION.fr.md](ADMINISTRATION.fr.md) §4.

## 10. Mettre à jour

```bash
# poste de développement
dotnet publish sources/freespeech.push/FreeSpeech.Push.csproj -c Release -o sources/deploy/push/publish
scp -r sources/deploy/push/publish utilisateur@machine:ftp/push/
# machine
sudo ~/update-freespeech-push.sh
```

`appsettings.Local.json` et `secrets/` ne sont pas touchés par une mise à jour.

## 11. Sauvegarde

`/var/lib/freespeech-push/peer-identity.json` (la clé de la passerelle : la perdre oblige à relier chaque relais), `peers.json` et `settings.json`, droits `600` ; et `/var/www/freespeech_push/appsettings.Local.json` avec `secrets/` (les clés Firebase/Apple se retrouvent aussi auprès de leurs consoles). Pour migrer ou sauvegarder l'état en une seule fois : `admin export` et `import` ([cli-push.fr.md](cli-push.fr.md) §9).

## En cas de problème

| Symptôme | Piste |
|---|---|
| `Providers: none` | `appsettings.Local.json` absent ou chemin de la clé faux ; droits `www-data` |
| Toute requête refusée (401) | le relais n'est pas lié à la passerelle (`admin list`, `admin requests`, `admin accept <id>`) |
| Requêtes refusées (403) | ce relais est banni : `admin unban <id>` puis nouvelle demande |
| Beaucoup de « occupé » (429) | maintenance activée (`admin status`), ou `max-concurrent` trop bas |
| `push_result.ok = false` à l'enregistrement | le relais n'a aucune passerelle liée (`admin list push` sur le relais) |
| 503 de la passerelle | plateforme sans identifiants (par exemple APNs non configuré) |
| 410 | jeton périmé : le relais le supprime, le téléphone en enverra un nouveau |
| `nginx -t` : « duplicate … zone » | le nom `freespeech_push` est déclaré deux fois dans les fichiers de `sites-enabled` |
| Pas de notification, aucune erreur | le téléphone économise la batterie : exclure FreeSpeech de l'optimisation, vérifier les notifications |
