/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.History;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client;

/// <summary>Asked by a device that has just been restored (its identity re-entered, a backup of the other device imported): "send me what happened since". Addressed to my own identity.</summary>
public sealed class DeviceSyncRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>The asking device (random, per run): the answer is addressed to it, and a device never answers itself.</summary>
    [JsonPropertyName("device")] public string Device { get; set; } = "";

    /// <summary>Per conversation, the digest of what the asking device holds (see <see cref="HistoryStore.Digests"/>): the other device mirrors what differs.</summary>
    [JsonPropertyName("have")] public Dictionary<string, string> Have { get; set; } = [];

    /// <summary>Per contact, the digest of the card the asking device holds.</summary>
    [JsonPropertyName("contacts")] public Dictionary<string, string> Contacts { get; set; } = [];
    [JsonPropertyName("ts")] public long Ts { get; set; }

    /// <summary>Six digits shown on the asking device only: the owner types them on the other device, which sends nothing without them.</summary>
    [JsonPropertyName("code")] public string Code { get; set; } = "";
}

/// <summary>One part of the answer (or a refusal). Several parts, the last one flagged.</summary>
public sealed class DeviceSyncData
{
    [JsonPropertyName("request")] public string Request { get; set; } = "";
    [JsonPropertyName("to_device")] public string ToDevice { get; set; } = "";
    [JsonPropertyName("refused")] public bool Refused { get; set; }
    [JsonPropertyName("last")] public bool Last { get; set; }
    [JsonPropertyName("contacts")] public List<Contact> Contacts { get; set; } = [];

    /// <summary>Conversations that are sent whole: the asking device replaces what it holds of them (it mirrors this device).</summary>
    [JsonPropertyName("replace")] public List<string> Replace { get; set; } = [];

    /// <summary>Conversations the asking device holds and this one does not: removed there.</summary>
    [JsonPropertyName("remove")] public List<string> Remove { get; set; } = [];
    [JsonPropertyName("history")] public Dictionary<string, List<HistoryEntry>> History { get; set; } = [];

    /// <summary>Records of the files of those messages, without the file itself: a received one is offered again, a sent one cannot be served from here.</summary>
    [JsonPropertyName("attachments")] public List<AttachmentRecord> Attachments { get; set; } = [];
}

/// <summary>A request from another device of my identity, waiting for my answer.</summary>
/// <summary>Deliberately without the code: it is only on the screen of the asking device, and the person must type it here.</summary>
public sealed record DeviceSyncRequestInfo(string Id, DateTime At);

public sealed partial class FreeSpeechClient
{
    private const string TypeSyncRequest = "device_sync_request";   // listed in PROTOCOL §5 "Types de Letter"
    private const string TypeSyncData = "device_sync_data";
    private static readonly TimeSpan SyncRequestValidFor = TimeSpan.FromMinutes(5);

    /// <summary>Wrong codes tolerated for one request: at the next one it is cancelled and the asking device is told.</summary>
    public const int SyncCodeTries = 3;
    private readonly Dictionary<string, int> _syncWrong = [];
    private const int SyncPartBytes = 150_000;   // a part stays well under the relay's message limit

    private readonly string _device = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();   // this run of this device
    private readonly object _syncLock = new();
    private readonly List<(DeviceSyncRequest Request, DateTime At)> _syncIn = [];
    private (DeviceSyncRequest Request, DateTime At, bool Delivered)? _syncOut;
    private readonly HashSet<string> _syncReplaced = [];

    /// <summary>Files this device has offered to another device of my identity in an answer the owner agreed to: only those are served to it (a request from a thief of the phrases names none).</summary>
    private readonly HashSet<string> _syncServed = [];

