// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;
using B = Tetitulz.Pdf.Core.Tests.IO.Filters.Jbig2TestEncoder.Bitmap;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Halftone regions decoded end to end (T.88 §6.6, Annex C.5), each stream encoded by
/// <see cref="Jbig2TestEncoder"/>: a pattern dictionary of four 4x4 patterns and a halftone region
/// over a grid of their indices, placed as an independent decoder places it.
/// </summary>
public class Jbig2HalftoneDecodeTests
{
    private const int PageW = 40, PageH = 24, Hpw = 4, Hph = 4, Patterns = 4;
    private const int RegionX = 2, RegionY = 1, RegionW = 22, RegionH = 18;

    private static readonly B[] Pats =
    {
        B.Random(Hpw, Hph, 3), B.Random(Hpw, Hph, 5), B.Random(Hpw, Hph, 9), B.Random(Hpw, Hph, 17),
    };

    /// <summary>
    /// A 6x4 grid over four patterns (two bit planes), on a grid skewed by HRY.
    /// <list type="bullet">
    /// <item>Plain: every cell decoded, patterns drawn with OR onto a white region.</item>
    /// <item>HENABLESKIP: cells whose pattern lies wholly outside the region are not coded.</item>
    /// <item>HDEFPIXEL with HCOMBOP XOR: a black region with each pattern drawn inverted.</item>
    /// </list>
    /// <item>GSTEMPLATE 2: the planes' AT pixel is at x+2 there, not x+3 (Annex C.5, as an independent decoder
    /// sets it); the decoder used x+3 for every template.</item>
    /// All four were refused, the first for its second plane. Before that, only plane 0 was
    /// decoded, the cells were placed without the grid vectors' 1/256 scale with the HRY signs
    /// exchanged, and the patterns went straight onto the page, unclipped by the region.
    /// </summary>
    [Theory]
    [InlineData(false, false, 0, 0)]
    [InlineData(true, false, 0, 0)]
    [InlineData(false, true, 2, 0)]
    [InlineData(false, false, 0, 2)]
    public void AFourPatternHalftoneDecodes(bool enableSkip, bool defaultPixelBlack, int hCombOp, int template)
    {
        const int hgw = 6, hgh = 4, hgx = 1 << 8, hgy = 3 << 8, hrx = 4 << 8, hry = 1 << 8;
        int X(int n, int m) => (hgx + m * hry + n * hrx) >> 8;
        int Y(int n, int m) => (hgy + m * hrx - n * hry) >> 8;

        var values = new int[hgw, hgh];
        var skip = new B(hgw, hgh);
        for (int m = 0; m < hgh; m++)
            for (int n = 0; n < hgw; n++)
            {
                values[n, m] = (n * 3 + m * 5 + n * m) % Patterns;
                bool outside = X(n, m) + Hpw <= 0 || X(n, m) >= RegionW || Y(n, m) + Hph <= 0 || Y(n, m) >= RegionH;
                if (enableSkip && outside) { skip[n, m] = 1; values[n, m] = 0; }
            }
        if (enableSkip) Assert.True(Skipped(skip, hgw, hgh), "the fixture must skip at least one cell");

        // Gray-coded planes, most significant first, on one decoder (Annex C.5).
        var e = new Jbig2TestEncoder();
        int generic = e.Block(1 << 16);
        var skipOrNull = enableSkip ? skip : null;
        for (int j = 1; j >= 0; j--)
        {
            var plane = new B(hgw, hgh);
            for (int m = 0; m < hgh; m++)
                for (int n = 0; n < hgw; n++)
                {
                    int gray = values[n, m] ^ (values[n, m] >> 1);
                    plane[n, m] = (gray >> j) & 1;
                }
            if (template == 2) e.Generic2(generic, plane, atX: 2, skipOrNull);
            else e.Generic(generic, plane, Jbig2TestEncoder.DefaultAt, skipOrNull);
        }
        byte[] coded = e.Finish();

        var body = new MemoryStream();
        Jbig2TestEncoder.U32(body, RegionW); Jbig2TestEncoder.U32(body, RegionH);
        Jbig2TestEncoder.U32(body, RegionX); Jbig2TestEncoder.U32(body, RegionY);
        body.WriteByte(0);                                               // region onto the page: OR
        body.WriteByte((byte)((template << 1) | (enableSkip ? 0x08 : 0) | (hCombOp << 4) | (defaultPixelBlack ? 0x80 : 0)));
        Jbig2TestEncoder.U32(body, hgw); Jbig2TestEncoder.U32(body, hgh);
        Jbig2TestEncoder.U32(body, hgx); Jbig2TestEncoder.U32(body, hgy);
        Jbig2TestEncoder.U16(body, hrx); Jbig2TestEncoder.U16(body, hry);
        body.Write(coded, 0, coded.Length);

        var s = new MemoryStream();
        Jbig2TestEncoder.Segment(s, 0, 48, null, Jbig2TestEncoder.PageInfo(PageW, PageH));
        Jbig2TestEncoder.Segment(s, 1, 16, null, PatternDictionary());
        Jbig2TestEncoder.Segment(s, 2, 22, new byte[] { 1 }, body.ToArray());

        var region = new B(RegionW, RegionH);
        if (defaultPixelBlack)
            for (int y = 0; y < RegionH; y++) for (int x = 0; x < RegionW; x++) region[x, y] = 1;
        for (int m = 0; m < hgh; m++)
            for (int n = 0; n < hgw; n++)
            {
                if (enableSkip && skip[n, m] != 0) continue;
                B pat = Pats[values[n, m]];
                for (int y = 0; y < Hph; y++)
                    for (int x = 0; x < Hpw; x++)
                    {
                        int rx = X(n, m) + x, ry = Y(n, m) + y;
                        if (rx < 0 || ry < 0 || rx >= RegionW || ry >= RegionH) continue;
                        region[rx, ry] = hCombOp == 2 ? region[rx, ry] ^ pat[x, y] : region[rx, ry] | pat[x, y];
                    }
            }
        var expected = new B(PageW, PageH);
        region.DrawOn(expected, RegionX, RegionY);

        Assert.Equal(expected.Packed(), Jbig2Decoder.Decode(s.ToArray(), null, out _, out _));
    }

