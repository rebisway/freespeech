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
using FreeSpeech.Client.History;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Outbox;
using FreeSpeech.Client.Relay;
using FreeSpeech.Client.Storage;

namespace FreeSpeech.Client;

/// <summary>
/// The client API: everything a FreeSpeech front-end (console, mobile, desktop) does, with no UI.
/// A front-end calls the methods, shows their results, and listens to the events; it never
/// touches the relay, the crypto or the stores directly.
///
/// Lifecycle: <see cref="TryLoadIdentity"/> (or <see cref="CreateIdentity"/>, which also unlocks a locked profile) → <see cref="Initialize"/>
/// → <see cref="ConnectAsync"/> → use → <see cref="DisposeAsync"/>.
/// User errors are thrown as <see cref="ClientException"/>. Events may fire on any thread.
/// </summary>
public sealed partial class FreeSpeechClient : IGroupTransport, IAsyncDisposable
{
    private static readonly TimeSpan BackgroundTickInterval = TimeSpan.FromSeconds(30);

    private readonly ClientOptions _options;
    private readonly string _cliServerUrl;
    private readonly IKeyCache _keyCache;
    private ClientDatabase _db = null!;
    private IdentityStorage _identityStorage = null!;
    private HistoryStore _history = null!;
    private ContactAliasStore _aliases = null!;
    private OtcEntryStore _otcEntries = null!;
    private PendingContactStore _pendingContacts = null!;
    private ContactStore _confirmedContacts = null!;
    private PendingMessageStore _pendingMessages = null!;
    private readonly BackgroundScheduler _scheduler = new(BackgroundTickInterval);
    private AttachmentService _attachments = null!;
    private Archive.ArchiveService _archive = null!;
    private BackupService _backup = null!;
    private GroupService _groups = null!;
    private RelayPool _relay = null!;
    private UserIdentity _identity = null!;
    private ContactExchangeService _contactExchange = null!;

    public FreeSpeechClient(ClientOptions options)
    {
        _options = options;
        _keyCache = options.KeyCache ?? new PlainFileKeyCache(options.ProfileDir);
        _cliServerUrl = RelayPool.Normalize(options.ServerUrl);
    }

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    /// <summary>A message or file offer arrived (1-to-1, group, or synced from another device of ours).</summary>
    public event Action<ChatMessage>? MessageReceived;

    /// <summary>A contact asked to connect (manual exchange): accept with <see cref="ConfirmAsync"/>.</summary>
    public event Action<ContactCard>? ContactRequested;

    public event Action<Contact>? ContactConfirmed;

    /// <summary>A contact changed its name, picture, description or servers.</summary>
    public event Action<Contact>? ContactUpdated;

    /// <summary>A server connection dropped (<c>false</c>) or came back (<c>true</c>); reconnection is automatic.</summary>
    public event Action<string, bool>? ServerConnectionChanged;

    /// <summary>Anything else worth telling the user (attachment progress, rejected input, group changes, non-fatal errors).</summary>
    public event Action<string>? Notice;

    internal void RaiseNotice(string text) => Notice?.Invoke(text);

    /// <summary>A server refused to carry what I send because of its volume limits: (code "quota_exceeded" or "rate_limited", seconds until sending resumes). Sending continues by itself after that.</summary>
    public event Action<string, int>? SendLimited;

    /// <summary>Bytes I may still send today across my servers, as of the last send; null when no server has a quota or none has answered yet.</summary>
    public long? SendQuotaLeft => _relay.QuotaLeft;

    // ------------------------------------------------------------------
    // Identity and lifecycle
    // ------------------------------------------------------------------

    public UserIdentity Identity => _identity;

    public string CliServerUrl => _cliServerUrl;

    public string ProfileDir => _options.ProfileDir;

    /// <summary>The profile has a database but no cached key: the recovery phrases must be entered again (<see cref="CreateIdentity"/>).</summary>
    public bool NeedsPhrases { get; private set; }

