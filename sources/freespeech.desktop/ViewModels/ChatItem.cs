/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.History;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>One row of the message menu.</summary>
/// <param name="IsReact">Opens the reaction bar instead of running something at once.</param>
public sealed record MenuOption(string Label, string Icon, Func<Task> Run, bool IsReact = false);

/// <summary>One message found by the search in a chat: the words around the hit, who wrote them and when.</summary>
public sealed class SearchResult(FreeSpeechClient.SearchHit hit, string query)
{
	public string MessageId { get; } = hit.Entry.Id;
	public string TimeText { get; } = hit.Entry.At.ToLocalTime().ToString("dd/MM/yy HH:mm");
	public string Author { get; } = hit.Entry.Direction == MessageDirection.Sent ? Loc.T("search_you") : hit.Conversation.Group != null ? hit.AuthorName : "";
	public string Snippet { get; } = Build(hit.Entry.Text, query);

	private static string Build(string full, string query)
	{
		var text = full.ReplaceLineEndings(" ");
		var at = System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query.Trim(), System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
		if (at > 40) text = "…" + text[(at - 40)..];
		return text.Length > 140 ? text[..140] + "…" : text;
	}
}

/// <summary>A file picked for sending, waiting under the composer. A picture shows as a thumbnail, anything else as its name.</summary>
public sealed class PendingFile(string path, string name)
{
	private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];

	public string Path { get; } = path;
	public string Name { get; } = name;
	public bool IsImage => ImageExtensions.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
	public bool IsOther => !IsImage;
	public Bitmap? Thumb { get; } = IsImagePath(path) ? ChatItem.Decode(path, 120) : null;

	private static bool IsImagePath(string p) => ImageExtensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase);
}

/// <summary>One line of the chat as the screen shows it. Rebuilt from the stored message whenever it changes.</summary>
public sealed partial class ChatItem : ObservableObject
{
	private static readonly Dictionary<string, Bitmap?> Pictures = [];

	[ObservableProperty] private bool _found;

	public required string Id { get; init; }
	public required bool IsMine { get; init; }
	public required string Author { get; init; }
	public required string Text { get; init; }
	public required string Time { get; init; }
	public required MessageState State { get; init; }
	public DateTime At { get; init; }
	public bool Edited { get; init; }
	public bool IsFile { get; init; }

	/// <summary>A file message whose record was not stored yet when the line was built (shown as "Photo"): rebuilt a moment later.</summary>
	public bool NeedsRefresh { get; init; }
	public string FileStatus { get; init; } = "";

	/// <summary>State of the attached file, shown as a small coloured dot: red not downloaded, orange downloading, green downloaded.</summary>
	public AttachmentState? FileState { get; init; }
	public IBrush FileDot => new SolidColorBrush(Color.Parse(FileState switch
	{
		AttachmentState.Complete => "#4CAF50",
		AttachmentState.Receiving => "#FFA726",
		AttachmentState.Removed => "#8A8A8A",
		_ => "#E5534B",
	}));

	public string? ReplyPreview { get; init; }
	public Bitmap? ReplyThumb { get; init; }
	public bool ShowAuthor { get; init; }

	/// <summary>Group message from someone else: the sender's picture (base64), shown tiny before the name.</summary>
	public string? AuthorPhoto { get; init; }

	/// <summary>Group lines: who wrote it, and how far I can vouch for them (0 = not my contact, 1 = a contact, 2 = a contact whose safety number I compared).</summary>
	public string AuthorId { get; init; } = "";
	public int AuthorTrust { get; init; }
	public bool AuthorIsContact => AuthorTrust == 1;
	public bool AuthorIsVerified => AuthorTrust == 2;

	/// <summary>Local path of a downloaded picture, shown as a thumbnail in the bubble.</summary>
	public string? ImagePath { get; init; }
	public Bitmap? Image { get; init; }

	/// <summary>The line "N unread messages" inserted before the first message not read yet; no bubble.</summary>
	public bool IsSeparator { get; init; }

