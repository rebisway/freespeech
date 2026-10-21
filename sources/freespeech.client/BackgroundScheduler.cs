/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

namespace FreeSpeech.Client;

/// <summary>
/// Single periodic background tick standing in for the mobile OS scheduler
/// (WorkManager/BGTaskScheduler, §9's ~15 min push fallback). Pending contacts (30s) and
/// pending messages are retried from this one tick, on purpose: on a real device you get exactly one background wake-up point,
/// not two competing timers doing the same "is anyone reachable now?" check.
/// Register every retry action that should run on that same wake-up.
/// </summary>
public class BackgroundScheduler : IAsyncDisposable
{
    private readonly TimeSpan _interval;
    private readonly List<Func<Task>> _ticks = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public BackgroundScheduler(TimeSpan interval)
    {
        _interval = interval;
    }

    /// <summary>Registers an action to run on every tick (e.g. retry pending contacts, flush an outbox).</summary>
    public void OnTick(Func<Task> action) => _ticks.Add(action);

    public void Start()
    {
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_interval, ct);
                foreach (var tick in _ticks)
                {
                    try { await tick(); }
                    catch { /* one failing tick must not stop the others or the loop */ }
                }
            }
        }
        catch (TaskCanceledException) { /* stopping */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop; } catch { /* already handled */ }
        }
        _cts.Dispose();
    }
}
