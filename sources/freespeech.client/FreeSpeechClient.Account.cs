/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Backup;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Outbox;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client;

// Contacts, my profile, my servers, backup.
public sealed partial class FreeSpeechClient
{
    // ------------------------------------------------------------------
    // Contacts (§3)
    // ------------------------------------------------------------------

    public IReadOnlyList<Contact> Contacts => _confirmedContacts.All;

    public IReadOnlyCollection<KeyValuePair<string, string>> Aliases => _aliases.All;

    public Contact? FindContact(string nameOrId) => _confirmedContacts.FindById(ResolveContact(nameOrId));

    public Contact RequireContact(string nameOrId) =>
        FindContact(nameOrId) ?? throw new ClientException($"'{nameOrId}' is not a confirmed contact — exchange contact cards first ('export'/'import').");

    public void SetAlias(string name, string discoveryId) => _aliases.Set(name, discoveryId);

    /// <summary>Gives a contact a name only I see (two Thomas); null or empty goes back to their own nickname. Nothing is sent to them.</summary>
    public void RenameContact(Contact contact, string? localName)
    {
        localName = localName?.Trim();
        if (localName is { Length: > 64 }) throw new ClientException("Name too long (max 64 characters).", "profile_name_long");
        contact.LocalName = string.IsNullOrEmpty(localName) || localName == contact.Nickname ? null : localName;
        _confirmedContacts.Upsert(contact);
    }

    /// <summary>The number to compare with this contact (§3): the same on both phones unless someone swapped the keys at the exchange.</summary>
    public string SafetyNumberOf(Contact contact) => SafetyNumber.Compute(
        _identity.EncryptionPublicKey, _identity.SigningPublicKey,
        Convert.FromBase64String(contact.EncryptionPublicKey), Convert.FromBase64String(contact.SigningPublicKey));

    /// <summary>Records that I compared the safety number with this contact and it matched (or clears the mark).</summary>
    public void SetVerified(Contact contact, bool verified)
    {
        contact.VerifiedAt = verified ? DateTime.UtcNow : null;
        _confirmedContacts.Upsert(contact);
    }

    /// <summary>A ContactCard (base64) to hand over out of band; <paramref name="kind"/> is "manual" (valid 1 month) or "qr" (10 min).</summary>
    public string ExportCard(string kind, bool includePhoto = true)
    {
        if (kind != "manual" && kind != "qr") throw new ClientException("Usage: export [manual|qr]");
        return ContactExchangeService.SerializeCard(_contactExchange.ExportCard(kind, includePhoto));
    }

    /// <summary>Imports a card received out of band; the contact is confirmed once both sides are online. Returns the card's nickname.</summary>
    public async Task<string> ImportCardAsync(string base64)
    {
        var card = ContactExchangeService.DeserializeCard(base64) ?? throw new ClientException("Could not parse this ContactCard.");
        await _contactExchange.ImportCardAsync(card);
        return card.Nickname;
    }

    /// <summary>Manual contact requests awaiting my decision.</summary>
    public IReadOnlyList<ContactCard> PendingRequests => _contactExchange.PendingConfirmations.ToList();

    /// <summary>Accepts a pending manual contact request; returns the contact's id.</summary>
    public async Task<string> ConfirmAsync(string nameOrId)
    {
        var id = ResolveContact(nameOrId);
        await _contactExchange.ConfirmPendingAsync(id);
        return id;
    }

    // ------------------------------------------------------------------
    // My profile
    // ------------------------------------------------------------------

