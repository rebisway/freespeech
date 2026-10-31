# Recherche de mise à jour des clients (mise de côté)

Fonctionnalité complète, compilée et testée, retirée de l'arborescence de travail. Elle n'est ni publiée ni dans le protocole.

## Fonctionnement

Les builds APK, Windows et Linux peuvent chercher une version plus récente ; le bundle Google Play n'a pas de recherche. La recherche ne parle qu'au site de
l'éditeur, dont l'adresse (`FsUpdateUrl`) et la clé publique Ed25519 (`FsUpdateKey`) sont choisies à la compilation (`sources/Directory.Build.props`, remplacées par
`sources/freespeech.defaults.props`). Sans `FsUpdateUrl`, il n'y a ni recherche ni bouton (cas de tout build personnalisé).

- Le site sert `update/latest.json` et sa signature détachée `update/latest.json.sig` (Ed25519, base64, sur les octets exacts du fichier).
- Le manifeste contient `format` (1), `published`, `programs` (`android-apk` : `version`, `build`, `date` ; `windows` et `linux` : `version`, `date`) et
  `download` (adresse `https` avec `{lang}`). Refus si la signature est fausse, si le manifeste a plus de 400 jours, si `download` n'est pas en `https` ou si le
  programme manque.
- Une version est disponible si elle est supérieure, ou égale avec un numéro de build supérieur (Android). Rien n'est téléchargé : le client ouvre la page de
  téléchargement.
- Recherche à la demande dans Réglages, ou au démarrage si l'utilisateur l'a activé (désactivé par défaut) ; une version trouvée au démarrage est annoncée une fois.

## Contenu du dossier

- `modifications.patch` : `svn diff` des fichiers existants modifiés (csproj, props, scripts de build, ARCHITECTURE fr/en, Réglages mobile et desktop, 10 fichiers
  de langue, `ClientService.cs`, `DesktopSession.cs`).
- `nouveaux/` : les fichiers créés, avec leur chemin relatif à la racine du dépôt.
  - `sources/freespeech.client/Update/UpdateChecker.cs` (bibliothèque : vérification de signature, comparaison de versions)
  - `sources/freespeech.tests/UpdateCheckerTests.cs` (5 tests)
  - `sources/freespeech.mobile/Services/UpdateService.cs`, `sources/freespeech.desktop/Services/UpdateService.cs`
  - `sources/tools/updatesign/` (`Program.cs`, `updatesign.csproj` : crée la clé, écrit et signe le manifeste)
  - `scripts/make-update-manifest.ps1`

## Remettre en place

Depuis la racine du dépôt :

```
patch -p0 < sources/mise_a_jour/modifications.patch
```

(ou `svn patch sources/mise_a_jour/modifications.patch`), puis copier le contenu de `nouveaux/` à la racine et faire `svn add` sur chaque fichier et dossier
copié (`sources/freespeech.client/Update`, `sources/tools/updatesign`, etc.). Le patch a été fait sur la révision 354 ; en cas de décalage, appliquer à la main à partir
des fichiers de `nouveaux/` et des hunks du patch.

## Mise en service

1. `scripts\make-update-manifest.ps1 -NewKey` : crée `%USERPROFILE%\.freespeech\update-signing.key` (hors dépôt, à sauvegarder) et affiche la clé publique.
2. Dans `sources\freespeech.defaults.props` : `FsUpdateUrl` (adresse du site) et `FsUpdateKey` (clé publique).
3. Après chaque `bump-version`, `scripts\make-update-manifest.ps1`, puis publier le site (`wwwroot/update/`).

## État

Compile (desktop ; mobile en cible Windows). 5 tests ajoutés passent. Le test `MigrationTests.ACreatorHandsTheGroupOverThenMigrates` échoue de son côté,
sans lien constaté avec cette fonctionnalité.
