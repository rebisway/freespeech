/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using FreeSpeech.Client.Contacts;
using FreeSpeech.Client.Discovery;
using FreeSpeech.Client.Relay;

namespace FreeSpeech.Client;

public sealed partial class FreeSpeechClient
{
    /// <summary>True once <see cref="ConnectAsync"/> has run: a change of servers then also changes the connections.</summary>
    private bool _connectStarted;

    /// <summary>
    /// First connection of a new identity whose servers nobody chose (<see cref="UserIdentity.ServersInitial"/>): the first relays the application's
    /// directories offer become its servers; when none answers, one of the built-in fallback relays, drawn at random, takes the place of the configured
    /// default. The default is kept only when there is neither directory nor fallback.
    /// </summary>
    private async Task ChooseFirstServersAsync()
    {
        if (!_identity.ServersInitial || _identity.Servers.Count != 1 || RelayPool.Normalize(_identity.Servers[0].Url) != _cliServerUrl) return;

        var found = (await DirectoryPool.ResolveAsync(_options.Directories, _options.Country, 2)).Where(u => u != _cliServerUrl).ToList();
        if (found.Count == 0 && _options.FallbackServers.Count > 0)
        {
            var pool = _options.FallbackServers.Select(RelayPool.Normalize).Distinct().ToList();
            found = [pool[Random.Shared.Next(pool.Count)]];
        }
        if (found.Count == 0 || found.SequenceEqual([_cliServerUrl])) return;

        ReplaceServers(found);
    }

    /// <summary>Makes <paramref name="urls"/> the whole list of own servers (in that order) and keeps the ones left behind from coming back at the next start.</summary>
    private void ReplaceServers(IReadOnlyList<string> urls)
    {
        var left = _identity.Servers.Select(s => RelayPool.Normalize(s.Url)).Append(_cliServerUrl).Where(u => !urls.Contains(u)).Distinct().ToList();
        _identity.Servers.Clear();
        for (var i = 0; i < urls.Count; i++) _identity.Servers.Add(new ServerEntry { Url = urls[i], Priority = i });
        foreach (var u in left) if (!_identity.RetiredServers.Contains(u)) _identity.RetiredServers.Add(u);
        foreach (var u in urls) _identity.RetiredServers.Remove(u);
        _identityStorage.Save(_identity);
    }

    /// <summary>The addresses among <paramref name="urls"/> that answer, in the same order.</summary>
    private static async Task<List<string>> ReachableAsync(IEnumerable<string> urls)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        var tests = urls.Select(async u =>
        {
            try { using var response = await http.GetAsync(u.TrimEnd('/') + "/api/time"); return response.IsSuccessStatusCode ? u : null; }
            catch (Exception) { return null; }
        }).ToList();
        return (await Task.WhenAll(tests)).Where(u => u != null).Select(u => u!).ToList();
    }

    /// <summary>The same change on a running client: the new servers are joined, the ones left behind are dropped.</summary>
    private async Task SwitchServersAsync(IReadOnlyList<string> urls)
    {
        var before = _identity.Servers.Select(s => RelayPool.Normalize(s.Url)).ToList();
        ReplaceServers(urls);
        if (!_connectStarted) return;
        foreach (var u in urls.Where(u => !before.Contains(u))) await _relay.AddOwnAsync(u);
        foreach (var u in before.Where(u => !urls.Contains(u) && u != _cliServerUrl)) await _relay.RemoveOwnAsync(u);
    }

    /// <summary>
    /// A backup lists the servers of the identity it was made from: when this identity still has the servers it was given at creation and at least one
    /// of the backup's answers, the backup's list takes their place. Nothing changes when none answers.
    /// </summary>
    private async Task AdoptBackupServersAsync(IReadOnlyList<string> saved)
    {
        var urls = saved.Select(RelayPool.Normalize).Distinct().ToList();
        if (urls.Count == 0 || urls.Any(u => !Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))) return;
        if ((await ReachableAsync(urls)).Count == 0) return;
        _identity.ServersInitial = false;
        await SwitchServersAsync(urls);
    }

    /// <summary>
    /// The very first contact of an identity that kept its initial servers: when the contact is on other servers and one of them answers, those become
    /// this identity's servers, so both are on the same relay. Done once, whatever the outcome.
    /// </summary>
    private async Task AdoptFirstContactServersAsync(Contact contact)
    {
        if (!_identity.ServersInitial || _confirmedContacts.All.Count() > 1) return;
        _identity.ServersInitial = false;
        _identityStorage.Save(_identity);
        try
        {
            var theirs = contact.Servers.Where(s => s.Circle == null).OrderBy(s => s.Priority).Select(s => RelayPool.Normalize(s.Url))
                .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")).Distinct().ToList();
            var mine = _identity.Servers.Select(s => RelayPool.Normalize(s.Url)).ToHashSet();
            if (theirs.Count == 0 || theirs.Any(mine.Contains)) return;
            var answering = (await ReachableAsync(theirs)).Take(2).ToList();
            if (answering.Count == 0) return;
            await SwitchServersAsync(answering);
            await NotifyContactsAsync();
        }
        catch (Exception ex) { RaiseNotice($"[!] Could not move to the servers of {contact.DisplayName}: {ex.Message}"); }
    }

    /// <summary>
    /// Where to look for a relay when none of this identity's own can be reached: first the relays of the contacts, in random order — a contact on another
    /// relay is a way in that costs nobody anything — and only when every contact is on a relay already tried (or there is no contact) the directories
    /// the application knows. <paramref name="failed"/> are the addresses already tried and to leave out.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindRelaysAsync(IEnumerable<string>? failed = null, CancellationToken ct = default)
    {
        var skip = (failed ?? []).Concat(_identity?.Servers.Select(s => s.Url) ?? []).Append(_cliServerUrl).Select(RelayPool.Normalize).ToHashSet();

        var fromContacts = (_confirmedContacts?.All ?? []).SelectMany(c => c.Servers).Where(s => s.Circle == null).Select(s => RelayPool.Normalize(s.Url))
            .Where(u => !skip.Contains(u)).Distinct().OrderBy(_ => Random.Shared.Next()).ToList();
        if (fromContacts.Count > 0 || _options.Directories.Count == 0) return fromContacts;

        return (await DirectoryPool.ResolveAsync(_options.Directories, _options.Country, 3, ct: ct)).Where(u => !skip.Contains(u)).ToList();
    }
}
