/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Net.Http.Json;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Groups;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Outbox;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client;

// Sending, history, files, forwarding and the outbox.
public sealed partial class FreeSpeechClient
{
    // ------------------------------------------------------------------
    // Conversations
    // ------------------------------------------------------------------

    /// <summary>Resolves a contact reference: confirmed contact nickname, then manual alias, else assumed to already be a raw discovery_id.</summary>
    public string ResolveContact(string nameOrId) =>
        _confirmedContacts.FindByNickname(nameOrId)?.DiscoveryId ?? _aliases.Resolve(nameOrId);

    /// <summary>The 1-to-1 conversation with a contact (name, alias or id).</summary>
    public Conversation ContactConversation(string nameOrId)
    {
        var id = ResolveContact(nameOrId);
        return new Conversation(id, id, null);
    }

    public static Conversation GroupConversation(GroupRecord group) => new($"group:{group.GroupId}", null, group);

    /// <summary>A contact or a group. Names are tried as contact first; <c>group:&lt;name&gt;</c> forces a group.</summary>
    public Conversation ResolveConversation(string arg)
    {
        var forceGroup = arg.StartsWith("group:", StringComparison.OrdinalIgnoreCase);
        if (!forceGroup)
        {
            var id = ResolveContact(arg);
            if (_confirmedContacts.FindById(id) != null || _history.Count(id) > 0) return new Conversation(id, id, null);
        }

        var group = _groups.Store.Resolve(forceGroup ? arg[6..] : arg);
        if (group != null) return GroupConversation(group);

        throw new ClientException($"'{arg}' is neither a contact nor a group (use 'group:<name>' to force a group).");
    }

    // ------------------------------------------------------------------
    // History
    // ------------------------------------------------------------------

