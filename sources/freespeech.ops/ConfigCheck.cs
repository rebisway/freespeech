/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FreeSpeech.Ops;

/// <summary>One thing the installation still lacks (a secret, a credentials file, a first account) and how to fix it.</summary>
public sealed record ConfigIssue(string Key, string Problem, string Fix);

/// <summary>
/// Every FreeSpeech server takes its secrets from ONE place: <c>appsettings.Local.json</c>, next to the program, outside the repository
/// and never part of a publication (so an update cannot overwrite it). This is the alarm when something is missing: it is logged at
/// start-up at the highest level (whatever the log level of the installation) and the <c>check</c> command of each server prints it
/// and exits with 1, which the update scripts run after every update.
/// </summary>
public static class ConfigCheck
{
    public const string LocalFile = "appsettings.Local.json";
    public const int MinSecretLength = 16;

    /// <summary>The configuration of an installed server as the server itself sees it (files next to the program, then environment).</summary>
    public static IConfiguration Load() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Production.json", optional: true)
        .AddJsonFile(LocalFile, optional: true)
        .AddEnvironmentVariables()
        .Build();

    /// <summary>The standard way to say how to fix a missing setting.</summary>
    public static string HowToSet(string key) =>
        $"set \"{key}\" in {Path.Combine(AppContext.BaseDirectory, LocalFile)} (examples: appsettings.Local.<service>.example.json in the deploy folder of the sources); a secret: openssl rand -hex 32";

    /// <summary>A required secret: present, and long enough not to be guessed.</summary>
    public static ConfigIssue? Secret(IConfiguration configuration, string key, string consequence)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return new ConfigIssue(key, $"is not set — {consequence}", HowToSet(key));
        // The example files hold placeholders such as "<openssl rand -hex 32>": copied as they are, they must not pass for a secret.
        if (value.StartsWith('<') || value.Contains("change-me", StringComparison.OrdinalIgnoreCase))
            return new ConfigIssue(key, $"still has the example value — {consequence}", HowToSet(key));
        if (value.Length < MinSecretLength) return new ConfigIssue(key, $"is too short ({value.Length} characters, at least {MinSecretLength}) — {consequence}", HowToSet(key));
        return null;
    }

    /// <summary>Prints the verdict of the <c>check</c> command. 0 when nothing is missing, 1 otherwise.</summary>
    public static int Run(string appName, IReadOnlyList<ConfigIssue> issues)
    {
        Console.WriteLine($"{appName} — configuration ({Path.Combine(AppContext.BaseDirectory, LocalFile)}{(File.Exists(Path.Combine(AppContext.BaseDirectory, LocalFile)) ? "" : ", file absent")})");
        if (issues.Count == 0) { Console.WriteLine("  [ ok ] nothing missing"); return 0; }
        foreach (var issue in issues)
        {
            Console.WriteLine($"  [ !! ] {issue.Key} {issue.Problem}");
            Console.WriteLine($"         fix: {issue.Fix}");
        }
        Console.WriteLine($"  {issues.Count} problem(s): this installation is NOT ready.");
        return 1;
    }

    /// <summary>The same verdict in the server's log: Critical, so no log level of the installation can hide it.</summary>
    public static void LogAtStartup(ILogger logger, string appName, IReadOnlyList<ConfigIssue> issues)
    {
        foreach (var issue in issues) logger.LogCritical("{App} NOT CONFIGURED: {Key} {Problem}. To fix: {Fix}", appName, issue.Key, issue.Problem, issue.Fix);
    }
}
