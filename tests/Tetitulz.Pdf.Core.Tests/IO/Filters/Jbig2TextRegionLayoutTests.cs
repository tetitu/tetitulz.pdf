// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;
using B = Tetitulz.Pdf.Core.Tests.IO.Filters.Jbig2TestEncoder.Bitmap;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// A text region's layout flags (T.88 §6.4.5): TRANSPOSED in each corner, and the region bitmap
/// that SBDEFPIXEL fills and SBCOMBOP draws onto, decoded from streams <see cref="Jbig2TestEncoder"/>
/// writes. The expected placement here is an independent decoder's (3c.vi to 3c.x), written out per case.
/// </summary>
public class Jbig2TextRegionLayoutTests
{
    private const int PageW = 40, PageH = 32;

    private static readonly B Sym0 = B.Random(6, 8, 31);
    private static readonly B Sym1 = B.Random(5, 7, 37);

    /// <summary>
    /// TRANSPOSED=1: S runs down and T across. Two instances in one strip at T 10, S starting at 2,
    /// with IDS 3 between. Each corner is a row; a RIGHT corner puts the symbol's right column at T,
    /// and S always advances by the symbol's height − 1. The decoder refused the flag.
    /// </summary>
    [Theory]
    [InlineData(0)]   // BOTTOMLEFT
    [InlineData(1)]   // TOPLEFT
    [InlineData(2)]   // BOTTOMRIGHT
    [InlineData(3)]   // TOPRIGHT
    public void ATransposedRegionPlacesEachInstanceDownTheStrip(int refCorner)
    {
        const int t = 10, firstS = 2, ds = 3;
        bool right = refCorner >= 2;

        var e = new Jbig2TestEncoder();
        int iadt = e.Block(512), iafs = e.Block(512), iads = e.Block(512), iaid = e.Block(2);
        e.Int(iadt, 0); e.Int(iadt, t); e.Int(iafs, firstS);
        e.Id(iaid, 1, 0); e.Int(iads, ds);
        e.Id(iaid, 1, 1); e.Int(iads, null);
        byte[] coded = e.Finish();

        var region = new MemoryStream();
        Jbig2TestEncoder.TextRegionHeader(region, PageW, PageH, refine: false, instances: 2,
            layout: (refCorner << 4) | (1 << 6));
        region.Write(coded, 0, coded.Length);

        var expected = new B(PageW, PageH);
        int s0 = firstS;
        Sym0.DrawOn(expected, right ? t - Sym0.W + 1 : t, s0);
        int s1 = s0 + Sym0.H - 1 + ds;
        Sym1.DrawOn(expected, right ? t - Sym1.W + 1 : t, s1);

        Assert.Equal(expected.Packed(), Decode(region.ToArray()));
    }

    /// <summary>
    /// SBDEFPIXEL=1 with SBCOMBOP XOR, the region 20x12 at (4, 3): the region starts black, each
    /// symbol inverts what is under it, and the second instance, half past the region's right edge,
    /// is clipped by it. The flag was refused; the operator was replaced by the segment's; and
    /// instances were drawn on the page directly, unclipped.
    /// </summary>
    [Fact]
    public void ABlackRegionTakesItsSymbolsByItsOwnOperator()
    {
        const int rw = 20, rh = 12, rx = 4, ry = 3;

        var e = new Jbig2TestEncoder();
        int iadt = e.Block(512), iafs = e.Block(512), iads = e.Block(512), iaid = e.Block(2);
        e.Int(iadt, 0); e.Int(iadt, 1); e.Int(iafs, 2);
        e.Id(iaid, 1, 0); e.Int(iads, 9);               // S 2 + 5 + 9 = 16: the second runs past 20
        e.Id(iaid, 1, 1); e.Int(iads, null);
        byte[] coded = e.Finish();

        var region = new MemoryStream();
        Jbig2TestEncoder.TextRegionHeader(region, rw, rh, refine: false, instances: 2,
            layout: (1 << 4) | (2 << 7) | (1 << 9), x: rx, y: ry);   // TOPLEFT, XOR, black
        region.Write(coded, 0, coded.Length);

        var regionBitmap = new B(rw, rh);
        for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++) regionBitmap[x, y] = 1;
        void Xor(B symbol, int x0, int y0)
        {
            for (int y = 0; y < symbol.H; y++)
                for (int x = 0; x < symbol.W; x++)
                    if (x0 + x < rw && y0 + y < rh)
                        regionBitmap[x0 + x, y0 + y] ^= symbol[x, y];
        }
        Xor(Sym0, 2, 1);
        Xor(Sym1, 16, 1);
        var expected = new B(PageW, PageH);
        regionBitmap.DrawOn(expected, rx, ry);

        Assert.Equal(expected.Packed(), Decode(region.ToArray()));
    }

    private static byte[] Decode(byte[] region)
    {
        var s = new MemoryStream();
        Jbig2TestEncoder.Segment(s, 0, 48, null, Jbig2TestEncoder.PageInfo(PageW, PageH));
        Jbig2TestEncoder.Segment(s, 1, 0, null, Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 }));
        Jbig2TestEncoder.Segment(s, 2, 6, new byte[] { 1 }, region);
        return Jbig2Decoder.Decode(s.ToArray(), null, out _, out _);
    }
}
