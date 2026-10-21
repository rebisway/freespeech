# Wire models / Modèles de messages

*English, then français. The diagram is the same in both languages: class and field names are the ones of the code. / Le diagramme est le même dans les deux langues : les noms de classes et de champs sont ceux du code.*

## English

What travels between a client and a relay, and what an envelope carries. Field names are the JSON names. An envelope carries a Letter (or a contact-exchange wrapper) in its encrypted `msg`; a Letter carries a JSON string in its `payload`. See [PROTOCOL_API.en.md](../../PROTOCOL_API.en.md).

## Français

Ce qui circule entre un client et un relais, et ce que porte une enveloppe. Les noms de champs sont les noms JSON. Une enveloppe porte une lettre (ou une enveloppe de transport d'échange de contact) dans son `msg` chiffré ; une lettre porte une chaîne JSON dans son `payload`. Voir [PROTOCOL_API.fr.md](../../PROTOCOL_API.fr.md).

```mermaid
classDiagram
    direction LR

    class WsMessage {
        +string type
        +string request_id
        +string discovery_id
        +Envelope envelope
        +bool delivered
        +bool self_multi_device
        +bool online
        +string enc_key
        +string sign_key
        +string nonce
        +string signature
        +string invitation
        +bool request_access
        +string name
        +string note
        +string http_token
        +string access
        +ProvisionerInfo[] provisioners
        +string[] servers
        +string reason
        +string platform
        +string token
        +string gateway
        +bool ok
        +string message
    }

    class ProvisionerInfo {
        +string id
        +string enc_key
        +string sign_key
    }

    class Envelope {
        +string from
        +string to
        +int crypto
        +string msg
        +long ts
        +string reply_to
        +string to_server
    }

    class LetterPayload {
        +string type
        +string payload
        +string sig
        +string orig_to
    }

    class OtcEnvelope {
        +string type
        +string payload
    }

    class ContactCard {
        +string version
        +string nickname
        +string photo
        +string description
        +string discovery_id
        +string encryption_public_key
        +string signing_public_key
        +ServerEntry[] servers
        +string revocation_public_key
        +bool enable_push
        +string otc
        +string flag
        +long created_at
    }

    class ServerEntry {
        +string url
        +int priority
    }

    class MessageOp {
        +string target
        +string text
        +long seq
    }

    class AckOp {
        +string[] ids
    }

    class TypingOp {
        +string group
    }

    class MovedOp {
        +string old_id
        +ContactCard new_card
        +long ts
        +string sig
    }

    class PendingFlag {
        +string from
        +string to
        +string state
        +datetime next_connect_at
        +datetime updated_at
    }

    WsMessage o-- Envelope : "send, message"
    WsMessage o-- ProvisionerInfo : "registered, server_state"
    Envelope ..> LetterPayload : "msg, crypto = 1"
    Envelope ..> OtcEnvelope : "msg, crypto = 0"
    OtcEnvelope ..> ContactCard : "payload (JSON)"
    LetterPayload ..> ContactCard : "payload of identity_update"
    LetterPayload ..> MessageOp : "message_edit, message_retract, message_react, read_ack"
    LetterPayload ..> AckOp : "ack"
    LetterPayload ..> TypingOp : "typing"
    LetterPayload ..> MovedOp : "identity_moved"
    MovedOp o-- ContactCard
    ContactCard o-- ServerEntry
```

`PendingFlag` is the body of the flag calls (`/api/flags`), not a frame. / `PendingFlag` est le corps des appels sur les drapeaux (`/api/flags`), pas une trame.
