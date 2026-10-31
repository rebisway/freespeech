# How it works: servers, clients, waking a phone

This document describes how the programs of FreeSpeech cooperate. The detail of the frames is in [PROTOCOL.en.md](PROTOCOL.en.md) and [PROTOCOL_API.en.md](PROTOCOL_API.en.md); references such as "§ 8" are sections of the protocol. The features of the applications are listed in [FEATURES.en.md](FEATURES.en.md), the path of a message in each situation in [MESSAGE_FLOWS.en.md](MESSAGE_FLOWS.en.md), and the organisation of the code in [ARCHITECTURE.en.md](ARCHITECTURE.en.md).

## 1. The four components

| Component | Role | What it keeps |
|---|---|---|
| Application (mobile, desktop, console) | Computes the identity, encrypts, signs, keeps the history | Keys, contacts, messages and files, in an encrypted database on the device |
| Relay (`freespeech.relay`) | Receives an encrypted envelope and passes it to the recipient's connection | In memory only: the connected devices and a few waiting markers. No mailbox |
| Push gateway (`freespeech.push`) | Sends a wake-up signal with no content to a phone whose application is closed | The wake-up tokens it receives and a counter per relay. The Google (FCM) and Apple (APNs) credentials are only there |
| Directory (`freespeech.directory`) | Tells an application that knows no relay which ones take new accounts | The linked relays, with their country and free places. Nothing about users, no address in the logs |

The components are independent. A relay works without a gateway or a directory; the phones then check periodically whether they have messages. An application needs no server as long as it sends and receives nothing.

## 2. One message, from start to end

```
 Alice (phone)               Bob's relay                Bob (phone)
        │  1. encrypted envelope   │                          │
        │ ───────────────────────► │  2. is Bob connected?    │
        │                          │ ── yes: passes it on ──► │  3. decrypts, keeps
        │                          │ ◄── ack (encrypted) ──── │
        │ ◄── ack passed on ────── │                          │
```

1. Alice's application signs the message, encrypts it for Bob with a fresh ephemeral key and makes an envelope of it that holds the sender, the recipient, the timestamp and the encrypted content (§ 5). The relay reads the envelope to route it and does not read the content.
2. Alice drops the envelope on one of Bob's servers, as his contact card announces them. She tries them in priority order. If a server does not answer, or if Bob is not connected there, she goes on to the next (§ 8).
3. If Bob is connected somewhere, the relay passes the envelope to his connection. Bob decrypts it, checks the signature, keeps the message and returns an encrypted acknowledgement. The relay counts the message as delivered as soon as it has written it on Bob's connection, even if that connection died without the relay knowing. Without an acknowledgement, Alice therefore keeps the message in her send queue and sends it again after 30 s, 2 min, 10 min, 1 h and 6 h. A message already received is not handled twice, but it is acknowledged again (§ 5, "Confirmed delivery").
4. If Bob is connected on none of his servers, the message stays with Alice, in her send queue ("Sends in progress" screen). Alice puts a waiting marker on every server of Bob that she can reach. When Bob connects, the relay tells Alice, whose application sends its queue at once instead of waiting for the next try.

A message is therefore delivered only if both devices are connected at the same time. The relays have nothing to back up or purge, and hold no message that an authority could ask them for.

## 3. Contacts do not need to be on the same server

An application registers at once on every server of its list that it can reach, with no limit on the number, for example the private server of a company and a public server (§ 8). Its contact card announces this list with a priority for each server.

Alice does not need to be on Bob's server, and her server does not need to know Bob's.

```
 Alice ── relay X           relay Z ── Bob
   │                           ▲
   └── she writes directly ────┘     X and Z may not know each other
```

Alice connects herself to Bob's server to drop the envelope. The connection is made on demand and given up if the server goes down. Bob answers the same way, on one of Alice's servers. The choice of server is made by the application, because only it knows which servers it reaches from its network: a private server can be inside a company network, out of reach of another server, as well as on the internet. If Bob is connected on only some of his servers, Alice tries the next ones.

