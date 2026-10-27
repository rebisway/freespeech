/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using System.Text.Json.Serialization;
using FreeSpeech.Client.Outbox;

namespace FreeSpeech.Client;

/// <summary>Payload of an "ack" Letter: the letters of the receiver's contact that arrived, by message id ("{sender}:{ts}").</summary>
internal sealed class AckOp
{
    [JsonPropertyName("ids")] public List<string> Ids { get; set; } = [];
}

// The relay calls a letter "delivered" as soon as it wrote it to the contact's connection, which may already be dead (a phone that lost its network, a process that was
// killed): the letter would be lost without anyone knowing. The contact therefore confirms every letter it keeps (PROTOCOL §5, "ack"), and a delivered letter stays in the
// outbox until the confirmation comes; without it the letter is sent again, a few times, with growing delays. A message, an edit, a deletion, a reaction and a file offer
// all go this way: there is one way for a letter of a contact to reach it.
public sealed partial class FreeSpeechClient
{
    private const string TypeAck = "ack";

    /// <summary>The Letter types that are confirmed: every one that goes through the outbox and is kept by the receiver.</summary>
    private static readonly HashSet<string> AckTypes = ["message", "attachment", TypeEdit, TypeRetract, TypeFileRetract, TypeReaction, "identity_update", TypeRoutePolicy];

    private static readonly TimeSpan[] DefaultAckBackoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    private TimeSpan[] AckBackoff => _options.AckBackoff ?? DefaultAckBackoff;

    private readonly object _ackLock = new();
    private readonly Dictionary<string, List<string>> _acks = [];

    /// <summary>Test aid: letters that arrive are dropped unseen, as if the connection they came through was dead.</summary>
    internal bool DropIncoming { get => _relay.DropIncoming; set => _relay.DropIncoming = value; }

    /// <summary>A letter of <paramref name="from"/> was kept: it is confirmed to them, with the others of the next moments in one Letter.</summary>
    private void QueueAck(string from, long ts)
    {
        if (!_options.SendAcks) return;
        lock (_ackLock)
        {
            if (!_acks.TryGetValue(from, out var ids))
            {
                _acks[from] = ids = [];
                _ = SendAcksAsync(from);
            }
            ids.Add($"{from}:{ts}");
        }
    }

    private async Task SendAcksAsync(string to)
    {
        try
        {
            await Task.Delay(300);
            List<string> ids;
            lock (_ackLock) { ids = _acks[to]; _acks.Remove(to); }
            await SendLetterOnceAsync(to, TypeAck, JsonSerializer.Serialize(new AckOp { Ids = ids }));
        }
        catch (Exception ex) when (ex is ClientException or HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // Best effort: without it the contact sends the letter again, and it is stored once.
        }
    }

    /// <summary>My contact confirms letters of mine: they leave the outbox. Only the contact they were sent to can confirm them.</summary>
    private void HandleAck(string from, string payload)
    {
        AckOp? op;
        try { op = JsonSerializer.Deserialize<AckOp>(payload); }
        catch (JsonException) { return; }
        if (op == null || op.Ids.Count == 0 || op.Ids.Count > 500) return;
        _pendingMessages.Acknowledge(from, op.Ids.Where(id => id.StartsWith(_identity.DiscoveryId + ":", StringComparison.Ordinal)));
        FlagDownIfNothingWaits(from);
    }

    /// <summary>
    /// A letter delivered but not yet confirmed: has it waited long enough to be sent again? <paramref name="giveUp"/> when all the resends are used and the last wait
    /// is over too: the letter is then dropped (a contact whose version does not confirm letters would otherwise be sent it for ever).
    /// </summary>
    private bool DueForResend(PendingMessage pm, out bool giveUp)
    {
        var backoff = AckBackoff;
        var due = DateTime.UtcNow - (pm.SentAt ?? DateTime.UtcNow) >= backoff[Math.Min(pm.Resends, backoff.Length - 1)];
        giveUp = due && pm.Resends >= backoff.Length;
        return due && !giveUp;
    }
}