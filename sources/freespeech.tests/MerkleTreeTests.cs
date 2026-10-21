/*******************************************************************
 * FreeSpeech - P2P Encrypted Messaging
 * -----------------------------------------------------------------------
 * Copyright (c) 2024-2026 Thomas Gourgnier
 * Licensed under the GNU Affero General Public License v3.0 or later.
 * SPDX-License-Identifier: AGPL-3.0-or-later
 *******************************************************************/

using System.Security.Cryptography;
using FreeSpeech.Client.Attachments;

namespace FreeSpeech.Tests;

/// <summary>Merkle tree (§6/§7): every proof verifies for every size incl. odd ones; anything altered — data, index, proof, leaf count — fails.</summary>
public class MerkleTreeTests
{
    private static List<byte[]> Leaves(int n) =>
        Enumerable.Range(0, n).Select(_ => MerkleTree.Leaf(RandomNumberGenerator.GetBytes(64))).ToList();

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(16)] [InlineData(17)] [InlineData(530)]
    public void EveryLeafProof_verifies(int n)
    {
        var leaves = Leaves(n);
        var root = MerkleTree.Root(leaves);
        for (var i = 0; i < n; i++)
            Assert.True(MerkleTree.VerifyTree(root, n, i, leaves[i], MerkleTree.Proof(leaves, i)), $"leaf {i}/{n}");
    }

    [Fact]
    public void SingleLeaf_rootIsTheLeaf_andProofIsEmpty()
    {
        var leaves = Leaves(1);
        Assert.Equal(leaves[0], MerkleTree.Root(leaves));
        Assert.Empty(MerkleTree.Proof(leaves, 0));
    }

    [Fact]
    public void ProofLength_isLogarithmic()
    {
        var leaves = Leaves(530); // a 25 MiB file
        Assert.InRange(MerkleTree.Proof(leaves, 0).Count, 1, 10);
    }

    [Fact]
    public void Tampering_isRejected()
    {
        var leaves = Leaves(9);
        var root = MerkleTree.Root(leaves);
        var proof = MerkleTree.Proof(leaves, 4);

        Assert.False(MerkleTree.VerifyTree(root, 9, 4, MerkleTree.Leaf([1, 2, 3]), proof));   // wrong data
        Assert.False(MerkleTree.VerifyTree(root, 9, 5, leaves[4], proof));                    // wrong index
        Assert.False(MerkleTree.VerifyTree(root, 9, 4, leaves[4], proof.Skip(1).ToList()));   // proof too short
        Assert.False(MerkleTree.VerifyTree(root, 9, 4, leaves[4], proof.Append(new byte[32]).ToList())); // proof too long
        // (A wrong leaf count is NOT detectable on a raw tree — see ContentId_bindsTheSize... — which is why offers carry ContentId.)
        Assert.False(MerkleTree.VerifyTree(root, 9, 9, leaves[4], proof));                    // index out of range
        Assert.False(MerkleTree.VerifyTree(root, 9, -1, leaves[4], proof));

        var flipped = proof.Select(p => (byte[])p.Clone()).ToList();
        flipped[0][0] ^= 1;
        Assert.False(MerkleTree.VerifyTree(root, 9, 4, leaves[4], flipped));                  // altered sibling
    }

    [Fact]
    public void ContentId_bindsTheSize_soTheSameTreeCannotPassForAnotherFileLength()
    {
        const int chunk = 48 * 1024;
        var data = RandomNumberGenerator.GetBytes(3 * chunk - 100);           // 3 chunks
        var leaves = MerkleTree.LeavesOf(new MemoryStream(data), chunk);
        var id = MerkleTree.ContentId(leaves, data.Length);
        var proof = MerkleTree.Proof(leaves, 1);

        Assert.True(MerkleTree.Verify(id, data.Length, chunk, 1, leaves[1], proof));
        Assert.False(MerkleTree.Verify(id, data.Length - 1, chunk, 1, leaves[1], proof));   // same chunk count, different size
        Assert.False(MerkleTree.Verify(id, data.Length + chunk, chunk, 1, leaves[1], proof)); // different chunk count
        Assert.False(MerkleTree.Verify(id, 0, chunk, 1, leaves[1], proof));
        // Two files with the same chunks but different lengths never share an id.
        Assert.NotEqual(id, MerkleTree.ContentId(leaves, data.Length - 1));
    }

    [Fact]
    public void OddNodePromotion_doesNotAllowDuplicateLeafForgery()
    {
        // Classic Merkle pitfall: [a,b,c] must not share a root with [a,b,c,c].
        var l = Leaves(3);
        Assert.NotEqual(MerkleTree.Root(l), MerkleTree.Root(l.Append(l[2]).ToList()));
    }

    [Fact]
    public void LeafAndNode_areDomainSeparated()
    {
        // A 2-leaf root must not equal the leaf hash of the concatenated children.
        var l = Leaves(2);
        var concat = l[0].Concat(l[1]).ToArray();
        Assert.NotEqual(MerkleTree.Root(l), MerkleTree.Leaf(concat));
    }

    [Fact]
    public void LeavesOf_matchesManualChunking()
    {
        var data = RandomNumberGenerator.GetBytes(100_000);
        var leaves = MerkleTree.LeavesOf(new MemoryStream(data), 48 * 1024);
        Assert.Equal(3, leaves.Count);
        Assert.Equal(MerkleTree.Leaf(data.AsSpan(0, 48 * 1024)), leaves[0]);
        Assert.Equal(MerkleTree.Leaf(data.AsSpan(96 * 1024)), leaves[2]);
        Assert.Equal(3, MerkleTree.LeafCount(100_000, 48 * 1024));
    }
}
