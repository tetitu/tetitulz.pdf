// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Pixel-asserting tests for three things otherwise covered only at a lower level: an
/// annotation's <c>/AS</c> appearance-state selection, a CCITT image rendered end to end, and a
/// JBIG2 arithmetic generic region rendered end to end.
///
/// <para>A decode that works and a decode that reaches the page are different claims. Handing
/// T.88's polarity (1 = black) straight to a DeviceGray image, where 0 is black, would render
/// every JBIG2 image in negative while every filter-level test passed.</para>
/// </summary>
public sealed class RemainingCoverageTests
{
    // ====================================================================
    //  Annotation /AS — which sub-appearance is drawn
    // ====================================================================

    /// <summary>
    /// §12.5.5: when <c>/AP /N</c> is a sub-dictionary, <c>/AS</c> names the entry to use. Asserted
    /// both ways round, so neither result can come from a renderer that always picks the same one
    /// (or the first).
    /// </summary>
    [Theory]
    [InlineData("/On",  true)]   // green appearance
    [InlineData("/Off", false)]  // red appearance
    public void AnnotationAppearanceState_SelectsTheNamedSubAppearance(string state, bool expectGreen)
    {
        using var bmp = PixelAssert.Render(WidgetWithAppearanceStates(state));
        var c = PixelAssert.At(bmp, 50, 50);

        if (expectGreen)
        {
            Assert.True(c.Green > 200 && c.Red < 80, $"/AS {state} should draw the green /On appearance, got {c}");
        }
        else
        {
            Assert.True(c.Red > 200 && c.Green < 80, $"/AS {state} should draw the red /Off appearance, got {c}");
        }
    }