    private void OpenStores(ClientDatabase db)
    {
        _db = db;
        _identityStorage = new IdentityStorage(db);
        _history = new HistoryStore(db);
        _aliases = new ContactAliasStore(db);
        _otcEntries = new OtcEntryStore(db);
        _pendingContacts = new PendingContactStore(db);
        _archivedContacts = new ArchivedContactStore(db);
        _confirmedContacts = new ContactStore(db);
        _pendingMessages = new PendingMessageStore(db);
        _pins = new RevocationPinStore(db);
    }

    /// <summary>
    /// Opens the profile with the cached key and loads its identity; false when there is none yet, or when the
    /// key is missing (<see cref="NeedsPhrases"/>: the database exists but cannot be opened without the phrases).
    /// </summary>
    public bool TryLoadIdentity()
    {
        var exists = ClientDatabase.Exists(_options.ProfileDir);
        var key = _keyCache.Load();
        if (key == null)
        {
            NeedsPhrases = exists;
            return false;
        }

        try { OpenStores(ClientDatabase.Open(_options.ProfileDir, key)); }
        catch (ClientException)
        {
            NeedsPhrases = true; // stale or damaged cache: the phrases regenerate the key
            return false;
        }

        NeedsPhrases = false;
        var loaded = _identityStorage.Load();
        if (loaded == null)
        {
            DiscardEmptyDatabase();   // no identity in it: nothing is lost, and a key cached for another identity must not outlive it
            return false;
        }
        _identity = loaded;
        return true;
    }

    /// <summary>Closes the profile database and deletes its files. Only for a database that holds no identity (so nothing else).</summary>
    private void DiscardEmptyDatabase()
    {
        _db.Dispose();
        _db = null!;
        var path = ClientDatabase.PathFor(_options.ProfileDir);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }

    public static bool IsPhraseValid(string phrase) => IdentityService.IsPhraseValid(phrase);

    /// <summary>
    /// Before creating anything: can this backup be opened with an identity derived from these phrases? A wrong
    /// phrase would otherwise create a different, empty identity. An old-format backup cannot be checked up front (true).
    /// </summary>
    public static bool BackupMatchesPhrases(string backupPath, string phrase1, string phrase2, string phrase3)
    {
        if (!Backup.BackupCrypto.IsStreamFormat(backupPath)) return true;
        var identity = IdentityService.Derive(phrase1, phrase2, phrase3, null);
        using var input = File.OpenRead(backupPath);
        return Backup.BackupCrypto.TryDecryptStream(input, Stream.Null, identity.EncryptionPrivateKey);
    }

    /// <summary>
    /// Creates the identity from three recovery phrases (there is no recovery without them) — or, on a profile
    /// that already exists but is locked (<see cref="NeedsPhrases"/>), unlocks it: the phrases give back the
    /// database key, which is cached for next time. Throws <see cref="ArgumentException"/> for a bad phrase and
    /// <see cref="ClientException"/> when they do not match an existing profile.
    /// </summary>
    public UserIdentity CreateIdentity(string phrase1, string phrase2, string phrase3, string nickname)
    {
        var derived = IdentityService.Derive(phrase1, phrase2, phrase3, nickname);
        var key = IdentityService.DeriveDatabaseKey(phrase1, phrase2, phrase3);

        OpenStores(ClientDatabase.Open(_options.ProfileDir, key));
        _keyCache.Store(key);
        NeedsPhrases = false;

        var existing = _identityStorage.Load();
        if (existing != null && existing.DiscoveryId != derived.DiscoveryId)
            throw new ClientException("These phrases do not match the identity of this profile.", "phrases_mismatch");
        _identity = existing ?? derived;
        if (existing == null)
        {
            _identity.ServersInitial = _options.Directories.Count > 0 || _options.FallbackServers.Count > 0;
            _identityStorage.Save(_identity);
        }
        return _identity;
    }

