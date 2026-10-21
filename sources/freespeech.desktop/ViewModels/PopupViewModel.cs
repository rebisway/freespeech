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
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>One conversation in the tray popup: who wrote, what, and a box to answer on the spot.</summary>
public sealed partial class PopupCard(string key, string title, string text) : ObservableObject
{
	[ObservableProperty] private string _title = title;
	[ObservableProperty] private string _text = text;
	[ObservableProperty] private string _draft = "";
	[ObservableProperty, NotifyPropertyChangedFor(nameof(HasThumb))] private Avalonia.Media.Imaging.Bitmap? _thumb;

	/// <summary>The message the card shows, when it is a file: its picture replaces the text line once downloaded.</summary>
	public string? MessageId { get; set; }
	public bool HasThumb => Thumb != null;

	public string Key { get; } = key;
	public string Initial => Title.Length == 0 ? "?" : Title[..1].ToUpperInvariant();

	/// <summary>The popup closes this card after a while, unless the user is answering.</summary>
	public DispatcherTimer? Timer { get; set; }

	partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Initial));
}

/// <summary>
/// The popup the tray raises when a message arrives while the window is not on screen: one card per conversation, with a reply box
/// (the message goes out without opening the window), a click on the text opening the conversation. Cards go away by themselves.
/// </summary>
public sealed partial class PopupViewModel
{
	private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(14);

	public ObservableCollection<PopupCard> Cards { get; } = [];

	public string ReplyHint => Loc.T("d_reply_hint");
	public string SendTip => Loc.T("d_send");
	public string OpenTip => Loc.T("d_open_chat");
	public string CloseTip => Loc.T("d_close");

	public event Action<string>? Opened;
	public event Action<string, string>? Replied;
	public event Action? Emptied;

	/// <summary>A new message: the card of that conversation is updated (or made) and its time starts again.</summary>
	public void Show(string title, string text, string key, string? messageId = null)
	{
		var card = Cards.FirstOrDefault(c => c.Key == key);
		if (card == null)
		{
			card = new PopupCard(key, title, text) { MessageId = messageId };
			Cards.Insert(0, card);
			if (Cards.Count > 4) Remove(Cards[^1]);
		}
		else { card.Title = title; card.Text = text; card.MessageId = messageId; card.Thumb = null; }
		Restart(card);
	}

	/// <summary>The picture of a message arrived: the card of that message shows it.</summary>
	public void FileReady(string messageId, string path)
	{
		if (Cards.FirstOrDefault(c => c.MessageId == messageId) is { } card) card.Thumb = ChatItem.Decode(path, 240);
	}

	public void Cancel(string key)
	{
		if (Cards.FirstOrDefault(c => c.Key == key) is { } card) Remove(card);
	}

	/// <summary>The user is looking at or answering a card: it stays.</summary>
	public void Hold(PopupCard card) => card.Timer?.Stop();

	public void Restart(PopupCard card)
	{
		card.Timer?.Stop();
		if (!string.IsNullOrEmpty(card.Draft)) return;   // an answer is being written
		card.Timer ??= MakeTimer(card);
		card.Timer.Start();
	}

	private DispatcherTimer MakeTimer(PopupCard card)
	{
		var timer = new DispatcherTimer { Interval = Lifetime };
		timer.Tick += (_, _) => { timer.Stop(); if (string.IsNullOrEmpty(card.Draft)) Remove(card); };
		return timer;
	}

	private void Remove(PopupCard card)
	{
		card.Timer?.Stop();
		Cards.Remove(card);
		if (Cards.Count == 0) Emptied?.Invoke();
	}

	[RelayCommand]
	private void Open(PopupCard card)
	{
		Opened?.Invoke(card.Key);
		Remove(card);
	}

	[RelayCommand]
	private void Reply(PopupCard card)
	{
		var text = card.Draft.Trim();
		if (text.Length == 0) return;
		Replied?.Invoke(card.Key, text);
		Remove(card);
	}

	[RelayCommand]
	private void Dismiss(PopupCard card) => Remove(card);
}
