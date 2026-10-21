/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Reflection;

namespace FreeSpeech.Desktop;

/// <summary>
/// Values chosen at build time (sources/Directory.Build.props, overridden by the unversioned sources/freespeech.defaults.props) and stored in
/// the assembly metadata by the csproj: the default relay. No address is written in the source.
/// </summary>
internal static class BuildDefaults
{
	public static string Get(string key) =>
		typeof(BuildDefaults).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";
}