    /// <summary>
    /// HRX is unsigned (§7.4.5.1.2.3): 130 pixels is 33280/256, above a 16-bit signed maximum. Read
    /// signed, the second cell went 126 pixels left instead of 130 right, off the region.
    /// </summary>
    [Fact]
    public void AGridVectorAbove32767_IsUnsigned()
    {
        const int width = 140, hrx = 130 << 8, hgx = 1 << 8;
        var e = new Jbig2TestEncoder();
        int generic = e.Block(1 << 16);
        var high = new B(2, 1); high[1, 0] = 1;            // values 0 and 3: gray 00 and 10
        e.Generic(generic, high);
        e.Generic(generic, new B(2, 1));
        byte[] coded = e.Finish();

        var body = new MemoryStream();
        Jbig2TestEncoder.U32(body, width); Jbig2TestEncoder.U32(body, Hph);
        Jbig2TestEncoder.U32(body, 0); Jbig2TestEncoder.U32(body, 0);
        body.WriteByte(0); body.WriteByte(0);
        Jbig2TestEncoder.U32(body, 2); Jbig2TestEncoder.U32(body, 1);
        Jbig2TestEncoder.U32(body, hgx); Jbig2TestEncoder.U32(body, 0);
        Jbig2TestEncoder.U16(body, hrx); Jbig2TestEncoder.U16(body, 0);
        body.Write(coded, 0, coded.Length);

        var s = new MemoryStream();
        Jbig2TestEncoder.Segment(s, 0, 48, null, Jbig2TestEncoder.PageInfo(width, Hph));
        Jbig2TestEncoder.Segment(s, 1, 16, null, PatternDictionary());
        Jbig2TestEncoder.Segment(s, 2, 22, new byte[] { 1 }, body.ToArray());

        var expected = new B(width, Hph);
        Pats[0].DrawOn(expected, 1, 0);
        Pats[3].DrawOn(expected, 131, 0);

        Assert.Equal(expected.Packed(), Jbig2Decoder.Decode(s.ToArray(), null, out _, out _));
    }

