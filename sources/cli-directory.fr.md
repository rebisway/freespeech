# Ligne de commande de l'annuaire

`dotnet FreeSpeech.Directory.dll admin <commande>` administre un annuaire en marche, sans interface web et sans redémarrage : chaque changement s'applique tout de suite et l'annuaire l'enregistre.
Protocole : [PROTOCOL.fr.md](PROTOCOL.fr.md) §10 « Annuaire » ; installation : [INSTALL_DIRECTORY.fr.md](INSTALL_DIRECTORY.fr.md).
Sans `admin`, le même programme démarre l'annuaire (`--urls http://127.0.0.1:5294`) ; `check` contrôle l'installation et `import` restaure un état (§6).

## 1. Principes

**Où l'exécuter.** Sur la machine de l'annuaire, dans le dossier du programme, ou depuis un autre ordinateur par un tunnel SSH. L'API d'administration ne répond qu'aux appels faits depuis la machine elle-même et exige le secret d'administration ; nginx ne la publie pas (`/admin` est fermé). Le script `sources/deploy/install-shortcuts.sh` crée le raccourci `fsdir` (lance `admin` en `www-data`, depuis n'importe quel dossier) : `fsdir status`, ou `fsdir` seul pour le prompt `directory>`.

| Réglage | Rôle |
|---|---|
| `--url <adresse>` ou `FS_ADMIN_URL` | l'adresse locale de l'annuaire (par défaut : `ASPNETCORE_URLS`, sinon `http://127.0.0.1:5000`) |
| `FS_ADMIN_SECRET` | le secret d'administration. À défaut, lu dans `Admin:Secret` de `appsettings.Local.json` du dossier du programme |

| Code de sortie | Signification |
|---|---|
| 0 | réussi |
| 1 | mauvais usage (commande inconnue, argument manquant, confirmation absente dans un script) |
| 2 | aucun secret d'administration disponible |
| 3 | annuaire injoignable |
| 4 | refusé ou échoué (mauvais secret, appel ne venant pas de la machine, identifiant inconnu ou ambigu, vérification refusée) |

**Identifiants.** L'identifiant d'un relais est l'empreinte hexadécimale de 64 caractères de sa clé ; un **préfixe de 6 caractères ou plus** est accepté s'il est unique. Sans commande, un invite interactif s'ouvre quand une personne tape.

## 2. Relais proposés

| Commande | Effet |
|---|---|
| `servers` | les relais acceptés qui ont envoyé un battement depuis `expiry-minutes` minutes : adresse, pays, places restantes (`-1` : sans limite), accueil de nouveaux comptes, version et date du relais, adresse vérifiée (`reachable`), dernier battement, et `listed` (proposé aux clients en ce moment) |

Un relais est proposé s'il est lié, a parlé récemment, répond à son adresse avec la clé acceptée, accepte de nouveaux comptes et a de la place. L'annuaire ne connaît ni le nombre d'utilisateurs d'un relais ni aucun client.

## 3. Réglages à chaud

| Commande | Effet |
|---|---|
| `settings` | les réglages en vigueur, ceux de la configuration et ceux qui ont été changés |
| `settings set expiry-minutes <n>` | minutes sans battement avant d'écarter un relais (1 à 1 440 ; 10 par défaut, `Directory__ExpiryMinutes`) |
| `settings set probe-minutes <n>` | minutes entre deux vérifications de l'adresse des relais (1 à 1 440 ; 30 par défaut, `Directory__ProbeMinutes`) |
| `settings set list-size <n>` | relais proposés par réponse quand le client n'en demande pas un nombre (1 à 10 ; 3 par défaut, `Directory__ListSize`) |
| `settings reset` | revient aux réglages de la configuration |

Les changements sont gardés dans `settings.json` (dossier d'état, `Directory__StatePath`, `directory-state` par défaut).

## 4. Liaison avec les relais

Chaque serveur a sa paire de clés (`peer-identity.json`) ; un lien n'existe que si **les deux** administrateurs l'ont accepté. Le type (`relay`) peut être omis.

| Commande | Effet |
|---|---|
| `peer-id` | l'empreinte de cet annuaire, à comparer avec l'autre administrateur. La clé publique affichée par `GET /peer/identity` est celle que les applications connaissent (`FsDirectories`) |
| `add relay <url> [--fingerprint f] [--insecure]` | demande un lien à un relais ; son administrateur accepte par `accept directory <id>` |
| `list [type]` | tous les serveurs liés (`linked`), demandés (`asked`), refusés (`refused`) ou bannis (`banned`), tous types confondus si aucun n'est donné ; le contact déclaré par leur administrateur est affiché entre `<` `>` |
| `requests [--name t] [--limit N] [--offset N] [--json]` | demandes reçues des relais (nom, adresse, empreinte) Chaque demande indique le contact déclaré par l'expéditeur et l'adresse d'où elle est venue. |
| `accept <id…>` ou `--all [--name t] [--older-than D] [--yes]` | accepte ; refuse si l'adresse annoncée ne présente pas la clé de la demande (code 4) |
| `reject <id…>` ou `--all … [--ban]` | refuse ; `--ban` interdit de redemander |
| `remove <id>` | oublie un relais (il disparaît des listes ; il peut redemander) |
| `ban <id>` / `unban <id>` | bannit / lève le bannissement |
| `peer-requests open` / `close` | prend les demandes entrantes, ou non |
| `peer-refresh` | demande maintenant où en sont les demandes envoyées |

L'adresse de l'annuaire (`Peer:PublicUrl`, voir `self` ci-dessous) est obligatoire. `Peer:Contact` indique comment un autre administrateur peut joindre le vôtre pour vérifier qui demande.

```bash
# Côté relais : le relais demande à l'annuaire
dotnet FreeSpeech.Relay.dll admin add directory https://directory1.exemple.org
# Côté annuaire : l'administrateur de l'annuaire accepte
dotnet FreeSpeech.Directory.dll admin requests
dotnet FreeSpeech.Directory.dll admin accept 3fa91c
dotnet FreeSpeech.Directory.dll admin servers
```

## 5. Courrier : SMTP et notifications

Les commandes `smtp` et `notify` sont celles du relais ([cli-relay.fr.md](cli-relay.fr.md) §10) : `smtp show`, `smtp set host|port|tls|user|from|to|prefix <valeur>`, `smtp set password`, `smtp clear`, `smtp test [--to adresse]`, `notify`, `notify on|off [type]`, `notify send "<message>"`.
Le sujet est `<début> <événement>` ; le début par défaut est `[DIRECTORY] <hôte>` (`{kind}` et `{host}` sont remplacés). Types de notification pour l'annuaire :

| Type | Quand |
|---|---|
| `peers` | un relais demande un lien : nom, adresse, empreinte à comparer, puis `accept` |
| `abuse` | appels signés falsifiés ou rejoués, trop de demandes de lien ou de liste depuis une adresse |
| `system` | au démarrage, un secret ou un réglage manque |

Un type envoie au plus un mail par heure (`Notify:MinIntervalSeconds`), sans aucune adresse de client.

## 6. Identité, sauvegarde, version

| Commande | Effet |
|---|---|
| `self` | type, nom, adresse publique et empreinte et clé publique de cet annuaire |
| `key` | la clé publique de cet annuaire seule, sur une ligne : la valeur à mettre après `url|` dans `FsDirectories` (`https://directory1.exemple.org|<clé>`) |
| `self set name <valeur>` / `self set public-url <valeur>` | le nom présenté aux relais ; l'adresse publique (`https://`) ; écrits dans `appsettings.Local.json` et appliqués tout de suite |
| `self set contact <valeur>` | comment un autre administrateur peut vérifier qui vous êtes quand vous lui demandez un lien : adresse de courriel, numéro de téléphone ou page (128 caractères au plus, vide pour l'effacer) ; envoyé, signé, avec chaque demande |
| `export <fichier> [--force]` | enregistre l'état (clé, relais acceptés, réglages) dans un fichier **chiffré par une phrase secrète** (8 caractères au moins ; saisie sans écho, ou `FS_STATE_PASSPHRASE`) |
| `dotnet FreeSpeech.Directory.dll import <fichier> [--force]` | **sans `admin`, sur la machine, service arrêté** : remet les fichiers en place (`.before-import` garde ceux qui sont remplacés). Codes 0, 1 ou 4 |
| `version` | version, date de publication, environnement d'exécution et durée de fonctionnement |

L'archive ne contient pas `appsettings.Local.json` (secret d'administration, compte SMTP). Pour migrer sans changer d'adresse ni de clé : `export`, installation sur la nouvelle machine sans démarrer, `import`, `appsettings.Local.json`, démarrage, DNS.

## 7. Journaux et suivi

| Commande | Effet |
|---|---|
| `log` | niveaux de journalisation en cours |
| `log level <catégorie\|all> <niveau>` | change un niveau **à chaud** (`trace`, `debug`, `information`, `warning`, `error`, `critical`, `none`) ; catégories : `directory`, `http` |
| `log reset` | revient aux niveaux de la configuration |
| `log tail [-n 100] [--level debug] [-f]` | dernières lignes en mémoire ; `-f` continue de suivre |
| `trace <id> [--minutes 15]` / `trace list` / `trace stop <id\|all>` | suivi d'une identité (60 minutes au plus) |

L'annuaire ne journalise aucune adresse de client.

## 8. Contrôle de l'installation

`dotnet FreeSpeech.Directory.dll check` (sans `admin`) liste ce qui manque : `Admin:Secret` absent, trop court ou resté à la valeur d'exemple, `Peer:PublicUrl` absent, aucun relais accepté (quand `Directory__StatePath` est accessible), compte SMTP à moitié réglé. Code de sortie 1 s'il manque quelque chose. La même liste est journalisée à chaque démarrage au niveau *Critical* et envoyée par courriel (type `system`) quand les notifications sont actives. Le script de mise à jour la lance.
