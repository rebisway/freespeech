/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Mobile.Services;
using FreeSpeech.Mobile.ViewModels;
using FreeSpeech.Mobile.Views;
using Microsoft.Extensions.Logging;
#if ANDROID
using ZXing.Net.Maui.Controls;
#endif

namespace FreeSpeech.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		StartupTrace.Begin();
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
#if ANDROID
			.UseBarcodeReader()
#endif
#if ANDROID
			.ConfigureMauiHandlers(handlers => handlers.AddHandler<Controls.VideoPlayerView, VideoPlayerHandler>())
#endif
#if ANDROID
			.ConfigureMauiHandlers(_ =>
			{
				// The message box in the chat, and the field that edits a message, have no underline: what is behind them is enough.
				Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
				{
					if (view is Editor { StyleId: "Composer" or "EditField" }) handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
				});
			})
#endif
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// One client for the whole app.
		builder.Services.AddSingleton<ClientService>();
#if ANDROID
		ClientService.Notifier = new AndroidNotifier();
		ClientService.Collage = new AndroidCollage();
		ClientService.KeepAlive = new AndroidKeepAlive();
		ClientService.Printer = new AndroidPrinter();
		ClientService.Recorder = new AndroidAudioRecorder();
		ClientService.Player = new AndroidAudioPlayer();
		ClientService.Thumbnailer = new AndroidVideoThumbnailer();
		ClientService.PictureThumbnailer = new AndroidPictureThumbnailer();
		ClientService.Documents = new AndroidDocumentPicker();
		// The protected screen: the flag of the current window, now and at each start (MainActivity).
		ClientService.ScreenGuard = on => MainThread.BeginInvokeOnMainThread(() =>
		{
			var window = Platform.CurrentActivity?.Window;
			if (on) window?.SetFlags(Android.Views.WindowManagerFlags.Secure, Android.Views.WindowManagerFlags.Secure);
			else window?.ClearFlags(Android.Views.WindowManagerFlags.Secure);
		});
		// The discreet keyboard: no suggestions and no learning from what is typed, in every text field made while the setting is on.
		Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("FsDiscreetKeyboard", (handler, _) => DiscreetKeyboard(handler.PlatformView));
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("FsDiscreetKeyboard", (handler, _) => DiscreetKeyboard(handler.PlatformView));
		// The free space of the partition that holds the app's files (the generic call does not see it on Android).
		FreeSpeech.Client.Attachments.AttachmentStore.FreeSpaceProbe = dir => new Android.OS.StatFs(dir).AvailableBytes;
#endif

		builder.Services.AddTransient<StartPage>();
		builder.Services.AddTransient<OnboardingPage>();
		builder.Services.AddTransient<OnboardingViewModel>();
		builder.Services.AddTransient<ConversationsPage>();
		builder.Services.AddTransient<ConversationsViewModel>();
		builder.Services.AddTransient<ChatPage>();
		builder.Services.AddTransient<ChatViewModel>();
		builder.Services.AddTransient<ContactsPage>();
		builder.Services.AddTransient<ContactsViewModel>();
		builder.Services.AddTransient<GroupsPage>();
		builder.Services.AddTransient<GroupsViewModel>();
		builder.Services.AddTransient<InvitePage>();
		builder.Services.AddTransient<InviteViewModel>();
		builder.Services.AddTransient<ApprovePage>();
		builder.Services.AddTransient<ApproveViewModel>();
		builder.Services.AddTransient<PhotoCropPage>();
		builder.Services.AddTransient<SafetyPage>();
		builder.Services.AddTransient<SafetyViewModel>();
		builder.Services.AddTransient<WipePage>();
		builder.Services.AddTransient<MigratePage>();
		builder.Services.AddTransient<PhrasesBackupPage>();
		builder.Services.AddTransient<PhrasesBackupViewModel>();
		builder.Services.AddTransient<BackupPage>();
		builder.Services.AddTransient<AttachmentArchivePage>();
		builder.Services.AddTransient<AttachmentArchiveViewModel>();
		builder.Services.AddTransient<BackupViewModel>();
		builder.Services.AddTransient<NewGroupPage>();
		builder.Services.AddTransient<GroupInfoPage>();
		builder.Services.AddTransient<GroupJoinPage>();
		builder.Services.AddTransient<GroupJoinViewModel>();
		builder.Services.AddTransient<OutboxPage>();
		builder.Services.AddTransient<OutboxViewModel>();
		builder.Services.AddTransient<ArchivePage>();
		builder.Services.AddTransient<ArchiveViewModel>();
		builder.Services.AddTransient<GroupInfoViewModel>();
		builder.Services.AddTransient<NewGroupViewModel>();
		builder.Services.AddTransient<SharePage>();
		builder.Services.AddTransient<ShareViewModel>();
		builder.Services.AddTransient<SettingsPage>();
		builder.Services.AddTransient<SettingsViewModel>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		DiagLog.Apply();   // diagnostic journal: off unless the user switched it on in Settings
		var app = builder.Build();
		StartupTrace.Mark("app built");
		return app;
	}

#if ANDROID
	/// <summary>Asks the keyboard not to suggest and not to learn from this field (<c>IME_FLAG_NO_PERSONALIZED_LEARNING</c>, Android 8 and later). The keyboard decides whether it obeys.</summary>
	private static void DiscreetKeyboard(Android.Widget.EditText box)
	{
		if (!ClientService.DiscreetKeyboard) return;
		box.InputType |= Android.Text.InputTypes.TextFlagNoSuggestions;
		if (OperatingSystem.IsAndroidVersionAtLeast(26)) box.ImeOptions = (Android.Views.InputMethods.ImeAction)((int)box.ImeOptions | 0x01000000);
	}
#endif
}