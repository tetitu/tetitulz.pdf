// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The transcoder decodes each code-block into an array of its workspace's, one a block size, and turns it into its
/// known bits in place: a 2000 × 2000 RGB image made HTJ2K allocated 93 MB with an array (and a copy) a block, 51 MB
/// with these. The transcoded output is the same either way, so the reuse is what these tests can see.
/// </summary>
public class JpxTranscoderWorkspaceTests
{
    /// <summary>A size asked for again is the same array, cleared; another size is another array, kept beside it.</summary>
    [Fact]
    public void BlockValues_IsOneClearedArrayASize()
    {
        var workspace = new JpxBlockDecoder();
        long[] whole = workspace.BlockValues(4096);
        whole[17] = 5;
        long[] edge = workspace.BlockValues(1024);

        Assert.NotSame(whole, edge);
        Assert.Equal(1024, edge.Length);
        long[] again = workspace.BlockValues(4096);
        Assert.Same(whole, again);
        Assert.Equal(0, again[17]);
        Assert.Same(edge, workspace.BlockValues(1024));
    }

    /// <summary>
    /// Known bits are written over the decoded values: (2μ + 1)·2<sup>k</sup> becomes μ·2<sup>k</sup>, signed, and 0
    /// stays 0.
    /// </summary>
    [Fact]
    public void KnownBits_RewritesTheValuesInPlace()
    {
        long[] values = { 0, 3, -3, 12, 7, -40 };   // 3 = (2·1+1)·1, 12 = (2·1+1)·4, 7 = (2·3+1)·1, 40 = (2·2+1)·8

        long[] known = JpxTranscoder.KnownBits(values);

        Assert.Same(values, known);
        Assert.Equal(new long[] { 0, 1, -1, 4, 3, -16 }, known);
    }
}
