// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The three simple-font lookups — <c>GetWidth</c>, <c>HasExplicitWidth</c> and <c>Decode</c> —
/// must give the same answer for <c>/Subtype /Type1</c> and <c>/Subtype /TrueType</c>, because
/// §9.6.6 defines them once for both.
/// </summary>
/// <remarks>
/// <para><b>Why this suite exists.</b> The two classes carried byte-identical bodies for
/// <c>HasExplicitWidth</c> and <c>Decode</c> and the same <c>/Widths</c> lookup inside
/// <c>GetWidth</c>, over six privately duplicated fields, and that had already produced a live
/// defect: §9.6.6.4's implicit-base-encoding rule was fixed in the TrueType copy only, so byte
/// 0x27 with no <c>/Encoding</c> extracted as U+2019 through one class and U+0027 through the
/// other. They now share <c>PdfSimpleFont</c>.</para>
///
/// <para><b>Each case asserts the value, not merely the agreement.</b> Two implementations that
/// are one implementation agree by construction, so an equality-only test would be a tautology
/// the moment the merge landed. The expected width and the expected decoded string are named
/// here; the cross-subtype comparison is the part that would catch a future re-fork.</para>
///
/// <para><b>The last case is the one that separates them</b>, and it is deliberately left
/// separate: only Type 1 falls back to Standard-14 AFM metrics when the font has no
/// <c>/Widths</c> (§9.6.2.2). A TrueType font in that state still measures
/// <c>/MissingWidth</c>.</para>
/// </remarks>
public class SimpleFontSharedLookupTests
{
    /// <summary>Object number of the <c>/Subtype /Type1</c> font dictionary in the fixture.</summary>
    private const int Type1FontObject = 5;

    /// <summary>Object number of the <c>/Subtype /TrueType</c> font dictionary in the fixture.</summary>
    private const int TrueTypeFontObject = 6;

    /// <summary>The fixture's <c>/Widths</c> entry for code 65, chosen not to look like a default.</summary>
    private const double WidthOfA = 777;

    /// <summary>The fixture's <c>/MissingWidth</c>.</summary>
    private const double MissingWidth = 313;

    [Fact]
    public void ExplicitWidth_IsTheWidthsEntry_ForBothSimpleSubtypes()
    {
        var (type1, trueType) = BothFonts(withWidths: true);

        Assert.Equal(WidthOfA, type1.GetWidth('A'));
        Assert.Equal(WidthOfA, trueType.GetWidth('A'));
        Assert.True(type1.HasExplicitWidth('A'));
        Assert.True(trueType.HasExplicitWidth('A'));
    }

    /// <summary>
    /// A code outside the <c>/Widths</c> array measures <c>/MissingWidth</c> and reports itself as
    /// not explicit — on both sides of <c>/FirstChar</c>, since the index is
    /// <c>code - /FirstChar</c> and a code below it produces a negative one.
    /// </summary>
    [Theory]
    [InlineData(31)]    // below /FirstChar 32 → negative index
    [InlineData(200)]   // past the end of the 40-entry array
    public void CodeOutsideTheWidthsArray_MeasuresMissingWidth_ForBothSimpleSubtypes(int code)
    {
        var (type1, trueType) = BothFonts(withWidths: true);

        Assert.Equal(MissingWidth, type1.GetWidth(code));
        Assert.Equal(MissingWidth, trueType.GetWidth(code));
        Assert.False(type1.HasExplicitWidth(code));
        Assert.False(trueType.HasExplicitWidth(code));
    }

    /// <summary>
    /// All four <c>Decode</c> tiers in one string: <c>/ToUnicode</c> beats <c>/Differences</c>,
    /// which beats the base encoding, which beats the raw byte.
    /// </summary>
    /// <remarks>
    /// Byte 0x27 is the tier-3 probe on purpose. It is the byte the two classes disagreed about:
    /// StandardEncoding (the §9.6.6.4 implicit base, and the default one level down for an
    /// <c>/Encoding</c> dictionary with no <c>/BaseEncoding</c> — which is what this fixture has)
    /// maps it to U+2019, while WinAnsi leaves it at U+0027.
    /// </remarks>
    [Fact]
    public void Decode_AppliesToUnicodeThenDifferencesThenEncodingThenTheRawByte_ForBothSimpleSubtypes()
    {
        var (type1, trueType) = BothFonts(withWidths: true);

        // 0x43 is in /ToUnicode; 0x42 is remapped by /Differences to the glyph named A;
        // 0x27 is only in the base encoding; 0x01 is in none of them.
        var probe = new byte[] { 0x43, 0x42, 0x27, 0x01 };

        // Z from /ToUnicode, A from /Differences, U+2019 from StandardEncoding, U+0001 raw.
        const string expected = "ZA\u2019\u0001";

        Assert.Equal(expected, type1.Decode(probe));
        Assert.Equal(expected, trueType.Decode(probe));
    }

