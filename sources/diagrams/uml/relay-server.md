# Relay server / Serveur relais

*English, then français. Class and interface names are the ones of the code. / Les noms de classes et d'interfaces sont ceux du code.*

## English

What `RelaySocketHandler` uses to serve one WebSocket connection: who is connected (`IPresenceService`, `ISocketRegistry`), delivery to the recipient (`IMessageDeliveryService`), neighbouring servers (`ISiblingRegistry`, `IS2SClient`), push wake-ups (`IPushRegistry`, `IPushNotifier`), presence flags (`IPendingFlagService`, `IHttpSessionService`), load shedding (`IRedirectService`) and who may register (`IClientAccess`). The server stores no message. See [ARCHITECTURE.en.md](../../ARCHITECTURE.en.md).

## Français

Ce que `RelaySocketHandler` utilise pour servir une connexion WebSocket : qui est connecté (`IPresenceService`, `ISocketRegistry`), la livraison au destinataire (`IMessageDeliveryService`), les serveurs voisins (`ISiblingRegistry`, `IS2SClient`), les réveils push (`IPushRegistry`, `IPushNotifier`), les drapeaux de présence (`IPendingFlagService`, `IHttpSessionService`), le délestage (`IRedirectService`) et qui peut s'inscrire (`IClientAccess`). Le serveur ne stocke aucun message. Voir [ARCHITECTURE.fr.md](../../ARCHITECTURE.fr.md).

```mermaid
classDiagram
    direction LR

    class RelaySocketHandler {
        +HandleAsync(WebSocket)
    }

    class IPresenceService {
        <<interface>>
        discovery_id to connections
    }
    class ISocketRegistry {
        <<interface>>
        connection id to socket
    }
    class IMessageDeliveryService {
        <<interface>>
        +DeliverLocallyAsync(Envelope)
    }
    class ISiblingRegistry {
        <<interface>>
        public or private status, neighbours
    }
    class IS2SClient {
        <<interface>>
        calls to a neighbour
    }
    class IPushRegistry {
        <<interface>>
        push token per identity
    }
    class IPushNotifier {
        <<interface>>
        +WakeAsync(discovery_id)
    }
    class IPendingFlagService {
        <<interface>>
        WAITING and READY flags
    }
    class IHttpSessionService {
        <<interface>>
        http_token of a connection
    }
    class IRedirectService {
        <<interface>>
        load shedding
    }
    class IClientAccess {
        <<interface>>
        open or allowlist, bans, invitations
    }

    RelaySocketHandler ..> IPresenceService
    RelaySocketHandler ..> ISocketRegistry
    RelaySocketHandler ..> IMessageDeliveryService
    RelaySocketHandler ..> ISiblingRegistry
    RelaySocketHandler ..> IS2SClient
    RelaySocketHandler ..> IPushRegistry
    RelaySocketHandler ..> IPushNotifier
    RelaySocketHandler ..> IPendingFlagService
    RelaySocketHandler ..> IHttpSessionService
    RelaySocketHandler ..> IRedirectService
    RelaySocketHandler ..> IClientAccess
    IMessageDeliveryService ..> IPresenceService
    IMessageDeliveryService ..> ISocketRegistry
```
