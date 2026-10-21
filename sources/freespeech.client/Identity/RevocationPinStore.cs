/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text.Json.Serialization;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client.Identity;

/// <summary>
/// The revocation public key of each identity I know, pinned on first sight and never replaced (PROTOCOL §12, identity migration): a
/// migration notice counts only when it is signed by the key pinned here. Replacing a pinned key would let whoever holds the signing
/// key of a stolen phone choose the key that later "proves" the migration, so a different key for an id already pinned is refused.
/// </summary>
public sealed class RevocationPinStore
{
    private sealed class Pin
    {
        [JsonPropertyName("id")] public required string Id { get; set; }
        [JsonPropertyName("key")] public required string Key { get; set; }
        [JsonPropertyName("first_seen")] public DateTime FirstSeen { get; set; }
    }

    private readonly DocCollection<Pin> _docs;
    private readonly List<Pin> _pins;
    private readonly object _lock = new();

    public RevocationPinStore(ClientDatabase db)
    {
        _docs = new DocCollection<Pin>(db, "revocation_pins", p => p.Id);
        _pins = _docs.Load();
    }

    /// <summary>The pinned key (base64) of this identity, or null when none was ever seen.</summary>
    public string? Find(string discoveryId)
    {
        lock (_lock) return _pins.Find(p => p.Id == discoveryId)?.Key;
    }

    /// <summary>
    /// Pins <paramref name="keyBase64"/> for this identity if none is pinned yet. True when the key is now the pinned one (new pin, or the same
    /// as before); false for an empty key or one that differs from the pin (kept as it was).
    /// </summary>
    public bool TryPin(string discoveryId, string? keyBase64)
    {
        if (string.IsNullOrWhiteSpace(keyBase64)) return false;
        lock (_lock)
        {
            var existing = _pins.Find(p => p.Id == discoveryId);
            if (existing != null) return existing.Key == keyBase64;
            _pins.Add(new Pin { Id = discoveryId, Key = keyBase64, FirstSeen = DateTime.UtcNow });
            _docs.Save(_pins);
            return true;
        }
    }

    /// <summary>All pins as (id, key): what a backup keeps.</summary>
    public IReadOnlyList<(string Id, string Key)> All
    {
        get { lock (_lock) return _pins.Select(p => (p.Id, p.Key)).ToList(); }
    }
}