    /// <summary>
    /// With no <c>/Widths</c> at all, a Standard-14 font of <b>either</b> simple subtype measures
    /// its built-in AFM metrics (§9.6.2.2). Neither reports the width as explicit — an AFM width
    /// is not a <c>/Widths</c> entry, and space detection and combining-mark placement need to
    /// tell the two apart.
    ///
    /// <para><b>This test asserted the opposite until 2026-08-27, and its name said so.</b> It
    /// read <c>WithNoWidthsArray_OnlyTheType1FontFallsBackToStandard14Metrics</c> and pinned
    /// <c>/MissingWidth</c> — i.e. <b>0</b> — for the TrueType side, calling the asymmetry "the
    /// one place the two are allowed to differ". §9.6.3 Table 113 carries the same "Required
    /// except for the standard 14 fonts" note as Table 111, so
    /// <c>/Subtype /TrueType /BaseFont /Arial</c> with no <c>/Widths</c> is legal — and every
    /// glyph in such a font drew at zero advance. The asymmetry was a defect, not a rule.</para>
    ///
    /// <para>It is <b>renamed</b> rather than edited in place. CLAUDE.md: when a behaviour
    /// changes, rename the test or restore the behaviour — never edit the assertion and leave the
    /// name, because a name that contradicts its body is the one failure mode no guard here
    /// catches.</para>
    /// </summary>
    [Fact]
    public void WithNoWidthsArray_BothSimpleSubtypesFallBackToStandard14Metrics()
    {
        var (type1, trueType) = BothFonts(withWidths: false);

        Assert.Equal(667, type1.GetWidth('A'));          // Helvetica 'A' in the AFM tables
        Assert.Equal(667, trueType.GetWidth('A'));       // was MissingWidth (0) — the defect
        Assert.False(type1.HasExplicitWidth('A'));
        Assert.False(trueType.HasExplicitWidth('A'));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Both simple fonts over the <b>same</b> objects: one document, one <c>/Widths</c> array, one
    /// <c>/Encoding</c> dictionary, one <c>/FontDescriptor</c> and one <c>/ToUnicode</c> map, so
    /// nothing but the <c>/Subtype</c> separates them.
    /// </summary>
    private static (PdfFont Type1, PdfFont TrueType) BothFonts(bool withWidths)
    {
        // The document is disposed before the assertions run; the fonts have already read
        // everything they need out of it in their constructors, which is what the render and
        // extraction paths rely on too.
        using var ms = new MemoryStream(FontPdf(withWidths));
        using var doc = PdfDocument.Open(ms);

        var type1Dict = doc.ResolveObject(new PdfReference(Type1FontObject, 0)) as PdfDictionary;
        var trueTypeDict = doc.ResolveObject(new PdfReference(TrueTypeFontObject, 0)) as PdfDictionary;
        Assert.NotNull(type1Dict);
        Assert.NotNull(trueTypeDict);
        Assert.Equal("Type1", (type1Dict![PdfName.Subtype] as PdfName)?.Value);
        Assert.Equal("TrueType", (trueTypeDict![PdfName.Subtype] as PdfName)?.Value);

        // One shared /ToUnicode map, passed the way PdfFont.Create passes it: 0x43 → "Z".
        var toUnicode = new Dictionary<int, string> { { 0x43, "Z" } };

        return (new PdfType1Font(type1Dict, doc, toUnicode),
                new PdfTrueTypeFont(trueTypeDict, doc, toUnicode));
    }

    /// <summary>
    /// A one-page document carrying two font dictionaries that share every referenced object.
    /// </summary>
    private static byte[] FontPdf(bool withWidths)
    {
        // /FirstChar 32 with 40 entries covers codes 32..71. Index 33 is code 65 ('A').
        var widths = new StringBuilder("[");
        for (int i = 0; i < 40; i++)
            widths.Append(' ').Append(i == 33
                ? WidthOfA.ToString(CultureInfo.InvariantCulture)
                : "500");
        widths.Append(" ]");

        string metrics = withWidths
            ? " /FirstChar 32 /Widths 7 0 R /Encoding 8 0 R"
            : "";   // no /Widths and no /Encoding: the Standard-14 fallback's own shape

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
            StreamObj("BT /F1 12 Tf (A) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica" + metrics +
            " /FontDescriptor 9 0 R >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Helvetica" + metrics +
            " /FontDescriptor 9 0 R >>",
            widths.ToString(),
            // No /BaseEncoding: the implicit base is StandardEncoding (§9.6.6.4), which is the
            // rule the two classes once disagreed about.
            "<< /Differences [66 /A] >>",
            "<< /Type /FontDescriptor /FontName /Helvetica /MissingWidth " +
            MissingWidth.ToString(CultureInfo.InvariantCulture) + " >>");
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
