// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The generic region decoder forms its context from sliding windows — one per run of template
/// positions that are consecutive in index and in x on one row — rather than reading every
/// position of the template, bounds-checked, for every pixel (61 ns a pixel: the composition
/// budget's 128 Mi pixels, which a 98-byte symbol dictionary spends in full, would take eight
/// seconds). This suite runs that per-position loop, kept here as the reference, over the same
/// MQ decoder and the same bytes, and asks for the same bitmap: every template, with the nominal AT
/// pixels and with each moved where the windows cannot merge it (another row, the row below, the
/// current row at and past x, far outside), TPGDON on and off, a skip bitmap, widths that are not
/// multiples of eight. Random bytes decode to some bitmap either way; the two loops must agree on
/// which, and the bitmaps must not be blank, or the agreement would be about nothing.
/// </summary>
public class Jbig2GenericRegionWindowsTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int template in new[] { 0, 1, 2, 3 })
            foreach (bool tpgdon in new[] { false, true })
                foreach (string at in new[] { "nominal", "scattered", "ahead", "below", "far" })
                    yield return new object[] { template, tpgdon, at };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WindowedContext_DecodesWhatThePerPositionLoopDecodes(int template, bool tpgdon, string at)
    {
        sbyte[] atPixels = AtPixels(template, at);
        var rng = new Random(template * 31 + (tpgdon ? 7 : 0) + at.Length);
        bool anyInk = false;
        foreach (var (w, h) in new[] { (37, 23), (8, 1), (1, 9), (64, 16), (5, 5), (130, 3) })
        {
            var data = new byte[600];
            rng.NextBytes(data);
            int contexts = ContextCount(template);

            var expected = Reference(new MqDecoder(data, 0, contexts), w, h, template, tpgdon, atPixels);
            var actual = Jbig2Decoder.DecodeGenericRegion(new MqDecoder(data, 0, contexts), 0, w, h, template, tpgdon, atPixels, null);

            Assert.Equal(expected.Data, actual.Data);
            foreach (byte b in expected.Data) if (b != 0) anyInk = true;
        }
        Assert.True(anyInk, "every bitmap decoded blank, so the agreement is about nothing");
    }

    /// <summary>USESKIP: a skipped pixel is 0 and not coded, in both loops, and the windows move past it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void WindowedContext_WithASkipBitmap_DecodesWhatThePerPositionLoopDecodes(int template)
    {
        var rng = new Random(99 + template);
        var data = new byte[600];
        rng.NextBytes(data);
        var skip = new Jbig2Decoder.Jbig2Bitmap(41, 17);
        for (int y = 0; y < 17; y++) for (int x = 0; x < 41; x++) if (rng.Next(3) == 0) skip.SetPixel(x, y, 1);
        sbyte[] atPixels = AtPixels(template, "nominal");
        int contexts = ContextCount(template);

        var expected = Reference(new MqDecoder(data, 0, contexts), 41, 17, template, false, atPixels, skip);
        var actual = Jbig2Decoder.DecodeGenericRegion(new MqDecoder(data, 0, contexts), 0, 41, 17, template, false, atPixels, null, skip);

        Assert.Equal(expected.Data, actual.Data);
        bool anyInk = false, anySkippedBlank = false;
        for (int y = 0; y < 17; y++)
            for (int x = 0; x < 41; x++)
            {
                if (actual.GetPixel(x, y) != 0) anyInk = true;
                if (skip.GetPixel(x, y) != 0) { Assert.Equal(0, actual.GetPixel(x, y)); anySkippedBlank = true; }
            }
        Assert.True(anyInk && anySkippedBlank);
    }

    /// <summary>The runs the nominal templates fall into: one per row, three each; a moved AT pixel is a run of its own.</summary>
    [Fact]
    public void ContextRuns_AreOnePerRowForTheNominalTemplates_AndOneMoreForAMovedAtPixel()
    {
        var nominal = Jbig2Decoder.GenericContextRuns.For(Jbig2Decoder.Template0RefPositions);
        Assert.Equal(3, nominal.Count);
        Assert.Equal(new[] { -2, -1, 0 }, nominal.Dy);
        Assert.Equal(new[] { 5, 7, 4 }, nominal.Length);
        Assert.Equal(new[] { 11, 4, 0 }, nominal.Shift);
        Assert.Equal(new[] { -2, -3, -4 }, nominal.DxFirst);

        var moved = ((int dy, int dx)[])Jbig2Decoder.Template0RefPositions.Clone();
        moved[11] = (-3, -5);   // AT1 to another row
        var runs = Jbig2Decoder.GenericContextRuns.For(moved);
        Assert.Equal(4, runs.Count);
        Assert.Equal(new[] { -2, -1, -3, 0 }, runs.Dy);
        Assert.Equal(new[] { 5, 6, 1, 4 }, runs.Length);
        Assert.Equal(new[] { 11, 5, 4, 0 }, runs.Shift);
    }

    /// <summary>
    /// A region is charged its setup beside its area: a one-pixel symbol costs about a microsecond
    /// of bitmap, runs and windows against sixteen nanoseconds a pixel, and a dictionary of millions
    /// of them charged a unit each would spend minutes inside the budget. Sixty-four units, pinned.
    /// </summary>
    [Fact]
    public void GenericRegion_IsChargedItsSetupBesideItsArea()
    {
        var data = new byte[64];
        new Random(5).NextBytes(data);
        var budget = new Tetitulz.Pdf.Core.Util.WorkBudget(1_000_000, "test");

        Jbig2Decoder.DecodeGenericRegion(new MqDecoder(data, 0, 1 << 16), 0, 5, 3, 0, false, AtPixels(0, "nominal"), budget);

        Assert.Equal(64, Jbig2Decoder.GenericRegionSetupUnits);
        Assert.Equal(Jbig2Decoder.GenericRegionSetupUnits + 5 * 3, budget.Spent);
    }

    // ------------------------------------------------------------------ the reference

    private static int ContextCount(int template) => 1 << (template == 0 ? 16 : template == 1 ? 13 : 10);

    private static sbyte[] AtPixels(int template, string kind)
    {
        // (dx, dy) pairs, as the segment header carries them.
        sbyte[] nominal = template switch
        {
            0 => new sbyte[] { 3, -1, -3, -1, 2, -2, -2, -2 },
            1 => new sbyte[] { 3, -1 },
            2 => new sbyte[] { 2, -1 },
            _ => new sbyte[] { 2, -1 },
        };
        var at = (sbyte[])nominal.Clone();
        switch (kind)
        {
            case "scattered":
                at[0] = -5; at[1] = -3;
                if (template == 0) { at[2] = 4; at[3] = -2; at[4] = 0; at[5] = -4; at[6] = -1; at[7] = 0; }
                break;
            case "ahead": at[0] = 1; at[1] = 0; break;      // the current row, not yet decoded: 0
            case "below": at[0] = 0; at[1] = 1; break;      // the row below: 0
            case "far": at[0] = -127; at[1] = -2; if (template == 0) { at[2] = 127; at[3] = -1; } break;
        }
        return at;
    }

    /// <summary>The loop the windows replaced, position by position.</summary>
    private static Jbig2Decoder.Jbig2Bitmap Reference(MqDecoder mq, int width, int height, int template,
        bool tpgdon, sbyte[] atPixels, Jbig2Decoder.Jbig2Bitmap skip = null)
    {
        (int dy, int dx)[] refTable = template switch
        {
            0 => Jbig2Decoder.Template0RefPositions,
            1 => Jbig2Decoder.Template1RefPositions,
            2 => Jbig2Decoder.Template2RefPositions,
            _ => Jbig2Decoder.Template3RefPositions,
        };
        int[] atSlots = template switch { 0 => new[] { 11, 5, 4, 0 }, 1 => new[] { 9 }, 2 => new[] { 7 }, _ => new[] { 5 } };
        int ltpContext = template switch { 0 => 0x9B25, 1 => 0x0795, 2 => 0x00E5, _ => 0x0195 };
        int contextBits = refTable.Length;

        var bitmap = new Jbig2Decoder.Jbig2Bitmap(width, height);
        var positions = ((int dy, int dx)[])refTable.Clone();
        for (int i = 0; i < atSlots.Length && i * 2 + 1 < atPixels.Length; i++)
            positions[atSlots[i]] = (atPixels[i * 2 + 1], atPixels[i * 2]);

        bool ltp = false;
        for (int y = 0; y < height; y++)
        {
            if (tpgdon)
            {
                if (mq.Decode(ltpContext) != 0) ltp = !ltp;
                if (ltp)
                {
                    if (y > 0) Buffer.BlockCopy(bitmap.Data, (y - 1) * bitmap.Stride, bitmap.Data, y * bitmap.Stride, bitmap.Stride);
                    continue;
                }
            }
            for (int x = 0; x < width; x++)
            {
                if (skip != null && skip.GetPixel(x, y) != 0) continue;
                int context = 0;
                for (int i = 0; i < contextBits; i++)
                {
                    int ry = y + positions[i].dy, rx = x + positions[i].dx;
                    int p = (ry < 0 || rx < 0 || rx >= width || ry >= height) ? 0 : bitmap.GetPixel(rx, ry);
                    context = (context << 1) | p;
                }
                if (mq.Decode(context) != 0) bitmap.SetPixel(x, y, 1);
            }
        }
        return bitmap;
    }
}
