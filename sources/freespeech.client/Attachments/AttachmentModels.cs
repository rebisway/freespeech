/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json.Serialization;

namespace FreeSpeech.Client.Attachments;

/// <summary>Payload of an "attachment" Letter (protocol §6): metadata + optional thumbnail, sent like a normal message. The attachment's uid is the Letter's own message_id ("from:ts"), not repeated here.</summary>
public class AttachmentOffer
{
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("mime")] public required string Mime { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>Merkle root (hex) over the 48 KiB chunks — the content identity. The chunk count is derived from <see cref="Size"/>, never sent.</summary>
    [JsonPropertyName("root")] public required string Root { get; set; }

    /// <summary>Small preview (base64 JPEG) so the receiver can decide before downloading. Never produced by the console client (no imaging library) — mobile fills it.</summary>
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
}

/// <summary>Payload of an "attachment_request" Letter: <c>missing</c> null = every chunk, otherwise only those chunk ids (resume).</summary>
public class AttachmentRequest
{
    [JsonPropertyName("ref")] public required string Ref { get; set; }
    [JsonPropertyName("missing")] public List<int>? Missing { get; set; }
}

/// <summary>Payload of an "attachment_chunk" Letter. Each chunk travels in its own encrypted+signed envelope.</summary>
public class AttachmentChunk
{
    [JsonPropertyName("ref")] public required string Ref { get; set; }
    [JsonPropertyName("chunk_id")] public int ChunkId { get; set; }
    [JsonPropertyName("total_chunks")] public int TotalChunks { get; set; }
    [JsonPropertyName("data")] public required string Data { get; set; }

    /// <summary>Merkle proof (base64 sibling hashes, leaf to root) letting the receiver verify this chunk on its own, whoever served it.</summary>
    [JsonPropertyName("proof")] public List<string> Proof { get; set; } = [];
}

/// <summary>
/// Payload of an "attachment_have" Letter (groups only, between members): which chunks of a file I hold — the answer to a request I cannot serve in full. A member that
/// holds only part of the file (still downloading it) serves those chunks and says what it holds; <c>Busy</c> = "I can serve nobody now" (the transfer setting of that
/// device), the other fields then mean nothing. The requester hands the chunks I lack to other holders at once instead of waiting for its silence timer.
/// </summary>
public class AttachmentHave
{
    [JsonPropertyName("ref")] public required string Ref { get; set; }

    /// <summary>Base64 bitmap, bit (i % 8) of byte (i / 8) set = chunk i held. Empty when <see cref="Complete"/> (everything is held) or when nothing is.</summary>
    [JsonPropertyName("have")] public string Have { get; set; } = "";
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    [JsonPropertyName("busy")] public bool Busy { get; set; }
}

/// <summary>Chunk sets as the bitmaps of <see cref="AttachmentHave"/>.</summary>
public static class ChunkBitmap
{
    public static string Encode(IEnumerable<int> ids, int chunks)
    {
        var bytes = new byte[(chunks + 7) / 8];
        foreach (var i in ids) if (i >= 0 && i < chunks) bytes[i / 8] |= (byte)(1 << (i % 8));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The set, or null when the text is not a bitmap of exactly this size (a peer's word is never trusted blindly).</summary>
    public static HashSet<int>? Decode(string? text, int chunks)
    {
        if (string.IsNullOrEmpty(text)) return [];
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); } catch (FormatException) { return null; }
        if (bytes.Length != (chunks + 7) / 8) return null;
        var set = new HashSet<int>();
        for (var i = 0; i < chunks; i++) if ((bytes[i / 8] & (1 << (i % 8))) != 0) set.Add(i);
        return set;
    }
}
public enum AttachmentDirection { Sent, Received }

/// <summary>Offered = metadata received, not downloaded; Receiving = requested, chunks arriving; Complete; Failed = too many chunks failed their Merkle proof; Removed = the content was deleted from this device (by me, or removed by the author): never served, never downloaded again; Archived = the file was moved into an archive file to free space (the record, and the thumbnail of a picture or video, stay): loading that archive makes it Complete again.</summary>
public enum AttachmentState { Offered, Receiving, Complete, Failed, Removed, Archived }

/// <summary>Local bookkeeping for one attachment, sent or received.</summary>
public class AttachmentRecord
{
    /// <summary>message_id of the "attachment" Letter ("senderId:ts").</summary>
    [JsonPropertyName("ref")] public required string Ref { get; set; }
    [JsonPropertyName("contact_id")] public required string ContactId { get; set; }
    [JsonPropertyName("direction")] public AttachmentDirection Direction { get; set; }
    [JsonPropertyName("name")] public required string Name { get; set; }
    [JsonPropertyName("mime")] public required string Mime { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("root")] public required string Root { get; set; }
    [JsonPropertyName("chunks")] public int Chunks { get; set; }

    /// <summary>Corrupt chunks received for this file (failed Merkle proof); too many = the download is abandoned.</summary>
    [JsonPropertyName("strikes")] public int Strikes { get; set; }

    /// <summary>Set for a file offered in a group (§7): any member holding the complete file may serve it, so the download is spread over several holders.</summary>
    [JsonPropertyName("group_id")] public string? GroupId { get; set; }

    /// <summary>Failed proofs per serving peer. A peer reaching the limit is banned for this file (the download continues from the others); in 1-to-1 there is nobody else, so it fails.</summary>
    [JsonPropertyName("holder_strikes")] public Dictionary<string, int> HolderStrikes { get; set; } = [];
    [JsonPropertyName("banned_holders")] public List<string> BannedHolders { get; set; } = [];

    /// <summary>The download is paused because the transfer setting forbids this network (mobile data): it resumes by itself on Wi-Fi.</summary>
    [JsonPropertyName("waiting_network")] public bool WaitingNetwork { get; set; }

    /// <summary>The user chose "download anyway" for this file: the transfer setting no longer holds it back.</summary>
    [JsonPropertyName("network_override")] public bool NetworkOverride { get; set; }
    /// <summary>The record came from another device of my identity (the mirror, protocol §1): the file is fetched from that device, not from the contact.</summary>
    [JsonPropertyName("from_my_device")] public bool FromMyDevice { get; set; }
    [JsonPropertyName("state")] public AttachmentState State { get; set; }

    /// <summary>The content-addressed blob (<c>blobs/{root}.ext</c>) once Complete — shared by every record with the same root, in any conversation.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }

    [JsonPropertyName("last_activity_at")] public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}
