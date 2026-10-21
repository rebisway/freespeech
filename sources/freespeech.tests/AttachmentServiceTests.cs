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
/// Attachment protocol (§6) without any network: <see cref="AttachmentService"/>
/// instances wired through an in-memory transport that can be cut or made to
/// lie, so the happy path, resume, swarm-style serving, content-addressed
/// de-duplication and every rejection rule are exercised deterministically.
/// </summary>
public class AttachmentServiceTests : IDisposable
{
    private const string AliceId = "aaaaaaaa11111111";
    private const string BobId = "bbbbbbbb22222222";
    private const string CarolId = "cccccccc33333333";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class Peer
    {
        public required string Id { get; init; }
        public required AttachmentService Service { get; set; }
        public Peer? Other { get; set; }
        public List<string> Notifications { get; } = [];
        public int SentChunks;
        public int DroppedChunks;   // chunks the transport refused (link down)
        public readonly List<int> SentIds = [];   // the ids of the chunks that left, in order (diagnostics)
        public int SentRequests;

        /// <summary>Chunks the transport still lets through; int.MaxValue = link up, 0 = peer offline.</summary>
        public int ChunkBudget = int.MaxValue;

        /// <summary>When set, every chunk leaving this peer has its data damaged (proof left intact) — a lying holder.</summary>
        public bool Corrupt;
    }

    private Peer NewPeer(string id, Func<AttachmentRecord, string, bool>? authorize = null)
    {
        var peer = new Peer { Id = id, Service = null! };
        peer.Service = new AttachmentService(
            new AttachmentStore(Path.Combine(_root, id)),
            (to, type, payload) =>
            {
                if (peer.Other == null) return Task.FromResult(false);
                switch (type)
                {
                    case "attachment_request":
                        peer.SentRequests++;
                        peer.Other.Service.OnRequest(peer.Id, payload);
                        return Task.FromResult(true);
                    case "attachment_chunk":
                        if (peer.ChunkBudget <= 0) { Interlocked.Increment(ref peer.DroppedChunks); return Task.FromResult(false); }
                        peer.ChunkBudget--;
                        peer.SentChunks++;
                        lock (peer.SentIds) peer.SentIds.Add(JsonSerializer.Deserialize<AttachmentChunk>(payload)!.ChunkId);
                        if (peer.Corrupt)
                        {
                            var c = JsonSerializer.Deserialize<AttachmentChunk>(payload)!;
                            var bytes = Convert.FromBase64String(c.Data);
                            bytes[0] ^= 0xFF;
                            c.Data = Convert.ToBase64String(bytes);
                            payload = JsonSerializer.Serialize(c);
                        }
                        peer.Other.Service.OnChunk(peer.Id, payload);
                        return Task.FromResult(true);
                    default:
                        return Task.FromResult(false);
                }
            },
            peer.Notifications.Add,
            authorize);
        return peer;
    }

    private (Peer Alice, Peer Bob) Pair()
    {
        var alice = NewPeer(AliceId);
        var bob = NewPeer(BobId);
        alice.Other = bob;
        bob.Other = alice;
        return (alice, bob);
    }

