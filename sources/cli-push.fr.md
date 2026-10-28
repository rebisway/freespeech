# Ligne de commande de la passerelle push

`dotnet FreeSpeech.Push.dll admin <commande>` administre une passerelle en marche, sans interface web et sans redémarrage : chaque changement s'applique tout de suite
et la passerelle l'enregistre. Concepts : [ADMINISTRATION.fr.md](ADMINISTRATION.fr.md) §4 ; installation : [INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md).
Sans `admin`, le même programme démarre la passerelle (`--urls http://127.0.0.1:5291`) ; `check` contrôle l'installation.

## 1. Principes

**Où lancer.** Sur la machine de la passerelle, dans le dossier du programme, ou depuis un autre poste par un tunnel SSH Le script `sources/deploy/install-shortcuts.sh` crée le raccourci `fspush` (lance `admin` en `www-data`, depuis n'importe quel dossier) : `fspush status`, ou `fspush` seul pour le prompt `push>`.
(`ssh -N -L 15291:127.0.0.1:5291 utilisateur@serveur`, puis `--url http://127.0.0.1:15291`). L'API d'administration n'accepte que les appels venant de la machine
elle-même et exige le secret d'administration ; nginx ne la publie pas (`/admin` est fermé).

| Réglage | Rôle |
|---|---|
| `--url <adresse>` ou `FS_ADMIN_URL` | adresse locale de la passerelle (défaut : `ASPNETCORE_URLS`, sinon `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` | secret d'administration. Absent : lu dans `Admin:Secret` de `appsettings.Local.json` du dossier du programme |

| Code de sortie | Sens |
|---|---|
| 0 | réussi |
| 1 | usage incorrect (commande inconnue, argument manquant, confirmation absente en script) |
| 2 | aucun secret d'administration disponible |
| 3 | passerelle injoignable |
| 4 | refusé ou échec (mauvais secret, appel qui ne vient pas de la machine, identifiant inconnu ou ambigu, vérification refusée) |

**Identifiants.** Un identifiant de relais est l'empreinte de 64 caractères hexadécimaux de sa clé. Les commandes de traitement des demandes acceptent un **préfixe de
6 caractères au moins** s'il est unique (« ambigu » sinon, code 4).

**Traitement en lot** (`accept`, `reject`). Sans identifiant ni `--all`, rien n'est fait. `--all` prend toute la file ; `--name t` (nom ou adresse contient *t*) et
`--older-than N` (jours, `7` ou `7d`) la restreignent. Une confirmation `[y/N]` est demandée au clavier ; dans un script (entrée standard redirigée), `--yes` est
obligatoire, sinon la commande refuse (code 1).

**Prompt interactif.** `dotnet FreeSpeech.Push.dll admin` sans commande ouvre un prompt `push>` (comme `asterisk -r` ou `mysql`) : on y tape les mêmes commandes,
sans répéter `admin`. `help` les liste, `exit`, `quit` ou Ctrl+D quittent. Les flèches haut/bas parcourent l'historique (200 lignes, gardé dans `~/.freespeech_admin_history`), gauche/droite, Début/Fin, Retour arrière et Suppr éditent la ligne, Ctrl+U l'efface. Une erreur n'arrête pas la session. Ctrl+C arrête la commande en cours (journal suivi, suivi
d'identité) et garde le prompt. Au prompt, `trace <id>` affiche en direct le trafic de l'identité jusqu'à Ctrl+C, qui termine aussi le suivi. Dans un script (entrée ou sortie
redirigée), la commande sans argument affiche l'aide et sort avec le code 1 ; `--shell` force le prompt en lisant les commandes sur l'entrée standard, une par ligne.

## 2. État et réglages

| Commande | Effet |
|---|---|
| `version` | version, environnement d'exécution et durée de fonctionnement de la passerelle **en marche** (pas celle des fichiers sur disque) |
| `status` | fournisseurs (fcm, apns), compteurs, limites, maintenance |
| `set min-interval <secondes>` | un réveil par jeton d'appareil au plus toutes les *N* secondes (défaut 15) |
| `set max-concurrent <n>` | au-delà de *n* réveils simultanés, répond « occupé » (défaut 100) |
| `maintenance on` / `maintenance off` | répond « occupé » à tout : les relais utilisent une autre passerelle, un redémarrage ne perd rien |

## 3. Relais autorisés

| Commande | Effet |
|---|---|
| `relays` | relais liés, avec leurs compteurs (envoyés, échecs, occupé, limités) |

## 4. Liaison avec un relais

Chaque serveur a sa paire de clés (`peer-identity.json`). Un lien n'existe que si **les deux** administrateurs l'ont accepté ; seule la clé publique est échangée.
Une passerelle ne se lie qu'à des relais : le type `relay` est facultatif dans ces commandes.

| Commande | Effet |
|---|---|
| `peer-id` | l'empreinte de cette passerelle, à lire à l'administrateur du relais |
| `add relay <url> [--fingerprint f] [--insecure]` | demande un lien à un relais ; son empreinte s'affiche (`--fingerprint` la vérifie). `https` obligatoire ; `http` pour cette machine ou un réseau privé, ou avec `--insecure`. `Peer:PublicUrl` doit être réglé |
| `list [type]` | tous les serveurs liés (`linked`), demandés (`asked`), refusés (`refused`) ou bannis (`banned`), tous types confondus si aucun n'est donné ; le contact déclaré par leur administrateur est affiché entre `<` `>` ; pour un relais, la colonne `LINK` donne l'âge du dernier appel reçu (`ok 35s`, `none yet` depuis le démarrage ; en mémoire) |
| `requests [--name t] [--limit N] [--offset N] [--json]` | demandes reçues (identifiant de 12 caractères, date, nom, adresse) Chaque demande indique le contact déclaré par l'expéditeur et l'adresse d'où elle est venue. |
| `accept <id…>` ou `accept --all [--name t] [--older-than J] [--yes]` | accepte ; refuse si l'adresse annoncée ne présente pas la clé de la demande (code 4) |
| `reject <id…>` ou `reject --all … [--ban]` | refuse ; `--ban` interdit de redemander |
| `remove <id>` | oublie un relais (il peut redemander) |
| `ban <id>` / `unban <id>` | bannit / lève le ban |
| `peer-requests open` / `close` | accepte ou non les demandes entrantes |
| `peer-refresh` | demande tout de suite où en sont les demandes envoyées, et si les relais liés connaissent encore cette passerelle |

Réglages (non secrets, dans `appsettings.Local.json`) : `Peer:Name` (nom présenté), `Peer:Contact` (comment un autre administrateur peut joindre le vôtre pour vérifier qui demande) et `Peer:PublicUrl` (adresse de cette passerelle : obligatoire pour demander un relais).

```bash
# Un relais demande ; la passerelle accepte
dotnet FreeSpeech.Push.dll admin requests
dotnet FreeSpeech.Push.dll admin accept 3fa9c1          # préfixe de l'empreinte lue dans « requests »

# La passerelle demande un relais ; le relais accepte (côté relais : voir cli-relay.fr.md)
dotnet FreeSpeech.Push.dll admin add relay https://relais.exemple.org
```

## 5. Journaux et suivi

| Commande | Effet |
|---|---|
| `log` | niveaux de journalisation en cours |
| `log level <catégorie\|all> <niveau>` | change un niveau **à chaud** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`). Catégories : `push`, `relays`, `fcm`, `apns`, `http` |
| `log reset` | revient aux niveaux de la configuration |
| `log tail [-n 100] [--level debug] [-f]` | dernières lignes en mémoire ; `-f` suit en continu |
| `trace <id> [--minutes 15]` / `trace list` / `trace stop <id\|all>` | suit une identité (60 minutes au plus) / suivis en cours / arrêt |

Les journaux ne contiennent que des métadonnées : aucun jeton d'appareil, aucun contenu.

## 6. Contrôle de l'installation

`dotnet FreeSpeech.Push.dll check` (sans `admin`) dit ce qui manque : `Admin:Secret` absent, trop court ou resté à la valeur d'exemple ; fichier du compte de service Firebase
(`Fcm:ServiceAccountFile`) absent ou introuvable ; aucun relais autorisé. Code de sortie 1 s'il manque quelque chose ; le script de mise à jour le lance à la fin.

## 7. Procédures courantes

```bash
# Autoriser un relais
dotnet FreeSpeech.Push.dll admin peer-id                 # lire l'empreinte à l'administrateur du relais
dotnet FreeSpeech.Push.dll admin requests
dotnet FreeSpeech.Push.dll admin accept 3fa9c1

# Mettre la passerelle en maintenance puis la remettre
dotnet FreeSpeech.Push.dll admin maintenance on
dotnet FreeSpeech.Push.dll admin maintenance off
```

## 8. Courrier : SMTP et notifications

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

**Sujet.** `<préfixe> <événement>`. Le préfixe est libre (`smtp set prefix`), avec les variables `{kind}` (`PUSH`) et `{host}` (le serveur : l'hôte de `Peer:PublicUrl`, sinon le nom de la machine).
Par défaut `[{kind}] {host}`, par exemple `[PUSH] push.exemple.org Link request waiting` ; `smtp set prefix "[relay.exemple.org]"` donne `[relay.exemple.org] Link request waiting`.

**Types de notification** (un mail n'est jamais envoyé pour un type coupé) :

| Type | Quand |
|---|---|
| `peers` | un autre serveur demande un lien : nom, adresse, empreinte à comparer, puis `accept` |
| `abuse` | abus ou attaque détectés : appels signés falsifiés ou rejoués, trop de demandes de lien depuis une adresse |
| `access` | sans objet pour une passerelle push (aucun client) |
| `system` | au démarrage, un secret ou un réglage manque (la même alarme que le journal *Critical*) |

**Limite.** Pour qu'une rafale d'événements ne devienne pas une rafale de courriels, un type envoie au plus un mail par heure (`Notify:MinIntervalSeconds`, 3600) : le premier événement part tout de suite,
les suivants sont comptés et envoyés ensemble à la fin de l'heure (« +N more »). Un mail ne contient aucun identifiant de client, aucune adresse de client, aucun jeton.

`check` signale un compte à moitié réglé (`Smtp:User` sans `Smtp:Password`, notifications actives sans hôte, expéditeur ou destinataire).

## 9. Sauvegarde et restauration : `export` et `import`

| Commande | Effet |
|---|---|
| `export <fichier> [--force]` | enregistre l'état du serveur dans un fichier **chiffré par une phrase secrète** (8 caractères au moins ; saisie sans écho, ou `FS_STATE_PASSPHRASE`). Droits 600. Refuse d'écraser sans `--force` |
| `dotnet FreeSpeech.Push.dll import <fichier> [--force]` | **sans `admin`, sur la machine, service arrêté** : remet les fichiers en place. Refuse de remplacer un état existant sans `--force`, qui garde chaque fichier remplacé à côté en `.before-import`. Code de sortie 0, 1 (usage) ou 4 (phrase secrète fausse, fichier d'un autre type de serveur, état existant) |

L'archive contient `settings.json`, `peers.json` et `peer-identity.json`. Elle contient la clé privée du serveur, d'où le chiffrement. Elle ne contient **pas** `appsettings.Local.json` (secret d'administration, compte SMTP) : à recréer sur la nouvelle machine.
Pour migrer : installer le serveur selon [INSTALL_PUSH.fr.md](INSTALL_PUSH.fr.md) sans le démarrer, restaurer, régler `appsettings.Local.json`, démarrer.

```bash
dotnet FreeSpeech.Push.dll admin export ~/push.fsstate                    # ancienne machine
sudo systemctl stop freespeech-push                                  # nouvelle machine, service arrêté
cd /var/www/freespeech_push && sudo -u www-data env $(systemctl show freespeech-push.service -p Environment --value) dotnet FreeSpeech.Push.dll import ~/push.fsstate
sudo systemctl start freespeech-push
```

## 10. Présentation du serveur : `self`

| Commande | Effet |
|---|---|
| `self` | type, nom, adresse publique et empreinte et clé publique de ce serveur, comme les autres serveurs le voient |
| `key` | la clé publique de ce serveur seule, sur une ligne, à copier là où elle est demandée |
| `self set name <valeur>` | le nom présenté aux autres administrateurs (64 caractères au plus) |
| `self set public-url <valeur>` | l'adresse publique (`https://`, ou `http://` sur un réseau privé ; vide pour l'effacer) |
| `self set contact <valeur>` | comment un autre administrateur peut vérifier qui vous êtes quand vous lui demandez un lien : adresse de courriel, numéro de téléphone ou page (128 caractères au plus, vide pour l'effacer) ; envoyé, signé, avec chaque demande |

Le changement s'applique tout de suite (liaisons, sujet des mails, `status`) et est écrit dans `appsettings.Local.json` (`Peer:Name`, `Peer:PublicUrl`, `Peer:Contact`), donc conservé au redémarrage.