    /// <summary>
    /// HMMR=1 with four patterns: two MMR planes back to back, each ended by an EOFB and padded to a
    /// byte, as <see cref="CcittFaxEncoder"/> writes them. The second plane starts where the first
    /// ended. More than one MMR plane was refused.
    /// </summary>
    [Fact]
    public void AnMmrHalftoneDecodesItsPlanesInTurn()
    {
        const int hgw = 5, hgh = 3, hrx = Hpw << 8;
        var values = new int[hgw, hgh];
        for (int m = 0; m < hgh; m++) for (int n = 0; n < hgw; n++) values[n, m] = (n + 2 * m) % Patterns;

        var data = new MemoryStream();
        for (int j = 1; j >= 0; j--)
        {
            var plane = new B(hgw, hgh);
            for (int m = 0; m < hgh; m++)
                for (int n = 0; n < hgw; n++)
                    plane[n, m] = ((values[n, m] ^ (values[n, m] >> 1)) >> j) & 1;
            byte[] mmr = CcittFaxEncoder.Encode(plane.Packed(), hgw, hgh, blackIs1: true);
            data.Write(mmr, 0, mmr.Length);
        }

        var body = new MemoryStream();
        Jbig2TestEncoder.U32(body, hgw * Hpw); Jbig2TestEncoder.U32(body, hgh * Hph);
        Jbig2TestEncoder.U32(body, 0); Jbig2TestEncoder.U32(body, 0);
        body.WriteByte(0); body.WriteByte(0x01);                      // HMMR
        Jbig2TestEncoder.U32(body, hgw); Jbig2TestEncoder.U32(body, hgh);
        Jbig2TestEncoder.U32(body, 0); Jbig2TestEncoder.U32(body, 0);
        Jbig2TestEncoder.U16(body, hrx); Jbig2TestEncoder.U16(body, 0);
        data.WriteTo(body);

        var s = new MemoryStream();
        Jbig2TestEncoder.Segment(s, 0, 48, null, Jbig2TestEncoder.PageInfo(hgw * Hpw, hgh * Hph));
        Jbig2TestEncoder.Segment(s, 1, 16, null, PatternDictionary());
        Jbig2TestEncoder.Segment(s, 2, 22, new byte[] { 1 }, body.ToArray());

        var expected = new B(hgw * Hpw, hgh * Hph);
        for (int m = 0; m < hgh; m++)
            for (int n = 0; n < hgw; n++)
                Pats[values[n, m]].DrawOn(expected, n * Hpw, m * Hph);

        Assert.Equal(expected.Packed(), Jbig2Decoder.Decode(s.ToArray(), null, out _, out _));
    }

    /// <summary>Whether any cell is skipped: the skip row must actually exercise HSKIP.</summary>
    private static bool Skipped(B skip, int w, int h)
    {
        for (int m = 0; m < h; m++) for (int n = 0; n < w; n++) if (skip[n, m] != 0) return true;
        return false;
    }

    /// <summary>
    /// The four patterns as one collective bitmap (§6.7.5): a generic region 16 wide, template 0,
    /// AT1 at (-HDPW, 0) and the other AT pixels at their defaults.
    /// </summary>
    private static byte[] PatternDictionary()
    {
        var collective = new B(Hpw * Patterns, Hph);
        for (int i = 0; i < Patterns; i++) Pats[i].DrawOn(collective, i * Hpw, 0);

        var e = new Jbig2TestEncoder();
        e.Generic(e.Block(1 << 16), collective, new (int, int)[] { (-Hpw, 0), (-3, -1), (2, -2), (-2, -2) });
        byte[] coded = e.Finish();

        var body = new MemoryStream();
        body.WriteByte(0);                                // HDMMR 0, HDTEMPLATE 0
        body.WriteByte(Hpw); body.WriteByte(Hph);
        Jbig2TestEncoder.U32(body, Patterns - 1);         // GRAYMAX
        body.Write(coded, 0, coded.Length);
        return body.ToArray();
    }
}
