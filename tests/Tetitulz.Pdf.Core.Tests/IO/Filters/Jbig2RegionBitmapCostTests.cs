// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Util;
using Xunit;
using static Tetitulz.Pdf.Core.Tests.IO.Filters.Jbig2TestEncoder;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// What a page's or a region's own bitmap costs (T.88 §7.4.8, §6.4.5, §6.6.5): allocated and filled
/// with its default pixel whatever the segment then draws, it is charged to the work budget at
/// <see cref="Jbig2Decoder.RegionBitmapPixelsPerUnit"/> before it is made. The streams are segments of
/// about thirty bytes each: page-information segments, text regions of no instances and halftone
/// regions whose one cell falls outside them, each region placed off the page.
/// </summary>
public class Jbig2RegionBitmapCostTests
{
    public enum Kind { TextRegion, HalftoneRegion, PageInformation }

    /// <summary>
    /// Segments that draw nothing, each declaring an 8 192 × 8 192 bitmap of black default pixels:
    /// uncharged, a thousand of them allocate eight gigabytes for a unit or two each; charged, a
    /// budget of ten bitmaps refuses the eleventh, having allocated no more than those.
    /// </summary>
    [Theory]
    [InlineData(Kind.TextRegion)]
    [InlineData(Kind.HalftoneRegion)]
    [InlineData(Kind.PageInformation)]
    public void Hostile_ManyRegionBitmapsThatDrawNothing_AreRefusedByTheBudget(Kind kind)
    {
        const int side = 8192;
        byte[] stream = Stream(kind, 1000, side);
        long bitmap = (long)side * side / Jbig2Decoder.RegionBitmapPixelsPerUnit;

        long before = GC.GetAllocatedBytesForCurrentThread();
        var refusal = Assert.Throws<WorkBudgetExceededException>(() =>
            Jbig2Decoder.Decode(stream, null, new WorkBudget(10 * bitmap + bitmap / 2), out _, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("budget", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(allocated, 0, 11L * side * side / 8 + (16L << 20));
    }

    /// <summary>
    /// The acceptance control: the same kind of stream, three bitmaps long, decodes under a budget of
    /// four, and the page it leaves is the 64 × 64 page the stream starts with, or for page
    /// information, the last page declared.
    /// </summary>
    [Theory]
    [InlineData(Kind.TextRegion, 64)]
    [InlineData(Kind.HalftoneRegion, 64)]
    [InlineData(Kind.PageInformation, 8192)]
    public void RegionBitmapsWithinTheBudget_Decode(Kind kind, int pageSide)
    {
        const int side = 8192;
        long bitmap = (long)side * side / Jbig2Decoder.RegionBitmapPixelsPerUnit;

        Jbig2Decoder.Decode(Stream(kind, 3, side), null, new WorkBudget(4 * bitmap), out int width, out int height);

        Assert.Equal(pageSide, width);
        Assert.Equal(pageSide, height);
    }

    /// <summary>
    /// Each bitmap spends its measured price, pinned through the budget's own evidence: one unit and
    /// a unit per 256 pixels, beside what its segment otherwise costs — a text region's draw onto the
    /// page (one unit, off the page); a halftone region's one-pixel plane (the generic region's setup
    /// and its pixel), its cell's draw and its own draw onto the page. The stream's 64 × 64 page is a
    /// bitmap too, and the halftone stream's one-pattern dictionary its decode and its split.
    /// </summary>
    [Theory]
    [InlineData(Kind.TextRegion, 1)]
    [InlineData(Kind.HalftoneRegion, 1 + 1 + Jbig2Decoder.GenericRegionSetupUnits + 1)]
    [InlineData(Kind.PageInformation, 0)]
    public void EachRegionBitmap_IsChargedItsPrice(Kind kind, int segmentBeside)
    {
        Assert.Equal(256, Jbig2Decoder.RegionBitmapPixelsPerUnit);
        const int side = 512, count = 5;
        long page = 1 + 64 * 64 / 256;
        long dictionary = kind == Kind.HalftoneRegion ? Jbig2Decoder.GenericRegionSetupUnits + 1 + 1 : 0;
        long bitmap = 1 + side * side / 256;

        Jbig2Decoder.Decode(Stream(kind, count, side), null, out _, out _);

        Assert.Equal(page + dictionary + count * (bitmap + segmentBeside), Jbig2Decoder.LastWorkUnits);
    }

    /// <summary>
    /// A page of unknown height grows geometrically, and each grown page is a region bitmap at the
    /// same price: a 64-wide page starting a row high and then reached by a region ending at row 100
    /// grows once, to 100 rows (half its height and 64 more, but no less than the region needs).
    /// </summary>
    [Fact]
    public void AGrownPageOfUnknownHeight_IsChargedItsBitmap()
    {
        var s = new MemoryStream();
        var info = new MemoryStream();
        U32(info, 64); U32(info, -1); U32(info, 0); U32(info, 0); info.WriteByte(0); U16(info, 0);
        Segment(s, 0, 48, null, info.ToArray());
        Segment(s, 1, 6, null, TextRegion(64, 100, x: 0, y: 0));

        Jbig2Decoder.Decode(s.ToArray(), null, out _, out int height);

        long first = 1 + 64 * 1 / 256, grown = 1 + 64 * 100 / 256, region = 1 + 64 * 100 / 256, draw = 1 + 64 * 100;
        Assert.Equal(100, height);
        Assert.Equal(first + grown + region + draw, Jbig2Decoder.LastWorkUnits);
    }

    private static byte[] Stream(Kind kind, int count, int side)
    {
        var s = new MemoryStream();
        int number = 0;
        Segment(s, number++, 48, null, PageInfo(64, 64));
        if (kind == Kind.HalftoneRegion)
        {
            var dictionary = new MemoryStream();
            dictionary.WriteByte(0); dictionary.WriteByte(1); dictionary.WriteByte(1); U32(dictionary, 0);   // one 1 × 1 pattern
            Segment(s, number++, 16, null, dictionary.ToArray());
        }
        for (int i = 0; i < count; i++)
        {
            switch (kind)
            {
                case Kind.TextRegion:
                    Segment(s, number++, 6, null, TextRegion(side, side, x: 1 << 30, y: 1 << 30, defaultOnes: true));
                    break;
                case Kind.HalftoneRegion:
                    HalftoneSegment(s, number++, side);
                    break;
                default:
                    var info = new MemoryStream();
                    U32(info, side); U32(info, side); U32(info, 0); U32(info, 0);
                    info.WriteByte(0x04); U16(info, 0);                       // default pixel 1
                    Segment(s, number++, 48, null, info.ToArray());
                    break;
            }
        }
        return s.ToArray();
    }

    /// <summary>A text region of no instances, its SBDEFPIXEL as asked.</summary>
    private static byte[] TextRegion(int w, int h, int x, int y, bool defaultOnes = false)
    {
        var body = new MemoryStream();
        TextRegionHeader(body, w, h, refine: false, instances: 0, layout: (1 << 4) | (defaultOnes ? 1 << 9 : 0), x: x, y: y);
        return body.ToArray();
    }

    /// <summary>
    /// A halftone region over the stream's pattern dictionary (segment 1), off the page, HDEFPIXEL 1,
    /// its one grid cell placed outside it. The referred-to segment number is as wide as §7.2.5 makes
    /// it for this segment's own number.
    /// </summary>
    private static void HalftoneSegment(Stream s, int number, int side)
    {
        var body = new MemoryStream();
        U32(body, side); U32(body, side); U32(body, 1 << 30); U32(body, 1 << 30); body.WriteByte(0);
        body.WriteByte(0x80);                                         // HDEFPIXEL 1, template 0, arithmetic
        U32(body, 1); U32(body, 1); U32(body, -(1 << 20)); U32(body, -(1 << 20)); U16(body, 0); U16(body, 0);
        byte[] b = body.ToArray();

        U32(s, number);
        s.WriteByte(22);                                              // immediate halftone region
        s.WriteByte(1 << 5);                                          // one referred-to segment
        if (number <= 256) s.WriteByte(1);
        else if (number <= 65536) U16(s, 1);
        else U32(s, 1);
        s.WriteByte(1);
        U32(s, b.Length);
        s.Write(b, 0, b.Length);
    }
}
