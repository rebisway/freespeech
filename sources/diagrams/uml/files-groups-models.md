# Files, groups and devices / Fichiers, groupes et appareils

*English, then français. Class and field names are the ones of the code (field names are the JSON names). / Les noms de classes et de champs sont ceux du code (les noms de champs sont les noms JSON).*

## English

The payloads of the Letters about files (section 5 of [PROTOCOL_API.en.md](../../PROTOCOL_API.en.md)), groups (section 6) and several devices (section 8). A `group_state` Letter carries the state as a string; the state itself is a `GroupStateData`. A group message is a `GroupMessageLetter` sealed with the group key.

## Français

Les contenus des lettres sur les fichiers (section 5 de [PROTOCOL_API.fr.md](../../PROTOCOL_API.fr.md)), les groupes (section 6) et les appareils multiples (section 8). Une lettre `group_state` porte l'état sous forme de chaîne ; l'état lui-même est un `GroupStateData`. Un message de groupe est un `GroupMessageLetter` scellé avec la clé de groupe.

```mermaid
classDiagram
    direction LR

    class AttachmentOffer {
        +string name
        +string mime
        +long size
        +string root
        +string thumbnail
    }
    class AttachmentRequest {
        +string ref
        +int[] missing
    }
    class AttachmentChunk {
        +string ref
        +int chunk_id
        +int total_chunks
        +string data
        +string[] proof
    }
    class AttachmentHave {
        +string ref
        +string have
        +bool complete
        +bool busy
    }

    class GroupMember {
        +string discovery_id
        +string nickname
        +string encryption_public_key
        +string signing_public_key
        +ServerEntry[] servers
    }
    class GroupStateData {
        +string group_id
        +int epoch
        +string prev_hash
        +long created_at
        +string name
        +string created_by
        +string[] moderators
        +GroupMember[] members
        +string key_check
    }
    class GroupStateLetter {
        +string state
        +string signer
        +string sig
        +string key_envelope
        +SponsorProof sponsor
    }
    class SponsorProof {
        +string id
        +long ts
        +string sig
    }
    class GroupMessageLetter {
        +string type
        +string payload
        +string group_id
        +int epoch
        +long seq
        +string reply_to
        +string sig
    }
    class GroupSyncLetter {
        +string group_id
        +int epoch
        +map have
    }
    class GroupRelayLetter {
        +string group_id
        +GroupStateLetter[] states
        +GroupKeyItem[] keys
        +GroupRelayMessage[] messages
        +BanOp[] bans
        +PinOp[] pins
        +GroupProfileOp profile
        +MemberProfile[] member_profiles
        +bool more
    }
    class GroupKeyItem {
        +int epoch
        +string state_hash
        +string envelope
    }
    class GroupRelayMessage {
        +string from
        +long ts
        +string msg
        +long seq
    }
    class BanOp {
        +string group_id
        +string kind
        +string id
        +long ts
        +string signer
        +string sig
    }
    class PinOp {
        +string group_id
        +string kind
        +string target
        +long ts
        +string signer
        +string sig
    }
    class PinUpdateLetter {
        +string group_id
        +PinOp[] ops
    }
    class BanUpdateLetter {
        +string group_id
        +BanOp[] ops
    }
    class GroupProfileOp {
        +string group_id
        +string name
        +string photo
        +string description
        +long ts
        +string signer
        +string sig
    }
    class MemberProfile {
        +string group_id
        +string id
        +string nickname
        +string photo
        +string description
        +long ts
        +string sig
        +string revocation_public_key
        +string revocation_sig
    }
    class GroupSuggestLetter {
        +string group_id
        +GroupMember candidate
        +long ts
        +string sig
    }
    class GroupLeaveLetter {
        +string group_id
    }

    class DeviceSyncRequest {
        +string id
        +string device
        +map have
        +map contacts
        +long ts
        +string code
    }
    class DeviceSyncData {
        +string request
        +string to_device
        +bool refused
        +bool last
        +Contact[] contacts
        +string[] replace
        +string[] remove
        +map history
        +AttachmentRecord[] attachments
    }
    class WipeOp {
        +long ts
        +string sig
    }

    GroupStateData o-- GroupMember
    GroupStateLetter ..> GroupStateData : "state, as a string"
    GroupStateLetter o-- SponsorProof
    GroupRelayLetter o-- GroupStateLetter
    GroupRelayLetter o-- GroupKeyItem
    GroupRelayLetter o-- GroupRelayMessage
    GroupRelayLetter o-- BanOp
    GroupRelayLetter o-- PinOp
    PinUpdateLetter o-- PinOp
    GroupRelayLetter o-- GroupProfileOp
    GroupRelayLetter o-- MemberProfile
    BanUpdateLetter o-- BanOp
    GroupSuggestLetter o-- GroupMember
    GroupRelayMessage ..> GroupMessageLetter : "msg, sealed"
```
