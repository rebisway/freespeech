/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Reflection;

namespace FreeSpeech.Ops;

/// <summary>Version and release date of the running program, as written in <c>version.props</c> at build time.</summary>
public static class ReleaseInfo
{
    private static readonly Assembly? Entry = Assembly.GetEntryAssembly();

    public static string? Version { get; } =
        Entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? Entry?.GetName().Version?.ToString();

    /// <summary>The release date (yyyy-MM-dd), or null for a build that does not carry one.</summary>
    public static string? Date { get; } =
        Entry?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "VersionDate")?.Value is { Length: > 0 } d ? d : null;

    /// <summary>"1.1.0 (2026-10-17)".</summary>
    public static string Describe() => Date == null ? Version ?? "?" : $"{Version} ({Date})";
}
