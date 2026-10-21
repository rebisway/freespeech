/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using Avalonia.Threading;
using FreeSpeech.Client;
using FreeSpeech.Client.Discovery;

namespace FreeSpeech.Desktop.Services;

public enum SessionState { Starting, NeedsOnboarding, NeedsPhrases, Ready }

/// <summary>
/// Owns the client library for the window: opens the profile, connects, and re-raises the client's events on the UI thread.
/// All the messaging logic lives in <see cref="FreeSpeechClient"/>; nothing here is protocol.
/// </summary>
public sealed class DesktopSession
{
	private FreeSpeechClient? _client;
	private bool _hooked;

	public SessionState State { get; private set; } = SessionState.Starting;

	public FreeSpeechClient Client => _client ?? throw new InvalidOperationException("The profile is not open.");

	public string Server { get; private set; } = Options.Server;

	/// <summary>Something changed in the conversation list (a message, a read receipt, a contact).</summary>
	public event Action? ConversationsChanged;

	/// <summary>A system notice (a request to connect, an invitation, a proposal, an identity change): who, and what. Shown at the top of the conversation list.</summary>
	public event Action<string, string>? SystemNotice;

	/// <summary>Another device of my identity asks to be mirrored from this one (its owner, here, must agree).</summary>
	public event Action<DeviceSyncRequestInfo>? DeviceSyncAsked;

	/// <summary>The request this device made moved on: "sent", "refused", "received", "done".</summary>
	public event Action<string>? DeviceSyncStateChanged;

	public event Action<ChatMessage>? MessageReceived;

	public event Action<string>? ReadReceived;

	/// <summary>A message of the conversation changed (edited, deleted, reacted to, a file state).</summary>
	public event Action<MessageChange>? MessageChanged;

	/// <summary>A received file finished downloading (its message id).</summary>
	public event Action<string>? FileCompleted;

	/// <summary>Someone is typing: the conversation's history key and who (a contact or a group member).</summary>
	public event Action<string, string>? TypingReceived;

	/// <summary>The pinned messages of a group changed (history key of the group).</summary>
	public event Action<string>? PinsChanged;

	private static string ProfileDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeSpeech", Options.Profile);

	private static FreeSpeechClient Create(string server)
	{
		var client = new FreeSpeechClient(new ClientOptions { ProfileDir = ProfileDir, ServerUrl = server, Directories = server == Options.DefaultServer ? DirectoryPool.Parse(BuildDefaults.Get("Directories")) : [], FallbackServers = server == Options.DefaultServer ? BuildDefaults.Get("FallbackServers").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [], Country = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName, KeyCache = new DesktopKeyCache(ProfileDir) })
		{
			AutoAcceptMigration = Prefs.AutoAcceptMigration,
			AutoExcludeMigrated = Prefs.AutoExcludeMigrated,
		};
		// An order from the owner's other device (PROTOCOL section 1, remote wipe): erase everything on this computer and start over.
		client.WipeRequested += () =>
		{
			Diag.Log("Wipe order received: erasing this computer's data");
			Dispatcher.UIThread.Post(async () => await AppLock.WipeAndRestartAsync());
		};
		return client;
	}

	/// <summary>Opens the profile if there is one, and connects.</summary>
	public async Task<SessionState> StartAsync()
	{
		_client = Create(Server);
		if (!await Task.Run(_client.TryLoadIdentity)) return State = _client.NeedsPhrases ? SessionState.NeedsPhrases : SessionState.NeedsOnboarding;
		await ConnectAsync();
		return State;
	}

