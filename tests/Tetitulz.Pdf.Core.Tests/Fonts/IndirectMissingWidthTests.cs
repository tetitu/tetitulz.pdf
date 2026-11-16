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
/// §9.6.1's <c>/MissingWidth</c> must be honoured by both simple subtypes whether it is written
/// directly or as an indirect reference.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>PdfTrueTypeFont</c> read the entry through a resolving helper and
/// <c>PdfType1Font</c> read it straight off the descriptor dictionary — and
/// <c>PdfDictionary</c>'s indexer does not follow a reference, so a perfectly legal
/// <c>/MissingWidth 9 0 R</c> read as <b>absent</b> through Type 1. Absent means Table 122's
/// default of <b>0</b>, so every character the <c>/Widths</c> array does not cover measured a zero
/// advance and piled up at one x, while the identical TrueType dictionary measured the real
/// value.</para>
///
/// <para>Both reads are now <c>PdfSimpleFont.ReadFontDescriptor</c>'s, alongside the §9.6.6.4
/// Symbolic flag, which had drifted the same way in the same two lines.</para>
///
/// <para><b>The width is asserted, never merely its presence.</b> The fixture's
/// <c>/MissingWidth</c> is 313 — a value chosen not to look like a default, so 0 (the old Type 1
/// answer), 500 (a <c>/Widths</c> entry) and 1000 (<c>/DW</c>) are all distinguishable from
/// it.</para>
/// </remarks>
public class IndirectMissingWidthTests
{
    /// <summary>Object number of the <c>/Subtype /Type1</c> font dictionary in the fixture.</summary>
    private const int Type1FontObject = 5;

    /// <summary>Object number of the <c>/Subtype /TrueType</c> font dictionary in the fixture.</summary>
    private const int TrueTypeFontObject = 6;

    /// <summary>The fixture's <c>/MissingWidth</c>: not 0, not 500, not 1000.</summary>
    private const double MissingWidth = 313;

    /// <summary>Every entry of the fixture's ten-element <c>/Widths</c> array.</summary>
    private const double ExplicitWidth = 500;

    /// <summary>A code past the end of the <c>/Widths</c> array, so §9.6.1 says /MissingWidth.</summary>
    private const int CodeOutsideWidths = 200;

    /// <summary>
    /// <b>The defect.</b> An indirect <c>/MissingWidth</c> measures its real value through both
    /// simple subtypes.
    /// </summary>
    [Fact]
    public void AnIndirectMissingWidthIsHonouredByBothSimpleSubtypes()
    {
        var (type1, trueType) = BothFonts(indirectMissingWidth: true);

        Assert.Equal(MissingWidth, type1.GetWidth(CodeOutsideWidths));      // was 0 — the defect
        Assert.Equal(MissingWidth, trueType.GetWidth(CodeOutsideWidths));
    }

    /// <summary>
    /// <b>The control.</b> A direct <c>/MissingWidth</c> was already honoured by both, and still
    /// is — so the fix is "follow the reference", not "change what the entry means".
    /// </summary>
    [Fact]
    public void ADirectMissingWidthIsStillHonouredByBothSimpleSubtypes()
    {
        var (type1, trueType) = BothFonts(indirectMissingWidth: false);

        Assert.Equal(MissingWidth, type1.GetWidth(CodeOutsideWidths));
        Assert.Equal(MissingWidth, trueType.GetWidth(CodeOutsideWidths));
    }

    /// <summary>
    /// <b>The floor.</b> <c>/MissingWidth</c> applies only where <c>/Widths</c> does not reach, and
    /// a width recovered from it is not an explicit one — otherwise "return /MissingWidth for
    /// everything" would satisfy the cases above.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingWidthDoesNotDisplaceTheWidthsArray(bool indirectMissingWidth)
    {
        var (type1, trueType) = BothFonts(indirectMissingWidth);

        Assert.Equal(ExplicitWidth, type1.GetWidth(32));
        Assert.Equal(ExplicitWidth, trueType.GetWidth(32));
        Assert.True(type1.HasExplicitWidth(32));
        Assert.True(trueType.HasExplicitWidth(32));
        Assert.False(type1.HasExplicitWidth(CodeOutsideWidths));
        Assert.False(trueType.HasExplicitWidth(CodeOutsideWidths));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Both simple fonts over the <b>same</b> objects — one <c>/Widths</c> array and one
    /// <c>/FontDescriptor</c> — so nothing but the <c>/Subtype</c> separates them.
    /// </summary>
    private static (PdfFont Type1, PdfFont TrueType) BothFonts(bool indirectMissingWidth)
    {
        using var ms = new MemoryStream(FontPdf(indirectMissingWidth));
        using var doc = PdfDocument.Open(ms);

        var type1Dict = doc.ResolveObject(new PdfReference(Type1FontObject, 0)) as PdfDictionary;
        var trueTypeDict = doc.ResolveObject(new PdfReference(TrueTypeFontObject, 0)) as PdfDictionary;
        Assert.Equal("Type1", (type1Dict?[PdfName.Subtype] as PdfName)?.Value);
        Assert.Equal("TrueType", (trueTypeDict?[PdfName.Subtype] as PdfName)?.Value);

        return (new PdfType1Font(type1Dict, doc, null),
                new PdfTrueTypeFont(trueTypeDict, doc, null));
    }

    private static byte[] FontPdf(bool indirectMissingWidth)
    {
        string missing = indirectMissingWidth
            ? "9 0 R"
            : MissingWidth.ToString(CultureInfo.InvariantCulture);

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
            StreamObj("BT /F1 12 Tf ( ) Tj ET\n"),
            // /BaseFont /Custom: not one of the standard 14, so the §9.6.2.2 AFM tier cannot fire
            // and /MissingWidth is unambiguously what a code outside /Widths measures.
            "<< /Type /Font /Subtype /Type1 /BaseFont /Custom /FirstChar 32 /Widths 7 0 R" +
            " /FontDescriptor 8 0 R >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Custom /FirstChar 32 /Widths 7 0 R" +
            " /FontDescriptor 8 0 R >>",
            "[ 500 500 500 500 500 500 500 500 500 500 ]",
            "<< /Type /FontDescriptor /FontName /Custom /MissingWidth " + missing + " >>",
            MissingWidth.ToString(CultureInfo.InvariantCulture));
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
