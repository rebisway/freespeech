# Ligne de commande du serveur relais

`dotnet FreeSpeech.Relay.dll admin <commande>` administre un relais en marche, sans interface web et sans redémarrage : chaque changement s'applique tout de
suite et le serveur l'enregistre. Concepts : [ADMINISTRATION.fr.md](ADMINISTRATION.fr.md) ; installation : [INSTALL_RELAY.fr.md](INSTALL_RELAY.fr.md).
Sans `admin`, le même programme démarre le relais (`--urls http://127.0.0.1:5290` choisit l'adresse d'écoute) ; `check` contrôle l'installation.

## 1. Principes

**Où lancer.** Sur la machine du relais, dans le dossier du programme, ou depuis un autre poste par un tunnel SSH Le script `sources/deploy/install-shortcuts.sh` crée le raccourci `fsrelay` (lance `admin` en `www-data`, depuis n'importe quel dossier) : `fsrelay status`, ou `fsrelay` seul pour le prompt `relay>`.
(`ssh -N -L 15290:127.0.0.1:5290 utilisateur@serveur`, puis `--url http://127.0.0.1:15290`). L'API d'administration n'accepte que les appels venant de la machine
elle-même et exige le secret d'administration ; nginx ne la publie pas.

| Réglage | Rôle |
|---|---|
| `--url <adresse>` ou `FS_ADMIN_URL` | adresse locale du relais (défaut : `ASPNETCORE_URLS`, sinon `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` | secret d'administration. Absent : lu dans `Admin:Secret` de `appsettings.Local.json` du dossier du programme |

| Code de sortie | Sens |
|---|---|
| 0 | réussi |
| 1 | usage incorrect (commande inconnue, argument manquant, confirmation absente en script) |
| 2 | aucun secret d'administration disponible |
| 3 | serveur injoignable |
| 4 | refusé ou échec (mauvais secret, appel qui ne vient pas de la machine, identifiant inconnu ou ambigu, vérification refusée) |

**Identifiants.** Un identifiant d'utilisateur ou de serveur est une empreinte de 64 caractères hexadécimaux. Les commandes de traitement des demandes acceptent un
**préfixe de 6 caractères au moins** s'il est unique (« ambigu » sinon, code 4). Un **code de vérification** tel que `pending` l'affiche (`3FA9-C1B2-7E40`, tirets et casse sans importance) est accepté de même.

**Traitement en lot** (`accept`, `reject`). Sans identifiant ni `--all`, rien n'est fait. `--all` prend toute la file ; `--name t` (nom ou mot contient *t*) et `--older-than N`
(jours, `7` ou `7d`) la restreignent. Une confirmation `[y/N]` est demandée au clavier ; dans un script (entrée standard redirigée), `--yes` est obligatoire, sinon la
commande refuse (code 1).

**Sorties.** Les listes s'affichent en tableau ; `--json` (là où il existe) donne la réponse brute pour un script. `status`, `clients`, `siblings` affichent du JSON.

**Prompt interactif.** `dotnet FreeSpeech.Relay.dll admin` sans commande ouvre un prompt `relay>` (comme `asterisk -r` ou `mysql`) : on y tape les mêmes commandes,
sans répéter `admin`. `help` les liste, `exit`, `quit` ou Ctrl+D quittent. Les flèches haut/bas parcourent l'historique (200 lignes, gardé dans `~/.freespeech_admin_history`), gauche/droite, Début/Fin, Retour arrière et Suppr éditent la ligne, Ctrl+U l'efface. Une erreur n'arrête pas la session. Ctrl+C arrête la commande en cours (journal suivi, suivi
d'identité) et garde le prompt. Au prompt, `trace <id>` affiche en direct le trafic de l'identité jusqu'à Ctrl+C, qui termine aussi le suivi. Dans un script (entrée ou sortie
redirigée), la commande sans argument affiche l'aide et sort avec le code 1 ; `--shell` force le prompt en lisant les commandes sur l'entrée standard, une par ligne.

## 2. État et statut

| Commande | Effet |
|---|---|
| `version` | version, environnement d'exécution et durée de fonctionnement du serveur **en marche** (pas celle des fichiers sur disque) |
| `status` | public ou privé, mode d'accès, compteurs (voisins, clients autorisés, demandes en attente…) |
| `public` / `private` | participe ou non aux échanges entre serveurs |
| `drain <n>` | invite jusqu'à *n* clients à passer sur un voisin qui a de la place (maintenance) |
| `limits` | les limites de volume en vigueur : par identité sur 24 h, par identité sur 10 min, par adresse sur 24 h, et d'où elles viennent (réglage ou changées par l'administrateur) |
| `limits set [--identity-day T] [--identity-10min T] [--address-day T]` | change les limites données, tout de suite et durablement ; *T* = `1G`, `300M`, `500K` ou des octets (unités binaires), `0` ou `off` = pas de limite |
| `limits reset` | revient aux limites des réglages (`Server__MaxBytes…`) |
| `traffic [--limit N]` | qui a le plus envoyé sur les dernières 24 h : identités et adresses, les plus gros d'abord (10 par défaut) |