    /// <summary>Builds the services from the loaded identity. No network yet.</summary>
    public void Initialize()
    {
        if (_identity == null) throw new ClientException("No identity: call TryLoadIdentity or CreateIdentity first.");

        // Older identities only have PublicServer (or nothing): seed the §8 server list from it, else from the configured URL.
        if (_identity.Servers.Count == 0)
        {
            _identity.Servers.Add(new ServerEntry { Url = RelayPool.Normalize(_identity.PublicServer ?? _cliServerUrl), Priority = 0 });
            _identityStorage.Save(_identity);
        }

        _relay = new RelayPool(_identity.DiscoveryId, new RegisterAuth(_identity.EncryptionPublicKey, _identity.SigningPublicKey, _identity.SigningPrivateKey));
        _attachments = new AttachmentService(new AttachmentStore(_options.ProfileDir, _db), SendLetterOnceAsync, RaiseNotice, chunkDelay: _options.ChunkDelay,
            authorize: AuthorizeAttachmentRequest, holders: HoldersOf,
            isGroupPeer: (rec, from) => rec.GroupId != null && _groups.IsMember(rec.GroupId, from) && from != _identity.DiscoveryId,
            canTransfer: FileTransfer.Allows);
        _archive = new Archive.ArchiveService(_identity.EncryptionPrivateKey, _attachments);
        _groups = new GroupService(_identity, new GroupStore(_db), this, id => _confirmedContacts.FindById(id) != null, RaiseNotice, id => _relay.IsProvisioner(id))
        {
            RequireInvitationApproval = true,   // being added to a group is the user's decision, not the inviter's
        };
        _groups.OnMessage += HandleGroupMessage;
        _groups.PinsChanged += rec => PinsChanged?.Invoke(GroupConversation(rec));
        _groups.PinRevocation = (id, key) => _pins.TryPin(id, key);
        _backup = new BackupService(_identity.EncryptionPrivateKey, _confirmedContacts, _aliases, _history, _attachments.Store, _groups.Store,
            () => new BackupProfile { Nickname = _identity.Nickname, Description = _identity.Description, Photo = _identity.Photo }, _pins,
            () => _identity.Servers.OrderBy(s => s.Priority).Select(s => RelayPool.Normalize(s.Url)).ToList());
        _contactExchange = new ContactExchangeService(_identity, _relay, _otcEntries, _pendingContacts, _confirmedContacts, (id, key) => _pins.TryPin(id, key));
        _contactExchange.OnContactRequestReceived += card => ContactRequested?.Invoke(card);
        _contactExchange.OnContactConfirmed += contact =>
        {
            _archivedContacts.Remove(contact.DiscoveryId);   // added again: the archived conversation is back in the list
            ContactConfirmed?.Invoke(contact);
            _ = AdoptFirstContactServersAsync(contact);
        };
    }

