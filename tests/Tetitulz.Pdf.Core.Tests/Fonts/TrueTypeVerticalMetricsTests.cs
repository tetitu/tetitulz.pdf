// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="TrueTypeFontProgram.TryGetVerticalAdvance"/> (the <c>vhea</c>/<c>vmtx</c> read PDF/UA-2
/// §8.4.5.6 compares against) and <see cref="CidVerticalMetrics.TryFind"/> (the <c>/W2</c> lookup the
/// renderers and that check share).
/// </summary>
public class TrueTypeVerticalMetricsTests
{
    [Fact]
    public void VmtxAdvanceHeights_AreReadPerGlyph()
    {
        TrueTypeFontProgram program = TrueTypeFontProgram.Parse(MinimalTrueType.BuildSymbolicWithVerticalMetrics("AB", 950));

        Assert.True(program.HasVerticalMetrics);
        Assert.True(program.TryGetVerticalAdvance(1, out int advance));
        Assert.Equal(950, advance);
        Assert.False(program.TryGetVerticalAdvance(3, out _));   // three glyphs: .notdef, A, B
        Assert.False(program.TryGetVerticalAdvance(-1, out _));
    }

    [Fact]
    public void AProgramWithoutVheaAndVmtx_HasNoVerticalMetrics()
    {
        TrueTypeFontProgram program = TrueTypeFontProgram.Parse(MinimalTrueType.BuildSymbolic("AB"));

        Assert.False(program.HasVerticalMetrics);
        Assert.False(program.TryGetVerticalAdvance(1, out _));
    }