A link between two relays is optional (§ 10). It is used to pass an envelope to a neighbouring relay where the recipient is connected, for example when a contact writes to the server that the recipient has left. It is also used to advise a client to move to a neighbour that has room (§ 10, "Moving a client") and to register with a directory. Only the administrator of a relay changes the list of its neighbours.

## 4. Private server and public server

Two separate settings carry the word "private" (§ 10).

The access mode says who can register on the server. In `open` mode, everyone except banned identities. In `allowlist` mode, only the identities the administrator allowed, by a single-use invitation or by accepting a request. A server in `allowlist` mode is called here a private or restricted server.

The links between servers say whom the server communicates with. A private relay accepts calls only from its explicitly linked neighbours, the directories do not offer it and it sends them nothing.

A common use is to run a private server for close contacts and to stay reachable on a public server for the others.

```
                       your contact card: servers = [ public (priority 0), private (priority 1) ]
   close contacts ───► private server ◄─────── you (registered on both at once)
   (invited)                                    ▲
   acquaintances ────► public server ───────────┘
   (no access to the private one)
```

You are registered on both servers and you receive what arrives on either. Your card says which servers are restricted (the `restricted` property) and lists the open servers first. A restricted server on which the reader of the card is not allowed is never contacted: the application does not try the connection and the server's administrator sees no refusal. The card does not give the address of a restricted server, only an identifier that the members of that server recognise: someone who receives your card without being a member does not learn it, and a member who adds that server recognises all their contacts on it at once.

When two contacts share a restricted server, they write to each other only through that server, or through all those they share, in the order of the card. If the server does not answer, the message waits in the send queue with the mention "waiting for the private server". It does not go through an open server without the agreement of both. With a plain acquaintance, who shares no restricted server with you, sending follows the order of their card, through their open servers.

Falling back on an open server is a setting per contact, which each side makes for its own sends: wait (`Never`, the default), be asked (`Ask`) or let it happen (`Auto`). Each side tells the other its choice with a small encrypted message, `route_policy`, which goes through the shared restricted server. The fallback takes place only if both accepted. In `Ask` mode the application puts the question as soon as the restricted server does not answer, once per outage, and the answer "yes" holds until the server is back. In `Auto` mode the fallback starts after 2 minutes without an answer. The contact answers the same way, according to their own setting.

The fallback is never remembered. Every send, and every resend of the queue, tries the restricted server first, and as soon as it delivers a message it is used again. The application shows a notice when the fallback starts and when it ends. For each recipient the application uses the list of their servers, without asking the user anything.

The conversations list does not tell the servers apart. The state of each one (connected, private, waiting for validation, refused) is visible in the settings, and the "If the private server does not answer" setting is in the contacts list. A private server can be installed in a company network or on the internet. Only the devices that reach it connect there, and the others go through the next server of the list. The members of a group can also be spread between a private server and public ones.

Exchanges with close contacts go only through a server administered by oneself or by someone trusted. If a public server goes down or is blocked, these exchanges go on. If the private server changes its address, the application sends the new list to the contacts (§ 10, "Moving a client"; MESSAGE_FLOWS, case 1.11).

The administrator of a server sees the metadata of those who connect to it: who, when, and the IP address during the connection. They never see the content. The public server where the acquaintances are sees the metadata of those exchanges in the same way. Nothing is provided to hide the traffic from a network that filters: a WebSocket connection over HTTPS to a domain can be blocked by its address or name. The contacts who have another server in their list stay reachable, and for the others the messages wait in the sender's queue.

## 5. Waking a phone whose application is closed

A phone does not keep a connection open when the application is closed. It is woken by a push notification with no content (§ 9).

```
 Alice          Bob's relay          Push gateway          Google/Apple        Bob (app closed)
   │ message         │                      │                    │                  │
   ├────────────────►│ Bob is absent: it    │                    │                  │
   │                 │ notes "waiting" and  │                    │                  │
   │                 │ has a token of Bob ─►│  push "wake up"    │                  │
   │                 │                      ├───────────────────►│ ────────────────►│ the system
   │                 │                      │                    │                  │ wakes the app
   │                 │◄─────────────────────────────── Bob connects and declares himself ready
   │◄── "ready" ─────┤
   │ sends its queue │
   ├────────────────►│ ───────────────────────────────────────────────────────────► │ decrypts,
   │                 │                                                               │ notifies
```

