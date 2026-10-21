# Client library / Bibliothèque cliente

*English, then français. Class names are the ones of the code. / Les noms de classes sont ceux du code.*

## English

`FreeSpeechClient` is the one entry point of the library (`freespeech.client`): an application creates it, loads or creates an identity, then connects. It owns the stores, which keep their data in the profile's single encrypted database, and the services that implement the protocol. It has no user interface: the apps only call it and listen to its events. See [ARCHITECTURE.en.md](../../ARCHITECTURE.en.md).

## Français

`FreeSpeechClient` est l'unique point d'entrée de la bibliothèque (`freespeech.client`) : une application le crée, charge ou crée une identité, puis se connecte. Il possède les magasins, qui gardent leurs données dans la base chiffrée unique du profil, et les services qui mettent en œuvre le protocole. Il n'a aucune interface : les applications ne font que l'appeler et écouter ses événements. Voir [ARCHITECTURE.fr.md](../../ARCHITECTURE.fr.md).

```mermaid
classDiagram
    direction TB

    class FreeSpeechClient {
        +TryLoadIdentity()
        +CreateIdentity()
        +Initialize()
        +ConnectAsync()
        +SendAsync()
        +DisposeAsync()
    }

    class ClientDatabase {
        freespeech.db (Vault)
    }

    class UserIdentity
    class IdentityStorage
    class HistoryStore
    class ContactStore
    class ContactAliasStore
    class OtcEntryStore
    class PendingContactStore
    class PendingMessageStore
    class RelayPool
    class RelayClient
    class ContactExchangeService
    class AttachmentService
    class GroupService
    class BackupService
    class BackgroundScheduler

    FreeSpeechClient *-- ClientDatabase
    FreeSpeechClient *-- UserIdentity
    FreeSpeechClient *-- RelayPool
    FreeSpeechClient *-- ContactExchangeService
    FreeSpeechClient *-- AttachmentService
    FreeSpeechClient *-- GroupService
    FreeSpeechClient *-- BackupService
    FreeSpeechClient *-- BackgroundScheduler
    FreeSpeechClient *-- IdentityStorage
    FreeSpeechClient *-- HistoryStore
    FreeSpeechClient *-- ContactStore
    FreeSpeechClient *-- ContactAliasStore
    FreeSpeechClient *-- OtcEntryStore
    FreeSpeechClient *-- PendingContactStore
    FreeSpeechClient *-- PendingMessageStore

    IdentityStorage ..> ClientDatabase
    HistoryStore ..> ClientDatabase
    ContactStore ..> ClientDatabase
    ContactAliasStore ..> ClientDatabase
    OtcEntryStore ..> ClientDatabase
    PendingContactStore ..> ClientDatabase
    PendingMessageStore ..> ClientDatabase

    RelayPool o-- "1..*" RelayClient : one per server
    ContactExchangeService ..> RelayPool
    ContactExchangeService ..> ContactStore
    ContactExchangeService ..> OtcEntryStore
    ContactExchangeService ..> PendingContactStore
```
