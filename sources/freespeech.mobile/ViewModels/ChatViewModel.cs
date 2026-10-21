/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.History;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.ViewModels;

/// <summary>One row of the message menu.</summary>
/// <param name="LeavesViewer">Chosen from the picture viewer, this action takes the user back to the chat (reply).</param>
/// <param name="IsReact">Opens the horizontal reaction bar instead of running something at once.</param>
public sealed record MenuOption(string Label, string Icon, Func<Task> Run, bool LeavesViewer = false, bool IsReact = false);

/// <summary>A picture of the conversation and the message it belongs to: the viewer offers that message's actions on it.</summary>
public sealed record GalleryPhoto(string Path, string MessageId);

/// <summary>A file picked for sending, waiting under the composer. A picture shows as a thumbnail, anything else as its name.</summary>
public sealed record PendingFile(string Path, string Name)
{
	private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];

	public bool IsImage => ImageExtensions.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase);
	public bool IsOther => !IsImage;
}

/// <summary>One message found by the search in a chat: the words around the hit, who wrote them and when.</summary>
public sealed class SearchResult(FreeSpeechClient.SearchHit hit, string query)
{
	public string MessageId { get; } = hit.Entry.Id;
	public string TimeText { get; } = hit.Entry.At.ToLocalTime().ToString("dd/MM/yy HH:mm");
	public string Author { get; } = hit.Entry.Direction == FreeSpeech.Client.History.MessageDirection.Sent ? Loc.T("search_you") : hit.Conversation.Group != null ? hit.AuthorName : "";
	public string Snippet { get; } = Build(hit.Entry.Text, query);

	private static string Build(string full, string query)
	{
		var text = full.ReplaceLineEndings(" ");
		var at = System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query.Trim(), System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
		if (at > 40) text = "…" + text[(at - 40)..];
		return text.Length > 140 ? text[..140] + "…" : text;
	}
}

/// <summary>One line of the chat as the screen shows it. Rebuilt from the stored message whenever it changes.</summary>
public sealed class ChatItem : System.ComponentModel.INotifyPropertyChanged
{
	public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

	/// <summary>A downloaded voice message: a play button and its length in the bubble.</summary>
	public string? AudioPath { get; init; }
	public bool IsAudio => AudioPath != null;
	public TimeSpan? AudioLength { get; init; }

	private bool _playing;
	public bool Playing
	{
		get => _playing;
		set { _playing = value; PropertyChanged?.Invoke(this, new(nameof(AudioGlyph))); }
	}
	public string AudioGlyph => Playing ? "ic_pause.png" : "ic_play.png";
	public string AudioLabel => Loc.T("audio_message") + (AudioLength is { } l ? $"  {(int)l.TotalMinutes}:{l.Seconds:00}" : "");

	private static Color Res(string key) => (Color)Application.Current!.Resources[key];   // the palette, dark or light
	private static Color Mine => Res("BubbleMine");
	private static Color Theirs => Res("BubbleTheirs");

	public required string Id { get; init; }
	public required bool IsMine { get; init; }
	public required string Author { get; init; }
	public required string Text { get; init; }
	public required string Time { get; init; }
	public required MessageState State { get; init; }
	public bool Edited { get; init; }
	public bool IsFile { get; init; }
	public string FileStatus { get; init; } = "";

	/// <summary>State of the attached file, shown as a small coloured dot: red not downloaded, orange downloading, green downloaded.</summary>
	public AttachmentState? FileState { get; init; }
	public Color FileDotColor => FileState switch
	{
		AttachmentState.Complete => Color.FromArgb("#4CAF50"),
		AttachmentState.Receiving => Color.FromArgb("#FFA726"),
		AttachmentState.Removed or AttachmentState.Archived => Color.FromArgb("#8A8A8A"),
		_ => Color.FromArgb("#E5534B"),
	};
	public string? ReplyPreview { get; init; }

	/// <summary>When the message answered is a picture or a video: a small thumbnail instead of its file name.</summary>
	public string? ReplyThumb { get; init; }
	public bool HasReplyThumb => ReplyThumb != null;
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

	/// <summary>The small JPEG kept in the database for this picture. The bubble draws it instead of decoding the full file; the full file opens on a tap.</summary>
	public byte[]? ImageThumb { get; init; }

	private ImageSource? _imageSource;

	/// <summary>What the bubble shows: the kept thumbnail, or the file itself when there is none (an animated GIF, a picture that could not be read).</summary>
	public ImageSource? ImageSource => _imageSource ??= ImageThumb != null ? ImageSource.FromStream(() => new MemoryStream(ImageThumb)) : ImagePath != null ? ImageSource.FromFile(ImagePath) : null;

	/// <summary>The line "N unread messages" inserted before the first message not read yet; no bubble.</summary>
	public bool IsSeparator { get; init; }

	/// <summary>My message, and the contact has read it.</summary>
	public bool ReadByPeer { get; init; }

	/// <summary>Emojis people put on this message ("👍 2 ❤️": a number when several chose the same one).</summary>
	public string ReactionsText { get; init; } = "";
	public bool HasReactions => ReactionsText.Length > 0;

	/// <summary>A downloaded video: a play line in the bubble, the player opens inside the app.</summary>
	public string? VideoPath { get; init; }
	public bool IsVideo => VideoPath != null;
	public string VideoName { get; init; } = "";
	public string VideoLabel => "▶  " + VideoName;

	/// <summary>First frame of the video, shown in the bubble with a play button; null when it cannot be read (the play line shows instead).</summary>
	public string? VideoThumb { get; init; }
	public bool HasVideoThumb => VideoThumbBytes != null || VideoThumb != null;

	/// <summary>The first frame of the video kept in the database: drawn in the bubble, and still there when the video itself was archived.</summary>
	public byte[]? VideoThumbBytes { get; init; }

	private ImageSource? _videoSource;
	public ImageSource? VideoThumbSource => _videoSource ??= VideoThumbBytes != null ? ImageSource.FromStream(() => new MemoryStream(VideoThumbBytes)) : VideoThumb != null ? ImageSource.FromFile(VideoThumb) : null;

	/// <summary>The file was moved into an archive to free space: only its record and, for a picture or a video, its thumbnail remain.</summary>
	public bool IsArchived => FileState == AttachmentState.Archived;
	public bool CanPlay => !IsArchived;
	public bool ShowVideoLine => IsVideo && !HasVideoThumb;
	public double ThumbWidth { get; init; } = -1;
	public double ThumbHeight { get; init; } = -1;

	public bool IsBubble => !IsSeparator;
	public bool ShowTicks => IsMine && !IsSeparator && !IsDeleted;
	public string Ticks => ReadByPeer ? "✓✓" : "✓";
	public Color TickColor => ReadByPeer ? Res("Primary") : Res("Gray200");

	/// <summary>Size of the thumbnail on screen, known before the picture is read, so the list does not move when it loads.</summary>
	public double ImageWidth { get; init; } = -1;
	public double ImageHeight { get; init; } = -1;

	/// <summary>Rough height in dp of this line, to tell whether the unread ones fit on the screen.</summary>
	public double EstimatedHeight => HiddenInAlbum ? 0 : IsAlbum ? AlbumHeight + 56 : IsSeparator ? 34 : (HasImage ? ImageHeight + 56 : 58 + 20 * (Text.Length / 34)) + (HasReply ? 32 : 0) + (HasFileStatus ? 20 : 0);

	public bool HasImage => ImagePath != null || ImageThumb != null;
	public DateTime At { get; init; }

	/// <summary>The message's own time (with <see cref="Id"/>, its place in the history: what the next page is read from).</summary>
	public long Ts { get; init; }

	// Album (several pictures in a row from one sender): the last picture of the run stands for all of them with a mosaic, the
	// earlier ones are left out of the list. The messages themselves stay separate lines (reply, delete, read marks, paging).
	private AlbumInfo? _album;
	public bool HiddenInAlbum { get; private set; }
	public bool IsAlbum => _album != null;
	public bool ShowSingleImage => HasImage && !IsAlbum;
	public string? AlbumImage => _album?.Image;
	public double AlbumWidth => _album?.Width ?? -1;
	public double AlbumHeight => _album?.Height ?? 0;
	public IReadOnlyList<AlbumCell>? AlbumCells => _album?.Cells;

	public void SetAlbum(AlbumInfo? album, bool hidden)
	{
		if (HiddenInAlbum == hidden && _album?.Key == album?.Key) return;
		_album = album;
		HiddenInAlbum = hidden;
		foreach (var n in new[] { nameof(IsAlbum), nameof(HiddenInAlbum), nameof(ShowSingleImage), nameof(AlbumImage), nameof(AlbumWidth), nameof(AlbumHeight), nameof(AlbumCells), nameof(EstimatedHeight) })
			PropertyChanged?.Invoke(this, new(n));
	}
	public bool ShowText => !HasImage && !IsVideo && !IsAudio;
	public bool IsDeleted => State is MessageState.Retracted or MessageState.FileRetracted;
	public bool HasReply => !string.IsNullOrEmpty(ReplyPreview);
	public bool HasFileStatus => FileStatus.Length > 0 && !HasImage && !IsVideo && !IsAudio;
	public Color Bubble => IsMine ? Mine : Theirs;
	public LayoutOptions Align => IsMine ? LayoutOptions.End : LayoutOptions.Start;
	// My bubbles: a near-black warm fill with a fine gold outline (dark theme); theirs: plain warm grey. No large block of colour.
	public Color TextColor => IsDeleted ? Res("Gray200") : Res("Gray100");
	public Color MetaColor => Res("Gray300");
	public Color StatusColor => Res("Secondary");
	public Color BubbleStroke => Found ? Res("Primary") : IsMine && !FreeSpeech.Mobile.Services.ThemeService.IsLight ? Color.FromArgb("#8A6C36") : Colors.Transparent;   // a muted gold; the bright one marks the message a search led to
	public double BubbleStrokeThickness => Found ? 3 : IsMine && !FreeSpeech.Mobile.Services.ThemeService.IsLight ? 1 : 0;

