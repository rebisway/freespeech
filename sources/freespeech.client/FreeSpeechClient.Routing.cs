/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Crypto;
using FreeSpeech.Client.Outbox;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client;

/// <summary>The route_policy letter: whether the sender allows an open server for their messages with the receiver while the restricted servers they share do not answer.</summary>
public sealed class RoutePolicyLetter
{
    [JsonPropertyName("open_fallback")] public bool OpenFallback { get; set; }
}

public sealed partial class FreeSpeechClient
{
    private const string TypeRoutePolicy = "route_policy";

    /// <summary>Since when none of the restricted servers shared with a contact answers; absent while one does.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _privateDownSince = new();

    /// <summary>Contacts for whom the user said yes to an open server: it lasts until the restricted servers answer again.</summary>
    private readonly ConcurrentDictionary<string, bool> _fallbackApproved = new();

    /// <summary>Contacts reached through an open server because the restricted ones do not answer.</summary>
    private readonly ConcurrentDictionary<string, bool> _fellBack = new();

    /// <summary>Contacts for whom the proposal of an open server was already raised in the current outage.</summary>
    private readonly ConcurrentDictionary<string, bool> _fallbackProposed = new();

    /// <summary>Raised once per outage in "ask" mode when the restricted servers shared with a contact do not answer and both sides allow an open server: the app offers it.</summary>
    public event Action<Contact>? OpenFallbackProposed;

    /// <summary>Whether this is a restricted server of mine that I am let in on (registered there, not turned away since).</summary>
    private bool InRestricted(string url) =>
        _identity.Servers.Any(s => s.Restricted && RelayPool.Normalize(s.Url) == url) && _relay.RefusalOf(url) == null;

    private IReadOnlyList<string> CandidatesFor(string peerId) => RouteFor(peerId).Urls;

    /// <summary>The address of my restricted server that an identifier in the card of <paramref name="ownerId"/> names, or null.</summary>
    private string? ResolveCircle(string ownerId, string circle) => CircleKeys.Match(ownerId, circle, _identity.Servers.Where(s => s.Restricted));

    /// <summary>An own server gave the key of its circle: remembered, and the contacts get my card again (it names that server by the new identifier).</summary>
    private void OnOwnCircleKey(string url, string key)
    {
        var entry = _identity.Servers.FirstOrDefault(s => RelayPool.Normalize(s.Url) == url);
        if (entry == null || !CircleKeys.Learn(entry, key)) return;
        _identityStorage.Save(_identity);
        if (_connectStarted && entry.Restricted) _ = NotifyContactsAsync();   // a server not yet known as restricted is announced when its access is learnt, with this key
    }

    /// <summary>The servers to try to write to someone, in order (see <see cref="RoutePolicy"/>).</summary>
    private RoutePolicy.Route RouteFor(string peerId)
    {
        var contact = _confirmedContacts.FindById(peerId);
        var theirs = contact?.Servers ?? _groups.FindMember(peerId)?.Servers;
        if (theirs != null) theirs = _relay.Resolved(theirs, peerId).ToList();
        var ownOpen = _relay.OwnUrls.Where(u => !_identity.Servers.Any(s => s.Restricted && RelayPool.Normalize(s.Url) == u));
        return RoutePolicy.Choose(theirs, InRestricted, contact != null && OpenFallbackNow(contact), ownOpen, strictApplies: contact != null);
    }

    /// <summary>
    /// The open servers may be used for this contact now: both sides agreed and the restricted ones do not answer. "Ask": as soon as the user said yes, and
    /// that holds until the restricted servers answer again. "Auto": once they have been silent for the delay.
    /// </summary>
    private bool OpenFallbackNow(Contact contact)
    {
        if (contact.OpenFallback == OpenFallbackMode.Never || !contact.PeerOpenFallback) return false;
        if (!_privateDownSince.TryGetValue(contact.DiscoveryId, out var since)) return false;
        return contact.OpenFallback == OpenFallbackMode.Ask
            ? _fallbackApproved.ContainsKey(contact.DiscoveryId)
            : DateTime.UtcNow - since >= _options.OpenFallbackDelay;
    }

    /// <summary>Sends an envelope on the given servers, and keeps track of whether the restricted servers shared with the contact answer.</summary>
    private async Task<PoolSendResult> SendRoutedAsync(string? peerId, IReadOnlyList<string> candidates, Envelope envelope)
    {
        var result = await _relay.SendAsync(candidates, envelope);
        if (peerId != null) TrackRestrictedPath(peerId, result);
        return result;
    }

    private void TrackRestrictedPath(string peerId, PoolSendResult result)
    {
        var contact = _confirmedContacts.FindById(peerId);
        if (contact == null) return;
        var shared = RouteFor(peerId).Shared;
        if (shared.Count == 0) { ClearRoute(peerId); return; }

        if (result.DeliveredVia != null && shared.Contains(result.DeliveredVia))
        {
            var wasDown = _privateDownSince.ContainsKey(peerId) || _fellBack.ContainsKey(peerId);
            ClearRoute(peerId);
            if (wasDown) RaiseNotice($"[i] {contact.DisplayName}: back on the restricted server.");
            return;
        }
        if (result.Delivered)
        {
            if (_fellBack.TryAdd(peerId, true)) RaiseNotice($"[i] Sent to {contact.DisplayName} through an open server: the restricted server does not answer.");
            return;
        }

        // A restricted server that answers but where the contact is absent is not an outage: the contact is simply not there.
        if (result.ReachableUrls.Any(shared.Contains)) { _privateDownSince.TryRemove(peerId, out _); _fallbackProposed.TryRemove(peerId, out _); return; }

        _privateDownSince.GetOrAdd(peerId, DateTime.UtcNow);
        // "Ask": the question is the delay, so it comes at once, one time for the outage.
        if (contact.OpenFallback == OpenFallbackMode.Ask && contact.PeerOpenFallback && !_fallbackApproved.ContainsKey(peerId) && _fallbackProposed.TryAdd(peerId, true))
            OpenFallbackProposed?.Invoke(contact);
    }