    private string MakeFile(string name, byte[] content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Random(int size)
    {
        var b = new byte[size];
        RandomNumberGenerator.Fill(b);
        return b;
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

    /// <summary>Sender prepares + receiver records the offer, as the Letter path would.</summary>
    private (AttachmentRecord Sent, AttachmentRecord Offered) Offer(Peer alice, Peer bob, string file, long ts = 100)
    {
        var (sent, payload) = alice.Service.Prepare(AliceId, BobId, file, ts);
        var offered = bob.Service.OnOffer(AliceId, ts, payload);
        Assert.NotNull(offered);
        return (sent, offered!);
    }

    private static int BlobCount(Peer p) =>
        Directory.Exists(Path.Combine(p.Service.Store.BlobDir)) ? Directory.GetFiles(p.Service.Store.BlobDir).Length : 0;

    // ------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_multiChunk_isByteIdentical_andMimeSniffedFromContent()
    {
        var (alice, bob) = Pair();
        // PNG signature + noise, deliberately misnamed: the content must win over the extension.
        var content = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Random(200_000)).ToArray();
        var file = MakeFile("photo.dat", content);

        var (sent, offered) = Offer(alice, bob, file);
        Assert.Equal("image/png", sent.Mime);
        Assert.Equal(5, offered.Chunks); // ceil(200008 / 49152), derived from the size, never sent

        await bob.Service.RequestAsync(offered);
        await WaitFor(() => offered.State == AttachmentState.Complete);

        Assert.Equal(content, File.ReadAllBytes(offered.Path!));
        Assert.Equal(5, alice.SentChunks);
        Assert.Empty(bob.Service.Store.PartIds(offered.Ref)); // parts cleaned up
    }

    [Fact]
    public async Task Resume_requestsOnlyTheMissingChunks()
    {
        var (alice, bob) = Pair();
        var content = Random(300_000); // 7 chunks
        var (_, offered) = Offer(alice, bob, MakeFile("big.bin", content));

        alice.ChunkBudget = 3; // link dies after 3 chunks
        await bob.Service.RequestAsync(offered);
        await WaitFor(() => bob.Service.Store.PartIds(offered.Ref).Count == 3);
        Assert.Equal(AttachmentState.Receiving, offered.State);
        // Alice stops at the first chunk the link refuses. The link comes back only once she has stopped: if it came back while she was still
        // serving the first request, she would send the rest of it and the resumed request would add the same chunks again.
        await WaitFor(() => Volatile.Read(ref alice.DroppedChunks) >= 1);
        await Task.Delay(100);

        // Stalled long enough -> the tick re-requests, link is back.
        alice.ChunkBudget = int.MaxValue;
        offered.LastActivityAt = DateTime.UtcNow.AddMinutes(-1);
        await bob.Service.ResumeStalledAsync();
        await WaitFor(() => offered.State == AttachmentState.Complete);

        Assert.True(alice.SentChunks == 7, "sent: " + string.Join(",", alice.SentIds)); // 3 first attempt + only the 4 missing ones
        Assert.Equal(content, File.ReadAllBytes(offered.Path!));
    }

    [Fact]
    public async Task ASenderWokenByPush_isAskedAgainAtOnce_withoutWaitingForTheStallWindow()
    {
        var (alice, bob) = Pair();
        var content = Random(150_000);
        var (_, offered) = Offer(alice, bob, MakeFile("photo.jpg", content));

        alice.ChunkBudget = 0;                       // alice is switched off: the first request gets no answer
        await bob.Service.RequestAsync(offered);
        Assert.Equal(AttachmentState.Receiving, offered.State);

        // The relay tells bob that alice is online (she was woken by a push): bob asks again straight away.
        alice.ChunkBudget = int.MaxValue;
        offered.LastActivityAt = DateTime.UtcNow.AddSeconds(-10);   // far inside the 30 s-class stall window
        await bob.Service.ResumeForPeerAsync(offered.ContactId);
        await WaitFor(() => offered.State == AttachmentState.Complete);
        Assert.Equal(content, File.ReadAllBytes(offered.Path!));

        // A download that has just made progress is left alone; so is another contact's.
        var (_, other) = Offer(alice, bob, MakeFile("other.jpg", Random(60_000)));
        alice.ChunkBudget = 0;
        await bob.Service.RequestAsync(other);
        var before = bob.SentRequests;
        await bob.Service.ResumeForPeerAsync(other.ContactId);        // activity is fresh
        await bob.Service.ResumeForPeerAsync("somebody-else");
        Assert.Equal(before, bob.SentRequests);
    }

    [Fact]
    public async Task Resume_doesNotRerequestBeforeStallWindow()
    {
        var (alice, bob) = Pair();
        var (_, offered) = Offer(alice, bob, MakeFile("f.bin", Random(100_000)));
        alice.ChunkBudget = 0;
        await bob.Service.RequestAsync(offered);
        var before = bob.SentRequests;

        await bob.Service.ResumeStalledAsync(); // activity is fresh
        Assert.Equal(before, bob.SentRequests);
    }

    [Fact]
    public async Task HolderWithDamagedCopy_refusesToServeCorruptBytes()
    {
        var (alice, bob) = Pair();
        var (sent, offered) = Offer(alice, bob, MakeFile("t.bin", Random(120_000)));

        // Same length, different bytes, after the offer (and its Merkle root) was published.
        var tampered = File.ReadAllBytes(sent.Path!);
        tampered[100] ^= 0xFF;
        File.WriteAllBytes(sent.Path!, tampered);

        await bob.Service.RequestAsync(offered);
        await WaitFor(() => alice.Notifications.Any(n => n.Contains("corrupted")));

        Assert.Equal(0, alice.SentChunks);                              // nothing bad left the sender
        Assert.Equal(AttachmentState.Receiving, offered.State);         // still waiting, not poisoned
        Assert.Empty(bob.Service.Store.PartIds(offered.Ref));
    }

    [Fact]
    public async Task LyingHolder_isCaughtChunkByChunk_thenDownloadRecoversFromAnHonestOne()
    {
        var (alice, bob) = Pair();
        var content = Random(150_000);
        var (_, offered) = Offer(alice, bob, MakeFile("liar.bin", content));

        alice.Corrupt = true; // valid proofs, damaged data
        await bob.Service.RequestAsync(offered);
        await WaitFor(() => offered.State == AttachmentState.Failed);

        Assert.Equal(AttachmentService.MaxStrikes, offered.Strikes);
        Assert.Empty(bob.Service.Store.PartIds(offered.Ref));           // no corrupt byte ever written
        Assert.Contains(bob.Notifications, n => n.Contains("Merkle proof"));

        alice.Corrupt = false; // honest again: retry works
        alice.SentChunks = 0;
        await bob.Service.RequestAsync(offered);
        await WaitFor(() => offered.State == AttachmentState.Complete);
        Assert.Equal(content, File.ReadAllBytes(offered.Path!));
    }

    [Fact]
    public async Task AnyHolder_canServe_whenAuthorized_swarmStyle()
    {
        // Bob downloaded the file from Alice; Carol (a group member) fetches it from BOB while Alice is away.
        var alice = NewPeer(AliceId);
        var bob = NewPeer(BobId, authorize: (rec, from) => rec.State == AttachmentState.Complete && from == CarolId);
        var carol = NewPeer(CarolId);
        alice.Other = bob; bob.Other = alice;

        var content = Random(130_000);
        var (_, payload) = alice.Service.Prepare(AliceId, BobId, MakeFile("g.bin", content), 50);
        var atBob = bob.Service.OnOffer(AliceId, 50, payload)!;
        await bob.Service.RequestAsync(atBob);
        await WaitFor(() => atBob.State == AttachmentState.Complete);

        alice.Other = null;                       // Alice offline from now on
        bob.Other = carol; carol.Other = bob;
        var atCarol = carol.Service.OnOffer(AliceId, 50, payload)!;   // same offer, same ref
        await carol.Service.RequestAsync(atCarol);
        await WaitFor(() => atCarol.State == AttachmentState.Complete);

        Assert.Equal(content, File.ReadAllBytes(atCarol.Path!));
        Assert.True(bob.SentChunks >= 3);
    }

    [Fact]
    public async Task Request_fromAnotherContact_isNotServed()
    {
        var (alice, bob) = Pair();
        var (sent, _) = Offer(alice, bob, MakeFile("p.bin", Random(50_000)));

        alice.Service.OnRequest(CarolId, JsonSerializer.Serialize(new AttachmentRequest { Ref = sent.Ref }));
        await Task.Delay(300);
        Assert.Equal(0, alice.SentChunks);
    }

    [Fact]
    public async Task Request_withOutOfRangeIds_isClamped()
    {
        var (alice, bob) = Pair();
        var (sent, offered) = Offer(alice, bob, MakeFile("o.bin", Random(100_000))); // 3 chunks
        offered.State = AttachmentState.Receiving;

        alice.Service.OnRequest(BobId, JsonSerializer.Serialize(new AttachmentRequest { Ref = sent.Ref, Missing = [-1, 1, 1, 99, int.MaxValue] }));
        await WaitFor(() => alice.SentChunks >= 1);
        await Task.Delay(200);
        Assert.Equal(1, alice.SentChunks); // only chunk 1, once
    }

    // ---- content-addressed storage / forwarding ------------------------

    [Fact]
    public async Task Forward_reusesTheSameBlob_noCopyNoRehash()
    {
        var (alice, bob) = Pair();
        var (sent, offered) = Offer(alice, bob, MakeFile("fw.bin", Random(90_000)));
        await bob.Service.RequestAsync(offered);
        await WaitFor(() => offered.State == AttachmentState.Complete);
        var blobsBefore = BlobCount(bob);

        var (fwd, payload) = bob.Service.Forward(BobId, CarolId, offered, ts: 200);

        Assert.Equal(offered.Root, fwd.Root);
        Assert.Equal(offered.Path, fwd.Path);                    // same file on disk
        Assert.Equal(blobsBefore, BlobCount(bob));               // nothing duplicated
        Assert.Equal(AttachmentDirection.Sent, fwd.Direction);
        Assert.Equal(CarolId, fwd.ContactId);
        Assert.Equal($"{BobId}:200", fwd.Ref);
        Assert.Equal(offered.Root, JsonSerializer.Deserialize<AttachmentOffer>(payload)!.Root);
    }

    [Fact]
    public void Forward_ofAFileNotDownloadedYet_isRefused()
    {
        var (alice, bob) = Pair();
        var (_, offered) = Offer(alice, bob, MakeFile("nd.bin", Random(10_000)));
        Assert.ThrowsAny<InvalidOperationException>(() => bob.Service.Forward(BobId, CarolId, offered, 1));
    }

    [Fact]
    public void SameFileSentToTwoConversations_isStoredOnce()
    {
        var (alice, _) = Pair();
        var file = MakeFile("twice.bin", Random(70_000));

        var (a, _) = alice.Service.Prepare(AliceId, BobId, file, 1);
        var (b, _) = alice.Service.Prepare(AliceId, CarolId, file, 2);

        Assert.Equal(a.Root, b.Root);
        Assert.Equal(a.Path, b.Path);
        Assert.Equal(1, BlobCount(alice));
    }

    [Fact]
    public async Task OfferForContentWeAlreadyHold_isCompleteImmediately_andDownloadIsSkipped()
    {
        var (alice, bob) = Pair();
        var file = MakeFile("dup.bin", Random(90_000));
        var (_, first) = Offer(alice, bob, file, ts: 10);
        await bob.Service.RequestAsync(first);
        await WaitFor(() => first.State == AttachmentState.Complete);
        var sentBefore = alice.SentChunks;

        // Same content arrives again (another conversation / a forward): nothing to fetch.
        var (_, payload2) = alice.Service.Prepare(AliceId, BobId, file, 11);
        var second = bob.Service.OnOffer(AliceId, 11, payload2)!;

        Assert.Equal(AttachmentState.Complete, second.State);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(sentBefore, alice.SentChunks);
        Assert.Equal(1, BlobCount(bob));
    }

    [Fact]
    public async Task DownloadOfferedWhoseContentArrivedMeanwhile_sendsNoRequest()
    {
        var (alice, bob) = Pair();
        var file = MakeFile("late.bin", Random(90_000));
        var (_, first) = Offer(alice, bob, file, ts: 10);
        var (_, payload2) = alice.Service.Prepare(AliceId, BobId, file, 11);
        var second = bob.Service.OnOffer(AliceId, 11, payload2)!; // offered while first still not downloaded
        Assert.Equal(AttachmentState.Offered, second.State);

        await bob.Service.RequestAsync(first);
        await WaitFor(() => first.State == AttachmentState.Complete);
        var requestsBefore = bob.SentRequests;

        var msg = await bob.Service.RequestAsync(second);
        Assert.Equal(requestsBefore, bob.SentRequests);
        Assert.Equal(AttachmentState.Complete, second.State);
        Assert.Contains("same content", msg);
    }

    // ---- validation -----------------------------------------------------

    [Theory]
    [InlineData(0, 64, 'a')]            // empty
    [InlineData(-5, 64, 'a')]           // negative size
    [InlineData(3_000_000_000, 64, 'a')]   // over the 2 GiB limit
    [InlineData(100_000, 10, 'a')]      // root not 64 chars
    [InlineData(100_000, 64, 'z')]      // root not hex
    public void MalformedOffer_isRejected(long size, int rootLength, char rootChar)
    {
        var (_, bob) = Pair();
        var payload = JsonSerializer.Serialize(new AttachmentOffer { Name = "x.bin", Mime = "a/b", Size = size, Root = new string(rootChar, rootLength) });

        Assert.Null(bob.Service.OnOffer(AliceId, 1, payload));
        Assert.Empty(bob.Service.Store.All);
    }

    [Fact]
    public void GarbageOffer_isRejected()
    {
        var (_, bob) = Pair();
        Assert.Null(bob.Service.OnOffer(AliceId, 1, "not json"));
    }

    [Fact]
    public void DuplicateOffer_keepsExistingProgress()
    {
        var (alice, bob) = Pair();
        var (_, offered) = Offer(alice, bob, MakeFile("d.bin", Random(10_000)), ts: 7);
        offered.State = AttachmentState.Receiving;

        var (_, payload) = alice.Service.Prepare(AliceId, BobId, MakeFile("d2.bin", Random(10_000)), 7);
        var again = bob.Service.OnOffer(AliceId, 7, payload);

        Assert.Same(offered, again);
        Assert.Equal(AttachmentState.Receiving, again!.State);
        Assert.Single(bob.Service.Store.All);
    }

    [Fact]
    public void Chunk_rules_areEnforced()
    {
        var (alice, bob) = Pair();
        var content = Random(100_000); // 3 chunks, last = 1696 bytes
        var (sent, offered) = Offer(alice, bob, MakeFile("c.bin", content));
        var leaves = MerkleTree.LeavesOf(new MemoryStream(content), AttachmentService.ChunkSize);

        string Valid(int id)
        {
            var data = content.Skip(id * AttachmentService.ChunkSize).Take(AttachmentService.ChunkSize).ToArray();
            return JsonSerializer.Serialize(new AttachmentChunk
            {
                Ref = offered.Ref, ChunkId = id, TotalChunks = 3, Data = Convert.ToBase64String(data),
                Proof = MerkleTree.Proof(leaves, id).Select(Convert.ToBase64String).ToList(),
            });
        }
        string With(Action<AttachmentChunk> edit, int id = 0)
        {
            var c = JsonSerializer.Deserialize<AttachmentChunk>(Valid(id))!;
            edit(c);
            return JsonSerializer.Serialize(c);
        }

        // Not requested yet (state Offered): ignored even if perfectly valid.
        bob.Service.OnChunk(AliceId, Valid(0));
        Assert.Empty(bob.Service.Store.PartIds(offered.Ref));

        offered.State = AttachmentState.Receiving;
        bob.Service.OnChunk(AliceId, With(c => c.Ref = "nobody:1"));                    // unknown ref
        bob.Service.OnChunk(AliceId, With(c => c.ChunkId = 3));                         // id out of range
        bob.Service.OnChunk(AliceId, With(c => c.ChunkId = -1));                        // negative id
        bob.Service.OnChunk(AliceId, With(c => c.TotalChunks = 4));                     // wrong total
        bob.Service.OnChunk(AliceId, With(c => c.Data = Convert.ToBase64String(new byte[10])));  // wrong length
        bob.Service.OnChunk(AliceId, With(c => c.Data = "!!!not-base64!!!"));
        bob.Service.OnChunk(AliceId, With(c => c.Proof = ["!!!"]));                     // bad proof encoding
        bob.Service.OnChunk(AliceId, "{bad json");
        Assert.Empty(bob.Service.Store.PartIds(offered.Ref));
        Assert.Equal(0, offered.Strikes); // malformed != corrupt: only a proof failure earns a strike

        bob.Service.OnChunk(AliceId, With(c => c.Proof = []));                          // right size, no proof -> corrupt
        Assert.Equal(1, offered.Strikes);
        Assert.Empty(bob.Service.Store.PartIds(offered.Ref));

        // A chunk with the proof of ANOTHER index fails as well (no swapping chunks around).
        bob.Service.OnChunk(AliceId, With(c => c.Proof = MerkleTree.Proof(leaves, 1).Select(Convert.ToBase64String).ToList()));
        Assert.Equal(2, offered.Strikes);

        bob.Service.OnChunk(AliceId, Valid(0)); // finally a valid one — from any sender: the proof, not the sender, is what counts
        Assert.Single(bob.Service.Store.PartIds(offered.Ref));
    }

    [Theory]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a/b/c.png", "c.png")]
    [InlineData("", "file")]
    [InlineData("   ", "file")]
    [InlineData("we:ird?.png", "we_ird_.png")]
    public void FileName_isSanitized(string input, string expected) =>
        Assert.Equal(expected, AttachmentStore.SanitizeFileName(input));

    [Fact]
    public void Prepare_rejectsEmptyMissingAndOversizedFiles()
    {
        var (alice, _) = Pair();
        Assert.ThrowsAny<InvalidOperationException>(() => alice.Service.Prepare(AliceId, BobId, MakeFile("e.bin", []), 1));
        Assert.Throws<FileNotFoundException>(() => alice.Service.Prepare(AliceId, BobId, Path.Combine(_root, "nope.bin"), 1));

        var big = Path.Combine(_root, "huge.bin");
        Directory.CreateDirectory(_root);
        using (var fs = File.Create(big)) fs.SetLength(AttachmentService.MaxSize + 1);
        Assert.ThrowsAny<InvalidOperationException>(() => alice.Service.Prepare(AliceId, BobId, big, 1));
    }

    [Fact]
    public void Prepare_copiesTheFile_soLaterDeletionOfTheSourceDoesNotBreakServing()
    {
        var (alice, _) = Pair();
        var src = MakeFile("gone.bin", Random(5_000));
        var (rec, _) = alice.Service.Prepare(AliceId, BobId, src, 1);
        File.Delete(src);
        Assert.True(File.Exists(rec.Path));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, "x.bin", "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0 }, "x.bin", "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "x.bin", "image/webp")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0, 0, 0, 0, 0, 0 }, "x.bin", "application/pdf")]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, "x.png", "image/png")]      // unknown content -> extension
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, "x.unknown", "application/octet-stream")]
    public void DetectMime_prefersContent_thenExtension(byte[] head, string name, string expected) =>
        Assert.Equal(expected, AttachmentService.DetectMime(MakeFile(Guid.NewGuid().ToString("N") + "_" + name, head), name));

    [Fact]
    public async Task RemoveContent_deletesTheBlob_stopsServing_andKeepsAFileStillSharedByAnotherOffer()
    {
        var (alice, bob) = Pair();
        var file = MakeFile("removable.bin", Random(1000));
        var (_, offered) = Offer(alice, bob, file);
        await bob.Service.RequestAsync(offered);
        await WaitFor(() => offered.State == AttachmentState.Complete);
        var blob = offered.Path!;
        Assert.True(File.Exists(blob));

        // The same content offered again in another conversation: same blob, nothing to download.
        var (_, payload2) = alice.Service.Prepare(AliceId, BobId, file, 200);
        var second = bob.Service.OnOffer(AliceId, 200, payload2)!;
        Assert.Equal(AttachmentState.Complete, second.State);
        Assert.Equal(blob, second.Path);

        Assert.True(bob.Service.RemoveContent(offered.Ref));
        Assert.Equal(AttachmentState.Removed, offered.State);
        Assert.Null(offered.Path);
        Assert.True(File.Exists(blob));                                   // the other offer still needs it

        Assert.True(bob.Service.RemoveContent(second.Ref));
        Assert.False(File.Exists(blob));                                  // last reference gone

        Assert.Equal("This file was deleted.", await bob.Service.RequestAsync(offered));   // and it is not fetched again
        Assert.False(bob.Service.RemoveContent("nobody:1"));
    }
    [Fact]
    public async Task ReceivedMimeMismatch_isCorrectedAndReported()
    {
        var (alice, bob) = Pair();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(Random(2_000)).ToArray();
        var (_, payload) = alice.Service.Prepare(AliceId, BobId, MakeFile("m.bin", jpeg), 1);

        // A lying sender declares png for jpeg content.
        var offer = JsonSerializer.Deserialize<AttachmentOffer>(payload)!;
        offer.Mime = "image/png";
        var offered = bob.Service.OnOffer(AliceId, 1, JsonSerializer.Serialize(offer))!;

        await bob.Service.RequestAsync(offered);
        await WaitFor(() => offered.State == AttachmentState.Complete);

        Assert.Equal("image/jpeg", offered.Mime);
        Assert.Contains(bob.Notifications, n => n.Contains("declared image/png"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* temp dir, best effort */ }
    }
}
