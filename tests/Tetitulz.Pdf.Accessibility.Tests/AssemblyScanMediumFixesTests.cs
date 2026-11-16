// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The accessibility defects raised by the 2026 assembly scan at MEDIUM (plan 10).
/// </summary>
public class AssemblyScanMediumFixesTests
{
    // =====================================================================
    //  Fonts.cs read /Subtype and friends without resolving an indirect value
    // =====================================================================

    /// <summary>
    /// A Type0 font whose <c>/Subtype</c> is written as an indirect reference is recognised as a
    /// Type0, so its embedded descendant is found and no UA21 is raised.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> Every other partial in the Checking folder resolves before casting
    /// and says so; <c>Fonts.cs</c> was the sole exception, reading <c>/Subtype</c>,
    /// <c>/BaseFont</c> and <c>/BaseEncoding</c> through the bare indexer at eight sites. The
    /// indexer does not follow a reference (§7.3.10), so a value written <c>n 0 R</c> read as null —
    /// indistinguishable from an absent key. <c>IsEmbedded</c> therefore never took the Type0
    /// branch, never looked at <c>/DescendantFonts</c>, and read <c>/FontDescriptor</c> off the
    /// Type0 dictionary, which correctly has none: a conformant font with an embedded
    /// <c>/FontFile2</c> was reported as "no /FontDescriptor" and <c>IsConforming</c> went false.
    /// </para>
    /// </remarks>
    [Fact]
    public void Audit_Type0FontWithAnIndirectSubtype_IsNotReportedAsUnembedded()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(IndirectSubtypeType0Pdf(), writable: false));

        Assert.DoesNotContain(issues, i =>
            i.Code == "UA21" && i.Message.IndexOf("FontDescriptor", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The same document with the <c>/Subtype</c> written <b>directly</b> is also clean — so the
    /// test above is about the indirection, not about the fixture being unreportable.
    /// </summary>
    [Fact]
    public void Audit_Type0FontWithADirectSubtype_IsAlsoClean()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(DirectSubtypeType0Pdf(), writable: false));

        Assert.DoesNotContain(issues, i =>
            i.Code == "UA21" && i.Message.IndexOf("FontDescriptor", StringComparison.Ordinal) >= 0);
    }

    // =====================================================================
    //  UA24 — an inline image drawn from inside a Form XObject counts
    // =====================================================================

    /// <summary>
    /// A page whose only content is <c>/Fm0 Do</c>, where the form holds an inline image and no
    /// text, is reported as image-only.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>PageInfo.HasInlineImage</c> was assigned once from the page's own
    /// scan and never updated from a form's — while image <i>XObject</i> invocations and text
    /// operators were both counted through forms. The asymmetry contradicted
    /// <c>CheckImageOnlyPages</c>' stated design, and a genuinely image-only, unreadable page passed
    /// UA24 in silence with no coverage notice either, since <c>ContentUsable</c> was true.</para>
    /// </remarks>
    [Fact]
    public void Audit_InlineImageInsideAForm_StillMakesThePageImageOnly()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(InlineImageInsideFormPdf(), writable: false));

        Assert.Contains(issues, i => i.Code == "UA24");
    }

    /// <summary>A form holding text is not an image-only page, so the flag is not simply always set.</summary>
    [Fact]
    public void Audit_TextBearingForm_IsNotReportedAsImageOnly()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(TextInsideFormPdf(), writable: false));

        Assert.DoesNotContain(issues, i => i.Code == "UA24");
    }

    // =====================================================================
    //  SetFieldTooltip refuses a blank tooltip instead of writing /TU ()
    // =====================================================================

    /// <summary>
    /// A null, empty or whitespace tooltip is refused with a stated reason, and the document comes
    /// back byte-for-byte unchanged.
    /// </summary>
    /// <remarks>
    /// <c>MakeTextString</c> turns null/empty into an empty <c>PdfString</c>, so the field used to be
    /// given <c>/TU ()</c> and the method returned <c>true</c> — while the conformance checker treats
    /// a blank <c>/TU</c> as still missing, so a subsequent audit re-reported UA08 against the same
    /// field with nothing anywhere recording why the "successful" fix did nothing.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetFieldTooltip_WithABlankTooltip_IsRefusedAndSaysSo(string tooltip)
    {
        byte[] input = FormFieldPdf();

        using var inMs = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();

        bool ok = PdfStructureTreeEditor.SetFieldTooltip(inMs, outMs, "lastName", tooltip);

        Assert.False(ok);
        Assert.Equal(input, outMs.ToArray());
        Assert.Contains("null, empty", PdfAccessibility.LastDiagnostic ?? "", StringComparison.Ordinal);
    }

    /// <summary>A real tooltip still lands, so the guard did not simply disable the feature.</summary>
    [Fact]
    public void SetFieldTooltip_WithARealTooltip_StillWrites()
    {
        using var inMs = new MemoryStream(FormFieldPdf(), writable: false);
        using var outMs = new MemoryStream();

        Assert.True(PdfStructureTreeEditor.SetFieldTooltip(inMs, outMs, "lastName", "Family name"));
        Assert.Contains("Family name",
            Encoding.Latin1.GetString(outMs.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>A null field name throws rather than silently matching the first unnamed field.</summary>
    [Fact]
    public void SetFieldTooltip_WithANullFieldName_Throws()
    {
        using var inMs = new MemoryStream(FormFieldPdf(), writable: false);
        using var outMs = new MemoryStream();

        Assert.Throws<ArgumentNullException>(
            () => PdfStructureTreeEditor.SetFieldTooltip(inMs, outMs, null, "Family name"));
    }

    // =====================================================================
    //  The seven editors return the document instead of throwing
    // =====================================================================

    /// <summary>
    /// A document the parser cannot open is copied through unchanged, with the reason reported —
    /// not thrown out of the method leaving the caller's output stream empty.
    /// </summary>
    /// <remarks>
    /// <para>Every operation on <c>PdfStructureTreeEditor</c> called <c>PdfDocument.Open</c> outside
    /// any try/catch, so a caller chaining the editors stream-to-stream (the pattern
    /// <c>PdfAccessibility</c>'s own class doc demonstrates) lost the document entirely rather than
    /// getting it back unchanged — and the type's class doc promises the opposite. It was the only
    /// writer in the assembly behaving that way.</para>
    /// <para>Asserted on all three of: no exception, the bytes returned intact, and a reported
    /// reason. The middle one is what makes it a behaviour test rather than a "does not throw".</para>
    /// </remarks>
    [Fact]
    public void SetDocumentLanguage_OnAMalformedDocument_CopiesItThroughAndReports()
    {
        byte[] rubbish = Encoding.ASCII.GetBytes("this is not a PDF at all, not even close");

        using var inMs = new MemoryStream(rubbish, writable: false);
        using var outMs = new MemoryStream();

        bool ok = PdfStructureTreeEditor.SetDocumentLanguage(inMs, outMs, "en-US");

        Assert.False(ok);
        Assert.Equal(rubbish, outMs.ToArray());
        Assert.Contains("could not be opened", PdfAccessibility.LastDiagnostic ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>The same for the counting variant, whose zero return is the honest answer.</summary>
    [Fact]
    public void SetStructureTabOrder_OnAMalformedDocument_CopiesItThroughAndReports()
    {
        byte[] rubbish = Encoding.ASCII.GetBytes("%PDF-1.7 but the xref is a lie");

        using var inMs = new MemoryStream(rubbish, writable: false);
        using var outMs = new MemoryStream();

        Assert.Equal(0, PdfStructureTreeEditor.SetStructureTabOrder(inMs, outMs));
        Assert.Equal(rubbish, outMs.ToArray());
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    private const string Font =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>A Type0 font whose <c>/Subtype</c> is an indirect reference (object 8).</summary>
    private static byte[] IndirectSubtypeType0Pdf() => Type0Pdf("8 0 R");

    private static byte[] DirectSubtypeType0Pdf() => Type0Pdf("/Type0");

    private static byte[] Type0Pdf(string subtype) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObject("", "BT /F1 12 Tf 72 700 Td <0041> Tj ET\n"),

        // The Type0 font, its descendant, and a descriptor carrying a program.
        "<< /Type /Font /Subtype " + subtype + " /BaseFont /Emb /Encoding /Identity-H "
            + "/DescendantFonts [6 0 R] /ToUnicode 9 0 R >>",
        "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Emb /CIDSystemInfo "
            + "<< /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 7 0 R >>",
        "<< /Type /FontDescriptor /FontName /Emb /Flags 4 /FontBBox [0 -200 1000 900] "
            + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 10 0 R >>",
        "/Type0",
        StreamObject("", "/CIDInit /ProcSet findresource begin end\n"),
        StreamObject("/Length1 4", "abcd"),
    });

    /// <summary>One page drawing a form whose only content is an inline image.</summary>
    private static byte[] InlineImageInsideFormPdf() => FormPagePdf(
        "q 20 0 0 20 0 0 cm BI /W 2 /H 2 /CS /G /BPC 8 ID \x01\x02\x03\x04 EI Q\n");

    /// <summary>The control: the same shape, but the form holds text.</summary>
    private static byte[] TextInsideFormPdf() => FormPagePdf(
        "BT /F1 10 Tf 10 10 Td (text inside the form) Tj ET\n");

    private static byte[] FormPagePdf(string formContent) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
        StreamObject("", "q 1 0 0 1 0 0 cm /Fm0 Do Q\n"),
        Font,
        StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
            + "/Resources << /Font << /F1 5 0 R >> >>", formContent),
    });

    /// <summary>A document with one AcroForm text field named <c>lastName</c>.</summary>
    private static byte[] FormFieldPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>",
        StreamObject("", "\n"),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (lastName) /Rect [72 700 272 720] /P 3 0 R >>",
    });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static PdfDictionary FirstPageFont(PdfDocument doc, string resourceName = "F1")
    {
        PdfDictionary resources = doc.Pages[0].Resources;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        var fontDict = doc.ResolveObject(fonts[new PdfName(resourceName)]) as PdfDictionary;
        Assert.NotNull(fontDict);
        return fontDict;
    }

    /// <summary>Assembles a classic-xref PDF; bodies[0] is object 1 and is always the catalog.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";
}