	/// <summary>Creates the identity from three phrases, or unlocks the existing profile with them, then connects.</summary>
	public async Task CreateOrUnlockAsync(string phrase1, string phrase2, string phrase3, string nickname, string server)
	{
		if (server.Trim() != Server) { Server = server.Trim(); _client = Create(Server); _hooked = false; }
		_client ??= Create(Server);
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
			client.MessageReceived += m => Dispatcher.UIThread.Post(() => { MessageReceived?.Invoke(m); ConversationsChanged?.Invoke(); AutoDownload(m); });
			client.MessageChanged += c => Dispatcher.UIThread.Post(() => { MessageChanged?.Invoke(c); ConversationsChanged?.Invoke(); });
			client.Attachments.Completed += rec => Dispatcher.UIThread.Post(() => { FileCompleted?.Invoke(rec.Ref); ConversationsChanged?.Invoke(); });
			client.TypingReceived += (c, who) => Dispatcher.UIThread.Post(() => TypingReceived?.Invoke(c.HistoryKey, who));
			client.ReadReceived += c => Dispatcher.UIThread.Post(() => { ReadReceived?.Invoke(c.HistoryKey); ConversationsChanged?.Invoke(); });
			client.PinsChanged += c => Dispatcher.UIThread.Post(() => PinsChanged?.Invoke(c.HistoryKey));
			client.ContactConfirmed += _ => Dispatcher.UIThread.Post(() => ConversationsChanged?.Invoke());
			client.SendLimited += (code, seconds) => Dispatcher.UIThread.Post(() => SystemNotice?.Invoke(Loc.T("limit_title"), Loc.T(code == "quota_exceeded" ? "limit_quota" : "limit_rate", WaitText(seconds))));
			client.ContactRequested += c => Dispatcher.UIThread.Post(() => { ConversationsChanged?.Invoke(); SystemNotice?.Invoke(c.Nickname, Loc.T("banner_request")); });
			client.MigrationRequested += n => Dispatcher.UIThread.Post(() => { ConversationsChanged?.Invoke(); SystemNotice?.Invoke(n.OldName, Loc.T("banner_migration")); });
			client.Groups.InvitationReceived += i => Dispatcher.UIThread.Post(() => { ConversationsChanged?.Invoke(); SystemNotice?.Invoke(i.InviterName, Loc.T("banner_group_invite", i.Name)); });
			client.Groups.SuggestionReceived += s => Dispatcher.UIThread.Post(() => { ConversationsChanged?.Invoke(); SystemNotice?.Invoke(s.SponsorName, Loc.T("banner_group_suggest", s.Candidate.Nickname, s.GroupName)); });
			client.ContactUpdated += _ => Dispatcher.UIThread.Post(() => ConversationsChanged?.Invoke());
				client.DeviceSyncRequested += r => Dispatcher.UIThread.Post(() => DeviceSyncAsked?.Invoke(r));
				client.DeviceSyncChanged += s => Dispatcher.UIThread.Post(() => DeviceSyncStateChanged?.Invoke(s));
		}
		client.SendTypingIndicator = Prefs.TypingIndicator;
		await client.ConnectAsync();
		State = SessionState.Ready;
		ConversationsChanged?.Invoke();
		ScanOffers(delayMs: 3000);   // offers that arrived while the program was closed
		// A computer has room and a fast line: whatever is still only offered (a failed request, a record stored late) is asked for again every minute.
		new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => ScanOffers()).Start();
	}

	/// <summary>The user is typing in a conversation: a throw-away signal, at most one every few seconds (the library decides).</summary>
	public void NotifyTyping(Conversation conversation)
	{
		if (State != SessionState.Ready) return;
		Client.SendTypingIndicator = Prefs.TypingIndicator;
		_ = Client.SendTypingAsync(conversation);
	}

	/// <summary>
	/// Asks before sending files over <see cref="Prefs.ConfirmSendMb"/>: how many, how big, and what the servers still allow today.
	/// True = go on (also when nothing is that big or the setting is 0).
	/// </summary>
	public async Task<bool> ConfirmLargeSendAsync(IEnumerable<string> paths)
	{
		if (Prefs.ConfirmSendMb <= 0) return true;
		var limit = Prefs.ConfirmSendMb * 1024L * 1024L;
		var big = new List<long>();
		foreach (var path in paths)
			try { if (new FileInfo(path).Length > limit) big.Add(new FileInfo(path).Length); }
			catch (IOException) { /* gone: the send reports it */ }
		if (big.Count == 0) return true;

		var units = Loc.T("size_units");
		var text = big.Count == 1
			? Loc.T("send_confirm_one", FreeSpeech.Client.Attachments.ByteSize.Format(big[0], units))
			: Loc.T("send_confirm_many", big.Count, FreeSpeech.Client.Attachments.ByteSize.Format(big.Sum(), units));
		if (Client.SendQuotaLeft is { } left && left < big.Sum())
			text += "\n\n" + Loc.T("send_confirm_quota", FreeSpeech.Client.Attachments.ByteSize.Format(left, units));
		return await Ui.ConfirmAsync(Loc.T("send_confirm_title"), text, Loc.T("send_confirm_go"), Loc.T("cancel"));
	}

	/// <summary>A wait in seconds as a person reads it: "40 s", "12 min", "5 h".</summary>
	private static string WaitText(int seconds) => seconds < 90 ? $"{seconds} s" : seconds < 5400 ? $"{(seconds + 30) / 60} min" : $"{(seconds + 1800) / 3600} h";

	/// <summary>Downloads an offered file right away when it is small enough (the setting), so a picture is already there when the chat opens.</summary>
	private void AutoDownload(ChatMessage m)
	{
		if (m.IsFile) ScanOffers(delayMs: 1000);   // the record may be stored just after the event
	}

	/// <summary>Requests every file still only offered (received, or sent from another device of the identity) and under the size setting.</summary>
	public void ScanOffers(int delayMs = 0)
	{
		if (Prefs.AutoDownloadMb <= 0) return;
		_ = Task.Run(async () =>
		{
			if (delayMs > 0) await Task.Delay(delayMs);
			var limit = Prefs.AutoDownloadMb * 1024L * 1024L;
			foreach (var rec in Client.Attachments.Store.All.Where(r => r.State == FreeSpeech.Client.Attachments.AttachmentState.Offered && r.Size <= limit).ToList())
			{
				try { Client.Attachments.EnsureRoom(rec); await Client.Attachments.RequestAsync(rec); }
				catch (Exception ex) when (ex is ClientException or InvalidOperationException) { /* stays offered: the user can ask for it */ }
			}
		});
	}

	/// <summary>Sends a reply typed in a system notification and marks the conversation read. False when the profile is not open.</summary>
	public async Task<bool> QuickReplyAsync(string historyKey, string text)
	{
		if (State != SessionState.Ready || string.IsNullOrWhiteSpace(text) || Client.FindConversation(historyKey) is not { } conversation) return false;
		await Client.SendAsync(conversation, text.Trim());
		Client.SendReadReceipts = Prefs.SendReadReceipts;
		await Client.MarkReadAsync(conversation);
		Dispatcher.UIThread.Post(() => ConversationsChanged?.Invoke());
		return true;
	}

	/// <summary>For screens that changed the data themselves (a contact removed, a request answered): the lists reload.</summary>
	public void RaiseConversationsChanged() => ConversationsChanged?.Invoke();

	/// <summary>This device asks another device of my identity (connected now) to mirror it; the answer comes through <see cref="DeviceSyncStateChanged"/>. Returns false when none is connected.</summary>
	public async Task<bool> AskDeviceSyncAsync()
	{
		if (State != SessionState.Ready) return false;
		try { return await Client.RequestDeviceSyncAsync(); }
		catch (Exception ex) when (ex is ClientException or HttpRequestException) { return false; }
	}

	public async Task ShutdownAsync()
	{
		if (_client != null) await _client.DisposeAsync();
	}
}