The notification carries no message. Its payload is the same every time, with no sender, no preview and no identity, so Google and Apple only see a wake-up order. The application is woken without any action from the user, by a high-priority data message. It opens a session of a few dozen seconds, connects to its servers and declares itself ready.

The relay then tells Alice that Bob is ready. If Alice has also closed her application, her phone is woken the same way. Alice sends her queue, Bob decrypts the message and shows a local notification. The sender's name and the preview are produced on the device and pass through no Google or Apple service.

Only the gateway holds the FCM and APNs credentials. A relay is linked to it by keys, with the agreement of both administrators, and a gateway with no linked relay refuses every request. When there is no push notification (token refused, phone that blocks the application, no gateway linked), the application polls its servers about every 15 minutes with the system's scheduler (WorkManager on Android). If Alice switches her phone off right after sending, the message waits for her return.

The desktop client does not use this mechanism. It stays connected as long as the program runs, and closing the window reduces it to the notification area.

## 6. When the connection drops

WebSocket carries whole messages. Each frame states its length and whether it is the last of the message, so there is no end character to look for in the JSON. The application and the relay put the frames of a message together and decode the JSON only once the message is complete (§ 11).

If the connection drops during a reception, the part already received is thrown away and no application sees a truncated message. A message whose reception was not confirmed stays in the sender's send queue and goes again (§ 2, point 3). The application reconnects every 30 seconds while the server does not answer, and skips an unreachable server for 30 seconds so that each send does not wait for a time-out. A relay that receives a message bigger than its limit closes the connection (code 1009) instead of filling its memory.

## 7. Files, groups, several devices

The relay does not store files. A file is announced in the conversation, the recipient asks for it when they want it, and the sender sends it in pieces of 48 KiB, each checked with a Merkle tree, while both are online. In a group, the members who already have the file supply it to the others (§ 6).

A group exists on no server. It is a signed state with a shared key that changes at each change of members or roles, and the members exchange it like messages. A member who stayed offline gets what they missed from the other members (§ 7).

The same identity can run on several devices connected at the same time. A message sent from one is copied to the others. A restored device can ask another one for the history, with a six-digit check code (§ 1).

## 8. First connection and choice of the server

An application that creates an identity has no server. It takes its first servers in this order (§ 4):

1. The server the user chose in advanced mode, which is then kept.
2. Otherwise, the relays offered by one of the directories built into the application, among those that take accounts and have room, starting with the device's country. The directories are asked in a random order, once.
3. If no directory answers, one of the built-in fallback relays, drawn at random.

These initial servers can change once. A restored backup that lists reachable servers adopts them, and so does the very first contact when it is on other reachable servers, so that both are on the same relay.

An application that has a relay does not contact a directory. The client library knows how to look for a relay when none of the identity's own answers, first among the contacts' and then in the directories, but no application calls it automatically.

## 9. What each component knows

| | Relay | Push gateway | Directory | Google / Apple |
|---|---|---|---|---|
| Content of messages | no | no | no | no |
| Who writes to whom, and when | yes (envelopes and waiting markers) | no | no | no |
| IP address of devices | yes, during the connection (no access log in the nginx example) | the relays' only | none kept | the phone's |
| Wake-up token | yes | yes | no | yes |
| Identity of the user | a computed identifier, no name or number | no | no | no |

A private relay answers only the accounts its administrator allowed and its linked neighbours. Its answer to an unknown address cannot be told from that of an address that does not exist (§ 10).

## 10. Links between servers

Links between servers (relay and relay, relay and push gateway, relay and directory) rest on one key pair per server and on the agreement of both administrators, given with the commands `add`, `requests` and `accept`. No secret is shared and every call between servers is signed. A request states the name, the address and the contact its author declares, and the administrator checks the fingerprint of the key by another means before accepting (§ 10, "Linking servers by mutual consent").
