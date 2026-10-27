/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client.Contacts;

/// <summary>
/// Contact lifecycle over OTC (protocol §3), on the raw WebSocket relay.
///
///   1. EXPORT  — we generate an OTC, build our card, export it out-of-band.
///   2. IMPORT  — we receive their card, encrypt our own card with their OTC,
///                store a PendingContact, and try to deliver it.
///   3. CONFIRM — we receive their encrypted card, validate the OTC, ack
///                immediately, then either auto-confirm (qr) or wait for the
///                user (manual) before replying with our own card.
///   4. FINALIZE — we receive their confirmation and store the Contact.
///
/// The relay/server never stores any of this (§9/§10 — presence flags only),
/// so PendingContact is what survives an offline target: kept locally,
/// retried by the caller's background scheduler until delivered.
/// </summary>
public class ContactExchangeService
{
    private static readonly TimeSpan QrValidity = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ManualValidity = TimeSpan.FromDays(30);

    private readonly UserIdentity _identity;
    private readonly RelayPool _relay;
    private readonly OtcEntryStore _otcEntries;
    private readonly PendingContactStore _pendingContacts;
    private readonly ContactStore _contacts;

    /// <summary>A manual-flag request is waiting for the user to accept or reject it.</summary>
    public event Action<ContactCard>? OnContactRequestReceived;

    /// <summary>A contact was confirmed and stored (either side of the exchange).</summary>
    public event Action<Contact>? OnContactConfirmed;

    // Card + OTC kept for manual requests awaiting the user's decision, keyed by their discovery_id.
    private readonly Dictionary<string, (ContactCard Card, string Otc)> _pendingConfirmations = new();

    /// <summary>Called with (id, revocation key) for every card that becomes a contact: the key is pinned on first sight (§12, identity migration).</summary>
    private readonly Action<string, string?>? _pinRevocation;

    public ContactExchangeService(
        UserIdentity identity, RelayPool relay,
        OtcEntryStore otcEntries, PendingContactStore pendingContacts, ContactStore contacts, Action<string, string?>? pinRevocation = null)
    {
        _pinRevocation = pinRevocation;
        _identity = identity;
        _relay = relay;
        _otcEntries = otcEntries;
        _pendingContacts = pendingContacts;
        _contacts = contacts;
        PurgeExpiredCodes();
    }

    /// <summary>
    /// Codes older than their validity (10 minutes for a QR, 30 days for a copied card) plus a week are useless: a request using one is
    /// ignored. The week keeps a code whose exchange began just before expiry, so its confirmation can still complete. The expiry date
    /// is never stored: it is the creation date plus the validity of the card's kind.
    /// </summary>
    public int PurgeExpiredCodes()
    {
        var grace = TimeSpan.FromDays(7);
        var now = DateTime.UtcNow;
        return _otcEntries.RemoveWhere(e => now - e.CreatedAt > (e.Flag == "qr" ? QrValidity : ManualValidity) + grace);
    }

    // ------------------------------------------------------------------
    // 1. EXPORT
    // ------------------------------------------------------------------

    /// <summary>Builds a card ready for out-of-band export. Generates and stores a fresh OTC.</summary>
    /// <param name="flag">"manual" (valid 1 month) or "qr" (valid 10 min).</param>
    /// <param name="includePhoto">False for a QR code: the picture would not fit, it reaches the contact with the next profile update.</param>
    public ContactCard ExportCard(string flag, bool includePhoto = true)
    {
        if (flag != "manual" && flag != "qr")
            throw new ArgumentException("Flag must be 'manual' or 'qr'.", nameof(flag));

        PurgeExpiredCodes();   // every new card tidies the old ones
        var otc = OtcCrypto.GenerateOtc();
        _otcEntries.Add(new OtcEntry { Otc = otc, Flag = flag, CreatedAt = DateTime.UtcNow });

        var card = BuildOurCard();
        if (!includePhoto) card.Photo = null;
        card.Otc = otc;
        card.Flag = flag;
        return card;
    }

    /// <summary>Our own current card (§5 identity_update payload, or any other future use) — no OTC, unlike <see cref="ExportCard"/>.</summary>
    public ContactCard BuildCurrentCard() => BuildOurCard();

