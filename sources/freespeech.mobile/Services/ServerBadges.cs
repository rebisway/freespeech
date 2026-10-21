/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;

namespace FreeSpeech.Mobile.Services;

/// <summary>One server as the top bar shows it: a small symbol (image name) and the sentence that explains it.</summary>
public sealed record ServerBadge(string Url, string Icon, string TextKey);

/// <summary>
/// What kind of server each of my servers is and where I stand on it — so nothing is hidden from the user: a private server (it only
/// takes people its administrator allowed) or a public one; connected, waiting for the administrator to approve me, not allowed, or offline.
/// The kind is told by the server when connected; it is remembered, so an offline server keeps its symbol.
/// </summary>
public static class ServerBadges
{
	public static ServerBadge Of(ServerStatus s)
	{
		var key = "srv_kind_" + s.Server.Url;
		var kind = s.Refusal is "private_server" or "access_pending" ? "allowlist" : s.Access;
		if (kind != null) Remember(key, kind); else kind = Recall(key);
		var isPrivate = kind == "allowlist";
		var url = s.Server.Url;

		if (s.Connected) return isPrivate ? new(url, "ic_srv_private_ok", "srv_private_online") : new(url, "ic_srv_public_ok", "srv_public_online");
		return s.Refusal switch
		{
			"access_pending" => new(url, "ic_srv_pending", "srv_private_pending"),
			"private_server" => new(url, "ic_srv_private_bad", "srv_private_denied"),
			"access_refused" => new(url, "ic_srv_private_bad", "srv_refused"),
			_ => isPrivate ? new(url, "ic_srv_private_off", "srv_private_offline")
				: kind == null ? new(url, "ic_srv_public_off", "srv_offline") : new(url, "ic_srv_public_off", "srv_public_offline"),
		};
	}

	/// <summary>
	/// The one badge the top bar shows for all my servers: what needs my attention first (an approval waiting, a refusal), then a server
	/// that is offline, otherwise the main server (the first of the list) in its normal state. Null when there is no server.
	/// </summary>
	public static ServerBadge? Summary(IReadOnlyList<ServerBadge> badges) =>
		badges.FirstOrDefault(b => b.TextKey == "srv_private_pending")
		?? badges.FirstOrDefault(b => b.TextKey is "srv_private_denied" or "srv_refused")
		?? badges.FirstOrDefault(b => b.Icon.EndsWith("_off", StringComparison.Ordinal))
		?? badges.FirstOrDefault();

	private static void Remember(string key, string kind)
	{
		try { Preferences.Default.Set(key, kind); } catch { /* a convenience only */ }
	}

	private static string? Recall(string key)
	{
		try { var v = Preferences.Default.Get(key, ""); return v.Length == 0 ? null : v; }
		catch { return null; }
	}
}
