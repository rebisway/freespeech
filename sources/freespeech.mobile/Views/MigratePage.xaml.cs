/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.Views;

/// <summary>
/// Moving to a new identity after a theft (PROTOCOL §12). Step 1: choose who takes over the groups I lead or moderate, type the OLD phrases
/// (they sign the notice), then the app draws the NEW phrases and sends the user through the save screen, which sends the notice. Step 2: once the
/// notice has reached everyone, the new phrases switch this phone to the new identity. Step 2 is also what opens when the app was restarted
/// between the two: the state lives in the profile, the phrases never do.
/// </summary>
public partial class MigratePage : ContentPage
{
	private readonly ClientService _service;
	private readonly List<(FreeSpeechClient.RoleHandover Role, Picker? Picker)> _rows = [];
	private bool _busy;

	public MigratePage(ClientService service)
	{
		InitializeComponent();
		_service = service;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Refresh();
	}

	private void Refresh()
	{
		var client = _service.Client;
		var announced = client.OutgoingMigrationId;
		Step1.IsVisible = announced == null;
		Step2.IsVisible = announced != null;
		if (announced != null)
		{
			NewIdLabel.Text = Loc.T("mig_new_id", announced[..8]);
			PendingLabel.Text = Loc.T("mig_pending", client.PendingMigrationLetters);
			return;
		}

		GroupsBox.Children.Clear();
		_rows.Clear();
		var roles = client.RolesToHandOver();
		NoGroups.IsVisible = roles.Count == 0;
		foreach (var role in roles)
		{
			var box = new VerticalStackLayout { Spacing = 4 };
			box.Add(new Label { Text = role.Group.Name, FontFamily = "OpenSansSemibold" });
			Picker? picker = null;
			if (role.IsCreator)
			{
				box.Add(new Label { Text = Loc.T("mig_group_creator"), FontSize = 13, TextColor = (Color)Application.Current!.Resources["Gray200"] });
				picker = new Picker { Title = Loc.T("mig_pick"), ItemsSource = role.Candidates.Select(c => c.Nickname).ToList() };
				box.Add(picker);
			}
			else box.Add(new Label { Text = Loc.T("mig_group_moderator"), FontSize = 13, TextColor = (Color)Application.Current!.Resources["Gray200"] });
			GroupsBox.Children.Add(box);
			_rows.Add((role, picker));
		}
	}

	private void Busy(bool on)
	{
		_busy = on;
		Spinner.IsVisible = Spinner.IsRunning = on;
	}

	private async void OnContinue(object? sender, EventArgs e)
	{
		if (_busy) return;
		Result.Text = "";
		var old = new[] { Old1.Text ?? "", Old2.Text ?? "", Old3.Text ?? "" }.Select(p => p.Trim()).ToArray();
		if (old.Any(p => !FreeSpeechClient.IsPhraseValid(p))) { Result.Text = Loc.T("onb_err_phrase"); return; }

		var successors = new Dictionary<string, string>();
		foreach (var (role, picker) in _rows.Where(r => r.Role.IsCreator))
		{
			if (role.Candidates.Count == 0) { Result.Text = Loc.T("mig_err_no_candidates", role.Group.Name); return; }
			if (picker is not { SelectedIndex: >= 0 } p) { Result.Text = Loc.T("mig_err_successor", role.Group.Name); return; }
			successors[role.Group.GroupId] = role.Candidates[p.SelectedIndex].DiscoveryId;
		}

		Busy(true);
		try
		{
			var client = _service.Client;
			await Task.Run(() => client.CheckMyPhrases(old[0], old[1], old[2]));   // wrong phrases are caught before anything is changed
			await client.HandOverRolesAsync(successors);

			var generator = await BookLoader.LoadAsync(Loc.Instance.Language);
			NewIdentityDraft.Set(generator.Generate().ToArray(), client.Identity.Nickname ?? "", _service.ServerUrl);
			MigrationDraft.Set(old);
			Old1.Text = Old2.Text = Old3.Text = "";
			await Shell.Current.GoToAsync("phrases");   // the save screen sends the notice when it is left
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException or IOException) { Result.Text = ErrorText.Of(ex); }
		finally { Busy(false); }
	}

	private void OnRefresh(object? sender, EventArgs e) => Refresh();

	private async void OnFinish(object? sender, EventArgs e)
	{
		if (_busy) return;
		Result.Text = "";
		var phrases = new[] { New1.Text ?? "", New2.Text ?? "", New3.Text ?? "" }.Select(p => p.Trim()).ToArray();
		if (phrases.Any(p => !FreeSpeechClient.IsPhraseValid(p))) { Result.Text = Loc.T("onb_err_phrase"); return; }
		var client = _service.Client;
		if (client.PendingMigrationLetters > 0
			&& !await Dialogs.AlertAsync(Loc.T("mig_finish"), Loc.T("mig_finish_pending", client.PendingMigrationLetters), Loc.T("mig_finish_yes"), Loc.T("cancel"))) return;

		Busy(true);
		try
		{
			await Task.Run(() => client.SwitchToNewIdentity(phrases));   // key derivation and re-keying of the local database
			New1.Text = New2.Text = New3.Text = "";
			Result.Text = Loc.T("mig_done");
			AppRestart.Now();   // the profile opens again as the new identity
		}
		catch (ClientException ex) { Result.Text = ErrorText.Of(ex); }
		finally { Busy(false); }
	}
}