Le relais ne distingue pas un morceau de fichier d'un message : les limites comptent des **octets envoyés**. Elles sont gardées dans `limits.json` (`Server__LimitsFilePath`) et s'appliquent sans redémarrage ; les compteurs en cours sont conservés.

## 3. Qui peut s'inscrire

| Commande | Effet |
|---|---|
| `access open` / `access allowlist` | tout le monde sauf les bannis / seulement les identités autorisées ou invitées |
| `clients` | autorisés, bannis, invitations en attente |
| `connected` | les identités connectées **maintenant** (en mémoire seulement : rien n'est gardé de celles qui se sont connectées avant), avec leur état ici : autorisée, bannie ou ni l'un ni l'autre |
| `allow-connected` | autorise toutes les identités connectées maintenant (sauf les bannies) : à lancer avant `access allowlist`, pour que personne de présent ne soit refusé à sa prochaine connexion |
| `allow <id> [note]` | autorise une identité (64 caractères) |
| `remove <id>` | la retire de la liste et coupe ses connexions |
| `ban <id> [motif]` / `unban <id>` | bannit (refusé partout, même en mode ouvert, connexions coupées) / lève le ban |
| `invite [note] [--days N]` | crée une invitation à usage unique `fs-inv-…` (7 jours par défaut, 90 au plus ; le jeton s'affiche une fois) |
| `revoke <id-invitation>` | révoque une invitation non utilisée |

## 4. Demandes d'accès (serveur privé)

| Commande | Effet |
|---|---|
| `pending [--name t] [--limit N] [--offset N] [--json]` | demandes en attente, les plus anciennes d'abord (**code de vérification** à comparer avec le téléphone de la personne, date, nom et mot déclarés) |
| `accept <id…>` ou `accept --all [--name t] [--older-than J] [--yes]` | autorise les demandeurs |
| `reject <id…>` ou `reject --all [--name t] [--older-than J] [--ban] [--reason t] [--yes]` | refuse ; `--ban` bannit aussi l'identité |
| `requests open` / `requests close` | accepte ou non de nouvelles demandes (celles en attente restent) |

## 5. Voisins suggérés et adresses bannies

| Commande | Effet |
|---|---|
| `siblings` / `candidates` / `blacklist` | relais liés (voisins) / voisins suggérés / adresses bannies |
| `dismiss <url>` | oublie un voisin suggéré |
| `server-ban <url> [motif]` / `server-unban <url>` | bannit une adresse (voisin ou passerelle) : retirée des listes, refusée ensuite |

## 6. Liaison avec une passerelle push, un relais voisin ou un annuaire

Chaque serveur a sa paire de clés (`peer-identity.json`). Un lien n'existe que si **les deux** administrateurs l'ont accepté ; seule la clé publique est échangée.
Le type (`push`, `relay` ou `directory`) est obligatoire.

| Commande | Effet |
|---|---|
| `peer-id` | l'empreinte de ce relais, à lire à l'autre administrateur |
| `add <push\|relay\|directory> <url> [--fingerprint f] [--insecure]` | demande un lien ; l'empreinte du serveur demandé s'affiche (`--fingerprint` la vérifie). `https` obligatoire ; `http` pour cette machine ou un réseau privé, ou avec `--insecure`. Un relais demande une passerelle (`push`) ou un relais public (`relay`) |
| `list [type]` | tous les serveurs liés (`linked`), demandés (`asked`), refusés (`refused`) ou bannis (`banned`), tous types confondus si aucun n'est donné ; le contact déclaré par leur administrateur est affiché entre `<` `>` ; pour un annuaire, la colonne `LINK` dit où en est l'envoi des battements : `ok 35s` (dernier envoi réussi), `failing 12min (unreachable)` ou `failing 12min (refused 401)` (échecs depuis cette durée), `none yet` (rien d'envoyé depuis le démarrage) ; l'état est gardé en mémoire |
| `requests <push\|relay> [--name t] [--limit N] [--offset N] [--json]` | demandes reçues Chaque demande indique le contact déclaré par l'expéditeur et l'adresse d'où elle est venue. |
| `accept <push\|relay> <id…>` ou `--all [--name t] [--older-than J] [--yes]` | accepte ; refuse si l'adresse annoncée ne présente pas la clé de la demande (code 4) |
| `reject <push\|relay> <id…>` ou `--all … [--ban]` | refuse ; `--ban` interdit de redemander |
| `remove <push\|relay> <id>` | oublie un serveur (il peut redemander) |
| `ban <push\|relay> <id>` / `unban <push\|relay> <id>` | bannit / lève le ban |
| `peer-requests open` / `close` | accepte ou non les demandes entrantes |
| `peer-refresh` | demande tout de suite où en sont les demandes envoyées, et si les serveurs liés connaissent encore ce relais |

Réglages (non secrets, dans `appsettings.Local.json`) : `Peer:Name` (nom présenté), `Peer:Contact` (comment un autre administrateur peut joindre le vôtre pour vérifier qui demande) et `Peer:PublicUrl` (adresse de ce relais : obligatoire pour demander un autre relais).
Un lien entre relais exige que les deux soient publics (`admin public`).

Un **annuaire** (`add directory <url>`, accepté par l'administrateur de l'annuaire par `accept relay <id>`) est informé toutes les `Directory:HeartbeatSeconds` secondes (120) de la situation de ce relais : son pays (`settings set country`), les places qui lui restent (`settings set max-connections`, `-1` quand il n'indique aucune limite) et s'il accepte de nouveaux comptes, avec sa version et sa date. Il propose alors le relais aux clients qui ne connaissent aucun relais. Un relais n'est proposé que tant qu'il est **public**, que l'accès des clients est `open` et qu'il lui reste de la place ; un relais privé ne dit rien. `Peer:PublicUrl` doit être réglé (`self set public-url`) avant de demander un annuaire : l'annuaire vérifie le relais à cette adresse. Le nombre d'utilisateurs n'est jamais envoyé.

```bash
# Relais → passerelle : le relais demande, la passerelle accepte (commande côté passerelle : voir cli-push.fr.md)
dotnet FreeSpeech.Relay.dll admin add push https://push.exemple.org

# Deux relais publics voisins
dotnet FreeSpeech.Relay.dll admin public
dotnet FreeSpeech.Relay.dll admin add relay https://autre-relais.exemple.org
dotnet FreeSpeech.Relay.dll admin requests relay       # sur l'autre relais
dotnet FreeSpeech.Relay.dll admin accept relay 3fa9c1
```

## 7. Journaux et suivi

| Commande | Effet |
|---|---|
| `log` | niveaux de journalisation en cours |
| `log level <catégorie\|all> <niveau>` | change un niveau **à chaud** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`). Catégories : `relay`, `s2s`, `push`, `access`, `flags`, `admin`, `http` |
| `log reset` | revient aux niveaux de la configuration |
| `log tail [-n 100] [--level debug] [-f]` | dernières lignes en mémoire ; `-f` suit en continu, comme une console |
| `trace <id> [--minutes 15]` | suit **une** identité (8 hexadécimaux au moins) ; s'arrête seul, 60 minutes au plus |
| `trace list` / `trace stop <id\|all>` | suivis en cours / arrêt |

Le suivi d'une identité raconte aussi **chaque étape d'un réveil** : drapeau WAITING posé ou reçu, contact revenu en ligne alors qu'on l'attend (et réveil de l'expéditeur absent), passerelle choisie et résultat, réveil retenu (un seul par 30 s et par identité), absence de jeton, absence de passerelle liée, jeton retiré parce que le réseau push ne le connaît plus. Avec la trace des deux identités d'une conversation, la chaîne « message en attente → réveil → renvoi » se lit à la suite.

Les journaux ne contiennent que des métadonnées : identifiants raccourcis à 8 caractères, jamais de contenu, de clé ni de jeton d'appareil. « Qui écrit à qui » n'apparaît
qu'au niveau `debug` ou dans un suivi demandé.

## 8. Contrôle de l'installation

`dotnet FreeSpeech.Relay.dll check` (sans `admin`) dit ce qui manque : `Admin:Secret` absent, trop court ou resté à la valeur d'exemple. Code de sortie 1 s'il manque quelque
chose ; le script de mise à jour le lance à la fin.

## 9. Procédures courantes

```bash
# Serveur privé pour une famille
dotnet FreeSpeech.Relay.dll admin access allowlist
dotnet FreeSpeech.Relay.dll admin invite "Anna" --days 7      # donner le jeton à Anna, une seule fois

# Valider les personnes qui ont demandé l'accès
dotnet FreeSpeech.Relay.dll admin pending
dotnet FreeSpeech.Relay.dll admin accept 3fa9c1 b27e40
dotnet FreeSpeech.Relay.dll admin reject --all --older-than 7d --yes

# Diagnostiquer sans redémarrer
dotnet FreeSpeech.Relay.dll admin log level relay information
dotnet FreeSpeech.Relay.dll admin trace 3fa9c1d2 --minutes 10
dotnet FreeSpeech.Relay.dll admin log tail -f
dotnet FreeSpeech.Relay.dll admin log reset
```

## 10. Courrier : SMTP et notifications

Le serveur peut écrire à son administrateur. Tout se règle par commande (les mêmes routes de l'API d'administration servent à toute console) et s'applique tout de suite. Le compte est enregistré dans `appsettings.Local.json`
(`Smtp:*`, `Notify:*`), mot de passe compris : il n'est jamais relu ni affiché. Le fichier est réécrit à chaque changement : ses autres clés sont gardées, ses commentaires `//` disparaissent.

| Commande | Effet |
|---|---|
| `smtp show` | compte et interrupteurs en cours, sans le mot de passe (`password_set`), avec un exemple de sujet |
| `smtp set host\|port\|tls\|user\|from\|to\|prefix <valeur>` | `tls` : `starttls` (port 587), `implicit` (port 465) ou `none` ; `to` : une ou plusieurs adresses séparées par des virgules ; `user` vide : pas d'authentification |
| `smtp set password` | saisie sans écho, ou lue sur l'entrée standard, ou dans `FS_SMTP_PASSWORD` ; jamais en argument |
| `smtp clear` | oublie le compte et coupe les notifications |
| `smtp test [--to adresse]` | envoie un mail tout de suite et **affiche la réponse du serveur SMTP** : `ok: sent to …`, ou `FAILED at <étape>: <message>` (étapes `settings`, `connect`, `authenticate`, `send`). Code de sortie 0, ou 4 si le mail n'est pas parti. Une tentative dure au plus `Smtp:TimeoutSeconds` secondes (20 par défaut) : un hôte qui ne répond pas donne `FAILED at connect: no answer from <hôte>:<port> within 20 s` |
| `notify` | interrupteurs des notifications |
| `notify on\|off [peers\|abuse\|access\|system]` | tout, ou un seul type. Au départ les notifications sont coupées ; chaque type est actif une fois `notify on` donné |
| `notify send "<message>"` | envoie un mail à l'administrateur tout de suite (hors limite d'une heure) et affiche le résultat comme `smtp test` |

**Sujet.** `<préfixe> <événement>`. Le préfixe est libre (`smtp set prefix`), avec les variables `{kind}` (`RELAY`) et `{host}` (le serveur : l'hôte de `Peer:PublicUrl`, sinon le nom de la machine).
Par défaut `[{kind}] {host}`, par exemple `[RELAY] relay.exemple.org Link request waiting` ; `smtp set prefix "[relay.exemple.org]"` donne `[relay.exemple.org] Link request waiting`.

**Types de notification** (un mail n'est jamais envoyé pour un type coupé) :

| Type | Quand |
|---|---|
| `peers` | un autre serveur demande un lien : nom, adresse, empreinte à comparer, puis `accept` |
| `abuse` | abus ou attaque détectés : un identifiant ou une adresse dépasse sa limite de trafic, un serveur lié dépasse son quota, appels signés falsifiés ou rejoués, trop de demandes de lien depuis une adresse |
| `access` | serveur privé en `allowlist` : un client demande l'accès et attend votre décision (`admin pending`, puis `accept`) |
| `system` | au démarrage, un secret ou un réglage manque (la même alarme que le journal *Critical*) |

**Limite.** Pour qu'une rafale d'événements ne devienne pas une rafale de courriels, un type envoie au plus un mail par heure (`Notify:MinIntervalSeconds`, 3600) : le premier événement part tout de suite,
les suivants sont comptés et envoyés ensemble à la fin de l'heure (« +N more »). Un mail ne contient aucun identifiant de client, aucune adresse de client, aucun jeton.

`check` signale un compte à moitié réglé (`Smtp:User` sans `Smtp:Password`, notifications actives sans hôte, expéditeur ou destinataire).

## 11. Sauvegarde et restauration : `export` et `import`

| Commande | Effet |
|---|---|
| `export <fichier> [--force]` | enregistre l'état du serveur dans un fichier **chiffré par une phrase secrète** (8 caractères au moins ; saisie sans écho, ou `FS_STATE_PASSPHRASE`). Droits 600. Refuse d'écraser sans `--force` |
| `dotnet FreeSpeech.Relay.dll import <fichier> [--force]` | **sans `admin`, sur la machine, service arrêté** : remet les fichiers en place. Refuse de remplacer un état existant sans `--force`, qui garde chaque fichier remplacé à côté en `.before-import`. Code de sortie 0, 1 (usage) ou 4 (phrase secrète fausse, fichier d'un autre type de serveur, état existant) |

L'archive contient `siblings.json`, `access.json`, `settings.json`, `limits.json`, `peers.json` et `peer-identity.json`. Elle contient la clé privée du serveur, d'où le chiffrement. Elle ne contient **pas** `appsettings.Local.json` (secret d'administration, compte SMTP) : à recréer sur la nouvelle machine.
Pour migrer : installer le serveur selon [INSTALL_RELAY.fr.md](INSTALL_RELAY.fr.md) sans le démarrer, restaurer, régler `appsettings.Local.json`, démarrer.

```bash
dotnet FreeSpeech.Relay.dll admin export ~/relais.fsstate                    # ancienne machine
sudo systemctl stop freespeech-relay                                  # nouvelle machine, service arrêté
cd /var/www/freespeech_relay && sudo -u www-data env $(systemctl show freespeech-relay.service -p Environment --value) dotnet FreeSpeech.Relay.dll import ~/relais.fsstate
sudo systemctl start freespeech-relay
```

## 12. Réglages du service : `settings`

Les réglages du service (`Server__…`) se changent à chaud ; la configuration donne les valeurs de départ, un changement est appliqué tout de suite et conservé dans `settings.json`
(`Server__SettingsFilePath`), et `settings reset` revient à la configuration.

| Commande | Effet |
|---|---|
| `settings` | les réglages en vigueur, ceux de la configuration et ceux qui ont été changés |
| `settings set max-connections <n>` | au-delà de `n` connexions, le serveur propose un serveur voisin (0 : aucune limite) |
| `settings set max-message-bytes <octets>` | taille maximale d'un message (1 024 à 67 108 864) |
| `settings set s2s-rate <n>` | requêtes par minute et par serveur voisin (1 à 1 000 000) |
| `settings set country <code>` | le pays (deux lettres, ISO 3166) que ce relais indique aux annuaires auxquels il est lié (vide : aucun) |
| `settings set source-url <adresse>` | adresse du code source, servie par `GET /api/info` (vide : aucune) |
| `settings set max-access-requests <n>` / `access-request-days <jours>` | nombre de demandes d'accès gardées (serveur privé) / jours pendant lesquels une demande est gardée |
| `settings reset` | revient aux réglages de la configuration |

## 13. Présentation du serveur : `self`

| Commande | Effet |
|---|---|
| `self` | type, nom, adresse publique et empreinte et clé publique de ce serveur, comme les autres serveurs le voient |
| `key` | la clé publique de ce serveur seule, sur une ligne, à copier là où elle est demandée |
| `self set name <valeur>` | le nom présenté aux autres administrateurs (64 caractères au plus) |
| `self set public-url <valeur>` | l'adresse publique (`https://`, ou `http://` sur un réseau privé ; vide pour l'effacer) |
| `self set contact <valeur>` | comment un autre administrateur peut vérifier qui vous êtes quand vous lui demandez un lien : adresse de courriel, numéro de téléphone ou page (128 caractères au plus, vide pour l'effacer) ; envoyé, signé, avec chaque demande |

Le changement s'applique tout de suite (liaisons, sujet des mails, `status`) et est écrit dans `appsettings.Local.json` (`Peer:Name`, `Peer:PublicUrl`, `Peer:Contact`), donc conservé au redémarrage.
