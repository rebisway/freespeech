# Administrer ses serveurs FreeSpeech (relais et passerelle push)

*English version: [ADMINISTRATION.en.md](ADMINISTRATION.en.md). Installation : [INSTALL_RELAY.fr.md](INSTALL_RELAY.fr.md) et [INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md). Lignes de commande : [cli-relay.fr.md](cli-relay.fr.md) et [cli-push.fr.md](cli-push.fr.md). Protocole : [PROTOCOL.fr.md](PROTOCOL.fr.md) §10.*

Ce document décrit **la configuration actuelle** et comment la changer **sans interface web et sans redémarrer** : une
commande `admin` livrée avec chaque serveur. Elle suffit pour tenir un serveur public, un serveur privé ou une passerelle push.

## 1. Principe

- Un serveur ne stocke aucun message, mais il garde **en mémoire** qui est connecté, les marqueurs d'attente et les jetons push.
  Le redémarrer coupe toutes les connexions et efface cet état : on évite donc de le faire. Tout ce qui s'administre se règle
  **à chaud** et s'enregistre dans un petit fichier JSON (écriture atomique).
- On n'a besoin de redémarrer que pour les réglages de démarrage : port, chemins des fichiers, secret d'administration. Avant un redémarrage :
  `admin drain 100` (le relais invite 100 clients à passer sur un voisin) ou `admin maintenance on` (la passerelle répond
  « occupé », les relais utilisent une autre passerelle).
- L'API d'administration n'est **jamais** publique : elle exige le secret **et** un appel venant de la machine elle-même.

## 2. Se connecter à l'administration

La liste complète des commandes, leurs options et leurs codes de sortie est dans [cli-relay.fr.md](cli-relay.fr.md) (relais) et [cli-push.fr.md](cli-push.fr.md) (passerelle push).

À faire **sur le serveur** (ssh), ou sur votre poste avec un tunnel SSH (`ssh -L 5290:127.0.0.1:5290 ubuntu@relay.exemple.org`).

```bash
export FS_ADMIN_URL=http://127.0.0.1:5290         # relais ; la passerelle push est sur 5291
dotnet /var/www/freespeech_relay/FreeSpeech.Relay.dll admin status      # lancée dans ce dossier, elle lit le secret dans appsettings.Local.json
```

Le secret (`Admin:Secret`) se met **dans `appsettings.Local.json`**, à côté du programme, comme pour tous les secrets de FreeSpeech
(voir [INSTALL_RELAY.fr.md](INSTALL_RELAY.fr.md)). Depuis **un autre poste** (tunnel SSH), on le donne par `FS_ADMIN_SECRET`
dans l'environnement du terminal (jamais en argument).