	/// <summary>My message, and the contact has read it.</summary>
	public bool ReadByPeer { get; init; }

	/// <summary>Emojis people put on this message ("👍 2 ❤️": a number when several chose the same one).</summary>
	public string ReactionsText { get; init; } = "";
	public bool HasReactions => ReactionsText.Length > 0;

	/// <summary>A downloaded video or voice message: opened in the system's player.</summary>
	public string? VideoPath { get; init; }
	public string VideoName { get; init; } = "";
	public string? AudioPath { get; init; }
	public bool IsVideo => VideoPath != null;
	public bool IsAudio => AudioPath != null;
	public string MediaLabel => IsAudio ? "▶  " + Loc.T("audio_message") : "▶  " + VideoName;

	public bool IsBubble => !IsSeparator;
	public bool HasImage => Image != null;

	/// <summary>A picture sits in an even margin inside the edge of its bubble; text keeps the wider side margin.</summary>
	public Avalonia.Thickness BubblePadding => HasImage ? new Avalonia.Thickness(8) : new Avalonia.Thickness(12, 7);
	public bool ShowTicks => IsMine && !IsSeparator && !IsDeleted;
	public string Ticks => ReadByPeer ? "✓✓" : "✓";
	public bool IsDeleted => State is MessageState.Retracted or MessageState.FileRetracted;
	public bool HasReply => !string.IsNullOrEmpty(ReplyPreview);
	public bool HasReplyThumb => ReplyThumb != null;
	public bool ShowText => !HasImage && !IsVideo && !IsAudio;
	public bool HasFileStatus => FileStatus.Length > 0 && !HasImage && !IsVideo && !IsAudio;
	public HorizontalAlignment Align => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
	public double Opacity => IsDeleted ? 0.7 : 1;
	public FontStyle TextStyle => IsDeleted ? FontStyle.Italic : FontStyle.Normal;

	/// <summary>A message made only of emojis (a few at most) is shown a little larger (24, 20 or 17 against the usual 12).</summary>
	public double TextSize
	{
		get
		{
			var s = Text.Trim();
			if (IsDeleted || IsFile || s.Length == 0 || s.Length > 30) return 12;
			var elements = new System.Globalization.StringInfo(s).LengthInTextElements;
			if (elements > 6) return 12;
			foreach (var rune in s.EnumerateRunes())
			{
				var v = rune.Value;
				if (System.Text.Rune.IsWhiteSpace(rune)) continue;
				var emoji = v >= 0x1F000 || (v >= 0x2190 && v <= 0x2BFF) || v is 0x200D or 0xFE0F or 0x20E3 or 0x00A9 or 0x00AE or 0x203C or 0x2049 || (v >= 0xE0020 && v <= 0xE007F);
				if (!emoji) return 12;
			}
			return elements <= 1 ? 24 : elements <= 3 ? 20 : 17;
		}
	}

	/// <summary>The text with its status: a deleted message keeps a line.</summary>
	public string Display => State switch
	{
		MessageState.Retracted => Loc.T("chat_deleted"),
		MessageState.FileRetracted => Loc.T("chat_file_retracted"),
		_ => Text,
	};

	public string EditedLabel => Edited ? Loc.T("chat_edited") : "";
	public bool ShowEdited => Edited && !IsDeleted;

	/// <summary>A picture of the disk, reduced to this width (kept: a rebuilt line does not read the file again).</summary>
	public static Bitmap? Decode(string path, int width)
	{
		var key = $"{path}|{width}";
		if (Pictures.TryGetValue(key, out var cached)) return cached;
		Bitmap? bitmap = null;
		try { using var stream = File.OpenRead(path); bitmap = Bitmap.DecodeToWidth(stream, width); }
		catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { /* not a picture: the file line shows */ }
		if (Pictures.Count > 200) Pictures.Clear();
		Pictures[key] = bitmap;
		return bitmap;
	}
}
