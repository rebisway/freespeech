/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;
using FreeSpeech.Ops;

namespace FreeSpeech.Tests;

/// <summary>
/// Writes a server's <c>peers.json</c> before it starts, with servers already accepted, so that a test can begin with servers that are
/// linked (the enrolment itself — add, request, accept — is tested on its own in <c>PeerTests</c> and <c>PeerRelayTests</c>).
/// </summary>
public static class PeerSeed
{
    /// <summary>Accepted peers: kind ("relay" or "push"), address, and their identity when the test holds their key (otherwise a fresh key stands in).</summary>
    public static void Accepted(string stateDir, IEnumerable<(string Kind, string Url, PeerIdentity? Who)> peers)
    {
        Directory.CreateDirectory(stateDir);
        var list = peers.Select((p, i) =>
        {
            var who = p.Who ?? PeerIdentity.LoadOrCreate(Path.Combine(stateDir, $"stand-in-{i}.key"));
            return new Dictionary<string, object?>
            {
                ["id"] = who.Fingerprint, ["kind"] = p.Kind, ["name"] = p.Kind + "-" + i, ["url"] = p.Url, ["public_key"] = who.PublicKey,
                ["state"] = PeerState.Accepted, ["since"] = DateTime.UtcNow, ["sent"] = true,
            };
        }).ToList();
        File.WriteAllText(Path.Combine(stateDir, "peers.json"), JsonSerializer.Serialize(new Dictionary<string, object?> { ["requests_open"] = true, ["peers"] = list }));
    }

    public static void AcceptedPushGateways(string stateDir, params string[] urls) => Accepted(stateDir, urls.Select(u => ("push", u, (PeerIdentity?)null)));
}
