/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Crypto;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Relay;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client;

// Remote wipe (PROTOCOL §1, "Stolen phone"): the owner types the three phrases on another device and every device of the identity that
// was installed BEFORE that moment erases itself. The letter is signed by the revocation key, derived from the phrases and never stored: the
// identity keys sit in the database of the stolen phone, so a signature by them would prove nothing.
public sealed partial class FreeSpeechClient
{
    private const string TypeWipe = "wipe";   // listed in PROTOCOL §5 "Types de Letter"

    /// <summary>How long a wipe is offered again after it was sent: a stolen phone may only come online days later.</summary>
    public static readonly TimeSpan WipeResendFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan WipeResendEvery = TimeSpan.FromMinutes(2);

    /// <summary>A valid wipe reached this device: the application must erase everything it keeps for this profile and start over.</summary>
    public event Action? WipeRequested;

    private DocCollection<PendingWipe>? _pendingWipeDocs;
    private DateTime _lastWipeAttempt = DateTime.MinValue;
    private long _wipeSentTs;

    public sealed class PendingWipe
    {
        [JsonPropertyName("ts")] public long Ts { get; set; }
        [JsonPropertyName("sig")] public string Sig { get; set; } = "";
        [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    }

    private sealed class WipeOp
    {
        [JsonPropertyName("ts")] public long Ts { get; set; }
        [JsonPropertyName("sig")] public string Sig { get; set; } = "";
    }

    private DocCollection<PendingWipe> PendingWipeDocs => _pendingWipeDocs ??= new DocCollection<PendingWipe>(_db, "pending_wipe", _ => "wipe");

    /// <summary>What a revocation signature covers: this identity and the moment, so it cannot be moved to another identity or another time.</summary>
    internal static byte[] WipeSigned(string discoveryId, long ts) => Encoding.UTF8.GetBytes($"FreeSpeech-wipe-v1|{discoveryId}|{ts}");

    /// <summary>True when this device knows the revocation key's public half, so it would obey a wipe. False on an identity from before it existed.</summary>
    public bool RemoteWipeReady => _identity.RevocationPublicKey != null;

    /// <summary>Do these three phrases give this device's identity? Throws <see cref="ClientException"/> (<c>wipe_wrong_phrases</c>) if not. Slow (one key derivation).</summary>
    public void CheckMyPhrases(string p1, string p2, string p3) => CheckPhrasesAreMine(p1, p2, p3);

    private void CheckPhrasesAreMine(string p1, string p2, string p3)
    {
        UserIdentity derived;
        try { derived = IdentityService.Derive(p1, p2, p3, null); }
        catch (ArgumentException ex) { throw new ClientException(ex.Message, "wipe_wrong_phrases"); }
        if (derived.DiscoveryId != _identity.DiscoveryId)
            throw new ClientException("These phrases are not the ones of this identity.", "wipe_wrong_phrases");
    }

    /// <summary>
    /// Makes an identity created before the revocation key existed obey a wipe: the phrases are typed once, and only the public half is kept.
    /// Throws <see cref="ClientException"/> (<c>wipe_wrong_phrases</c>) when they are not this identity's.
    /// </summary>
    public void EnableRemoteWipe(string phrase1, string phrase2, string phrase3)
    {
        CheckPhrasesAreMine(phrase1, phrase2, phrase3);
        _identity.RevocationPublicKey = IdentityService.DeriveRevocationKey(phrase1, phrase2, phrase3).PublicKey;
        _identityStorage.Save(_identity);
    }

    /// <summary>
    /// Erases every other device of this identity installed before now. Needs the three phrases (they sign the order). Sent live to the
    /// devices connected now, then offered again for <see cref="WipeResendFor"/> by the periodic tick. Returns true when a device got it
    /// at once. Best effort: the relay stores nothing, so a device that never connects while this one does is not reached.
    /// </summary>
    public async Task<bool> SendWipeAsync(string phrase1, string phrase2, string phrase3)
    {
        CheckPhrasesAreMine(phrase1, phrase2, phrase3);
        var (seed, pub) = IdentityService.DeriveRevocationKey(phrase1, phrase2, phrase3);
        if (_identity.RevocationPublicKey == null || !_identity.RevocationPublicKey.AsSpan().SequenceEqual(pub))
        {
            _identity.RevocationPublicKey = pub;
            _identityStorage.Save(_identity);
        }

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sig = Primitives.Ed25519Sign(seed, WipeSigned(_identity.DiscoveryId, ts));
        var pending = new PendingWipe { Ts = ts, Sig = Convert.ToBase64String(sig), CreatedAt = DateTime.UtcNow };
        PendingWipeDocs.Save([pending]);
        _wipeSentTs = ts;
        _lastWipeAttempt = DateTime.UtcNow;
        return await TrySendWipeAsync(pending);
    }

    private async Task<bool> TrySendWipeAsync(PendingWipe w)
    {
        try
        {
            var ts = NextTs();
            var payload = JsonSerializer.Serialize(new WipeOp { Ts = w.Ts, Sig = w.Sig });
            var cipher = MessageCrypto.Encrypt(_identity.DiscoveryId, _identity.DiscoveryId, TypeWipe, payload, ts, _identity.EncryptionPublicKey, _identity.SigningPrivateKey);
            var envelope = new Envelope { From = _identity.DiscoveryId, To = _identity.DiscoveryId, Crypto = 1, Msg = cipher, Ts = ts };
            return (await _relay.SendAsync(_relay.Candidates(null), envelope)).Delivered;
        }
        catch (Exception ex) when (ex is ClientException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Called by the periodic tick: offers a wipe sent earlier again, until it is old.</summary>
    private async Task ResendPendingWipeAsync()
    {
        var w = PendingWipeDocs.Load().FirstOrDefault();
        if (w == null) return;
        if (DateTime.UtcNow - w.CreatedAt > WipeResendFor) { PendingWipeDocs.Save([]); return; }
        if (DateTime.UtcNow - _lastWipeAttempt < WipeResendEvery) return;
        _lastWipeAttempt = DateTime.UtcNow;
        await TrySendWipeAsync(w);
    }

    /// <summary>A wipe letter arrived (addressed to my own identity). It counts only with the revocation signature, and only for a device older than it.</summary>
    private void HandleWipe(string payload)
    {
        if (_identity.RevocationPublicKey == null) return;   // cannot be verified: ignored, never obeyed on faith
        WipeOp? op;
        try { op = JsonSerializer.Deserialize<WipeOp>(payload); }
        catch (JsonException) { return; }
        if (op == null || op.Ts == _wipeSentTs) return;       // the sender never erases itself

        byte[] sig;
        try { sig = Convert.FromBase64String(op.Sig); }
        catch (FormatException) { return; }
        if (!Primitives.Ed25519Verify(_identity.RevocationPublicKey, WipeSigned(_identity.DiscoveryId, op.Ts), sig)) return;

        // A device installed after the order (the owner restored later) ignores it, so an old order can never erase a new phone.
        if (!OrderAppliesTo(_identity.CreatedAt, op.Ts)) return;

        WipeRequested?.Invoke();
    }

    /// <summary>A wipe ordered at <paramref name="orderTs"/> (Unix ms) erases a device installed strictly before it.</summary>
    internal static bool OrderAppliesTo(DateTime installedAt, long orderTs) =>
        new DateTimeOffset(DateTime.SpecifyKind(installedAt.ToUniversalTime(), DateTimeKind.Utc)).ToUnixTimeMilliseconds() < orderTs;
}
