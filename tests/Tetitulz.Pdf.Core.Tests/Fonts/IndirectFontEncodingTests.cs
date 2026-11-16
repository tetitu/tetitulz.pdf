// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A <c>/Differences</c> array written as an <b>indirect reference</b> must be applied.
/// </summary>
/// <remarks>
/// <para><b>The defect, in both simple-font classes.</b> <c>/Encoding</c> was resolved and then
/// <c>encodingDict.GetArray(PdfName.Differences)</c> was not, so
/// <c>/Encoding &lt;&lt; /Differences 9 0 R &gt;&gt;</c> left <c>ParseDifferences</c> unrun and the
/// difference tables empty. <c>/BaseEncoding</c> beside it had the same problem.</para>
///
/// <para><b>The blast radius differs by font kind, and that is worth knowing before reading the
/// fix.</b> For an embedded CFF/Type 1, <c>FontCache.ApplyPdfEncodingOverride</c> resolves
/// <c>/Differences</c> itself and overrides the glyph mapping, so those still <i>drew</i>
/// correctly — only extraction and widths were wrong. For a <b>TrueType</b> font there is no such
/// compensating path (that override is applied to <c>CffFont</c> only), so glyph selection was
/// wrong as well, and silently: the report at the top of
/// <c>GlyphIdResolver.ApplyBuiltInGlyphIds</c> is gated on the font using its built-in encoding,
/// which a font that <i>has</i> an <c>/Encoding</c> entry does not.</para>
///
/// <para><b>Asserted through <c>Decode</c></b>, which is the path extraction takes and the one
/// affected in every case. The fixture remaps code 65 to the glyph named <c>B</c> — chosen because
/// it needs no Adobe Glyph List lookup to be unambiguous, and because the <i>unmapped</i> answer
/// for code 65 is the very ordinary <c>"A"</c>, so a test that silently lost the differences would
/// still see a plausible letter rather than an obvious blank.</para>
/// </remarks>
public class IndirectFontEncodingTests
{
    [Theory]
    [InlineData("TrueType", true)]
    [InlineData("TrueType", false)]   // control — the spelling that always worked
    [InlineData("Type1", true)]
    [InlineData("Type1", false)]      // control
    public void Differences_AreAppliedWhicheverWayTheArrayIsSpelled(string subtype, bool indirect)
    {
        string decoded = Decode(FontPdf(subtype, indirect), 65);

        Assert.Equal("B", decoded);
    }

    /// <summary>
    /// The floor: with no <c>/Differences</c> at all, code 65 really is <c>"A"</c>. Without this the
    /// assertion above could be satisfied by an implementation that always returned <c>"B"</c>, and
    /// <c>"A"</c> is exactly the answer the defect produced.
    /// </summary>
    [Theory]
    [InlineData("TrueType")]
    [InlineData("Type1")]
    public void NoDifferences_LeavesTheCodeAtItsBaseEncoding(string subtype)
    {
        Assert.Equal("A", Decode(FontPdf(subtype, indirect: false, omitDifferences: true), 65));
    }

    /// <summary>
    /// <b><c>/BaseEncoding</c> is resolved too</b> — which this file's remarks have claimed since it
    /// was written ("<c>/BaseEncoding</c> beside it had the same problem") while every fixture in it
    /// wrote the name directly. A mutation reading it non-resolving survived the whole suite.
    /// </summary>
    /// <remarks>
    /// Code <c>0x27</c> is the instrument: WinAnsi calls it <c>quotesingle</c> (U+0027) and
    /// §9.6.6.4's implicit base, StandardEncoding, calls it <c>quoteright</c> (U+2019). A dropped
    /// resolve does not fail — it reads <c>/BaseEncoding</c> as <i>absent</i>, which is a legal
    /// state with a perfectly ordinary character on the other side of it, so nothing but a code
    /// where the two tables disagree can tell the difference.
    /// </remarks>
    [Theory]
    [InlineData("TrueType", true)]
    [InlineData("TrueType", false)]   // control — the spelling that always worked
    [InlineData("Type1", true)]
    [InlineData("Type1", false)]      // control
    public void BaseEncoding_IsHonouredWhicheverWayItIsSpelled(string subtype, bool indirectBase)
    {
        string decoded = Decode(FontPdf(subtype, indirect: false, indirectBase: indirectBase), 0x27);

        Assert.Equal("'", decoded);
    }

    /// <summary>
    /// The floor: with no <c>/BaseEncoding</c> at all, <c>0x27</c> really is U+2019 — so the
    /// assertion above is falsifiable in the direction the defect takes it.
    /// </summary>
    [Theory]
    [InlineData("TrueType")]
    [InlineData("Type1")]
    public void NoBaseEncoding_LeavesTheCodeAtTheImplicitStandardEncoding(string subtype)
    {
        Assert.Equal("’",
            Decode(FontPdf(subtype, indirect: false, omitBaseEncoding: true), 0x27));
    }

    // ------------------------------------------------------------------

    private static string Decode(byte[] pdf, byte code)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var fontDict = doc.ResolveObject(new PdfReference(5, 0)) as PdfDictionary;
        Assert.NotNull(fontDict);

        return PdfFont.Create(fontDict!, doc).Decode(new[] { code });
    }

    /// <summary>
    /// A simple font whose <c>/Encoding</c> dictionary remaps code 65 to the glyph named
    /// <c>B</c>. When <paramref name="indirect"/> the <c>/Differences</c> array is object 7; when
    /// <paramref name="indirectBase"/> the <c>/BaseEncoding</c> name is object 8.
    /// </summary>
    private static byte[] FontPdf(
        string subtype,
        bool indirect,
        bool omitDifferences = false,
        bool indirectBase = false,
        bool omitBaseEncoding = false)
    {
        string diffs = omitDifferences
            ? ""
            : " /Differences " + (indirect ? "7 0 R" : "[65 /B]");

        string baseEncoding = omitBaseEncoding
            ? ""
            : " /BaseEncoding " + (indirectBase ? "8 0 R" : "/WinAnsiEncoding");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObj("BT /F1 12 Tf (A) Tj ET\n"),
            "<< /Type /Font /Subtype /" + subtype + " /BaseFont /Helvetica" +
            " /Encoding <<" + baseEncoding + diffs + " >> >>",
            "null",
            "[65 /B]",
            "/WinAnsiEncoding");
    }

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
