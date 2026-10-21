/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpeech.Client.Identity;
using FreeSpeech.Client.Security;
using FreeSpeech.Desktop.Services;

namespace FreeSpeech.Desktop.ViewModels;

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
public sealed partial class LockViewModel : ObservableObject
{
	private readonly TaskCompletionSource<bool> _done = new();

	[ObservableProperty] private string _pin = "";
	[ObservableProperty] private string _confirm = "";
	[ObservableProperty] private string _error = "";
	[ObservableProperty] private bool _busy;
	[ObservableProperty] private bool _forgotOpen;
	[ObservableProperty] private string _phrase1 = "";
	[ObservableProperty] private string _phrase2 = "";
	[ObservableProperty] private string _phrase3 = "";

	public LockViewModel(LockMode mode) => Mode = mode;

	public LockMode Mode { get; }

	public Task<bool> Result => _done.Task;

	public bool IsSet => Mode == LockMode.Set;
	public bool CanCancel => Mode != LockMode.Unlock;
	public bool CanForget => Mode == LockMode.Unlock;
	public string Title => Loc.T(Mode switch { LockMode.Unlock => "lock_title_unlock", LockMode.Verify => "lock_title_verify", _ => "lock_title_set" });
	public string Hint => IsSet ? Loc.T("lock_set_hint", PinLock.MinDigits, PinLock.MaxDigits) : "";
	public string PinHint => Loc.T(IsSet ? "lock_new_hint" : "lock_pin_hint");
	public string ConfirmHint => Loc.T("lock_confirm_hint");
	public string ForgotText => Loc.T("lock_forgot");
	public string ForgotHint => Loc.T("lock_forgot_hint");
	public string OkTip => Loc.T("ok");
	public string CancelTip => Loc.T("cancel");

	private void Fail(string text)
	{
		Error = text;
		Pin = Confirm = "";
	}

	[RelayCommand]
	private async Task OkAsync()
	{
		if (Busy) return;
		if (!PinLock.IsValidPin(Pin)) { Fail(Loc.T("lock_invalid", PinLock.MinDigits, PinLock.MaxDigits)); return; }
		Busy = true;
		try
		{
			if (IsSet)
			{
				if (Pin != Confirm) { Fail(Loc.T("lock_mismatch")); return; }
				await AppLock.SetPinAsync(Pin);
				Finish(true);
				return;
			}
			switch (await AppLock.VerifyAsync(Pin))
			{
				case PinResult.Ok: Finish(true); break;
				case PinResult.Wiped: break;   // the app is restarting on a clean profile
				default: Fail(AppLock.AttemptsLeft is { } n ? Loc.T("lock_wrong_left", n) : Loc.T("lock_wrong")); break;
			}
		}
		finally { Busy = false; }
	}

	[RelayCommand] private void Cancel() => Finish(false);

	[RelayCommand] private void ToggleForgot() => ForgotOpen = !ForgotOpen;

	/// <summary>The three phrases give the same database key as the one this computer keeps: it is the owner, the lock is switched off.</summary>
	[RelayCommand]
	private async Task PhrasesAsync()
	{
		if (Busy) return;
		Busy = true;
		try
		{
			var (p1, p2, p3) = (Phrase1.Trim(), Phrase2.Trim(), Phrase3.Trim());
			var same = await Task.Run(() =>
			{
				try
				{
					var typed = IdentityService.DeriveDatabaseKey(p1, p2, p3);
					var kept = DesktopKeyCache.ForProfile().Load();
					return kept != null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(typed, kept);
				}
				catch (ArgumentException) { return false; }
			});
			if (!same) { Fail(Loc.T("lock_phrases_wrong")); return; }
			AppLock.Disable();
			Finish(true);
		}
		finally { Busy = false; }
	}

	private void Finish(bool ok)
	{
		if (Mode == LockMode.Unlock) AppLock.Showing = false;
		Pin = Confirm = Phrase1 = Phrase2 = Phrase3 = "";
		_done.TrySetResult(ok);
	}
}