    /// <summary>Connects every own server (unreachable ones are retried automatically) and starts the background tick.</summary>
    public async Task<IReadOnlyList<ServerStatus>> ConnectAsync()
    {
        _relay.OnMessage += HandleIncomingEnvelope;
        _relay.OnServerAccess += OnOwnServerAccess;
        _relay.OnDisconnected += url => ServerConnectionChanged?.Invoke(url, false);
        _relay.OnReconnected += url => ServerConnectionChanged?.Invoke(url, true);
        _relay.OnAccessGranted += url => RaiseNotice($"[+] The administrator of {url} accepted your access request: you are connected.");
        // A contact we owe a message, or a file we are waiting for, just woke up: act now, not at the next tick.
        // A letter the relay called delivered may have gone into a connection that was already dead: since the contact is back, it is sent again now, not after its backoff.
        _relay.OnPeerOnline += peer => { _ = ResendUnconfirmedAndFlushAsync(peer); _ = _attachments.ResumeForPeerAsync(peer); };
        // A letter sent again because my confirmation was lost arrives a second time: it is not handled again, but it is confirmed again.
        _relay.OnDuplicate += env => { if (env.Crypto == 1 && env.To == _identity.DiscoveryId && _confirmedContacts.FindById(env.From) != null) QueueAck(env.From, env.Ts); };

        _relay.OnRedirect += (from, servers, reason) => _ = HandleRedirectAsync(from, servers, reason);
        _relay.OnSendLimited += (_, code, seconds) => SendLimited?.Invoke(code, seconds);

        await ChooseFirstServersAsync();   // a new identity takes the relays of the directories, else a built-in fallback
        _connectStarted = true;

        // The configured default is added unless this identity was moved away from it (§10 "Moving a client").
        var urls = _identity.Servers.Select(s => RelayPool.Normalize(s.Url))
            .Concat(_identity.RetiredServers.Contains(RelayPool.Normalize(_cliServerUrl)) ? [] : [_cliServerUrl]).Distinct().ToList();
        var statuses = new List<ServerStatus>();
        foreach (var url in urls)
        {
            await _relay.AddOwnAsync(url);
            statuses.Add(new ServerStatus(new ServerEntry { Url = url }, _relay.IsConnected(url)));
        }

        // Single background tick standing in for the mobile OS scheduler (§9): reconnects the relay if it
        // dropped (first, the other ticks need it), retries pending contact requests, flushes the message
        // outbox (the relay never stores anything for us), and runs the p2p catch-up and file-transfer retries.
        _scheduler.OnTick(() => _relay.MaintainAsync());
        _scheduler.OnTick(_contactExchange.RetryAllPendingAsync);
        _scheduler.OnTick(() => FlushPendingMessagesAsync(fromTick: true));
        _scheduler.OnTick(_groups.SyncAllAsync);
        _scheduler.OnTick(_attachments.ResumeStalledAsync);
        _scheduler.OnTick(RepeatDeviceSyncAsync);
        _scheduler.Start();

        // The outbox goes out now, not at the first tick (30 s later): a sender woken by push for a contact who is already online gets no peer_online,
        // and that contact's own wake-up listens for a few seconds only.
        _ = Task.Run(async () =>
        {
            try { await FlushPendingMessagesAsync(); }
            catch (Exception ex) { ClientLog.Warn("outbox", $"flush at connection failed: {ex.GetType().Name} {ex.Message}"); }
        });
        return statuses;
    }

    public async ValueTask DisposeAsync()
    {
        await _scheduler.DisposeAsync();
        if (_relay != null) await _relay.DisposeAsync();
        _db?.Dispose();
    }

    // ------------------------------------------------------------------
    // Incoming traffic
    // ------------------------------------------------------------------

    /// <summary>Who may fetch a file from us: in 1-to-1 the contact it was sent to; in a group any current member (what we hold of the file, whole or in part, is decided by the service: every member who downloads it becomes a source).</summary>
    private bool AuthorizeAttachmentRequest(AttachmentRecord rec, string from) =>
        from == _identity.DiscoveryId ? ServedToMyDevice(rec)   // another device of mine: only what its owner agreed to give it (device mirror)
        : rec.GroupId == null
            ? AttachmentService.ContactOnly(rec, from)
            : from != _identity.DiscoveryId && _groups.IsMember(rec.GroupId, from);

    /// <summary>Whom to ask for a received file: its author first, then the other members (not ourselves, not blacklisted).</summary>
    private IReadOnlyList<string> HoldersOf(AttachmentRecord rec)
    {
        if (rec.FromMyDevice) return [_identity.DiscoveryId];   // a file of the device mirror: it comes from my other device
        if (rec.GroupId == null) return [rec.ContactId];
        var group = _groups.Store.Find(rec.GroupId);
        if (group == null) return [rec.ContactId];

        var banned = _groups.Bans(group);
        return _groups.Members(group).Select(m => m.DiscoveryId)
            .Where(id => id != _identity.DiscoveryId && !banned.Contains(id))
            .OrderBy(id => id == rec.ContactId ? 0 : 1)
            .ToList();
    }

