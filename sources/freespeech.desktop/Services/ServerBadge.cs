/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;

namespace FreeSpeech.Desktop.Services;

/// <summary>One server as the title bar shows it: the symbol (lock, globe or hourglass), its colour, whether it is struck out, and the sentence that explains it.</summary>
public sealed record ServerBadge(string Url, string Glyph, string Color, bool Slash, string TextKey);

/// <summary>
/// What kind of server each of my servers is and where I stand on it, as on the mobile app: a private server (it only takes people its administrator
/// allowed) or a public one; connected, waiting for the administrator to approve me, not allowed, or offline. The kind is told by the server when
/// connected; it is remembered, so an offline server keeps its symbol.
/// </summary>
public static class ServerBadges
{
	private const string Gold = "#E6C98F", Amber = "#C89A4B", Red = "#C4584B", Grey = "#8A8076";

	public static ServerBadge Of(ServerStatus s)
	{
		var key = "srv_kind_" + s.Server.Url;
		var kind = s.Refusal is "private_server" or "access_pending" ? "allowlist" : s.Access;
		if (kind != null) Prefs.Set(key, kind); else kind = Prefs.Get<string?>(key, null);
		var isPrivate = kind == "allowlist";
		var url = s.Server.Url;
		var glyph = isPrivate ? "Srv.lock" : "Srv.globe";

		if (s.Connected) return new(url, glyph, Gold, false, isPrivate ? "srv_private_online" : "srv_public_online");
		return s.Refusal switch
		{
			"access_pending" => new(url, "Srv.hourglass", Amber, false, "srv_private_pending"),
			"private_server" => new(url, "Srv.lock", Red, false, "srv_private_denied"),
			"access_refused" => new(url, "Srv.lock", Red, false, "srv_refused"),
			_ => isPrivate ? new(url, glyph, Grey, true, "srv_private_offline") : new(url, glyph, Grey, true, kind == null ? "srv_offline" : "srv_public_offline"),
		};
	}

	/// <summary>The one badge the title bar shows for all my servers: what needs attention first (an approval waiting, a refusal), then an offline server, otherwise the main one.</summary>
	public static ServerBadge? Summary(IReadOnlyList<ServerBadge> badges) =>
		badges.FirstOrDefault(b => b.TextKey == "srv_private_pending")
		?? badges.FirstOrDefault(b => b.TextKey is "srv_private_denied" or "srv_refused")
		?? badges.FirstOrDefault(b => b.Slash)
		?? badges.FirstOrDefault();
}