/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Client.Attachments;
using FreeSpeech.Client.Discovery;
using FreeSpeech.Mobile.Localization;

namespace FreeSpeech.Mobile.Services;

public enum AppState { Starting, NeedsOnboarding, NeedsPhrases, Ready }

/// <summary>A short notification shown on the home screen: who, one line, and where a tap leads (a Shell route).</summary>
public sealed record BannerInfo(string Title, string Text, string Target);

/// <summary>
/// The app's single <see cref="FreeSpeechClient"/> and its life cycle. All messaging logic is in the library; this class
/// only creates it with the phone's folders, runs its blocking calls off the UI thread, and re-raises its events on the
/// UI thread so view models can touch their bindings.
/// </summary>
public sealed class ClientService
{
	// Development default: the PC's server through `adb reverse` (see the csproj). A real server is typed at onboarding.
#if DEBUG
	public const string DefaultServer = "http://127.0.0.1:5299";   // the developer's PC, reached over adb reverse
#else
	public static readonly string DefaultServer = BuildDefaults.Get("DefaultServer");   // chosen at build time, see BuildDefaults
#endif

	private FreeSpeechClient? _client;
	private bool _hooked;

	public AppState State { get; private set; } = AppState.Starting;

	public FreeSpeechClient Client => _client ?? throw new InvalidOperationException("The client is not started.");

	public string ServerUrl
	{
		get => Preferences.Default.Get("server_url", DefaultServer);
		set => Preferences.Default.Set("server_url", value.Trim());
	}

	public event Action? ConversationsChanged;
	public event Action<ChatMessage>? MessageReceived;
	public event Action<MessageChange>? MessageChanged;
	public event Action<string>? Notice;

	/// <summary>A received file finished downloading (its message id): the chat shows it.</summary>
	public event Action<string>? FileCompleted;

	/// <summary>The user is looking at this conversation (history key): its notification banner goes away.</summary>
	public event Action<string>? ChatSeen;

	/// <summary>A contact read my messages (history key of the conversation).</summary>
	public event Action<string>? ReadReceived;

	/// <summary>Someone is typing: the conversation's history key and who (a contact or a group member).</summary>
	public event Action<string, string>? TypingReceived;

	/// <summary>The pinned messages of a group changed (history key of the group).</summary>
	public event Action<string>? PinsChanged;

	/// <summary>Show my typing to the other side, and theirs to me (reciprocal; on by default).</summary>
	public static bool TypingIndicator
	{
		get => Preferences.Default.Get("typing_indicator", true);
		set => Preferences.Default.Set("typing_indicator", value);
	}

	/// <summary>The user is typing in a conversation: a throw-away signal, at most one every few seconds.</summary>
	public void NotifyTyping(Conversation conversation)
	{
		if (State != AppState.Ready) return;
		Client.SendTypingIndicator = TypingIndicator;
		_ = Client.SendTypingAsync(conversation);
	}

	/// <summary>False while the app is in the background: what arrives then is not read yet.</summary>
	public static bool InForeground
	{
		get => _inForeground;
		set
		{
			var returned = value && !_inForeground;
			_inForeground = value;
			if (!value)
			{
				_current?.HoldWhilePending();
				// Letters delivered but not yet confirmed: raise the WAITING flag now, the app may be gone when the contact comes back.
				var client = _current?.Client;
				if (client != null) _ = Task.Run(async () => { try { await client.FlagUnconfirmedAsync(); } catch (Exception) { /* best effort */ } });
			}
			if (returned) ForegroundReturned?.Invoke();
		}
	}

	/// <summary>The app came back on screen (or the PIN was entered): an open chat then counts what it shows as read.</summary>
	public static event Action? ForegroundReturned;

	private static bool _inForeground = true;
	private static ClientService? _current;
	private bool _holding;

	public ClientService()
	{
		_current = this;
		_ = RecoveryPhrases.PurgeAsync();   // earlier versions kept the drawn phrases on the phone: gone now
	}

	/// <summary>
	/// The app just went to the background with messages still waiting for their recipient (or a file being served): keeps the process
	/// alive for a few minutes, so the retry that follows the recipient's push wake-up still finds us. Swiping the app away no longer
	/// ends the send. Android shows a discreet "sending" notification meanwhile.
	/// </summary>
	/// <summary>A letter the relay has not taken yet (one delivered and only waiting for its confirmation does not need the app kept alive).</summary>
	private bool HasUnsent() => Client.Outbox.Any(p => p.SentAt == null && !p.Parked && DateTime.UtcNow - p.CreatedAt < FreeSpeechClient.OutboxGiveUpAfter);   // a letter older than that is no longer retried: it must not keep the app awake