| Réglage | Rôle |
|---|---|
| `--url …` ou `FS_ADMIN_URL` | adresse locale du serveur (défaut : `ASPNETCORE_URLS`, sinon `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` ou `Admin:Secret` | secret d'administration (jamais en argument de ligne de commande) |
| `Admin__AllowRemote=true` | autorise un appel qui ne vient pas de la machine : **réseau privé seulement** |

Codes de sortie : `0` ok, `1` usage, `2` pas de secret, `3` serveur injoignable, `4` refusé ou échec. La commande `help` liste tout.

## 3. Serveur relais : `FreeSpeech.Relay.dll admin …`

### 3.1 Public ou privé, et qui peut s'inscrire

Deux notions **distinctes** :

- **Public / privé** (`public`, `private`) : ne concerne que les échanges **entre serveurs**. Un serveur privé n'en fait aucun.
- **Mode d'accès** (`access open|allowlist`) : qui peut **s'inscrire** sur ce serveur. `open` = tout le monde sauf les bannis ;
  `allowlist` = seulement les identités autorisées ou invitées.

Un serveur « privé » au sens usuel (famille, association, entreprise) combine donc `private` **et** `access allowlist`.

| Commande | Effet |
|---|---|
| `status` | mode public/privé, mode d'accès, compteurs |
| `public` / `private` | participe ou non aux échanges entre serveurs |
| `access open` / `access allowlist` | qui peut s'inscrire |
| `clients` | autorisés, bannis, invitations en attente |
| `allow <id> [note]` | autorise une identité (64 caractères hexadécimaux) |
| `remove <id>` | la retire de la liste et **coupe ses connexions** |
| `ban <id> [motif]` / `unban <id>` | bannit : refusé partout, **même en mode ouvert**, connexions coupées |
| `invite [note] [--days N]` | crée une **invitation à usage unique** (jeton `fs-inv-…`, 7 jours par défaut, 90 au plus) |
| `revoke <id-invitation>` | révoque une invitation non utilisée |
| `pending [--name t] [--limit N] [--offset N] [--json]` | **demandes d'accès** en attente, les plus anciennes d'abord |
| `accept <id…>` ou `accept --all [--name t] [--older-than J] [--yes]` | autorise les demandeurs (voir ci-dessous) |
| `reject <id…>` ou `reject --all [--name t] [--older-than J] [--ban] [--reason t] [--yes]` | refuse les demandes, et bannit avec `--ban` |
| `requests open` / `requests close` | accepte ou non de nouvelles demandes (celles en attente restent) |

**Rejoindre un serveur (côté utilisateur).** L'utilisateur ouvre *Réglages → Serveurs* et ajoute l'adresse : c'est le **serveur qui dit à
l'appli** s'il est ouvert ou privé, l'utilisateur n'a rien à choisir. Un serveur ouvert se connecte aussitôt. Un serveur privé est affiché
« privé » avec deux boutons, deux voies. **Invitation** (*J'ai une invitation*) : l'administrateur crée l'invitation et la transmet
(message, voix, papier) ; l'utilisateur saisit le jeton. Le jeton est dépensé à la première inscription réussie ; ensuite l'identité est
« autorisée » et se reconnecte sans lui. **Demande d'accès** (*Demander l'accès*) : après un message de confirmation (son nom part au
serveur), la demande est envoyée et l'appli affiche « en attente de validation ». Si le serveur répond « accès refusé », l'appli l'indique
aussi. Quand l'administrateur change la règle d'inscription, les appareils connectés le savent aussitôt (événement `server_state`) et
l'appli montre le serveur en conséquence ; les autres l'apprennent à leur prochaine connexion.

**Traiter les demandes d'accès.** `pending` donne, pour chaque demande, un identifiant (12 caractères), la date, le nom et le
mot laissés par la personne ; `--json` donne tout, pour un script. `accept` et `reject` prennent un ou plusieurs identifiants :
**un préfixe de 6 caractères au moins suffit** s'il est unique (« ambigu » sinon : donnez-en plus). Pour traiter en lot,
`--all` prend toute la file, que `--name` (nom ou mot contient…) et `--older-than 7d` restreignent. Garde-fous : sans
identifiant ni `--all`, rien n'est fait ; `--all` demande confirmation au clavier, et **exige `--yes` dans un script** ; un
identifiant inconnu (déjà traité, expiré, mal tapé) est signalé et le code de sortie est 4, les autres étant traités quand même.
`reject --ban` bannit en plus l'identité : elle ne pourra plus redemander. Sans `--ban`, la personne peut redemander.

**Savoir qui demande : le code de vérification.** Le nom et le mot d'une demande sont **déclarés** par la personne, le serveur ne les vérifie pas
(les identités sont anonymes). Ce qui est prouvé, c'est la possession de la clé. Pour relier une demande à la personne qui tient le téléphone, chaque
demande porte un **code de vérification** : les 12 premiers caractères de l'identifiant, en majuscules, par groupes de quatre
(`3FA9-C1B2-7E40`). L'appli l'affiche à la personne quand elle envoie sa demande ; la personne le lit à l'administrateur par un **autre moyen**
(téléphone, en personne), qui le compare avec celui de `pending` (ou de la page *Clients* de la console) avant d'accepter. Un imposteur qui a
écrit le même nom ne peut pas connaître le code d'une identité qu'il ne détient pas. `accept` et `reject` acceptent le code tel qu'affiché (tirets
et casse sans importance). Pour une personne choisie à l'avance, l'**invitation** reste plus directe : le jeton remis par un canal sûr suffit.

Ce qu'il faut savoir : la demande n'est prise **qu'après la preuve de possession des clés** (on ne peut pas demander au nom d'un
autre) ; une personne qui redemande (appli fermée puis relancée, nom corrigé) **met à jour sa demande** sans la doubler ; le
texte reçu est nettoyé (pas de caractères de contrôle, 64 et 200 caractères au plus) ; la file est bornée
(`Server__MaxAccessRequests`, 500 par défaut) et les demandes **expirent** (`Server__AccessRequestDays`, 14 jours par défaut ;
une personne qui redemande renouvelle le délai). Si la file se remplit de demandes indésirables, `requests close` la ferme :
les inconnus reçoivent alors simplement « serveur privé ». **Côté utilisateur**, l'appli retente à chaque démarrage et à
intervalles réguliers : dès que l'administrateur a accepté, la connexion réussit, le serveur passe à « en ligne » dans la liste
et l'appli affiche un avis (console) ; il n'y a pas d'autre signal à attendre.