    private void ClearRoute(string peerId)
    {
        _privateDownSince.TryRemove(peerId, out _);
        _fallbackApproved.TryRemove(peerId, out _);
        _fellBack.TryRemove(peerId, out _);
        _fallbackProposed.TryRemove(peerId, out _);
    }

    /// <summary>Where the servers used to reach a contact stand now.</summary>
    public RouteState RouteStateOf(string contactId)
    {
        if (RouteFor(contactId).Shared.Count == 0) return RouteState.Open;
        if (_fellBack.ContainsKey(contactId)) return RouteState.OpenFallback;
        return _privateDownSince.ContainsKey(contactId) ? RouteState.PrivateDown : RouteState.PrivateOnly;
    }

    /// <summary>What I allow for this contact when the restricted servers we share do not answer.</summary>
    public OpenFallbackMode OpenFallbackOf(string contactId) => _confirmedContacts.FindById(contactId)?.OpenFallback ?? OpenFallbackMode.Never;

    /// <summary>Whether this contact told me they allow it too (both sides are needed).</summary>
    public bool PeerAllowsOpenFallback(string contactId) => _confirmedContacts.FindById(contactId)?.PeerOpenFallback ?? false;

    /// <summary>Sets what I allow for this contact and tells them (a route_policy letter, through the restricted servers we share when there are).</summary>
    public async Task SetOpenFallbackAsync(string contactId, OpenFallbackMode mode)
    {
        var contact = _confirmedContacts.FindById(contactId) ?? throw new ClientException("Not a contact.", "contact_unknown");
        contact.OpenFallback = mode;
        _confirmedContacts.Upsert(contact);
        if (mode == OpenFallbackMode.Never) { _fallbackApproved.TryRemove(contactId, out _); _fallbackProposed.TryRemove(contactId, out _); }
        ContactUpdated?.Invoke(contact);
        await SendRoutePolicyAsync(contact);
        _ = FlushPendingMessagesAsync();
    }

    /// <summary>The user said yes to an open server for this contact: it is used without asking again until the restricted servers answer again.</summary>
    public void ApproveOpenFallback(string contactId)
    {
        _fallbackApproved[contactId] = true;
        _ = FlushPendingMessagesAsync();
    }

    private async Task SendRoutePolicyAsync(Contact contact)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new RoutePolicyLetter { OpenFallback = contact.OpenFallback != OpenFallbackMode.Never });
            var ts = DateTime.UtcNow.Ticks;
            var cipherMsg = MessageCrypto.Encrypt(_identity.DiscoveryId, contact.DiscoveryId, TypeRoutePolicy, payload, ts, Convert.FromBase64String(contact.EncryptionPublicKey), _identity.SigningPrivateKey);
            var envelope = new Envelope { From = _identity.DiscoveryId, To = contact.DiscoveryId, Crypto = 1, Msg = cipherMsg, Ts = ts };
            foreach (var old in _pendingMessages.All.Where(p => p.Type == TypeRoutePolicy && p.To == contact.DiscoveryId).ToList()) _pendingMessages.Remove(old.Id);   // only the last choice matters

            var result = await SendRoutedAsync(contact.DiscoveryId, CandidatesFor(contact.DiscoveryId), envelope);
            _pendingMessages.Add(new PendingMessage
            {
                Id = $"{_identity.DiscoveryId}:{ts}", To = contact.DiscoveryId, Type = TypeRoutePolicy, Text = payload, Ts = ts, CreatedAt = DateTime.UtcNow,
                SentAt = result.Delivered ? DateTime.UtcNow : null,
            });
        }
        catch (Exception ex)
        {
            RaiseNotice($"[!] Could not tell {contact.DisplayName} about the change: {ex.Message}");
        }
    }

    private void HandleIncomingRoutePolicy(string from, string payload)
    {
        RoutePolicyLetter? letter;
        try { letter = JsonSerializer.Deserialize<RoutePolicyLetter>(payload); }
        catch (JsonException) { letter = null; }
        var contact = _confirmedContacts.FindById(from);
        if (letter == null || contact == null) return;
        if (contact.PeerOpenFallback == letter.OpenFallback) return;
        contact.PeerOpenFallback = letter.OpenFallback;
        _confirmedContacts.Upsert(contact);
        if (!letter.OpenFallback) { _fallbackApproved.TryRemove(from, out _); _fellBack.TryRemove(from, out _); }
        ContactUpdated?.Invoke(contact);
    }

    /// <summary>An own server told who may sign up on it: remembered on the server's entry, and announced to the contacts when it changed (their cards list the restricted ones last).</summary>
    private void OnOwnServerAccess(string url, string? access)
    {
        var entry = _identity.Servers.FirstOrDefault(s => RelayPool.Normalize(s.Url) == url);
        if (entry == null || access == null) return;
        var restricted = access == "allowlist";
        if (entry.Restricted == restricted) return;
        entry.Restricted = restricted;
        _identityStorage.Save(_identity);
        if (_connectStarted) _ = NotifyContactsAsync();
    }
}
