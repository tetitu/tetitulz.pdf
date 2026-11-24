// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Four render-path keys that were read without following an indirect reference. Each is asserted
/// on the evidence the defect actually changes — absolute pixels where both renderers were wrong
/// together, and glyph position where the defect is in an advance.
/// </summary>
/// <remarks>
/// <para>They split into two kinds, and the distinction decides the assertion:</para>
///
/// <para><b>Both renderers wrong, identically.</b> <c>/ImageMask</c> and a stencil's
/// <c>/Decode</c> were open-coded at six sites across the two files, none of them resolving. A
/// parity test is <i>structurally blind</i> to these — the two renderers agreed, on the wrong
/// answer. Only absolute pixel evidence can see it, which is why the stencil cases assert colour at
/// a point rather than raster-against-SVG. Both now go through <c>ImageStencil</c>.</para>
///
/// <para><b>One renderer wrong.</b> <c>/BM</c> and the Type 3 <c>/FirstChar</c> were resolved on
/// the SVG side and not on the raster side — <c>/BM</c> being the only unresolved read among
/// fifteen in its own method. <c>/FirstChar</c> is nonetheless <i>not</i> asserted by comparing the
/// two: the defect is in an <b>advance</b>, each renderer stays internally self-consistent, and
/// what moves is every glyph after the one that was misread.</para>
///
/// <para><b>A fifth was raised and is REFUTED — <c>/Filter</c> and <c>/DecodeParms</c> in
/// <c>DecodeImageStream</c>.</b> The reasoning was sound in isolation: a raw <c>/Filter</c> would be
/// a non-null <c>PdfReference</c>, skipping the "no filter" early return, and
/// <c>FilterPipeline.Decode</c> would match neither its name nor its array branch and hand back the
/// compressed bytes. But <c>PdfParser</c> normalises an indirect <c>/Filter</c> <b>and</b>
/// <c>/DecodeParms</c> in place when it builds the <c>PdfStream</c> — and the stream-valued entries
/// inside <c>/DecodeParms</c> too — precisely so a filter, which runs in Core with no document,
/// never receives a reference. A fix was written, threaded a required <c>PdfDocument</c> through
/// eight call sites, and was <b>reverted</b>: the mutation could not be made to fail, because the
/// input state it guards against cannot be constructed by the parser.</para>
/// </remarks>
public class IndirectRenderReadTests
{
    // ================================================================
    //  Both renderers were wrong identically — absolute pixel evidence
    // ================================================================

    /// <summary>
    /// A stencil mask paints in the fill colour whichever way <c>/ImageMask</c> is spelled.
    /// <c>false</c> is the control: the direct spelling always worked.
    /// </summary>
    /// <remarks>
    /// Read as "not a stencil", the image is not painted in the fill colour <i>and</i> takes the
    /// 8-bpc default with a 3-component space, against Table 90's fixed 1 bpc and Table 89's ban on
    /// a <c>/ColorSpace</c> — 24 bits per pixel read from a buffer holding 1. The rows are read from
    /// the wrong offsets, so this is a stride error, not only a colour one.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /ImageMask true
    [InlineData(true)]   // /ImageMask 9 0 R
    public void AStencilPaintsInTheFillColour_WhicheverWayImageMaskIsSpelled(bool indirect)
    {
        using var bmp = RenderRaster(StencilPdf(maskIndirect: indirect, decodeIndirect: false,
                                                invertDecode: false));

        // The stencil is a 2x2 all-zero-sample image over the whole page; with the default
        // /Decode [0 1] a 0 sample paints, so the page must be solid green.
        var centre = bmp.GetPixel(50, 50);
        Assert.True(centre.Green > 200 && centre.Red < 80,
            "expected the fill colour (green), got " + Describe(centre));
    }

