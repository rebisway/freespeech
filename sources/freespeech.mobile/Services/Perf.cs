/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Diagnostics;

namespace FreeSpeech.Mobile.Services;

/// <summary>
/// Timing marks for the screens that feel slow, written to the Android log under the tag FSPERF (adb logcat -s FSPERF): the time since <see cref="Start"/>
/// and the time since the previous mark. Nothing is stored or sent.
/// </summary>
public static class Perf
{
	private static readonly Stopwatch Clock = new();
	private static long _last;

	public static void Start(string what)
	{
		Clock.Restart();
		_last = 0;
		Write(what);
	}

	public static void Mark(string what)
	{
		if (!Clock.IsRunning) return;
		Write(what);
	}

#if ANDROID
	/// <summary>Logs every job of the main thread that lasts more than <paramref name="thresholdMs"/>, with what the system says it was (the handler, the callback).</summary>
	public static void WatchMainThread(int thresholdMs = 100)
	{
		Android.OS.Looper.MainLooper?.SetMessageLogging(new SlowJobs(thresholdMs));
		// While the main thread is busy for long, look at where it is every 150 ms: the top of its Java stack says which view is being measured or drawn.
		new System.Threading.Thread(() =>
		{
			var main = Android.OS.Looper.MainLooper?.Thread;
			while (main != null)
			{
				System.Threading.Thread.Sleep(150);
				var started = System.Threading.Interlocked.Read(ref _busySince);
				if (started == 0 || (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency < thresholdMs) continue;
				try
				{
					var frames = main.GetStackTrace() ?? [];
					Android.Util.Log.Info("FSPERF", "  stack: " + string.Join(" < ", frames.Take(16).Select(f => $"{f.ClassName?.Split('.').LastOrDefault()}.{f.MethodName}")));
				}
				catch (System.Exception) { /* best effort */ }
			}
		}) { IsBackground = true, Name = "fsperf-sampler" }.Start();
	}

	private static long _busySince;

	private sealed class SlowJobs(int thresholdMs) : Java.Lang.Object, Android.Util.IPrinter
	{
		private long _start;
		private string _what = "";

		public void Println(string? x)
		{
			if (x != null && x.StartsWith(">>>>> Dispatching", StringComparison.Ordinal)) { _start = Stopwatch.GetTimestamp(); _what = x; System.Threading.Interlocked.Exchange(ref _busySince, _start); return; }
			if (x != null && x.StartsWith("<<<<< Finished", StringComparison.Ordinal) && _start != 0)
			{
				var ms = (Stopwatch.GetTimestamp() - _start) * 1000 / Stopwatch.Frequency;
				if (ms >= thresholdMs) Android.Util.Log.Info("FSPERF", $"main thread busy {ms} ms: {_what}");
				_start = 0;
				System.Threading.Interlocked.Exchange(ref _busySince, 0);
			}
		}
	}
#endif

	private static void Write(string what)
	{
		var now = Clock.ElapsedMilliseconds;
		var line = $"{now,5} ms (+{now - _last,4}) {what}";
		_last = now;
#if ANDROID
		Android.Util.Log.Info("FSPERF", line);
#else
		Debug.WriteLine("FSPERF " + line);
#endif
	}
}