    /// <summary>Reads a picture (JPEG/PNG/WebP, at most 100 KiB) as base64. The content is sniffed, never the extension.</summary>
    public static string ReadPicture(string path)
    {
        path = path.Trim('"');
        if (!File.Exists(path)) throw new ClientException($"File not found: {path}");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > GroupProfileOp.MaxPhotoBytes)
            throw new ClientException($"Picture too large ({bytes.Length / 1024} KiB, max {GroupProfileOp.MaxPhotoBytes / 1024}). Resize it first.", "profile_picture_big", bytes.Length / 1024, GroupProfileOp.MaxPhotoBytes / 1024);
        var mime = AttachmentService.DetectMime(path, path);
        if (mime is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new ClientException($"Not a JPEG/PNG/WebP picture (looks like {mime}).", "profile_picture_type", mime);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Changes one profile field and saves; <see cref="PublishProfileAsync"/> then tells contacts and groups. Returns the number of contacts to notify.</summary>
    private int UpdateProfile(Action<UserIdentity> change)
    {
        change(_identity);
        _identityStorage.Save(_identity);
        return _confirmedContacts.All.Count;
    }

    public int SetNickname(string nickname)
    {
        nickname = nickname.Trim();
        if (nickname.Length > 64) throw new ClientException("Name too long (max 64 characters).", "profile_name_long");
        return UpdateProfile(i => i.Nickname = nickname);
    }

    /// <summary>My description; null clears it.</summary>
    public int SetDescription(string? text)
    {
        text = text?.Trim();
        if (text != null && text.Length > GroupProfileOp.MaxDescriptionLength)
            throw new ClientException($"Too long ({text.Length} characters, max {GroupProfileOp.MaxDescriptionLength}).", "profile_description_long", text.Length, GroupProfileOp.MaxDescriptionLength);
        return UpdateProfile(i => i.Description = text);
    }

    /// <summary>My picture from a file; null clears it.</summary>
    public int SetPhoto(string? path)
    {
        var photo = path == null ? null : ReadPicture(path);
        return UpdateProfile(i => i.Photo = photo);
    }

    /// <summary>Sends the profile to every confirmed contact (identity_update, §5) and to every group I am in (signed member profile, §7).</summary>
    public async Task PublishProfileAsync()
    {
        foreach (var contact in _confirmedContacts.All) await SendIdentityUpdateAsync(contact);
        await _groups.PublishMyProfileAsync(_identity.Nickname ?? _identity.DiscoveryId[..8], _identity.Photo, _identity.Description);
    }

    /// <summary>Sends our current ContactCard as an identity_update Letter (§5) to one contact; it goes through the outbox like a chat message (the newest card replaces one still waiting).</summary>
    private async Task SendIdentityUpdateAsync(Contact contact)
    {
        try
        {
            var payload = JsonSerializer.Serialize(_contactExchange.BuildCurrentCard());
            var ts = DateTime.UtcNow.Ticks;
            var cipherMsg = MessageCrypto.Encrypt(_identity.DiscoveryId, contact.DiscoveryId, "identity_update", payload, ts, Convert.FromBase64String(contact.EncryptionPublicKey), _identity.SigningPrivateKey);
            var envelope = new Envelope { From = _identity.DiscoveryId, To = contact.DiscoveryId, Crypto = 1, Msg = cipherMsg, Ts = ts };
            // The newest card replaces any update still waiting for this contact: only the last profile matters.
            foreach (var old in _pendingMessages.All.Where(p => p.Type == "identity_update" && p.To == contact.DiscoveryId).ToList()) _pendingMessages.Remove(old.Id);

            var result = await SendRoutedAsync(contact.DiscoveryId, CandidatesFor(contact.DiscoveryId), envelope);
            // Kept in the outbox like a chat message: sent again when they come back if they were offline, or if they do not confirm having it (see FreeSpeechClient.Ack.cs).
            _pendingMessages.Add(new PendingMessage
            {
                Id = $"{_identity.DiscoveryId}:{ts}", To = contact.DiscoveryId, Type = "identity_update", Text = payload, Ts = ts, CreatedAt = DateTime.UtcNow,
                SentAt = result.Delivered ? DateTime.UtcNow : null,
            });
            if (!result.Delivered) RaiseNotice($"[!] {contact.DisplayName} is offline right now — the profile update is queued and will be sent when they are back.");
        }
        catch (Exception ex)
        {
            RaiseNotice($"[!] Could not notify {contact.DisplayName} of the change: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // My servers (§8)
    // ------------------------------------------------------------------

    /// <summary>True when at least one of my servers is connected now.</summary>
    public bool AnyServerConnected => _relay.AnyOwnConnected;

    /// <summary>Tries again, at once, the servers of mine that are not connected (the periodic tick would wait 30 s). A phone that has just started often has no network yet.</summary>
    public Task ReconnectServersAsync() => _relay.MaintainAsync();

    public IReadOnlyList<ServerStatus> Servers =>
        _identity.Servers.OrderBy(s => s.Priority).Select(s => new ServerStatus(s, _relay.IsConnected(RelayPool.Normalize(s.Url)), _relay.RefusalOf(s.Url), _relay.AccessOf(s.Url))).ToList();

    /// <summary>
    /// Adds a server to the list this identity advertises AND stays registered on (N private + M public, no limit). Call <see cref="NotifyContactsAsync"/> afterwards.
    /// <paramref name="invitation"/> is the token the administrator of a private server gave (single use; not stored).
    /// </summary>
    public async Task<ServerStatus> AddServerAsync(string url, int? priority = null, string? invitation = null)
    {
        url = RelayPool.Normalize(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ClientException("URL must be http(s)://...", "server_url");
        if (_identity.Servers.Any(s => RelayPool.Normalize(s.Url) == url)) throw new ClientException("Already in the list.", "server_duplicate");

        var prio = priority ?? (_identity.Servers.Count == 0 ? 0 : _identity.Servers.Max(s => s.Priority) + 1);
        var entry = new ServerEntry { Url = url, Priority = prio };
        _identity.Servers.Add(entry);
        _identity.ServersInitial = false;
        _identity.RetiredServers.Remove(url);   // adding it back by hand lifts the "moved away" mark
        await _relay.AddOwnAsync(url, invitation);
        _identityStorage.Save(_identity);
        return new ServerStatus(entry, _relay.IsConnected(url), _relay.RefusalOf(url), _relay.AccessOf(url));
    }

    /// <summary>
    /// Asks the administrator of a private server (one of my servers that answered "private_server") to allow me: my name and a note go
    /// to the server (§10 "Access requests") — so the caller asks the user first. The status says what came back: "access_pending" = waiting.
    /// </summary>
    public async Task<ServerStatus> RequestServerAccessAsync(string url, string? note = null)
    {
        url = RelayPool.Normalize(url);
        var entry = _identity.Servers.FirstOrDefault(s => RelayPool.Normalize(s.Url) == url) ?? throw new ClientException("Not in the list.", "server_unknown");
        await _relay.RequestAccessAsync(url, _identity.Nickname ?? _identity.DiscoveryId[..8], note);
        return new ServerStatus(entry, _relay.IsConnected(url), _relay.RefusalOf(url), _relay.AccessOf(url));
    }

    /// <summary>
    /// Uses the invitation token an administrator gave for a private server that is already in my list and turned me away ("private_server"):
    /// the next registration carries it (single use, kept in memory only). The status says what came back.
    /// </summary>
    public async Task<ServerStatus> UseInvitationAsync(string url, string invitation)
    {
        url = RelayPool.Normalize(url);
        var entry = _identity.Servers.FirstOrDefault(s => RelayPool.Normalize(s.Url) == url) ?? throw new ClientException("Not in the list.", "server_unknown");
        if (string.IsNullOrWhiteSpace(invitation)) throw new ClientException("Invitation required.", "invitation_required");
        await _relay.AddOwnAsync(url, invitation);
        return new ServerStatus(entry, _relay.IsConnected(url), _relay.RefusalOf(url), _relay.AccessOf(url));
    }

    /// <summary>
    /// The verification code of an identity: the first 12 characters of its id, upper case, in groups of four ("3FA9-C1B2-7E40"). Whoever asks a private
    /// server for access reads it out to its administrator, who compares it with the one the server shows next to the request: that is how the
    /// administrator knows the request comes from the person on the phone. The same notation is used by the server's tools.
    /// </summary>
    public static string VerificationCode(string discoveryId)
    {
        var head = (discoveryId.Length >= 12 ? discoveryId[..12] : discoveryId).ToUpperInvariant();
        return string.Join('-', Enumerable.Range(0, (head.Length + 3) / 4).Select(i => head.Substring(i * 4, Math.Min(4, head.Length - i * 4))));
    }

    /// <summary>Removes a server. Call <see cref="NotifyContactsAsync"/> afterwards.</summary>
    public async Task<string> RemoveServerAsync(string url)
    {
        url = RelayPool.Normalize(url);
        var entry = _identity.Servers.FirstOrDefault(s => RelayPool.Normalize(s.Url) == url) ?? throw new ClientException("Not in the list.", "server_unknown");
        if (_identity.Servers.Count == 1) throw new ClientException("Cannot remove the last server.", "server_last");

        _identity.Servers.Remove(entry);
        _identity.ServersInitial = false;
        if (url != _cliServerUrl) await _relay.RemoveOwnAsync(url);
        _identityStorage.Save(_identity);
        return url;
    }

    private const int MaxMovesPerDay = 3;
    private readonly SemaphoreSlim _moveGate = new(1, 1);

    /// <summary>Raised after this identity moved to another server (old url, new url).</summary>
    public event Action<string, string>? ServerMoved;

    /// <summary>
    /// A server of ours suggests siblings that have room (§10 "Moving a client"). The server only advises: we move
    /// only from one of our own servers, to an https sibling we can register on (the new server checks our proof of
    /// identity itself), at most <see cref="MaxMovesPerDay"/> times a day so two full servers cannot bounce us forever.
    /// Then the contacts are told our new address (queued if we are offline).
    /// </summary>
    private async Task HandleRedirectAsync(string from, IReadOnlyList<string> candidates, string reason)
    {
        if (!await _moveGate.WaitAsync(0)) return;
        try
        {
            from = RelayPool.Normalize(from);
            if (!_relay.OwnUrls.Contains(from)) return;

            _identity.Moves.RemoveAll(t => t < DateTime.UtcNow.AddDays(-1));
            if (_identity.Moves.Count >= MaxMovesPerDay) { RaiseNotice("[!] Server asked us to move, but we moved too often today: staying."); return; }

            var fromIsHttp = from.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            foreach (var raw in candidates.Take(3))
            {
                var target = RelayPool.Normalize(raw);
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(fromIsHttp && uri.Scheme == "http"))) continue;
                if (target == from || _relay.OwnUrls.Contains(target)) continue;

                await _relay.AddOwnAsync(target);
                if (!_relay.IsConnected(target)) { await _relay.RemoveOwnAsync(target); continue; }

                var old = _identity.Servers.FirstOrDefault(s => RelayPool.Normalize(s.Url) == from);
                _identity.Servers.Remove(old!);
                _identity.Servers.RemoveAll(s => RelayPool.Normalize(s.Url) == target);
                _identity.Servers.Add(new ServerEntry { Url = target, Priority = old?.Priority ?? 0 });
                _identity.ServersInitial = false;
                if (!_identity.RetiredServers.Contains(from)) _identity.RetiredServers.Add(from);
                _identity.RetiredServers.Remove(target);
                _identity.Moves.Add(DateTime.UtcNow);
                await _relay.RemoveOwnAsync(from);
                _identityStorage.Save(_identity);

                RaiseNotice($"[i] Moved from {from} to {target} ({reason}).");
                ServerMoved?.Invoke(from, target);
                await NotifyContactsAsync();
                return;
            }
        }
        catch (Exception ex) { RaiseNotice($"[!] Could not move to another server: {ex.Message}"); }
        finally { _moveGate.Release(); }
    }

    /// <summary>Sends every confirmed contact an identity_update so their ContactCard reflects the new server list (§8).</summary>
    public async Task<int> NotifyContactsAsync()
    {
        var contacts = _confirmedContacts.All.ToList();
        foreach (var contact in contacts) await SendIdentityUpdateAsync(contact);
        return contacts.Count;
    }

    // ------------------------------------------------------------------
    // Backup (§2)
    // ------------------------------------------------------------------

    /// <summary>A new name each time, with the local date and time ("freespeech-backup-20261004-153012.enc"): several backups are told apart at a glance.</summary>
    public string DefaultBackupPath => Path.Combine(_options.ProfileDir, $"freespeech-backup-{DateTime.Now:yyyyMMdd-HHmmss}.enc");

    /// <summary>
    /// Writes an encrypted backup (contacts, aliases, history, groups and — per the mode — attachment files),
    /// with a key derived from our own identity: only readable by re-deriving the same identity, which is the
    /// new-phone transfer this exists for. Mode <c>Recent</c> = files of the last N months. Files left out are
    /// not lost from the conversation: a received one stays downloadable; a SENT one is gone unless kept.
    /// </summary>
    public BackupSummary CreateBackup(string? path, AttachmentBackupMode mode, int months = 6) =>
        _backup.Create(path ?? DefaultBackupPath, mode, months);

    /// <summary>
    /// Merges a backup (streaming archive or legacy base64 blob) into this profile; repeating it is harmless. <paramref name="restoreProfile"/> also puts back
    /// the name, description and picture saved in the backup: used when the identity was just recreated from its phrases and is still blank.
    /// Left off on an identity already in use, so an older backup does not overwrite a newer profile.
    /// </summary>
    public RestoreSummary RestoreBackup(string path, bool restoreProfile = false)
    {
        path = path.Trim('"');
        if (!File.Exists(path)) throw new ClientException($"File not found: {path}");
        var summary = _backup.Restore(path)
            ?? throw new ClientException("Could not decrypt this backup — it was not made by this identity, or the file is corrupted/truncated.", "backup_decrypt");
        if (summary.Servers is { Count: > 0 } savedServers && _identity.ServersInitial)
            Task.Run(() => AdoptBackupServersAsync(savedServers)).GetAwaiter().GetResult();   // callers are already off the UI thread
        if (restoreProfile && summary.Profile is { } saved)
            UpdateProfile(i =>
            {
                if (!string.IsNullOrWhiteSpace(saved.Nickname)) i.Nickname = saved.Nickname.Trim();
                if (!string.IsNullOrWhiteSpace(saved.Description)) i.Description = saved.Description;
                if (!string.IsNullOrEmpty(saved.Photo)) i.Photo = saved.Photo;
            });
        return summary;
    }
}
