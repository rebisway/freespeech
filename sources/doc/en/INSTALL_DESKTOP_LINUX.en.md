# Linux desktop client: building and publishing the packages

Everything is built from Windows (no Linux tool needed), except the AppImage.

```powershell
scripts\build-desktop-linux.ps1          # version of sources\version.props (FsDesktopVersion)
```

Output in `sources\deploy\release\desktop-linux\`:

| File | Use |
|---|---|
| `freespeech-desktop-<version>-linux-x64.tar.gz` | the program (self-contained, no .NET needed), icon and shortcut |
| `freespeech-desktop_<version>_amd64.deb` | Ubuntu/Debian package: `/opt/freespeech`, `freespeech` command, menu shortcut |
| `install.sh` | per-user install, no root, from the `.tar.gz` |
| `SHA256SUMS` | checksums (verified by `install.sh`) |
| `build-appimage.sh` | builds the AppImage (run on Linux) |

## Hosting

Put these files in one folder of a static web server. In `install.sh`, replace the default value of `BASE` with that folder's address.

## Installing (user)

- Ubuntu/Debian: `sudo apt install ./freespeech-desktop_<version>_amd64.deb` (uninstall: `sudo apt remove freespeech`).
- Any distribution: `curl -fsSL https://your-site/freespeech/install.sh | sh` (uninstall: `sh install.sh --uninstall`). The program goes to `~/.local/share/freespeech`, the shortcut to the applications menu.
- AppImage: `chmod +x FreeSpeech-<version>-x86_64.AppImage`, then double-click.

Messages and keys (the user's configuration folder) are never touched by an update or an uninstall.

## AppImage

On Linux: `sh build-appimage.sh freespeech-desktop-<version>-linux-x64.tar.gz` (downloads `appimagetool`; appends the AppImage to `SHA256SUMS`).