    /// <summary>
    /// A <c>/Mask</c> stencil whose <c>/Decode</c> is <c>[1 0]</c> masks the opposite samples —
    /// whichever way the <c>/Decode</c> is spelled.
    /// </summary>
    /// <remarks>
    /// <para>The only one of these findings that selects the <b>opposite</b> result rather than
    /// losing a refinement: read as absent, both renderers masked the complement of what the file
    /// asked for. Both agreed, so a parity test is structurally blind to it.</para>
    ///
    /// <para><b>It has to go through an image's <c>/Mask</c> entry, and the first fixture did
    /// not.</b> A directly-drawn <c>/ImageMask</c> reads its <c>/Decode</c> through
    /// <c>GetDecodeArray</c>, which already resolved; <c>ApplyStencilMask</c> — the site this row
    /// names — runs only for an image carrying a separate stencil stream in <c>/Mask</c>. The
    /// first version drew a bare <c>/ImageMask</c> and the mutation survived it, which is how the
    /// mismatch surfaced.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /Decode [1 0]
    [InlineData(true)]   // /Decode 9 0 R
    public void AMaskDecodeInverts_WhicheverWayItIsSpelled(bool indirect)
    {
        // The mask's samples are all 0. Default /Decode [0 1] makes 0 mean "paint"; [1 0] reverses
        // it, so the red image is masked out entirely and the white page shows through.
        using var inverted = RenderRaster(MaskedImagePdf(decodeIndirect: indirect, invertDecode: true));
        using var normal = RenderRaster(MaskedImagePdf(decodeIndirect: false, invertDecode: false));

        var invertedCentre = inverted.GetPixel(50, 50);
        var normalCentre = normal.GetPixel(50, 50);

        Assert.True(invertedCentre.Red > 200 && invertedCentre.Green > 200,
            "expected the image to be masked out, got " + Describe(invertedCentre));

        // …and the control must paint, or "masked out" is a claim about a broken fixture.
        Assert.True(normalCentre.Red > 200 && normalCentre.Green < 80,
            "the control did not paint: " + Describe(normalCentre));
    }

    // ================================================================
    //  One renderer alone would be wrong — raster/SVG parity is the evidence
    // ================================================================