	private bool _found;

	/// <summary>The message a search result opened the chat on: outlined for a few seconds.</summary>
	public bool Found
	{
		get => _found;
		set { _found = value; PropertyChanged?.Invoke(this, new(nameof(BubbleStroke))); PropertyChanged?.Invoke(this, new(nameof(BubbleStrokeThickness))); }
	}
	/// <summary>The same margin above a picture as at its sides (11), instead of the thinner one above a line of text.</summary>
	public Thickness BubblePadding => (HasImage || IsAlbum || HasVideoThumb) && !ShowAuthor && !HasReply ? new Thickness(11, 11, 11, 7) : new Thickness(11, 7, 11, 7);
	public FontAttributes TextStyle => IsDeleted ? FontAttributes.Italic : FontAttributes.None;

	/// <summary>A message made only of emojis (a few at most) is shown a little larger (30, 26 or 22 against the usual 14).</summary>
	public double TextSize
	{
		get
		{
			var s = Text.Trim();
			if (IsDeleted || IsFile || s.Length == 0 || s.Length > 30) return -1;
			var elements = new System.Globalization.StringInfo(s).LengthInTextElements;
			if (elements > 6) return -1;
			foreach (var rune in s.EnumerateRunes())
			{
				var v = rune.Value;
				if (System.Text.Rune.IsWhiteSpace(rune)) continue;
				var emoji = v >= 0x1F000 || (v >= 0x2190 && v <= 0x2BFF) || v is 0x200D or 0xFE0F or 0x20E3 or 0x00A9 or 0x00AE or 0x203C or 0x2049 || (v >= 0xE0020 && v <= 0xE007F);
				if (!emoji) return -1;
			}
			return elements <= 1 ? 30 : elements <= 3 ? 26 : 22;   // slightly larger than the usual 14
		}
	}

	/// <summary>A message whose first line is "md" is shown formatted (titles, lists, quotes, bold, italic, links...); any other as typed.</summary>
	public bool ShowMarkup => ShowText && !IsDeleted && FreeSpeech.Client.Markup.MessageMarkup.IsMarkdown(Text);
	public bool ShowPlainText => ShowText && !ShowMarkup;

	/// <summary>Which template draws this line: a light one for plain text, formatted text, a lone picture or a voice message, the full one otherwise (the rows are built by MAUI, which is the slow part of opening a chat).</summary>
	public FreeSpeech.Client.Display.BubbleKind Kind => FreeSpeech.Client.Display.BubbleClassifier.Of(new(IsSeparator, ShowPlainText, ShowMarkup, ShowAuthor, HasReply, HasFileStatus, HasReactions, IsFile, IsAlbum, HasImage, HasVideoThumb, IsVideo, IsAudio, IsArchived, Edited, IsDeleted));

	/// <summary>Set by the open chat: what touching a link in a formatted message does.</summary>
	public static Action<string>? LinkTapped { get; set; }

	private FormattedString? _formatted;

	public FormattedString Formatted => _formatted ??= FreeSpeech.Mobile.Services.MarkupText.Build(Text, TextColor, Res("Gray200"), Res("Primary"), Res("Gray500"), url => LinkTapped?.Invoke(url));

	/// <summary>The text with its status: a deleted message keeps a line, an edited one says so.</summary>
	public string Display => State switch
	{
		MessageState.Retracted => Loc.T("chat_deleted"),
		MessageState.FileRetracted => Loc.T("chat_file_retracted"),
		_ => Text,
	};

	/// <summary>Small "edited" mark shown before the time, not after the text.</summary>
	public string EditedLabel => Edited ? Loc.T("chat_edited") : "";
	public bool ShowEdited => Edited && !IsDeleted;
}

/// <summary>
/// A conversation screen: the newest messages first, older ones loaded when the user scrolls up, live updates, and the
/// actions on a message (reply, edit, delete for me or for everyone, forward, files). Every rule lives in the client library.
/// </summary>
[QueryProperty(nameof(Key), "key")]
public partial class ChatViewModel : ObservableObject
{
	private const int PageSize = 30;
	private const double MaxThumbnail = 240;

	private readonly ClientService _service;
	private Conversation? _conversation;
	private HistoryCursor? _oldest;
	private HistoryCursor? _newest;
	private ChatItem? _replyTarget;
	private string _key = "";