	private void HoldWhilePending()
	{
		if (_holding || State != AppState.Ready || KeepAlive is not { } keep) return;
		if (!HasUnsent() && !Client.Attachments.IsServing) return;
		_holding = true;
		keep.Start(Loc.T("notif_pending"));
		_ = Task.Run(async () =>
		{
			try
			{
				var end = DateTime.UtcNow + TimeSpan.FromMinutes(3);
				// Done when everything is delivered, or when the user comes back, or after three minutes.
				while (!_inForeground && DateTime.UtcNow < end && (HasUnsent() || Client.Attachments.IsServing)) await Task.Delay(2000);
			}
			catch (Exception ex) { Diag.Log($"Hold failed: {ex.GetType().Name}"); }
			finally { keep.Stop(); _holding = false; }
		});
	}

	/// <summary>Tell contacts when I read their messages (on by default).</summary>
	public static bool SendReadReceipts
	{
		get => Preferences.Default.Get("send_read_receipts", true);
		set => Preferences.Default.Set("send_read_receipts", value);
	}

	/// <summary>System notifications while the app is in the background (on by default; the phone's own permission also applies).</summary>
	public static bool NotificationsEnabled
	{
		get => Preferences.Default.Get("notifications", true);
		set => Preferences.Default.Set("notifications", value);
	}

	/// <summary>
	/// Wake-up through the network while the app is closed: the push signal (relayed by the phone vendor's service, carrying no content)
	/// and the periodic check. Independent of <see cref="NotificationsEnabled"/>, which only decides whether the phone's shade is used.
	/// </summary>
	public static bool BackgroundWakeEnabled
	{
		get => Preferences.Default.Get("background_wake", true);
		set => Preferences.Default.Set("background_wake", value);
	}

	/// <summary>Applies <see cref="BackgroundWakeEnabled"/> now: the push token is given to (or withdrawn from) the servers, the periodic check scheduled or cancelled.</summary>
	public async Task ApplyBackgroundWakeAsync()
	{
		Notifier?.ApplyBackgroundPolling(BackgroundWakeEnabled);
		if (State == AppState.Ready) await RegisterPushAsync(Client);
	}

	private static bool _setupOfferedThisRun;

	/// <summary>
	/// Once, the first time the home screen shows on a phone that may block background starts: explains in one box and leads to the
	/// system settings. <paramref name="force"/> (the Settings button) asks again whatever happened before.
	/// </summary>
	public static async Task OfferBackgroundSetupAsync(bool force = false)
	{
		if (Notifier is not { } notifier || Shell.Current == null) return;
		if (!force && (_setupOfferedThisRun || Preferences.Default.Get("bg_setup_asked", false) || !BackgroundWakeEnabled || !notifier.NeedsBackgroundSetup)) return;
		_setupOfferedThisRun = true;
		Preferences.Default.Set("bg_setup_asked", true);
		if (await Shell.Current.DisplayAlertAsync(Loc.T("bgsetup_title"), Loc.T("bgsetup_text"), Loc.T("bgsetup_go"), Loc.T("bgsetup_later"))) notifier.OpenBackgroundSetup();
	}

	/// <summary>Set by the platform: shows notifications in the system shade.</summary>
	public static INotifier? Notifier { get; set; }
	public static IKeepAlive? KeepAlive { get; set; }
	public static IPrinter? Printer { get; set; }

	/// <summary>Draws album mosaics (Android); null where there is no implementation, and the chat then shows the pictures one by one.</summary>
	public static ICollage? Collage { get; set; }

	/// <summary>Voice messages: set per platform (null where there is no implementation, e.g. the Windows UI build).</summary>
	public static IAudioRecorder? Recorder { get; set; }
	public static IAudioPlayer? Player { get; set; }
	public static IVideoThumbnailer? Thumbnailer { get; set; }

	/// <summary>Lets the person choose where an archive file goes or comes from (the system document picker on Android).</summary>
	public static IDocumentPicker? Documents { get; set; }

	/// <summary>Makes the small picture of an image, kept in the database (see <see cref="FreeSpeech.Client.Attachments.ThumbnailStore"/>).</summary>
	public static IPictureThumbnailer? PictureThumbnailer { get; set; }

