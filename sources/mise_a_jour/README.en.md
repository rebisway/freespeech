# Client update check (set aside)

Complete feature, built and tested, taken out of the working tree. It is neither published nor part of the protocol.

## How it works

The APK, Windows and Linux builds can look for a newer version; the Google Play bundle has no check. The check talks only to the publisher's website, whose
address (`FsUpdateUrl`) and Ed25519 public key (`FsUpdateKey`) are chosen at build time (`sources/Directory.Build.props`, overridden by
`sources/freespeech.defaults.props`). Without `FsUpdateUrl` there is no check and no button (every custom build).

- The website serves `update/latest.json` and its detached signature `update/latest.json.sig` (Ed25519, base64, over the exact bytes of the file).
- The manifest holds `format` (1), `published`, `programs` (`android-apk`: `version`, `build`, `date`; `windows` and `linux`: `version`, `date`) and
  `download` (an `https` address with `{lang}`). It is refused when the signature fails, when it is more than 400 days old, when `download` is not `https` or
  when the program is missing.
- A version is available when it is higher, or equal with a higher build number (Android). Nothing is downloaded: the client opens the download page.
- The check runs on request in Settings, or at start-up when the user turned that on (off by default); a version found at start-up is announced once.

## Contents

- `modifications.patch`: `svn diff` of the existing files that were modified (csproj, props, build scripts, ARCHITECTURE fr/en, mobile and desktop Settings, 10
  language files, `ClientService.cs`, `DesktopSession.cs`).
- `nouveaux/`: the created files, with their path relative to the repository root.
  - `sources/freespeech.client/Update/UpdateChecker.cs` (library: signature check, version comparison)
  - `sources/freespeech.tests/UpdateCheckerTests.cs` (5 tests)
  - `sources/freespeech.mobile/Services/UpdateService.cs`, `sources/freespeech.desktop/Services/UpdateService.cs`
  - `sources/tools/updatesign/` (`Program.cs`, `updatesign.csproj`: creates the key, writes and signs the manifest)
  - `scripts/make-update-manifest.ps1`

## Putting it back

From the repository root:

```
patch -p0 < sources/mise_a_jour/modifications.patch
```

(or `svn patch sources/mise_a_jour/modifications.patch`), then copy the contents of `nouveaux/` to the root and `svn add` every copied file and folder
(`sources/freespeech.client/Update`, `sources/tools/updatesign`, etc.). The patch was made against revision 354; if the files have moved on, apply the hunks by hand
using the files in `nouveaux/`.

## Going live

1. `scripts\make-update-manifest.ps1 -NewKey`: creates `%USERPROFILE%\.freespeech\update-signing.key` (outside the repository, back it up) and prints the public key.
2. In `sources\freespeech.defaults.props`: `FsUpdateUrl` (website address) and `FsUpdateKey` (public key).
3. After each `bump-version`, run `scripts\make-update-manifest.ps1`, then publish the website (`wwwroot/update/`).

## Status

Builds (desktop; mobile for the Windows target). The 5 added tests pass. `MigrationTests.ACreatorHandsTheGroupOverThenMigrates` fails on its own, with no
link found to this feature.
