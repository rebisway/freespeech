/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using QRCoder;

namespace FreeSpeech.Mobile.Services;

/// <summary>QR helpers: drawing a card as a QR code, and handing the text read by the scanner page back to the invite page.</summary>
public static class ScanHub
{
	/// <summary>Text read by the scanner, consumed once by the invite page when it reappears.</summary>
	public static string? Result { get; set; }

	/// <summary>Black on white, with a quiet zone: gold on dark would be unreadable for a camera.</summary>
	public static byte[] Render(string text)
	{
		using var generator = new QRCodeGenerator();
		using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
		return new PngByteQRCode(data).GetGraphic(8);
	}
}
