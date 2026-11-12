# Message exchanges: every case

This document follows a message in each situation, first with a contact (one to one) and then in a group. It completes [HOW_IT_WORKS.en.md](HOW_IT_WORKS.en.md), which describes the role of each component. The exact formats are in [PROTOCOL.en.md](PROTOCOL.en.md) and [PROTOCOL_API.en.md](PROTOCOL_API.en.md). In the diagrams, `Rx` stands for a relay and `A`, `B`, `C` for devices.

Three rules hold in every case. The relay keeps no message: a message that is not delivered stays in the sender's send queue ("Sends in progress" screen), on their device. The relay counts a message as delivered as soon as it has written it on the recipient's connection, even if that connection died without the relay knowing. The recipient therefore confirms each delivery with an encrypted acknowledgement, and without it the sender sends the same message again, with the same identifier, after 30 s, 2 min, 10 min, 1 h and 6 h; the recipient handles it once. A message that the relay could not deliver because the recipient is absent is not concerned: it waits in the queue and goes when the recipient shows up (push wake-up or connection). As a back-up, the application tries again at each 30-second cycle during the first 10 minutes, then every 15 minutes up to 24 hours, then every 6 hours up to 3 days. And everything is signed and end-to-end encrypted: the relay reads the envelope (sender, recipient, date) and never the content.

## Part 1. With a contact

### 1.1 Becoming contacts

```
 Alice                                              Bob
   │ card: public keys, nickname, servers, one-time code
   │ ─────── QR code (in person) or message (remotely) ───────────►│
   │                                                               │ imports the card
   │◄───── Bob's card, encrypted with the code, through a relay ───│
   │ confirms (automatic with a QR, by hand remotely)              │
   │ ─────────────────── confirmation ────────────────────────────►│
   ▼ confirmed contacts: each knows the other's servers            ▼
```

The code of a QR is valid for 10 minutes and the import is automatic. The code of a remote share (copy-paste, mail) is valid for one month and needs a manual validation. The exchange goes through the relays, so the contact is confirmed only when both are online; a request that waits is sent again at each cycle of the application. To make sure nobody slipped their own keys into the exchange, both compare a safety number outside the application ("Check security" screen).

### 1.2 Both online, on the same server

```
 A ── envelope ─► R1 ── writes on B's connection ─► B
 A ◄── ack (encrypted) ── R1 ◄──────────────────────── B
```
A sees ✓ (sent) when the relay has the envelope, then B's confirmation. B keeps the message, decrypts it and displays it.

### 1.3 On different servers

```
 A ─ connected to R1        R2 ─ B (B announces R2 in his card)
 A ─── envelope, direct connection to R2 ───►│── B
 A ◄─────── ack, through a server of A ◄─────│◄─ B
```
A opens a connection to B's server herself; R1 and R2 do not need to know each other. B answers the same way on a server of A. If B announces several servers, A tries them in priority order and goes on to the next at once when a server does not answer or B is not there.

### 1.4 B offline, application closed, with push

```
 A ─► R2: B is absent. R2 notes "A is waiting for B" and calls the push gateway
 R2 ─► gateway ─► Google/Apple ─► B's phone (invisible wake-up, no content)
 B: wakes up, connects to his servers, declares himself ready
 R2 ─► A: "B is ready"   →   A sends her queue at once
 A ─► R2 ─► B: decrypts, local notification (name and preview produced on the device)
```
While B was not there, the message did not leave A's device.

### 1.5 B offline, without push

Same sequence without the wake-up. The message waits in A's queue. B connects at the next poll of the system (about 15 minutes on Android) or when the application is opened, A is told and sends. The desktop client stays connected as long as it runs.

### 1.6 A has no reachable server

The message stays in A's queue and nothing is put on a server. The application reconnects every 30 seconds and the queue goes as soon as a server answers. Waiting for a message can be stopped ("Stop waiting") and resumed later.

### 1.6 bis The shared restricted server does not answer

```
 A and B share the restricted server P (and each also has open servers)

 P answers, B is connected there   : A ─► P ─► B                      (the only path used)
 P answers, B is not connected     : the message waits at A           (B is simply absent)
 P does not answer                 : the message waits at A, "waiting for the private server"
   if A and B accepted the fallback (route_policy):
      Ask : A's application asks at once, once; on "yes" the next messages go through an open
            server of B, with no new question, until P is back
      Auto: after 2 minutes without an answer from P, A ─► B's open server ─► B  (notice "sent through an open server")
   B answers the same way, according to their own setting (in Ask mode they are asked once on their side)
 P is back                         : every send starts with P again; as soon as it delivers a message, the fallback is over
```
Without the agreement of both, or without a shared restricted server, there is no fallback: the message waits or follows the ordinary path.

### 1.7 Drops and lost confirmations

```
Case a. drop while sending            : A gets no ack → send again (30 s, 2 min, 10 min, 1 h, 6 h)
Case b. drop while receiving          : B never gets a piece of a message (whole messages only);
                                         A sends again as in case a
Case c. message received, ack lost    : A sends again; B recognises the message, does not show it twice
                                         and sends the ack again
Case d. A switched off right after    : the queue is kept in A's database; it goes when A is back
```
After the last delay (6 h), a message that was not confirmed is not sent again.

