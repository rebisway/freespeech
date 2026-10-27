# Features of the applications

This document lists what the mobile (Android) and desktop (Windows, Linux; the code is also meant for macOS) applications do. Both share the same library (`freespeech.client`): messages, groups, files and security are therefore the same; only the interface and what the device allows change. How the servers work is in [HOW_IT_WORKS.en.md](HOW_IT_WORKS.en.md), a message followed through every case in [MESSAGE_FLOWS.en.md](MESSAGE_FLOWS.en.md), the formats in [PROTOCOL.en.md](PROTOCOL.en.md).

## Platforms

| | Mobile | Desktop |
|---|---|---|
| System | Android 7 and later (APK, Google Play) | Windows (MSI installer, per user, no administrator rights) · Linux (AppImage) |
| iOS, macOS | iOS: not published · macOS: same code, no package | |
| Launch | one instance | one profile per name: `FreeSpeech.exe --profile name --server url` |
| Display | one screen at a time | two columns (list, conversation); a single column under 700 px |
| Languages | German, English, Spanish, French, Italian, Dutch, Polish, Portuguese, Russian, Turkish, plus any imported language | the same |

## Identity and start

| Feature | Mobile | Desktop |
|---|---|---|
| First connection: a first name, three phrases drawn from a public-domain book in the system language | yes | yes |
| No account, no phone number, no e-mail address | yes | yes |
| "Save your three phrases" screen (show, copy, print a sheet, `.fskey` key file protected by a password) | yes | yes |
| Restore an identity: three phrases typed, pasted, read from the printed sheet or from the key file; with or without a backup file | yes (sheet read with the camera) | yes (key file, picture of the sheet) |
| Check that a key file opens the identity | yes | yes |
| Choose one's server from the start (advanced mode) | yes | yes |
| Server found by the built-in directories, else a fallback relay | yes | yes |

## Messages

| Feature | Mobile | Desktop |
|---|---|---|
| Text, quoted reply, forward to another conversation | yes | yes |
| Formatting (headings, lists, bold, italic, strikethrough, code, quote, links) when the first line is `md` | yes | yes |
| Emoji reactions | yes | yes |
| Edit a message | yes | yes |
| Delete for me / for everyone (the content is erased for all) | yes | yes |
| Copy, share the text | yes | yes (copy) |
| Search in a conversation | yes | yes |
| "New messages" separator, loading of the previous messages when scrolling up | yes | yes |
| Pin a message (moderators, three at most) | yes | yes |
| Mute a conversation | yes | yes |
| Read receipt (✓ sent, golden ✓✓ read), can be turned off | yes | yes |
| "typing…" indicator, can be turned off | yes | yes |
| Links open after a confirmation showing the real address | yes | yes |
| "Sends in progress" screen: waiting messages, "Stop waiting", resume, number of resends | yes | yes |
| Voice message (recording and playback) | yes | no |

## Files and media

| Feature | Mobile | Desktop |
|---|---|---|
| Send a file up to 2 GiB, with a confirmation above an adjustable size | yes | yes |
| A received file shows its size; download on request or automatic up to an adjustable size | yes | yes |
| Download on Wi-Fi only, or mobile data, or small files only | yes | no |
| Thumbnail of images and videos | yes | yes (images) |
| Image viewer | yes | no |
| Video playback | yes | no |
| Album (several images as a mosaic) | yes | no |
| Take a photo, film, with compression of a video before sending | yes | no |
| Receive content shared from another application | yes | no |
| Withdraw a sent file (for everyone) | yes | yes |
| A file resumes after a drop, several holders in a group | yes | yes |
| Archive files by date, type or size to free space, then load them back | yes | no |

## Contacts

| Feature | Mobile | Desktop |
|---|---|---|
| Add by a QR code shown or scanned | yes (show and scan) | yes (show) |
| Add by a single-use card: copy, paste, send by SMS, mail or messaging | yes | yes (copy, paste, mail) |
| Pending requests: accept or refuse | yes | yes |
| Rename a contact (a name only you see) | yes | yes |
| Check the safety number (golden ✔ in the lists) | yes | yes |
| Delete a contact (the conversation stays readable in the archive) | yes | yes |
| Profile picture, name, description: edit and send to contacts and groups | yes | yes |

## Groups

| Feature | Mobile | Desktop |
|---|---|---|
| Create a group by choosing contacts, name it, describe it, set a picture | yes | yes |
| Invitation to accept or refuse, send again or cancel an invitation | yes | yes |
| Roles: creator, moderators; appoint, remove, hand over the creator role | yes | yes |
| Exclude, exclude and ban, unban (the creator) | yes | yes |
| Suggest one of your contacts to the moderators | yes | yes |
| My name and picture in this group | yes | yes |
| Leave; group archived read-only; invited again | yes | yes |
| Catch-up of missed messages from the other members | yes | yes |

## Servers

| Feature | Mobile | Desktop |
|---|---|---|
| Several servers at the same time, public and private; connected / offline state of each | yes | yes |
| Add or remove a server | yes | yes |
| Private server: use an invitation, or ask for access (with a check code to give to the administrator) | yes | yes |
| A full server can advise a move (three times a day at most) | yes | yes |
| "Server limit" banner when the daily volume is reached | yes | yes |
| Shared restricted server with a contact: the only path used by default; per-contact setting "If the private server does not answer" (wait, ask me, use an open server); "Send through an open server" in "Sends in progress" | yes | yes |

## Notifications and wake-up

| Feature | Mobile | Desktop |
|---|---|---|
| System notification with sender and preview produced on the device | yes | no |
| Wake-up of the closed application by a push with no content (can be turned off) | yes | no (stays connected while it runs) |
| Periodic polling (about 15 min) when push is absent or refused | yes | no |
| Check of the notifications' reliability (the manufacturer's battery saver) | yes | no |
| Preview hidden on the lock screen | yes | no |
| Application popup, bottom right corner, with a reply field | no | yes |
| Notification-area icon: last 12 conversations, Open, Settings, Quit; closing the window hides it | no | yes |

## Security and privacy

| Feature | Mobile | Desktop |
|---|---|---|
| Encrypted local database | yes (key in the phone's secure storage) | yes (key protected by DPAPI on Windows; otherwise the three phrases at every start) |
| Lock by a code of 4 to 8 digits, adjustable delay (immediately to one week) | yes | yes |
| Erase the data after a number of wrong codes (5 or 10) | yes | yes |
| Forgotten code: back in through the three phrases | yes | yes |
| Protected screen (no screenshot, no preview in the recent applications) | yes | no |
| Discreet keyboard | yes | no |
| Remote erase of a stolen phone (turned on with the three phrases) | yes | yes |
| Change identity after a theft (notice to the contacts, hand-over of the groups) | yes | yes |

## Backup and several devices

| Feature | Mobile | Desktop |
|---|---|---|
| Encrypted backup (no attachments, last 6 months, or all), saved where you want | yes | yes |
| Restore: contacts, groups, history, servers, profile | yes | yes |
| The same identity on several devices at the same time | yes | yes |
| Copy the history of another device of the identity (six-digit check code) | yes | yes |

## Settings and appearance

| Feature | Mobile | Desktop |
|---|---|---|
| Dark theme, light theme, or the system's | yes (at restart) | yes (at once) |
| Choice of the language, import of a language file (JSON), template to translate, removal | yes | yes |
| Diagnostic log to share | yes | yes |
| Version and date shown | yes | yes |
