/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Diagnostics;
using System.Net;
using Avalonia.Media.Imaging;
using FreeSpeech.Client.Identity;
using QRCoder;
using ZXing;
using ZXing.Common;

namespace FreeSpeech.Desktop.Services;

/// <summary>
/// The printed sheet of the three recovery phrases, with a QR code that holds them. It is drawn in the browser from a page served once
/// on the loopback address, so the phrases never touch the disk; and read back from a picture of the sheet.
/// </summary>
public static class PhraseSheetPaper
{
	/// <summary>Black on white, with a quiet zone: gold on dark would be unreadable.</summary>
	public static byte[] RenderQr(string text)
	{
		using var generator = new QRCodeGenerator();
		using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
		return new PngByteQRCode(data).GetGraphic(8);
	}

	/// <summary>Opens the sheet in the default browser, which prints it. The page is served to that one request only. False when it could not be shown.</summary>
	public static async Task<bool> PrintAsync(string[] phrases)
	{
		var qr = Convert.ToBase64String(RenderQr(PhraseSheet.Encode(phrases)));
		var items = string.Concat(phrases.Select((p, i) => $"<p><b>{i + 1}.</b> {WebUtility.HtmlEncode(p)}</p>"));
		var html = $"<html><head><meta charset=\"utf-8\"><title>FreeSpeech</title></head><body style=\"font-family:sans-serif;margin:32px\"><h2>{WebUtility.HtmlEncode(Loc.T("ph_print_title"))}</h2>{items}"
			+ $"<p style=\"margin-top:28px\"><img src=\"data:image/png;base64,{qr}\" width=\"220\" height=\"220\"/></p>"
			+ $"<p style=\"color:#a33;margin-top:20px\"><b>{WebUtility.HtmlEncode(Loc.T("ph_print_warn"))}</b></p>"
			+ "<script>window.onload=function(){setTimeout(function(){window.print();},300);};</script></body></html>";
		try
		{
			var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
			using var listener = new HttpListener();
			var port = FreePort();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");
			listener.Start();
			Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/{token}/") { UseShellExecute = true });
			var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(2));
			var bytes = System.Text.Encoding.UTF8.GetBytes(html);
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.Headers["Cache-Control"] = "no-store";
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
			return true;
		}
		catch (Exception ex) when (ex is HttpListenerException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
	}

	/// <summary>Reads the three phrases back from a picture of the printed sheet, or null when it is not a FreeSpeech sheet.</summary>
	public static string[]? ReadPicture(string path) => ReadQr(path) is { } text ? PhraseSheet.TryDecode(text) : null;

	/// <summary>The text of the QR code in a picture (a photo or a scan), or null when there is none.</summary>
	public static string? ReadQr(string path)
	{
		try
		{
			using var bitmap = new Bitmap(path);
			var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
			var pixels = new byte[width * height * 4];
			var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
			try { bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4); }
			finally { handle.Free(); }
			var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
			var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] } };
			return reader.Decode(source)?.Text;
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return null; }
	}

	private static int FreePort()
	{
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}
}
