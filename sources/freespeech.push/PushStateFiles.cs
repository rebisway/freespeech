/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;

namespace FreeSpeech.Push;

/// <summary>Where the push gateway keeps its state, for <c>export</c> and <c>import</c> (same defaults as the code that writes these files).</summary>
public static class PushStateFiles
{
    public static string StateDirectory(IConfiguration c) => Path.GetFullPath(c["Push:StatePath"] ?? "push-state");

    /// <summary>Folder of the identity and of the links with relays: the state folder unless <c>Peer:StatePath</c> says otherwise.</summary>
    public static string PeerDirectory(IConfiguration c) => Path.GetFullPath(c["Peer:StatePath"] ?? c["Push:StatePath"] ?? "push-state");

    public static StateFiles Files(IConfiguration c) => new("push",
    [
        new("settings.json", Path.Combine(StateDirectory(c), "settings.json")),
        new("peers.json", Path.Combine(PeerDirectory(c), "peers.json")),
        new("peer-identity.json", Path.Combine(PeerDirectory(c), "peer-identity.json")),
    ]);
}