	[ObservableProperty] private string _title = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasStagingStatus))] private string _stagingStatus = "";
	public bool HasStagingStatus => StagingStatus.Length > 0;
	[ObservableProperty] private string _muteGlyph = "ic_bell.png";   // the icon file: bell, or crossed bell when silent
	[ObservableProperty] private string? _photo;
	[ObservableProperty] private string _draft = "";

	/// <summary>"is typing…" under the name (the other side, or a member of the group); empty when nobody types.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(IsTyping))] private string _typingText = "";
	public bool IsTyping => TypingText.Length > 0;
	private int _typingToken;

	/// <summary>Typing sends a throw-away signal (at most one every few seconds, see the client); an emptied field sends nothing.</summary>
	partial void OnDraftChanged(string value)
	{
		if (value.Length > 0 && _conversation != null && ClientService.InForeground) _service.NotifyTyping(_conversation);
	}

	private void OnTyping(string historyKey, string who)
	{
		if (_conversation == null || historyKey != _conversation.HistoryKey) return;
		TypingText = _conversation.Group != null ? Loc.T("chat_typing_group", Client.DisplayNameOf(_conversation, who)) : Loc.T("chat_typing");
		// The indicator fades by itself when the signals stop (they come every few seconds while the other side types).
		var token = ++_typingToken;
		_ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => { if (token == _typingToken) TypingText = ""; }));
	}
	[ObservableProperty] private string _replyBanner = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(CanWrite))] private bool _isReadOnly;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(IsDirect)), NotifyPropertyChangedFor(nameof(HeaderMenuInfoLabel))] private bool _isGroup;

	// The menu of the bar (search, silent mode, group or contact screen), one button on the right to leave the name more room.
	[ObservableProperty] private bool _isHeaderMenuOpen;

	public string HeaderMenuInfoLabel => Loc.T(IsGroup ? "a11y_group_info" : "a11y_contact_menu");

	[RelayCommand] private void ToggleHeaderMenu() => IsHeaderMenuOpen = !IsHeaderMenuOpen;
	[RelayCommand] private void HeaderMenuSearch() { IsHeaderMenuOpen = false; ToggleSearch(); }
	[RelayCommand] private void HeaderMenuMute() { IsHeaderMenuOpen = false; ToggleMute(); }
	[RelayCommand] private async Task HeaderMenuInfoAsync() { IsHeaderMenuOpen = false; if (IsGroup) await OpenGroupInfoAsync(); else await ContactMenuAsync(); }

	/// <summary>A conversation with one contact (not a group).</summary>
	public bool IsDirect => !IsGroup;

	/// <summary>How far I vouch for the contact of a direct chat (0 = none, 1 = a contact, 2 = a contact whose safety number I compared); shown by a check in the bar.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(ContactUnverified), nameof(ContactVerified))] private int _contactTrust;
	public bool ContactUnverified => IsDirect && ContactTrust == 1;
	public bool ContactVerified => IsDirect && ContactTrust == 2;

	// ------------------------------------------------------------------
	// Pinned messages (groups): a banner under the bar, the menu of a message offers to pin it (moderators)
	// ------------------------------------------------------------------

	private List<HistoryEntry> _pinned = [];
	private int _pinnedAt;

	[ObservableProperty] private bool _showPinned;
	[ObservableProperty] private int _pinnedMaxLines = 1;
	[ObservableProperty] private string _pinnedPreview = "";
	[ObservableProperty] private string _pinnedCount = "";

	private string? _expandedPinId;

	/// <summary>The pinned messages this device has already shown whole once (a pin is shown whole the first time, then on one line).</summary>
	private static HashSet<string> SeenPins
	{
		get => _seenPins ??= [.. Preferences.Default.Get("seen_pins", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)];
	}
	private static HashSet<string>? _seenPins;

	public void RefreshPinned()
	{
		_pinned = _conversation?.Group == null ? [] : [.. Client.GetPinned(_conversation)];
		_pinnedAt = _pinned.Count == 0 ? 0 : Math.Clamp(_pinnedAt, 0, _pinned.Count - 1);
		ShowPinned = _pinned.Count > 0;
		PinnedPreview = _pinned.Count == 0 ? "" : PreviewText.Of(Client, _pinned[_pinnedAt]);
		PinnedCount = _pinned.Count > 1 ? $"{_pinnedAt + 1}/{_pinned.Count}" : "";

		// The first time a pinned message is shown it is shown whole; once seen, the banner keeps one line.
		if (_pinned.Count > 0)
		{
			var id = _pinned[_pinnedAt].Id;
			if (SeenPins.Add(id))
			{
				_expandedPinId = id;
				Preferences.Default.Set("seen_pins", string.Join('\n', SeenPins.TakeLast(300)));
			}
		}
		PinnedMaxLines = _pinned.Count > 0 && _expandedPinId == _pinned[_pinnedAt].Id ? 8 : 1;
	}

	/// <summary>A touch on the banner: the list goes to the pinned message shown, and the banner moves on to the next one.</summary>
	[RelayCommand]
	private void GoToPinned()
	{
		if (_pinned.Count == 0) return;
		_focusId = _pinned[_pinnedAt].Id;
		_pinnedAt = (_pinnedAt + 1) % _pinned.Count;
		RefreshPinned();
		FocusMessage();
	}

	private void OnPinsChanged(string historyKey)
	{
		if (_conversation != null && historyKey == _conversation.HistoryKey) RefreshPinned();
	}

	/// <summary>In a group where I use a name or a picture of my own: who I am there, in the bar, so identities are not mixed up.</summary>
	[ObservableProperty] private bool _showMyIdentity;
	[ObservableProperty] private string _myIdentityText = "";
	[ObservableProperty] private string? _myGroupPhoto;
	[ObservableProperty] private string _myGroupName = "";

	public void RefreshIdentity()
	{
		if (_conversation?.Group is { } group && Client.ResolveGroup(group.GroupId) is { } rec && Client.Groups.HasOwnIdentity(rec))
		{
			MyGroupName = Client.Groups.MyNameIn(rec);
			MyGroupPhoto = Client.Groups.MyPhotoIn(rec);
			MyIdentityText = MyGroupName;
			ShowMyIdentity = true;
		}
		else ShowMyIdentity = false;
	}

	public void RefreshTrust() => ContactTrust = _conversation?.ContactId is { } id && !IsGroup ? Client.TrustOf(id) : 0;

	/// <summary>The check in the bar: the safety number of this contact, to compare it.</summary>
	[RelayCommand]
	private Task OpenContactSafetyAsync() => _conversation?.ContactId is { } id ? Shell.Current.GoToAsync($"safety?id={Uri.EscapeDataString(id)}") : Task.CompletedTask;

	public bool CanWrite => !IsReadOnly;
	[ObservableProperty] private bool _hasMoreBefore;

	/// <summary>What the list shows: <see cref="_flat"/> without the pictures an album stands in for.</summary>
	public ObservableCollection<ChatItem> Items { get; } = [];

	/// <summary>Every line of the conversation we hold, one per message, in order (the albums only change what <see cref="Items"/> shows).</summary>
	private readonly List<ChatItem> _flat = [];

	/// <summary>Files chosen but not sent yet (shown as thumbnails under the messages).</summary>
	public ObservableCollection<PendingFile> Pending { get; } = [];

	public bool HasPending => Pending.Count > 0;

	/// <summary>Raised when the list should jump to its last message (after sending, receiving, opening).</summary>
	public event Action<int?>? ScrollToEndRequested;

	/// <summary>Rough height of the lines from <paramref name="index"/> to the end.</summary>
	public double EstimatedHeightFrom(int index) => Items.Skip(index).Sum(i => i.EstimatedHeight);

	public ChatViewModel(ClientService service)
	{
		_service = service;
		service.MessageReceived += OnReceived;
		service.PinsChanged += OnPinsChanged;
		ChatItem.LinkTapped = url => _ = OpenLinkAsync(url);
		ClientService.ForegroundReturned += MarkRead;
		service.MessageChanged += OnChanged;
		service.FileCompleted += Refresh;   // a download ended: the bubble turns into a thumbnail
		service.ReadReceived += OnReadReceived;
		service.TypingReceived += OnTyping;
		Pending.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPending));
	}

	private FreeSpeechClient Client => _service.Client;

	public string Key
	{
		get => _key;
		set
		{
			// Shell hands the query over again when we come back from the picture viewer: reloading would drop the user's place in the list.
			var key = Uri.UnescapeDataString(value);
			if (_conversation != null && key == _key) return;
			_key = key;
			Load();
		}
	}

	private string? _focusId;

	// ------------------------------------------------------------------
	// Search in this conversation
	// ------------------------------------------------------------------

	/// <summary>The search panel covers the messages; the magnifier in the bar opens and closes it.</summary>
	[ObservableProperty] private bool _isSearchOpen;

	/// <summary>What is looked for in THIS conversation (case and accents ignored, two letters at least).</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(NoResults))] private string _searchText = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(NoResults))] private bool _searchDone;
	public ObservableCollection<SearchResult> Results { get; } = [];
	public bool NoResults => SearchDone && Results.Count == 0 && SearchText.Trim().Length >= FreeSpeechClient.MinSearchLength;
	private int _searchToken;

	[RelayCommand]
	private void ToggleSearch()
	{
		IsSearchOpen = !IsSearchOpen;
		if (!IsSearchOpen) SearchText = "";
	}

	/// <summary>The search runs a moment after the last key, off the screen thread; an answer that arrives after a newer question is dropped.</summary>
	partial void OnSearchTextChanged(string value)
	{
		var token = ++_searchToken;
		var text = value.Trim();
		if (_conversation == null || text.Length < FreeSpeechClient.MinSearchLength) { Results.Clear(); SearchDone = false; return; }
		var conversation = _conversation;
		_ = Task.Run(async () =>
		{
			await Task.Delay(250);
			if (token != _searchToken) return;
			var hits = Client.Search(text, 200, conversation).Select(h => new SearchResult(h, text)).ToList();
			MainThread.BeginInvokeOnMainThread(() =>
			{
				if (token != _searchToken) return;
				Results.Clear();
				foreach (var h in hits) Results.Add(h);
				SearchDone = true;
				OnPropertyChanged(nameof(NoResults));
			});
		});
	}

	/// <summary>A tap on a result: the panel closes and the list goes to that message (older pages are read until it is there), outlined for a moment.</summary>
	[RelayCommand]
	private void OpenResult(SearchResult result)
	{
		_focusId = result.MessageId;
		IsSearchOpen = false;
		SearchText = "";
		FocusMessage();
	}

	/// <summary>Raised when the list should jump to a given line (a search result), centred.</summary>
	public event Action<int>? ScrollToItemRequested;

	private void FocusMessage()
	{
		var id = _focusId;
		_focusId = null;
		if (id == null) return;
		while (!_flat.Any(i => i.Id == id) && HasMoreBefore) LoadOlder();
		var item = Items.FirstOrDefault(i => i.Id == id);
		if (item == null) { ScrollToEndRequested?.Invoke(null); return; }   // inside an album, or no longer there: the end, as usual
		item.Found = true;
		ScrollToItemRequested?.Invoke(Items.IndexOf(item));
		_ = Task.Delay(3000).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => item.Found = false));
	}

	/// <summary>Stops listening when the page goes away.</summary>
	public void Detach()
	{
		if (IsRecorderOpen) DiscardRecording();   // leaving the screen drops an unsent recording
		ClientService.Player?.Stop();
		if (_service.OpenChatKey == _conversation?.HistoryKey) _service.OpenChatKey = null;
		_service.MessageReceived -= OnReceived;
		_service.PinsChanged -= OnPinsChanged;
		ChatItem.LinkTapped = null;
		ClientService.ForegroundReturned -= MarkRead;
		_service.MessageChanged -= OnChanged;
		_service.FileCompleted -= Refresh;
		_service.ReadReceived -= OnReadReceived;
		_service.TypingReceived -= OnTyping;
	}

	[RelayCommand]
	private Task OpenGroupInfoAsync() => Shell.Current.GoToAsync($"groupinfo?key={Uri.EscapeDataString(_key)}");

	/// <summary>A link in a formatted message: the real address is shown and confirmed before anything opens.</summary>
	private async Task OpenLinkAsync(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
		if (await Dialogs.AlertAsync(Loc.T("link_open_title"), uri.AbsoluteUri, Loc.T("link_open"), Loc.T("cancel")))
			try { await Launcher.Default.OpenAsync(uri); } catch (Exception ex) when (ex is FeatureNotSupportedException or InvalidOperationException) { await Alert(ErrorText.Of(ex)); }
	}

	/// <summary>The check after a group member's name: the safety number of that contact, to compare it.</summary>
	[RelayCommand]
	private Task OpenSafety(ChatItem item) => item.AuthorTrust == 0 ? Task.CompletedTask : Shell.Current.GoToAsync($"safety?id={Uri.EscapeDataString(item.AuthorId)}");

	/// <summary>A conversation with a contact: give them a name only I see, or compare the safety number with them.</summary>
	[RelayCommand]
	private async Task ContactMenuAsync()
	{
		if (_conversation?.ContactId is not { } id || Client.FindContact(id) is not { } contact) return;
		var rename = Loc.T("contact_rename_title");
		var verify = Loc.T("safety_title");
		var choice = await Dialogs.ChooseAsync(Title, Loc.T("cancel"), null, rename, verify);
		if (choice == verify) { await Shell.Current.GoToAsync($"safety?id={Uri.EscapeDataString(id)}"); return; }
		if (choice != rename) return;

		var name = await Dialogs.PromptAsync(rename, Loc.T("contact_rename_text", contact.Nickname), Loc.T("ok"), Loc.T("cancel"), initialValue: contact.DisplayName, maxLength: 64);
		if (name == null) return;   // cancelled
		try
		{
			Client.RenameContact(contact, name);
			Title = contact.DisplayName;
			_service.NotifyConversationsChanged();
		}
		catch (ClientException ex) { await Alert(ErrorText.Of(ex)); }
	}

	/// <summary>Silent mode for this conversation: nothing pops up, the unread counter still counts.</summary>
	[RelayCommand]
	private void ToggleMute()
	{
		if (_conversation == null) return;
		var muted = !ClientService.IsMuted(_conversation.HistoryKey);
		_service.SetMuted(_conversation.HistoryKey, muted);
		MuteGlyph = muted ? "ic_bell_off.png" : "ic_bell.png";
		_service.Announce(Title, Loc.T(muted ? "chat_muted" : "chat_unmuted"));
	}

	[RelayCommand]
	private async Task OpenVideoAsync(ChatItem item)
	{
		if (item.IsArchived) { await Dialogs.AlertAsync(Loc.T("archived_title"), Loc.T("archived_hint"), Loc.T("ok")); return; }
		if (item.VideoPath != null) await Shell.Current.GoToAsync($"video?path={Uri.EscapeDataString(item.VideoPath)}");
	}

	[RelayCommand]
	private async Task OpenImageAsync(ChatItem item)
	{
		if (item.IsArchived) { await Dialogs.AlertAsync(Loc.T("archived_title"), Loc.T("archived_hint"), Loc.T("ok")); return; }
		if (item.ImagePath == null || _conversation == null) return;
		Views.ImageViewerPage.Gallery = ImagesOfConversation(_conversation);   // the viewer swipes through all of them
		Views.ImageViewerPage.MenuFor = OptionsForMessage;                    // and offers the message's actions on each
		Views.ImageViewerPage.ReactTo = ReactAsync;
		await Shell.Current.GoToAsync($"viewer?path={Uri.EscapeDataString(item.ImagePath)}");
	}

	/// <summary>Every downloaded picture of the conversation, oldest first (the whole history, not only the lines on screen), found from the attachment records.</summary>
	private List<GalleryPhoto> ImagesOfConversation(Conversation conv) => Client.GetPictures(conv).Select(p => new GalleryPhoto(p.Path, p.MessageId)).ToList();

	/// <summary>Small picture in the reply banner when the message answered is a picture or a video.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasReplyBannerThumb))] private string? _replyBannerThumb;
	public bool HasReplyBannerThumb => ReplyBannerThumb != null;

	public bool IsHasReplyBanner => !string.IsNullOrEmpty(ReplyBanner);

	partial void OnReplyBannerChanged(string value) => OnPropertyChanged(nameof(IsHasReplyBanner));

	// ------------------------------------------------------------------
	// Reading
	// ------------------------------------------------------------------

	private void Load()
	{
		FreeSpeech.Mobile.Services.Perf.Mark("Load: starts");
		_conversation = Client.FindConversation(_key);
		if (_conversation == null) return;
		_service.OpenChatKey = _conversation.HistoryKey;
		_reactions = null;
		MuteGlyph = ClientService.IsMuted(_conversation.HistoryKey) ? "ic_bell_off.png" : "ic_bell.png";

		IsGroup = _conversation.Group != null;
		IsReadOnly = _conversation.Group is { Active: false } || (_conversation.Group == null && Client.FindContact(_conversation.ContactId!) == null);   // a group I left or a contact I removed: readable, nothing can be sent
		Title = _conversation.Group?.Name ?? Client.FindContact(_conversation.ContactId!)?.DisplayName
			?? Client.ArchivedContacts.FirstOrDefault(a => a.DiscoveryId == _conversation.ContactId)?.Nickname ?? _conversation.ContactId![..8];
		FreeSpeech.Mobile.Services.Perf.Mark("Load: title and contact");
		Photo = Client.GetConversations().FirstOrDefault(s => s.Conversation.HistoryKey == _conversation.HistoryKey)?.Photo;
		FreeSpeech.Mobile.Services.Perf.Mark("Load: GetConversations (photo)");

		var unread = Client.UnreadCount(_conversation);
		var page = Client.GetLatestMessages(_conversation, PageSize);
		FreeSpeech.Mobile.Services.Perf.Mark("Load: messages read");
		PrepareReactions(page.Entries);
		FreeSpeech.Mobile.Services.Perf.Mark("Load: reactions");
		_hasMoreAfter = false;
		Items.Clear();
		_flat.Clear();

		// The separator goes before the oldest unread message: the unread ones are the last `unread` received lines.
		var firstUnread = unread == 0 ? null : page.Entries.Where(e => e.Direction == MessageDirection.Received).Reverse().Take(unread).LastOrDefault();
		ChatItem? separator = null;
		foreach (var e in page.Entries)
		{
			if (firstUnread != null && e.Id == firstUnread.Id) { separator = Separator(unread); _flat.Add(separator); }
			_flat.Add(Build(e));
		}
		FreeSpeech.Mobile.Services.Perf.Mark("Load: lines built");
		ApplyAlbums();   // also fills Items from _flat
		FreeSpeech.Mobile.Services.Perf.Mark("Load: albums and Items");
		int? separatorIndex = separator == null ? null : Items.IndexOf(separator);
		_oldest = page.Oldest;
		_newest = page.Newest;
		HasMoreBefore = page.HasMoreBefore;
		if (_focusId != null) FocusMessage();                   // opened from a search result: on that message
		else ScrollToEndRequested?.Invoke(separatorIndex);   // opening: at the first unread message if they do not all fit, else at the end
		ShowToBottom = separatorIndex is { } at && Items.Count - at > FarFromBottom;   // opened on many new messages: the chevron brings to the end
		RefreshTrust();
		RefreshIdentity();
		RefreshPinned();
		FreeSpeech.Mobile.Services.Perf.Mark("Load: trust, identity, pinned");
		MarkRead();
		FreeSpeech.Mobile.Services.Perf.Mark("Load: done");
	}

	private static ChatItem Separator(int unread) => new()
	{
		Id = "unread-separator", IsMine = false, Author = "", Text = Loc.T("chat_unread"),
		Time = "", State = MessageState.Normal, IsSeparator = true,
	};

	/// <summary>What is on screen counts as read (while the app is in front): counters drop, the contact gets a receipt.</summary>
	private void MarkRead()
	{
		if (_conversation != null && ClientService.InForeground) _ = _service.MarkReadAsync(_conversation);
	}

	/// <summary>The contact read my messages: the ticks turn gold.</summary>
	private void OnReadReceived(string historyKey)
	{
		if (_conversation == null || historyKey != _conversation.HistoryKey) return;
		foreach (var item in _flat.Where(i => i.IsMine && !i.ReadByPeer && !i.IsSeparator).ToList()) Refresh(item.Id);
	}

	/// <summary>
	/// The lines held at most: reading far back in a long history does not keep every page in memory. Whichever end is far from where the
	/// user is reading is let go, and read again from the database if they come back to it.
	/// </summary>
	private const int MaxLines = 150;

	private bool _hasMoreAfter;

	/// <summary>The double chevron that brings back to the newest message: shown when the screen is far from the bottom (reading far back, or a chat opened on many new messages).</summary>
	[ObservableProperty] private bool _showToBottom;

	/// <summary>Lines from the bottom beyond which the double chevron shows (a little more than a screen).</summary>
	private const int FarFromBottom = 8;

	public void UpdateBottomButton(int lastVisibleIndex) => ShowToBottom = _hasMoreAfter || (Items.Count > FarFromBottom && lastVisibleIndex >= 0 && lastVisibleIndex < Items.Count - FarFromBottom);

	[RelayCommand]
	private void GoToBottom()
	{
		ShowToBottom = false;
		if (_hasMoreAfter) Load();   // the end was let go: start again from the newest lines (opens at the end)
		else ScrollToEndRequested?.Invoke(null);
	}

	/// <summary>Newer lines than the last one held exist (the end was let go while reading far back).</summary>
	public bool HasMoreAfter => _hasMoreAfter;

	/// <summary>Adds the next older page on top (the user scrolled up). Returns how many lines were added.</summary>
	public int LoadOlder()
	{
		if (_conversation == null || !HasMoreBefore || _oldest == null) return 0;

		var page = Client.GetMessagesBefore(_conversation, _oldest.Value, PageSize);
		PrepareReactions(page.Entries);
		for (var i = page.Entries.Count - 1; i >= 0; i--) _flat.Insert(0, Build(page.Entries[i]));
		_oldest = page.Oldest ?? _oldest;
		HasMoreBefore = page.HasMoreBefore;
		if (_flat.Count > MaxLines)
		{
			// Reading back: the newest lines are the far end.
			_flat.RemoveRange(MaxLines, _flat.Count - MaxLines);
			if (_flat.LastOrDefault(i => !i.IsSeparator) is { } last) _newest = new HistoryCursor(last.Ts, last.Id);
			_hasMoreAfter = true;
		}
		ApplyAlbums();
		return page.Entries.Count;
	}

	/// <summary>Adds the next newer page at the bottom (the user scrolled back down after reading far back). Returns whether lines were added.</summary>
	public bool LoadNewer()
	{
		if (_conversation == null || !_hasMoreAfter || _newest == null) return false;

		var page = Client.GetMessagesAfter(_conversation, _newest.Value, PageSize);
		PrepareReactions(page.Entries);
		foreach (var e in page.Entries) _flat.Add(Build(e));
		_newest = page.Newest ?? _newest;
		_hasMoreAfter = page.HasMoreAfter;
		if (_flat.Count > MaxLines)
		{
			// Reading forward: the oldest lines are the far end.
			_flat.RemoveRange(0, _flat.Count - MaxLines);
			if (_flat.FirstOrDefault(i => !i.IsSeparator) is { } first) _oldest = new HistoryCursor(first.Ts, first.Id);
			HasMoreBefore = true;
		}
		ApplyAlbums();
		return page.Entries.Count > 0;
	}

	/// <summary>Where a line is among those shown now, or -1 (the screen keeps its place across a page that was added or let go).</summary>
	public int IndexOfLine(string id) { for (var i = 0; i < Items.Count; i++) if (Items[i].Id == id) return i; return -1; }

	/// <summary>Adds whatever arrived after the last line we show.</summary>
	private void AppendNew()
	{
		if (_conversation == null) return;
		if (_newest == null || _hasMoreAfter) { Load(); return; }   // the end was let go: start again from the newest lines

		while (true)
		{
			var page = Client.GetMessagesAfter(_conversation, _newest.Value, PageSize);
			PrepareReactions(page.Entries);
			foreach (var e in page.Entries) _flat.Add(Build(e));
			ApplyAlbums();
			_newest = page.Newest ?? _newest;
			if (!page.HasMoreAfter) break;
		}
		ScrollToEndRequested?.Invoke(null);
	}

	// The reactions of the lines held, read a page at a time (not the whole conversation), kept up to date when one changes.
	private Dictionary<string, List<Reaction>>? _reactions;

	private void PrepareReactions(IReadOnlyList<HistoryEntry> entries)
	{
		if (_conversation == null || entries.Count == 0) return;
		_reactions ??= [];
		var found = Client.GetReactions(_conversation, entries.Select(e => e.Id).ToList());
		foreach (var e in entries) _reactions[e.Id] = found.TryGetValue(e.Id, out var list) ? list : [];
	}

	private void ReloadReactions(string id)
	{
		if (_conversation == null) return;
		_reactions ??= [];
		_reactions[id] = Client.GetReactions(_conversation, [id]).TryGetValue(id, out var list) ? list : [];
	}
	/// <summary>The reaction bar, left to right: the six first ones, then stars in the eyes, hearts in the eyes, applause, screaming in fear.</summary>
	public static readonly string[] QuickReactions = ["👍", "❤️", "😂", "😮", "😢", "🙏", "🤩", "😍", "👏", "😱"];
	public string[] ReactionEmojis => QuickReactions;

	private string ReactionsOf(Conversation conv, string id)
	{
		if (_reactions == null || !_reactions.TryGetValue(id, out var list)) { ReloadReactions(id); list = _reactions![id]; }   // a line built before its page was prepared
		if (list.Count == 0) return "";
		return string.Join("  ", list.GroupBy(r => r.Emoji).Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key));
	}

	private ChatItem Build(HistoryEntry e)
	{
		var conv = _conversation!;
		var mine = e.Direction == MessageDirection.Sent;
		var author = mine ? Loc.T("chat_me") : conv.Group != null ? Client.DisplayNameOf(conv, e.From ?? "") : Title;

		string? reply = null, replyThumb = null;
		if (e.ReplyToId != null)
		{
			var target = Client.FindMessage(conv, e.ReplyToId);
			if (target == null) reply = Loc.T("chat_reply_missing");
			else if (Client.Attachments.Store.Find(target.Id) is { } targetFile)
			{
				reply = PreviewText.Of(Client, target);   // "Photo", "Video: name"… never the path
				replyThumb = ThumbOf(targetFile);
			}
			else reply = Snippet(target);
		}

		var fileRec = Client.Attachments.Store.Find(e.Id);
		var isFile = fileRec != null;
		var video = fileRec is { State: AttachmentState.Complete, Path: { } vp } && fileRec.Mime.StartsWith("video/", StringComparison.Ordinal) && File.Exists(vp) ? vp : null;
		var image = fileRec is { State: AttachmentState.Complete, Path: { } p } && fileRec.Mime.StartsWith("image/", StringComparison.Ordinal) && File.Exists(p) ? p : null;
		// The video's first frame comes from the database (it stays when the video is archived); the cached frame file is the fallback.
		var isVideoFile = fileRec != null && fileRec.Mime.StartsWith("video/", StringComparison.Ordinal);
		var videoFrame = isVideoFile ? Client.Attachments.ThumbnailOf(fileRec!) : null;
		var thumb = videoFrame != null || video == null ? null : ClientService.Thumbnailer?.ThumbnailOf(video);
		double thumbW = -1, thumbH = -1;
		var (tw, th) = videoFrame != null ? (videoFrame.Width, videoFrame.Height) : thumb != null ? ProfilePhoto.SizeOf(thumb) : (0, 0);
		if (tw > 0 && th > 0) { var fit = Math.Min(MaxThumbnail / tw, MaxThumbnail / th); (thumbW, thumbH) = (tw * fit, th * fit); }
		var audio = fileRec is { State: AttachmentState.Complete, Path: { } ap } && fileRec.Mime.StartsWith("audio/", StringComparison.Ordinal) && File.Exists(ap) ? ap : null;
		double shownW = -1, shownH = -1;
		// The kept thumbnail already says how big the picture is: the file is not opened to measure it.
		var picture = fileRec != null && fileRec.Mime.StartsWith("image/", StringComparison.Ordinal) ? Client.Attachments.ThumbnailOf(fileRec) : null;
		if (image != null || picture != null)
		{
			var (w, h) = picture != null ? (picture.Width, picture.Height) : ProfilePhoto.SizeOf(image!);
			if (w > 0 && h > 0) { var fit = Math.Min(MaxThumbnail / w, MaxThumbnail / h); (shownW, shownH) = (w * fit, h * fit); }
		}
		return new ChatItem
		{
			AudioPath = audio, AudioLength = audio == null ? null : ClientService.Player?.DurationOf(audio), Playing = audio != null && audio == _playingPath,
			VideoThumb = thumb, VideoThumbBytes = videoFrame?.Data, ThumbWidth = thumbW, ThumbHeight = thumbH,
			VideoPath = video, VideoName = fileRec?.Name ?? "", ImagePath = image, ImageThumb = picture?.Data, ImageWidth = shownW, ImageHeight = shownH,
			Id = e.Id, At = e.At, Ts = e.Ts, IsMine = mine, Author = author, Text = e.Text, Edited = e.Edited, State = e.State,
			Time = TimeLabel(e.At, e.Ts, mine), IsFile = isFile, FileStatus = FileStatusOf(e.Id), FileState = fileRec?.State, ReadByPeer = Client.IsReadByPeer(conv, e),
			ReplyPreview = reply, ReplyThumb = replyThumb, ShowAuthor = !mine && conv.Group != null, AuthorPhoto = !mine && conv.Group != null ? Client.MemberPhotoIn(conv.Group, e.From ?? "") : null, AuthorId = e.From ?? "", AuthorTrust = !mine && conv.Group != null ? Client.TrustOf(e.From ?? "") : 0, ReactionsText = e.State is MessageState.Retracted ? "" : ReactionsOf(conv, e.Id),
		};
	}

	/// <summary>
	/// The time under a message: "10:42" for mine, and for one received more than a minute after it was sent, both times
	/// ("10:42 · received 10:57"; the send time carries its date when it is not the day of arrival). <paramref name="ts"/> is the sender's clock.
	/// </summary>
	internal static string TimeLabel(DateTime at, long ts, bool mine)
	{
		var received = at.ToLocalTime();
		if (mine || ts <= 0 || ts > DateTime.MaxValue.Ticks) return received.ToString("HH:mm");
		var sent = new DateTime(ts, DateTimeKind.Utc).ToLocalTime();
		if (sent >= received || received - sent < TimeSpan.FromMinutes(1)) return received.ToString("HH:mm");   // arrived at once (or the sender's clock is ahead)
		return Loc.T("time_sent_received", sent.ToString(sent.Date == received.Date ? "HH:mm" : "dd/MM HH:mm"), received.ToString("HH:mm"));
	}

	/// <summary>A picture itself, or the first frame of a video, when the file is here; null otherwise.</summary>
	private static string? ThumbOf(AttachmentRecord rec)
	{
		if (rec is not { State: AttachmentState.Complete, Path: { } path } || !File.Exists(path)) return null;
		if (rec.Mime.StartsWith("image/", StringComparison.Ordinal)) return path;
		return rec.Mime.StartsWith("video/", StringComparison.Ordinal) ? ClientService.Thumbnailer?.ThumbnailOf(path) : null;
	}

	private static string Snippet(HistoryEntry e) => e.State switch
	{
		MessageState.Retracted => Loc.T("chat_deleted"),
		MessageState.FileRetracted => Loc.T("preview_file_removed"),
		_ => FreeSpeech.Client.Markup.MessageMarkup.PlainText(e.Text) is var plain && plain.Length > 60 ? plain[..60] + "…" : FreeSpeech.Client.Markup.MessageMarkup.PlainText(e.Text),
	};

	private string FileStatusOf(string id)
	{
		var rec = Client.Attachments.Store.Find(id);
		if (rec == null) return "";
		// A file that is not here yet says how big it is, so the person decides whether to fetch it.
		var size = ByteSize.Format(rec.Size, Loc.T("size_units"));
		return rec.State switch
		{
			AttachmentState.Complete => Loc.T(rec.Direction == AttachmentDirection.Sent ? "chat_file_sent" : "chat_file_done"),
			AttachmentState.Receiving => $"{Loc.T(rec.WaitingNetwork ? "chat_file_waiting_wifi" : "chat_file_receiving")} · {size}",
			AttachmentState.Removed => Loc.T("chat_file_removed"),
			AttachmentState.Archived => $"{Loc.T("chat_file_archived")} · {size}",
			AttachmentState.Failed => $"{Loc.T("chat_file_failed")} · {size}",
			_ => $"{Loc.T("chat_file_offered")} · {size}",
		};
	}

	// ------------------------------------------------------------------
	// Live updates
	// ------------------------------------------------------------------

	private void OnReceived(ChatMessage m)
	{
		if (_conversation == null || m.Conversation.HistoryKey != _conversation.HistoryKey) return;
		if (!m.Mine) { _typingToken++; TypingText = ""; }   // the message is here: nobody is typing it any more
		if (ClientService.InForeground) ClearUnreadLine();   // something happened and the chat is on screen: the "new messages" line goes
		AppendNew();
		RefreshPinned();   // a pinned message that was missing may be here now
		if (!m.Mine) MarkRead();   // the chat is open: this message is seen at once
	}

	/// <summary>The "new messages" line only says where the unread ones start: it goes at the first event in the chat (a message, a reaction, an edit, a deletion; mine or theirs).</summary>
	private void ClearUnreadLine()
	{
		if (_flat.RemoveAll(i => i.IsSeparator) > 0) ApplyAlbums();
	}

	private void OnChanged(MessageChange c)
	{
		if (_conversation == null || c.Conversation.HistoryKey != _conversation.HistoryKey) return;
		if (ClientService.InForeground) ClearUnreadLine();   // a reaction, an edit or a deletion is an event too
		if (c.Kind != MessageChangeKind.Reacted) RefreshPinned();   // an edit or a deletion changes the banner
		if (c.Kind == MessageChangeKind.Reacted) ReloadReactions(c.MessageId);
		if (c.Kind == MessageChangeKind.Reacted) RefreshReacted(c.MessageId);
		else Refresh(c.MessageId);
	}

	private void Refresh(string id)
	{
		var i = _flat.FindIndex(x => x.Id == id);
		if (i < 0 || _conversation == null) return;
		var entry = Client.FindMessage(_conversation, id);
		if (entry == null) _flat.RemoveAt(i);
		else _flat[i] = Build(entry);
		ApplyAlbums();
	}

	private const int AlbumMinimum = 3;                              // from three pictures in a row
	private static readonly TimeSpan AlbumGap = TimeSpan.FromMinutes(2);   // between two neighbours of the same run

	/// <summary>A plain picture line: no text, reply, reaction or deletion, which an album would hide.</summary>
	private static bool Groupable(ChatItem i) => i is { ImagePath: not null, IsSeparator: false, IsDeleted: false, HasReply: false, HasReactions: false };

	/// <summary>
	/// Groups runs of at least three pictures from the same sender (at most two minutes apart) into one bubble: a mosaic of at most four
	/// pictures with "+N" on the last one when there are more, never taller than a third of the screen. Recomputed after every change,
	/// then <see cref="Items"/> is brought in line with <see cref="_flat"/> by removals and insertions only, so the scroll position holds.
	/// </summary>
	private void ApplyAlbums()
	{
		var start = 0;
		while (start < _flat.Count)
		{
			if (!Groupable(_flat[start])) { _flat[start].SetAlbum(null, false); start++; continue; }
			var end = start;
			while (end + 1 < _flat.Count && Groupable(_flat[end + 1]) && _flat[end + 1].IsMine == _flat[start].IsMine && _flat[end + 1].Author == _flat[start].Author
			       && _flat[end + 1].At >= _flat[end].At && _flat[end + 1].At - _flat[end].At <= AlbumGap) end++;

			var run = _flat.GetRange(start, end - start + 1);
			var album = run.Count >= AlbumMinimum ? BuildAlbum(run) : null;
			if (album != null)
			{
				for (var k = 0; k < run.Count - 1; k++) run[k].SetAlbum(null, true);
				run[^1].SetAlbum(album, false);
			}
			else foreach (var it in run) it.SetAlbum(null, false);
			start = end + 1;
		}

		var target = _flat.Where(i => !i.HiddenInAlbum).ToList();
		var wanted = new HashSet<ChatItem>(target);
		for (var k = Items.Count - 1; k >= 0; k--) if (!wanted.Contains(Items[k])) Items.RemoveAt(k);
		for (var k = 0; k < target.Count; k++) if (k >= Items.Count || !ReferenceEquals(Items[k], target[k])) Items.Insert(k, target[k]);
	}

	private AlbumInfo? BuildAlbum(List<ChatItem> run)
	{
		if (ClientService.Collage is not { } collage) return null;
		var (w, h) = AlbumLayout.Size();
		var shown = run.Take(4).ToList();
		var rects = AlbumLayout.Cells(shown.Count, w, h);
		var overlay = run.Count > 4 ? $"+{run.Count - 4}" : "";
		var bubble = shown[0].Bubble;
		var argb = 0xFF000000u | ((uint)(bubble.Red * 255) << 16) | ((uint)(bubble.Green * 255) << 8) | (uint)(bubble.Blue * 255);
		var image = collage.Make(shown.Select(s => s.ImagePath!).ToList(), overlay, w, h, rects, argb);
		if (image == null) return null;
		var cells = rects.Select((r, k) => new AlbumCell(r.X, r.Y, r.W, r.H, shown[k])).ToList();
		return new AlbumInfo(image, image, w, h, cells);
	}
	// ------------------------------------------------------------------
	// Sending
	// ------------------------------------------------------------------

	[RelayCommand]
	private async Task SendAsync()
	{
		var text = Draft.Trim();
		if ((text.Length == 0 && Pending.Count == 0) || _conversation == null) return;
		if (!await _service.ConfirmLargeSendAsync(Pending.Select(p => p.Path))) return;   // a big file only goes after a yes; everything stays staged on a no

		try
		{
			var files = Pending.ToList();   // nothing leaves before this tap: the user could still remove any of them
			var replyTo = _replyTarget?.Id;
			Draft = "";
			Pending.Clear();
			ClearReply();
			if (text.Length > 0) await Client.SendAsync(_conversation, text, replyTo);
			foreach (var f in files) await Client.UploadAsync(_conversation, f.Path);
			ClearUnreadLine();
			AppendNew();
		}
		catch (ClientException ex) { await Alert(ErrorText.Of(ex)); }
		catch (InvalidOperationException ex) { await Alert(ErrorText.Of(ex)); }   // e.g. "no longer in this group"
	}

	// The attach button opens a row of icons; each one picks, captures or records. Whatever it yields only waits
	// under the composer: the send button sends it.
	[ObservableProperty] private bool _isAttachOpen;

	[RelayCommand] private void Attach() => IsAttachOpen = !IsAttachOpen;

	[RelayCommand]
	private async Task AttachFileAsync()
	{
		IsAttachOpen = false;
		await GuardAsync(async () =>
		{
			var picked = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = Loc.T("chat_pick_file") });
			foreach (var file in picked ?? []) Stage(file.FullPath, file.FileName);
		});
	}

	[RelayCommand]
	private async Task AttachCaptureAsync(string kind)
	{
		IsAttachOpen = false;
		await GuardAsync(async () =>
		{
			if (!MediaPicker.Default.IsCaptureSupported) { await Alert(Loc.T("attach_no_camera")); return; }
			var shot = kind == "video" ? await MediaPicker.Default.CaptureVideoAsync() : await MediaPicker.Default.CapturePhotoAsync();
			if (shot != null) Stage(await KeepAsync(shot), shot.FileName);
		});
	}

	private async Task GuardAsync(Func<Task> action)
	{
		if (_conversation == null) return;
		try { await action(); }
		catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or PermissionException or FeatureNotSupportedException) { await Alert(ErrorText.Of(ex)); }
	}

	// ------------------------------------------------------------------
	// Voice messages: record, listen, then send (or drop)
	// ------------------------------------------------------------------

	private string? _recordingPath;
	private IDispatcherTimer? _recTimer;
	private DateTime _recStart;

	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowComposer)), NotifyPropertyChangedFor(nameof(IsRecorderOpen)), NotifyPropertyChangedFor(nameof(IsReviewing))] private bool _isRecording;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(ShowComposer)), NotifyPropertyChangedFor(nameof(IsRecorderOpen)), NotifyPropertyChangedFor(nameof(IsReviewing))] private bool _hasRecording;
	[ObservableProperty] private string _recTime = "0:00";
	[ObservableProperty] private string _previewGlyph = "ic_play.png";

	public bool IsRecorderOpen => IsRecording || HasRecording;
	public bool IsReviewing => HasRecording && !IsRecording;
	public bool ShowComposer => CanWrite && !IsRecorderOpen;

	partial void OnIsReadOnlyChanged(bool value) => OnPropertyChanged(nameof(ShowComposer));

	[RelayCommand]
	private async Task AttachAudioAsync()
	{
		IsAttachOpen = false;
		if (ClientService.Recorder is not { } rec) { await Alert(Loc.T("attach_no_camera")); return; }

		var dir = Path.Combine(FileSystem.CacheDirectory, "capture");
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.m4a");
		if (!await rec.StartAsync(path)) { await Alert(Loc.T("audio_denied")); return; }

		_recordingPath = path;
		_recStart = DateTime.UtcNow;
		RecTime = "0:00";
		IsRecording = true;
		_recTimer = Application.Current!.Dispatcher.CreateTimer();
		_recTimer.Interval = TimeSpan.FromMilliseconds(250);
		_recTimer.Tick += (_, _) => { var t = DateTime.UtcNow - _recStart; RecTime = $"{(int)t.TotalMinutes}:{t.Seconds:00}"; };
		_recTimer.Start();
	}

	/// <summary>Stops recording; the user can then listen before deciding.</summary>
	[RelayCommand]
	private void StopRecording()
	{
		_recTimer?.Stop();
		ClientService.Recorder?.Stop();
		IsRecording = false;
		HasRecording = _recordingPath != null && File.Exists(_recordingPath) && new FileInfo(_recordingPath).Length > 0;
		if (!HasRecording) DiscardRecording();
	}

	[RelayCommand]
	private void PreviewRecording()
	{
		if (_recordingPath == null) return;
		if (PreviewGlyph == "ic_pause.png") { ClientService.Player?.Stop(); return; }
		PreviewGlyph = "ic_pause.png";
		ClientService.Player?.Play(_recordingPath, () => MainThread.BeginInvokeOnMainThread(() => PreviewGlyph = "ic_play.png"));
	}

	[RelayCommand]
	private void DiscardRecording()
	{
		_recTimer?.Stop();
		ClientService.Player?.Stop();
		if (IsRecording) ClientService.Recorder?.Cancel();
		else if (_recordingPath != null) try { File.Delete(_recordingPath); } catch (IOException) { }
		_recordingPath = null;
		IsRecording = false;
		HasRecording = false;
		PreviewGlyph = "ic_play.png";
	}

	[RelayCommand]
	private async Task SendRecordingAsync()
	{
		ClientService.Player?.Stop();
		if (_recordingPath is not { } path) return;
		_recordingPath = null;
		HasRecording = false;
		PreviewGlyph = "ic_play.png";
		Pending.Add(new PendingFile(path, Path.GetFileName(path)));
		await SendAsync();
	}

	// Playing a voice message received.
	private string? _playingPath;

	[RelayCommand]
	private void PlayAudio(ChatItem item)
	{
		if (item.AudioPath is not { } path || ClientService.Player is not { } player) return;
		var wasPlaying = item.Playing;
		player.Stop();   // ends whatever was playing (its done callback clears the flag)
		if (wasPlaying) return;

		_playingPath = path;
		item.Playing = true;
		player.Play(path, () => MainThread.BeginInvokeOnMainThread(() => { item.Playing = false; if (_playingPath == path) _playingPath = null; }));
	}

	private static readonly string[] VideoExtensions = [".mp4", ".mov", ".3gp", ".webm", ".mkv", ".m4v"];

	/// <summary>Waits under the composer. A video is shrunk and re-encoded first (720p H.264): a phone video is far too heavy to send as it is.</summary>
	private void Stage(string path, string name)
	{
		if (Pending.Any(p => p.Path == path)) return;
		var extension = Path.GetExtension(path);
		if (PhotoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) { StageImage(path, name); return; }
		if (!VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) { Pending.Add(new PendingFile(path, name)); return; }
		_ = StageVideoAsync(path, name);
	}

	// Animated GIFs are left alone: re-encoding would keep a single frame.
	private static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png", ".webp", ".heic"];

	/// <summary>A picture is reduced to 1600 px (JPEG) before it waits under the composer: never sent heavier than needed.</summary>
	private void StageImage(string path, string name)
	{
#if ANDROID
		var small = ImageCompressor.Shrink(path);
		if (small != null && new FileInfo(small).Length < new FileInfo(path).Length) (path, name) = (small, Path.GetFileNameWithoutExtension(name) + Path.GetExtension(small));   // never send a bigger one
#endif
		if (!Pending.Any(p => p.Path == path)) Pending.Add(new PendingFile(path, name));
	}

	private async Task StageVideoAsync(string path, string name)
	{
		StagingStatus = Loc.T("video_compressing", name);
		try
		{
#if ANDROID
			var small = await VideoCompressor.CompressAsync(path);
			if (new FileInfo(small).Length < new FileInfo(path).Length) (path, name) = (small, Path.GetFileNameWithoutExtension(name) + ".mp4");   // never send a bigger one
#endif
		}
		catch (IOException) { /* the phone cannot re-encode it: the original goes */ }
		finally { StagingStatus = ""; }
		if (!Pending.Any(p => p.Path == path)) Pending.Add(new PendingFile(path, name));
		await Task.CompletedTask;
	}

	/// <summary>A capture lands in the camera's cache folder: copy it to ours, with a name that tells when it was taken.</summary>
	private static async Task<string> KeepAsync(FileResult shot)
	{
		var dir = Path.Combine(FileSystem.CacheDirectory, "capture");
		Directory.CreateDirectory(dir);
		var target = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(shot.FileName)}");
		await using var input = await shot.OpenReadAsync();
		await using var output = File.Create(target);
		await input.CopyToAsync(output);
		return target;
	}
	[RelayCommand] private void RemovePending(PendingFile file) => Pending.Remove(file);

	[RelayCommand] private void CancelReply() => ClearReply();

	private void ClearReply()
	{
		_replyTarget = null;
		ReplyBanner = "";
		ReplyBannerThumb = null;
	}

	// ------------------------------------------------------------------
	// Actions on a message
	// ------------------------------------------------------------------

	[RelayCommand]
	private Task ActionsAsync(ChatItem item)
	{
		var options = OptionsFor(item);
		if (options == null) return Task.CompletedTask;
		MenuItems.Clear();
		foreach (var o in options) MenuItems.Add(o);
		IsMenuOpen = true;   // the page shows the popup; PickMenu runs the chosen action
		return Task.CompletedTask;
	}

	/// <summary>The actions the picture viewer offers on a picture (long press or the menu button): reply, react, forward, delete.</summary>
	public List<MenuOption>? OptionsForMessage(string id)
	{
		if (_conversation == null) return null;
		var entry = Client.FindMessage(_conversation, id);
		return entry == null ? null : OptionsFor(Build(entry));
	}

	/// <summary>The "Info" dialog of a message: when it was sent (to the second), when it arrived here, when it was edited, and how far it went.</summary>
	private async Task ShowInfoAsync(HistoryEntry entry, bool mine)
	{
		static string Full(DateTime t) => t.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
		static DateTime? OfTicks(long ts) => ts > 0 && ts <= DateTime.MaxValue.Ticks ? new DateTime(ts, DateTimeKind.Utc) : null;
		var lines = new List<string>();
		lines.Add($"{Loc.T("info_sent")} : {Full(OfTicks(entry.Ts) ?? entry.At)}");
		if (!mine) lines.Add($"{Loc.T("info_received")} : {Full(entry.At)}");
		if (entry.EditedTs is { } edited && OfTicks(edited) is { } editedAt) lines.Add($"{Loc.T("info_edited")} : {Full(editedAt)}");
		if (mine && _conversation is { } conv && entry.State == MessageState.Normal)
		{
			var queued = Client.Outbox.FirstOrDefault(p => p.Id == entry.Id);
			var status = queued != null ? Loc.T(queued.SentAt == null ? "info_unsent" : "info_delivered")
				: Loc.T(Client.IsReadByPeer(conv, entry) ? "info_read" : "info_confirmed");
			lines.Add($"{Loc.T("info_status")} : {status}");
		}
		await Dialogs.AlertAsync(Loc.T("info_title"), string.Join("\n", lines), Loc.T("ok"));
	}

	/// <summary>Everything that can be done with this message, each entry keeping its action next to its translated label.</summary>
	private List<MenuOption>? OptionsFor(ChatItem item)
	{
		if (_conversation == null) return null;
		var conv = _conversation;
		var index = Client.IndexOfMessage(conv, item.Id);
		if (index == 0) return null;

		var rec = Client.Attachments.Store.Find(item.Id);

		// Each entry keeps its action next to its (translated) label: nothing depends on comparing label texts.
		var options = new List<MenuOption>();
		if (!item.IsDeleted) options.Add(new(Loc.T("act_reply"), "ic_reply.png", () => { Reply(item); return Task.CompletedTask; }, LeavesViewer: true));
		var hasText = !item.IsDeleted && !item.IsFile && item.Text.Length > 0;
		if (hasText) options.Add(new(Loc.T("act_copy"), "ic_copy.png", async () =>
		{
			await Clipboard.Default.SetTextAsync(item.Text);
			_service.Announce(Title, Loc.T("chat_copied"));
		}));
		var sharablePath = rec is { State: AttachmentState.Complete, Path: { } sp } && File.Exists(sp) ? sp : null;
		if (hasText || sharablePath != null) options.Add(new(Loc.T("act_share"), "ic_share.png", () => sharablePath != null
			? Share.Default.RequestAsync(new ShareFileRequest(rec!.Name, new ShareFile(sharablePath)))   // hands the file to another app
			: Share.Default.RequestAsync(new ShareTextRequest(item.Text))));
		if (item.IsMine && !item.IsDeleted && !item.IsFile && item.State == MessageState.Normal) options.Add(new(Loc.T("act_edit"), "ic_edit.png", async () =>
		{
			_editTarget = (conv, index, item);
			EditText = item.Text;
			IsEditOpen = true;
			EditRequested?.Invoke();
			await Task.CompletedTask;
		}));
		if (rec != null && rec.State == AttachmentState.Complete && rec.Path != null) options.Add(new(Loc.T("act_open_file"), "ic_open.png",
			() => Launcher.Default.OpenAsync(new OpenFileRequest(rec.Name, new ReadOnlyFile(rec.Path!)))));
		if (rec != null && rec.Direction == AttachmentDirection.Received && rec.State is AttachmentState.Offered or AttachmentState.Receiving or AttachmentState.Failed) options.Add(new(Loc.T("act_download"), "ic_download.png", async () =>
		{
			var asked = await Client.DownloadAsync(conv, index);   // English text for the console: only logged, the coloured dot on the message shows the state
				Diag.Log($"Download asked: {(asked.StartsWith("Requested", StringComparison.Ordinal) ? "sender reached" : "sender offline, flag set")}");
			Refresh(item.Id);
		}));
		if (rec is { Direction: AttachmentDirection.Received, State: AttachmentState.Receiving, WaitingNetwork: true }) options.Add(new(Loc.T("act_download_anyway"), "ic_download.png", async () =>
		{
			await Client.DownloadAsync(conv, index, anyway: true);   // this file only: the setting stays as it is for the others
			Refresh(item.Id);
		}));
		if (!item.IsDeleted && !item.IsMine) options.Add(new(Loc.T("act_react"), "ic_react.png", () => { OpenReactionBar(item); return Task.CompletedTask; }, IsReact: true));
		if (!item.IsDeleted) options.Add(new(Loc.T("act_forward"), "ic_forward.png", () => ForwardAsync(conv, index)));
		if (!item.IsDeleted && Client.CanPin(conv))
		{
			var pinned = Client.IsPinned(conv, item.Id);
			options.Add(new(Loc.T(pinned ? "act_unpin" : "act_pin"), "ic_pin.png", async () =>
			{
				try { await Client.PinMessageAsync(conv, index, !pinned); RefreshPinned(); }
				catch (Exception ex) when (ex is ClientException or InvalidOperationException) { await Alert(ErrorText.Of(ex)); }
			}));
		}
		if (!item.IsDeleted && Client.FindMessage(conv, item.Id) is { } info) options.Add(new(Loc.T("act_info"), "ic_info.png", () => ShowInfoAsync(info, item.IsMine)));
		// One delete per scope, whatever the message holds: a message with a file loses the file with it.
		options.Add(new(Loc.T("act_delete_me"), "ic_delete.png", async () =>
		{
			if (!await Dialogs.AlertAsync(Loc.T("chat_delete_me_title"), Loc.T("chat_delete_me_text"), Loc.T("delete"), Loc.T("cancel"))) return;
			if (rec != null && rec.State != AttachmentState.Removed) Client.DeleteFile(conv, index);
			Client.DeleteMessage(conv, index);
			_flat.RemoveAll(x => x.Id == item.Id);   // the line on screen (the item may come from the viewer)
			ClearUnreadLine();
			ApplyAlbums();
		}));
		if (item.IsMine && item.State != MessageState.Retracted) options.Add(new(Loc.T("act_delete_all"), "ic_delete_all.png", async () =>
		{
			if (await Dialogs.AlertAsync(Loc.T("chat_delete_all_title"), Loc.T("chat_delete_all_text"), Loc.T("delete"), Loc.T("cancel")))
			{
				if (rec != null && rec.State != AttachmentState.Removed) await Client.RetractFileAsync(conv, index);   // the file goes too, on their side
				await Client.RetractMessageAsync(conv, index);
				ClearUnreadLine();
				Refresh(item.Id);
			}
		}));

		return options;
	}

	// ------------------------------------------------------------------
	// Reactions: a horizontal bar, opened by a long press on a message, a picture or a file (or by "React" in the menu)
	// ------------------------------------------------------------------

	[ObservableProperty] private bool _isReactionBarOpen;
	private ChatItem? _reactionTarget;

	// ------------------------------------------------------------------
	// Editing a message: a bare field over the chat, a tick to confirm, a tap outside to cancel
	// ------------------------------------------------------------------

	[ObservableProperty] private bool _isEditOpen;
	[ObservableProperty] private string _editText = "";
	private (Conversation Conv, int Index, ChatItem Item)? _editTarget;

	/// <summary>Asked when the field opens: the page gives it the focus (and so the keyboard).</summary>
	public event Action? EditRequested;

	/// <summary>The tick: the new text goes out (an empty or unchanged one changes nothing).</summary>
	[RelayCommand]
	private async Task ConfirmEditAsync()
	{
		var target = _editTarget;
		var text = EditText.Trim();
		CloseEdit();
		if (target is not { } t || text.Length == 0 || text == t.Item.Text) return;
		await Client.EditMessageAsync(t.Conv, t.Index, text);
		ClearUnreadLine();
		Refresh(t.Item.Id);
	}

	/// <summary>A tap outside the field: nothing changes.</summary>
	[RelayCommand] private void CancelEdit() => CloseEdit();

	/// <summary>The field is closed (confirmed or cancelled): the page folds the keyboard away.</summary>
	public event Action? EditClosed;

	private void CloseEdit() { IsEditOpen = false; _editTarget = null; EditText = ""; EditClosed?.Invoke(); }

	private void OpenReactionBar(ChatItem item) { _reactionTarget = item; IsReactionBarOpen = true; }

	[RelayCommand] private void CloseReactionBar() => IsReactionBarOpen = false;

	/// <summary>A long press: the reaction bar on what others wrote; on my own messages (nothing to react to) the usual menu.</summary>
	[RelayCommand]
	private Task LongPressAsync(ChatItem item)
	{
		if (item.IsSeparator || item.IsDeleted) return Task.CompletedTask;
		if (item.IsMine) return ActionsAsync(item);
		OpenReactionBar(item);
		return Task.CompletedTask;
	}

	[RelayCommand]
	private async Task PickReactionAsync(string emoji)
	{
		IsReactionBarOpen = false;
		if (_reactionTarget is { } item) await ReactAsync(item.Id, emoji);
	}

	/// <summary>Puts <paramref name="emoji"/> on the message (the same one again takes it back). Also used by the picture viewer.</summary>
	public async Task ReactAsync(string messageId, string emoji)
	{
		if (_conversation == null || !QuickReactions.Contains(emoji)) return;
		var index = Client.IndexOfMessage(_conversation, messageId);
		if (index == 0) return;
		try { await Client.ReactAsync(_conversation, index, emoji); }
		catch (ClientException ex) { await Alert(ErrorText.Of(ex)); return; }
		ReloadReactions(messageId);
		ClearUnreadLine();
		RefreshReacted(messageId);
	}

	/// <summary>Redraws a line whose reactions changed; on the last line, which grows, the list follows so it stays fully in view.</summary>
	private void RefreshReacted(string id)
	{
		Refresh(id);
		if (_flat.Count > 0 && _flat[^1].Id == id) ScrollToEndRequested?.Invoke(null);
	}

	/// <summary>Starts a reply to this message (menu entry, or a swipe to the right on the bubble).</summary>
	[RelayCommand]
	private void Reply(ChatItem item)
	{
		if (item.IsDeleted || item.IsSeparator) return;
		_replyTarget = item;
		var file = _conversation == null ? null : Client.Attachments.Store.Find(item.Id);
		var said = file != null && _conversation != null && Client.FindMessage(_conversation, item.Id) is { } target ? PreviewText.Of(Client, target) : item.Text;   // "Photo", never a path
		ReplyBannerThumb = file == null ? null : ThumbOf(file);
		ReplyBanner = Loc.T("chat_reply_banner", item.Author, said.Length > 40 ? said[..40] + "…" : said);
	}

	/// <summary>The popup of actions on a message: an icon and a label per row.</summary>
	public ObservableCollection<MenuOption> MenuItems { get; } = [];
	[ObservableProperty] private bool _isMenuOpen;

	[RelayCommand] private void CloseMenu() => IsMenuOpen = false;

	[RelayCommand]
	private async Task PickMenuAsync(MenuOption option)
	{
		IsMenuOpen = false;
		try { await option.Run(); }
		catch (ClientException ex) { await Alert(ErrorText.Of(ex)); }
		catch (InvalidOperationException ex) { await Alert(ErrorText.Of(ex)); }
	}

	/// <summary>The most conversations a message is forwarded to at once (the relay limits how fast one sends).</summary>
	private const int ForwardMax = 20;

	private async Task ForwardAsync(Conversation source, int index)
	{
		var targets = Client.GetConversations().Where(s => s.Conversation.HistoryKey != source.HistoryKey).ToList();
		if (targets.Count == 0) { await Alert(Loc.T("chat_forward_none")); return; }

		var titles = targets.Select(t => t.IsGroup ? Loc.T("chat_forward_group", t.Title) : t.Title).ToArray();
		var picked = await Dialogs.ChooseManyAsync(Loc.T("chat_forward_to"), Loc.T("act_forward"), Loc.T("cancel"), ForwardMax, titles);
		if (picked == null || picked.Count == 0) return;

		var failed = 0;
		foreach (var i in picked)
		{
			try { await Client.ForwardAsync(source, index, targets[i].Conversation); }
			catch (Exception ex) when (ex is ClientException or InvalidOperationException) { failed++; }
		}
		await Alert(failed == 0 ? Loc.T("chat_forwarded") : Loc.T("share_failed", failed));
	}

	private static Task Alert(string message) => Dialogs.AlertAsync("FreeSpeech", message, Loc.T("ok"));
}