    /// <summary>
    /// Handles a "message" push from the relay. crypto=0 is OTC contact exchange (§3), never recorded in
    /// the history; crypto=2 is a group message (§7); everything else is a normal message. The relay
    /// callback is synchronous, so async work is fired without blocking the receive loop and any failure
    /// is surfaced as a notice.
    /// </summary>
    private void HandleIncomingEnvelope(Envelope envelope)
    {
        if (envelope.Crypto == 0)
        {
            _ = HandleIncomingOtcEnvelopeSafeAsync(envelope);
            return;
        }

        if (envelope.Crypto == 2) // sealed with the group key, the tag says which
        {
            _ = _groups.OnGroupEnvelopeAsync(envelope.From, envelope.Msg, envelope.Ts);
            return;
        }

        HandleIncomingChatEnvelope(envelope);
    }

    private async Task HandleIncomingOtcEnvelopeSafeAsync(Envelope envelope)
    {
        try { await _contactExchange.HandleIncomingOtcEnvelopeAsync(envelope.From, envelope.Msg); }
        catch (Exception ex) { RaiseNotice($"[!] Contact exchange error: {ex.Message}"); }
    }

    /// <summary>
    /// Decrypts and verifies an incoming crypto=1 envelope (§5). Two shapes share this path: a Letter from a
    /// contact (From=them, To=me), and a self-sync copy of a "message" (From=To=me — see
    /// <see cref="SendSelfSyncCopyAsync"/>, real recipient carried as <c>orig_to</c> inside the encrypted
    /// payload). The Letter's own <c>type</c> then decides how the payload is interpreted.
    /// </summary>
    /// <summary>The signing key of the sender of a group invitation, read from the member list of the invitation itself (the member's id is derived from its keys), or null if it is not one.</summary>
    private byte[]? GroupInviterKey(Envelope envelope)
    {
        if (MessageCrypto.PeekUnverified(envelope.Msg, _identity.EncryptionPrivateKey) is not ("group_state", var payload)) return null;
        try
        {
            var letter = JsonSerializer.Deserialize<GroupStateLetter>(payload);
            var data = letter == null ? null : JsonSerializer.Deserialize<GroupStateData>(letter.State);
            var signer = data?.Members.FirstOrDefault(m => m.DiscoveryId == envelope.From);
            return signer != null && GroupCrypto.MemberIdMatchesKeys(signer) ? Convert.FromBase64String(signer.SigningPublicKey) : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    private void HandleIncomingChatEnvelope(Envelope envelope)
    {
        var isSelfCopy = envelope.From == _identity.DiscoveryId && envelope.To == _identity.DiscoveryId;

        byte[] signingPubKey;
        var fromGroupMemberOnly = false;
        var fromAdministratorOnly = false;
        if (isSelfCopy)
        {
            signingPubKey = _identity.SigningPublicKey;
        }
        else
        {
            var senderContact = _confirmedContacts.FindById(envelope.From);
            if (senderContact != null)
            {
                signingPubKey = Convert.FromBase64String(senderContact.SigningPublicKey);
            }
            else if (_groups.FindMember(envelope.From) is { } groupMember)
            {
                // Not a contact, but a member of one of our groups: its keys come from the signed GroupState (§7).
                signingPubKey = Convert.FromBase64String(groupMember.SigningPublicKey);
                fromGroupMemberOnly = true;
            }
            else if (_relay.FindProvisioner(envelope.From) is { } administrator)
            {
                // Not a contact, but an administrator tool my private server names (keys checked against the id): it may invite me to a group, nothing else.
                signingPubKey = Convert.FromBase64String(administrator.SignKey);
                fromGroupMemberOnly = true;
                fromAdministratorOnly = true;
            }
            else if (GroupInviterKey(envelope) is { } inviterKey)
            {
                // Not a contact, not yet in a group with me: a group invitation (§7) carries its signer's keys, which the signer's id certifies. The signature is checked below
                // as for any letter; whether a stranger may invite me (a sponsor I know vouches) is decided when the state is applied.
                signingPubKey = inviterKey;
                fromGroupMemberOnly = true;
            }
            else
            {
                RaiseNotice($"[!] Message from unknown contact {envelope.From} — cannot verify, dropped.");
                return;
            }
        }

        var decrypted = MessageCrypto.TryDecrypt(envelope.From, envelope.To, envelope.Msg, envelope.Ts, _identity.EncryptionPrivateKey, signingPubKey);
        if (decrypted == null)
        {
            RaiseNotice($"[!] Could not decrypt message from {envelope.From} — dropped.");
            return;
        }

        var (type, payload, origTo, signatureValid) = decrypted.Value;
        if (!signatureValid)
        {
            RaiseNotice($"[!] Message from {envelope.From} has an invalid signature — dropped.");
            return;
        }

        // An administrator tool that is not (yet) a group peer: its invitation to a group, and only that.
        if (fromAdministratorOnly && type != "group_state") return;

        // A letter of a contact that is kept is confirmed to it (see FreeSpeechClient.Ack.cs).
        if (!isSelfCopy && !fromGroupMemberOnly && AckTypes.Contains(type)) QueueAck(envelope.From, envelope.Ts);

        // Group protocol (§7): allowed from any member of a shared group; everything else needs a real contact.
        if (GroupService.IsGroupLetter(type))
        {
            if (!isSelfCopy) _ = _groups.OnLetterAsync(envelope.From, type, payload);
            return;
        }

        // A group member who is not a contact may still fetch/serve group attachments (§7); nothing else.
        // A migration notice may come from a group peer who is not a contact: only a moderator of a shared group acts on it (see HandleMoved).
        if (type == TypeMoved)
        {
            if (!isSelfCopy) _ = HandleMovedSafeAsync(envelope.From, payload, fromContact: !fromGroupMemberOnly);
            return;
        }

        if (fromGroupMemberOnly && type is not ("attachment_request" or "attachment_chunk" or "attachment_have"))
        {
            RaiseNotice($"[!] '{type}' from {envelope.From}, who is a group member but not a contact — ignored.");
            return;
        }

        if (type == "identity_update")
        {
            HandleIncomingIdentityUpdate(envelope.From, payload);
            return;
        }
        if (type == TypeRoutePolicy)
        {
            HandleIncomingRoutePolicy(envelope.From, payload);
            return;
        }

        // Another device of my identity asks for my data, or answers my request: only from myself (the sync letters are signed and sealed with my own keys).
        if (type is TypeSyncRequest or TypeSyncData)
        {
            if (isSelfCopy) { if (type == TypeSyncRequest) HandleDeviceSyncRequest(payload); else HandleDeviceSyncData(payload); }
            return;
        }

        // A contact confirms letters of mine that it kept: they leave my outbox.
        if (type == TypeAck)
        {
            if (!isSelfCopy) HandleAck(envelope.From, payload);
            return;
        }

        // A wipe order only comes from myself (another device of this identity); the revocation signature inside is what it is worth.
        if (type == TypeWipe)
        {
            if (isSelfCopy) HandleWipe(payload);
            return;
        }

        // Attachment protocol (§6) — only from a real contact, never a self-copy.
        if (type is "attachment" or "attachment_request" or "attachment_chunk" or "attachment_have")
        {
            if (isSelfCopy && type is not ("attachment_request" or "attachment_chunk")) return;   // my other device asking for, or sending, a file of the mirror
            switch (type)
            {
                case "attachment": HandleIncomingAttachmentOffer(envelope, payload); break;
                case "attachment_request": _attachments.OnRequest(envelope.From, payload); break;
                case "attachment_have": _attachments.OnHave(envelope.From, payload); break;
                default: _attachments.OnChunk(envelope.From, payload); break;
            }
            return;
        }

        // An earlier message was edited, deleted for everyone, or lost its file — by its author only.
        if (type is TypeEdit or TypeRetract or TypeFileRetract)
        {
            if (!isSelfCopy) ApplyRemoteChange(new Conversation(envelope.From, envelope.From, null), envelope.From, type, payload, envelope.Ts);
            return;
        }

        if (type == TypeReaction)
        {
            if (!isSelfCopy) ApplyRemoteReaction(new Conversation(envelope.From, envelope.From, null), envelope.From, payload, envelope.Ts);
            return;
        }

        if (type == TypeTyping)
        {
            if (!isSelfCopy) HandleTyping(envelope.From, payload);
            return;
        }

        if (type == TypeReadReceipt)
        {
            if (!isSelfCopy) HandleReadReceipt(envelope.From, payload);
            return;
        }

        // A letter type this version does not know (a newer client): dropped in silence, so a type added later never disturbs
        // the older versions still in use.
        if (type != "message") return;

        if (payload.Length > MaxIncomingTextLength)
        {
            RaiseNotice($"[!] A message of {payload.Length} characters from {envelope.From} is over the limit — dropped.");
            return;
        }

        var messageId = $"{envelope.From}:{envelope.Ts}";
        var peer = isSelfCopy ? origTo ?? envelope.To : envelope.From;
        var direction = isSelfCopy ? MessageDirection.Sent : MessageDirection.Received;

        // The reply index is computed before appending, like the history it refers to.
        var replyIndex = ReplyIndexOf(peer, envelope.ReplyTo);
        if (!AppendHistorySafe(peer, messageId, envelope.Ts, direction, payload, envelope.ReplyTo)) return;   // a duplicate is not announced again

        // Stored text, not the payload: a change that arrived earlier (edit) is already applied; a message that is
        // gone (deleted here, or already deleted by its author) is not announced.
        var stored = _history.Find(peer, messageId);
        if (stored == null || stored.State != MessageState.Normal) return;
        MessageReceived?.Invoke(new ChatMessage(
            new Conversation(peer, peer, null), envelope.From, _confirmedContacts.FindById(envelope.From)?.DisplayName ?? envelope.From,
            Mine: isSelfCopy, FromOtherDevice: isSelfCopy, IsFile: false, stored.Text, envelope.ReplyTo, replyIndex,
            HistoryIndexOf(peer, messageId)));
    }

    private void HandleIncomingAttachmentOffer(Envelope envelope, string payload)
    {
        var rec = _attachments.OnOffer(envelope.From, envelope.Ts, payload);
        if (rec == null) return;

        var text = $"[file] {rec.Name} ({rec.Size} bytes, {rec.Mime})";
        var replyIndex = ReplyIndexOf(envelope.From, envelope.ReplyTo);
        if (!AppendHistorySafe(envelope.From, rec.Ref, envelope.Ts, MessageDirection.Received, text, envelope.ReplyTo)) return;
        if (!StillShown(envelope.From, rec.Ref)) return;
        MessageReceived?.Invoke(new ChatMessage(
            new Conversation(envelope.From, envelope.From, null), envelope.From, _confirmedContacts.FindById(envelope.From)?.DisplayName ?? envelope.From,
            Mine: false, FromOtherDevice: false, IsFile: true, text, envelope.ReplyTo, replyIndex,
            HistoryIndexOf(envelope.From, rec.Ref)));
    }

    /// <summary>
    /// Applies an incoming "identity_update" Letter (§5): nickname/photo/description/servers may change
    /// freely, but the crypto keys are immutable — a mismatch means a stale/forged card or a key change we
    /// cannot safely trust automatically, so the whole update is rejected.
    /// </summary>
    private void HandleIncomingIdentityUpdate(string fromDiscoveryId, string payload)
    {
        ContactCard? card;
        try { card = JsonSerializer.Deserialize<ContactCard>(payload); }
        catch (JsonException) { card = null; }

        if (card == null || card.DiscoveryId != fromDiscoveryId)
        {
            RaiseNotice($"[!] Malformed identity_update from {fromDiscoveryId} — dropped.");
            return;
        }

        var existing = _confirmedContacts.FindById(fromDiscoveryId);
        if (existing == null)
        {
            RaiseNotice($"[!] identity_update from unconfirmed contact {fromDiscoveryId} — dropped.");
            return;
        }

        if (card.EncryptionPublicKey != existing.EncryptionPublicKey || card.SigningPublicKey != existing.SigningPublicKey)
        {
            RaiseNotice($"[!] identity_update from {fromDiscoveryId} changes crypto keys — rejected (possible impersonation).");
            return;
        }

        // The first revocation key I see for this contact is pinned; a different one later is refused (the rest of the update still applies).
        if (!string.IsNullOrEmpty(card.RevocationPublicKey) && !_pins.TryPin(fromDiscoveryId, card.RevocationPublicKey))
            RaiseNotice($"[!] identity_update from {fromDiscoveryId} announces another revocation key than the pinned one — ignored.");

        existing.Nickname = card.Nickname;
        existing.Photo = card.Photo;
        existing.Description = card.Description;
        existing.Servers = card.Servers;
        _confirmedContacts.Upsert(existing);
        ContactUpdated?.Invoke(existing);
    }

    /// <summary>
    /// After an offer was stored: is it still to be shown? Not if I deleted it here, or its author already deleted it or its
    /// file (the change arrived first): then the file record is emptied too.
    /// </summary>
    private bool StillShown(string key, string reference)
    {
        var stored = _history.Find(key, reference);
        if (stored != null && stored.State is MessageState.Normal or MessageState.FileDeleted) return true;
        _attachments.RemoveContent(reference);
        return false;
    }

    private void HandleGroupMessage(string groupId, string from, long ts, string type, string payload, string? replyTo)
    {
        var rec = _groups.Store.Find(groupId);
        if (rec == null) return;
        if (type is TypeEdit or TypeRetract or TypeFileRetract)
        {
            ApplyRemoteChange(GroupConversation(rec), from, type, payload, ts);
            return;
        }
        if (type == TypeReaction)
        {
            ApplyRemoteReaction(GroupConversation(rec), from, payload, ts);
            return;
        }
        var nick = _groups.Members(rec).FirstOrDefault(m => m.DiscoveryId == from)?.Nickname ?? from[..8];
        var mine = from == _identity.DiscoveryId;
        var text = payload;

        if (type == "attachment")
        {
            var file = _attachments.OnOffer(from, ts, payload, groupId);
            if (file == null) return;
            text = $"[file] {file.Name} ({file.Size} bytes, {file.Mime})";
        }
        else if (type != "message") return;
        else if (text.Length > MaxIncomingTextLength) { RaiseNotice($"[!] A group message of {text.Length} characters is over the limit — dropped."); return; }

        var key = $"group:{groupId}";
        var id = $"{from}:{ts}";
        var replyIndex = ReplyIndexOf(key, replyTo);
        if (!AppendHistorySafe(key, id, ts, mine ? MessageDirection.Sent : MessageDirection.Received, text, replyTo, from)) return;
        var storedMsg = _history.Find(key, id);
        if (storedMsg == null || storedMsg.State is not (MessageState.Normal or MessageState.FileDeleted))
        {
            if (type == "attachment") _attachments.RemoveContent(id);
            return;
        }
        MessageReceived?.Invoke(new ChatMessage(
            new Conversation(key, null, rec), from, nick, mine, FromOtherDevice: false, IsFile: type == "attachment", storedMsg.Text, replyTo, replyIndex,
            HistoryIndexOf(key, id)));
    }

    // ------------------------------------------------------------------
    // History helpers
    // ------------------------------------------------------------------

    private int HistoryIndexOf(string key, string id) => _history.IndexOf(key, id);

    /// <summary>null = not a reply, 0 = target not in the local history, n = its 1-based index.</summary>
    private int? ReplyIndexOf(string key, string? replyToId) => replyToId == null ? null : _history.IndexOf(key, replyToId);

    /// <summary>
    /// Two devices under the same profile directory (e.g. the same identity launched twice with an identical
    /// profile name) share history.json and can hit a transient file lock. Retry briefly rather than losing
    /// the operation's own result behind an unrelated I/O exception.
    /// </summary>
    private bool AppendHistorySafe(string key, string messageId, long originTs, MessageDirection direction, string text, string? replyToId = null, string? from = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _history.Append(key, messageId, originTs, direction, text, replyToId, from);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                RaiseNotice($"[!] Could not save to local history: {ex.Message}");
                return false;
            }
        }
    }
}