    public IReadOnlyList<HistoryLine> GetHistory(Conversation conversation)
    {
        var entries = _history.Get(conversation.HistoryKey);
        var lines = new List<HistoryLine>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            int? reply = null;
            if (entries[i].ReplyToId != null)
            {
                var target = -1;
                for (var j = 0; j < entries.Count; j++)
                    if (entries[j].Id == entries[i].ReplyToId) { target = j; break; }
                reply = target + 1; // 0 = not in local history
            }
            lines.Add(new HistoryLine(i + 1, entries[i], reply));
        }
        return lines;
    }

    private HistoryEntry EntryAt(Conversation c, int index) =>
        _history.At(c.HistoryKey, index)
        ?? throw new ClientException($"No message #{index} {(c.Group == null ? "with this contact" : "in this group")}.");

    // Paged reading — what a mobile conversation screen uses: open on the newest messages, load older ones
    // when the user scrolls up, newer ones when they scroll back down, or jump to a given message.

    /// <summary>The newest <paramref name="limit"/> messages of the conversation.</summary>
    public HistoryPage GetLatestMessages(Conversation conversation, int limit = 50) => _history.Latest(conversation.HistoryKey, limit);

    /// <summary>Up to <paramref name="limit"/> messages older than <paramref name="cursor"/> (<see cref="HistoryPage.Oldest"/> of the page on screen).</summary>
    public HistoryPage GetMessagesBefore(Conversation conversation, HistoryCursor cursor, int limit = 50) => _history.Before(conversation.HistoryKey, cursor, limit);

    /// <summary>Up to <paramref name="limit"/> messages newer than <paramref name="cursor"/> (<see cref="HistoryPage.Newest"/> of the page on screen).</summary>
    public HistoryPage GetMessagesAfter(Conversation conversation, HistoryCursor cursor, int limit = 50) => _history.After(conversation.HistoryKey, cursor, limit);

    /// <summary>A window centred on a message: jump to a quoted message or a search result.</summary>
    public HistoryPage GetMessagesAround(Conversation conversation, string messageId, int limit = 50) => _history.Around(conversation.HistoryKey, messageId, limit);

    /// <summary>Numbers a page the way <see cref="GetHistory"/> does (1-based position in the whole conversation, reply targets resolved), for front-ends that show "#N".</summary>
    public IReadOnlyList<HistoryLine> GetLines(Conversation conversation, HistoryPage page)
    {
        if (page.Entries.Count == 0) return [];
        var first = _history.IndexOf(conversation.HistoryKey, page.Entries[0].Id);
        return page.Entries.Select((e, i) => new HistoryLine(first + i, e,
            e.ReplyToId == null ? null : _history.IndexOf(conversation.HistoryKey, e.ReplyToId))).ToList();
    }

    /// <summary>The cursor of message #<paramref name="index"/> (as shown by history), to page before/after it.</summary>
    public HistoryCursor CursorAt(Conversation conversation, int index)
    {
        var e = EntryAt(conversation, index);
        return new HistoryCursor(e.Ts, e.Id);
    }

    /// <summary>One message by id (e.g. the target of a reply, to show a preview), or null.</summary>
    public HistoryEntry? FindMessage(Conversation conversation, string messageId) => _history.Find(conversation.HistoryKey, messageId);

    public int MessageCount(Conversation conversation) => _history.Count(conversation.HistoryKey);

    // ------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------

    /// <summary>Strictly increasing origin timestamp: chunks/letters sent back-to-back must never share a ts (it is part of the message_id, the signature and the relay de-duplication key).</summary>
    private long NextTs() => MessageClock.Next();

    /// <summary>Longest text this client sends or accepts to edit to (characters). A bigger text is a file, not a message.</summary>
    public const int MaxTextLength = 8000;

    /// <summary>What a received message may hold: well above what is sent, so a client with another limit still gets through.</summary>
    private const int MaxIncomingTextLength = 4 * MaxTextLength;

    private static void CheckTextLength(string text)
    {
        if (text.Length > MaxTextLength)
            throw new ClientException($"Message too long ({text.Length} characters, {MaxTextLength} at most).", "msg_too_long", text.Length, MaxTextLength);
    }

    /// <summary>Sends text to a contact or a group. Nothing is lost: a message that cannot be delivered now is queued in the outbox.</summary>
    public async Task<SentMessage> SendAsync(Conversation to, string text, string? replyToId = null)
    {
        CheckTextLength(text);
        if (to.Group != null)
        {
            var r = await _groups.SendAsync(to.Group, text, replyTo: replyToId);
            AppendHistorySafe(to.HistoryKey, $"{_identity.DiscoveryId}:{r.Ts}", r.Ts, MessageDirection.Sent, text, replyToId, _identity.DiscoveryId);
            return new SentMessage(to, text, r.Ts, false, SendStatus.Delivered, r.Delivered, r.Recipients);
        }
        return await SendLetterAsync(to, "message", text, replyToId, ts: null, historyText: text);
    }

    /// <summary>Replies to message #<paramref name="index"/> of the conversation history.</summary>
    public Task<SentMessage> ReplyAsync(Conversation to, int index, string text) =>
        SendAsync(to, text, EntryAt(to, index).Id);

    /// <summary>Offers a file (recipient or members download on demand, chunked and encrypted). One file per call.</summary>
    public async Task<SentMessage> UploadAsync(Conversation to, string path)
    {
        var ts = NextTs();
        return to.Group == null
            ? await OfferAsync(to, _attachments.Prepare(_identity.DiscoveryId, to.ContactId!, path.Trim('"'), ts), ts)
            : await OfferAsync(to, _attachments.Prepare(_identity.DiscoveryId, to.Group.GroupId, path.Trim('"'), ts, to.Group.GroupId), ts);
    }

    private async Task<SentMessage> OfferAsync(Conversation to, (AttachmentRecord Rec, string Payload) offer, long ts)
    {
        var label = $"[file] {offer.Rec.Name} ({offer.Rec.Size} bytes, {offer.Rec.Mime})";
        if (to.Group == null)
            return await SendLetterAsync(to, "attachment", offer.Payload, replyToId: null, ts, historyText: label);

        var r = await _groups.SendAsync(to.Group, offer.Payload, "attachment", ts);
        AppendHistorySafe(to.HistoryKey, $"{_identity.DiscoveryId}:{ts}", ts, MessageDirection.Sent, label, from: _identity.DiscoveryId);
        return new SentMessage(to, label, ts, true, SendStatus.Delivered, r.Delivered, r.Recipients);
    }

    /// <summary>
    /// Re-sends message #<paramref name="index"/> of <paramref name="source"/> to another contact or group.
    /// Text is simply sent again; a file is re-offered by its Merkle root (§6) — same blob on disk, nothing
    /// copied, hashed or re-uploaded; the new recipient downloads from us (or skips it if they already hold that content).
    /// </summary>
    public async Task<SentMessage> ForwardAsync(Conversation source, int index, Conversation target)
    {
        if (source.HistoryKey == target.HistoryKey) throw new ClientException("Source and destination are the same conversation.", "msg_same_conversation");

        var entry = EntryAt(source, index);
        var rec = _attachments.Store.Find(entry.Id);
        if (rec == null)
        {
            if (entry.Text.StartsWith("[file]")) throw new ClientException("That file is no longer known locally — cannot forward it.", "msg_file_unknown");
            return await SendAsync(target, entry.Text);
        }

        if (rec.State != AttachmentState.Complete) throw new ClientException("Download it first — you can only forward a file you hold.", "msg_download_first");

        var ts = NextTs();
        return target.Group == null
            ? await OfferAsync(target, _attachments.Forward(_identity.DiscoveryId, target.ContactId!, rec, ts), ts)
            : await OfferAsync(target, _attachments.Forward(_identity.DiscoveryId, target.Group.GroupId, rec, ts, target.Group.GroupId), ts);
    }

    /// <summary>
    /// Sends a Letter of any queued type ("message", "attachment") to a contact, encrypted for their stored
    /// public key (§5, crypto=1). Two failure modes land in the same local outbox: the recipient being offline
    /// (relay reachable) and the relay itself being unreachable (§9); either way the message is not lost, only
    /// the WAITING flag (which also needs the relay) is skipped in the second case.
    /// <paramref name="historyText"/> non-null = also recorded in the local history.
    /// </summary>
    private async Task<SentMessage> SendLetterAsync(Conversation to, string type, string text, string? replyToId, long? ts, string? historyText)
    {
        var peer = to.ContactId!;
        var contact = _confirmedContacts.FindById(peer)
            ?? throw new ClientException($"No confirmed contact for {peer} — exchange a ContactCard first ('export'/'import') so the message can be encrypted for them.");
        if (contact.MovedTo != null)
            throw new ClientException($"{contact.DisplayName} moved to a new identity: write to the new one.", "contact_moved", contact.DisplayName);

        var sendTs = ts ?? NextTs();
        var cipherMsg = MessageCrypto.Encrypt(_identity.DiscoveryId, peer, type, text, sendTs, Convert.FromBase64String(contact.EncryptionPublicKey), _identity.SigningPrivateKey);
        var envelope = new Envelope { From = _identity.DiscoveryId, To = peer, Crypto = 1, Msg = cipherMsg, Ts = sendTs, ReplyTo = replyToId };

        var result = await SendRoutedAsync(peer, CandidatesFor(peer), envelope);
        SendStatus status;
        if (result.Delivered)
        {
            status = SendStatus.Delivered;
            if (result.SelfMultiDevice && type == "message") // self-sync covers chat messages only; an attachment is served by the device that holds the file
                await SendSelfSyncCopyAsync(result.DeliveredVia!, peer, text, sendTs, replyToId);
            // The relay wrote it to a connection; whether the contact kept it, only their acknowledgement says: until then it stays in the outbox.
            if (AckTypes.Contains(type))
            {
                var letterId = $"{_identity.DiscoveryId}:{sendTs}";
                _pendingMessages.Add(new PendingMessage { Id = letterId, To = peer, Type = type, Text = text, Ts = sendTs, ReplyToId = replyToId, CreatedAt = DateTime.UtcNow, SentAt = DateTime.UtcNow });
                WatchUnconfirmed(peer, letterId);
            }
        }
        else
        {
            if (result.AnyReachable) // some server reachable, recipient just offline there
            {
                status = SendStatus.QueuedRecipientOffline;
                await TrySetWaitingFlagAsync(result.ReachableUrls, peer);
            }
            else status = SendStatus.QueuedNoServer;
            _pendingMessages.Add(new PendingMessage { Id = $"{_identity.DiscoveryId}:{sendTs}", To = peer, Type = type, Text = text, Ts = sendTs, ReplyToId = replyToId, CreatedAt = DateTime.UtcNow });
        }

        if (historyText != null)
            AppendHistorySafe(peer, $"{_identity.DiscoveryId}:{sendTs}", sendTs, MessageDirection.Sent, historyText, replyToId);
        return new SentMessage(to, historyText ?? text, sendTs, type == "attachment", status, status == SendStatus.Delivered ? 1 : 0, 1);
    }

    /// <summary>
    /// Fire-and-forget Letter for the attachment protocol (§6): not queued in the outbox and not in history —
    /// the receiver's stall timer is the retry mechanism. A request that finds the peer absent still sets the
    /// WAITING flag (same negotiation as a text message).
    /// </summary>
    private async Task<bool> SendLetterOnceAsync(string to, string type, string payload)
    {
        if (to == _identity.DiscoveryId) return await SendToMyDevicesAsync(type, payload);   // my other device (device mirror: its files)

        // The peer's key comes from the contact card, or — for a group member who is not a contact — from the signed GroupState.
        var encKey = _confirmedContacts.FindById(to)?.EncryptionPublicKey ?? _groups.FindMember(to)?.EncryptionPublicKey;
        if (encKey == null) return false;

        var ts = NextTs();
        var cipherMsg = MessageCrypto.Encrypt(_identity.DiscoveryId, to, type, payload, ts, Convert.FromBase64String(encKey), _identity.SigningPrivateKey);
        var envelope = new Envelope { From = _identity.DiscoveryId, To = to, Crypto = 1, Msg = cipherMsg, Ts = ts };
        var result = await SendRoutedAsync(to, CandidatesFor(to), envelope);
        if (!result.Delivered && result.AnyReachable && type == "attachment_request")
            await TrySetWaitingFlagAsync(result.ReachableUrls, to);
        return result.Delivered;
    }

    /// <summary>
    /// Live multi-device sync (§1/§5): sent only when the relay says we have another active connection right
    /// now. The original envelope is encrypted for the recipient's key and unreadable by our other devices, so
    /// this is a second envelope addressed to ourselves, carrying the real recipient as <c>orig_to</c>.
    /// </summary>
    private async Task SendSelfSyncCopyAsync(string viaUrl, string origTo, string text, long ts, string? replyToId)
    {
        try
        {
            var cipherMsg = MessageCrypto.Encrypt(_identity.DiscoveryId, _identity.DiscoveryId, "message", text, ts, _identity.EncryptionPublicKey, _identity.SigningPrivateKey, origTo);
            var envelope = new Envelope { From = _identity.DiscoveryId, To = _identity.DiscoveryId, Crypto = 1, Msg = cipherMsg, Ts = ts, ReplyTo = replyToId };
            await _relay.SendOnAsync(viaUrl, envelope);
        }
        catch (Exception ex)
        {
            RaiseNotice($"[!] Self-sync copy to another device failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Outbox
    // ------------------------------------------------------------------

    /// <summary>What the outbox holds: letters waiting for their contact (<see cref="PendingMessage.SentAt"/> null) and letters delivered but not confirmed yet.</summary>
    public IReadOnlyList<PendingMessage> Outbox => _pendingMessages.All;

    /// <summary>
    /// Stops the waiting for an undelivered letter (<paramref name="parked"/> true) or resumes it. The letter is not deleted: it still goes out when its contact comes online.
    /// Returns false when the letter is unknown or already delivered.
    /// </summary>
    public bool SetOutboxParked(string id, bool parked) => _pendingMessages.SetParked(id, parked);

    /// <summary>
    /// Retries every queued outbound message (also run by the background tick). The relay has no
    /// store-and-forward (§9/§10 — presence flags only), so this outbox is what survives the recipient being offline.
    /// </summary>
    public async Task FlushPendingMessagesAsync() => await FlushPendingMessagesAsync(fromTick: false);

    /// <summary>
    /// <paramref name="fromTick"/>: the periodic run, which spaces out and finally stops retrying a letter nobody has taken (see <see cref="RetryInterval"/>).
    /// Any other run (the contact just came online, the app started, the user asked) tries every letter, however old.
    /// </summary>
    private async Task FlushPendingMessagesAsync(bool fromTick)
    {
        await _flushGate.WaitAsync();   // the tick and a peer_online event must not send the same message twice
        try { await FlushCoreAsync(fromTick); await ResendPendingWipeAsync(); }
        finally { _flushGate.Release(); }
    }

    /// <summary>After this long without a taker, the periodic run leaves a letter alone: it stays queued and goes out when its contact is seen online.</summary>
    public static readonly TimeSpan OutboxGiveUpAfter = TimeSpan.FromDays(3);

    /// <summary>
    /// How long the periodic run waits between two tries of a letter nobody has taken, by its age: every tick for the first 10 minutes,
    /// then every 15 minutes up to a day, then every 6 hours, and never after <see cref="OutboxGiveUpAfter"/> (null).
    /// </summary>
    public static TimeSpan? RetryInterval(TimeSpan age) =>
        age < TimeSpan.FromMinutes(10) ? TimeSpan.Zero
        : age < TimeSpan.FromHours(24) ? TimeSpan.FromMinutes(15)
        : age < OutboxGiveUpAfter ? TimeSpan.FromHours(6)
        : null;

    private readonly SemaphoreSlim _flushGate = new(1, 1);

    /// <summary>Registers (or, with null, removes) this device's push token on every own server (protocol §9). Re-sent after each reconnect.</summary>
    public Task SetPushTokenAsync(PushTokenInfo? info) => _relay.SetPushAsync(info);

    private async Task FlushCoreAsync(bool fromTick = false)
    {
        var queued = _pendingMessages.All.ToList();
        if (fromTick) queued.RemoveAll(NotDueForTick);
        var sent = 0;
        if (queued.Count > 0) ClientLog.Debug("outbox", $"{queued.Count} message(s) queued, trying to send");
        try { await FlushQueuedAsync(queued, () => sent++); }
        finally { if (queued.Count > 0) ClientLog.Info("outbox", $"run done: {sent} sent, {queued.Count - sent} still queued"); }
    }

    /// <summary>True when the periodic run should skip this letter for now (not yet taken by its contact, and not due again per <see cref="RetryInterval"/>).</summary>
    private static bool NotDueForTick(PendingMessage pm)
    {
        if (pm.SentAt != null) return false;   // delivered, waiting for the acknowledgement: DueForResend decides
        if (pm.Parked) return true;            // the user stopped the waiting: only the contact coming online (or a manual flush) sends it
        var wait = RetryInterval(DateTime.UtcNow - pm.CreatedAt);
        if (wait == null) return true;
        return pm.LastAttemptAt is { } last && DateTime.UtcNow - last < wait;
    }

    private async Task FlushQueuedAsync(List<PendingMessage> queued, Action onSent)
    {
        foreach (var pm in queued)
        {
            // Delivered, waiting for the contact's confirmation: sent again only when it has waited long enough; dropped when every resend was used.
            if (pm.SentAt != null)
            {
                if (!DueForResend(pm, out var giveUp))
                {
                    if (giveUp) _pendingMessages.Remove(pm.Id);
                    continue;
                }
            }

            // A recipient who is not a contact (group invitation) carries its own key and servers.
            byte[] recipientEncPub;
            IReadOnlyList<string> candidates;
            if (pm.RecipientEncKey != null)
            {
                recipientEncPub = Convert.FromBase64String(pm.RecipientEncKey);
                candidates = _relay.Candidates(pm.RecipientServers, pm.To);
            }
            else
            {
                var contact = _confirmedContacts.FindById(pm.To);
                if (contact == null) continue; // still no key to encrypt for — leave queued
                recipientEncPub = Convert.FromBase64String(contact.EncryptionPublicKey);
                candidates = CandidatesFor(pm.To);
            }

            var cipherMsg = MessageCrypto.Encrypt(_identity.DiscoveryId, pm.To, pm.Type, pm.Text, pm.Ts, recipientEncPub, _identity.SigningPrivateKey);
            var envelope = new Envelope { From = _identity.DiscoveryId, To = pm.To, Crypto = 1, Msg = cipherMsg, Ts = pm.Ts, ReplyTo = pm.ReplyToId };

            var result = await SendRoutedAsync(pm.RecipientEncKey == null ? pm.To : null, candidates, envelope);
            _pendingMessages.TouchLastAttempt(pm.Id);

            if (result.Delivered)
            {
                onSent();
                var firstTime = pm.SentAt == null;
                if (!firstTime) _pendingMessages.MarkResent(pm.Id);                 // sent again, still waiting for the confirmation
                else if (AckTypes.Contains(pm.Type)) _pendingMessages.MarkSent(pm.Id);   // delivered: it stays until the contact confirms it
                else _pendingMessages.Remove(pm.Id);
                if (AckTypes.Contains(pm.Type)) WatchUnconfirmed(pm.To, pm.Id);
                if (result.SelfMultiDevice && pm.Type == "message" && firstTime)
                    await SendSelfSyncCopyAsync(result.DeliveredVia!, pm.To, pm.Text, pm.Ts, pm.ReplyToId);
            }
            else if (pm.SentAt != null) _pendingMessages.MarkUnsent(pm.Id);   // the contact is not reachable now: it waits for them as any queued letter
        }
    }

    // ------------------------------------------------------------------
    // Files
    // ------------------------------------------------------------------

    /// <summary>Sent and received files with their transfer state.</summary>
    public AttachmentService Attachments => _attachments;

    /// <summary>Moves downloaded files into an encrypted archive file to free space, and loads one back (protocol §2 "Archive").</summary>
    public Archive.ArchiveService Archive => _archive;

    /// <summary>
    /// The "files only on Wi-Fi" setting (protocol §6): <see cref="TransferPolicy.Mode"/> is the user's choice, <see cref="TransferPolicy.Network"/> what the app says
    /// the device is on. It holds back file chunks only, asked for or served; messages always go through. A console client leaves the network unknown: nothing is held back.
    /// </summary>
    public TransferPolicy FileTransfer { get; } = new();

    /// <summary>The app tells which kind of network the device is on now; downloads that were waiting for Wi-Fi start again when it allows them.</summary>
    public void SetNetwork(NetworkKind network)
    {
        FileTransfer.Network = network;
        if (_attachments != null) _ = _attachments.NetworkChangedAsync();   // not created yet before Initialize: the setting is read when it is
    }

    /// <summary>Downloads the file offered by message #<paramref name="index"/> (resumes automatically; any member holding it can serve a group file). Returns a status line.</summary>
    public async Task<string> DownloadAsync(Conversation from, int index, bool anyway = false)
    {
        var rec = _attachments.Store.Find(EntryAt(from, index).Id) ?? throw new ClientException($"#{index} is not a file.", "msg_not_file", index);
        _attachments.EnsureRoom(rec);
        return anyway ? await _attachments.DownloadAnywayAsync(rec) : await _attachments.RequestAsync(rec);
    }

    // ------------------------------------------------------------------
    // Presence and flags (§9)
    // ------------------------------------------------------------------

    private async Task TrySetWaitingFlagAsync(IEnumerable<string> urls, string to)
    {
        var body = new { from = _identity.DiscoveryId, to, state = "WAITING" };
        // Best-effort on every server we could reach: the recipient may be registered on only a subset of
        // theirs (§8), and polls flags on each. The local outbox is what actually guarantees delivery.
        foreach (var (_, resp, _) in await _relay.HttpAsync(urls, h => h.PostAsJsonAsync("/api/flags", body)))
            resp?.Dispose();
    }

    public async Task<bool> IsOnlineAsync(string nameOrId)
    {
        var id = ResolveContact(nameOrId);
        return await _relay.IsOnlineAsync(CandidatesFor(id), id);
    }

    private static List<FlagResult> ToFlagResults(List<(string Url, HttpResponseMessage? Resp, string? Error)> results)
    {
        var list = new List<FlagResult>();
        foreach (var (url, resp, error) in results)
        {
            list.Add(resp is { IsSuccessStatusCode: true } ? new FlagResult(url, true, null)
                : new FlagResult(url, false, resp != null ? $"HTTP {(int)resp.StatusCode}" : error));
            resp?.Dispose();
        }
        return list;
    }

    public async Task<IReadOnlyList<FlagResult>> SetWaitingAsync(string nameOrId)
    {
        var to = ResolveContact(nameOrId);
        var body = new { from = _identity.DiscoveryId, to, state = "WAITING" };
        return ToFlagResults(await _relay.HttpAsync(CandidatesFor(to), h => h.PostAsJsonAsync("/api/flags", body)));
    }

    public async Task<IReadOnlyList<FlagResult>> SetReadyAsync(string nameOrId, int seconds)
    {
        var to = ResolveContact(nameOrId);
        var body = new { from = _identity.DiscoveryId, to, state = "READY", next_connect_at = DateTime.UtcNow.AddSeconds(seconds) };
        return ToFlagResults(await _relay.HttpAsync(CandidatesFor(to), h => h.PostAsJsonAsync("/api/flags", body)));
    }

    // ------------------------------------------------------------------
    // WAITING flag for a letter nobody confirmed
    // ------------------------------------------------------------------

    private readonly object _flagLock = new();
    private readonly Dictionary<string, DateTime> _flaggedForUnconfirmed = [];
    private static readonly TimeSpan FlagRefresh = TimeSpan.FromMinutes(10);

    private TimeSpan UnconfirmedFlagDelay => _options.UnconfirmedFlagDelay ?? TimeSpan.FromSeconds(3);

    /// <summary>True while a WAITING flag I raised for a letter of this contact nobody confirmed is up (tests).</summary>
    internal bool FlaggedForUnconfirmed(string peer) { lock (_flagLock) return _flaggedForUnconfirmed.ContainsKey(peer); }

    /// <summary>
    /// The relay calls a letter delivered once it wrote it to a connection, which a phone in airplane mode has left dead for a while: the letter then waits for the
    /// contact's confirmation, and no WAITING flag was raised because it counted as delivered. After a short grace (a live contact confirms within a fraction of a second)
    /// the flag is raised for every contact who still has a letter of mine to confirm: when the contact is back on a new connection the relay tells me (or wakes me by push),
    /// and the letter goes out again at once.
    /// </summary>
    private void WatchUnconfirmed(string peer, string letterId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(UnconfirmedFlagDelay);
                if (_pendingMessages.All.Any(p => p.Id == letterId && p.SentAt != null)) await FlagUnconfirmedAsync(peer);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ClientException)
            {
                ClientLog.Warn("outbox", $"WAITING flag for an unconfirmed letter failed: {ex.GetType().Name}");
            }
        });
    }

    /// <summary>
    /// Raises the WAITING flag for every contact (or only <paramref name="onlyPeer"/>) who has not confirmed a letter of mine the relay called delivered. An app that is
    /// going to the background calls it without waiting for the grace: it may not be alive when the contact comes back.
    /// </summary>
    public async Task FlagUnconfirmedAsync(string? onlyPeer = null)
    {
        var peers = _pendingMessages.All.Where(p => p.SentAt != null && (onlyPeer == null || p.To == onlyPeer)).Select(p => p.To).Distinct().ToList();
        foreach (var peer in peers)
        {
            lock (_flagLock)
            {
                if (_flaggedForUnconfirmed.TryGetValue(peer, out var at) && DateTime.UtcNow - at < FlagRefresh) continue;   // up already (a relay that restarted loses its flags: raised again after a while)
                _flaggedForUnconfirmed[peer] = DateTime.UtcNow;
            }
            await TrySetWaitingFlagAsync(CandidatesFor(peer), peer);
        }
    }

    private async Task ResendUnconfirmedAndFlushAsync(string peer)
    {
        _pendingMessages.MarkAllUnsentFor(peer);
        await FlushPendingMessagesAsync();
    }

    /// <summary>The contact confirmed: when nothing of mine waits for them any more, the flag raised for them is taken down (nobody is woken for nothing later).</summary>
    private void FlagDownIfNothingWaits(string peer)
    {
        if (_pendingMessages.All.Any(p => p.To == peer)) return;
        bool raised;
        lock (_flagLock) raised = _flaggedForUnconfirmed.Remove(peer);
        if (!raised) return;
        _ = Task.Run(async () =>
        {
            try { await _relay.HttpAsync(CandidatesFor(peer), h => h.DeleteAsync($"/api/flags/{_identity.DiscoveryId}/{peer}")); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ClientException) { /* best effort: the flag expires by itself */ }
        });
    }

    public async Task<IReadOnlyList<FlagResult>> ClearFlagAsync(string fromNameOrId, string toNameOrId)
    {
        var from = ResolveContact(fromNameOrId);
        var to = ResolveContact(toNameOrId);
        var urls = _confirmedContacts.FindById(from) is { } fromContact ? _relay.Candidates(fromContact.Servers, from) : _relay.Candidates(_confirmedContacts.FindById(to)?.Servers, to);
        return ToFlagResults(await _relay.HttpAsync(urls, h => h.DeleteAsync($"/api/flags/{from}/{to}")));
    }

    /// <summary>Flags addressed to me live on each of MY servers (I poll where I'm registered).</summary>
    public async Task<(IReadOnlyList<FlagInfo> Flags, IReadOnlyList<FlagResult> Errors)> GetFlagsAsync()
    {
        var flags = new List<FlagInfo>();
        var errors = new List<FlagResult>();
        foreach (var (url, resp, error) in await _relay.HttpAsync(_relay.OwnUrls, h => h.GetAsync($"/api/flags/{_identity.DiscoveryId}")))
        {
            if (resp is not { IsSuccessStatusCode: true })
            {
                errors.Add(new FlagResult(url, false, resp != null ? $"HTTP {(int)resp.StatusCode}" : error));
                resp?.Dispose();
                continue;
            }

            var dtos = await resp.Content.ReadFromJsonAsync<List<FlagDto>>() ?? [];
            resp.Dispose();
            flags.AddRange(dtos.Select(f => new FlagInfo(url, f.From, f.To, f.State, f.NextConnectAt)));
        }
        return (flags, errors);
    }

    private sealed class FlagDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("from")] public string From { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("to")] public string To { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("state")] public string State { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("next_connect_at")] public DateTime? NextConnectAt { get; set; }
    }
}
