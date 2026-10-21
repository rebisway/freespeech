/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.Controls;

/// <summary>
/// The top bar of every main page: the page's name on the left; on the right "FreeSpeech" and its flag, with a small badge on the flag
/// that says where I stand on my servers (private or public, connected, waiting for approval, offline). A tap lists every server in words.
/// </summary>
public partial class BrandTitleView : ContentView
{
	private IDispatcherTimer? _timer;
	private List<ServerBadge> _badges = [];
	public static readonly BindableProperty HeadingProperty = BindableProperty.Create(
		nameof(Heading), typeof(string), typeof(BrandTitleView), "",
		propertyChanged: (b, _, v) => ((BrandTitleView)b).HeadingLabel.Text = (string)v);

	public BrandTitleView()
	{
		InitializeComponent();
		FlagWithBadge.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await ExplainAsync()) });
		Loaded += (_, _) => Start();
		Unloaded += (_, _) => Stop();
	}

	// The state is read from memory (no network call), a few seconds apart, only while this bar is on screen.
	private void Start()
	{
		Refresh();
		if (_timer != null) return;
		_timer = Dispatcher.CreateTimer();
		_timer.Interval = TimeSpan.FromSeconds(4);
		_timer.Tick += (_, _) => Refresh();
		_timer.Start();
	}

	private void Stop()
	{
		_timer?.Stop();
		_timer = null;
	}

	private void Refresh()
	{
		List<ServerBadge> badges;
		try
		{
			var service = Handler?.MauiContext?.Services.GetService<ClientService>();
			if (service == null) return;
			badges = [.. service.Client.Servers.Select(ServerBadges.Of)];
		}
		catch (InvalidOperationException) { return; }   // the client is not started yet

		if (badges.SequenceEqual(_badges)) return;
		_badges = badges;
		var shown = ServerBadges.Summary(badges);
		StateBadge.IsVisible = shown != null;
		if (shown == null) return;
		StateBadge.Source = shown.Icon;
		SemanticProperties.SetDescription(FlagWithBadge, string.Join(" ", badges.Select(b => $"{HostOf(b.Url)}: {Loc.T(b.TextKey)}")));
	}

	/// <summary>The same thing in words, for every server: nothing is left to guess from a colour.</summary>
	private async Task ExplainAsync()
	{
		var lines = _badges.Select(b => $"{HostOf(b.Url)}\n{Loc.T(b.TextKey)}");
		await Dialogs.AlertAsync(Loc.T("srv_title"), string.Join("\n\n", lines), Loc.T("ok"));
	}

	private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

	public string Heading
	{
		get => (string)GetValue(HeadingProperty);
		set => SetValue(HeadingProperty, value);
	}
}
