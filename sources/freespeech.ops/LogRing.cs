/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace FreeSpeech.Ops;

public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    public override string ToString() =>
        $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Short(Level)}] {Category}: {Message}";

    private static string Short(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "---",
    };
}

/// <summary>
/// The last log lines, in memory only (never written to disk by this class), and a live feed for "log tail --follow".
/// What lands here is exactly what the logging pipeline lets through, so raising a level makes more appear.
/// </summary>
public sealed class LogRing
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<LogEntry> _lines = new();
    private readonly ConcurrentDictionary<Guid, Channel<LogEntry>> _followers = new();

    public LogRing(int capacity = 2000) => _capacity = capacity;

    public void Add(LogEntry entry)
    {
        _lines.Enqueue(entry);
        while (_lines.Count > _capacity && _lines.TryDequeue(out _)) { }
        foreach (var follower in _followers.Values) follower.Writer.TryWrite(entry);
    }

    /// <summary>The newest <paramref name="count"/> lines at <paramref name="minLevel"/> or above, oldest first.</summary>
    public IReadOnlyList<LogEntry> Last(int count, LogLevel minLevel = LogLevel.Trace) =>
        _lines.Where(e => e.Level >= minLevel).TakeLast(Math.Max(0, count)).ToList();

    /// <summary>
    /// Starts listening <b>now</b> and returns what was logged since, as it comes, until the subscription is disposed. Subscribe
    /// first, then read the past with <see cref="Last"/>: nothing can fall between the two. A slow reader loses the oldest lines
    /// instead of slowing the server.
    /// </summary>
    public Subscription Subscribe(LogLevel minLevel)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        _followers[id] = channel;
        return new Subscription(channel, minLevel, () => _followers.TryRemove(id, out _));
    }

    public sealed class Subscription(Channel<LogEntry> channel, LogLevel minLevel, Action onDispose) : IDisposable
    {
        public async IAsyncEnumerable<LogEntry> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct))
                if (entry.Level >= minLevel) yield return entry;
        }

        public void Dispose() => onDispose();
    }
}

/// <summary>Feeds the <see cref="LogRing"/> with every log call the pipeline lets through.</summary>
public sealed class LogRingProvider(LogRing ring) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RingLogger(categoryName, ring);
    public void Dispose() { }

    private sealed class RingLogger(string category, LogRing ring) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception != null) message += " | " + exception.GetType().Name + ": " + exception.Message;
            ring.Add(new LogEntry(DateTime.Now, logLevel, category, message));
        }
    }
}

/// <summary>
/// Per-identity trace, like Asterisk's "sip set debug peer": while a trace is active for an identity, the code paths that call
/// <see cref="Write"/> record what happens to it (registration, refusals, sends, wake-ups) in the log ring. Metadata only —
/// callers must never pass content or keys — and every trace expires on its own.
/// </summary>
public sealed class TraceService(LogRing ring)
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(60);

    private readonly ConcurrentDictionary<string, DateTime> _active = new();

    public void Start(string id, TimeSpan duration)
    {
        id = Normalize(id);
        if (id.Length < 8) throw new ArgumentException("give at least the first 8 characters of the identifier", nameof(id));
        if (duration <= TimeSpan.Zero || duration > MaxDuration) duration = MaxDuration;
        _active[id] = DateTime.UtcNow + duration;
        ring.Add(new LogEntry(DateTime.Now, LogLevel.Information, "trace", $"trace started for {Short(id)} for {duration.TotalMinutes:0} min (admin)"));
    }

    public bool Stop(string id)
    {
        var removed = _active.TryRemove(Normalize(id), out _);
        if (removed) ring.Add(new LogEntry(DateTime.Now, LogLevel.Information, "trace", $"trace stopped for {Short(Normalize(id))} (admin)"));
        return removed;
    }

    public void StopAll() { foreach (var id in _active.Keys) Stop(id); }

    public IReadOnlyList<(string Id, DateTime Until)> Active()
    {
        Purge();
        return _active.Select(a => (a.Key, a.Value)).ToList();
    }

    /// <summary>True when any trace is running — lets hot paths skip building the text.</summary>
    public bool Any => !_active.IsEmpty;

    /// <summary>Records <paramref name="text"/> for <paramref name="id"/> if a trace for it (full id or its start) is active.</summary>
    public void Write(string id, string text)
    {
        if (_active.IsEmpty) return;
        Purge();
        var normalized = Normalize(id);
        if (!_active.Keys.Any(k => normalized.StartsWith(k, StringComparison.Ordinal) || k.StartsWith(normalized, StringComparison.Ordinal))) return;
        ring.Add(new LogEntry(DateTime.Now, LogLevel.Information, "trace", $"{Short(normalized)} {text}"));
    }

    private void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, until) in _active)
            if (until <= now && _active.TryRemove(id, out _))
                ring.Add(new LogEntry(DateTime.Now, LogLevel.Information, "trace", $"trace for {Short(id)} expired"));
    }

    private static string Normalize(string id) => id.Trim().ToLowerInvariant();
    public static string Short(string id) => id.Length <= 8 ? id : id[..8];
}
