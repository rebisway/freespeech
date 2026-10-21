/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace FreeSpeech.Ops;

/// <summary>
/// Signed calls between servers. A call carries three headers: <c>X-Peer-Id</c> (the caller's fingerprint), <c>X-Peer-Ts</c> (unix
/// seconds) and <c>X-Peer-Sig</c> (signature of "METHOD\nPATH\nTS\nSHA-256(body)"). The receiver checks that the caller is a peer it
/// accepted, that the time is close to its own, that the signature is good for the pinned key and that it has not seen it before.
/// </summary>
public static class PeerSigning
{
    public const string IdHeader = "X-Peer-Id";
    public const string TsHeader = "X-Peer-Ts";
    public const string SigHeader = "X-Peer-Sig";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private static readonly ConcurrentDictionary<string, DateTime> Seen = new();

    public static string Canonical(string method, string path, long ts, byte[] body) =>
        $"{method.ToUpperInvariant()}\n{path}\n{ts}\n{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()}";

    /// <summary>Adds the three headers to an outgoing call. <paramref name="path"/> is the path as the receiver sees it (e.g. "/push").</summary>
    public static void Sign(HttpRequestMessage request, PeerIdentity identity, string path, byte[] body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        request.Headers.Add(IdHeader, identity.Fingerprint);
        request.Headers.Add(TsHeader, ts.ToString());
        request.Headers.Add(SigHeader, identity.Sign(Canonical(request.Method.Method, path, ts, body)));
    }

    public enum Outcome { Ok, NoSignature, UnknownPeer, NotAccepted, Banned, BadTime, BadSignature, Replay }

    /// <summary>
    /// Checks the signature of an incoming call against <paramref name="store"/>. With <paramref name="anyState"/> a peer that is only
    /// waiting (it asked me) is recognised too — enough to answer "where does my request stand?", never to serve it.
    /// The body must be readable more than once (the caller enabled buffering).
    /// </summary>
    public static async Task<(Outcome Outcome, Peer? Peer)> VerifyAsync(HttpRequest request, PeerStore store, string expectedKind, bool anyState = false)
    {
        var id = request.Headers[IdHeader].ToString();
        var tsText = request.Headers[TsHeader].ToString();
        var signature = request.Headers[SigHeader].ToString();
        if (id.Length == 0 || tsText.Length == 0 || signature.Length == 0) return (Outcome.NoSignature, null);

        if (!long.TryParse(tsText, out var ts) || Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts)).TotalSeconds) > Window.TotalSeconds)
            return (Outcome.BadTime, null);

        var peer = store.Find(id);
        if (peer == null || peer.Kind != expectedKind) return (Outcome.UnknownPeer, null);
        if (peer.State == PeerState.Banned) return (Outcome.Banned, peer);
        if (peer.State != PeerState.Accepted && !(anyState && peer.State == PeerState.PendingIn)) return (Outcome.NotAccepted, peer);

        using var buffer = new MemoryStream();
        request.Body.Position = 0;
        await request.Body.CopyToAsync(buffer);
        request.Body.Position = 0;

        if (!PeerIdentity.Verify(peer.PublicKey, Canonical(request.Method, request.Path.Value ?? "", ts, buffer.ToArray()), signature)) return (Outcome.BadSignature, peer);

        // Each signature is good once: a captured call cannot be played again inside the time window.
        var now = DateTime.UtcNow;
        if (!Seen.TryAdd(peer.Id + ":" + signature, now + Window + Window)) return (Outcome.Replay, peer);
        if (Seen.Count > 20_000) foreach (var (key, until) in Seen) if (until < now) Seen.TryRemove(key, out _);
        return (Outcome.Ok, peer);
    }

    /// <summary>The text a server signs when it asks another one for a link (the key it presents must be the one that signed).</summary>
    public static string RequestText(string kind, string? name, string url, long ts, string? contact = null) =>
        $"peer-request\n{kind}\n{name}\n{url}\n{ts}" + (string.IsNullOrEmpty(contact) ? "" : "\n" + contact);
}
