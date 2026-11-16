// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>FontCache</c> reads that decide <b>writing mode</b> and <b>font kind</b>, with the value
/// written as an indirect reference.
/// </summary>
/// <remarks>
/// <para><b>All three fall back to the ordinary case, which is why nothing looks broken.</b>
/// An unresolved <c>/WMode</c> or vertical <c>/Encoding</c> yields writing mode 0, so a vertical
/// CJK document lays out <i>horizontally</i>. An unresolved <c>/Subtype</c> means a Type 3 font is
/// not recognised as one, so its <c>/CharProcs</c> procedures are never run. An unresolved
/// <c>/DW2</c> falls to the built-in default vertical advance. Every one of them produces a page
/// that renders — just not the page the file describes.</para>
///
/// <para>These were found by asking where a <i>resolve already sits next to a read that has none</i>
/// rather than by grepping for the raw reads: <c>FontCache</c> line 414 resolves the font
/// dictionary and then reads its <c>/Subtype</c> raw, and line 577 resolves <c>/CIDSystemInfo</c>
/// with a belt-and-braces fallback five lines after <c>/BaseFont</c> gets none. That adjacency has
/// now located three clusters in this register.</para>
///
/// <para>Each case pairs the indirect spelling with the direct one, which is what every other
/// fixture in the suite uses and always worked.</para>
/// </remarks>
public class IndirectFontCacheReadTests
{
    /// <summary>
    /// A Type0 font declaring a vertical <c>/Encoding</c> is writing mode 1 whichever way that
    /// name is spelled.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Encoding 9 0 R
    [InlineData(false)]  // /Encoding /Identity-V
    public void VerticalEncoding_YieldsWritingMode1(bool indirect)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Type0Pdf(indirect, "/Identity-V")));
        using var cache = new FontCache();

        Assert.Equal(1, cache.GetWMode("F1", doc.Pages[0].Resources, doc));
    }

    /// <summary>
    /// A vertical predefined CMap the host never registered is still vertical: the name says so,
    /// and the codec's Identity fallback keeps it. This is the case <c>FontCache</c>'s own copy of
    /// the name rule covered before the codec became the one reading.
    /// </summary>
    [Fact]
    public void AnUnregisteredVerticalCMapName_YieldsWritingMode1()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Type0Pdf(indirect: false, "/Tetitulz-Unregistered-V")));
        using var cache = new FontCache();

        Assert.Equal(1, cache.GetWMode("F1", doc.Pages[0].Resources, doc));
    }

    /// <summary>
    /// The floor: a horizontal encoding really is writing mode 0, so the assertion above cannot be
    /// met by an implementation that always answers 1 — and 0 is exactly what the defect produced.
    /// </summary>
    [Fact]
    public void HorizontalEncoding_YieldsWritingMode0()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Type0Pdf(indirect: false, "/Identity-H")));
        using var cache = new FontCache();

        Assert.Equal(0, cache.GetWMode("F1", doc.Pages[0].Resources, doc));
    }

    /// <summary>
    /// A Type0 font naming no <c>/Encoding</c> is horizontal by its codec, so a writing mode its
    /// descendant CIDFont declares — a <c>/WMode</c>, or a vertical CMap name in a misplaced
    /// <c>/Encoding</c> — is what decides. The plain descendant is the control.
    /// </summary>
    [Theory]
    [InlineData(" /WMode 1", 1)]
    [InlineData(" /Encoding /UniJIS-UTF16-V", 1)]
    [InlineData("", 0)]
    public void ADescendantsWritingMode_DecidesWhenTheType0FontNamesNone(string descendantEntries, int expected)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Type0Pdf(indirect: false, encoding: null, descendantEntries)));
        using var cache = new FontCache();

        Assert.Equal(expected, cache.GetWMode("F1", doc.Pages[0].Resources, doc));
    }

    /// <summary>
    /// Table 117's default position-vector x is half the glyph's horizontal width, and a declared
    /// <c>/DW 0</c> is a width: its half is 0. The half-em substitute put such a glyph half an em
    /// off its column. <c>/DW 800</c> is the control, 0.4 em.
    /// </summary>
    [Theory]
    [InlineData(0, 0f)]
    [InlineData(800, 0.4f)]
    public void TheDefaultPositionVectorX_IsHalfTheDeclaredDefaultWidth(int dw, float expectedEm)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Type0Pdf(indirect: false, "/Identity-V", dw: dw)));
        using var cache = new FontCache();

        var metrics = cache.GetVerticalMetrics("F1", 0x20, doc.Pages[0].Resources, doc, 1f);
        Assert.False(metrics.IsExplicit);
        Assert.Equal(expectedEm, metrics.PositionXEm, 4);
    }

    /// <summary>
    /// A Type 3 font is recognised whichever way its <c>/Subtype</c> is spelled. Unrecognised, its
    /// glyph procedures are never run.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Subtype 9 0 R
    [InlineData(false)]  // /Subtype /Type3
    public void Type3Font_IsRecognised(bool indirect)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Type3Pdf(indirect)));
        using var cache = new FontCache();

        Assert.True(cache.IsType3Font("F1", doc.Pages[0].Resources, doc));
    }

    /// <summary>The floor: a Type1 font is not a Type 3 font.</summary>
    [Fact]
    public void Type1Font_IsNotType3()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Type0Pdf(indirect: false, "/Identity-H")));
        using var cache = new FontCache();

        Assert.False(cache.IsType3Font("F1", doc.Pages[0].Resources, doc));
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A Type0/CIDFontType2 font whose <c>/Encoding</c> is <paramref name="encoding"/>. Object 9
    /// carries that name when <paramref name="indirect"/>; a null <paramref name="encoding"/> writes
    /// no <c>/Encoding</c>. <paramref name="descendantEntries"/> is appended to the CIDFont.
    /// </summary>
    private static byte[] Type0Pdf(bool indirect, string encoding, string descendantEntries = "", int dw = 1000) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
        " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj("BT /F1 12 Tf ET\n"),
        "<< /Type /Font /Subtype /Type0 /BaseFont /Kozuka" +
        (encoding == null ? "" : " /Encoding " + (indirect ? "9 0 R" : encoding)) +
        " /DescendantFonts [6 0 R] >>",
        "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Kozuka /DW " + dw.ToString(CultureInfo.InvariantCulture) +
        " /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >>" + descendantEntries + " >>",
        "null",
        "null",
        encoding ?? "null");

    /// <summary>A Type 3 font; object 9 carries the subtype name when <paramref name="indirect"/>.</summary>
    private static byte[] Type3Pdf(bool indirect) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
        " /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObj("BT /F1 12 Tf ET\n"),
        "<< /Type /Font /Subtype " + (indirect ? "9 0 R" : "/Type3") +
        " /FontBBox [0 0 10 10] /FontMatrix [0.001 0 0 0.001 0 0]" +
        " /CharProcs << >> /Encoding << /Differences [65 /a] >> /FirstChar 65 /LastChar 65" +
        " /Widths [500] >>",
        "null",
        "null",
        "null",
        "/Type3");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
