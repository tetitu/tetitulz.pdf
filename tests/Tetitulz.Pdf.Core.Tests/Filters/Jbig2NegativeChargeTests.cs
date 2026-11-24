// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Filters;

/// <summary>
/// A JBIG2 stream cannot buy room in the budget it decodes under. Two object streams filtered
/// through <c>/JBIG2Decode</c>: the first, 73 bytes, sums a symbol width to −2 147 483 600, so a
/// region charging <c>64 + width × height</c> would pay a negative cost, crediting the stream's
/// budget and the parser's whole-document budget above it with 38 G units; the second, 143 bytes
/// (the first is its prefix), would then decode its full 128 Mi stream budget, 2.6 s, in each of
/// the two recovery scans.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class Jbig2NegativeChargeTests
{
    private const string Hostile73 =
        "312001000000005B000337302034203334203C3C000000000000141E002F54773D3D3D3D3D3D3D3D3D3D3D3D3D3D" +
        "3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D65636F64202F5061";

    private const string Hostile143 =
        "312001000000005B000337302034203334203C3C000000000000141E002F54773D3D3D3D3D3D3D3D3D3D3D3D3D3D" +
        "3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D3D65636F6465202F466C6174654465636F6465202F466C6174654465" +
        "636F64633D3D3D3D3D3D3D3D3D3D3D3D3D65636F6465202F466C6174654465636F64652033203E3E0A7374726561" +
        "6D0A010203";

    [Fact]
    public void Adversarial_negative_symbol_width_does_not_credit_the_parent_budget()
    {
        var parent = new WorkBudget(1 << 20, "document");

        // The first stream fails on its own (the negative width), and hands its bytes on.
        Decode(Hostile73, parent);
        Assert.NotNull(Jbig2DecodeFilter.LastFallbackReason);
        Assert.InRange(parent.Spent, 0, parent.Total);

        // The second spends the parent past its million, and the parent refuses as itself. With the
        // credit it decoded on to its own 128 Mi and fell back, never refused.
        var ex = Assert.Throws<WorkBudgetExceededException>(() => Decode(Hostile143, parent));
        Assert.Same(parent, ex.Budget);
    }

    /// <summary>
    /// The control: the second stream alone, under a parent with room, runs to its own budget (two
    /// million here, so the test is quick) and falls back, charging the parent exactly that.
    /// </summary>
    [Fact]
    public void Adversarial_symbol_dictionary_alone_is_stopped_by_its_own_budget()
    {
        try
        {
            PdfLimits.MaxJbig2CompositionPixels = 2 << 20;
            var parent = new WorkBudget(long.MaxValue / 4, "document");

            Decode(Hostile143, parent);

            Assert.Contains("JBIG2 decode", Jbig2DecodeFilter.LastFallbackReason, StringComparison.Ordinal);
            Assert.Equal(2 << 20, parent.Spent);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static byte[] Decode(string hex, WorkBudget work)
        => FilterPipeline.Decode(Convert.FromHexString(hex), PdfName.JBIG2Decode, null,
            PdfLimits.MaxDecompressedBytes, work);
}