    /// <summary>
    /// An <c>/AS</c> naming an entry the sub-dictionary does not contain draws nothing: the state the
    /// widget is in has no appearance. A check box's off appearance is optional (ISO 32000-2
    /// §12.7.5.2.3) and producers leave it out, so falling back to another entry drew every unchecked
    /// box checked. The fallback stays for a widget with no <c>/AS</c> (WidgetMissingStateAppearanceTests).
    /// </summary>
    [Fact]
    public void AnnotationAppearanceState_NamingAMissingEntry_DrawsNothing()
    {
        using var bmp = PixelAssert.Render(WidgetWithAppearanceStates("/Nonexistent"));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Green > 200 && c.Red > 200 && c.Blue > 200,
            $"an unmatched /AS should draw no appearance, got {c}");
    }

    // ====================================================================
    //  CCITT, end to end
    // ====================================================================

    /// <summary>
    /// A Group 4 image decoded by the real filter and drawn onto the page.
    /// </summary>
    /// <remarks>
    /// The filter has thorough unit tests and the one render test that mentioned CCITT registered a
    /// no-op filter to bypass the decode, so nothing checked that a decoded CCITT image reaches the
    /// page with the right pixels — the case where the filter returns its <b>compressed input</b> on
    /// failure (which it does, by design) would have looked identical.
    /// </remarks>
    [Fact]
    public void CcittImage_DecodesAndPaintsItsPixels()
    {
        using var bmp = PixelAssert.Render(CcittImagePdf());

        // Every row of the 16x4 source is 8 white then 8 black, scaled over the whole page.
        var left  = PixelAssert.At(bmp, 25, 50);
        var right = PixelAssert.At(bmp, 75, 50);

        Assert.True(left.Red > 200 && left.Green > 200 && left.Blue > 200,
            $"the white half decoded wrong, got {left}");
        Assert.True(right.Red < 60 && right.Green < 60 && right.Blue < 60,
            $"the black half decoded wrong, got {right}");
    }

    /// <summary>
    /// The same image with <c>/BlackIs1 true</c> inverts, which a renderer that ignored the decode
    /// parameters — or that painted the compressed bytes — could not produce.
    /// </summary>
    [Fact]
    public void CcittImage_BlackIs1_InvertsTheResult()
    {
        using var normal   = PixelAssert.Render(CcittImagePdf());
        using var inverted = PixelAssert.Render(CcittImagePdf(blackIs1: true));

        var normalLeft   = PixelAssert.At(normal, 25, 50);
        var invertedLeft = PixelAssert.At(inverted, 25, 50);

        Assert.True(normalLeft.Red > 200, $"expected white on the left by default, got {normalLeft}");
        Assert.True(invertedLeft.Red < 60, $"/BlackIs1 did not invert, got {invertedLeft}");
    }

    // ====================================================================
    //  JBIG2, end to end
    // ====================================================================

    /// <summary>
    /// An arithmetic-coded JBIG2 generic region (template 0) decoded by the real filter and drawn
    /// onto the page as an image XObject — the last entry on GAPS.md §2's "no pixel-asserting
    /// test" list.
    /// </summary>
    /// <remarks>
    /// <para><b>The four probes are chosen so nothing but a real decode satisfies them.</b>
    /// <see cref="Jbig2DecodeFilter"/> returns its <i>compressed input</i> when a decode fails, and
    /// the 1-bpc raster path accepts any buffer at least <c>ceil(w/8)*h</c> long
    /// (<c>ContentStreamInterpreter.Images.cs</c>), so a failed decode paints the segment stream as
    /// pixels instead of vanishing. That stream begins <c>00 00 00 00 30 00 …</c>, so the
    /// passthrough's row 0 is uniformly black — which the (2,0) probe rejects. The probes also form
    /// a 2×2 grid with both colours in every row and every column, so a vertical flip, a horizontal
    /// flip, a transpose or a constant fill each fail at least one of them.</para>
    ///
    /// <para><b>The expected pattern is pinned, not computed.</b> An arithmetic decoder's output for
    /// a chosen payload is not analytically obvious; it was read off the decoder once and written
    /// down here. Deriving it inside the assertion would make the test agree with the decoder
    /// instead of with the page, which is banned pattern 3 with an extra step. If a future
    /// correction to the decoder moves these bits, re-baseline deliberately — do not relax the
    /// assertion.</para>
    /// </remarks>
    [Fact]
    public void Jbig2ArithmeticGenericRegion_DecodesAndPaintsItsPixels()
    {
        using var bmp = PixelAssert.Render(Jbig2ImagePdf(Jbig2ArithPayload));

        // The direct evidence that the managed decoder ran, rather than the filter handing back
        // the compressed bytes: the slot is [ThreadStatic] and cleared at the top of every Decode,
        // and the render is synchronous on this thread.
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);

        // 16x16 source over a 128pt page at 72 dpi → 8 device px per source pixel; probe the
        // centre of each 8x8 block. Image row 0 is at the TOP of the unit square (§8.9.5.2).
        var white0  = bmp.GetPixel(2 * 8 + 4,  0 * 8 + 4);   // row 0 col 2  — T.88 bit 0
        var black0  = bmp.GetPixel(14 * 8 + 4, 0 * 8 + 4);   // row 0 col 14 — T.88 bit 1
        var black2  = bmp.GetPixel(2 * 8 + 4,  2 * 8 + 4);   // row 2 col 2  — T.88 bit 1
        var white2  = bmp.GetPixel(8 * 8 + 4,  2 * 8 + 4);   // row 2 col 8  — T.88 bit 0

        Assert.True(white0.Red > 200 && white0.Green > 200 && white0.Blue > 200,
            $"row 0 col 2 is unmarked in the decoded region, so it must be white; got {white0}");
        Assert.True(black0.Red < 60 && black0.Green < 60 && black0.Blue < 60,
            $"row 0 col 14 is the only marked pixel in row 0, so it must be black; got {black0}");
        Assert.True(black2.Red < 60 && black2.Green < 60 && black2.Blue < 60,
            $"row 2 col 2 is marked in the decoded region, so it must be black; got {black2}");
        Assert.True(white2.Red > 200 && white2.Green > 200 && white2.Blue > 200,
            $"row 2 col 8 is unmarked in the decoded region, so it must be white; got {white2}");
    }

    /// <summary>
    /// Flipping one bit of the arithmetic payload repaints the region — which is what proves the
    /// probes above are reading the decode and not the fixture's geometry.
    /// </summary>
    /// <remarks>
    /// An MQ decoder has no synchronisation points, so one changed byte diverges the whole page
    /// from row 0 onward. Both renders must still be real decodes: if the altered payload merely
    /// made the filter fall back, "the pixels changed" would prove nothing about the decoder.
    /// </remarks>
    [Fact]
    public void Jbig2ArithmeticGenericRegion_OneChangedPayloadByte_RepaintsTheRegion()
    {
        var altered = (byte[])Jbig2ArithPayload.Clone();
        altered[0] ^= 0x80;                       // 0x84 -> 0x04

        using var original = PixelAssert.Render(Jbig2ImagePdf(Jbig2ArithPayload));
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
        using var changed  = PixelAssert.Render(Jbig2ImagePdf(altered));
        Assert.Null(Jbig2DecodeFilter.LastFallbackReason);

        // Row 0 col 2: unmarked under the original payload, marked under the altered one.
        var before = original.GetPixel(2 * 8 + 4, 4);
        var after  = changed.GetPixel(2 * 8 + 4, 4);

        Assert.True(before.Red > 200 && before.Green > 200 && before.Blue > 200,
            $"expected white at row 0 col 2 for the pinned payload; got {before}");
        Assert.True(after.Red < 60 && after.Green < 60 && after.Blue < 60,
            $"the altered payload must decode to a marked pixel there; got {after}");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>
    /// A Widget whose <c>/AP /N</c> holds two named appearances — a green form and a red one — over
    /// the whole page, selected by <paramref name="state"/>.
    /// </summary>
    private static byte[] WidgetWithAppearanceStates(string state)
    {
        const string on  = "0 1 0 rg 0 0 100 100 re f";
        const string off = "1 0 0 rg 0 0 100 100 re f";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            Stream("", ""),   // nothing on the page itself, so all ink comes from the annotation
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 100 100] /F 4" +
            $" /AS {state} /AP << /N << /On 6 0 R /Off 7 0 R >> >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]", on),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]", off));
    }

    /// <summary>
    /// A 16x4 Group 4 image — every row 8 white then 8 black — filling the page.
    /// </summary>
    /// <remarks>
    /// The codes are <c>001</c> (horizontal) + <c>10011</c> (white 8) + <c>000101</c> (black 8) for
    /// row 0, then <c>1 1</c> (V0 twice) for each of rows 1-3, zero-padded: <c>33 17 F0</c>. Hex is
    /// wrapped in ASCIIHexDecode so the fixture stays an ASCII string.
    /// </remarks>
    private static byte[] CcittImagePdf(bool blackIs1 = false)
    {
        const string hex = "3317F0>";
        string parms = "[ null << /K -1 /Columns 16 /Rows 4" +
                       (blackIs1 ? " /BlackIs1 true" : "") + " >> ]";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 16 /Height 4 /ColorSpace /DeviceGray" +
                   " /BitsPerComponent 1 /Filter [/ASCIIHexDecode /CCITTFaxDecode]" +
                   $" /DecodeParms {parms}", hex));
    }

    /// <summary>
    /// The arithmetic payload whose decoded bitmap the two JBIG2 tests pin. Its first three rows
    /// are, MSB-first, with <c>#</c> = a marked (T.88 value 1) pixel:
    /// <code>
    ///   row 0:  ..............#.
    ///   row 1:  ...#..##.######.
    ///   row 2:  #######...##..#.
    /// </code>
    /// </summary>
    private static readonly byte[] Jbig2ArithPayload =
        { 0x84, 0xC7, 0x3B, 0xFC, 0xE1, 0xA4, 0x73, 0x21 };

    /// <summary>
    /// A 16x16 JBIG2 image — one arithmetic generic region covering the whole page — filling a
    /// 128pt page, so every source pixel is an 8x8 device block at 72 dpi.
    /// </summary>
    /// <remarks>
    /// The segment bytes are binary, so they are wrapped in <c>/ASCIIHexDecode</c> ahead of
    /// <c>/JBIG2Decode</c> the same way the CCITT fixture above wraps its <c>3317F0&gt;</c>, which
    /// keeps the whole fixture an ASCII string. The builders below are copies of the ones in
    /// <c>tests/Tetitulz.Pdf.Core.Tests/IO/Filters/Jbig2ArithmeticGenericRegionTests.cs</c>:
    /// CLAUDE.md forbids a test project from depending on another test project, so the duplication
    /// is deliberate.
    /// </remarks>
    private static byte[] Jbig2ImagePdf(byte[] arithData)
    {
        byte[] region = Jbig2GenericRegionArithBody(16, 16, arithData);
        byte[] jbig2  = Jbig2Concat(
            Jbig2SegmentHeader(0, 48, 1, 19), Jbig2PageInfoBody(16, 16),
            Jbig2SegmentHeader(1, 38, 1, (uint)region.Length), region,
            Jbig2SegmentHeader(2, 51, 0, 0));

        var hex = new StringBuilder(jbig2.Length * 2 + 1);
        foreach (byte b in jbig2) hex.Append(b.ToString("X2"));
        hex.Append('>');

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 128 128] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 128 0 0 128 0 0 cm /Im1 Do Q"),
            Stream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray" +
                   " /BitsPerComponent 1 /Filter [/ASCIIHexDecode /JBIG2Decode]", hex.ToString()));
    }

    private static byte[] Jbig2Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var r = new byte[total];
        int off = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, r, off, p.Length);
            off += p.Length;
        }
        return r;
    }

    /// <summary>T.88 §7.2 segment header: number(4), flags(1), referred-to(1), page(1), length(4).</summary>
    private static byte[] Jbig2SegmentHeader(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength) =>
        new byte[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),  (byte)(segmentNumber & 0xFF),
            (byte)(segmentType & 0x3F),
            0x00,                                   // no referred-to segments
            (byte)(pageAssociation & 0xFF),
            (byte)(dataLength >> 24), (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),  (byte)(dataLength & 0xFF),
        };

    /// <summary>T.88 §7.4.8 page information: width(4), height(4), then 11 bytes left at zero.</summary>
    private static byte[] Jbig2PageInfoBody(uint width, uint height)
    {
        var body = new byte[19];
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        return body;
    }

    /// <summary>
    /// T.88 §7.4.6 immediate generic region: 17-byte region info, one flags byte (MMR = 0,
    /// GBTEMPLATE = 0, TPGDON = 0), the four template-0 AT pairs at their default positions, then
    /// the arithmetic data.
    /// </summary>
    private static byte[] Jbig2GenericRegionArithBody(int width, int height, byte[] arithData)
    {
        var body = new byte[17 + 1 + 8 + arithData.Length];
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        // x, y and the region flags byte at 16 stay zero (region at the origin, OR combination).
        body[17] = 0x00;                            // MMR=0, GBTEMPLATE=0, TPGDON=0
        body[18] = 0x03; body[19] = 0xFF;           // AT1 = (+3, -1)
        body[20] = 0xFD; body[21] = 0xFF;           // AT2 = (-3, -1)
        body[22] = 0x02; body[23] = 0xFE;           // AT3 = (+2, -2)
        body[24] = 0xFE; body[25] = 0xFE;           // AT4 = (-2, -2)
        Buffer.BlockCopy(arithData, 0, body, 26, arithData.Length);
        return body;
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
