// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfObjectEquivalence"/>'s byte budget: every pair of decoded streams compared is
/// charged what was decoded, and a comparison the budget does not admit is undecided, never a
/// verdict — a node budget alone priced a 16 MiB comparison as one node.
/// </summary>
public class PdfObjectEquivalenceTests
{
    private static PdfStream Stream(string content)
    {
        var stream = new PdfStream();
        stream.RawData = Encoding.ASCII.GetBytes(content);
        return stream;
    }

    [Fact]
    public void ComparingTwoStreams_ChargesTheirDecodedLengthsToTheByteBudget()
    {
        string content = new string('a', 1000);
        int nodes = 1000;
        long bytes = 4000;

        var verdict = PdfObjectEquivalence.Compare(null, Stream(content), Stream(content), ref nodes, 1 << 20, ref bytes);

        Assert.Equal(PdfObjectEquivalence.Result.Same, verdict);
        Assert.Equal(2000, bytes);
    }

    [Fact]
    public void AComparisonTheByteBudgetDoesNotAdmit_IsUndecided_NotAVerdict()
    {
        int nodes = 1000;
        long bytes = 1999;

        var verdict = PdfObjectEquivalence.Compare(null, Stream(new string('a', 1000)), Stream(new string('b', 1000)),
            ref nodes, 1 << 20, ref bytes);

        Assert.Equal(PdfObjectEquivalence.Result.Undecided, verdict);
    }

    [Fact]
    public void DifferentStreamsWithinTheBudget_AreDifferent()
    {
        int nodes = 1000;
        long bytes = 1 << 20;

        var verdict = PdfObjectEquivalence.Compare(null, Stream(new string('a', 1000)), Stream(new string('b', 1000)),
            ref nodes, 1 << 20, ref bytes);

        Assert.Equal(PdfObjectEquivalence.Result.Different, verdict);
    }

    /// <summary>The overload without a byte budget charges nothing, as its callers expect.</summary>
    [Fact]
    public void WithoutAByteBudget_StreamsOfAnySizeCompare()
    {
        int nodes = 1000;
        string content = string.Concat(Enumerable.Repeat("0123456789", 10_000));

        var verdict = PdfObjectEquivalence.Compare(null, Stream(content), Stream(content), ref nodes, 1 << 20);

        Assert.Equal(PdfObjectEquivalence.Result.Same, verdict);
    }
}
