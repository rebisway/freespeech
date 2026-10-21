/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Security;
using FreeSpeech.Mobile.Localization;
using FreeSpeech.Mobile.Services;

namespace FreeSpeech.Mobile.Views;

public enum LockMode
{
	/// <summary>The app is locked: the PIN opens it. No way out but the PIN (or the phrases).</summary>
	Unlock,
	/// <summary>Asks for the current PIN before a sensitive change (switching the lock off, changing the code).</summary>
	Verify,
	/// <summary>Chooses a new PIN, typed twice.</summary>
	Set,
}

/// <summary>The PIN screen, shown over everything else. <see cref="Result"/> is true when the PIN was accepted (or set).</summary>
public partial class LockPage : ContentPage
{
	private readonly LockMode _mode;
	private readonly TaskCompletionSource<bool> _done = new();
	private bool _busy;

	public Task<bool> Result => _done.Task;

	public LockPage(LockMode mode)
	{
		InitializeComponent();
		_mode = mode;
	TitleLabel.Text = Loc.T(mode switch { LockMode.Unlock => "lock_title_unlock", LockMode.Verify => "lock_title_verify", _ => "lock_title_set" });
		Hint.Text = mode == LockMode.Set ? Loc.T("lock_set_hint", PinLock.MinDigits, PinLock.MaxDigits) : "";
		Hint.IsVisible = mode == LockMode.Set;
		Pin.Placeholder = Loc.T(mode == LockMode.Set ? "lock_new_hint" : "lock_pin_hint");
		Confirm.IsVisible = mode == LockMode.Set;
		CancelButton.IsVisible = mode != LockMode.Unlock;
		ForgotLink.IsVisible = mode == LockMode.Unlock;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		Pin.Focus();
	}

	// Back never gets round the lock; on the other screens it is a cancel.
	protected override bool OnBackButtonPressed()
	{
		if (_mode != LockMode.Unlock) OnCancel(null, EventArgs.Empty);
		return true;
	}

	private void ShowError(string text)
	{
		Error.Text = text;
		Error.IsVisible = true;
		Pin.Text = Confirm.Text = "";
		Pin.Focus();
	}

	private void OnPinCompleted(object? sender, EventArgs e)
	{
		if (_mode == LockMode.Set) Confirm.Focus(); else OnOk(sender, e);
	}

	private void OnConfirmCompleted(object? sender, EventArgs e) => OnOk(sender, e);

	private async void OnOk(object? sender, EventArgs e)
	{
		if (_busy) return;
		var pin = Pin.Text ?? "";
		if (!PinLock.IsValidPin(pin)) { ShowError(Loc.T("lock_invalid", PinLock.MinDigits, PinLock.MaxDigits)); return; }
		_busy = true;
		Spinner.IsVisible = Spinner.IsRunning = true;
		try
		{
			if (_mode == LockMode.Set)
			{
				if (pin != Confirm.Text) { ShowError(Loc.T("lock_mismatch")); return; }
				await AppLock.SetPinAsync(pin);
				await Finish(true);
				return;
			}
			switch (await AppLock.VerifyAsync(pin))
			{
				case PinResult.Ok: await Finish(true); break;
				case PinResult.Wiped: break;   // the app is restarting on a clean phone
				default:
					var left = AppLock.AttemptsLeft;
					ShowError(left is { } n ? Loc.T("lock_wrong_left", n) : Loc.T("lock_wrong"));
					break;
			}
		}
		finally { _busy = false; Spinner.IsVisible = Spinner.IsRunning = false; }
	}

	private async void OnCancel(object? sender, EventArgs e) => await Finish(false);

	private async Task Finish(bool ok)
	{
		if (_mode == LockMode.Unlock)
		{
			AppLock.Showing = false;
			ClientService.InForeground = true;
		}
		KeyboardHelper.Hide();
		_done.TrySetResult(ok);
		try { await Navigation.PopModalAsync(false); }
		catch (InvalidOperationException) { /* already closed */ }
	}

	private void OnForgot(object? sender, EventArgs e) => ForgotBox.IsVisible = !ForgotBox.IsVisible;

	/// <summary>The three phrases give the same database key as the one this phone keeps: it is the owner, the lock is switched off.</summary>
	private async void OnPhrases(object? sender, EventArgs e)
	{
		if (_busy) return;
		_busy = true;
		Spinner.IsVisible = Spinner.IsRunning = true;
		try
		{
			var (p1, p2, p3) = (Phrase1.Text ?? "", Phrase2.Text ?? "", Phrase3.Text ?? "");
			var same = await Task.Run(() =>
			{
				try
				{
					var typed = IdentityService.DeriveDatabaseKey(p1, p2, p3);
					var kept = new SecureStorageKeyCache().Load();
					return kept != null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(typed, kept);
				}
				catch (ArgumentException) { return false; }
			});
			if (!same) { ShowError(Loc.T("lock_phrases_wrong")); return; }
			AppLock.Disable();
			await Finish(true);
		}
		finally { _busy = false; Spinner.IsVisible = Spinner.IsRunning = false; }
	}
}
