// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The encoder's side of Annex G and Annex E — <see cref="JpxForwardComponentTransform"/> and
/// <see cref="JpxForwardQuantizer"/> — each checked against the inverse the standard gives the
/// decoder, written here; and the MQ coder's JPEG 2000 termination against T.88's.
/// </summary>
public sealed class JpxEncoderTransformTests
{
    /// <summary>G-3 to G-5 on (100, 50, 25): ⌊(100 + 100 + 25)/4⌋ = 56, 25 − 50 = −25, 100 − 50 = 50; and G-6 to G-8 undo any triple.</summary>
    [Fact]
    public void ForwardRct_MatchesG3ToG5_AndTheInverseUndoesIt()
    {
        int[] r = { 100 }, g = { 50 }, b = { 25 };
        JpxForwardComponentTransform.Rct(r, g, b);
        Assert.Equal((56, -25, 50), (r[0], g[0], b[0]));

        var random = new Random(3);
        var c0 = new int[500];
        var c1 = new int[500];
        var c2 = new int[500];
        for (int i = 0; i < 500; i++)
        {
            c0[i] = random.Next(-128, 128);
            c1[i] = random.Next(-128, 128);
            c2[i] = random.Next(-128, 128);
        }
        int[] y0 = (int[])c0.Clone(), y1 = (int[])c1.Clone(), y2 = (int[])c2.Clone();
        JpxForwardComponentTransform.Rct(y0, y1, y2);
        for (int i = 0; i < 500; i++)
        {
            int green = y0[i] - (int)Math.Floor((y1[i] + y2[i]) / 4.0);   // G-6
            Assert.Equal((c0[i], c1[i], c2[i]), (y2[i] + green, green, y1[i] + green));   // G-7, G-8
        }
        Assert.NotEqual(c1, y1);
    }

    /// <summary>G-9 to G-11 on pure red: Y = 0.299·255, Cb = −0.16875·255, Cr = 0.5·255; and the inverse G-12 to G-14 undoes any triple within rounding.</summary>
    [Fact]
    public void ForwardIct_MatchesG9ToG11_AndTheInverseUndoesIt()
    {
        float[] r = { 255 }, g = { 0 }, b = { 0 };
        JpxForwardComponentTransform.Ict(r, g, b);
        Assert.Equal(76.245f, r[0], 3);
        Assert.Equal(-43.031f, g[0], 3);
        Assert.Equal(127.5f, b[0], 3);

        var random = new Random(5);
        var c0 = new float[200];
        var c1 = new float[200];
        var c2 = new float[200];
        for (int i = 0; i < 200; i++)
        {
            c0[i] = random.Next(-128, 128);
            c1[i] = random.Next(-128, 128);
            c2[i] = random.Next(-128, 128);
        }
        float[] y = (float[])c0.Clone(), cb = (float[])c1.Clone(), cr = (float[])c2.Clone();
        JpxForwardComponentTransform.Ict(y, cb, cr);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(c0[i], y[i] + 1.402f * cr[i], 2);
            Assert.Equal(c1[i], y[i] - 0.34413f * cb[i] - 0.71414f * cr[i], 2);
            Assert.Equal(c2[i], y[i] + 1.772f * cb[i], 2);
        }
    }

    [Fact]
    public void DcShift_TakesHalfTheRangeFromUnsignedSamplesOnly()
    {
        int[] unsigned8 = { 0, 128, 255 }, signed8 = { -128, 0, 127 }, unsigned12 = { 4095 };
        JpxForwardComponentTransform.DcShift(unsigned8, 8, false);
        JpxForwardComponentTransform.DcShift(signed8, 8, true);
        JpxForwardComponentTransform.DcShift(unsigned12, 12, false);
        Assert.Equal(new[] { -128, 0, 127 }, unsigned8);
        Assert.Equal(new[] { -128, 0, 127 }, signed8);
        Assert.Equal(new[] { 2047 }, unsigned12);
    }

    /// <summary>E-3 backwards: a step of 1/64 at R<sub>b</sub> = 10 is ε 16, μ 0; 0.75 is ε R<sub>b</sub> + 1 with μ 1024; the pair's step reproduces the request, and quantisation is E-9's floor with the sign kept.</summary>
    [Fact]
    public void StepSize_RoundTripsThroughE3_AndQuantizeFloorsTheMagnitude()
    {
        JpxForwardQuantizer.StepSize(1.0 / 64, 10, out int e, out int m);
        Assert.Equal((16, 0), (e, m));
        Assert.Equal(1.0 / 64, JpxForwardQuantizer.Step(e, m, 10), 12);

        JpxForwardQuantizer.StepSize(0.75, 8, out e, out m);
        Assert.Equal((9, 1024), (e, m));
        Assert.Equal(0.75, JpxForwardQuantizer.Step(e, m, 8), 12);

        // 1.9999 rounds up to the next power of two rather than a mantissa of 2048.
        JpxForwardQuantizer.StepSize(1.99999, 8, out e, out m);
        Assert.Equal((7, 0), (e, m));

        // The exponent is clamped to its five bits: a step too small for R_b = 8 becomes ε 31.
        JpxForwardQuantizer.StepSize(1e-12, 8, out e, out m);
        Assert.Equal((31, 0), (e, m));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxForwardQuantizer.StepSize(0, 8, out _, out _));

        Assert.Equal(-3, JpxForwardQuantizer.Quantize(-7.9f, 2));
        Assert.Equal(3, JpxForwardQuantizer.Quantize(7.9f, 2));
        Assert.Equal(0, JpxForwardQuantizer.Quantize(-1.9f, 2));
    }

    /// <summary>
    /// C.2.9: the JPEG 2000 termination is T.88's FLUSH without the FF AC marker, and a final 0xFF is
    /// discarded — so the segment never ends in 0xFF (B.10.7), and the decoder, which pads with
    /// 0xFF, reads the same decisions from both.
    /// </summary>
    [Fact]
    public void FinishCodewordSegment_DropsTheMarkerAndAFinalFF_AndDecodesTheSame()
    {
        var random = new Random(11);
        for (int trial = 0; trial < 200; trial++)
        {
            var decisions = new List<(int, int)>();
            int count = random.Next(1, 60);
            for (int i = 0; i < count; i++) decisions.Add((random.Next(3), random.Next(10) < 8 ? 1 : 0));

            var t88 = new MqEncoder(3);
            var t800 = new MqEncoder(3);
            foreach ((int cx, int bit) in decisions)
            {
                t88.Encode(cx, bit);
                t800.Encode(cx, bit);
            }
            byte[] withMarker = t88.Finish();
            byte[] segment = t800.FinishCodewordSegment();

            Assert.Equal(withMarker, segment.Concat(new byte[] { 0xFF, 0xAC }).ToArray());
            Assert.NotEqual(0xFF, segment[segment.Length - 1]);

            var decoder = new MqDecoder(segment, 0, segment.Length, 3);
            foreach ((int cx, int bit) in decisions) Assert.Equal(bit, decoder.Decode(cx));
        }
    }
}
