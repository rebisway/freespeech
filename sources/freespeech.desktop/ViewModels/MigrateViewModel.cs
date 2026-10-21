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
using FreeSpeech.Client.Groups;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

/// <summary>One group I lead or moderate: who takes over (the creator chooses a successor).</summary>
public sealed partial class RoleRow(FreeSpeechClient.RoleHandover role) : ObservableObject
{
	[ObservableProperty] private GroupMember? _successor;

	public FreeSpeechClient.RoleHandover Role { get; } = role;
	public string Name => Role.Group.Name;
	public bool IsCreator => Role.IsCreator;
	public bool IsModerator => !Role.IsCreator;
	public IReadOnlyList<GroupMember> Candidates => Role.Candidates;
	public string Explanation => Loc.T(Role.IsCreator ? "mig_group_creator" : "mig_group_moderator");
	public string PickHint => Loc.T("mig_pick");
}

/// <summary>
/// Moving to a new identity after a theft (PROTOCOL section 12). Step 1: choose who takes over the groups I lead or moderate, type the
/// OLD phrases (they sign the notice), then the app draws the NEW phrases and sends the user through the save screen, which sends the
/// notice. Step 2: once the notice has reached everyone, the new phrases switch this computer to the new identity. Step 2 is also what
/// opens when the app was restarted between the two: the state lives in the profile, the phrases never do.
/// </summary>
public sealed partial class MigrateViewModel : ObservableObject
{
	private readonly MainViewModel _owner;

	[ObservableProperty] private string _old1 = "";
	[ObservableProperty] private string _old2 = "";
	[ObservableProperty] private string _old3 = "";
	[ObservableProperty] private string _new1 = "";
	[ObservableProperty] private string _new2 = "";
	[ObservableProperty] private string _new3 = "";
	[ObservableProperty] private string _result = "";
	[ObservableProperty] private bool _busy;
	[ObservableProperty, NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2))] private string? _announced;
	[ObservableProperty] private string _newIdText = "";
	[ObservableProperty] private string _pendingText = "";
	[ObservableProperty] private bool _noGroups;

	public MigrateViewModel(MainViewModel owner)
	{
		_owner = owner;
		Refresh();
	}

	public ObservableCollection<RoleRow> Roles { get; } = [];

	private FreeSpeechClient Client => _owner.Session.Client;

	public bool IsStep1 => Announced == null;
	public bool IsStep2 => Announced != null;

	public string IntroText => Loc.T("mig_intro");
	public string GroupsTitle => Loc.T("mig_groups");
	public string NoGroupsText => Loc.T("mig_no_groups");
	public string OldPhrasesTitle => Loc.T("mig_old_phrases");
	public string ContinueLabel => Loc.T("mig_continue");
	public string SentTitle => Loc.T("mig_sent_title");
	public string WaitText => Loc.T("mig_wait");
	public string RefreshLabel => Loc.T("mig_refresh");
	public string FinishHint => Loc.T("mig_finish_hint");
	public string FinishLabel => Loc.T("mig_finish");

	[RelayCommand]
	private void Refresh()
	{
		Announced = Client.OutgoingMigrationId;
		if (Announced != null)
		{
			NewIdText = Loc.T("mig_new_id", Announced[..8]);
			PendingText = Loc.T("mig_pending", Client.PendingMigrationLetters);
			return;
		}

		Roles.Clear();
		var roles = Client.RolesToHandOver();
		NoGroups = roles.Count == 0;
		foreach (var role in roles) Roles.Add(new RoleRow(role));
	}

	[RelayCommand]
	private async Task ContinueAsync()
	{
		if (Busy) return;
		Result = "";
		var old = new[] { Old1, Old2, Old3 }.Select(p => p.Trim()).ToArray();
		if (old.Any(p => !FreeSpeechClient.IsPhraseValid(p))) { Result = Loc.T("onb_err_phrase"); return; }

		var successors = new Dictionary<string, string>();
		foreach (var row in Roles.Where(r => r.IsCreator))
		{
			if (row.Candidates.Count == 0) { Result = Loc.T("mig_err_no_candidates", row.Name); return; }
			if (row.Successor == null) { Result = Loc.T("mig_err_successor", row.Name); return; }
			successors[row.Role.Group.GroupId] = row.Successor.DiscoveryId;
		}

		Busy = true;
		try
		{
			await Task.Run(() => Client.CheckMyPhrases(old[0], old[1], old[2]));   // wrong phrases are caught before anything is changed
			await Client.HandOverRolesAsync(successors);

			var generator = await BookLoader.LoadAsync(Loc.Language);
			var drawn = generator.Generate().ToArray();
			NewIdentityDraft.Set(drawn, Client.Identity.Nickname ?? "", _owner.Session.Server);
			MigrationDraft.Set(old);
			Old1 = Old2 = Old3 = "";
			// The save screen sends the notice when it is left.
			_owner.Push(new PhrasesSaveViewModel(drawn) { ContinueAction = SendNoticeAsync }, Loc.T("ph_title"));
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException or IOException) { Result = ErrorText.Of(ex); }
		finally { Busy = false; }
	}

	/// <summary>Leaving the save screen: the notice signed with the OLD phrases goes out; the identity is not created here, the program switches later.</summary>
	private async Task SendNoticeAsync(PhrasesSaveViewModel save)
	{
		if (MigrationDraft.OldPhrases is not { } old || NewIdentityDraft.Phrases is not { } phrases) return;
		if (!save.Done && !await Ui.ConfirmAsync(Loc.T("ph_skip_title"), Loc.T("ph_skip_text"), Loc.T("ph_skip_yes"), Loc.T("ph_skip_no"))) return;
		try
		{
			var nick = NewIdentityDraft.Nickname;
			await Task.Run(() => Client.SendMovedAsync(old, phrases, nick));   // the slow key derivations stay off the UI thread
			MigrationDraft.Clear();
			NewIdentityDraft.Clear();
			_owner.Pop();   // back to this screen, now at step 2
			Refresh();
		}
		catch (Exception ex) when (ex is ClientException or InvalidOperationException or IOException) { save.Report(ErrorText.Of(ex)); }
	}

	[RelayCommand]
	private async Task FinishAsync()
	{
		if (Busy) return;
		Result = "";
		var phrases = new[] { New1, New2, New3 }.Select(p => p.Trim()).ToArray();
		if (phrases.Any(p => !FreeSpeechClient.IsPhraseValid(p))) { Result = Loc.T("onb_err_phrase"); return; }
		if (Client.PendingMigrationLetters > 0
			&& !await Ui.ConfirmAsync(Loc.T("mig_finish"), Loc.T("mig_finish_pending", Client.PendingMigrationLetters), Loc.T("mig_finish_yes"), Loc.T("cancel"))) return;

		Busy = true;
		try
		{
			await Task.Run(() => Client.SwitchToNewIdentity(phrases));   // key derivation and re-keying of the local database
			New1 = New2 = New3 = "";
			Result = Loc.T("mig_done");
			AppRestart.Now();   // the profile opens again as the new identity
		}
		catch (ClientException ex) { Result = ErrorText.Of(ex); }
		finally { Busy = false; }
	}
}
