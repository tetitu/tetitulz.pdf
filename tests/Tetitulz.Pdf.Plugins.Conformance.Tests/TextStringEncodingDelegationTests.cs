// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <c>PdfAccessibilityFixer.CreateTextString</c> was one of four hand-copied ISO 32000-1 §7.9.2.2
/// text-string encoders in the tree. It is now a one-line delegation to
/// <c>Core.Objects.PdfString(string)</c>.
///
/// <para><b>The ring model is what made this copy the interesting one.</b> Core's
/// <c>InternalsVisibleTo</c> names Base, Signing, Accessibility, Builder, DocxToPdf and the test
/// projects — <b>no plugin</b> — so the delegation is only legal because <c>PdfString(string)</c>
/// is <c>public</c>. Had it been internal, this copy would have had to stay.</para>
///
/// <para><b>What the delegation changes, and what it does not.</b> The local copy sent anything
/// outside printable ASCII through UTF-16BE. The Core constructor sends one byte per character
/// whenever that byte survives <c>GetText</c>'s PDFDocEncoding table, so a Latin-1 tooltip gets
/// smaller while a tooltip PDFDocEncoding would <i>reinterpret</i> still goes out as UTF-16BE with
/// the mandatory <c>FE FF</c> marker. <b>Every case round-trips to the same text either way</b> —
/// which is exactly why these assertions are on the bytes as well as on the text.</para>
///
/// <para>The Hebrew and pure-ASCII ends of this are already pinned by
/// <c>PdfUaSpecComplianceTests.Fix_HebrewTooltip_IsStoredAsAUtf16BeTextString</c> and
/// <c>Fix_AsciiTooltip_IsStoredAsAPlainLiteralNotUtf16</c>; this file adds the two cases those
/// miss, which are the two the delegation actually moves.</para>
/// </summary>
public class TextStringEncodingDelegationTests
{
    /// <summary>
    /// U+00A0 NO-BREAK SPACE — written as a single Latin-1 byte it is read back as U+20AC EURO
    /// SIGN, because the encoder and the decoder used different single-byte tables. Neither the
    /// old local copy nor the old Core constructor got this right on its own: the local copy
    /// avoided it by refusing every non-ASCII character, and the constructor got it wrong.
    /// </summary>
    private const string NoBreakSpaceTooltip = "Enter\u00A0a value";

    /// <summary>Latin-1, and nothing PDFDocEncoding disagrees with — the case that shrinks.</summary>
    private const string Latin1Tooltip = "Nom complet\u00E9";

    /// <summary>
    /// The band the old copy could not distinguish from Hebrew. It must survive as UTF-16BE, not
    /// as the byte 0xA0 that a reader would show as a currency symbol in the middle of a tooltip.
    /// </summary>
    [Fact]
    public void NoBreakSpaceTooltip_SurvivesAsUtf16RatherThanBecomingEuroSigns()
    {
        PdfString tooltip = FixAndReadTooltip(NoBreakSpaceTooltip);

        Assert.Equal(NoBreakSpaceTooltip, tooltip.GetText());
        Assert.DoesNotContain((char)0x20AC, tooltip.GetText()); // EURO SIGN
        Assert.Equal((byte)0xFE, tooltip.RawBytes[0]);
        Assert.Equal((byte)0xFF, tooltip.RawBytes[1]);
        Assert.Equal(2 + NoBreakSpaceTooltip.Length * 2, tooltip.RawBytes.Length);
    }

    /// <summary>
    /// The case that proves the delegation happened rather than the old blanket ASCII-only rule
    /// surviving: <c>é</c> is representable as one byte in both PDFDocEncoding and Latin-1, so the
    /// Core constructor uses one byte. The old copy would have produced a 2 + 2n-byte UTF-16BE
    /// string here, so this assertion is red against it while the text assertion beside it is
    /// green — which is the whole point of asserting both.
    /// </summary>
    [Fact]
    public void Latin1Tooltip_UsesOneByteperCharacterInsteadOfUtf16()
    {
        PdfString tooltip = FixAndReadTooltip(Latin1Tooltip);

        Assert.Equal(Latin1Tooltip, tooltip.GetText());
        Assert.Equal(Latin1Tooltip.Length, tooltip.RawBytes.Length);
        Assert.NotEqual((byte)0xFE, tooltip.RawBytes[0]);
    }

    // ================================================================
    //  Harness — a minimal AcroForm, written and read back for real
    // ================================================================

    private static PdfString FixAndReadTooltip(string template)
    {
        using var input  = new MemoryStream(FormPdf());
        using var output = new MemoryStream();
        PdfAccessibilityFixer.Fix(input, output,
            new AccessibilityFixOptions { DefaultTooltipTemplate = template });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var acroForm = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acroForm);
        var fields = doc.ResolveObject(acroForm[PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        var field = doc.ResolveObject(fields[0]) as PdfDictionary;
        Assert.NotNull(field);

        var tooltip = doc.ResolveObject(field[PdfName.TU]) as PdfString;
        Assert.NotNull(tooltip);
        return tooltip;
    }

    /// <summary>Catalog, page tree, and one widget that is also the (tooltip-less) form field.</summary>
    private static byte[] FormPdf()
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (field1) /Rect [0 0 100 20] >>");

    private static byte[] BuildPdf(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n{1}\nendobj\n", i + 1, objects[i]);
        }

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", objects.Length + 1);
        foreach (int off in offsets)
            sb.AppendFormat("{0:D10} 00000 n \n", off);

        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", objects.Length + 1);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
