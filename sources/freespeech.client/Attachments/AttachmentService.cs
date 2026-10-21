/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Text.Json;

namespace FreeSpeech.Client.Attachments;

/// <summary>
/// Attachments on demand (protocol §6): the sender pushes only a small
/// "attachment" offer; the receiver later asks for the file
/// ("attachment_request") and a holder streams it as independently
/// encrypted+signed "attachment_chunk" Letters, each with a Merkle proof so it
/// is verified on arrival whoever served it. Nothing is stored by the relay.
///
/// The Merkle root is the content identity: the same file offered in another
/// conversation (or already downloaded from someone else) is stored once and
/// never re-downloaded or re-hashed (<see cref="Forward"/>, <see cref="OnOffer"/>).
///
/// Transport is injected (<c>sendOnce</c>: to, type, payload → delivered), as
/// is the serving policy (<c>authorize</c>: may this peer fetch this record from
/// us?), so this class models the §6 state machine without touching crypto or
/// sockets, and the group layer (§7) can plug a "requester is a member" rule.
/// Resume = the receiver re-requests only the chunk ids it does not yet have.
/// </summary>
public class AttachmentService
{
    public const int ChunkSize = 48 * 1024;
    /// <summary>The largest file the protocol carries (2 GiB). It bounds the chunk count and what a peer can make this device store; the practical limit of one file is the user's own setting.</summary>
    public const long MaxSize = 2L * 1024 * 1024 * 1024;
    public const int MaxStrikes = 3;
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(20);

    private readonly AttachmentStore _store;
    private readonly Func<string, string, string, Task<bool>> _sendOnce;
    private readonly Action<string> _notify;
    private readonly Func<AttachmentRecord, string, bool> _authorize;
    private readonly Dictionary<string, Queue<int>> _serving = new();
    private readonly object _lock = new();

    /// <summary>True while this client is streaming a file to someone (the mobile app stays alive for it after a push wake-up).</summary>
    public bool IsServing { get { lock (_lock) return _serving.Count > 0; } }

    /// <summary>Default policy (1-to-1): only a file WE sent to that contact is served to them.</summary>
    public static readonly Func<AttachmentRecord, string, bool> ContactOnly =
        (rec, from) => rec.Direction == AttachmentDirection.Sent && rec.ContactId == from;

    private readonly TimeSpan _chunkDelay;

    /// <param name="chunkDelay">Pause after each served chunk. Zero in production; tests and the console (<c>FREESPEECH_CHUNK_DELAY_MS</c>) use it to slow a local transfer down enough to cut the link mid-file.</param>
    private readonly Func<AttachmentRecord, IReadOnlyList<string>>? _holders;
    private readonly Func<AttachmentRecord, string, bool>? _isGroupPeer;
    private readonly Func<long, bool> _canTransfer;

    // Group swarm (§7): chunks are handed out in small windows, so a fast holder is never left idle behind a slow one.
    private const int InitialWindow = 8, MinWindow = 2, MaxWindow = 64;
    private const double WindowSeconds = 3;   // a holder is given about this much data at a time, from its observed speed

