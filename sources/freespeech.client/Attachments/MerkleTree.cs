/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;

namespace FreeSpeech.Client.Attachments;

/// <summary>
/// Merkle tree over a file's fixed-size chunks (protocol §6/§7): the root
/// is the content identity, and a chunk plus its proof (the sibling hashes
/// on its way to the root) can be verified on arrival from ANY holder, so a
/// corrupt chunk from one peer is caught immediately instead of at the end.
///
/// Leaf = SHA-256(0x00 ‖ chunk), node = SHA-256(0x01 ‖ left ‖ right) (domain
/// separation). A node without a sibling (odd count) is promoted unchanged —
/// never duplicated — and the leaf count is always derived from the file size,
/// never taken from the sender, so the usual duplicate-leaf ambiguity cannot arise.
/// </summary>
public static class MerkleTree
{
    public const int HashSize = 32;

    public static byte[] Leaf(ReadOnlySpan<byte> chunk)
    {
        var buf = new byte[1 + chunk.Length];
        buf[0] = 0x00;
        chunk.CopyTo(buf.AsSpan(1));
        return SHA256.HashData(buf);
    }

    private static byte[] Node(byte[] left, byte[] right)
    {
        var buf = new byte[1 + 2 * HashSize];
        buf[0] = 0x01;
        left.CopyTo(buf, 1);
        right.CopyTo(buf, 1 + HashSize);
        return SHA256.HashData(buf);
    }

    public static int LeafCount(long size, int chunkSize) => (int)((size + chunkSize - 1) / chunkSize);

    /// <summary>Hashes a stream chunk by chunk (bounded memory).</summary>
    public static List<byte[]> LeavesOf(Stream stream, int chunkSize)
    {
        var leaves = new List<byte[]>();
        var buffer = new byte[chunkSize];
        int read;
        while ((read = ReadFully(stream, buffer)) > 0)
            leaves.Add(Leaf(buffer.AsSpan(0, read)));
        return leaves;
    }

    public static byte[] Root(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count == 0) throw new ArgumentException("No leaves.", nameof(leaves));
        var level = leaves.ToList();
        while (level.Count > 1) level = NextLevel(level);
        return level[0];
    }

    /// <summary>Sibling hashes from the leaf up to (not including) the root; levels where the node has no sibling contribute nothing.</summary>
    public static List<byte[]> Proof(IReadOnlyList<byte[]> leaves, int index)
    {
        if (index < 0 || index >= leaves.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var proof = new List<byte[]>();
        var level = leaves.ToList();
        var i = index;
        while (level.Count > 1)
        {
            var sibling = i ^ 1;
            if (sibling < level.Count) proof.Add(level[sibling]);
            level = NextLevel(level);
            i /= 2;
        }
        return proof;
    }

    /// <summary>
    /// The content identity announced in an offer: SHA-256(0x02 ‖ size ‖ tree root). Binding the
    /// size makes the leaf count part of what the root commits to, so one root can never be
    /// "verified" against a different chunk count, and equal ids imply equal sizes (safe de-duplication).
    /// </summary>
    public static byte[] ContentId(IReadOnlyList<byte[]> leaves, long size) => Bind(Root(leaves), size);

    private static byte[] Bind(byte[] treeRoot, long size)
    {
        var buf = new byte[1 + 8 + HashSize];
        buf[0] = 0x02;
        BitConverter.GetBytes(size).CopyTo(buf, 1);
        treeRoot.CopyTo(buf, 9);
        return SHA256.HashData(buf);
    }

    /// <summary>Verifies a chunk against an offer's content id: <paramref name="size"/> fixes the leaf count, the proof must lead to a tree root that the id commits to.</summary>
    public static bool Verify(byte[] contentId, long size, int chunkSize, int index, byte[] leaf, IReadOnlyList<byte[]> proof)
    {
        if (size <= 0) return false;
        var root = ComputeRoot(LeafCount(size, chunkSize), index, leaf, proof);
        return root != null && Bind(root, size).AsSpan().SequenceEqual(contentId);
    }

    /// <summary>Checks that <paramref name="leaf"/> sits at <paramref name="index"/> of a tree with <paramref name="leafCount"/> leaves and the given (raw) tree root.</summary>
    public static bool VerifyTree(byte[] root, int leafCount, int index, byte[] leaf, IReadOnlyList<byte[]> proof)
    {
        var computed = ComputeRoot(leafCount, index, leaf, proof);
        return computed != null && computed.AsSpan().SequenceEqual(root);
    }

    private static byte[]? ComputeRoot(int leafCount, int index, byte[] leaf, IReadOnlyList<byte[]> proof)
    {
        if (leafCount <= 0 || index < 0 || index >= leafCount) return null;

        var current = leaf;
        var count = leafCount;
        var i = index;
        var p = 0;
        while (count > 1)
        {
            var sibling = i ^ 1;
            if (sibling < count)
            {
                if (p >= proof.Count || proof[p].Length != HashSize) return null;
                current = (i & 1) == 0 ? Node(current, proof[p]) : Node(proof[p], current);
                p++;
            }
            count = (count + 1) / 2;
            i /= 2;
        }
        return p == proof.Count ? current : null;
    }

    private static List<byte[]> NextLevel(List<byte[]> level)
    {
        var next = new List<byte[]>((level.Count + 1) / 2);
        for (var i = 0; i < level.Count; i += 2)
            next.Add(i + 1 < level.Count ? Node(level[i], level[i + 1]) : level[i]);
        return next;
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