    /// <summary>
    /// The raster path honours an indirect <c>/BM</c>, as the SVG path already did.
    /// </summary>
    /// <remarks>
    /// <c>/BM</c> was the <b>only</b> unresolved read among fifteen in <c>ApplyExtGState</c>, and
    /// <c>SvgContentWriter.GraphicsState</c> resolves both it and its array element under a comment
    /// describing this exact defect class for the neighbouring keys. Multiply-blending green over
    /// red yields black; leaving the mode at Normal yields green, so the two are far apart.
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /BM /Multiply
    [InlineData(true)]   // /BM 9 0 R
    public void TheRasterPathHonoursBlendMode_WhicheverWayBmIsSpelled(bool indirect)
    {
        using var bmp = RenderRaster(BlendModePdf(indirect));

        var centre = bmp.GetPixel(50, 50);
        Assert.True(centre.Red < 80 && centre.Green < 80,
            "expected Multiply of green over red (black), got " + Describe(centre) +
            " — the blend mode stayed at Normal.");

        // And the SVG export must say the same thing, since it was already right.
        string svg = RenderSvg(BlendModePdf(indirect));
        Assert.Contains("multiply", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Type 3 glyph advances by its <c>/Widths</c> entry whichever way <c>/FirstChar</c> is
    /// spelled, so the glyph after it lands in the right place.
    /// </summary>
    /// <remarks>
    /// <para><b>Asserted on the position of the SECOND glyph, because that is the only thing a
    /// defect in an advance moves.</b> The glyph whose width was misread draws perfectly correctly;
    /// what changes is where everything after it goes, accumulating across the run. Both renderers
    /// stay internally self-consistent, so comparing their output cannot surface it — the raster
    /// side read <c>/FirstChar</c> raw while <c>SvgContentWriter.Text</c> read it through
    /// <c>PdfResolve.Number</c>, giving the two different advances for the same file.</para>
    ///
    /// <para>Unresolved, <c>firstChar</c> falls to 0, so <c>charCode - firstChar</c> indexes past
    /// the <c>/Widths</c> array and yields a <b>zero</b> advance — the second glyph draws on top of
    /// the first. The first glyph's procedure is deliberately empty so the <c>/Widths</c> fallback
    /// is reached at all; a procedure setting <c>d0</c> supplies its own advance and never consults
    /// the array.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /FirstChar 65
    [InlineData(true)]   // /FirstChar 9 0 R
    public void ATypeThreeGlyphAdvancesByItsWidth_WhicheverWayFirstCharIsSpelled(bool indirect)
    {
        using var bmp = RenderRaster(TypeThreePdf(indirect));

        // "AB" at 20pt from x=10. 'A' is empty and advances 1000/1000 * 20 = 20pt, so 'B' draws its
        // square from x=30 to x=50. With a zero advance it would draw from x=10 to x=30 instead.
        var advanced = bmp.GetPixel(40, 50);
        var notAdvanced = bmp.GetPixel(15, 50);

        Assert.True(advanced.Red < 80,
            "no ink where the advanced glyph belongs: " + Describe(advanced));
        Assert.True(notAdvanced.Red > 200,
            "ink where the un-advanced glyph would be: " + Describe(notAdvanced) +
            " — the /Widths entry was not applied.");
    }

    // ================================================================

    private static string Describe(SkiaSharp.SKColor c) =>
        "rgb(" + c.Red + "," + c.Green + "," + c.Blue + ")";

    private static SkiaSharp.SKBitmap RenderRaster(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var bmp = SkiaSharp.SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);
        return bmp;
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// A 2x2 all-zero-sample stencil stretched over a 100x100 page, drawn with a green fill.
    /// Object 9 holds whichever value the case writes indirectly.
    /// </summary>
    private static byte[] StencilPdf(bool maskIndirect, bool decodeIndirect, bool invertDecode)
    {
        string decodeValue = invertDecode ? "[1 0]" : "[0 1]";
        string maskEntry = maskIndirect ? "9 0 R" : "true";
        string decodeEntry = decodeIndirect ? "9 0 R" : decodeValue;
        string obj9 = maskIndirect ? "true" : decodeValue;

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "0 1 0 rg q 100 0 0 100 0 0 cm /Im1 Do Q\n"),
            // 1 bpc, 2x2, all samples 0. Two rows of one padded byte each.
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 1"
                   + " /ImageMask " + maskEntry + " /Decode " + decodeEntry
                   + " /Filter /ASCIIHexDecode", "0000>"),
            "null", "null", "null",
            obj9);
    }

    /// <summary>
    /// A 1x1 red image carrying a separate 2x2 all-zero stencil in <c>/Mask</c>. Object 9 holds the
    /// mask's <c>/Decode</c> when <paramref name="decodeIndirect"/> — the only difference.
    /// </summary>
    private static byte[] MaskedImagePdf(bool decodeIndirect, bool invertDecode)
    {
        string decodeValue = invertDecode ? "[1 0]" : "[0 1]";
        string decodeEntry = decodeIndirect ? "9 0 R" : decodeValue;

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB"
                   + " /BitsPerComponent 8 /Mask 6 0 R /Filter /ASCIIHexDecode", "ff0000>"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 1"
                   + " /ImageMask true /Decode " + decodeEntry + " /Filter /ASCIIHexDecode", "0000>"),
            "null", "null",
            decodeValue);
    }

    /// <summary>
    /// A 1x1 red DeviceRGB image behind an <c>/ASCIIHexDecode</c> filter, stretched over the page.
    /// Object 9 holds the filter name when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] FilteredImagePdf(bool indirect)
    {
        string filter = indirect ? "9 0 R" : "/ASCIIHexDecode";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB"
                   + " /BitsPerComponent 8 /Filter " + filter, "ff0000>"),
            "null", "null", "null",
            "/ASCIIHexDecode");
    }

    /// <summary>
    /// A green rectangle drawn over a red one with <c>/BM /Multiply</c>. Object 9 holds the blend
    /// mode name when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] BlendModePdf(bool indirect)
    {
        string bm = indirect ? "9 0 R" : "/Multiply";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /ExtGState << /Gs1 5 0 R >> >> >>",
            Stream("", "1 0 0 rg 0 0 100 100 re f\n"
                     + "/Gs1 gs 0 1 0 rg 0 0 100 100 re f\n"),
            "<< /Type /ExtGState /BM " + bm + " >>",
            "null", "null", "null",
            "/Multiply");
    }

    /// <summary>
    /// A Type 3 font whose first glyph is an empty procedure (so the <c>/Widths</c> fallback is
    /// used) and whose second draws a full-em square. Object 9 holds <c>/FirstChar</c> when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] TypeThreePdf(bool indirect)
    {
        string firstChar = indirect ? "9 0 R" : "65";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", "BT 0 0 0 rg /F1 20 Tf 10 40 Td (AB) Tj ET\n"),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000]"
                + " /FontMatrix [0.001 0 0 0.001 0 0]"
                + " /CharProcs << /a 6 0 R /b 7 0 R >>"
                + " /Encoding << /Type /Encoding /Differences [65 /a /b] >>"
                + " /FirstChar " + firstChar + " /LastChar 66 /Widths [1000 1000]"
                + " /Resources << >> >>",
            Stream("", ""),                                  // 'A' — empty, so /Widths is consulted
            Stream("", "0 0 1000 1000 re f\n"),             // 'B' — a full-em square
            "null",
            "65");
    }

    private static string Stream(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