    /// <summary>A holder that has delivered nothing for this long loses what it was given (it goes to the others) and is left alone for <see cref="BackoffFor"/>.</summary>
    public TimeSpan PeerSilence { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>When every missing chunk is already requested, an idle holder asks again for those requested longest ago from another holder (the first answer wins).</summary>
    public TimeSpan EndgameAfter { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How often a running group download checks its holders (silence, idle holders), between the 30 s background ticks.</summary>
    public TimeSpan SwarmCheckEvery { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a holder that said it holds nothing (or cannot serve now), or went silent, is left alone.</summary>
    public TimeSpan BackoffFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A running group download stops checking its holders every few seconds after this long without a chunk (the 30 s background tick continues).</summary>
    public TimeSpan WatchGiveUp { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A partial holder's announced chunks are a lower bound: after this long it is asked again for the others.</summary>
    public TimeSpan HaveRefresh { get; set; } = TimeSpan.FromSeconds(8);


    /// <param name="holders">Who to ask for a received file, in preference order (author first). Default: the contact who offered it. In a group it lists the members, and the missing chunks are spread over the ones that answer.</param>
    /// <param name="isGroupPeer">Is this peer a fellow member of the file's group? Only such a peer is told what we hold of that file — anybody else gets silence.</param>
    /// <param name="canTransfer">May chunks of a file of this size travel on the current network (<see cref="TransferPolicy"/>)? Default: always.</param>
    public AttachmentService(AttachmentStore store, Func<string, string, string, Task<bool>> sendOnce, Action<string> notify,
        Func<AttachmentRecord, string, bool>? authorize = null, TimeSpan chunkDelay = default,
        Func<AttachmentRecord, IReadOnlyList<string>>? holders = null, Func<AttachmentRecord, string, bool>? isGroupPeer = null,
        Func<long, bool>? canTransfer = null)
    {
        _store = store;
        _sendOnce = sendOnce;
        _notify = notify;
        _authorize = authorize ?? ContactOnly;
        _chunkDelay = chunkDelay;
        _holders = holders;
        _isGroupPeer = isGroupPeer;
        _canTransfer = canTransfer ?? (_ => true);
    }

    public AttachmentStore Store => _store;

    /// <summary>Makes the small picture of an image file (the platform decodes it); null = no thumbnails are made, the full file is shown.</summary>
    public IPictureThumbnailer? Pictures { get; set; }

    /// <summary>
    /// The small picture of a downloaded image: the one kept in the database, or made now from the file and kept. Null for anything that is not a
    /// still image, when the file is not here, or when there is no <see cref="Pictures"/> maker. An animated GIF keeps its full file.
    /// </summary>
    public PictureThumbnail? ThumbnailOf(AttachmentRecord rec)
    {
        var video = rec.Mime.StartsWith("video/", StringComparison.Ordinal);
        if (!video && (!rec.Mime.StartsWith("image/", StringComparison.Ordinal) || rec.Mime == "image/gif")) return null;
        if (_store.Thumbnails.Get(rec.Root) is { } kept) return kept;
        if (Pictures == null || rec is not { State: AttachmentState.Complete, Path: { } path } || !File.Exists(path)) return null;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var made = video ? Pictures.MakeVideo(path) : Pictures.Make(path);
        if (made != null)
        {
            _store.Thumbnails.Put(rec.Root, made);
            ClientLog.Debug("thumbs", $"made {made.Width}x{made.Height}, {made.Data.Length} bytes, in {clock.ElapsedMilliseconds} ms");
        }
        return made;
    }

    /// <summary>
    /// Makes, in the background and newest first, the thumbnail of every downloaded image that has none (pictures received before thumbnails
    /// were kept): the chat then finds them ready instead of decoding them as the person scrolls. Pauses a moment between files.
    /// </summary>
    public async Task BackfillThumbnailsAsync()
    {
        if (Pictures == null) return;
        var todo = _store.All.Where(r => (r.State == AttachmentState.Complete && (r.Mime.StartsWith("video/", StringComparison.Ordinal) || r.Mime.StartsWith("image/", StringComparison.Ordinal) && r.Mime != "image/gif")))
            .Reverse().DistinctBy(r => r.Root).Where(r => !_store.Thumbnails.Has(r.Root)).ToList();
        foreach (var rec in todo)
        {
            try { await Task.Run(() => ThumbnailOf(rec)); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* next time */ }
            await Task.Delay(15);
        }
        var (count, bytes) = _store.Thumbnails.Usage();
        if (todo.Count > 0) ClientLog.Info("thumbs", $"{todo.Count} thumbnail(s) made in the background; {count} kept, {bytes / 1024} KB");
    }

    /// <summary>Makes the thumbnail of a file that has just become available, off the caller's thread: the chat finds it ready.</summary>
    private void Warm(AttachmentRecord rec)
    {
        if (Pictures == null) return;
        _ = Task.Run(() =>
        {
            try { ThumbnailOf(rec); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* the chat makes it when it needs it */ }
        });
    }

    // ------------------------------------------------------------------
    // Sender side
    // ------------------------------------------------------------------

    /// <summary>
    /// Hashes the file (Merkle root), stores it once under its root (a file already
    /// stored — same content offered elsewhere — is not copied again) and builds the
    /// "attachment" payload. <paramref name="ts"/> makes the ref "{senderId}:{ts}".
    /// </summary>
    public (AttachmentRecord Record, string Payload) Prepare(string senderId, string contactId, string sourcePath, long ts, string? groupId = null)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("File not found.", sourcePath);
        if (info.Length == 0) throw new CodedInvalidOperationException("File is empty.", "file_empty");
        if (info.Length > MaxSize) throw new CodedInvalidOperationException($"File too large ({info.Length} bytes, max {MaxSize}).", "file_too_big", info.Length / (1024 * 1024), MaxSize / (1024 * 1024));

        List<byte[]> leaves;
        using (var fs = File.OpenRead(sourcePath))
            leaves = MerkleTree.LeavesOf(fs, ChunkSize);
        var root = Convert.ToHexString(MerkleTree.ContentId(leaves, info.Length)).ToLowerInvariant();

        var storedPath = _store.FindBlob(root);
        if (storedPath == null)
        {
            storedPath = _store.BlobPath(root, info.Name);
            File.Copy(sourcePath, storedPath, overwrite: true);
        }
        _store.SaveLeaves(root, leaves);

        return NewSentRecord(senderId, contactId, ts, AttachmentStore.SanitizeFileName(info.Name), DetectMime(storedPath, info.Name), info.Length, root, storedPath, groupId);
    }

    /// <summary>
    /// Offers a file we already hold (sent or received) to another conversation:
    /// same root, same blob on disk — nothing is copied, hashed or re-downloaded.
    /// </summary>
    public (AttachmentRecord Record, string Payload) Forward(string senderId, string toContactId, AttachmentRecord source, long ts, string? groupId = null)
    {
        if (source.State != AttachmentState.Complete || source.Path == null || !File.Exists(source.Path))
            throw new CodedInvalidOperationException("That file is not available locally (not downloaded yet).", "file_not_local");
        return NewSentRecord(senderId, toContactId, ts, source.Name, source.Mime, source.Size, source.Root, source.Path, groupId);
    }

    private (AttachmentRecord, string) NewSentRecord(string senderId, string contactId, long ts, string name, string mime, long size, string root, string path, string? groupId = null)
    {
        var offer = new AttachmentOffer { Name = name, Mime = mime, Size = size, Root = root };
        var record = _store.Add(new AttachmentRecord
        {
            Ref = $"{senderId}:{ts}", ContactId = contactId, Direction = AttachmentDirection.Sent,
            Name = name, Mime = mime, Size = size, Root = root, Chunks = MerkleTree.LeafCount(size, ChunkSize),
            State = AttachmentState.Complete, Path = path, GroupId = groupId,
        });
        Warm(record);
        return (record, JsonSerializer.Serialize(offer));
    }

    /// <summary>
    /// Handles an incoming request: streams the requested chunks in the background. A second request for the same file from the same peer while one is running
    /// is ignored. A fellow member that holds only PART of a group file (it is still downloading it) serves the chunks it holds — each with the proof it stored
    /// when it verified the chunk — and tells the requester what it holds (<c>attachment_have</c>) so the others are asked for the rest.
    /// </summary>
    public void OnRequest(string from, string payload)
    {
        AttachmentRequest? req;
        try { req = JsonSerializer.Deserialize<AttachmentRequest>(payload); }
        catch (JsonException) { return; }
        if (req == null) return;

        var rec = _store.Find(req.Ref);
        if (rec == null) return;

        var groupPeer = rec.GroupId != null && _isGroupPeer?.Invoke(rec, from) == true;
        var complete = rec.State == AttachmentState.Complete && rec.Path != null && File.Exists(rec.Path);
        var partial = !complete && rec.GroupId != null && rec.Direction == AttachmentDirection.Received && rec.State == AttachmentState.Receiving;

        // Nothing to give: a fellow member is told so (it re-spreads my share at once); anybody else gets silence.
        if (!complete && !partial)
        {
            if (groupPeer) SendHave(from, rec, [], complete: false, busy: false);
            return;
        }
        if (!_authorize(rec, from)) return;

        // The transfer setting of this device: no chunk leaves it on a network it forbids.
        if (!_canTransfer(rec.Size))
        {
            if (groupPeer) SendHave(from, rec, [], complete: false, busy: true);
            return;
        }

        var ids = (req.Missing ?? Enumerable.Range(0, rec.Chunks).ToList()).Where(i => i >= 0 && i < rec.Chunks).Distinct().ToList();
        if (ids.Count == 0) return;

        if (partial)
        {
            var held = _store.PartIds(rec.Ref);
            if (ids.Any(i => !held.Contains(i))) SendHave(from, rec, held, complete: false, busy: false);
            ids = ids.Where(held.Contains).ToList();
            if (ids.Count == 0) return;
        }

        var key = $"{from}|{rec.Ref}";
        lock (_lock)
        {
            if (_serving.TryGetValue(key, out var running))
            {
                // Already streaming to this peer: hand the new ids to the running job (a duplicate is harmless, the receiver drops it).
                foreach (var id in ids) if (!running.Contains(id)) running.Enqueue(id);
                return;
            }
            _serving[key] = new Queue<int>(ids);
        }

        _ = Task.Run(async () =>
        {
            try { await ServeAsync(from, rec, key, ids.Count); }
            catch (Exception ex) { _notify($"[!] Serving '{rec.Name}' failed: {ex.Message}"); }
            finally { lock (_lock) _serving.Remove(key); }
        });
    }

    private void SendHave(string to, AttachmentRecord rec, IReadOnlyCollection<int> held, bool complete, bool busy) =>
        _ = _sendOnce(to, "attachment_have", JsonSerializer.Serialize(new AttachmentHave
        {
            Ref = rec.Ref, Have = complete || busy ? "" : ChunkBitmap.Encode(held, rec.Chunks), Complete = complete, Busy = busy,
        }));

    /// <summary>Next chunk to serve for this job; when none is left the job is removed in the same critical section, so a request arriving right now is never lost.</summary>
    private bool TryNext(string key, out int id)
    {
        lock (_lock)
        {
            if (_serving.TryGetValue(key, out var q) && q.Count > 0) { id = q.Dequeue(); return true; }
            _serving.Remove(key);
            id = -1;
            return false;
        }
    }

    private enum ReadResult { Ok, Skip, Abort }

    /// <summary>
    /// One chunk to send with its Merkle proof: cut from the finished file (proof from the cached tree) or, while the file is still being downloaded, read back from the
    /// stored part with the proof kept beside it. The bytes are checked before they leave: a damaged local copy is never handed out.
    /// </summary>
    private ReadResult TryReadChunk(AttachmentRecord rec, int id, ref FileStream? file, ref List<byte[]>? leaves, byte[] buffer, out byte[] data, out List<string> proof)
    {
        data = [];
        proof = [];
        if (rec.State == AttachmentState.Complete && rec.Path != null && File.Exists(rec.Path))
        {
            if (file == null)
            {
                leaves = _store.LoadLeaves(rec.Root);
                if (leaves == null || leaves.Count != rec.Chunks)
                {
                    // Tree not cached (e.g. blob restored from a backup): rebuild once, and remember it.
                    using var fs = File.OpenRead(rec.Path);
                    leaves = MerkleTree.LeavesOf(fs, ChunkSize);
                    _store.SaveLeaves(rec.Root, leaves);
                }
                file = new FileStream(rec.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            file.Seek((long)id * ChunkSize, SeekOrigin.Begin);
            var read = file.Read(buffer, 0, ChunkSize);
            if (!MerkleTree.Leaf(buffer.AsSpan(0, read)).AsSpan().SequenceEqual(leaves![id]))
            {
                _notify($"[!] '{rec.Name}': local copy is corrupted at chunk {id} — not served.");
                return ReadResult.Abort;
            }
            data = buffer.AsSpan(0, read).ToArray();
            proof = MerkleTree.Proof(leaves, id).Select(Convert.ToBase64String).ToList();
            return ReadResult.Ok;
        }

        var part = _store.ReadPart(rec.Ref, id);
        var stored = _store.ReadProof(rec.Ref, id);
        if (part == null || stored == null) return ReadResult.Skip;   // not held (any more), or stored without its proof
        if (!MerkleTree.Verify(Convert.FromHexString(rec.Root), rec.Size, ChunkSize, id, MerkleTree.Leaf(part), stored)) return ReadResult.Skip;
        data = part;
        proof = stored.Select(Convert.ToBase64String).ToList();
        return ReadResult.Ok;
    }

    private async Task ServeAsync(string to, AttachmentRecord rec, string key, int initialCount)
    {
        _notify($"[sending '{rec.Name}' to {to[..Math.Min(8, to.Length)]}… — {initialCount} chunk(s)]");
        FileStream? file = null;
        List<byte[]>? leaves = null;
        var buffer = new byte[ChunkSize];
        try
        {
            while (TryNext(key, out var id))
            {
                if (!_canTransfer(rec.Size))
                {
                    _notify($"[sending '{rec.Name}' paused: the transfer setting does not allow this network]");
                    return;
                }

                var result = TryReadChunk(rec, id, ref file, ref leaves, buffer, out var data, out var proof);
                if (result == ReadResult.Abort) return;
                if (result == ReadResult.Skip) continue;

                var chunk = new AttachmentChunk { Ref = rec.Ref, ChunkId = id, TotalChunks = rec.Chunks, Data = Convert.ToBase64String(data), Proof = proof };

                // Stop at the first undelivered chunk: the receiver's silence timer re-requests exactly what is missing.
                if (!await _sendOnce(to, "attachment_chunk", JsonSerializer.Serialize(chunk)))
                {
                    _notify($"[!] '{rec.Name}': receiver unreachable at chunk {id}/{rec.Chunks} — they will resume.");
                    return;
                }

                if (_chunkDelay > TimeSpan.Zero) await Task.Delay(_chunkDelay);
            }
        }
        finally { if (file != null) await file.DisposeAsync(); }
    }

    // ------------------------------------------------------------------
    // Receiver side
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates an incoming offer and records it. Returns null (and notifies) if it
    /// is malformed or over our limits. If we already hold a file with the same
    /// root (any conversation), the record is Complete straight away: nothing to download.
    /// </summary>
    public AttachmentRecord? OnOffer(string from, long ts, string payload, string? groupId = null)
    {
        AttachmentOffer? offer;
        try { offer = JsonSerializer.Deserialize<AttachmentOffer>(payload); }
        catch (JsonException) { offer = null; }

        if (offer == null || offer.Size <= 0 || offer.Size > MaxSize || offer.Root.Length != 64
            || !offer.Root.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(offer.Name))
        {
            _notify($"[!] Malformed or oversized attachment offer from {from} — ignored.");
            return null;
        }

        var root = offer.Root.ToLowerInvariant();
        var rec = new AttachmentRecord
        {
            Ref = $"{from}:{ts}", ContactId = from, Direction = AttachmentDirection.Received,
            Name = AttachmentStore.SanitizeFileName(offer.Name), Mime = offer.Mime, Size = offer.Size,
            Root = root, Chunks = MerkleTree.LeafCount(offer.Size, ChunkSize), State = AttachmentState.Offered, GroupId = groupId,
        };

        var already = _store.FindCompleteByRoot(root);
        if (already != null && already.Size == offer.Size)
        {
            rec.State = AttachmentState.Complete;
            rec.Path = already.Path;
        }
        return _store.Add(rec);
    }

    /// <summary>
    /// Deletes the file from this device (chosen here, or the author removed it for everyone): the parts and the blob
    /// go, unless another conversation holds the same content (blobs are shared by Merkle root); the record stays as
    /// <see cref="AttachmentState.Removed"/> so the file is neither served nor downloaded again. Returns false if unknown.
    /// </summary>
    public bool RemoveContent(string reference)
    {
        var rec = _store.Find(reference);
        if (rec == null) return false;

        var path = rec.Path;
        _store.DeleteParts(rec.Ref);
        lock (_lock) _swarms.Remove(rec.Ref);
        rec.State = AttachmentState.Removed;
        rec.Path = null;
        _store.Save();

        if (path != null && !_store.All.Any(r => r.State == AttachmentState.Complete && r.Path == path))
        {
            try { File.Delete(path); }
            catch (IOException) { /* in use: left for the next cleanup, the record is already gone */ }
        }

        // The picture goes with the file the person chose to delete, unless another conversation still holds the same content.
        if (!_store.All.Any(r => r.Root == rec.Root && r.State == AttachmentState.Complete)) _store.Thumbnails.Delete(rec.Root);
        return true;
    }

    /// <summary>
    /// Checks that the drive can take this download: the chunks still missing, then the whole file beside them while it is assembled.
    /// Throws <c>not_enough_space</c> (needed and free, in MB) otherwise. Nothing is refused when the free space is unknown, or when the
    /// content is already here.
    /// </summary>
    public void EnsureRoom(AttachmentRecord rec)
    {
        if (rec.State is AttachmentState.Complete or AttachmentState.Removed || _store.FindCompleteByRoot(rec.Root) != null) return;
        if (_store.FreeSpace() is not { } free) return;

        var held = (long)_store.PartIds(rec.Ref).Count * ChunkSize;
        var needed = Math.Max(0, rec.Size - held) + rec.Size;
        const long margin = 50L * 1024 * 1024;   // the phone needs some room of its own too
        if (free < needed + margin)
            throw new CodedInvalidOperationException($"Not enough free space ({needed} bytes needed, {free} free).", "not_enough_space", (needed + margin) / (1024 * 1024) + 1, free / (1024 * 1024));
    }

    /// <summary>User asked to download: (re)requests whatever chunks are still missing. A group file is fetched from every member that can serve it (see <see cref="PlanSwarm"/>).</summary>
    public async Task<string> RequestAsync(AttachmentRecord rec)
    {
        if (rec.State == AttachmentState.Removed) return "This file was deleted.";
        if (rec.Direction != AttachmentDirection.Received) return "This is a file you sent.";
        if (rec.State == AttachmentState.Complete) return $"Already available: {rec.Path}";

        // Same content already stored under another offer? Nothing to fetch.
        var already = _store.FindCompleteByRoot(rec.Root);
        if (already != null && already.Size == rec.Size)
        {
            rec.State = AttachmentState.Complete;
            rec.Path = already.Path;
            rec.WaitingNetwork = false;
            _store.Save();
            return $"Already available (same content): {rec.Path}";
        }

        // The transfer setting: on a network it forbids, the download waits (the user can say "download anyway"); it restarts by itself on Wi-Fi.
        if (!rec.NetworkOverride && !_canTransfer(rec.Size))
        {
            if (rec.State != AttachmentState.Receiving) { rec.Strikes = 0; rec.HolderStrikes.Clear(); rec.BannedHolders.Clear(); }
            rec.State = AttachmentState.Receiving;
            rec.WaitingNetwork = true;
            rec.LastActivityAt = DateTime.UtcNow;
            _store.Save();
            return "Waiting for Wi-Fi — the transfer setting does not allow mobile data for this file.";
        }

        var candidates = (_holders?.Invoke(rec) ?? [rec.ContactId]).Distinct().ToList();
        var trusted = candidates.Where(h => !rec.BannedHolders.Contains(h)).ToList();
        if (trusted.Count == 0)
        {
            rec.State = AttachmentState.Failed;
            _store.Save();
            return "No trustworthy holder left for this file.";
        }

        if (rec.State != AttachmentState.Receiving) { rec.Strikes = 0; rec.HolderStrikes.Clear(); rec.BannedHolders.Clear(); }
        rec.State = AttachmentState.Receiving;
        rec.WaitingNetwork = false;
        rec.LastActivityAt = DateTime.UtcNow;
        _store.Save();

        if (rec.GroupId != null) return await RequestSwarmAsync(rec, trusted);

        // 1-to-1: one holder, who is asked for everything (what is still missing after an interruption).
        var have = _store.PartIds(rec.Ref);
        var missing = Enumerable.Range(0, rec.Chunks).Where(i => !have.Contains(i)).ToList();
        if (missing.Count == 0) return "Nothing left to request.";
        var req = new AttachmentRequest { Ref = rec.Ref, Missing = have.Count == 0 ? null : missing };
        return await _sendOnce(trusted[0], "attachment_request", JsonSerializer.Serialize(req))
            ? $"Requested '{rec.Name}' ({missing.Count}/{rec.Chunks} chunk(s))."
            : "Sender is offline — WAITING flag set, the request is retried automatically.";
    }

    /// <summary>The user chose to download this file whatever the network (the transfer setting no longer holds it back).</summary>
    public async Task<string> DownloadAnywayAsync(AttachmentRecord rec)
    {
        rec.NetworkOverride = true;
        _store.Save();
        return await RequestAsync(rec);
    }

    /// <summary>The network changed (the app says so): downloads that were waiting for Wi-Fi start again if the setting now allows them.</summary>
    public async Task NetworkChangedAsync()
    {
        foreach (var rec in _store.All.Where(r => r.Direction == AttachmentDirection.Received && r.State == AttachmentState.Receiving && r.WaitingNetwork).ToList())
            if (rec.NetworkOverride || _canTransfer(rec.Size)) await RequestAsync(rec);
    }

    // ------------------------------------------------------------------
    // Group swarm (§7): who holds what, how much each holder is given, and when the work of a slow one moves to another
    // ------------------------------------------------------------------

    private sealed class SwarmPeer
    {
        public HashSet<int>? Has;                 // what the holder announced (null = never announced: assumed to hold the whole file)
        public bool Complete;
        public DateTime HasAt = DateTime.MinValue;
        public int Window = InitialWindow;        // chunks it may have outstanding
        public double Bps;                        // observed bytes per second
        public DateTime LastSeen = DateTime.UtcNow;       // last sign of life (a chunk, an announcement) or when it was given work
        public DateTime LastChunkAt = DateTime.MinValue;
        public DateTime BackoffUntil = DateTime.MinValue;
    }

    private sealed class Swarm
    {
        public Dictionary<string, SwarmPeer> Peers { get; } = [];
        public Dictionary<(int Id, string Holder), DateTime> Outstanding { get; } = [];   // chunk asked of a holder, and when
        public int Eligible;
        public bool Watching;
        public DateTime LastProgress = DateTime.UtcNow;   // last chunk that arrived
        public SwarmPeer Peer(string id) => Peers.TryGetValue(id, out var p) ? p : Peers[id] = new SwarmPeer();
        public int InFlight(string holder) => Outstanding.Keys.Count(k => k.Holder == holder);
    }

    private readonly Dictionary<string, Swarm> _swarms = [];

    /// <summary>Who has been asked for what (diagnostics, tests).</summary>
    public string DescribeSwarm(string reference)
    {
        lock (_lock)
        {
            if (!_swarms.TryGetValue(reference, out var sw)) return "no swarm";
            var parts = _store.PartIds(reference).Count;
            return $"parts={parts} outstanding=[{string.Join(", ", sw.Outstanding.Select(kv => $"{kv.Key.Id}@{kv.Key.Holder}"))}] peers=[{string.Join("; ", sw.Peers.Select(kv => $"{kv.Key}: win={kv.Value.Window} bps={kv.Value.Bps:F0} has={(kv.Value.Has == null ? "?" : kv.Value.Has.Count.ToString())} complete={kv.Value.Complete} backoff={(kv.Value.BackoffUntil > DateTime.UtcNow)}"))}] eligible={sw.Eligible}";
        }
    }

    private Swarm SwarmOf(string reference) => _swarms.TryGetValue(reference, out var s) ? s : _swarms[reference] = new Swarm();

    private static bool MayHave(SwarmPeer p, int id, DateTime now, TimeSpan refresh) =>
        p.Complete || p.Has == null || p.Has.Contains(id) || now - p.HasAt > refresh;

    /// <summary>
    /// Decides who is asked for which chunks (called under the lock). Every holder gets a window of chunks sized from its observed speed (about three seconds of data),
    /// so the fast ones are kept busy and a slow one holds back only its own small window. A holder that stays silent loses its window to the others; when everything
    /// missing is already requested, an idle holder asks again for what has been waiting longest elsewhere (the first answer wins, the duplicate is dropped). A partial
    /// holder is only asked for what it announced, and again for the rest after a while (it keeps receiving).
    /// </summary>
    private List<(string Holder, List<int> Ids)> PlanSwarm(AttachmentRecord rec, Swarm sw, IReadOnlyList<string> holders)
    {
        var now = DateTime.UtcNow;
        var parts = _store.PartIds(rec.Ref);
        // Chunks held now are done; chunks given to someone we no longer ask (banned, left the group) go back to the pool.
        foreach (var k in sw.Outstanding.Keys.Where(k => parts.Contains(k.Id) || !holders.Contains(k.Holder)).ToList()) sw.Outstanding.Remove(k);

        foreach (var h in holders)
        {
            var p = sw.Peer(h);
            if (sw.InFlight(h) > 0 && now - p.LastSeen > PeerSilence)
            {
                foreach (var k in sw.Outstanding.Keys.Where(k => k.Holder == h).ToList()) sw.Outstanding.Remove(k);
                p.Window = Math.Max(MinWindow, p.Window / 2);
                p.BackoffUntil = now + BackoffFor;
            }
        }

        var active = holders.Where(h => now >= sw.Peer(h).BackoffUntil).OrderByDescending(h => sw.Peer(h).Bps).ToList();
        sw.Eligible = active.Count;
        var plan = new Dictionary<string, List<int>>();
        void Give(string holder, int id)
        {
            sw.Outstanding[(id, holder)] = now;
            if (!plan.TryGetValue(holder, out var list)) plan[holder] = list = [];
            list.Add(id);
        }

        var requested = sw.Outstanding.Keys.Select(k => k.Id).ToHashSet();
        var pool = Enumerable.Range(0, rec.Chunks).Where(i => !parts.Contains(i) && !requested.Contains(i)).ToList();
        foreach (var h in active)
        {
            var p = sw.Peer(h);
            var inFlight = sw.InFlight(h);
            var room = active.Count == 1 ? int.MaxValue : p.Window - inFlight;
            if (room <= 0 || pool.Count == 0) continue;
            if (inFlight == 0) p.LastSeen = now;
            var take = pool.Where(i => MayHave(p, i, now, HaveRefresh)).Take(room).ToList();
            foreach (var id in take) Give(h, id);
            pool.RemoveAll(take.Contains);
        }

        if (pool.Count == 0 && active.Count > 1)
        {
            foreach (var h in active)
            {
                if (sw.InFlight(h) > 0) continue;   // still busy with its own chunks
                var p = sw.Peer(h);
                var again = sw.Outstanding.Where(kv => kv.Key.Holder != h && now - kv.Value >= EndgameAfter && MayHave(p, kv.Key.Id, now, HaveRefresh) && !sw.Outstanding.ContainsKey((kv.Key.Id, h)))
                    .OrderBy(kv => kv.Value).Select(kv => kv.Key.Id).Distinct().Take(Math.Max(MinWindow, p.Window)).ToList();
                if (again.Count == 0) continue;
                p.LastSeen = now;
                foreach (var id in again) Give(h, id);
            }
        }
        return [.. plan.Select(kv => (kv.Key, kv.Value))];
    }

    private async Task<string> RequestSwarmAsync(AttachmentRecord rec, IReadOnlyList<string> holders)
    {
        var sw = SwarmOf(rec.Ref);
        var asked = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            List<(string Holder, List<int> Ids)> plan;
            lock (_lock) plan = PlanSwarm(rec, sw, holders);
            if (plan.Count == 0) break;

            var failed = false;
            foreach (var (holder, ids) in plan)
            {
                if (await _sendOnce(holder, "attachment_request", JsonSerializer.Serialize(new AttachmentRequest { Ref = rec.Ref, Missing = ids }))) asked += ids.Count;
                else
                {
                    // Offline: what it was given goes back to the pool and is handed to the others in the very next pass.
                    lock (_lock)
                    {
                        foreach (var id in ids) sw.Outstanding.Remove((id, holder));
                        sw.Peer(holder).BackoffUntil = DateTime.UtcNow + SwarmCheckEvery;
                    }
                    failed = true;
                }
            }
            if (!failed) break;
        }

        bool running;
        lock (_lock) running = sw.Outstanding.Count > 0;
        Watch(rec, sw);
        if (asked > 0) return holders.Count == 1 ? $"Requested '{rec.Name}' ({asked}/{rec.Chunks} chunk(s))." : $"Requested '{rec.Name}' ({asked}/{rec.Chunks} chunk(s)) from {holders.Count} member(s).";
        if (running) return $"Download of '{rec.Name}' in progress.";
        return holders.Count == 1 ? "Sender is offline — WAITING flag set, the request is retried automatically." : "No member holding this file is online — retried automatically.";
    }

    /// <summary>While a group download runs, checks its holders every few seconds (silent ones, idle ones) instead of waiting for the 30 s background tick.</summary>
    private void Watch(AttachmentRecord rec, Swarm sw)
    {
        lock (_lock) { if (sw.Watching) return; sw.Watching = true; }
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(SwarmCheckEvery);
                    if (rec.State != AttachmentState.Receiving || rec.WaitingNetwork) return;
                    lock (_lock) { if (DateTime.UtcNow - sw.LastProgress > WatchGiveUp) return; }   // nothing arrives any more: the background tick carries on
                    await RequestAsync(rec);
                }
            }
            catch (Exception ex) { _notify($"[!] Checking the download of '{rec.Name}' failed: {ex.Message}"); }
            finally { lock (_lock) sw.Watching = false; }
        });
    }

