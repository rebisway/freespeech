/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;

namespace FreeSpeech.Relay;

/// <summary>Where the relay keeps its state, for <c>export</c> and <c>import</c> (same defaults as the services that write these files).</summary>
public static class ServerState
{
    /// <summary>Folder of the identity and of the links with other servers: next to <c>access.json</c> unless <c>Peer:StatePath</c> says otherwise.</summary>
    public static string PeerDirectory(IConfiguration c) =>
        Path.GetFullPath(c["Peer:StatePath"] ?? Path.GetDirectoryName(Path.GetFullPath(c["Server:AccessFilePath"] ?? "access.json"))!);

    public static StateFiles Files(IConfiguration c) => new("relay",
    [
        new("siblings.json", Path.GetFullPath(c["Server:SiblingsFilePath"] ?? "siblings.json")),
        new("access.json", Path.GetFullPath(c["Server:AccessFilePath"] ?? "access.json")),
        new("settings.json", Path.GetFullPath(c["Server:SettingsFilePath"] is { Length: > 0 } settings ? settings : "settings.json")),
        new("limits.json", Path.GetFullPath(c["Server:LimitsFilePath"] is { Length: > 0 } limits ? limits : "limits.json")),
        new("peers.json", Path.Combine(PeerDirectory(c), "peers.json")),
        new("peer-identity.json", Path.Combine(PeerDirectory(c), "peer-identity.json")),
    ]);
}
