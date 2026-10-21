/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/
using FreeSpeech.Client;
using FreeSpeech.Client.History;
using Xunit;

namespace FreeSpeech.Tests;

/// <summary>An edit or a deletion for everyone made while the recipient is stopped reaches them when they start again.</summary>
public class OfflineChangeTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _server;
    private readonly List<string> _dirs = [];
    private readonly List<FreeSpeechClient> _clients = [];

    public OfflineChangeTests(ServerFixture server) => _server = server;

    private static string Pad(string s) => (s + " " + string.Concat(Enumerable.Repeat("x", 80))).Substring(0, 80);

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }

    private async Task<FreeSpeechClient> ClientAsync(string tag, string nickname)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-offchg-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var c = new FreeSpeechClient(new ClientOptions { ProfileDir = dir, ServerUrl = _server.Http });
        c.CreateIdentity(Pad(tag + " p1"), Pad(tag + " p2"), Pad(tag + " p3"), nickname);
        c.Initialize();
        await c.ConnectAsync();
        _clients.Add(c);
        return c;
    }

    public void Dispose()
    {
        foreach (var c in _clients) try { c.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { /* best effort */ }
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeletionForEveryone_madeWhileTheRecipientIsStopped_isAppliedWhenItStarts(bool edit)
    {
        var a = await ClientAsync("offchg a " + edit, "alice");
        var b = await ClientAsync("offchg b " + edit, "bob");
        await b.ImportCardAsync(a.ExportCard("qr"));
        Assert.True(await UntilAsync(() => a.Contacts.Count == 1 && b.Contacts.Count == 1));
        var bId = b.Identity.DiscoveryId;
        var aId = a.Identity.DiscoveryId;

        await a.SendAsync(a.FindConversation(bId)!, "to change");
        Assert.True(await UntilAsync(() => b.GetHistory(b.FindConversation(aId)!).Any(l => l.Entry.Text == "to change")));

        var bDir = b.ProfileDir;
        await b.DisposeAsync();
        _clients.Remove(b);
        Assert.True(await UntilAsync(() => !a.IsOnlineAsync(bId).Result));

        var conv = a.FindConversation(bId)!;
        if (edit) await a.EditMessageAsync(conv, 1, "changed"); else await a.RetractMessageAsync(conv, 1);

        var again = new FreeSpeechClient(new ClientOptions { ProfileDir = bDir, ServerUrl = _server.Http });
        Assert.True(again.TryLoadIdentity());
        again.Initialize();
        await again.ConnectAsync();
        _clients.Add(again);

        Assert.True(await UntilAsync(() =>
        {
            var e = again.GetHistory(again.FindConversation(aId)!).First().Entry;
            return edit ? e.Text == "changed" : e.State == MessageState.Retracted;
        }), "the change did not reach the restarted device");
    }
}