    /// <summary>A fellow member told us what it holds of a file (or that it cannot serve now): its chunks are only asked of it from now on, and what it lacks goes to the others at once.</summary>
    public void OnHave(string from, string payload)
    {
        AttachmentHave? msg;
        try { msg = JsonSerializer.Deserialize<AttachmentHave>(payload); }
        catch (JsonException) { return; }
        if (msg == null) return;

        AttachmentRecord? rec;
        lock (_lock)
        {
            rec = _store.Find(msg.Ref);
            if (rec is not { Direction: AttachmentDirection.Received, State: AttachmentState.Receiving, GroupId: not null } || _isGroupPeer?.Invoke(rec, from) != true) return;

            var sw = SwarmOf(rec.Ref);
            var p = sw.Peer(from);
            var now = DateTime.UtcNow;
            if (msg.Busy)
            {
                p.BackoffUntil = now + BackoffFor;
                foreach (var k in sw.Outstanding.Keys.Where(k => k.Holder == from).ToList()) sw.Outstanding.Remove(k);
            }
            else
            {
                var has = msg.Complete ? null : ChunkBitmap.Decode(msg.Have, rec.Chunks);
                if (!msg.Complete && has == null) return;   // not a bitmap of this file: ignored
                p.Complete = msg.Complete;
                p.Has = has;
                p.HasAt = now;
                p.LastSeen = now;
                foreach (var k in sw.Outstanding.Keys.Where(k => k.Holder == from && !msg.Complete && !has!.Contains(k.Id)).ToList()) sw.Outstanding.Remove(k);
                if (!msg.Complete && has!.Count == 0) p.BackoffUntil = now + BackoffFor;
            }
        }

        _ = Task.Run(async () =>
        {
            try { await RequestAsync(rec); }
            catch (Exception ex) { _notify($"[!] Re-requesting '{rec.Name}' failed: {ex.Message}"); }
        });
    }

