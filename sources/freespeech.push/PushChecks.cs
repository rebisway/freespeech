/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeSpeech.Push;

/// <summary>What the push gateway needs from <c>appsettings.Local.json</c> and from its peers (see <see cref="ConfigCheck"/>).</summary>
public static class PushChecks
{
    public const string Name = "FreeSpeech push gateway";

    /// <param name="relayAccepted">Whether at least one relay is accepted; null when not known, e.g. from the check command without the service's state folder.</param>
    public static IReadOnlyList<ConfigIssue> Issues(IConfiguration configuration, bool? relayAccepted = null)
    {
        var issues = new List<ConfigIssue>();
        if (ConfigCheck.Secret(configuration, "Admin:Secret", "the admin API and the 'admin' command refuse everything") is { } admin) issues.Add(admin);

        var fake = configuration.GetValue<bool>("Push:Fake");
        var fcm = configuration["Fcm:ServiceAccountFile"];
        var apns = configuration["Apns:KeyFile"];
        var fcmOk = !string.IsNullOrWhiteSpace(fcm) && File.Exists(fcm);
        var apnsOk = !string.IsNullOrWhiteSpace(apns) && File.Exists(apns);
        if (!fake && !fcmOk && !apnsOk)
        {
            var why = string.IsNullOrWhiteSpace(fcm) && string.IsNullOrWhiteSpace(apns) ? "is not set" : $"names a file that does not exist ({(string.IsNullOrWhiteSpace(fcm) ? apns : fcm)})";
            issues.Add(new ConfigIssue("Fcm:ServiceAccountFile", $"{why} — no push network is available, every wake-up gets 503", ConfigCheck.HowToSet("Fcm:ServiceAccountFile") + " (path of the Firebase service account JSON)"));
        }
        else if (!fake && !string.IsNullOrWhiteSpace(fcm) && !fcmOk)
            issues.Add(new ConfigIssue("Fcm:ServiceAccountFile", $"names a file that does not exist ({fcm}) — Android wake-ups get 503", ConfigCheck.HowToSet("Fcm:ServiceAccountFile")));

        if (relayAccepted == false && !fake)
            issues.Add(new ConfigIssue("relay", "none accepted yet — every relay request is refused", "link one: 'dotnet FreeSpeech.Push.dll admin add relay <url>', or accept the request of a relay ('admin requests', 'admin accept <id>')"));
        issues.AddRange(MailChecks.Issues(configuration));
        return issues;
    }

    /// <summary>Same, for the <c>check</c> command: looks at the accepted relays when the service's state folder is reachable from here.</summary>
    public static IReadOnlyList<ConfigIssue> IssuesFromDisk(IConfiguration configuration)
    {
        bool? accepted = null;
        if (configuration["Push:StatePath"] is { Length: > 0 } state)
            accepted = new PeerStore(Path.Combine(state, "peers.json"), 1, TimeSpan.FromDays(14), NullLogger<PeerStore>.Instance).Accepted("relay").Count > 0;
        return Issues(configuration, accepted);
    }
}
