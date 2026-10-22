<p><img src="sources/store/screenshots/en/01-chats.png" alt="FreeSpeech: Chats" width="240"> <img src="sources/store/screenshots/en/02-menu.png" alt="FreeSpeech: Menu" width="240"> <img src="sources/store/screenshots/en/03-chat.png" alt="FreeSpeech: Chat" width="240"></p>

# FreeSpeech

**End-to-end encrypted messaging with no account, on servers that keep nothing.**

![Licence AGPL-3.0-or-later](https://img.shields.io/badge/licence-AGPL--3.0--or--later-C89A4B) ![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![Android 7 and later](https://img.shields.io/badge/Android-7%2B-3DDC84)

[Website](https://freespeech.rebisway.com/en/) · [Google Play](https://play.google.com/store/apps/details?id=org.freespeech.mobile) · [Protocol](sources/PROTOCOL.en.md) · [Architecture](sources/ARCHITECTURE.en.md) · [Français](README.fr.md)

---

FreeSpeech is a messenger you can use without giving anyone a phone number or an e-mail address. Your identity is computed on your phone from three phrases, and everything you send is signed and encrypted there before it leaves the device.

The servers do one thing: they pass messages between devices that are connected. They store nothing. When the recipient is offline, the message waits on the sender's phone and goes out as soon as the recipient is back.

You can be on several servers at once, public or private, and anyone can run one. The protocol is specified in this repository and does not depend on any particular framework, so a client or a server can be written in any language.

## Features

FreeSpeech does what you expect from a messenger: text, replies, emoji reactions, editing and deleting a message (for yourself or for everyone), a typing indicator, read receipts that can be switched off, groups, photos, videos and files, and a mute switch per chat. A message whose first line is `md` is shown formatted (titles, lists, bold, italic, links).

A group has a single shared key. It changes every time someone joins, leaves or is removed, which is what stops an excluded person from reading what comes next. A member who was offline asks the others for what they missed, and the author of those messages does not have to be around. An invitation is never automatic: the person invited has to accept it, and chooses there the name and picture they will show in that group, different from the ones they use elsewhere: two groups cannot be linked by name or picture. A check marks, in a group, the members who are your contacts (gold once you have compared their safety number).

Files go up to 2 GiB; the app asks you to confirm before sending a large one (the size is yours to set), a received file shows its size before you fetch it, and a relay can cap the volume each person sends per day. Files are cut into 48 KiB chunks, each checked against a Merkle proof, and an interrupted transfer picks up where it stopped. In a group, anyone who already has the file can send part of it, so the sender's phone is not the only source. Photos are resized and videos recompressed before they leave.

Two people can compare a safety number to check that nobody tampered with their key exchange. The local database is encrypted and a PIN is optional. The three phrases can be printed on a sheet with a QR code, or kept in a file protected by a password.

The Android app adds contacts with a QR code or a one-time card, runs the same identity on several devices, accepts content shared from other apps, and is translated into French, English and Spanish. Notifications only wake the app up. They never contain the text of a message.

## Design

Your three phrases go through Argon2id and HKDF and give two key pairs, one for encryption (X25519) and one for signing (Ed25519). The fingerprint of the public keys is your identifier. Nothing is registered anywhere to get it.

To become contacts, two people swap a card that holds their public keys, a nickname and the servers they use, either as a QR code or with a one-time code.

The server only comes in when you send something. The app signs the message, encrypts it with a fresh ephemeral key and hands it to a server over a WebSocket. The server can read the envelope, meaning who it is from and who it is for, and nothing else. If the recipient is connected, the server delivers the message, going through a neighbouring server when that is where the recipient is connected. If not, the message stays in the sender's outgoing queue. A notification wakes the recipient's phone, the app reconnects and the queue empties. The recipient checks the signature, then decrypts.

```
   Alice                                                  Bob
  (phone)                                                (phone)
      │ message signed, then encrypted                     ▲
      ▼                                                    │
 ┌──────────┐    link accepted by both        ┌──────────┐ │
 │ Relay A  │◄──────── administrators ───────►│ Relay B  │─┘
 └──────────┘                                 └──────────┘
   keeps nothing                                keeps nothing
```

A new app that knows no relay yet asks a directory once, on its very first connection, which public relays accept new accounts. The directory answers with a signed list and keeps nothing about clients. After that it is no longer involved. Two public servers become neighbours when both of their administrators accept the link. The diagrams are in [sources/diagrams/](sources/diagrams/); the files ending in `-en` are in English.
## Cryptography

| Use | Primitive |
|---|---|
| Identity, derived from the 3 phrases | Argon2id (32 MiB, t = 3), then HKDF-SHA256 |
| Key agreement for each message | Ephemeral X25519, HKDF-SHA256 |
| Encryption | ChaCha20-Poly1305 |
| Signature | Ed25519, bound to the sender, the recipient and the message type |
| Groups | One shared key per epoch, renewed on every membership change |
| Files | 48 KiB chunks, SHA-256 Merkle tree |
| Local database | SQLite, content encrypted field by field |
| Linking two servers | One ECDSA P-256 key per server, signed calls, a ±2 minute window, replay protection |

The primitives come from BouncyCastle, which is managed code and runs on Android and iOS. Fixed test vectors check them.

## Limits

- A server sees who writes to whom, when, and when each device connects. It never sees the content.
- There is no ratchet. If someone captured past traffic and later steals a recipient's long-term key, that traffic can be decrypted.
- Losing the three phrases loses the identity. There is no recovery.
- Deleting a message for everyone and editing a message are best effort. A modified client can ignore them, and the recipient may have copied the message already.
- A group member can pass the current key to someone who was excluded, outside the protocol.
- Received files are not encrypted by the app. On Android they sit in the app's private storage, which the system encrypts; on the desktop they are plain files in the app's data folder.
- The PIN protects the screen, not the encryption.

The security model is described in full in [ARCHITECTURE.en.md](sources/ARCHITECTURE.en.md), section "Security model and known limits".

## Repository layout

| Folder | Content |
|---|---|
| `sources/freespeech.relay` | The relay, in ASP.NET Core 10 |
| `sources/freespeech.client` | The client library (C#, .NET 10, no UI): identity, contacts, servers, messages, files, groups, storage |
| `sources/freespeech.mobile` | The .NET MAUI app for Android, with a Windows build for working on the interface |
| `sources/freespeech.desktop` | The Avalonia desktop client (solution `sources/FreeSpeech.Desktop.slnx`) |
| `sources/freespeech.console` | A console client for testing, on top of the library (Windows) |
| `sources/freespeech.push` | The notification gateway |
| `sources/freespeech.directory` | The relay directory, used at the very first connection |
| `sources/freespeech.ops` | Code shared by the relay and the gateway: administration command line, linking of servers, logs |
| `sources/freespeech.tests` | xunit tests |
| `sources/deploy/` | Service files, nginx files and configuration examples |
| `sources/diagrams/` | Diagrams, in French and English |
| `scripts/` | End-to-end scenarios with real clients and real servers |

## Getting started

**Use the app.** Join the open test on [Google Play](https://play.google.com/store/apps/details?id=org.freespeech.mobile). You can also download the APK from [freespeech.rebisway.com/download](https://freespeech.rebisway.com/download/) and compare the SHA-256 fingerprint shown on that page with the file you got. In Settings > Servers, `https://fs.rebisway.com` is the reference server, and you can add your own.

**Run a relay.** [INSTALL_RELAY.en.md](sources/INSTALL_RELAY.en.md) goes step by step through Ubuntu 24, nginx, systemd and the certificate. The administration commands are in [cli-relay.en.md](sources/cli-relay.en.md).

**Build and test.** You need the .NET 10 SDK.

```bash
dotnet build sources/FreeSpeech.slnx
dotnet test sources/freespeech.tests
```

To try it locally, start a server and two clients, each with its own profile:

```bash
dotnet run --project sources/freespeech.relay --no-launch-profile --urls http://localhost:5299
dotnet run --project sources/freespeech.console -- alice http://localhost:5299
dotnet run --project sources/freespeech.console -- bob http://localhost:5299
```

The first time a profile starts, the console asks for three recovery phrases of at least 75 characters each, all different. `help` lists the commands. On Windows PowerShell, `scripts\dev-run.ps1` opens the server and several clients at once, and `scripts\test-groups.ps1`, `scripts\test-attachments.ps1` and `scripts\test-multiserver.ps1` replay complete scenarios.

The mobile app has its own solution. It needs the .NET Android workload, and the repository must sit in a path without accented characters.

```bash
dotnet build sources/FreeSpeech.Mobile.slnx -f net10.0-android36.0
dotnet build sources/FreeSpeech.Mobile.slnx -f net10.0-windows10.0.19041.0   # run it on the PC
```

**Default addresses.** The mobile app and the desktop client start with example servers (`relay.exemple.org`, `push.exemple.org`). To build with your own, copy `sources/freespeech.defaults.props.example` to `sources/freespeech.defaults.props` and set `FsDefaultServer` there. The address is stored in the assembly at build time, and none is written in the source. The push gateway is not compiled into the app: each relay uses the gateways its administrator linked it with.

## Documentation

| | Français | English |
|---|---|---|
| Protocol | [PROTOCOL.fr.md](sources/PROTOCOL.fr.md) | [PROTOCOL.en.md](sources/PROTOCOL.en.md) |
| Protocol API reference | [PROTOCOL_API.fr.md](sources/PROTOCOL_API.fr.md) | [PROTOCOL_API.en.md](sources/PROTOCOL_API.en.md) |
| Architecture, encryption, storage | [ARCHITECTURE.fr.md](sources/ARCHITECTURE.fr.md) | [ARCHITECTURE.en.md](sources/ARCHITECTURE.en.md) |
| Installing a relay | [INSTALL_RELAY.fr.md](sources/INSTALL_RELAY.fr.md) | [INSTALL_RELAY.en.md](sources/INSTALL_RELAY.en.md) |
| Administering your servers | [ADMINISTRATION.fr.md](sources/ADMINISTRATION.fr.md) | [ADMINISTRATION.en.md](sources/ADMINISTRATION.en.md) |
| Relay command line | [cli-relay.fr.md](sources/cli-relay.fr.md) | [cli-relay.en.md](sources/cli-relay.en.md) |
| Push gateway command line | [cli-push.fr.md](sources/cli-push.fr.md) | [cli-push.en.md](sources/cli-push.en.md) |
| Installing the push gateway | [INSTALL_PUSH.fr.md](sources/INSTALL_PUSH.fr.md) | [INSTALL_PUSH.en.md](sources/INSTALL_PUSH.en.md) |
| Installing a relay directory | [INSTALL_DIRECTORY.fr.md](sources/INSTALL_DIRECTORY.fr.md) | [INSTALL_DIRECTORY.en.md](sources/INSTALL_DIRECTORY.en.md) |
| Several servers on one machine | [INSTALL_HOST.fr.md](sources/INSTALL_HOST.fr.md) | [INSTALL_HOST.en.md](sources/INSTALL_HOST.en.md) |
| Diagrams | [sources/diagrams/](sources/diagrams/) (`-fr`) | [sources/diagrams/](sources/diagrams/) (`-en`) |
| Class and database diagrams | [uml/](sources/diagrams/uml/), [db/](sources/diagrams/db/) | [uml/](sources/diagrams/uml/), [db/](sources/diagrams/db/) |

## Contributing

Bug reports, reviews of the cryptography and of the protocol, tests and fixes are all welcome. Please read the [protocol](sources/PROTOCOL.en.md) and the [architecture](sources/ARCHITECTURE.en.md) before proposing a change. Code comments are in English, every source file carries the licence header, and each document comes in French and in English (a `.fr.md` file and a `.en.md` file).

To report a vulnerability, write to <contact@rebisway.com>.

## Licence

FreeSpeech is free software under the **GNU AGPL v3.0 or later** (`AGPL-3.0-or-later`); see [LICENSE](LICENSE). © 2024-2026 Thomas Gourgnier.

The server is a network service, so the AGPL requires anyone who runs a modified version to offer its source code to the users of that service. Contributions will be accepted under a contributor agreement (CLA) that allows a commercial licence to be offered alongside.