    /// <summary>Verifies one chunk against the Merkle root and stores it; when the last one lands, assembles the file into its content-addressed blob.</summary>
    public void OnChunk(string from, string payload)
    {
        AttachmentChunk? chunk;
        try { chunk = JsonSerializer.Deserialize<AttachmentChunk>(payload); }
        catch (JsonException) { return; }
        if (chunk == null) return;

        AttachmentRecord? topUp = null;
        lock (_lock)
        {
            var rec = _store.Find(chunk.Ref);
            if (rec is not { Direction: AttachmentDirection.Received, State: AttachmentState.Receiving }) return;
            if (chunk.ChunkId < 0 || chunk.ChunkId >= rec.Chunks || chunk.TotalChunks != rec.Chunks) return;

            // Already held (a second holder answered the same request): nothing to verify or write, only the holder's speed to note.
            if (_store.HasPart(rec.Ref, chunk.ChunkId))
            {
                if (rec.GroupId != null) Credit(SwarmOf(rec.Ref), from, chunk.ChunkId);
                return;
            }

            byte[] data;
            List<byte[]> proof;
            try
            {
                data = Convert.FromBase64String(chunk.Data);
                proof = chunk.Proof.Select(Convert.FromBase64String).ToList();
            }
            catch (FormatException) { return; }

            var isLast = chunk.ChunkId == rec.Chunks - 1;
            var expected = isLast ? rec.Size - (long)(rec.Chunks - 1) * ChunkSize : ChunkSize;
            if (data.Length != expected) return;

            if (!MerkleTree.Verify(Convert.FromHexString(rec.Root), rec.Size, ChunkSize, chunk.ChunkId, MerkleTree.Leaf(data), proof))
            {
                rec.Strikes++;
                var strikes = rec.HolderStrikes[from] = rec.HolderStrikes.GetValueOrDefault(from) + 1;
                _notify($"[!] '{rec.Name}': chunk {chunk.ChunkId} from {from[..Math.Min(8, from.Length)]}… failed its Merkle proof — dropped ({strikes}/{MaxStrikes}).");
                if (strikes >= MaxStrikes)
                {
                    // In a group the other holders carry on without this one; in 1-to-1 there is nobody else.
                    if (rec.GroupId == null)
                    {
                        rec.State = AttachmentState.Failed;
                        _store.DeleteParts(rec.Ref);
                        _notify($"[!] '{rec.Name}': too many corrupt chunks — download abandoned. Use 'download' to try again.");
                    }
                    else if (!rec.BannedHolders.Contains(from))
                    {
                        rec.BannedHolders.Add(from);
                        _notify($"[!] '{rec.Name}': {from[..Math.Min(8, from.Length)]}… keeps sending corrupt chunks — no longer asked for this file.");
                    }
                }
                _store.Save();
                return;
            }

            _store.WritePart(rec.Ref, chunk.ChunkId, data, proof);
            rec.LastActivityAt = DateTime.UtcNow;

            if (_store.PartIds(rec.Ref).Count < rec.Chunks)
            {
                _store.Save();
                if (rec.GroupId != null)
                {
                    var sw = SwarmOf(rec.Ref);
                    Credit(sw, from, chunk.ChunkId);
                    // The sender is running out of work: hand out more now rather than at the next check.
                    if (sw.Eligible > 1 && sw.InFlight(from) <= Math.Max(1, sw.Peer(from).Window / 2)) topUp = rec;
                }
                return;
            }
            Finalize(rec);
        }
        if (topUp != null) _ = Task.Run(async () =>
        {
            try { await RequestAsync(topUp); }
            catch (Exception ex) { _notify($"[!] Requesting more of '{topUp.Name}' failed: {ex.Message}"); }
        });
    }