    private static string ContactDigest(Contact c) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(c))))[..16];

    /// <summary>May another device of my identity fetch this file from me? Only a file named in an answer my owner agreed to.</summary>
    private bool ServedToMyDevice(AttachmentRecord rec) { lock (_syncLock) return rec.State == AttachmentState.Complete && _syncServed.Contains(rec.Ref); }

    /// <summary>Requests from other devices of my identity, not answered yet (memory only: a request that waits more than a few minutes is stale).</summary>
    public IReadOnlyList<DeviceSyncRequestInfo> PendingDeviceSyncs
    {
        get { lock (_syncLock) return [.. _syncIn.Where(x => Fresh(x.Request)).Select(x => Info(x.Request, x.At))]; }
    }

    /// <summary>Another device of my identity asks for my data: the app asks the person (the code is the same on both screens).</summary>
    public event Action<DeviceSyncRequestInfo>? DeviceSyncRequested;

    /// <summary>What happens to the request this device made: "sent" (a device received it), "refused", "received" (a part arrived), "done".</summary>
    public event Action<string>? DeviceSyncChanged;

    /// <summary>Messages merged from the other device since the request.</summary>
    public int DeviceSyncMessages { get; private set; }

    /// <summary>The code of the request this device made, to show it ("123 456"): the person types it on their other device.</summary>
    public string? DeviceSyncCode { get { lock (_syncLock) return _syncOut is { } o ? $"{o.Request.Code[..3]} {o.Request.Code[3..]}" : null; } }

    private static bool Fresh(DeviceSyncRequest r) => DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).UtcDateTime < SyncRequestValidFor;
    private static DeviceSyncRequestInfo Info(DeviceSyncRequest r, DateTime at) => new(r.Id, at);

    /// <summary>The digits typed or shown, without spaces or dashes.</summary>
    private static string Digits(string? code) => new((code ?? "").Where(char.IsAsciiDigit).ToArray());

    // ------------------------------------------------------------------ the restored device

    /// <summary>
    /// Asks another device of my identity (one connected now) to be mirrored: the contacts whose card differs, and the conversations whose content differs
    /// (messages, edits, deletions), which come whole; a conversation that device does not hold is removed here. The other device is the reference. Nothing
    /// leaves it until its owner agrees (the request shows a code). Returns false when no other device is connected
    /// (the request is repeated for a few minutes, so one that comes online soon answers).
    /// </summary>
    public async Task<bool> RequestDeviceSyncAsync()
    {
        var request = new DeviceSyncRequest
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), Device = _device, Code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
            Have = _history.Digests().Where(kv => !kv.Key.StartsWith("group:", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Contacts = _confirmedContacts.All.ToDictionary(c => c.DiscoveryId, c => ContactDigest(c)),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        lock (_syncLock) { _syncOut = (request, DateTime.UtcNow, false); DeviceSyncMessages = 0; }
        var delivered = await SendToMyDevicesAsync(TypeSyncRequest, JsonSerializer.Serialize(request));
        lock (_syncLock) if (_syncOut is { } o && o.Request.Id == request.Id) _syncOut = (o.Request, o.At, delivered);
        if (delivered) DeviceSyncChanged?.Invoke("sent");
        return delivered;
    }

    /// <summary>Tick: a request that no device has received yet is offered again, for a few minutes.</summary>
    private async Task RepeatDeviceSyncAsync()
    {
        (DeviceSyncRequest Request, DateTime At, bool Delivered)? o;
        lock (_syncLock) o = _syncOut;
        if (o is not { Delivered: false } pending) return;
        if (!Fresh(pending.Request)) { lock (_syncLock) _syncOut = null; return; }
        if (await SendToMyDevicesAsync(TypeSyncRequest, JsonSerializer.Serialize(pending.Request)))
        {
            lock (_syncLock) if (_syncOut is { } cur && cur.Request.Id == pending.Request.Id) _syncOut = (cur.Request, cur.At, true);
            DeviceSyncChanged?.Invoke("sent");
        }
    }

    /// <summary>A letter to my own identity: it reaches my other devices, never this one. True when at least one received it.</summary>
    private async Task<bool> SendToMyDevicesAsync(string type, string payload)
    {
        try
        {
            var ts = NextTs();
            var cipher = MessageCrypto.Encrypt(_identity.DiscoveryId, _identity.DiscoveryId, type, payload, ts, _identity.EncryptionPublicKey, _identity.SigningPrivateKey);
            var envelope = new Envelope { From = _identity.DiscoveryId, To = _identity.DiscoveryId, Crypto = 1, Msg = cipher, Ts = ts };
            return (await _relay.SendAsync(_relay.Candidates(null), envelope)).Delivered;
        }
        catch (Exception ex) when (ex is ClientException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ the other device

    private void HandleDeviceSyncRequest(string payload)
    {
        DeviceSyncRequest? r;
        try { r = JsonSerializer.Deserialize<DeviceSyncRequest>(payload); }
        catch (JsonException) { return; }
        if (r == null || r.Id.Length is < 16 or > 64 || r.Device == _device || !Fresh(r)) return;   // my own echo, or a stale one

        DeviceSyncRequestInfo info;
        lock (_syncLock)
        {
            _syncIn.RemoveAll(x => !Fresh(x.Request));
            if (_syncIn.Any(x => x.Request.Id == r.Id)) return;   // repeated by the asker while I have not answered
            _syncIn.Add((r, DateTime.UtcNow));
            info = Info(r, DateTime.UtcNow);
        }
        DeviceSyncRequested?.Invoke(info);
    }

    /// <summary>
    /// The person typed the code shown on the asking device: when it is the right one, what that device lacks goes to it, in parts (returns how many messages).
    /// A wrong code throws <c>sync_wrong_code</c> (the request stays); the <see cref="SyncCodeTries"/>th wrong one cancels the request and tells the asking device.
    /// </summary>
    public async Task<int> ApproveDeviceSyncAsync(string requestId, string code)
    {
        DeviceSyncRequest? r;
        var cancelled = false;
        var left = 0;
        string? asker;
        lock (_syncLock)
        {
            r = _syncIn.FirstOrDefault(x => x.Request.Id == requestId).Request;
            asker = r?.Device;
            if (r != null && Fresh(r))
            {
                if (Digits(code) == r.Code && r.Code.Length == 6) _syncIn.RemoveAll(x => x.Request.Id == requestId);
                else
                {
                    var wrong = _syncWrong[requestId] = _syncWrong.GetValueOrDefault(requestId) + 1;
                    left = SyncCodeTries - wrong;
                    if (left <= 0) { _syncIn.RemoveAll(x => x.Request.Id == requestId); cancelled = true; }
                    r = null;
                }
            }
            else { r = null; left = -1; }
        }
        if (cancelled) await SendToMyDevicesAsync(TypeSyncData, JsonSerializer.Serialize(new DeviceSyncData { Request = requestId, ToDevice = asker ?? "", Refused = true, Last = true }));
        if (r == null) throw new ClientException(left < 0 ? "This request is no longer valid." : cancelled ? "Too many wrong codes: the request is cancelled." : "Wrong code.",
            left < 0 ? "sync_expired" : cancelled ? "sync_cancelled" : "sync_wrong_code", left);

        var sent = 0;
        var part = new DeviceSyncData { Request = r.Id, ToDevice = r.Device };
        var size = 0;
        async Task FlushAsync(bool last)
        {
            part.Last = last;
            await SendToMyDevicesAsync(TypeSyncData, JsonSerializer.Serialize(part));
            part = new DeviceSyncData { Request = r.Id, ToDevice = r.Device };
            size = 0;
        }
        async Task AddAsync(int bytes)
        {
            size += bytes;
            if (size >= SyncPartBytes) await FlushAsync(false);
        }

        // Contacts whose card the asking device does not hold, or holds differently (a card can carry a picture).
        foreach (var c in _confirmedContacts.All.Where(c => !r.Contacts.TryGetValue(c.DiscoveryId, out var d) || d != ContactDigest(c)).ToList())
        {
            part.Contacts.Add(c);
            await AddAsync(JsonSerializer.Serialize(c).Length);
        }

        // Conversations that differ come whole (the asking device replaces its copy); those it holds and this device does not are removed there.
        // Groups catch up with their members (§7), not from here.
        var mine = _history.Digests().Where(kv => !kv.Key.StartsWith("group:", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
        part.Remove.AddRange(r.Have.Keys.Where(k => !mine.ContainsKey(k)));
        foreach (var (conv, digest) in mine)
        {
            if (r.Have.TryGetValue(conv, out var theirs) && theirs == digest) continue;
            part.Replace.Add(conv);
            var list = part.History[conv] = [];
            foreach (var e in _history.Get(conv))
            {
                list.Add(e);
                sent++;
                var bytes = e.Text.Length + 120;
                if (_attachments.Store.Find(e.Id) is { } rec)
                {
                    part.Attachments.Add(new AttachmentRecord
                    {
                        Ref = rec.Ref, ContactId = rec.ContactId, Direction = rec.Direction, Name = rec.Name, Mime = rec.Mime, Size = rec.Size, Root = rec.Root,
                        Chunks = rec.Chunks, State = AttachmentState.Offered, GroupId = rec.GroupId,
                    });
                    if (rec.State == AttachmentState.Complete) lock (_syncLock) _syncServed.Add(rec.Ref);   // the asking device may now fetch it
                    bytes += 300;
                }
                if (size + bytes >= SyncPartBytes && list.Count > 0) { await FlushAsync(false); part.History[conv] = list = []; }   // the next part continues the conversation: the other side already replaced it
                await AddAsync(bytes);
            }
        }        await FlushAsync(true);
        return sent;
    }

    /// <summary>The person refused: the asking device is told, nothing is sent.</summary>
    public async Task RejectDeviceSyncAsync(string requestId)
    {
        DeviceSyncRequest? r;
        lock (_syncLock)
        {
            r = _syncIn.FirstOrDefault(x => x.Request.Id == requestId).Request;
            _syncIn.RemoveAll(x => x.Request.Id == requestId);
        }
        if (r == null) return;
        await SendToMyDevicesAsync(TypeSyncData, JsonSerializer.Serialize(new DeviceSyncData { Request = r.Id, ToDevice = r.Device, Refused = true, Last = true }));
    }

    // ------------------------------------------------------------------ the restored device again: the answer

    private void HandleDeviceSyncData(string payload)
    {
        DeviceSyncData? d;
        try { d = JsonSerializer.Deserialize<DeviceSyncData>(payload); }
        catch (JsonException) { return; }
        if (d == null) return;
        bool wasPending;
        lock (_syncLock) { wasPending = _syncIn.RemoveAll(x => x.Request.Id == d.Request) > 0; _syncWrong.Remove(d.Request); }   // another device of mine answered it (or it was cancelled): no longer pending here
        if (wasPending) DeviceSyncChanged?.Invoke("closed");
        if (d.ToDevice != _device) return;   // for another device, or my own echo
        lock (_syncLock) { if (_syncOut is not { } o || o.Request.Id != d.Request) return; }   // I asked nothing, or something else

        if (d.Refused)
        {
            lock (_syncLock) _syncOut = null;
            DeviceSyncChanged?.Invoke("refused");
            return;
        }

        foreach (var c in d.Contacts) _confirmedContacts.Upsert(c);   // the other device is the reference: its card wins
        foreach (var conv in d.Remove) _history.DeleteConversation(conv);
        foreach (var conv in d.Replace) if (_syncReplaced.Add(d.Request + "|" + conv)) _history.DeleteConversation(conv);   // once per request: the next parts add to it
        var added = 0;
        foreach (var (conv, entries) in d.History)
            foreach (var e in entries)
                if (_history.Append(conv, e.Id, e.Ts, e.Direction, e.Text, e.ReplyToId, e.From, e.State, e.EditedTs)) added++;
        foreach (var rec in d.Attachments)
        {
            if (_attachments.Store.Find(rec.Ref) != null) continue;
            // The file itself is fetched from the other device (a sent file too): the line becomes a real picture or document once it is here.
            rec.Direction = AttachmentDirection.Received;
            rec.FromMyDevice = true;
            rec.Path = null;
            rec.State = AttachmentState.Offered;
            _attachments.Store.Add(rec);
        }

        lock (_syncLock) DeviceSyncMessages += added;
        if (d.Last)
        {
            lock (_syncLock) { _syncOut = null; _syncReplaced.Clear(); }
            DeviceSyncChanged?.Invoke("done");
        }
        else DeviceSyncChanged?.Invoke("received");
    }
}
