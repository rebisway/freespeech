/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeSpeech.Directory;

/// <summary>What the directory needs from <c>appsettings.Local.json</c> and from its peers (see <see cref="ConfigCheck"/>).</summary>
public static class DirectoryChecks
{
    public const string Name = "FreeSpeech directory";

    /// <param name="relayAccepted">Whether at least one relay is accepted; null when not known.</param>
    public static IReadOnlyList<ConfigIssue> Issues(IConfiguration configuration, bool? relayAccepted = null)
    {
        var issues = new List<ConfigIssue>();
        if (ConfigCheck.Secret(configuration, "Admin:Secret", "the admin API and the 'admin' command refuse everything") is { } admin) issues.Add(admin);
        if (string.IsNullOrWhiteSpace(configuration["Peer:PublicUrl"]))
            issues.Add(new ConfigIssue("Peer:PublicUrl", "is not set — relays cannot check who asks them for a link", ConfigCheck.HowToSet("Peer:PublicUrl") + " (the https address of this directory)"));
        if (relayAccepted == false)
            issues.Add(new ConfigIssue("relay", "none accepted yet — the directory has nothing to offer", "accept the request of a relay ('admin requests', 'admin accept <id>'), or ask one: 'admin add relay <url>'"));
        issues.AddRange(MailChecks.Issues(configuration));
        return issues;
    }

    /// <summary>Same, for the <c>check</c> command: looks at the accepted relays when the state folder is reachable from here.</summary>
    public static IReadOnlyList<ConfigIssue> IssuesFromDisk(IConfiguration configuration)
    {
        bool? accepted = null;
        if (configuration["Directory:StatePath"] is { Length: > 0 })
            accepted = new PeerStore(Path.Combine(DirectoryState.PeerDirectory(configuration), "peers.json"), 1, TimeSpan.FromDays(14), NullLogger<PeerStore>.Instance).Accepted("relay").Count > 0;
        return Issues(configuration, accepted);
    }
}