    /// <summary>A chunk arrived from <paramref name="from"/> (called under the lock): it is no longer outstanding anywhere, and the holder's speed sets how much it is given next.</summary>
    private void Credit(Swarm sw, string from, int chunkId)
    {
        foreach (var k in sw.Outstanding.Keys.Where(k => k.Id == chunkId).ToList()) sw.Outstanding.Remove(k);
        var p = sw.Peer(from);
        var now = DateTime.UtcNow;
        sw.LastProgress = now;
        var since = p.LastChunkAt == DateTime.MinValue ? p.LastSeen : p.LastChunkAt;
        var instant = ChunkSize / Math.Max(0.001, (now - since).TotalSeconds);
        p.Bps = p.Bps <= 0 ? instant : 0.6 * p.Bps + 0.4 * instant;
        p.Window = Math.Clamp((int)(p.Bps * WindowSeconds / ChunkSize), MinWindow, MaxWindow);
        p.LastChunkAt = now;
        p.LastSeen = now;
    }

    private void Finalize(AttachmentRecord rec)
    {
        var path = _store.FindBlob(rec.Root) ?? _store.BlobPath(rec.Root, rec.Name);
        if (!File.Exists(path)) _store.Assemble(rec.Ref, rec.Chunks, path); // every chunk was proven: no whole-file re-hash needed
        _store.DeleteParts(rec.Ref);

        // The declared mime is the sender's word: trust the content instead.
        var actual = DetectMime(path, rec.Name);
        if (actual != "application/octet-stream" && actual != rec.Mime)
        {
            _notify($"[!] '{rec.Name}' was declared {rec.Mime} but its content is {actual} — using {actual}.");
            rec.Mime = actual;
        }

        // Keep the tree so we can serve this file to others (group swarm, forward) without re-hashing it later.
        if (_store.LoadLeaves(rec.Root) == null)
        {
            using var fs = File.OpenRead(path);
            _store.SaveLeaves(rec.Root, MerkleTree.LeavesOf(fs, ChunkSize));
        }

        rec.State = AttachmentState.Complete;
        rec.Path = path;
        rec.WaitingNetwork = false;
        rec.NetworkOverride = false;
        _swarms.Remove(rec.Ref);
        _notify($"[file received] '{rec.Name}' ({rec.Size} bytes, verified) -> {path}");
        _store.Save();
        Warm(rec);
        Completed?.Invoke(rec);
    }

