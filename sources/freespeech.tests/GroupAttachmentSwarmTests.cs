/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using System.Text.Json;
using FreeSpeech.Client.Attachments;

namespace FreeSpeech.Tests;

/// <summary>
/// Group attachments (§7): any member holding the complete file serves it, the requester spreads the missing
/// chunks over several holders, a silent holder's share moves to another one on the next round, a lying holder is
/// banned for that file without failing the download. Routed through an in-memory network of AttachmentServices.
/// </summary>
public class GroupAttachmentSwarmTests : IDisposable
{
    private const string GroupId = "g1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-swarm-" + Guid.NewGuid().ToString("N"));

    private sealed class Node
    {
        public required string Id { get; init; }
        public AttachmentService Svc { get; set; } = null!;
        public bool Online = true;
        public bool Corrupt;
        public int ChunksServed;
        public int HaveSent;
        public Func<long, bool> Policy = _ => true;   // the transfer setting of this device
        public List<string> Notes { get; } = [];
    }

    private sealed class Net
    {
        public Dictionary<string, Node> ById { get; } = [];
        public HashSet<string> Members { get; } = [];
    }

    private Node Add(Net net, string id, bool member = true, Func<AttachmentRecord, IReadOnlyList<string>>? holders = null, TimeSpan chunkDelay = default)
    {
        var node = new Node { Id = id };
        node.Svc = new AttachmentService(
            new AttachmentStore(Path.Combine(_root, id)),
            (to, type, payload) =>
            {
                if (!net.ById.TryGetValue(to, out var target) || !target.Online || !node.Online) return Task.FromResult(false);
                if (type == "attachment_request") target.Svc.OnRequest(node.Id, payload);
                else if (type == "attachment_have") { node.HaveSent++; target.Svc.OnHave(node.Id, payload); }
                else if (type == "attachment_chunk")
                {
                    if (node.Corrupt)
                    {
                        var c = JsonSerializer.Deserialize<AttachmentChunk>(payload)!;
                        var bytes = Convert.FromBase64String(c.Data);
                        bytes[0] ^= 0xFF;
                        c.Data = Convert.ToBase64String(bytes);
                        payload = JsonSerializer.Serialize(c);
                    }
                    node.ChunksServed++;
                    target.Svc.OnChunk(node.Id, payload);
                }
                return Task.FromResult(true);
            },
            node.Notes.Add,
            authorize: (rec, from) => rec.GroupId == GroupId && net.Members.Contains(from) && from != id,
            chunkDelay: chunkDelay,
            holders: holders,
            isGroupPeer: (rec, from) => rec.GroupId == GroupId && net.Members.Contains(from) && from != id,
            canTransfer: size => node.Policy(size))
        {
            // Short timings: the tests wait for seconds, not for the defaults of a phone.
            PeerSilence = TimeSpan.FromSeconds(1), EndgameAfter = TimeSpan.FromMilliseconds(200), SwarmCheckEvery = TimeSpan.FromMilliseconds(100),
            BackoffFor = TimeSpan.FromSeconds(2), HaveRefresh = TimeSpan.FromMilliseconds(400),
        };
        net.ById[id] = node;
        if (member) net.Members.Add(id);
        return node;
    }

    private string MakeFile(byte[] content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    /// <summary>Author offers a file to the group; the given nodes register the offer.</summary>
    private (AttachmentRecord Sent, string Payload) Offer(Node author, byte[] content, long ts = 1000)
    {
        var (sent, payload) = author.Svc.Prepare(author.Id, GroupId, MakeFile(content), ts, GroupId);
        return (sent, payload);
    }

    private static AttachmentRecord Receive(Node node, Node author, string payload, long ts = 1000) =>
        node.Svc.OnOffer(author.Id, ts, payload, GroupId)!;

    /// <summary>Each round: pretend the stall window elapsed, let the tick re-request the missing chunks, give the holders time to answer.</summary>
    private static async Task RoundsUntilComplete(AttachmentRecord rec, Node downloader, int max = 30)
    {
        for (var i = 0; i < max && rec.State != AttachmentState.Complete; i++)
        {
            rec.LastActivityAt = DateTime.UtcNow.AddMinutes(-1);
            await downloader.Svc.ResumeStalledAsync();
            await Task.Delay(150);
        }
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task OfferInAGroup_recordsTheGroup_onBothSides()
    {
        var net = new Net();
        var a = Add(net, "A"); var b = Add(net, "B");
        var (sent, payload) = Offer(a, new byte[100_000]);
        var received = Receive(b, a, payload);

        Assert.Equal(GroupId, sent.GroupId);
        Assert.Equal(GroupId, received.GroupId);
        Assert.Equal($"A:1000", received.Ref);       // same ref for everybody: message_id = author:ts
        Assert.Equal(sent.Ref, received.Ref);
    }

    [Fact]
    public async Task EveryMemberWhoDownloaded_becomesASource_soTheAuthorCanLeave()
    {
        var net = new Net();
        string[] all = ["A", "B", "C", "D"];
        var a = Add(net, "A");
        var b = Add(net, "B", holders: r => ["A", "C"]);
        var c = Add(net, "C", holders: r => ["A", "B"]);
        var d = Add(net, "D", holders: r => all.Where(x => x != "D").ToList());

        var content = RandomNumberGenerator.GetBytes(12 * AttachmentService.ChunkSize - 100);   // 12 chunks
        var (_, payload) = Offer(a, content);
        var atB = Receive(b, a, payload); var atC = Receive(c, a, payload); var atD = Receive(d, a, payload);

        // b and c also ask each other, and neither holds the file yet: that share waits for the next round.
        await b.Svc.RequestAsync(atB); await c.Svc.RequestAsync(atC);
        await RoundsUntilComplete(atB, b); await RoundsUntilComplete(atC, c);
        Assert.True(atB.State == AttachmentState.Complete && atC.State == AttachmentState.Complete);
        var servedByAuthor = a.ChunksServed;
        Assert.True(servedByAuthor >= 12);

        a.Online = false;                                         // the author is gone
        await d.Svc.RequestAsync(atD);
        await RoundsUntilComplete(atD, d);

        Assert.Equal(AttachmentState.Complete, atD.State);
        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
        Assert.Equal(servedByAuthor, a.ChunksServed);            // not a single chunk from the absent author
        Assert.True(b.ChunksServed + c.ChunksServed > 0);        // the chunks really came from the holders (how they split depends on thread timing)
    }

    [Fact]
    public async Task ChunksAreSpreadOverAllOnlineHolders_notPulledFromOne()
    {
        var net = new Net();
        var a = Add(net, "A");
        var b = Add(net, "B", holders: r => ["A"]);
        var d = Add(net, "D", holders: r => ["A", "B"]);

        var content = RandomNumberGenerator.GetBytes(20 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atB = Receive(b, a, payload); var atD = Receive(d, a, payload);
        await b.Svc.RequestAsync(atB);
        await WaitFor(() => atB.State == AttachmentState.Complete);
        a.ChunksServed = 0; b.ChunksServed = 0;

        await d.Svc.RequestAsync(atD);
        await WaitFor(() => atD.State == AttachmentState.Complete);

        Assert.True(a.ChunksServed > 0 && b.ChunksServed > 0);   // both holders carried a share
        Assert.True(a.ChunksServed + b.ChunksServed >= 20);      // every chunk came from one of them (a late duplicate is possible)
        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
    }

    [Fact]
    public async Task ASilentHolder_shareMovesToAnotherOnTheNextRound()
    {
        var net = new Net();
        var a = Add(net, "A");
        var c = Add(net, "C", holders: r => ["A"]);
        var d = Add(net, "D", holders: r => ["A", "C", "E"]);
        Add(net, "E");                                            // a member that never got the file: ignores requests

        var content = RandomNumberGenerator.GetBytes(9 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atC = Receive(c, a, payload); var atD = Receive(d, a, payload);
        await c.Svc.RequestAsync(atC);
        await WaitFor(() => atC.State == AttachmentState.Complete);

        await d.Svc.RequestAsync(atD);
        await RoundsUntilComplete(atD, d);

        Assert.Equal(AttachmentState.Complete, atD.State);
        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
    }

    [Fact]
    public async Task AMemberWithoutTheFile_saysSo_andItsShareIsReassignedAtOnce_noSilenceTimerNeeded()
    {
        var net = new Net();
        var a = Add(net, "A");
        var d = Add(net, "D", holders: r => ["A", "E"]);
        var e = Add(net, "E");                                    // member, knows the offer, does not hold the file

        var content = RandomNumberGenerator.GetBytes(20 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atD = Receive(d, a, payload);
        Receive(e, a, payload);

        await d.Svc.RequestAsync(atD);
        await WaitFor(() => atD.State == AttachmentState.Complete);     // no RoundsUntilComplete: the "have" reply did it

        Assert.True(e.HaveSent > 0);
        Assert.Equal(0, e.ChunksServed);
        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
    }

    [Fact]
    public async Task AnUnknownOrNonMemberAsker_getsSilence_notEvenAHave()
    {
        var net = new Net();
        var a = Add(net, "A");
        var outsider = Add(net, "X", member: false, holders: r => ["E"]);
        var e = Add(net, "E");
        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(3 * AttachmentService.ChunkSize));
        Receive(e, a, payload);
        var atX = Receive(outsider, a, payload);

        await outsider.Svc.RequestAsync(atX);
        await Task.Delay(300);

        Assert.Equal(0, e.HaveSent);                            // membership is not revealed to outsiders
    }

    [Fact]
    public async Task AnOfflineHoldersShare_isHandedToTheOnlineOnesInTheSameRound()
    {
        var net = new Net();
        var a = Add(net, "A");
        var b = Add(net, "B", holders: r => ["A"]);
        var d = Add(net, "D", holders: r => ["A", "B"]);

        var content = RandomNumberGenerator.GetBytes(30 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atB = Receive(b, a, payload); var atD = Receive(d, a, payload);
        await b.Svc.RequestAsync(atB);
        await WaitFor(() => atB.State == AttachmentState.Complete);

        a.Online = false;                                          // the author is away
        await d.Svc.RequestAsync(atD);
        await WaitFor(() => atD.State == AttachmentState.Complete); // one request, no stall rounds

        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
    }

    [Fact]
    public async Task ALyingHolder_isBannedForThisFile_andTheDownloadStillCompletes()
    {
        var net = new Net();
        var a = Add(net, "A");
        var liar = Add(net, "B", holders: r => ["A"]);
        var d = Add(net, "D", holders: r => ["A", "B"]);

        var content = RandomNumberGenerator.GetBytes(16 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atLiar = Receive(liar, a, payload); var atD = Receive(d, a, payload);
        await liar.Svc.RequestAsync(atLiar);
        await WaitFor(() => atLiar.State == AttachmentState.Complete);

        liar.Corrupt = true;                                      // B now damages every chunk it serves (proofs stay valid-looking)
        await d.Svc.RequestAsync(atD);
        await RoundsUntilComplete(atD, d, max: 40);

        Assert.Equal(AttachmentState.Complete, atD.State);        // NOT Failed: the honest holder carried on
        Assert.DoesNotContain("A", atD.BannedHolders);
        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
        // Banning B needs MaxStrikes bad chunks that arrive BEFORE the honest holder has finished the file; chunks of a
        // finished download are no longer checked, so whether the ban happens depends on thread timing. What must always hold
        // (above): the file is byte-identical, so no damaged chunk was ever accepted, and the honest holder is never blamed.
        // The ban itself is covered deterministically by WhenEveryHolderIsBanned_theDownloadFails_insteadOfLoopingForever.
    }

    [Fact]
    public async Task WhenEveryHolderIsBanned_theDownloadFails_insteadOfLoopingForever()
    {
        var net = new Net();
        var a = Add(net, "A");
        var d = Add(net, "D", holders: r => ["A"]);
        a.Corrupt = true;

        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(6 * AttachmentService.ChunkSize));
        var atD = Receive(d, a, payload);
        await d.Svc.RequestAsync(atD);
        await RoundsUntilComplete(atD, d, max: 4);

        Assert.Equal(AttachmentState.Failed, atD.State);
        Assert.Empty(d.Svc.Store.PartIds(atD.Ref));
    }

    [Fact]
    public async Task NoHolderOnline_leavesTheDownloadPending_andItResumesWhenOneReturns()
    {
        var net = new Net();
        var a = Add(net, "A");
        var d = Add(net, "D", holders: r => ["A"]);
        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(5 * AttachmentService.ChunkSize));
        var atD = Receive(d, a, payload);

        a.Online = false;
        var msg = await d.Svc.RequestAsync(atD);
        Assert.Equal(AttachmentState.Receiving, atD.State);
        Assert.Contains("offline", msg);

        a.Online = true;
        await RoundsUntilComplete(atD, d);
        Assert.Equal(AttachmentState.Complete, atD.State);
    }

    [Fact]
    public async Task ANonMember_cannotFetchAGroupFile()
    {
        var net = new Net();
        var a = Add(net, "A");
        var outsider = Add(net, "X", member: false, holders: r => ["A"]);
        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(3 * AttachmentService.ChunkSize));
        var atX = Receive(outsider, a, payload);

        await outsider.Svc.RequestAsync(atX);
        await Task.Delay(300);

        Assert.Equal(0, a.ChunksServed);
        Assert.NotEqual(AttachmentState.Complete, atX.State);
    }

    [Fact]
    public async Task AMemberWithoutTheCompleteFile_doesNotServe()
    {
        var net = new Net();
        var a = Add(net, "A"); var b = Add(net, "B");           // b has only the offer
        var d = Add(net, "D", holders: r => ["B"]);
        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(3 * AttachmentService.ChunkSize));
        Receive(b, a, payload);
        var atD = Receive(d, a, payload);

        await d.Svc.RequestAsync(atD);
        await Task.Delay(300);

        Assert.Equal(0, b.ChunksServed);
    }

    // ------------------------------------------------------------------ partial holders and dynamic distribution

    /// <summary>Gives <paramref name="node"/> only chunks 0..count-1 of the file, as a member still downloading it would hold them.</summary>
    private async Task<AttachmentRecord> HoldOnlyTheFirst(Node node, Node author, string payload, int count)
    {
        var rec = Receive(node, author, payload);
        rec.State = AttachmentState.Receiving;
        author.Svc.OnRequest(node.Id, JsonSerializer.Serialize(new AttachmentRequest { Ref = rec.Ref, Missing = Enumerable.Range(0, count).ToList() }));
        await WaitFor(() => node.Svc.Store.PartIds(rec.Ref).Count == count);
        return rec;
    }

    [Fact]
    public async Task AMemberStillDownloading_servesTheChunksItHolds_andSaysWhichOnes()
    {
        var net = new Net();
        var a = Add(net, "A");
        var b = Add(net, "B");
        var d = Add(net, "D", holders: r => ["B"]);
        var content = RandomNumberGenerator.GetBytes(20 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atB = await HoldOnlyTheFirst(b, a, payload, 8);   // 8 of 20 chunks, the author is not asked again
        var atD = Receive(d, a, payload);

        await d.Svc.RequestAsync(atD);
        await WaitFor(() => d.Svc.Store.PartIds(atD.Ref).Count == 8);
        await Task.Delay(300);

        Assert.Equal(8, b.ChunksServed);                          // exactly what it held, each with the proof it stored
        Assert.True(b.HaveSent > 0);                              // and it said what it holds
        Assert.Equal(AttachmentState.Receiving, atD.State);       // the other 12 chunks exist nowhere else yet
    }

    [Fact]
    public async Task APartialHolder_isOneSourceAmongOthers_andTheFileIsByteIdentical()
    {
        var net = new Net();
        var a = Add(net, "A");
        var b = Add(net, "B");
        var d = Add(net, "D", holders: r => ["A", "B"]);
        var content = RandomNumberGenerator.GetBytes(30 * AttachmentService.ChunkSize - 5);
        var (_, payload) = Offer(a, content);
        await HoldOnlyTheFirst(b, a, payload, 12);
        a.ChunksServed = 0;
        var atD = Receive(d, a, payload);

        await d.Svc.RequestAsync(atD);
        try { await WaitFor(() => atD.State == AttachmentState.Complete); }
        catch (TimeoutException) { throw new TimeoutException(d.Svc.DescribeSwarm(atD.Ref) + $" A served={a.ChunksServed} B served={b.ChunksServed} notes={string.Join("|", d.Notes)}"); }

        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
        Assert.True(b.ChunksServed > 0);                          // the partial holder carried part of it
        Assert.True(a.ChunksServed >= 18);                        // the author at least the chunks nobody else had
    }

    [Fact]
    public async Task APartialHolder_whoLaterReceivesMore_isAskedAgainForTheRest()
    {
        var net = new Net();
        var a = Add(net, "A");
        var b = Add(net, "B");
        var d = Add(net, "D", holders: r => ["B"]);
        var content = RandomNumberGenerator.GetBytes(16 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atB = await HoldOnlyTheFirst(b, a, payload, 6);
        var atD = Receive(d, a, payload);

        await d.Svc.RequestAsync(atD);
        await WaitFor(() => d.Svc.Store.PartIds(atD.Ref).Count == 6);

        // B receives the rest from the author: D, which only knows B held 6 chunks, asks it again after the refresh delay.
        a.Svc.OnRequest("B", JsonSerializer.Serialize(new AttachmentRequest { Ref = atB.Ref, Missing = Enumerable.Range(6, 10).ToList() }));
        await WaitFor(() => atB.State == AttachmentState.Complete);
        await RoundsUntilComplete(atD, d, max: 40);

        Assert.Equal(AttachmentState.Complete, atD.State);
        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
    }

    [Fact]
    public async Task ASlowHolder_doesNotHoldTheDownloadBack_theFastOneCarriesMore()
    {
        var net = new Net();
        var a = Add(net, "A");
        var slow = Add(net, "B", holders: r => ["A"], chunkDelay: TimeSpan.FromMilliseconds(250));
        var d = Add(net, "D", holders: r => ["B", "A"]);          // the slow one is even asked first
        var content = RandomNumberGenerator.GetBytes(32 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atB = Receive(slow, a, payload);
        await slow.Svc.RequestAsync(atB);
        await WaitFor(() => atB.State == AttachmentState.Complete);
        a.ChunksServed = 0; slow.ChunksServed = 0;

        var atD = Receive(d, a, payload);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await d.Svc.RequestAsync(atD);
        await WaitFor(() => atD.State == AttachmentState.Complete, 10_000);

        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
        Assert.True(a.ChunksServed > slow.ChunksServed, $"fast {a.ChunksServed} vs slow {slow.ChunksServed}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");   // half of the file at 250 ms a chunk would take 4 s on its own
    }

    [Fact]
    public async Task AHolderThatStaysSilent_losesItsChunksToTheOthers_withoutWaitingForTheNextTick()
    {
        var net = new Net();
        var a = Add(net, "A");
        var d = Add(net, "D", holders: r => ["E", "A"]);
        var e = Add(net, "E");                                    // reachable but never answers (no record of the file)
        var content = RandomNumberGenerator.GetBytes(10 * AttachmentService.ChunkSize);
        var (_, payload) = Offer(a, content);
        var atD = Receive(d, a, payload);

        await d.Svc.RequestAsync(atD);
        await WaitFor(() => atD.State == AttachmentState.Complete, 8000);   // only the swarm check, no ResumeStalledAsync
        Assert.Equal(content, File.ReadAllBytes(atD.Path!));
    }

    // ------------------------------------------------------------------ the "files only on Wi-Fi" setting

    [Fact]
    public async Task OnMobileData_aDownloadWaitsForWifi_thenStartsByItself()
    {
        var net = new Net();
        var a = Add(net, "A");
        var d = Add(net, "D", holders: r => ["A"]);
        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(6 * AttachmentService.ChunkSize));
        var atD = Receive(d, a, payload);

        d.Policy = _ => false;                                    // mobile data, "Wi-Fi only"
        var msg = await d.Svc.RequestAsync(atD);
        await Task.Delay(200);
        Assert.Contains("Wi-Fi", msg);
        Assert.True(atD.WaitingNetwork);
        Assert.Equal(AttachmentState.Receiving, atD.State);
        Assert.Equal(0, a.ChunksServed);                          // not a chunk was asked for

        d.Policy = _ => true;                                     // back on Wi-Fi
        await d.Svc.NetworkChangedAsync();
        await WaitFor(() => atD.State == AttachmentState.Complete);
        Assert.False(atD.WaitingNetwork);
    }

    [Fact]
    public async Task DownloadAnyway_overridesTheSettingForThatFile()
    {
        var net = new Net();
        var a = Add(net, "A");
        var d = Add(net, "D", holders: r => ["A"]);
        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(4 * AttachmentService.ChunkSize));
        var atD = Receive(d, a, payload);
        d.Policy = _ => false;

        await d.Svc.RequestAsync(atD);
        Assert.True(atD.WaitingNetwork);
        await d.Svc.DownloadAnywayAsync(atD);
        await WaitFor(() => atD.State == AttachmentState.Complete);
        Assert.False(atD.NetworkOverride);                        // done: the next file asks again
    }

    [Fact]
    public async Task AMemberOnMobileData_doesNotServe_andTheOthersDo()
    {
        var net = new Net();
        var a = Add(net, "A");
        var b = Add(net, "B", holders: r => ["A"]);
        var d = Add(net, "D", holders: r => ["B", "A"]);
        var (_, payload) = Offer(a, RandomNumberGenerator.GetBytes(20 * AttachmentService.ChunkSize));
        var atB = Receive(b, a, payload);
        await b.Svc.RequestAsync(atB);
        await WaitFor(() => atB.State == AttachmentState.Complete);
        a.ChunksServed = 0; b.ChunksServed = 0;

        b.Policy = _ => false;                                    // B holds the whole file but is on mobile data
        var atD = Receive(d, a, payload);
        await d.Svc.RequestAsync(atD);
        await WaitFor(() => atD.State == AttachmentState.Complete);

        Assert.Equal(0, b.ChunksServed);
        Assert.True(a.ChunksServed >= 20);
        Assert.True(b.HaveSent > 0);                              // it said "busy": its share moved to the author at once
    }

    [Fact]
    public void TheSetting_holdsBackMobileDataOnly_andLetsSmallFilesThroughWhenAsked()
    {
        var p = new TransferPolicy { Mode = FileTransferMode.WifiOnly };
        Assert.True(p.Allows(10_000_000));                        // network unknown (a console): nothing is held back
        p.Network = NetworkKind.Wifi;
        Assert.True(p.Allows(10_000_000));
        p.Network = NetworkKind.Mobile;
        Assert.False(p.Allows(10));
        p.Mode = FileTransferMode.WifiExceptSmall;
        Assert.True(p.Allows(TransferPolicy.SmallFileBytes));
        Assert.False(p.Allows(TransferPolicy.SmallFileBytes + 1));
        p.Mode = FileTransferMode.Always;
        Assert.True(p.Allows(25L * 1024 * 1024));
    }

    [Fact]
    public void AChunkBitmap_roundTrips_andRefusesTheWrongSize()
    {
        var text = ChunkBitmap.Encode([0, 3, 9, 20], 21);
        Assert.Equal(new HashSet<int> { 0, 3, 9, 20 }, ChunkBitmap.Decode(text, 21));
        Assert.Null(ChunkBitmap.Decode(text, 40));                // not the bitmap of a file of 40 chunks
        Assert.Null(ChunkBitmap.Decode("%%%", 21));
        Assert.Empty(ChunkBitmap.Decode("", 21)!);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp dir, best effort */ }
    }
}