    public static string SerializeCard(ContactCard card) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(card)));

    public static ContactCard? DeserializeCard(string base64)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return JsonSerializer.Deserialize<ContactCard>(json);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 2. IMPORT
    // ------------------------------------------------------------------

    /// <summary>Imports a card received out-of-band and attempts immediate delivery of our reply.</summary>
    public async Task ImportCardAsync(ContactCard theirCard)
    {
        if (string.IsNullOrWhiteSpace(theirCard.Otc))
            throw new InvalidOperationException("Imported card has no OTC.");
        if (theirCard.Servers.Count == 0)
            throw new InvalidOperationException("Imported card has no server.");

        // The issuer ignores a request made with an expired code, in silence: tell the user now, instead of leaving them waiting for an answer
        // that cannot come. The expiry is the card's creation date plus the validity of its kind (a couple of minutes of clock difference allowed).
        if (theirCard.CreatedAt > 0 && theirCard.Flag is "qr" or "manual")
        {
            var age = DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(theirCard.CreatedAt).UtcDateTime;
            if (age > (theirCard.Flag == "qr" ? QrValidity : ManualValidity) + TimeSpan.FromMinutes(2))
                throw new ClientException("This contact card has expired: ask for a new one.", "card_expired");
        }

        var ourCard = BuildOurCard();
        var otcEnv = new OtcEnvelope { Type = "contact_request", Payload = JsonSerializer.Serialize(ourCard) };
        var encryptedOurCard = OtcCrypto.EncryptWithOtc(JsonSerializer.Serialize(otcEnv), theirCard.Otc);

        var pending = new PendingContact
        {
            Id = Guid.NewGuid().ToString("N"),
            TheirCard = theirCard,
            EncryptedOurCard = encryptedOurCard,
            Otc = theirCard.Otc,
            CreatedAt = DateTime.UtcNow,
        };
        _pendingContacts.Add(pending);

        await TrySendPendingContactAsync(pending);
    }

    // ------------------------------------------------------------------
    // 3. CONFIRM — incoming crypto=0 envelope
    // ------------------------------------------------------------------

    /// <summary>Routes an incoming crypto=0 envelope through the OTC contact-exchange state machine.</summary>
    public async Task HandleIncomingOtcEnvelopeAsync(string fromDiscoveryId, string encryptedMessage)
    {
        // Case A: this is a reply to a card WE imported (we hold the OTC in PendingContact).
        var pending = _pendingContacts.FindByTargetId(fromDiscoveryId);
        if (pending != null)
        {
            var plaintext = OtcCrypto.TryDecryptWithOtc(encryptedMessage, pending.Otc);
            if (plaintext == null) return; // not for this OTC — ignore

            var env = JsonSerializer.Deserialize<OtcEnvelope>(plaintext);
            if (env?.Type == "contact_ack")
            {
                // Keep the OTC so we can still decrypt the contact_confirm that follows.
                _otcEntries.Add(new OtcEntry { Otc = pending.Otc, Flag = pending.TheirCard.Flag ?? "manual", CreatedAt = pending.CreatedAt });
                _pendingContacts.Remove(pending.Id);
                return;
            }

            if (env?.Type == "contact_confirm" && env.Payload != null)
            {
                var theirCard = JsonSerializer.Deserialize<ContactCard>(env.Payload);
                if (theirCard == null) return;

                _contacts.Upsert(ToContact(theirCard));
                _pendingContacts.Remove(pending.Id);
                OnContactConfirmed?.Invoke(ToContact(theirCard));
            }
            return;
        }

        // Case B: fresh request — try every OTC we generated ourselves.
        var match = OtcCrypto.TryDecryptWithAnyOtc(encryptedMessage, _otcEntries.All);
        if (match == null) return; // not for us, or already consumed

        var (otcEntry, requestPlaintext) = match.Value;
        var otcEnv = JsonSerializer.Deserialize<OtcEnvelope>(requestPlaintext);
        if (otcEnv?.Type == "contact_confirm" && otcEnv.Payload != null)
        {
            // Direct confirm (ACK step skipped/lost) — finalize straight away.
            var theirCard = JsonSerializer.Deserialize<ContactCard>(otcEnv.Payload);
            if (theirCard == null) return;
            _contacts.Upsert(ToContact(theirCard));
            _otcEntries.Remove(otcEntry.Otc);
            OnContactConfirmed?.Invoke(ToContact(theirCard));
            return;
        }

        if (otcEnv?.Type != "contact_request" || otcEnv.Payload == null) return;

        var requestCard = JsonSerializer.Deserialize<ContactCard>(otcEnv.Payload);
        if (requestCard == null) return;

        if (!IsOtcStillValid(otcEntry))
            return; // expired — silently ignore

        // ACK immediately so the sender's retry loop stops.
        await SendOtcEnvelopeAsync(requestCard, otcEntry.Otc, new OtcEnvelope { Type = "contact_ack" });

        if (otcEntry.Flag == "qr")
        {
            await DoConfirmAsync(requestCard, otcEntry.Otc);
            return;
        }

        // Manual: defer to the user.
        _pendingConfirmations[requestCard.DiscoveryId] = (requestCard, otcEntry.Otc);
        OnContactRequestReceived?.Invoke(requestCard);
    }

    /// <summary>Accepts a pending manual contact request (called after the user confirms).</summary>
    public async Task ConfirmPendingAsync(string discoveryId)
    {
        if (!_pendingConfirmations.Remove(discoveryId, out var ctx))
            throw new InvalidOperationException("No pending confirmation for this id.");

        await DoConfirmAsync(ctx.Card, ctx.Otc);
    }

    public IEnumerable<ContactCard> PendingConfirmations => _pendingConfirmations.Values.Select(v => v.Card);

    private async Task DoConfirmAsync(ContactCard theirCard, string otc)
    {
        var contact = ToContact(theirCard);
        _contacts.Upsert(contact);
        _otcEntries.Remove(otc);

        var ourCard = BuildOurCard();
        var confirmEnv = new OtcEnvelope { Type = "contact_confirm", Payload = JsonSerializer.Serialize(ourCard) };
        await SendOtcEnvelopeAsync(theirCard, otc, confirmEnv);

        OnContactConfirmed?.Invoke(contact);
    }

    // ------------------------------------------------------------------
    // Retry loop (called by the caller's background scheduler)
    // ------------------------------------------------------------------

    /// <summary>Retries delivery of every locally-pending contact request. No-op for those already sent recently.</summary>
    public async Task RetryAllPendingAsync()
    {
        foreach (var pc in _pendingContacts.All.ToList())
            await TrySendPendingContactAsync(pc);
    }

    private async Task TrySendPendingContactAsync(PendingContact pc)
    {
        var targetId = pc.TheirCard.DiscoveryId;

        var envelope = new Envelope
        {
            From = _identity.DiscoveryId,
            To = targetId,
            Crypto = 0,
            Msg = pc.EncryptedOurCard,
            Ts = DateTime.UtcNow.Ticks,
        };
        // Relay has no store-and-forward — an undelivered send is simply retried on the next tick.
        await _relay.SendAsync(_relay.Candidates(pc.TheirCard.Servers), envelope);
        _pendingContacts.TouchLastAttempt(pc.Id);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task SendOtcEnvelopeAsync(ContactCard theirCard, string otc, OtcEnvelope otcEnv)
    {
        var encrypted = OtcCrypto.EncryptWithOtc(JsonSerializer.Serialize(otcEnv), otc);
        var envelope = new Envelope
        {
            From = _identity.DiscoveryId,
            To = theirCard.DiscoveryId,
            Crypto = 0,
            Msg = encrypted,
            Ts = DateTime.UtcNow.Ticks,
        };
        await _relay.SendAsync(_relay.Candidates(theirCard.Servers), envelope);
    }

    private bool IsOtcStillValid(OtcEntry entry)
    {
        var age = DateTime.UtcNow - entry.CreatedAt;
        return entry.Flag switch
        {
            "qr" => age <= QrValidity,
            "manual" => age <= ManualValidity,
            _ => false,
        };
    }

    /// <summary>Advertises the identity's own server list (§8), in priority order.</summary>
    private ContactCard BuildOurCard() => new()
    {
        Nickname = _identity.Nickname ?? _identity.DiscoveryId[..8],
        Photo = _identity.Photo,
        Description = _identity.Description,
        DiscoveryId = _identity.DiscoveryId,
        EncryptionPublicKey = Convert.ToBase64String(_identity.EncryptionPublicKey),
        SigningPublicKey = Convert.ToBase64String(_identity.SigningPublicKey),
        RevocationPublicKey = _identity.RevocationPublicKey == null ? null : Convert.ToBase64String(_identity.RevocationPublicKey),
        Servers = Relay.RoutePolicy.CardServers(_identity.Servers),
        EnablePush = false,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    /// <summary>The card becomes a contact: its revocation key is pinned first (never replaced later).</summary>
    private Contact ToContact(ContactCard card)
    {
        _pinRevocation?.Invoke(card.DiscoveryId, card.RevocationPublicKey);
        return CardToContact(card);
    }

    private static Contact CardToContact(ContactCard card) => new()
    {
        DiscoveryId = card.DiscoveryId,
        Nickname = card.Nickname,
        Photo = card.Photo,
        Description = card.Description,
        EncryptionPublicKey = card.EncryptionPublicKey,
        SigningPublicKey = card.SigningPublicKey,
        Servers = card.Servers,
        ConfirmedAt = DateTime.UtcNow,
    };
}
