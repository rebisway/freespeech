/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Ops;

namespace FreeSpeech.Relay;

/// <summary>What the relay server needs from <c>appsettings.Local.json</c> (see <see cref="ConfigCheck"/>).</summary>
public static class ServerChecks
{
    public const string Name = "FreeSpeech relay server";

    public static IReadOnlyList<ConfigIssue> Issues(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        if (ConfigCheck.Secret(configuration, "Admin:Secret", "the admin API and the 'admin' command refuse everything") is { } admin) issues.Add(admin);
        issues.AddRange(MailChecks.Issues(configuration));
        return issues;
    }
}
