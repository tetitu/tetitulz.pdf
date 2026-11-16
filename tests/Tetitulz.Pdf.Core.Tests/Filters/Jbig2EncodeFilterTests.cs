// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Filters;

/// <summary>
/// <see cref="Jbig2EncodeFilter"/> — arithmetic-coded generic-region encoding, checked by decoding
/// it back through the filter a PDF reader would use.
/// </summary>
/// <remarks>
/// <para><b>The round trip shares whatever the two halves both believe, and that is worth stating
/// rather than hiding.</b> Encode-then-decode is an A-versus-B comparison: a context template both
/// sides got wrong would pass. What closes it is that <c>Jbig2DecodeFilter</c> already reads
/// streams produced by other encoders in real PDFs — a wrong template would have failed there long
/// before this existed — and that both sides now read <i>one</i> table rather than two copies.</para>
///
/// <para><b>Exactness is the claim.</b> JBIG2's reputation for substituting one character for
/// another comes from symbol matching, which this does not do; a generic region is lossless, so the
/// assertion is bit-for-bit and not a similarity.</para>
/// </remarks>
public class Jbig2EncodeFilterTests
{
    /// <summary>A pattern survives encoding and decoding, bit for bit.</summary>
    /// <remarks>
    /// The pattern is deliberately not uniform: a solid or empty image is compressed by any
    /// context model and would pass with the neighbours read in the wrong order.
    /// </remarks>
    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 5)]
    [InlineData(1, 1)]
    [InlineData(37, 19)]        // a width that is not a byte multiple
    public void APatternSurvivesTheRoundTripExactly(int width, int height)
    {
        int stride = (width + 7) / 8;
        byte[] bits = Pattern(width, height, stride);

        byte[] encoded = Jbig2EncodeFilter.Encode(bits, width, height, stride);
        Assert.NotNull(encoded);

        byte[] decoded = Decode(encoded, width, height);

        AssertRoundTripped(bits, decoded, width, height, stride);
    }

    /// <summary>Text-like content compresses, which is the whole reason to use it.</summary>
    /// <remarks>
    /// <b>A round trip alone would pass on an encoder that stored the bitmap verbatim.</b> The
    /// margin here is deliberately loose — the claim is that the context model does something, not
    /// that it hits a particular ratio, and pinning a ratio would make an improvement look like a
    /// regression.
    /// </remarks>
    [Fact]
    public void RepetitiveContentCompresses()
    {
        const int width = 256, height = 128;
        int stride = width / 8;

        // Horizontal rules every eighth row: highly predictable from the row above, which is what
        // the context model exists to exploit.
        var bits = new byte[stride * height];
        for (int y = 0; y < height; y += 8)
            for (int i = 0; i < stride; i++)
                bits[(y * stride) + i] = 0xFF;

        byte[] encoded = Jbig2EncodeFilter.Encode(bits, width, height, stride);

        Assert.NotNull(encoded);
        Assert.True(encoded.Length < bits.Length / 2,
            $"{encoded.Length} bytes from {bits.Length}; the context model is not predicting");

        AssertRoundTripped(bits, Decode(encoded, width, height), width, height, stride);
    }

    /// <summary>An all-black and an all-white image both survive.</summary>
    /// <remarks>
    /// The degenerate cases, and they are not redundant: an encoder whose adaptation never leaves
    /// its initial state produces a stream the decoder reads as the opposite colour, and a uniform
    /// image is where that is invisible to a size assertion but obvious to a pixel one.
    /// </remarks>
    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void AUniformImageSurvivesTheRoundTrip(int fill)
    {
        const int width = 24, height = 9;
        int stride = width / 8;

        var bits = new byte[stride * height];
        for (int i = 0; i < bits.Length; i++) bits[i] = (byte)fill;

        byte[] decoded = Decode(Jbig2EncodeFilter.Encode(bits, width, height, stride), width, height);

        AssertRoundTripped(bits, decoded, width, height, stride);
    }

    /// <summary>Unusable input is declined rather than encoded into nonsense.</summary>
    [Theory]
    [InlineData(0, 8, 1)]
    [InlineData(8, 0, 1)]
    [InlineData(8, 8, 0)]       // a stride too small for the width
    public void MalformedInputIsDeclined(int width, int height, int stride)
    {
        Assert.Null(Jbig2EncodeFilter.Encode(new byte[64], width, height, stride));
        Assert.Null(Jbig2EncodeFilter.Encode(null, 8, 8, 1));
    }

    /// <summary>A buffer shorter than the image it declares is declined.</summary>
    /// <remarks>
    /// Named <c>Truncated</c> so the guard reads the refusal as the behaviour under test: without
    /// the check the encoder indexes past the end of the caller's array.
    /// </remarks>
    [Fact]
    public void ATruncatedBufferIsDeclined()
    {
        Assert.Null(Jbig2EncodeFilter.Encode(new byte[4], width: 8, height: 8, stride: 1));
    }

    /// <summary>
    /// A black T.88 pixel comes back as a PDF sample of 0, not of 1.
    /// </summary>
    /// <remarks>
    /// <b>Every other assertion here depends on this and none of them states it.</b> They compare
    /// across the boundary, so a decoder that stopped inverting would make them all fail together
    /// with no indication of why; this is the one that names the convention. §8.9.5.2 makes 0
    /// black for a 1-bit DeviceGray sample, which is the opposite of T.88's internal bitmap.
    /// </remarks>
    [Fact]
    public void ABlackPixelBecomesASampleOfZero()
    {
        const int width = 8, height = 1;

        // One black pixel at x = 0, the rest white, in T.88 polarity.
        var bits = new byte[] { 0x80 };

        byte[] decoded = Decode(Jbig2EncodeFilter.Encode(bits, width, height, 1), width, height);

        Assert.Equal(0, (decoded[0] >> 7) & 1);          // black -> 0
        Assert.Equal(1, (decoded[0] >> 6) & 1);          // white -> 1
    }

    /// <summary>
    /// The exact bytes an 8x8 pattern encodes to.
    /// </summary>
    /// <remarks>
    /// <para><b>A characterisation test, and the only assertion here that can see a context
    /// relabelling.</b> Every other test round-trips, and a round trip <i>structurally cannot</i>
    /// detect one: reverse the order in which the ten neighbours are packed into the context index
    /// and the encoder and decoder still agree, because a bijection on the labels leaves each
    /// adaptive state seeing exactly the same subsequence of decisions. Mutating that order
    /// survived every other test in this file. The resulting stream is garbage to any other
    /// decoder.</para>
    ///
    /// <para><b>It also covers the flush.</b> The decoder pads past end-of-data with <c>0xFF</c>,
    /// so a FLUSH that emits one byte instead of two, or omits SETBITS, still round-trips — the
    /// missing bits are recovered from the padding. The last bytes here are the only thing that
    /// notices.</para>
    ///
    /// <para><b>These numbers came from this implementation</b>, so they prove nothing about
    /// correctness on their own; the round-trip tests do that. What this makes is an unintended
    /// change <i>loud</i> — the same reason <c>HashR6TerminationReadingTests</c> pins its
    /// hash. If a deliberate change moves them, re-derive them and say why in the commit.</para>
    /// </remarks>
    [Fact]
    public void TheEncodedBytesArePinnedSoAnUnintendedChangeIsLoud()
    {
        byte[] encoded = Jbig2EncodeFilter.Encode(Pattern(8, 8, 1), 8, 8, 1);

        byte[] expected =
        {
            // Page information segment: header, then 19 bytes of 8x8 page.
            0x00, 0x00, 0x00, 0x01, 0x30, 0x00, 0x01, 0x00, 0x00, 0x00, 0x13,
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

            // Immediate lossless generic region: header, region info, flags, the four AT pixels.
            0x00, 0x00, 0x00, 0x02, 0x27, 0x00, 0x01, 0x00, 0x00, 0x00, 0x24,
            0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08,
            // x, y, the external combination operator, and the generic-region flags.
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03, 0xFF, 0xFD, 0xFF, 0x02, 0xFE, 0xFE, 0xFE,

            // The MQ-coded region, ending in FLUSH's FF AC marker.
            0xC8, 0x34, 0xE9, 0x0E, 0xC7, 0x19, 0xF3, 0x8D, 0xFF, 0xAC,
        };

        Assert.Equal(expected, encoded);
    }

    // ================================================================ helpers

    /// <summary>Decodes through the filter a PDF reader uses, not through the internals.</summary>
    private static byte[] Decode(byte[] encoded, int width, int height)
    {
        Assert.NotNull(encoded);

        var parms = new PdfDictionary(2);
        parms.Set(PdfName.Width, new PdfNumber(width));
        parms.Set(PdfName.Height, new PdfNumber(height));

        byte[] decoded = new Jbig2DecodeFilter().Decode(encoded, parms);

        Assert.NotNull(decoded);
        return decoded;
    }

    /// <summary>
    /// Compares the pixels across the polarity boundary: T.88 in, PDF samples out.
    /// </summary>
    /// <remarks>
    /// <para><b>A correct round trip INVERTS, and that is the design rather than a bug.</b> The
    /// encoder speaks T.88's convention, where a set bit is black; <c>Jbig2DecodeFilter</c> emits
    /// PDF image samples, where §8.9.5.2 makes 0 black — so it inverts on the way out, as every
    /// other implementation does at the same boundary. A test expecting equality is comparing two
    /// different conventions.</para>
    ///
    /// <para><b>There is no switch that would "fix" it, and there used to be.</b>
    /// <c>EmitLegacyInvertedPolarity</c> let a caller ask for T.88 polarity back; it was a
    /// process-wide mutable static, so flipping it inside a test would have made this assertion
    /// depend on what else the process was doing — and it has since been deleted outright, because
    /// a compatibility flag for a defect is how the defect comes back (GAPS.md §2d).</para>
    ///
    /// <para>The padding bits past the width are not part of the image and the decoder may leave
    /// them however it likes, so this walks pixels rather than bytes.</para>
    /// </remarks>
    private static void AssertRoundTripped(byte[] t88, byte[] pdfSamples,
                                           int width, int height, int stride)
    {
        int actualStride = (width + 7) / 8;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int black = (t88[(y * stride) + (x >> 3)] >> (7 - (x & 7))) & 1;
                int sample = (pdfSamples[(y * actualStride) + (x >> 3)] >> (7 - (x & 7))) & 1;

                Assert.True(black == 1 - sample,
                    $"pixel ({x},{y}) came back as sample {sample} for a T.88 bit of {black}");
            }
        }
    }

    /// <summary>A pattern with runs, edges and isolated pixels.</summary>
    private static byte[] Pattern(int width, int height, int stride)
    {
        var bits = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Diagonals crossed with a block: neither a run-length coder's best case nor a
                // uniform field.
                bool set = ((x + y) % 5 == 0) || (x > width / 3 && x < width / 2 && y % 3 == 0);
                if (set) bits[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return bits;
    }
}