    [Fact]
    public void ATruncatedVmtx_CostsTheVerticalMetricsNotTheProgram()
    {
        // Three glyphs need twelve bytes; five cannot hold them.
        TrueTypeFontProgram program = TrueTypeFontProgram.Parse(
            MinimalTrueType.BuildSymbolicWithVerticalMetrics("AB", 950, vmtxBytes: 5));

        Assert.NotNull(program);
        Assert.False(program.HasVerticalMetrics);
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, program.GetAdvance(1));
    }

    private static PdfArray Numbers(params object[] items)
    {
        var array = new PdfArray();
        foreach (object item in items)
            array.Add(item is PdfArray nested ? nested : new PdfNumber(System.Convert.ToDouble(item)));
        return array;
    }

    [Fact]
    public void W2Lookup_ReadsBothFormsAndStepsFiveOverARange()
    {
        // 10 [ -1000 250 880  -900 300 870 ]   20 25 -800 400 860   30 [ -700 1 2 ]
        PdfArray w2 = Numbers(10, Numbers(-1000, 250, 880, -900, 300, 870), 20, 25, -800, 400, 860, 30, Numbers(-700, 1, 2));

        Assert.True(CidVerticalMetrics.TryFind(w2, 11, out float w1y, out float vx, out float vy));
        Assert.Equal((-900f, 300f, 870f), (w1y, vx, vy));
        Assert.True(CidVerticalMetrics.TryFind(w2, 23, out w1y, out vx, out vy));
        Assert.Equal((-800f, 400f, 860f), (w1y, vx, vy));
        // Only found when the range form advanced by five numbers, not three.
        Assert.True(CidVerticalMetrics.TryFind(w2, 30, out w1y, out _, out _));
        Assert.Equal(-700f, w1y);
        Assert.False(CidVerticalMetrics.TryFind(w2, 12, out _, out _, out _));
    }

    /// <summary>
    /// Wave 17 R13.4: every element is read through the caller's resolver — the displacement of
    /// each form sits behind a reference here. Without one, the reference is not a number and the
    /// triplet falls to the default; the range form (five numbers, then a reference where <c>w1y</c>
    /// should be) is still found by its CIDs and falls to the default too.
    /// </summary>
    [Fact]
    public void W2Lookup_ReadsEveryElementThroughTheResolver()
    {
        var objects = new Dictionary<int, PdfObject> { [7] = new PdfNumber(-900), [8] = new PdfNumber(-800) };
        PdfObject Resolve(PdfObject o)
            => o is PdfReference r && objects.TryGetValue(r.ReferenceObjectNumber, out PdfObject target) ? target : o;

        // 10 [ 7 0 R 250 880 ]   20 25 8 0 R 400 860
        var triplet = new PdfArray();
        triplet.Add(new PdfReference(7, 0)); triplet.Add(new PdfNumber(250)); triplet.Add(new PdfNumber(880));
        var w2 = new PdfArray();
        w2.Add(new PdfNumber(10)); w2.Add(triplet);
        w2.Add(new PdfNumber(20)); w2.Add(new PdfNumber(25)); w2.Add(new PdfReference(8, 0));
        w2.Add(new PdfNumber(400)); w2.Add(new PdfNumber(860));

        Assert.True(CidVerticalMetrics.TryFind(w2, 10, out float w1y, out float vx, out float vy, Resolve));
        Assert.Equal((-900f, 250f, 880f), (w1y, vx, vy));
        Assert.True(CidVerticalMetrics.TryFind(w2, 22, out w1y, out vx, out vy, Resolve));
        Assert.Equal((-800f, 400f, 860f), (w1y, vx, vy));

        Assert.True(CidVerticalMetrics.TryFind(w2, 10, out w1y, out _, out _));
        Assert.Equal(-1000f, w1y);
        Assert.True(CidVerticalMetrics.TryFind(w2, 22, out w1y, out _, out _));
        Assert.Equal(-1000f, w1y);
    }

    /// <summary>
    /// Wave 17 R2.10: a CID past <c>int</c> saturates (<c>IntValue</c>) where the <c>(int)LongValue</c>
    /// cast wrapped 2^32 + 10 onto 10 and handed CID 10 the metrics of a range that does not cover it;
    /// a genuine 10 still finds them.
    /// </summary>
    [Fact]
    public void W2Lookup_ACidPastInt_DoesNotWrapOntoASmallOne()
    {
        var wrapped = new PdfArray();
        wrapped.Add(new PdfNumber(4294967306.0));   // 2^32 + 10
        wrapped.Add(Numbers(-900, 250, 880));
        Assert.False(CidVerticalMetrics.TryFind(wrapped, 10, out _, out _, out _));

        var genuine = new PdfArray();
        genuine.Add(new PdfNumber(10));
        genuine.Add(Numbers(-900, 250, 880));
        Assert.True(CidVerticalMetrics.TryFind(genuine, 10, out float w1y, out _, out _));
        Assert.Equal(-900f, w1y);
    }

    /// <summary>
    /// Wave 17 R2.10, the other half: the triplet offset <c>(cid − c1) × 3</c> is formed in
    /// <c>long</c>. In <c>int</c>, CID 0x55555556 against a start of 0 is 0x100000002 — wrapped to 2,
    /// which is a real offset into a six-number array, so a CID a billion past the entry's range took
    /// the entry's second triplet. The control is the CID that second triplet belongs to.
    /// </summary>
    [Fact]
    public void W2Lookup_AWideGapDoesNotWrapTheTripletOffsetOntoARealOne()
    {
        var w2 = new PdfArray();
        w2.Add(new PdfNumber(0));
        w2.Add(Numbers(-900, 250, 880, -700, 300, 870));

        Assert.False(CidVerticalMetrics.TryFind(w2, 0x55555556, out _, out _, out _));

        Assert.True(CidVerticalMetrics.TryFind(w2, 1, out float w1y, out _, out _));
        Assert.Equal(-700f, w1y);
    }

    /// <summary>
    /// The index answers every CID as the scan it replaced did — the first entry in array order that
    /// covers it — on random arrays of both forms, overlapping entries, stray elements and a range
    /// written backwards. The scan was run for every glyph shown, glyphs × entries.
    /// </summary>
    [Fact]
    public void W2Index_AnswersAsTheLinearScan_OnRandomArrays()
    {
        int compared = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var random = new System.Random(seed);
            var w2 = new PdfArray();
            int entries = random.Next(0, 12);
            for (int e = 0; e < entries; e++)
            {
                int start = random.Next(0, 60);
                int roll = random.Next(10);
                if (roll < 4)
                {
                    var triplets = new PdfArray();
                    int count = random.Next(0, 5) * 3 + random.Next(0, 3);
                    for (int t = 0; t < count; t++) triplets.Add(new PdfNumber(random.Next(-1000, 1000)));
                    w2.Add(new PdfNumber(start)); w2.Add(triplets);
                }
                else if (roll < 8)
                {
                    int end = start + random.Next(-3, 20);
                    w2.Add(new PdfNumber(start)); w2.Add(new PdfNumber(end));
                    w2.Add(new PdfNumber(-random.Next(500, 1000))); w2.Add(new PdfNumber(random.Next(0, 500))); w2.Add(new PdfNumber(880));
                }
                else w2.Add(roll == 8 ? (PdfObject)new PdfName("Junk") : new PdfNumber(start));
            }

            var index = CidVerticalMetrics.Index.Read(w2);
            for (int cid = -2; cid < 90; cid++)
            {
                bool scanned = LinearScan(w2, cid, out float sw, out float sx, out float sy);
                bool indexed = index.TryFind(cid, out float iw, out float ix, out float iy);
                Assert.True(scanned == indexed, $"seed {seed}, CID {cid}");
                if (scanned) Assert.True((sw, sx, sy) == (iw, ix, iy), $"seed {seed}, CID {cid}");
                compared++;
            }
        }
        Assert.Equal(300 * 92, compared);
    }

    [Fact]
    public void AdversarialW2OfManyRanges_IsHeldInOneStretchARange()
    {
        // Fifty thousand two-CID ranges: one stretch each, found by a binary search.
        const int ranges = 50_000;
        var w2 = new PdfArray();
        for (int i = 0; i < ranges; i++)
        {
            w2.Add(new PdfNumber(i * 3)); w2.Add(new PdfNumber(i * 3 + 1));
            w2.Add(new PdfNumber(-(i % 1000))); w2.Add(new PdfNumber(0)); w2.Add(new PdfNumber(880));
        }

        var index = CidVerticalMetrics.Index.Read(w2);

        Assert.Equal(ranges, index.Stretches);
        Assert.True(index.TryFind(3 * 40_000 + 1, out float w1y, out _, out _));
        Assert.Equal(-(40_000 % 1000), w1y);
        Assert.False(index.TryFind(3 * 40_000 + 2, out _, out _, out _));
    }

    // The scan the index replaced, kept here as its oracle.
    private static bool LinearScan(PdfArray w2, int cid, out float w1y, out float v1x, out float v1y)
    {
        w1y = 0f; v1x = 0f; v1y = 0f;
        int i = 0;
        while (i + 1 < w2.Count)
        {
            int c1 = (w2[i] as PdfNumber)?.IntValue ?? 0;
            var second = w2[i + 1];
            if (second is PdfArray arr)
            {
                long tripletLong = ((long)cid - c1) * 3;
                if (tripletLong >= 0 && tripletLong + 2 < arr.Count)
                {
                    int triplet = (int)tripletLong;
                    w1y = (float)((arr[triplet] as PdfNumber)?.DoubleValue ?? -1000);
                    v1x = (float)((arr[triplet + 1] as PdfNumber)?.DoubleValue ?? 0);
                    v1y = (float)((arr[triplet + 2] as PdfNumber)?.DoubleValue ?? 880);
                    return true;
                }
                i += 2;
            }
            else if (second is PdfNumber c2Num && i + 4 < w2.Count)
            {
                int c2 = c2Num.IntValue;
                if (cid >= c1 && cid <= c2)
                {
                    w1y = (float)((w2[i + 2] as PdfNumber)?.DoubleValue ?? -1000);
                    v1x = (float)((w2[i + 3] as PdfNumber)?.DoubleValue ?? 0);
                    v1y = (float)((w2[i + 4] as PdfNumber)?.DoubleValue ?? 880);
                    return true;
                }
                i += 5;
            }
            else { i++; }
        }
        return false;
    }

    [Fact]
    public void W2Lookup_ASecondRangeIsFoundOnlyByStepping5()
    {
        // 20 25 -800 400 860   40 45 -600 500 850 — stepping three from the first range lands on
        // "400 860 …", and the scan never realigns on "40 45".
        PdfArray w2 = Numbers(20, 25, -800, 400, 860, 40, 45, -600, 500, 850);

        Assert.True(CidVerticalMetrics.TryFind(w2, 42, out float w1y, out _, out _));
        Assert.Equal(-600f, w1y);
    }
}
