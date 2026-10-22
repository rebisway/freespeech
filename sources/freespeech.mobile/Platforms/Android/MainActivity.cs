using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace FreeSpeech.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Appears in the system Share sheet: any file or text can be sent to FreeSpeech conversations.
[IntentFilter([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "*/*")]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public class MainActivity : MauiAppCompatActivity
{
	/// <summary>
	/// Set by the picture viewer while it is on screen. It sees every touch before the views do and takes nothing from
	/// them: MAUI's own recognizers (pan, double tap) keep working, which a touch listener on the view would break.
	/// </summary>
	public static ScaleGestureDetector? PinchDetector { get; set; }

	/// <summary>Same idea for the long press on a picture (it opens the message menu).</summary>
	public static GestureDetector? LongPressDetector { get; set; }

	/// <summary>The chat's long-press detector, and whether it fired for the touch in progress (the release is then cancelled, so it is not also a tap).</summary>
	public static GestureDetector? ChatLongPressDetector { get; set; }
	public static bool ChatLongPressFired { get; set; }

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		FreeSpeech.Mobile.Services.StartupTrace.ActivityCreated = true;   // a background check that starts now leaves the profile to the screen
		FreeSpeech.Mobile.Services.StartupTrace.Mark("MainActivity.OnCreate: start");
		base.OnCreate(savedInstanceState);
		FreeSpeech.Mobile.Services.StartupTrace.Mark("MainActivity.OnCreate: base done");
#if DEBUG
		FreeSpeech.Mobile.Services.Perf.WatchMainThread();   // slow jobs of the main thread in the log (adb logcat -s FSPERF); Debug builds only
#endif
		// Same dark tone as the app's navigation bar, so the system bar above it does not stand out.
#pragma warning disable CA1422   // SetStatusBarColor is obsolete from Android 15, where the bar simply shows what is behind it
		var light = FreeSpeech.Mobile.Services.ThemeService.IsLight;
		Window?.SetStatusBarColor(Android.Graphics.Color.ParseColor(light ? "#EFE9DD" : "#1C1812"));
		var bar = Android.Graphics.Color.ParseColor(light ? "#EFE9DD" : "#1C1812");
		Window?.DecorView?.SetBackgroundColor(bar);
		FindViewById(Android.Resource.Id.Content)?.SetBackgroundColor(bar);   // from Android 15 the bar shows what is behind it
		if (Window is { DecorView: { } dv } w && AndroidX.Core.View.WindowCompat.GetInsetsController(w, dv) is { } bars) bars.AppearanceLightStatusBars = light;   // dark icons on a light bar
#pragma warning restore CA1422
		if (FreeSpeech.Mobile.Services.ClientService.ProtectedScreen) Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);   // no screenshot, no preview in the recent apps
		Route(Intent);   // started by a tap on a notification
		FreeSpeech.Mobile.Services.ClientService.Notifier?.ApplyBackgroundPolling(FreeSpeech.Mobile.Services.ClientService.BackgroundWakeEnabled);
		FreeSpeech.Mobile.Services.StartupTrace.Mark("MainActivity.OnCreate: end");
	}

#pragma warning disable CA1422   // the old result callback is what the system document picker answers to
	protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		base.OnActivityResult(requestCode, resultCode, data);
		FreeSpeech.Mobile.Services.AndroidDocumentPicker.OnResult(requestCode, resultCode, data);
	}
#pragma warning restore CA1422

	protected override void OnNewIntent(Intent? intent)
	{
		base.OnNewIntent(intent);
		Route(intent);
	}

	private void Route(Intent? intent)
	{
		ShareIntake.Handle(this, intent);
		if (intent?.GetStringExtra("route") is { Length: > 0 } route) FreeSpeech.Mobile.Services.ClientService.RequestRoute(route);
	}

	public override bool DispatchTouchEvent(MotionEvent? e)
	{
		if (e != null)
		{
			PinchDetector?.OnTouchEvent(e);
			LongPressDetector?.OnTouchEvent(e);
			if (ChatLongPressDetector != null)
			{
				if (e.ActionMasked == MotionEventActions.Down) ChatLongPressFired = false;
				ChatLongPressDetector.OnTouchEvent(e);
				if (ChatLongPressFired && e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
				{
					ChatLongPressFired = false;
					e.Action = MotionEventActions.Cancel;   // the release of a long press is not a tap
				}
			}
		}
		return base.DispatchTouchEvent(e);
	}
}