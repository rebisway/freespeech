/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.History;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>
/// A conversation: the newest messages first, older ones loaded when the user scrolls up, live updates, and the actions on a message
/// (reply, edit, delete for me or for everyone, forward, react, files). Every rule lives in the client library.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
	private const int PageSize = 30;
	private const int MaxThumbnail = 480;

	private readonly MainViewModel _owner;
	private readonly DesktopSession _session;
	private readonly Conversation _conversation;
	private HistoryCursor? _oldest;
	private HistoryCursor? _newest;
	private ChatItem? _replyTarget;
	private ChatItem? _reactionTarget;
	private IReadOnlyDictionary<string, List<Reaction>>? _reactions;
	private int _typingToken;
	private int _searchToken;
	private string? _focusId;

	[ObservableProperty] private string _draft = "";
	[ObservableProperty] private string _error = "";
	[ObservableProperty] private bool _showBack;
	[ObservableProperty] private bool _hasMoreBefore;
	[ObservableProperty] private string? _photo;
	[ObservableProperty] private bool _isReadOnly;

	/// <summary>"is typing…" under the name (the other side, or a member of the group); empty when nobody types.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(IsTyping))] private string _typingText = "";

	[ObservableProperty] private string _replyBanner = "";
	[ObservableProperty] private bool _isMenuOpen;
	[ObservableProperty] private bool _isReactionBarOpen;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(MuteIcon))] private bool _muted;

	/// <summary>The search panel covers the messages; the magnifier in the bar opens and closes it.</summary>
	[ObservableProperty] private bool _isSearchOpen;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(NoResults))] private string _searchText = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(NoResults))] private bool _searchDone;

	public ChatViewModel(MainViewModel owner, ConversationRow row)
	{
		_owner = owner;
		_session = owner.Session;
		_conversation = row.Conversation;
		Title = row.Title;
		Key = row.Key;
		IsGroup = row.IsGroup;
		Photo = row.Photo;
		Muted = Prefs.IsMuted(Key);
		var client = _session.Client;
		IsReadOnly = _conversation.Group is { Active: false } || (_conversation.Group == null && client.FindContact(_conversation.ContactId!) == null);   // a group I left or a contact I removed: readable, nothing can be sent
		_session.MessageReceived += OnReceived;
		_session.PinsChanged += OnPinsChanged;
		_session.ConversationsChanged += RefreshIdentity;   // my name in the group may be changed from its info screen
		Controls.MarkupText.LinkClicked = url => _ = OpenLinkAsync(url);
		_session.MessageChanged += OnChanged;
		_session.FileCompleted += Refresh;   // a download ended: the bubble turns into a thumbnail
		_session.FileCompleted += _ => RefreshFiles();
		_session.ReadReceived += OnReadReceived;
		_session.TypingReceived += OnTyping;
		WindowFocus.Changed += OnForegroundChanged;
		Pending.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPending));
		RefreshIdentity();
		RefreshPinned();
		ContactTrust = _conversation.ContactId is { } contactId && _conversation.Group == null ? client.TrustOf(contactId) : 0;
		Load();
	}

	[ObservableProperty] private string _title = "";
	public string Key { get; }
	public bool IsGroup { get; }
	public bool IsTyping => TypingText.Length > 0;
	public bool CanWrite => !IsReadOnly;
	public bool HasReply => ReplyBanner.Length > 0;
	public bool HasPending => Pending.Count > 0;
	public bool NoResults => SearchDone && Results.Count == 0 && SearchText.Trim().Length >= FreeSpeechClient.MinSearchLength;
	public Avalonia.Media.Geometry? MuteIcon => Icon(Muted ? "Icon.bell_off" : "Icon.bell");

	public ObservableCollection<ChatItem> Items { get; } = [];
	public ObservableCollection<SearchResult> Results { get; } = [];

	/// <summary>Files chosen but not sent yet (shown as thumbnails under the messages).</summary>
	public ObservableCollection<PendingFile> Pending { get; } = [];

	/// <summary>The popup of actions on a message: an icon and a label per row.</summary>
	public ObservableCollection<MenuOption> MenuItems { get; } = [];

	public string HintText => Loc.T("chat_hint");
	public string SendLabel => Loc.T("d_send");
	public string SearchHint => Loc.T("search_hint");
	public string SearchNoneText => Loc.T("search_none");
	public string MuteTip => Loc.T("a11y_mute");
	public string GroupInfoTip => Loc.T("a11y_group_info");
	public string AttachTip => Loc.T("a11y_attach");
	public string SendTip => Loc.T("a11y_send");
	public string ReadOnlyText => Loc.T("chat_left_notice");
	public string[] ReactionEmojis => QuickReactions;

	/// <summary>The reaction bar, left to right.</summary>
	public static readonly string[] QuickReactions = ["👍", "❤️", "😂", "😮", "😢", "🙏", "🤩", "😍", "👏", "😱"];

	/// <summary>Raised when the list should jump to its last message (after sending, receiving, opening).</summary>
	public event Action<int?>? ScrollToEndRequested;

	/// <summary>Index of the unread separator at the last load (null: the end). The view reads it when it attaches, after the first load already ran.</summary>
	public int? OpenAt { get; private set; }

	/// <summary>Raised when the list should jump to a given line (a search result), centred.</summary>
	public event Action<int>? ScrollToItemRequested;

	public event Action? BackRequested;

	private FreeSpeechClient Client => _session.Client;

	private static Avalonia.Media.Geometry? Icon(string key) => Avalonia.Application.Current is { } app && app.TryGetResource(key, null, out var found) ? found as Avalonia.Media.Geometry : null;

	public void Detach()
	{
		_session.MessageReceived -= OnReceived;
		_session.PinsChanged -= OnPinsChanged;
		_session.ConversationsChanged -= RefreshIdentity;
		Controls.MarkupText.LinkClicked = null;
		_session.MessageChanged -= OnChanged;
		_session.FileCompleted -= Refresh;
		_session.ReadReceived -= OnReadReceived;
		_session.TypingReceived -= OnTyping;
		WindowFocus.Changed -= OnForegroundChanged;
	}

	[RelayCommand] private void Back() => BackRequested?.Invoke();

	/// <summary>The window comes back: what is on screen counts as read.</summary>
	private void OnForegroundChanged()
	{
		if (WindowFocus.IsActive) MarkRead();
	}

	// ------------------------------------------------------------------
	// Header: typing, mute, group
	// ------------------------------------------------------------------

	/// <summary>Typing sends a throw-away signal (at most one every few seconds, see the client); an emptied field sends nothing.</summary>
	partial void OnDraftChanged(string value)
	{
		if (value.Length > 0 && WindowFocus.IsActive) _session.NotifyTyping(_conversation);
	}

	private void OnTyping(string historyKey, string who)
	{
		if (historyKey != Key) return;
		TypingText = _conversation.Group != null ? Loc.T("chat_typing_group", Client.DisplayNameOf(_conversation, who)) : Loc.T("chat_typing");
		// The indicator fades by itself when the signals stop (they come every few seconds while the other side types).
		var token = ++_typingToken;
		_ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (token == _typingToken) TypingText = ""; }));
	}

	/// <summary>Silent mode for this conversation: no popup, the unread counter still counts.</summary>
	[RelayCommand]
	private void ToggleMute()
	{
		Muted = !Muted;
		Prefs.SetMuted(Key, Muted);
		_session.RaiseConversationsChanged();
	}

	[RelayCommand] private void OpenGroupInfo() => _owner.Push(new GroupInfoViewModel(_owner, Key), Loc.T("a11y_group_info"));

	public bool IsDirect => !IsGroup;

	// ------------------------------------------------------------------
	// Pinned messages (groups): a banner under the bar, the menu of a message offers to pin it (moderators)
	// ------------------------------------------------------------------

	private List<FreeSpeech.Client.History.HistoryEntry> _pinned = [];
	private int _pinnedAt;

	[ObservableProperty] private bool _showPinned;
	[ObservableProperty] private int _pinnedMaxLines = 1;

	private string? _expandedPinId;

	/// <summary>The pinned messages this device has already shown whole once (a pin is shown whole the first time, then on one line).</summary>
	private static HashSet<string> SeenPins => _seenPins ??= [.. Prefs.Get("seen_pins", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)];
	private static HashSet<string>? _seenPins;
	[ObservableProperty] private string _pinnedPreview = "";
	[ObservableProperty] private string _pinnedCount = "";

	private void RefreshPinned()
	{
		_pinned = _conversation.Group == null ? [] : [.. Client.GetPinned(_conversation)];
		_pinnedAt = _pinned.Count == 0 ? 0 : Math.Clamp(_pinnedAt, 0, _pinned.Count - 1);
		ShowPinned = _pinned.Count > 0;
		PinnedPreview = _pinned.Count == 0 ? "" : PreviewText.Of(Client, _pinned[_pinnedAt]);
		PinnedCount = _pinned.Count > 1 ? $"{_pinnedAt + 1}/{_pinned.Count}" : "";

		// The first time a pinned message is shown it is shown whole; once seen, the banner keeps one line.
		if (_pinned.Count > 0 && SeenPins.Add(_pinned[_pinnedAt].Id))
		{
			_expandedPinId = _pinned[_pinnedAt].Id;
			Prefs.Set("seen_pins", string.Join('\n', SeenPins.TakeLast(300)));
		}
		PinnedMaxLines = _pinned.Count > 0 && _expandedPinId == _pinned[_pinnedAt].Id ? 8 : 1;
	}

	/// <summary>A click on the banner: the list goes to the pinned message shown, and the banner moves on to the next one.</summary>
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
		if (historyKey == Key) RefreshPinned();
	}

	/// <summary>In a group where I use a name or a picture of my own: who I am there, in the bar, so identities are not mixed up.</summary>
	[ObservableProperty] private bool _showMyIdentity;
	[ObservableProperty] private string _myIdentityText = "";
	[ObservableProperty] private string? _myGroupPhoto;
	[ObservableProperty] private string _myGroupName = "";

	private void RefreshIdentity()
	{
		if (_conversation.Group is { } group && Client.ResolveGroup(group.GroupId) is { } rec && Client.Groups.HasOwnIdentity(rec))
		{
			MyGroupName = Client.Groups.MyNameIn(rec);
			MyGroupPhoto = Client.Groups.MyPhotoIn(rec);
			MyIdentityText = Loc.T("chat_my_identity", MyGroupName);
			ShowMyIdentity = true;
		}
		else ShowMyIdentity = false;
	}

	/// <summary>A link in a formatted message: the real address is shown and confirmed before anything opens.</summary>
	private static async Task OpenLinkAsync(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
		if (!await Ui.ConfirmAsync(Loc.T("link_open_title"), uri.AbsoluteUri, Loc.T("link_open"), Loc.T("cancel"))) return;
		try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { await Ui.AlertAsync(Loc.T("link_open_title"), ex.Message, Loc.T("ok")); }
	}

	/// <summary>How far I vouch for the contact of a direct chat (0 = none, 1 = a contact, 2 = a contact whose safety number I compared); shown by a check in the bar.</summary>
	[ObservableProperty, NotifyPropertyChangedFor(nameof(ContactUnverified), nameof(ContactVerified))] private int _contactTrust;
	public bool ContactUnverified => IsDirect && ContactTrust == 1;
	public bool ContactVerified => IsDirect && ContactTrust == 2;

	/// <summary>The check in the bar: the safety number of this contact, to compare it.</summary>
	[RelayCommand]
	private void OpenContactSafety()
	{
		if (_conversation.ContactId is { } id) _owner.Push(new SafetyViewModel(_owner, id), Loc.T("safety_title"));
	}
	public string ContactMenuTip => Loc.T("a11y_contact_menu");

	/// <summary>The check after a group member's name: the safety number of that contact, to compare it.</summary>
	[RelayCommand]
	private void OpenSafety(ChatItem item)
	{
		if (item.AuthorTrust > 0) _owner.Push(new SafetyViewModel(_owner, item.AuthorId), Loc.T("safety_title"));
	}

	/// <summary>A conversation with a contact: give them a name only I see, or compare the safety number with them.</summary>
	[RelayCommand]
	private async Task ContactMenuAsync()
	{
		var client = _session.Client;
		if (_conversation.ContactId is not { } id || client.FindContact(id) is not { } contact) return;
		var rename = Loc.T("contact_rename_title");
		var verify = Loc.T("safety_title");
		var picked = await Ui.ChooseAsync(Title, [rename, verify], Loc.T("cancel"));
		if (picked == 1) { _owner.Push(new SafetyViewModel(_owner, id), verify); return; }
		if (picked != 0) return;

		var name = await Ui.PromptAsync(rename, Loc.T("contact_rename_text", contact.Nickname), Loc.T("ok"), Loc.T("cancel"), contact.DisplayName);
		if (name == null) return;   // cancelled
		try
		{
			client.RenameContact(contact, name);
			Title = contact.DisplayName;
			_session.RaiseConversationsChanged();
		}
		catch (ClientException ex) { await Ui.AlertAsync(rename, ErrorText.Of(ex), Loc.T("ok")); }
	}

	// ------------------------------------------------------------------
	// Search in this conversation
	// ------------------------------------------------------------------

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
		if (text.Length < FreeSpeechClient.MinSearchLength) { Results.Clear(); SearchDone = false; return; }
		_ = Task.Run(async () =>
		{
			await Task.Delay(250);
			if (token != _searchToken) return;
			var hits = Client.Search(text, 200, _conversation).Select(h => new SearchResult(h, text)).ToList();
			Dispatcher.UIThread.Post(() =>
			{
				if (token != _searchToken) return;
				Results.Clear();
				foreach (var h in hits) Results.Add(h);
				SearchDone = true;
				OnPropertyChanged(nameof(NoResults));
			});
		});
	}

	/// <summary>A click on a result: the panel closes and the list goes to that message (older pages are read until it is there), outlined for a moment.</summary>
	[RelayCommand]
	private void OpenResult(SearchResult result)
	{
		_focusId = result.MessageId;
		IsSearchOpen = false;
		SearchText = "";
		FocusMessage();
	}

	private void FocusMessage()
	{
		var id = _focusId;
		_focusId = null;
		if (id == null) return;
		while (!Items.Any(i => i.Id == id) && HasMoreBefore) LoadOlder();
		var item = Items.FirstOrDefault(i => i.Id == id);
		if (item == null) { ScrollToEndRequested?.Invoke(null); return; }
		item.Found = true;
		ScrollToItemRequested?.Invoke(Items.IndexOf(item));
		_ = Task.Delay(3000).ContinueWith(_ => Dispatcher.UIThread.Post(() => item.Found = false));
	}

	// ------------------------------------------------------------------
	// Reading
	// ------------------------------------------------------------------

	private void Load()
	{
		_reactions = null;
		var unread = Client.UnreadCount(_conversation);
		var page = Client.GetLatestMessages(_conversation, PageSize);
		Items.Clear();

		// The separator goes before the oldest unread message: the unread ones are the last `unread` received lines.
		var firstUnread = unread == 0 ? null : page.Entries.Where(e => e.Direction == MessageDirection.Received).Reverse().Take(unread).LastOrDefault();
		ChatItem? separator = null;
		foreach (var e in page.Entries.Where(e => e.State != MessageState.DeletedForMe))
		{
			if (firstUnread != null && e.Id == firstUnread.Id) { separator = Separator(unread); Items.Add(separator); }
			Items.Add(Build(e));
		}
		_oldest = page.Oldest;
		_newest = page.Newest;
		HasMoreBefore = page.HasMoreBefore;
		int? separatorIndex = separator == null ? null : Items.IndexOf(separator);
		OpenAt = separatorIndex;
		ScrollToEndRequested?.Invoke(separatorIndex);   // opening: at the first unread message, else at the end
		RefreshFilesSoon();
		MarkRead();
	}

	private static ChatItem Separator(int unread) => new()
	{
		Id = "unread-separator", IsMine = false, Author = "", Text = Loc.T("chat_unread"),
		Time = "", State = MessageState.Normal, IsSeparator = true,
	};

	/// <summary>What is on screen counts as read (while the window is in use): counters drop, the contact gets a receipt.</summary>
	private void MarkRead()
	{
		if (!WindowFocus.IsActive) return;
		Client.SendReadReceipts = Prefs.SendReadReceipts;
		_ = MarkReadAsync();
	}

	private async Task MarkReadAsync()
	{
		try { await Client.MarkReadAsync(_conversation); }
		catch (Exception ex) when (ex is ClientException or HttpRequestException or InvalidOperationException) { /* the receipt is best effort */ }
		_session.RaiseConversationsChanged();
	}

	/// <summary>The contact read my messages: the ticks turn gold.</summary>
	private void OnReadReceived(string historyKey)
	{
		if (historyKey != Key) return;
		foreach (var item in Items.Where(i => i.IsMine && !i.ReadByPeer && !i.IsSeparator).ToList()) Refresh(item.Id);
	}

	/// <summary>Adds the next older page on top (the user scrolled up). Returns how many lines were added.</summary>
	public int LoadOlder()
	{
		if (!HasMoreBefore || _oldest == null) return 0;
		var page = Client.GetMessagesBefore(_conversation, _oldest.Value, PageSize);
		var entries = page.Entries.Where(e => e.State != MessageState.DeletedForMe).ToList();
		for (var i = entries.Count - 1; i >= 0; i--) Items.Insert(0, Build(entries[i]));
		_oldest = page.Oldest ?? _oldest;
		HasMoreBefore = page.HasMoreBefore;
		return entries.Count;
	}

	/// <summary>Adds whatever arrived after the last line we show.</summary>
	private void AppendNew()
	{
		if (_newest == null) { Load(); return; }
		while (true)
		{
			var page = Client.GetMessagesAfter(_conversation, _newest.Value, PageSize);
			foreach (var e in page.Entries.Where(e => e.State != MessageState.DeletedForMe)) Items.Add(Build(e));
			_newest = page.Newest ?? _newest;
			if (!page.HasMoreAfter) break;
		}
		ScrollToEndRequested?.Invoke(null);
		RefreshFilesSoon();
	}

	private string ReactionsOf(string id)
	{
		_reactions ??= Client.GetReactions(_conversation);
		if (!_reactions.TryGetValue(id, out var list) || list.Count == 0) return "";
		return string.Join("  ", list.GroupBy(r => r.Emoji).Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key));
	}

	private ChatItem Build(HistoryEntry e)
	{
		var conv = _conversation;
		var mine = e.Direction == MessageDirection.Sent;
		var author = mine ? Loc.T("chat_me") : conv.Group != null ? Client.DisplayNameOf(conv, e.From ?? "") : Title;

		string? reply = null;
		Avalonia.Media.Imaging.Bitmap? replyThumb = null;
		if (e.ReplyToId != null)
		{
			var target = Client.FindMessage(conv, e.ReplyToId);
			if (target == null) reply = Loc.T("chat_reply_missing");
			else if (Client.Attachments.Store.Find(target.Id) is { } targetFile)
			{
				reply = PreviewText.Of(Client, target);   // "Photo", "Video: name"… never the path
				if (targetFile is { State: AttachmentState.Complete, Path: { } tp } && targetFile.Mime.StartsWith("image/", StringComparison.Ordinal) && File.Exists(tp)) replyThumb = ChatItem.Decode(tp, 80);
			}
			else reply = Snippet(target);
		}

		var fileRec = Client.Attachments.Store.Find(e.Id);
		var done = fileRec is { State: AttachmentState.Complete, Path: { } p } && File.Exists(p) ? p : null;
		var image = done != null && fileRec!.Mime.StartsWith("image/", StringComparison.Ordinal) ? done : null;
		var video = done != null && fileRec!.Mime.StartsWith("video/", StringComparison.Ordinal) ? done : null;
		var audio = done != null && fileRec!.Mime.StartsWith("audio/", StringComparison.Ordinal) ? done : null;
		var bitmap = image == null ? null : ChatItem.Decode(image, MaxThumbnail);
		return new ChatItem
		{
			Id = e.Id, At = e.At, IsMine = mine, Author = author, Text = fileRec == null ? PreviewText.FileLabel(e.Text) ?? e.Text : e.Text, Edited = e.Edited, State = e.State,
			Time = e.At.ToLocalTime().ToString("HH:mm"), IsFile = fileRec != null, NeedsRefresh = fileRec == null ? PreviewText.FileLabel(e.Text) != null : fileRec.State != AttachmentState.Complete, FileStatus = FileStatusOf(e.Id), FileState = fileRec?.State,
			ReadByPeer = Client.IsReadByPeer(conv, e), ReplyPreview = reply, ReplyThumb = replyThumb,
			ImagePath = bitmap == null ? null : image, Image = bitmap, VideoPath = video, VideoName = fileRec?.Name ?? "", AudioPath = audio,
			ShowAuthor = !mine && conv.Group != null,
			AuthorId = e.From ?? "", AuthorTrust = !mine && conv.Group != null ? Client.TrustOf(e.From ?? "") : 0,
			AuthorPhoto = !mine && conv.Group != null ? Client.MemberPhotoIn(conv.Group, e.From ?? "") : null,
			ReactionsText = e.State is MessageState.Retracted ? "" : ReactionsOf(e.Id),
		};
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
			AttachmentState.Failed => $"{Loc.T("chat_file_failed")} · {size}",
			_ => $"{Loc.T("chat_file_offered")} · {size}",
		};
	}

	// ------------------------------------------------------------------
	// Live updates
	// ------------------------------------------------------------------

	private void OnReceived(ChatMessage m)
	{
		if (m.Conversation.HistoryKey != Key) return;
		if (!m.Mine) { _typingToken++; TypingText = ""; }   // the message is here: nobody is typing it any more
		if (WindowFocus.IsActive) ClearUnreadLine();   // something happened and the chat is on screen: the "new messages" line goes
		AppendNew();
		RefreshPinned();   // a pinned message that was missing may be here now
		if (!m.Mine) MarkRead();   // the chat is open: this message is seen at once
	}

	/// <summary>The "new messages" line only says where the unread ones start: it goes at the first event in the chat (a message, a reaction, an edit, a deletion; mine or theirs).</summary>
	private void ClearUnreadLine()
	{
		foreach (var line in Items.Where(i => i.IsSeparator).ToList()) Items.Remove(line);
	}

	private void OnChanged(MessageChange c)
	{
		if (c.Conversation.HistoryKey != Key) return;
		if (WindowFocus.IsActive) ClearUnreadLine();   // a reaction, an edit or a deletion is an event too
		if (c.Kind != MessageChangeKind.Reacted) RefreshPinned();   // an edit or a deletion changes the banner
		if (c.Kind == MessageChangeKind.Reacted) _reactions = null;
		Refresh(c.MessageId);
	}

	/// <summary>Lines of files still waiting for their record or their content are rebuilt (the record is stored just after the message event; a download ends later).</summary>
	private void RefreshFiles()
	{
		foreach (var id in Items.Where(i => i.NeedsRefresh).Select(i => i.Id).ToList()) Refresh(id);
	}

	private void RefreshFilesSoon()
	{
		if (!Items.Any(i => i.NeedsRefresh)) return;
		foreach (var delay in new[] { 1000, 3000, 8000 })
			_ = Task.Delay(delay).ContinueWith(_ => Dispatcher.UIThread.Post(RefreshFiles));
	}

	private void Refresh(string id)
	{
		var i = Items.ToList().FindIndex(x => x.Id == id);
		if (i < 0) return;
		var entry = Client.FindMessage(_conversation, id);
		if (entry == null || entry.State == MessageState.DeletedForMe) Items.RemoveAt(i);
		else Items[i] = Build(entry);
	}

	// ------------------------------------------------------------------
	// Sending
	// ------------------------------------------------------------------

	[RelayCommand]
	private async Task SendAsync()
	{
		var text = Draft.Trim();
		if (text.Length == 0 && Pending.Count == 0) return;
		Error = "";
		if (!await _session.ConfirmLargeSendAsync(Pending.Select(p => p.Path))) return;   // a big file only goes after a yes; everything stays staged on a no
		try
		{
			var files = Pending.ToList();   // nothing leaves before this click: the user could still remove any of them
			var replyTo = _replyTarget?.Id;
			Draft = "";
			Pending.Clear();
			ClearReply();
			if (text.Length > 0) await Client.SendAsync(_conversation, text, replyTo);
			foreach (var f in files) await Client.UploadAsync(_conversation, f.Path);
			ClearUnreadLine();
			AppendNew();
			_session.RaiseConversationsChanged();
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException or HttpRequestException)
		{
			Draft = text;
			Error = ErrorText.Of(ex);
		}
	}

	[RelayCommand] private void CancelReply() => ClearReply();

	private void ClearReply()
	{
		_replyTarget = null;
		ReplyBanner = "";
		OnPropertyChanged(nameof(HasReply));
	}

	partial void OnReplyBannerChanged(string value) => OnPropertyChanged(nameof(HasReply));

	/// <summary>Starts a reply to this message (menu entry).</summary>
	[RelayCommand]
	private void Reply(ChatItem item)
	{
		if (item.IsDeleted || item.IsSeparator) return;
		_replyTarget = item;
		var file = Client.Attachments.Store.Find(item.Id);
		var said = file != null && Client.FindMessage(_conversation, item.Id) is { } target ? PreviewText.Of(Client, target) : item.Text;   // "Photo", never a path
		ReplyBanner = Loc.T("chat_reply_banner", item.Author, said.Length > 40 ? said[..40] + "…" : said);
	}

	// ------------------------------------------------------------------
	// Actions on a message
	// ------------------------------------------------------------------

	/// <summary>The "Info" dialog of a message: when it was sent (to the second), when it arrived here, when it was edited, and how far it went.</summary>
	private async Task ShowInfoAsync(Conversation conv, HistoryEntry entry, bool mine)
	{
		static string Full(DateTime t) => t.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
		static DateTime? OfTicks(long ts) => ts > 0 && ts <= DateTime.MaxValue.Ticks ? new DateTime(ts, DateTimeKind.Utc) : null;
		var lines = new List<string> { $"{Loc.T("info_sent")} : {Full(OfTicks(entry.Ts) ?? entry.At)}" };
		if (!mine) lines.Add($"{Loc.T("info_received")} : {Full(entry.At)}");
		if (entry.EditedTs is { } edited && OfTicks(edited) is { } editedAt) lines.Add($"{Loc.T("info_edited")} : {Full(editedAt)}");
		if (mine && entry.State == MessageState.Normal)
		{
			var queued = Client.Outbox.FirstOrDefault(p => p.Id == entry.Id);
			var status = queued != null ? Loc.T(queued.SentAt == null ? "info_unsent" : "info_delivered")
				: Loc.T(Client.IsReadByPeer(conv, entry) ? "info_read" : "info_confirmed");
			lines.Add($"{Loc.T("info_status")} : {status}");
		}
		await Ui.AlertAsync(Loc.T("info_title"), string.Join("\n", lines), Loc.T("ok"));
	}

	/// <summary>Everything that can be done with this message, each entry keeping its action next to its translated label.</summary>
	public List<MenuOption> OptionsFor(ChatItem item)
	{
		var conv = _conversation;
		var index = Client.IndexOfMessage(conv, item.Id);
		var options = new List<MenuOption>();
		if (index == 0) return options;

		var rec = Client.Attachments.Store.Find(item.Id);
		if (!item.IsDeleted && CanWrite) options.Add(new(Loc.T("act_reply"), "Icon.reply", () => { Reply(item); return Task.CompletedTask; }));
		var hasText = !item.IsDeleted && !item.IsFile && item.Text.Length > 0;
		if (hasText) options.Add(new(Loc.T("act_copy"), "Icon.copy", async () =>
		{
			if (CopyText != null) await CopyText(item.Text);
			Error = "";
		}));
		if (item.IsMine && !item.IsDeleted && !item.IsFile && item.State == MessageState.Normal && CanWrite) options.Add(new(Loc.T("act_edit"), "Icon.edit", async () =>
		{
			var text = await Ui.PromptAsync(Loc.T("chat_edit_title"), Loc.T("chat_edit_text"), Loc.T("act_edit"), Loc.T("cancel"), item.Text, 4000);
			if (!string.IsNullOrWhiteSpace(text)) { await Client.EditMessageAsync(conv, index, text); ClearUnreadLine(); Refresh(item.Id); }
		}));
		if (rec is { State: AttachmentState.Complete, Path: { } openPath } && File.Exists(openPath)) options.Add(new(Loc.T("act_open_file"), "Icon.open", () => { OpenFile(openPath); return Task.CompletedTask; }));
		if (rec is { Direction: AttachmentDirection.Received, State: AttachmentState.Offered or AttachmentState.Receiving or AttachmentState.Failed }) options.Add(new(Loc.T("act_download"), "Icon.download", async () =>
		{
			await Client.DownloadAsync(conv, index);
			Refresh(item.Id);
		}));
		if (rec is { Direction: AttachmentDirection.Received, State: AttachmentState.Receiving, WaitingNetwork: true }) options.Add(new(Loc.T("act_download_anyway"), "Icon.download", async () =>
		{
			await Client.DownloadAsync(conv, index, anyway: true);   // this file only
			Refresh(item.Id);
		}));
		if (!item.IsDeleted && !item.IsMine && CanWrite) options.Add(new(Loc.T("act_react"), "Icon.react", () => { OpenReactionBar(item); return Task.CompletedTask; }, IsReact: true));
		if (!item.IsDeleted) options.Add(new(Loc.T("act_forward"), "Icon.forward", () => ForwardAsync(conv, index)));
		if (!item.IsDeleted && Client.FindMessage(conv, item.Id) is { } info) options.Add(new(Loc.T("act_info"), "Icon.info", () => ShowInfoAsync(conv, info, item.IsMine)));
		if (!item.IsDeleted && Client.CanPin(conv))
		{
			var pinned = Client.IsPinned(conv, item.Id);
			options.Add(new(Loc.T(pinned ? "act_unpin" : "act_pin"), "Icon.pin", async () =>
			{
				try { await Client.PinMessageAsync(conv, index, !pinned); RefreshPinned(); }
				catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Error = ErrorText.Of(ex); }
			}));
		}
		// One delete per scope, whatever the message holds: a message with a file loses the file with it.
		options.Add(new(Loc.T("act_delete_me"), "Icon.delete", async () =>
		{
			if (!await Ui.ConfirmAsync(Loc.T("chat_delete_me_title"), Loc.T("chat_delete_me_text"), Loc.T("delete"), Loc.T("cancel"))) return;
			if (rec != null && rec.State != AttachmentState.Removed) Client.DeleteFile(conv, index);
			Client.DeleteMessage(conv, index);
			Items.Remove(item);
			ClearUnreadLine();
			_session.RaiseConversationsChanged();
		}));
		if (item.IsMine && item.State != MessageState.Retracted && CanWrite) options.Add(new(Loc.T("act_delete_all"), "Icon.delete_all", async () =>
		{
			if (!await Ui.ConfirmAsync(Loc.T("chat_delete_all_title"), Loc.T("chat_delete_all_text"), Loc.T("delete"), Loc.T("cancel"))) return;
			if (rec != null && rec.State != AttachmentState.Removed) await Client.RetractFileAsync(conv, index);   // the file goes too, on their side
			await Client.RetractMessageAsync(conv, index);
			ClearUnreadLine();
			Refresh(item.Id);
		}));
		return options;
	}

	/// <summary>Set by the view: puts text on the clipboard.</summary>
	public Func<string, Task>? CopyText { get; set; }

	[RelayCommand]
	private async Task PickMenuAsync(MenuOption option)
	{
		IsMenuOpen = false;
		try { await option.Run(); }
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Error = ErrorText.Of(ex); }
	}

	/// <summary>A file or a picture clicked: opened in the system's program when it is here, asked for when it is not.</summary>
	[RelayCommand]
	private async Task OpenFileOfAsync(ChatItem item)
	{
		if (item.IsSeparator) return;
		var rec = Client.Attachments.Store.Find(item.Id);
		if (rec == null) return;
		try
		{
			if (rec is { State: AttachmentState.Complete, Path: { } path } && File.Exists(path)) OpenFile(path);
			else if (rec.Direction == AttachmentDirection.Received && rec.State is AttachmentState.Offered or AttachmentState.Failed)
			{
				var index = Client.IndexOfMessage(_conversation, item.Id);
				if (index > 0) { await Client.DownloadAsync(_conversation, index); Refresh(item.Id); }
			}
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Error = ErrorText.Of(ex); }
	}

	private static void OpenFile(string path)
	{
		try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Diag.Log($"Open file: {ex.GetType().Name}"); }
	}

	private async Task ForwardAsync(Conversation source, int index)
	{
		var targets = Client.GetConversations().Where(s => s.Conversation.HistoryKey != source.HistoryKey && !s.IsArchived).ToList();
		if (targets.Count == 0) { Error = Loc.T("chat_forward_none"); return; }

		var titles = targets.Select(t => t.IsGroup ? Loc.T("chat_forward_group", t.Title) : t.Title).ToList();
		var picked = await Ui.ChooseManyAsync(Loc.T("chat_forward_to"), titles, Loc.T("act_forward"), Loc.T("cancel"), max: 20);   // the relay limits how fast one sends
		if (picked == null || picked.Count == 0) return;

		var failed = 0;
		foreach (var i in picked)
		{
			try { await Client.ForwardAsync(source, index, targets[i].Conversation); }
			catch (Exception ex) when (ex is ClientException or InvalidOperationException) { failed++; }
		}
		Error = "";
		await Ui.AlertAsync("FreeSpeech", failed == 0 ? Loc.T("chat_forwarded") : Loc.T("share_failed", failed), Loc.T("ok"));
	}

	// ------------------------------------------------------------------
	// Reactions: a bar of emojis, opened by "React" in the menu
	// ------------------------------------------------------------------

	private void OpenReactionBar(ChatItem item) { _reactionTarget = item; IsReactionBarOpen = true; }

	[RelayCommand] private void CloseReactionBar() => IsReactionBarOpen = false;

	[RelayCommand]
	private async Task PickReactionAsync(string emoji)
	{
		IsReactionBarOpen = false;
		if (_reactionTarget is { } item) await ReactAsync(item.Id, emoji);
	}

	/// <summary>Puts <paramref name="emoji"/> on the message (the same one again takes it back).</summary>
	public async Task ReactAsync(string messageId, string emoji)
	{
		if (!QuickReactions.Contains(emoji)) return;
		var index = Client.IndexOfMessage(_conversation, messageId);
		if (index == 0) return;
		try { await Client.ReactAsync(_conversation, index, emoji); }
		catch (Exception ex) when (ex is ClientException or InvalidOperationException) { Error = ErrorText.Of(ex); return; }
		_reactions = null;
		ClearUnreadLine();
		Refresh(messageId);
	}

	// ------------------------------------------------------------------
	// Files waiting under the composer
	// ------------------------------------------------------------------

	/// <summary>Waits under the composer: the send button sends it. A picture is reduced first (never sent heavier than needed).</summary>
	public void Stage(string path)
	{
		if (!CanWrite || Pending.Any(p => p.Path == path)) return;
		var name = Path.GetFileName(path);
		var small = ImageShrink.Shrink(path);
		if (small != null && new FileInfo(small).Length < new FileInfo(path).Length) (path, name) = (small, Path.GetFileNameWithoutExtension(name) + Path.GetExtension(small));
		if (!Pending.Any(p => p.Path == path)) Pending.Add(new PendingFile(path, name));
	}

	[RelayCommand]
	private async Task AttachAsync()
	{
		foreach (var path in await Ui.PickFilesAsync(Loc.T("chat_pick_file"))) Stage(path);
	}

	[RelayCommand] private void RemovePending(PendingFile file) => Pending.Remove(file);
}
