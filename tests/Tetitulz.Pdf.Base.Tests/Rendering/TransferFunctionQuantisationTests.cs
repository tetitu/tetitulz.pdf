// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An image <c>/TR</c> transfer function must quantise to the byte
/// <see cref="PdfColor.Channel"/> prescribes — the single quantisation rule both renderers reach.
///
/// <para><b>The bug this pins.</b> The raster path built its 256-entry LUT by rounding; the SVG
/// path's <c>RemapComponent</c> used a bare <c>(byte)(v * 255f)</c>, which truncates. Same
/// function, same image, two different bytes, off by one across most of the range. CLAUDE.md names
/// that exact cast as the reason the two renderers once disagreed on <c>/Decode [0 0.5]</c> — 127
/// rendered, 128 exported.</para>
///
/// <para><b>This file was called <c>TransferFunctionParityTests</c> and invoked neither
/// renderer.</b> It compared <see cref="PdfColor.Channel"/> against a private
/// <c>RasterQuantise</c> helper that <i>reproduced</i> the LUT's arithmetic by hand — a fourth
/// copy of the rule, living in the test that existed to stop copies drifting. Had
/// <c>TransferFunctionLut</c> changed, the copy would not have, and the file would have gone on
/// claiming parity. The name promised a comparison between renderers; the body compared one
/// function with a transcription of another. It now drives the <b>real</b> builder, and it is
/// named for what it actually checks.</para>
///
/// <para>Renderer-to-renderer parity for <c>/TR</c> is asserted where it can only be asserted —
/// by rendering — in <c>ImageTransferFunctionParityTests</c>.</para>
/// </summary>
public class TransferFunctionQuantisationTests
{
    /// <summary>A truncating quantiser, kept only as the control below.</summary>
    private static byte OldTruncatingQuantise(float v)
    {
        if (v < 0f) v = 0f; else if (v > 1f) v = 1f;
        return (byte)(v * 255f);
    }

    /// <summary>
    /// A Type 2 exponential, <c>y = x²</c> over [0,1]. Chosen because its outputs land <b>off</b>
    /// the byte grid: sampling a function whose outputs are all <c>i/255</c> would prove nothing,
    /// since <c>v * 255</c> is then exact and truncation and rounding agree. That is the vacuity
    /// this file's predecessor documented and avoided, and it is preserved here.
    /// </summary>
    private static PdfDictionary SquareFunction()
    {
        var fn = new PdfDictionary();
        fn.Set(PdfName.FunctionType, new PdfNumber(2));
        var domain = new PdfArray(); domain.Add(new PdfNumber(0)); domain.Add(new PdfNumber(1));
        fn.Set(PdfName.Domain, domain);
        var c0 = new PdfArray(); c0.Add(new PdfNumber(0));
        var c1 = new PdfArray(); c1.Add(new PdfNumber(1));
        fn.Set(new PdfName("C0"), c0);
        fn.Set(new PdfName("C1"), c1);
        fn.Set(PdfName.N, new PdfNumber(2));
        return fn;
    }

    /// <summary>
    /// The real claim: every entry the production LUT builder writes is exactly what
    /// <see cref="PdfColor.Channel"/> would produce for that entry's function output. Nothing here
    /// reproduces the builder's arithmetic, so the builder cannot drift away from the shared rule
    /// without this failing.
    /// </summary>
    [Fact]
    public void TheRealTransferLutQuantisesEveryEntryWithTheSharedChannelRule()
    {
        byte[] lut = TransferFunctionLut.BuildChannel(SquareFunction(), null);

        Assert.Equal(256, lut.Length);

        for (int i = 0; i < 256; i++)
        {
            float x = i / 255f;
            Assert.Equal(PdfColor.Channel(x * x), lut[i]);
        }
    }

    /// <summary>
    /// The control that keeps the test above from being vacuous. If the LUT had been built by
    /// truncation, the assertion above would fail — so the old form must genuinely disagree across
    /// much of this function's output range, and here it is measured rather than asserted by
    /// assumption.
    /// </summary>
    [Fact]
    public void TheOldTruncatingFormWouldDisagreeWithTheRealLutAcrossMuchOfTheRange()
    {
        byte[] lut = TransferFunctionLut.BuildChannel(SquareFunction(), null);

        int disagreements = 0;
        for (int i = 0; i < 256; i++)
        {
            float x = i / 255f;
            if (OldTruncatingQuantise(x * x) != lut[i]) disagreements++;
        }

        Assert.True(disagreements > 64,
            $"expected the truncating form to differ across much of the output range, saw "
            + $"{disagreements} of 256 — if this is near zero the fixture's function no longer "
            + "lands off the byte grid and the test above has gone vacuous");
    }

    /// <summary>
    /// A transfer function's output is not confined to values that round-trip an 8-bit input — a
    /// Type 2 exponential lands anywhere in [0,1]. Half-levels are where truncation and rounding
    /// differ most.
    /// </summary>
    [Theory]
    [InlineData(0.5f, 128)]      // truncation gives 127
    [InlineData(0.002f, 1)]      // 0.51 -> truncation gives 0
    [InlineData(0.999f, 255)]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    public void HalfLevelsRoundRatherThanTruncate(float value, byte expected)
    {
        Assert.Equal(expected, PdfColor.Channel(value));
    }

    /// <summary>Out-of-range output from a badly-behaved function is clamped, not wrapped.</summary>
    [Theory]
    [InlineData(-0.5f, 0)]
    [InlineData(2f, 255)]
    public void OutOfRangeOutputIsClamped(float value, byte expected)
    {
        Assert.Equal(expected, PdfColor.Channel(value));
    }
}
