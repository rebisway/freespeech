/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Groups;
using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.History;

namespace FreeSpeech.Client;

/// <summary>
/// A failure the library explains with a stable <see cref="Code"/> (and the values to put in the sentence), so an app can show it
/// in the user's language. <see cref="Exception.Message"/> stays the English sentence, for the console, logs and tests.
/// </summary>
public interface ICodedMessage
{
    string Code { get; }
    object[] Args { get; }
}

/// <summary>A user-facing failure (unknown contact, bad argument...). The message is English; apps translate by <see cref="Code"/> when there is one.</summary>
public sealed class ClientException : Exception, ICodedMessage
{
    public ClientException(string message) : base(message) { }
    public ClientException(string message, string code, params object[] args) : base(message) { Code = code; Args = args; }

    public string Code { get; } = "";
    public object[] Args { get; } = [];
}

/// <summary>A refused action in a group (not allowed, not a member...): <see cref="InvalidOperationException"/> carrying a code, like <see cref="ClientException"/>.</summary>
public sealed class CodedInvalidOperationException(string message, string code, params object[] args) : InvalidOperationException(message), ICodedMessage
{
    public string Code { get; } = code;
    public object[] Args { get; } = args;
}

public sealed class ClientOptions
{
    /// <summary>Directory holding every local file of this profile (identity, contacts, history, files, groups...).</summary>
    public required string ProfileDir { get; init; }

    /// <summary>Server to connect to for this run even if the identity does not advertise it (also seeds a new identity).</summary>
    public required string ServerUrl { get; init; }

    /// <summary>The directories (address and public key) the application knows, asked for relays by a new identity and when no relay can be reached. Empty: none.</summary>
    public IReadOnlyList<Discovery.DirectoryEntry> Directories { get; init; } = [];

    /// <summary>How long the restricted servers shared with a contact must have been unreachable before an open server may be used (when both sides agreed).</summary>
    public TimeSpan OpenFallbackDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Built-in relays a new identity takes (one, at random) when no directory answers; set by the application only when the user chose no server.</summary>
    public IReadOnlyList<string> FallbackServers { get; init; } = [];

    /// <summary>ISO 3166 alpha-2 country of the device (its region), told to the directories to offer relays of the same country first.</summary>
    public string? Country { get; init; }

    /// <summary>Where the database key is kept between runs (DPAPI, Keychain, Keystore...). Default: a plain file — no protection, tests only.</summary>
    public Storage.IKeyCache? KeyCache { get; init; }

    /// <summary>Test aid: slows the serving side so a transfer can be interrupted mid-file on localhost.</summary>
    public TimeSpan ChunkDelay { get; init; }

    /// <summary>
    /// How long a letter the relay called delivered waits for the contact's acknowledgement before it is sent again, for the first, second... resend; after the last
    /// one it is dropped. Default: 30 seconds, 2 minutes, 10 minutes, 1 hour, 6 hours. Tests shorten it.
    /// </summary>
    public TimeSpan[]? AckBackoff { get; init; }

    /// <summary>How long a delivered letter may stay unconfirmed before the WAITING flag is raised for its contact (default 3 s: a live contact confirms within a fraction of a second). Tests shorten it.</summary>
    public TimeSpan? UnconfirmedFlagDelay { get; init; }

    /// <summary>Test aid: when false the client behaves like a version that does not acknowledge letters.</summary>
    public bool SendAcks { get; init; } = true;
}

/// <summary>A conversation: a contact (<see cref="ContactId"/>) or a group (<see cref="Group"/>). <see cref="HistoryKey"/> indexes the local history.</summary>
public sealed record Conversation(string HistoryKey, string? ContactId, GroupRecord? Group);

/// <summary>A message or file offer that arrived (or was synced from another device of ours).</summary>
/// <param name="ReplyIndex">null = not a reply; 0 = a reply to a message absent from the local history; n = history index of the target.</param>
/// <param name="HistoryIndex">1-based position in the conversation history, usable with download/reply/forward.</param>
public sealed record ChatMessage(
    Conversation Conversation, string FromId, string FromName, bool Mine, bool FromOtherDevice,
    bool IsFile, string Text, string? ReplyToId, int? ReplyIndex, int HistoryIndex);

public enum SendStatus
{
    Delivered,
    /// <summary>A server is reachable but the recipient is not connected: queued in the outbox, WAITING flag set.</summary>
    QueuedRecipientOffline,
    /// <summary>No server reachable: queued in the outbox.</summary>
    QueuedNoServer,
}

/// <summary>Result of sending to a contact (<see cref="Status"/>) or to a group (<see cref="Delivered"/>/<see cref="Recipients"/> live deliveries).</summary>
public sealed record SentMessage(Conversation To, string Text, long Ts, bool IsFile, SendStatus Status, int Delivered, int Recipients);

/// <param name="Refusal">"private_server" (an invitation from the administrator is needed, or ask for access), "access_pending" (asked, waiting for the administrator) or "access_refused" (banned) when the server turned this identity away; otherwise null.</param>
/// <param name="Access">"open" (anyone not banned may register: a public server) or "allowlist" (a private server), as the server said when connected; null if unknown yet.</param>
public sealed record ServerStatus(ServerEntry Server, bool Connected, string? Refusal = null, string? Access = null);

/// <summary>One history line; <see cref="ReplyIndex"/> as in <see cref="ChatMessage"/>.</summary>
public sealed record HistoryLine(int Index, HistoryEntry Entry, int? ReplyIndex);

/// <summary>Outcome of one HTTP call to a server's flag endpoint.</summary>
public sealed record FlagResult(string Url, bool Ok, string? Error);

public sealed record FlagInfo(string Url, string From, string To, string State, DateTime? NextConnectAt);