    /// <summary>Raised when a received file has been downloaded and verified (the UI shows it, e.g. as a thumbnail).</summary>
    public event Action<AttachmentRecord>? Completed;

    /// <summary>Background tick: re-requests any download that has made no progress for a while (sender was offline, connection dropped mid-transfer).</summary>
    public async Task ResumeStalledAsync()
    {
        foreach (var rec in _store.All.Where(r => r.Direction == AttachmentDirection.Received && r.State == AttachmentState.Receiving))
        {
            // A group download is followed all along (silent holders, idle holders); a 1-to-1 one only when it made no progress for a while.
            if (rec.GroupId == null)
            {
                if (DateTime.UtcNow - rec.LastActivityAt < StallAfter) continue;
                rec.LastActivityAt = DateTime.UtcNow; // don't re-request again before the next stall window
            }
            await RequestAsync(rec);
        }
    }

    /// <summary>
    /// A contact has just come online (the relay says so, protocol §9): a download that was waiting for them is requested again
    /// at once instead of at the next stall check. This is what lets a sender that was switched off be woken by a push, serve
    /// the file in the few seconds it stays connected, and go back to sleep. A download that just made progress is left alone.
    /// </summary>
    public async Task ResumeForPeerAsync(string peerId)
    {
        foreach (var rec in _store.All.Where(r => r.Direction == AttachmentDirection.Received && r.State == AttachmentState.Receiving
                                                  && (r.ContactId == peerId || r.GroupId != null)).ToList())
        {
            if (DateTime.UtcNow - rec.LastActivityAt < TimeSpan.FromSeconds(4)) continue;
            rec.LastActivityAt = DateTime.UtcNow;
            lock (_lock) { if (_swarms.TryGetValue(rec.Ref, out var sw) && sw.Peers.TryGetValue(peerId, out var p)) p.BackoffUntil = DateTime.MinValue; }   // it is back: no waiting
            await RequestAsync(rec);
        }
    }

    /// <summary>Sniffs well-known signatures (image/pdf/mp4), falls back to the extension, then octet-stream. Never executes or decodes the file.</summary>
    public static string DetectMime(string path, string fileName)
    {
        // An audio file in an MP4 container (voice notes) starts with the same "ftyp" signature as a video: the extension decides first.
        switch (Path.GetExtension(fileName).ToLowerInvariant())
        {
            case ".m4a": return "audio/mp4";
            case ".aac": return "audio/aac";
            case ".mp3": return "audio/mpeg";
            case ".ogg" or ".opus": return "audio/ogg";
            case ".wav": return "audio/wav";
            case ".amr": return "audio/amr";
        }

        try
        {
            var h = new byte[16];
            int n;
            using (var fs = File.OpenRead(path)) n = fs.Read(h, 0, h.Length);

            if (n >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "image/jpeg";
            if (n >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return "image/png";
            if (n >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return "image/gif";
            if (n >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return "image/webp";
            if (n >= 4 && h[0] == '%' && h[1] == 'P' && h[2] == 'D' && h[3] == 'F') return "application/pdf";
            if (n >= 12 && h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p') return "video/mp4";
        }
        catch (IOException) { /* fall through to the extension */ }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };
    }
}