	/// <summary>A notification was tapped while the app runs: the home screen goes where it points.</summary>
	public static string? PendingRoute { get; set; }
	public static event Action? RouteRequested;
	public static void RequestRoute(string route) { PendingRoute = route; RouteRequested?.Invoke(); }

	// ---- silent mode: a muted conversation raises no banner and no notification; its unread counter stays ----
	private static HashSet<string>? _muted;

	private static HashSet<string> Muted => _muted ??= [.. Preferences.Default.Get("muted_chats", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

	public static bool IsMuted(string historyKey) => Muted.Contains(historyKey);

	public void SetMuted(string historyKey, bool muted)
	{
		if (muted ? !Muted.Add(historyKey) : !Muted.Remove(historyKey)) return;
		Preferences.Default.Set("muted_chats", string.Join('\n', Muted));
		ConversationsChanged?.Invoke();
	}

	/// <summary>Tells the user about something when the app is in the background (nothing when it is on screen: the banner does it).</summary>
	private void Notify(string title, string text, string route)
	{
		if (InForeground || !NotificationsEnabled) return;
		Notifier?.Show(title, text, route);
	}

	/// <summary>Marks a conversation read (unread counters, receipt to the contact) and refreshes the lists.</summary>
	public async Task MarkReadAsync(Conversation conversation)
	{
		OnUi(() => ChatSeen?.Invoke(conversation.HistoryKey));   // a banner about this chat is no use any more
		Notifier?.Cancel($"chat?key={Uri.EscapeDataString(conversation.HistoryKey)}");   // nor is the phone notification
		Client.SendReadReceipts = SendReadReceipts;
		try { await Client.MarkReadAsync(conversation); }
		catch (Exception ex) when (ex is ClientException or HttpRequestException or InvalidOperationException) { /* the receipt is best effort */ }
		OnUi(() => ConversationsChanged?.Invoke());
	}

	/// <summary>Files up to this size (MB) are downloaded as soon as they are offered; 0 = never.</summary>
	public static int AutoDownloadMb
	{
		get => Preferences.Default.Get("auto_download_mb", 5);
		set => Preferences.Default.Set("auto_download_mb", Math.Max(0, value));
	}

	/// <summary>Shows "Outgoing" in the side menu: the letters waiting for their recipient, each of which can stop being waited for (a debugging aid, hidden by default).</summary>
	public static bool ShowOutbox
	{
		get => Preferences.Default.Get("show_outbox", false);
		set { Preferences.Default.Set("show_outbox", value); AppBadges.Instance.ShowOutbox = value; }
	}

	/// <summary>Asks the keyboard for no suggestions and no learning from what is typed (the keyboard has to respect it). Read when a text field is created.</summary>
	public static bool DiscreetKeyboard
	{
		get => Preferences.Default.Get("discreet_keyboard", false);
		set => Preferences.Default.Set("discreet_keyboard", value);
	}

	/// <summary>Applies the protected screen to the window now (set by the Android app); the choice itself is <see cref="ProtectedScreen"/>.</summary>
	public static Action<bool>? ScreenGuard { get; set; }

	/// <summary>No screenshot, no preview of the app in the list of recent apps.</summary>
	public static bool ProtectedScreen
	{
		get => Preferences.Default.Get("protected_screen", false);
		set { Preferences.Default.Set("protected_screen", value); ScreenGuard?.Invoke(value); }
	}

	/// <summary>On the lock screen a notification shows only "New message", not who wrote nor what.</summary>
	public static bool HideLockScreenContent
	{
		get => Preferences.Default.Get("hide_lock_screen_content", true);
		set => Preferences.Default.Set("hide_lock_screen_content", value);
	}

	/// <summary>A file larger than this (MB, as it will be sent: pictures and videos are already reduced) is only sent after the user confirms; 0 = never asked.</summary>
	public static int ConfirmSendMb
	{
		get => Preferences.Default.Get("confirm_send_mb", 25);
		set => Preferences.Default.Set("confirm_send_mb", Math.Max(0, value));
	}

	/// <summary>
	/// Asks before sending files over <see cref="ConfirmSendMb"/>: how many, how big, and what the servers still allow today.
	/// True = go on (also when nothing is that big or the setting is 0).
	/// </summary>
	public async Task<bool> ConfirmLargeSendAsync(IEnumerable<string> paths)
	{
		if (ConfirmSendMb <= 0) return true;
		var limit = ConfirmSendMb * 1024L * 1024L;
		var big = new List<long>();
		foreach (var path in paths)
			try { if (new FileInfo(path).Length > limit) big.Add(new FileInfo(path).Length); }
			catch (IOException) { /* gone: the send reports it */ }
		if (big.Count == 0) return true;

		var units = Loc.T("size_units");
		var text = big.Count == 1
			? Loc.T("send_confirm_one", ByteSize.Format(big[0], units))
			: Loc.T("send_confirm_many", big.Count, ByteSize.Format(big.Sum(), units));
		if (Client.SendQuotaLeft is { } left && left < big.Sum())
			text += "\n\n" + Loc.T("send_confirm_quota", ByteSize.Format(left, units));
		return await Dialogs.AlertAsync(Loc.T("send_confirm_title"), text, Loc.T("send_confirm_go"), Loc.T("cancel"));
	}

	/// <summary>A message or a contact request worth telling the user about (not raised for the chat that is open).</summary>
	public event Action<BannerInfo>? Banner;

	/// <summary>History key of the conversation on screen, if any: its messages raise no banner.</summary>
	public string? OpenChatKey { get; set; }

	private static string ProfileDir => Path.Combine(FileSystem.AppDataDirectory, "profile");

	/// <summary>A client on this phone's profile (also used by the background check, which opens it for a few seconds only).</summary>
	/// <summary>The directories built into the application (build setting <c>FsDirectories</c>), asked for relays by a new identity: only when the server is the default one, never over a server the user chose.</summary>
	private static IReadOnlyList<string> FallbacksFor(string serverUrl) => serverUrl == DefaultServer ? BuildDefaults.Get("FallbackServers").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

	private static IReadOnlyList<DirectoryEntry> DirectoriesFor(string serverUrl) => serverUrl == DefaultServer ? DirectoryPool.Parse(BuildDefaults.Get("Directories")) : [];

	private static string? DeviceCountry => System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName is { Length: 2 } c ? c : null;

	public static FreeSpeechClient NewClient() => ApplyTransferSetting(ObeyWipe(new(new ClientOptions { ProfileDir = ProfileDir, ServerUrl = Preferences.Default.Get("server_url", DefaultServer), Directories = DirectoriesFor(Preferences.Default.Get("server_url", DefaultServer)), FallbackServers = FallbacksFor(Preferences.Default.Get("server_url", DefaultServer)), Country = DeviceCountry, KeyCache = new SecureStorageKeyCache() })));

	/// <summary>What file chunks may do on mobile data (<see cref="FileTransferMode"/>, stored as a number): the default is always (the user restricts it if the data plan matters).</summary>
	public static FileTransferMode TransferMode
	{
		get => (FileTransferMode)Preferences.Default.Get("file_transfer_mode", (int)FileTransferMode.Always);
		set => Preferences.Default.Set("file_transfer_mode", (int)value);
	}

	/// <summary>The kind of network the phone is on now, as the transfer setting needs it (Wi-Fi and Ethernet are free, cellular is not).</summary>
	private static NetworkKind CurrentNetwork()
	{
		try
		{
			var profiles = Connectivity.Current.ConnectionProfiles;
			if (profiles.Contains(ConnectionProfile.WiFi) || profiles.Contains(ConnectionProfile.Ethernet)) return NetworkKind.Wifi;
			if (profiles.Contains(ConnectionProfile.Cellular)) return NetworkKind.Mobile;
		}
		catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException) { /* unknown: nothing is held back */ }
		return NetworkKind.Unknown;
	}

	/// <summary>Gives a client the "files only on Wi-Fi" setting and the current network (also for the short-lived client of a background check, which serves files too).</summary>
	public static FreeSpeechClient ApplyTransferSetting(FreeSpeechClient client)
	{
		client.FileTransfer.Mode = TransferMode;
		client.SetNetwork(CurrentNetwork());
		return client;
	}

	/// <summary>An order from the owner's other device (PROTOCOL §1, remote wipe): erase everything on this phone and start over. Hooked on every client, the background check's included.</summary>
	private static FreeSpeechClient ObeyWipe(FreeSpeechClient client)
	{
		client.WipeRequested += () => { Diag.Log("Wipe order received: erasing this phone"); AppLock.WipeAndRestart(); };
		client.AutoAcceptMigration = AutoAcceptMigration;
		client.AutoExcludeMigrated = AutoExcludeMigrated;
		return client;
	}

	/// <summary>Follow a contact's verified migration to a new identity without asking (never a contact I verified). Off by default.</summary>
	public static bool AutoAcceptMigration
	{
		get => Preferences.Default.Get("auto_accept_migration", false);
		set => Preferences.Default.Set("auto_accept_migration", value);
	}

	/// <summary>As a moderator, cut a migrated member's old identity off from my groups at once (a key rotation). On by default.</summary>
	public static bool AutoExcludeMigrated
	{
		get => Preferences.Default.Get("auto_exclude_migrated", true);
		set => Preferences.Default.Set("auto_exclude_migrated", value);
	}

	private void Create()
	{
		_client = NewClient();
		_hooked = false;
	}

	/// <summary>Opens the profile. Says what the app must show: the onboarding, the phrases prompt (key lost), or the conversations.</summary>
	public async Task<AppState> StartAsync()
	{
		if (State == AppState.Ready) return State;
		// A background check may still hold the profile: it is told to stop (it would otherwise run up to 45 s, or 10 min while serving a file).
		var waited = System.Diagnostics.Stopwatch.StartNew();
		StartupTrace.Mark("StartAsync: waiting for the profile");
		BackgroundPoll.RequestStop();
		// The wait has an end: a check that does not let go of the profile in 20 s is asked again, and the screen opens anyway after it
		// (a blank page for ever is worse than two short-lived clients on one database, which SQLite handles).
		if (await BackgroundPoll.Gate.WaitAsync(TimeSpan.FromSeconds(10))) BackgroundPoll.Gate.Release();
		else
		{
			StartupTrace.Mark("StartAsync: the background check still holds the profile after 10 s, asking it to stop again");
			BackgroundPoll.RequestStop();
			if (await BackgroundPoll.Gate.WaitAsync(TimeSpan.FromSeconds(10))) BackgroundPoll.Gate.Release();
			else StartupTrace.Mark("StartAsync: the background check never let go; opening the profile anyway");
		}
		StartupTrace.Mark($"StartAsync: profile free after {waited.ElapsedMilliseconds} ms");
		Diag.Log($"Start: profile free after {waited.ElapsedMilliseconds} ms");
		if (_client == null) Create();
		StartupTrace.Mark("StartAsync: client created");

		var loaded = await Task.Run(() => _client!.TryLoadIdentity());
		StartupTrace.Mark($"StartAsync: identity loaded={loaded}");
		if (!loaded) return State = _client!.NeedsPhrases ? AppState.NeedsPhrases : AppState.NeedsOnboarding;

		await ConnectAsync();
		StartupTrace.Mark("StartAsync: connected");
		return State;
	}

	/// <summary>No contact and no group yet: the next useful thing is to exchange contact details with someone.</summary>
	public bool HasNoContactsOrGroups => Client.Contacts.Count == 0 && Client.GetConversations().Count == 0;

	/// <summary>
	/// Where to go once the profile is open: the conversations, or — once, while there is nobody to talk to yet —
	/// the "Exchange contact details" page, so a new user is never left in front of an empty list.
	/// </summary>
	public string FirstScreen()
	{
		if (Preferences.Default.Get("contact_prompt_done", false) || !HasNoContactsOrGroups) return "//conversations";
		Preferences.Default.Set("contact_prompt_done", true);
		return "//invite";
	}

	/// <summary>Creates the identity from three recovery phrases — or unlocks an existing profile with them — and connects.</summary>
	public async Task CreateOrUnlockAsync(string phrase1, string phrase2, string phrase3, string nickname, string serverUrl)
	{
		if (State != AppState.Ready && serverUrl.Trim() != ServerUrl)
		{
			ServerUrl = serverUrl;
			Create();   // the server is chosen before the client exists
		}
		_client ??= ObeyWipe(new FreeSpeechClient(new ClientOptions { ProfileDir = ProfileDir, ServerUrl = ServerUrl, Directories = DirectoriesFor(ServerUrl), FallbackServers = FallbacksFor(ServerUrl), Country = DeviceCountry, KeyCache = new SecureStorageKeyCache() }));

		await Task.Run(() =>
		{
			_client.TryLoadIdentity();   // sets NeedsPhrases when a database already exists
			_client.CreateIdentity(phrase1, phrase2, phrase3, nickname);
		});
		await ConnectAsync();
	}

	private async Task ConnectAsync()
	{
		var client = Client;
		await Task.Run(client.Initialize);
		if (!_hooked)
		{
			_hooked = true;
			client.MessageReceived += m => OnUi(() => { MessageReceived?.Invoke(m); ConversationsChanged?.Invoke(); RaiseMessageBanner(m); AutoDownload(m); });
				client.Groups.InvitationReceived += i => OnUi(() => { UpdateBadges(); ConversationsChanged?.Invoke(); Banner?.Invoke(new BannerInfo(i.InviterName, Loc.T("banner_group_invite", i.Name), "//approve")); Notify(i.InviterName, Loc.T("banner_group_invite", i.Name), "//approve"); });
				client.Groups.SuggestionReceived += s => OnUi(() => { UpdateBadges(); ConversationsChanged?.Invoke(); Banner?.Invoke(new BannerInfo(s.SponsorName, Loc.T("banner_group_suggest", s.Candidate.Nickname, s.GroupName), "//approve")); Notify(s.SponsorName, Loc.T("banner_group_suggest", s.Candidate.Nickname, s.GroupName), "//approve"); });
				client.ReadReceived += c => OnUi(() => ReadReceived?.Invoke(c.HistoryKey));
				client.PinsChanged += c => OnUi(() => PinsChanged?.Invoke(c.HistoryKey));
				client.SendTypingIndicator = TypingIndicator;
				ApplyTransferSetting(client);
				Connectivity.Current.ConnectivityChanged += (_, _) => { if (_client != null) ApplyTransferSetting(_client); };
				client.TypingReceived += (c, who) => OnUi(() => TypingReceived?.Invoke(c.HistoryKey, who));
				client.DeviceSyncRequested += OnDeviceSyncRequested;
				client.DeviceSyncChanged += OnDeviceSyncChanged;
				client.Attachments.Pictures = PictureThumbnailer;
				client.Attachments.Completed += rec => OnUi(() => { FileCompleted?.Invoke(rec.Ref); ConversationsChanged?.Invoke(); });
			client.MessageChanged += c => OnUi(() => { MessageChanged?.Invoke(c); ConversationsChanged?.Invoke(); });
			client.ContactConfirmed += _ => OnUi(() => { UpdateBadges(); ConversationsChanged?.Invoke(); });
			client.ContactUpdated += _ => OnUi(() => ConversationsChanged?.Invoke());
			client.OpenFallbackProposed += c => OnUi(() => _ = AskOpenServerAsync(client, c));
			client.ContactRequested += c => OnUi(() => { UpdateBadges(); Banner?.Invoke(new BannerInfo(c.Nickname, Loc.T("banner_request"), "//approve")); Notify(c.Nickname, Loc.T("banner_request"), "//approve"); });
			client.MigrationRequested += n => OnUi(() => { UpdateBadges(); ConversationsChanged?.Invoke(); Banner?.Invoke(new BannerInfo(n.OldName, Loc.T("banner_migration"), "//approve")); Notify(n.OldName, Loc.T("banner_migration"), "//approve"); });
				client.Notice += n => OnUi(() => Notice?.Invoke(n));
				client.SendLimited += (code, seconds) => OnUi(() => Announce(Loc.T("limit_title"), Loc.T(code == "quota_exceeded" ? "limit_quota" : "limit_rate", WaitText(seconds))));
		}
		await client.ConnectAsync();
		_ = client.Attachments.BackfillThumbnailsAsync();   // pictures received earlier get their small picture in the background
		State = AppState.Ready;
		_ = RegisterPushAsync(client);
		_ = AnnounceRevocationAsync(client);

		ScanOffers(delayMs: 3000);
		UpdateBadges();
		ConversationsChanged?.Invoke();
	}

	/// <summary>Lets the groups I am in pin my revocation key (a signed member profile, once per group): a moderator who is not my contact can then check a migration notice. Never fails the start.</summary>
	private static async Task AnnounceRevocationAsync(FreeSpeechClient client)
	{
		try { await client.AnnounceRevocationKeyAsync(); }
		catch (Exception ex) when (ex is ClientException or InvalidOperationException or HttpRequestException) { Diag.Log($"Revocation key announcement failed: {ex.Message}"); }
	}

	/// <summary>
	/// Tells the servers this phone's push token (PROTOCOL §9), so a contact's message wakes the app instead of waiting for the
	/// 15-minute check. Release builds with Firebase only; each server wakes the phone through the push gateway it is linked with. Never fails the start.
	/// </summary>
	private static async Task RegisterPushAsync(FreeSpeechClient client)
	{
#if ANDROID && FCM
		try
		{
			if (!BackgroundWakeEnabled) { Diag.Log("Push: background wake-up is off, token withdrawn"); await client.SetPushTokenAsync(null); return; }
			var token = await FcmToken.GetAsync();
			if (string.IsNullOrEmpty(token)) { Diag.Log("Push: Firebase gave no token, nothing registered"); return; }
			Preferences.Default.Set("push_token", token);
			await client.SetPushTokenAsync(new FreeSpeech.Client.Relay.PushTokenInfo("fcm", token));
			Diag.Log($"Push: token registered with the servers (…{token[^6..]})");
		}
		catch (Exception ex) { Diag.Log($"Push registration failed: {ex.GetType().Name} {ex.Message}"); }
#else
		await Task.CompletedTask;
#endif
	}

	private void RaiseMessageBanner(ChatMessage m)
	{
		if (m.Mine || m.FromOtherDevice || IsMuted(m.Conversation.HistoryKey)) return;
		if (m.Conversation.HistoryKey == OpenChatKey && InForeground) return;
		var last = Client.GetLatestMessages(m.Conversation, 1).Entries.FirstOrDefault();   // the message that just arrived
		var title = m.Conversation.Group is { } g ? $"{m.FromName} · {g.Name}" : m.FromName;
		var text = last == null ? FreeSpeech.Client.Markup.MessageMarkup.PlainText(m.Text) : PreviewText.Of(Client, last);
		var route = $"chat?key={Uri.EscapeDataString(m.Conversation.HistoryKey)}";
		Notify(title, text, route);   // the system notification only: the unread counters already tell in the app, the banners are for system notices
	}

	/// <summary>Downloads an offered file right away when it is small enough (the setting), so a picture is already there when the chat opens.</summary>
	private void AutoDownload(ChatMessage m)
	{
		if (m.IsFile && !m.Mine && !m.FromOtherDevice) ScanOffers(delayMs: 1000);   // the record may be stored just after the event
	}

	/// <summary>Requests every received file still only offered and under the size setting. Also run after connecting: offers that arrived while the app was closed are picked up.</summary>
	private void ScanOffers(int delayMs = 0)
	{
		if (AutoDownloadMb <= 0) return;
		_ = Task.Run(async () =>
		{
			if (delayMs > 0) await Task.Delay(delayMs);
			var limit = AutoDownloadMb * 1024L * 1024L;
			foreach (var rec in Client.Attachments.Store.All.Where(r => r.Direction == AttachmentDirection.Received && r.State == AttachmentState.Offered && r.Size <= limit).ToList())
			{
				try { Client.Attachments.EnsureRoom(rec); await Client.Attachments.RequestAsync(rec); }
				catch (Exception ex) when (ex is ClientException or InvalidOperationException) { /* stays offered: the user can tap Download */ }
			}
		});
	}

	// ------------------------------------------------------------------
	// Mirroring another device of my identity (protocol §1)
	// ------------------------------------------------------------------

	/// <summary>
	/// A new device whose identity has just been restored: offers to be mirrored by another device of the same identity. On "yes" the request goes out and its
	/// code is shown; the person types it on their other device, which sends nothing without it.
	/// </summary>
	public async Task OfferDeviceSyncAsync()
	{
		if (State != AppState.Ready || Shell.Current == null) return;
		if (!await Dialogs.AlertAsync(Loc.T("sync_prompt_title"), Loc.T("sync_prompt_text"), Loc.T("yes"), Loc.T("no"))) return;
		var sent = await Client.RequestDeviceSyncAsync();
		if (!sent) { await Dialogs.AlertAsync(Loc.T("sync_prompt_title"), Loc.T("sync_none"), Loc.T("ok")); return; }
		await Dialogs.AlertAsync(Loc.T("sync_prompt_title"), Loc.T("sync_code_shown", Client.DeviceSyncCode ?? ""), Loc.T("ok"));
	}

	/// <summary>Another device asks for my data: an internal notice (and the phone''s, in the background); the person touches it to type the code the asking device shows.</summary>
	private void OnDeviceSyncRequested(DeviceSyncRequestInfo request) => OnUi(() =>
	{
		Banner?.Invoke(new BannerInfo(Loc.T("sync_title"), Loc.T("sync_notify"), "sync"));
		Notify(Loc.T("sync_title"), Loc.T("sync_notify"), "sync");
		ConversationsChanged?.Invoke();   // the home screen shows the waiting request until it is dealt with
	});

	/// <summary>True while another device waits for its code to be typed here.</summary>
	public bool HasDeviceSyncPending => State == AppState.Ready && Client.PendingDeviceSyncs.Count > 0;

	/// <summary>The box where the code shown on the other device is typed: the right one starts the transfer, a wrong one leaves a few tries.</summary>
	public async Task EnterSyncCodeAsync()
	{
		while (State == AppState.Ready && Shell.Current != null && Client.PendingDeviceSyncs.FirstOrDefault() is { } request)
		{
			var code = await Dialogs.PromptAsync(Loc.T("sync_title"), Loc.T("sync_enter_text"), Loc.T("sync_enter_ok"), Loc.T("cancel"), keyboard: Keyboard.Numeric, maxLength: 7);
			if (code == null) return;   // the request stays for a few minutes
			try
			{
				await Client.ApproveDeviceSyncAsync(request.Id, code);
				ClearSyncNotice();
				Announce(Loc.T("sync_title"), Loc.T("sync_started"));
				return;
			}
			catch (ClientException ex) when (ex.Code == "sync_wrong_code")
			{
				await Dialogs.AlertAsync(Loc.T("sync_title"), Loc.T("sync_wrong", ex.Args.FirstOrDefault() ?? 0), Loc.T("ok"));
			}
			catch (ClientException ex) when (ex.Code is "sync_cancelled" or "sync_expired")
			{
				ClearSyncNotice();
				await Dialogs.AlertAsync(Loc.T("sync_title"), Loc.T(ex.Code == "sync_cancelled" ? "sync_cancelled" : "sync_expired"), Loc.T("ok"));
				return;
			}
		}
	}

	/// <summary>The request is dealt with (answered here or on another device, cancelled, expired): the notice, the phone's notification and the bar on the home screen go.</summary>
	private void ClearSyncNotice()
	{
		Notifier?.Cancel("sync");
		Banner?.Invoke(new BannerInfo("", "", ""));
		NotifyConversationsChanged();
	}

	private void OnDeviceSyncChanged(string state) => OnUi(() =>
	{
		if (state == "closed") { ClearSyncNotice(); return; }
		if (state == "done") { Announce(Loc.T("sync_title_mine"), Loc.T("sync_done", Client.DeviceSyncMessages)); NotifyConversationsChanged(); ScanOffers(delayMs: 1000); }   // the pictures and documents come from the other device
		else if (state == "refused") Announce(Loc.T("sync_title_mine"), Loc.T("sync_refused"));
		else if (state == "received") NotifyConversationsChanged();
	});
	/// <summary>A short message on the home screen's banner (nothing to open: a tap closes it).</summary>
	public void Announce(string title, string text) => Banner?.Invoke(new BannerInfo(title, text, ""));

	/// <summary>A wait in seconds as a person reads it: "40 s", "12 min", "5 h".</summary>
	private static string WaitText(int seconds) => seconds < 90 ? $"{seconds} s" : seconds < 5400 ? $"{(seconds + 30) / 60} min" : $"{(seconds + 1800) / 3600} h";

	/// <summary>For screens that changed the data themselves (a restore): lists reload.</summary>
	public void NotifyConversationsChanged() => ConversationsChanged?.Invoke();

	/// <summary>The side menu's "Approve" entry shows how many contact requests wait for an answer.</summary>
	public void UpdateBadges()
	{
		if (_client == null) return;
		AppBadges.Instance.Archived = _client.ArchivedContacts.Count(a => _client.FindContact(a.DiscoveryId) == null) + _client.Groups.Store.All.Count(g => !g.Active);
		AppBadges.Instance.PendingRequests = _client.PendingRequests.Count + _client.Groups.PendingGroupInvites.Count + _client.Groups.Suggestions.Count + _client.PendingMigrations.Count;
	}

	private static void OnUi(Action a) => MainThread.BeginInvokeOnMainThread(a);

	/// <summary>The private servers shared with a contact do not answer and both sides allow an open one: the question, once for the outage. In the background it is a notice that leads to "Sends in progress".</summary>
	private async Task AskOpenServerAsync(FreeSpeechClient client, FreeSpeech.Client.Contacts.Contact contact)
	{
		if (!InForeground)
		{
			Banner?.Invoke(new BannerInfo(contact.DisplayName, Loc.T("banner_route"), "//outbox"));
			Notify(contact.DisplayName, Loc.T("banner_route"), "//outbox");
			return;
		}
		if (await Dialogs.AlertAsync(Loc.T("route_ask_title"), Loc.T("route_ask_text", contact.DisplayName), Loc.T("route_ask_yes"), Loc.T("route_ask_no")))
			client.ApproveOpenFallback(contact.DiscoveryId);
	}
}