**Identifiants.** Un identifiant est l'empreinte de ses clés publiques (64 hexadécimaux) : il ne révèle rien d'autre. `allow` et
`ban` demandent l'identifiant **complet**, que l'on trouve dans la fiche de contact de la personne. En mode `allowlist`, `clients`
liste les identités autorisées (dont celles arrivées par invitation) avec leur identifiant complet : on y prend celui à bannir. En
mode `open`, le serveur **ne retient pas** qui s'est connecté (par conception) : bannir quelqu'un demande son identifiant complet,
par exemple celui qu'un de ses contacts vous signale. C'est pourquoi l'invitation est le moyen simple de tenir un serveur fermé.

**Groupes pour les membres.** Un serveur privé peut **nommer** une identité FreeSpeech comme son outil d'administration : un client ordinaire (nom, image,
description) qui crée des groupes pour les personnes autorisées sur ce serveur. Les personnes reçoivent une invitation « de l'administrateur » et la
valident d'un geste ; elles ne sont jamais ajoutées sans accord. Le serveur retient les clés publiques d'une personne autorisée à sa première connexion
(`GET /api/admin/clients/keys`) ; une personne qui ne s'est pas encore connectée ne peut pas encore être mise dans un groupe. Pour que l'outil puisse le
faire, le serveur doit annoncer son adresse publique (celle des réglages de l'appli) : `Peer:PublicUrl` dans `appsettings.Local.json`, repris dans
`GET /api/admin/status` (`public_url`). L'outil se nomme par `POST /api/admin/provisioners` (voir PROTOCOL.fr.md, « Outil d'administration d'un serveur privé »)
et se retire par `DELETE /api/admin/provisioners?id=`. Cela ne concerne que les serveurs privés.
### 3.2 Serveurs voisins (relais public)

`add relay <url>` / `requests relay` / `accept relay <id>` (§3.4, mêmes commandes qu'avec une passerelle) lient deux relais **publics** par leurs clés ; ils sont alors
voisins. Les deux ont réglé `Peer:PublicUrl`.

| Commande | Effet |
|---|---|
| `siblings` | relais liés (voisins) |
| `candidates` / `dismiss <url>` | voisins **suggérés** par un voisin de confiance (jamais promus seuls) |
| `blacklist` | serveurs bannis |
| `server-ban <url> [motif]` / `server-unban <url>` | bannit un serveur (voisin **ou** passerelle push) : retiré des listes et refusé ensuite |
| `drain <n>` | invite jusqu'à n clients à passer sur un voisin qui a de la place (maintenance) |
| `limits` / `limits set …` / `limits reset` | limites de volume envoyé par identité et par adresse : voir, changer tout de suite, revenir aux réglages (détail : `cli-relay.fr.md`) |
| `traffic [--limit N]` | qui a le plus envoyé sur les dernières 24 h |

### 3.3 Fichiers et sauvegarde

| Fichier | Contenu | Réglage du chemin |
|---|---|---|
| `siblings.json` | public/privé, candidats, bannis | `Server__SiblingsFilePath` |
| `access.json` | mode d'accès, autorisés, bannis, empreintes des invitations, demandes d'accès | `Server__AccessFilePath` |
| `peer-identity.json` | la **clé privée de ce serveur** (liaison avec les passerelles, §3.4) | même dossier que `access.json` (`Peer__StatePath`) |
| `peers.json` | serveurs liés, demandes envoyées et reçues, bannis | idem |
| `limits.json` | les limites de volume changées par l'administrateur (absent = celles des réglages) | `Server__LimitsFilePath` |

À sauvegarder (`peer-identity.json` est une clé privée : droits `600`). Un `access.json` illisible met le serveur en `allowlist` : il ne
s'ouvre jamais par accident. `Server__ClientAccess=allowlist` fixe le mode d'un serveur neuf, avant tout fichier.

### 3.4 Lier une passerelle push ou un relais voisin

Deux serveurs ne se font confiance que si **les deux administrateurs** ont dit oui, quel que soit celui qui a demandé. Chaque serveur
a sa propre paire de clés (`peer-identity.json`, créée au premier démarrage) : seule la clé **publique** est échangée, son empreinte
(64 hexadécimaux) sert d'identifiant. Rien de secret ne passe d'un administrateur à l'autre.

| Commande (relais) | Effet |
|---|---|
| `peer-id` | l'empreinte de ce serveur : à lire à l'autre administrateur pour qu'il vérifie |
| `add push <url> [--fingerprint f] [--insecure]` (ou `add relay <url>` pour un relais voisin public) | demande un lien à une passerelle. L'empreinte de la passerelle s'affiche (`--fingerprint` la vérifie : refus si elle diffère). `https` obligatoire ; `http` seulement sur cette machine ou un réseau privé, ou avec `--insecure` |
| `list push` | les passerelles liées (`linked`), demandées (`asked`, en attente de leur administrateur), refusées, bannies |
| `requests push [--name t] [--limit N] [--json]` | les demandes **reçues** d'une passerelle qui veut servir ce relais |
| `accept push <id…>` ou `--all [--name t] [--older-than J] [--yes]` | accepte (préfixe de 6 hexadécimaux au moins ; mêmes garde-fous que pour les clients) |
| `reject push <id…>` ou `--all … [--ban]` | refuse, et bannit avec `--ban` |
| `remove push <id>` / `ban push <id>` / `unban push <id>` | oublie, ou bannit, un serveur |
| `peer-requests open` / `close` | prend ou non de nouvelles demandes |
| `peer-refresh` | demande tout de suite où en sont les demandes envoyées |

La passerelle a les mêmes commandes (§4), pour les relais. **Déroulement** : l'administrateur du relais fait `add push https://push.exemple.org` ;
celui de la passerelle voit la demande (`requests`), compare l'empreinte avec celle que le relais lui lit au téléphone, puis `accept <id>` ;
le relais l'apprend tout seul en moins d'une minute (ou `peer-refresh`). Si chacun demande à l'autre, le lien est conclu aussitôt. Les
réveils sont alors **signés** par le relais (horodatage et protection contre le rejeu) ; une passerelle liée est utilisée pour les clients
du relais. Ce que reçoit une demande : le nom, l'adresse et l'empreinte annoncés, sans
confiance tant que l'administrateur n'a pas accepté ; à l'acceptation, le serveur vérifie que **l'adresse annoncée présente bien la clé
de la demande** (sinon refus : on ne peut pas demander au nom d'une adresse qu'on ne tient pas). La file est bornée (`Peer__MaxRequests`,
200), les demandes expirent (`Peer__RequestDays`, 14), le texte reçu est nettoyé. Réglages non secrets : `Peer:Name` (nom présenté) et
`Peer:PublicUrl` (adresse de ce serveur, nécessaire quand c'est la **passerelle** qui demande un relais). Un fichier `peers.json` illisible
est copié en `.corrupt` et plus aucun serveur n'est reconnu jusqu'à sa réparation.

## 4. Passerelle push : `FreeSpeech.Push.dll admin …`

Chaque **relais** qui appelle la passerelle a **sa propre paire de clés** : on peut en bannir un sans toucher aux autres.

| Commande | Effet |
|---|---|
| `status` | fournisseurs (fcm, apns), compteurs, limites |
| `relays` | relais liés, avec leurs compteurs (envoyés, échecs, occupé, limités) |
| `set min-interval <s>` | un réveil par jeton d'appareil au plus toutes les s secondes (défaut 15) |
| `set max-concurrent <n>` | au-delà, répond « occupé » (défaut 100) |
| `maintenance on` / `off` | répond « occupé » à tous : les relais passent à une autre passerelle |

`add relay <url>` demande un lien à un relais (il doit l'accepter ; `Peer:PublicUrl` doit être réglé), et
`requests`, `accept <id>`, `reject`, `list`, `remove`, `ban`, `peer-id` traitent les demandes des relais, exactement comme au §3.4 (le mot
`relay` est facultatif sur la passerelle). Les relais liés sont reconnus à la signature de leurs appels.

Fichiers : `settings.json`, `peer-identity.json` (clé privée de la passerelle) et `peers.json` dans `Push__StatePath` (`/var/lib/freespeech-push`).
Une passerelle **sans aucun relais lié** refuse tout (hors développement).

## 5. Journaux et suivi

Commun au relais et à la passerelle (`log …`, `trace …`).

| Commande | Effet |
|---|---|
| `log` | niveaux actuels et catégories |
| `log level <catégorie\|all> <niveau>` | change un niveau **maintenant** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`) |
| `log reset` | retour aux niveaux de la configuration |
| `log tail [-n 100] [--level debug] [-f]` | dernières lignes ; `-f` suit en direct (Ctrl+C pour finir) |
| `trace <id> [--minutes 15]` | suit **une** identité (8 caractères hexadécimaux ou plus) ; s'arrête seul, 60 minutes au plus |
| `trace list` / `trace stop <id\|all>` | liste, arrête |

Catégories du relais : `relay` (connexions, envois), `s2s`, `push`, `access`, `flags`, `admin`, `http`. Passerelle : `push`,
`relays`, `fcm`, `apns`, `http`. `all` règle le niveau par défaut.

La production garde `Logging__LogLevel__FreeSpeech=Warning` : `log tail` n'y montre alors que les avertissements. Pour diagnostiquer :
`admin log level relay information`, observer, puis `admin log reset`.

### Règles de confidentialité (non négociables)

- Un journal ne contient que des **métadonnées**. Jamais le contenu d'un message, jamais une clé, jamais un **jeton d'appareil**,
  jamais la charge chiffrée (le niveau `debug` indique seulement le mode de chiffrement et la taille).
- Les identifiants y sont **raccourcis à 8 caractères**.
- « Qui écrit à qui » n'apparaît **pas** au niveau Information : seulement en `debug` (raccourci) ou dans un `trace` demandé.
- Un `trace` est limité à 60 minutes, s'arrête seul et laisse une ligne dans le journal. Les lignes restent **en mémoire** (2000).
- Dites-le à vos utilisateurs si vous activez un suivi : c'est de la bonne pratique, et pour un serveur public c'est une obligation
  de transparence.

## 6. Sécurité de l'administration

- Secret long et aléatoire (`openssl rand -hex 32`), propre à chaque serveur. Vide = **tout refusé**.
- Appel **de la machine elle-même** uniquement ; nginx ne publie pas l'API (`location /api/admin { return 404; }`). Même si ce bloc
  manquait, l'adresse transmise par nginx n'est pas locale : l'appel serait refusé.
- Le secret d'administration n'est jamais relu par la commande ni par l'API.
- Un jeton d'invitation ne se montre qu'une fois : notez-le tout de suite.
- Tout est réglable à chaud ; un changement est enregistré au même instant (écriture atomique : pas de fichier à moitié écrit).

## 7. Référence de l'API locale

Utile pour des scripts ; la commande `admin` n'en est qu'une enveloppe. En-tête `X-Admin-Secret`, JSON.

**Relais** (`/api/admin/…`) : `GET status`, `POST status {is_public}`, `GET access`, `POST access/mode {mode}`,
`POST clients {id,note}`, `DELETE clients?id=`, `POST clients/ban {id,reason}`, `DELETE clients/ban?id=`,
`POST invitations {note,days}` → `{token}`, `DELETE invitations?id=`, `GET siblings`, `GET/DELETE candidates`,
`GET/POST/DELETE blacklist`, `POST drain {count}`, `GET/POST/DELETE limits {identity_per_day,identity_per_10_minutes,address_per_day}`, `GET traffic?limit=`, `GET/POST/DELETE peers…`, `GET log`, `POST log/level {category,level}`,
`DELETE log/level`, `GET log/tail?lines=&level=&follow=`, `GET/POST/DELETE trace`.

**Passerelle** (`/admin/…`) : `GET status`, `GET relays`, `GET/POST/DELETE peers…`, `GET settings`, `POST settings {minIntervalSeconds,maxConcurrent,maintenance}`,
et les mêmes `log…` et `trace…`.

Exemple :

```bash
curl -s -H "X-Admin-Secret: $FS_ADMIN_SECRET" -H "Content-Type: application/json" \
     -d '{"note":"Anna","days":7}' http://127.0.0.1:5290/api/admin/invitations
```

## 8. Dépannage

| Symptôme | Piste |
|---|---|
| `Refused: wrong or missing admin secret, or not called from the machine itself` | secret absent ou faux, ou appel qui ne vient pas de la machine (tunnel SSH, pas nginx) |
| `Cannot reach … at http://127.0.0.1:5000` | mauvaise adresse : régler `--url` ou `FS_ADMIN_URL` (le service écoute sur 5290 / 5291) |
| `Not found` en supprimant | rien à retirer, ou nginx cache `/api/admin` (utiliser l'adresse locale) |
| Un utilisateur dit « serveur privé » | il n'est pas dans `clients` : `allow <id>` ou `invite` |
| Un utilisateur dit « accès refusé » | il est banni : `clients`, puis `unban <id>` |
| `log tail` vide | niveau `Warning` en production : `log level relay information` |
| Après un redémarrage, le relais a oublié ses voisins ou refuse tout le monde | `siblings.json` ou `access.json` illisible (édité à la main avec une erreur de syntaxe) : le journal dit `Could not parse`. Un `access.json` illisible met le serveur en `allowlist` ; un `siblings.json` illisible repart vide. Corriger le fichier (ou le restaurer) puis redémarrer **hors des heures d'usage**. Préférer la commande `admin` à l'édition à la main |
