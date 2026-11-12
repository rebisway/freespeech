# Client de bureau Linux : construire et publier les paquets

Tout se construit depuis Windows (aucun outil Linux requis), sauf l'AppImage.

```powershell
scripts\build-desktop-linux.ps1          # version de sources\version.props (FsDesktopVersion)
```

Résultat dans `sources\deploy\release\desktop-linux\` :

| Fichier | Usage |
|---|---|
| `freespeech-desktop-<version>-linux-x64.tar.gz` | le programme (autonome, sans .NET), icône et raccourci |
| `freespeech-desktop_<version>_amd64.deb` | paquet Ubuntu/Debian : `/opt/freespeech`, commande `freespeech`, raccourci dans le menu |
| `install.sh` | installation par utilisateur, sans droits root, à partir du `.tar.gz` |
| `SHA256SUMS` | sommes de contrôle (vérifiées par `install.sh`) |
| `build-appimage.sh` | construit l'AppImage (à lancer sous Linux) |

## Héberger

Déposez ces fichiers dans un même dossier d'un serveur web statique. Dans `install.sh`, remplacez la valeur par défaut de `BASE` par l'adresse de ce dossier.

## Installer (utilisateur)

- Ubuntu/Debian : `sudo apt install ./freespeech-desktop_<version>_amd64.deb` (désinstallation : `sudo apt remove freespeech`).
- Toute distribution : `curl -fsSL https://votre-site/freespeech/install.sh | sh` (désinstallation : `sh install.sh --uninstall`). Le programme va dans `~/.local/share/freespeech`, le raccourci dans le menu des applications.
- AppImage : `chmod +x FreeSpeech-<version>-x86_64.AppImage`, puis double-clic.

Les messages et les clés (dossier de configuration de l'utilisateur) ne sont jamais touchés par une mise à jour ni une désinstallation.

## AppImage

Sous Linux : `sh build-appimage.sh freespeech-desktop-<version>-linux-x64.tar.gz` (télécharge `appimagetool` ; ajoute l'AppImage à `SHA256SUMS`).