### 1.8 Read, "typing…", reactions

When B opens the conversation, his application sends a read receipt that covers all the previous messages, and A's ✓ becomes a golden ✓✓. The receipt goes with no queue and B can turn it off. The "typing…" indicator is a short-lived signal that is neither kept nor sent again, and it can be turned off too. A reaction is a message like the others, with the send queue and the confirmation.

### 1.9 Editing, deleting

| Action | What is sent | If B is offline |
|---|---|---|
| Edit | an edit, the most recent wins | send queue; a message not yet delivered is simply edited in the queue |
| Delete for me | nothing (local) | not applicable |
| Delete for everyone | a deletion; the line stays as "message deleted" | send queue; a message not yet delivered is removed from the queue, nothing is sent |

Only the author can edit or delete their message. The order of arrival is not guaranteed: an edit that arrives before the message waits, and a deletion wins over any edit.

### 1.10 A file

```
 A ─► B: announcement (name, size, thumbnail) like a message
 B decides: download?   no → nothing   yes → ─► A: request
 A ─► B: pieces of 48 KiB, each encrypted and proved by the Merkle tree
 B: checks every piece, puts them together
```
The file only goes if both are online. If A is absent, the request waits and the file arrives when A is back. On B's side one sets the automatic download by size, Wi-Fi only or mobile data, and the confirmation before sending a big file. A file that A withdrew is not served any more and B sees "File withdrawn by the sender". A server that limits the volume per day suspends the sending until the delay shown has passed ("Server limit" screen).

### 1.11 B changes server

B sends his contacts an update of his profile with his new list of servers; it is queued for a contact who is offline. In between, a message sent to the server B has left is found again if the two servers are linked. Otherwise A sends it again when she knows the new address.

### 1.12 B has several devices

The devices of one identity are connected together. A's message is delivered to each of those that are online. A message sent from one of B's devices is copied to his other devices. A restored device asks another one for the history, with a check code.

## Part 2. In a group

A group exists on no server. It is a signed state (members, moderators, key of the epoch) that each member keeps on their device. Every change of members or roles creates a new epoch, with a new key.

### 2.1 Creation and invitation

```
 Creator C                                         Invitee D
   │ creates the state (epoch 1), draws the key
   │ ── signed state + key encrypted for D (one to one, queued) ─►│
   │                                                              │ sees the invitation, nothing is joined
   │                                         accept ─► joins, asks the members for the catch-up
   │                                         refuse ─► the invitation is deleted, C knows nothing of it
```
A moderator invites one of their contacts. A member who is not a moderator can suggest one of their contacts to the moderators, who invite them with the proof of that sponsorship.

### 2.2 One message, all members online

```
 A ── one envelope per member (same content, encrypted with the group key) ──►
   ├─► Rb ─► B          each envelope follows the servers of the member aimed at
   ├─► Rc ─► C          (as one to one: different servers are possible)
   └─► Rd ─► D
```
The content is encrypted with the key of the epoch and signed by the author. The relay sees neither the name nor the identifier of the group, only an opaque label that designates the key. A member who is not a contact of another can write to them, since the group's state holds the public keys and the servers of each member. There is no delivery or read receipt in a group, only the unread counter.

### 2.3 A member is offline: the catch-up

```
 A writes messages 3 to 6 then disconnects.            B has 4-6, C has 3-6.
 D connects: "I have up to 2 from A"  ─►  B and C (up to 3 connected peers)
 B answers 4-6; C answers 3-6; D ignores the duplicates and ends up complete.
```
There is no queue per recipient. Each member keeps a log of the group's messages and serves it to the others, even if the author is switched off. When a member sees that another has more messages than they do, they ask for them in turn, so the members converge without depending on the author. The author's signature, inside the encrypted content, stops a peer from making up a message; it can only replay it.

### 2.4 Arrival, departure, exclusion

```
 moderator ─► new signed state (epoch n+1) + new key ─► each remaining member
 excluded member ─► receives the state WITHOUT the key: knows they are out and will not read what follows
```
An excluded member keeps what they already received, but cannot read the following messages, even intercepted ones. A message from a removed member is refused in direct delivery. It is accepted only if a current member relays it and if it dates from an epoch where the author was a member. If two moderators act at the same time, the state with the smallest fingerprint wins; the other is kept, its key stays readable, and its moderator sends their change again on top. Renaming the group or changing its picture does not change the key.

### 2.5 Editing, deleting, pinning, banning

Editing and deleting are the same operations as one to one, sent as group messages, and only the author can do them. A moderator can pin a message (three at most) and ban a person. A ban is a blacklist that the members apply by themselves, which differs from an exclusion, which changes the key.

### 2.6 A file in a group

```
 A ─► group: announcement of the file (content fingerprint, size, name)
 D wants the file: one-to-one request to any member who has it whole
 B (who already downloaded it) and A serve pieces to D in parallel
```
D checks every piece with the Merkle tree and drops a peer that sends a wrong piece. The author does not need to be online as long as another member holds the file, and each holder checks that the requester is a member of the current epoch.

### 2.7 Leaving, coming back

Leaving changes the state of the group like an exclusion, with a new key. The group stays readable in the archive, read-only. A moderator can invite the person again, who accepts or refuses; if they accept, the group becomes active again with its history.
