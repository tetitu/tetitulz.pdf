// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A composite font's <c>/W</c> array is indexed by <b>CID</b> (§9.7.4.3), not by the raw character
/// code. The two are equal only under <c>Identity-H</c> — which is why keying on the code looked
/// correct for years: the fixtures that exercised these paths all used Identity.
///
/// <para><b>Where it bit.</b> <c>PdfTemplateEngine</c> and <c>PdfRedactionEngine</c> both computed
/// <c>fi.Font?.GetWidth(c.Value)</c>. In the redactor that width feeds <c>GlyphIntersects</c>,
/// which decides whether a glyph overlaps a redaction rectangle — so a wrong width there does not
/// merely misplace text, it can make an overlapping glyph look as though it does not overlap and
/// <b>leave content the caller asked to destroy</b>. The identical defect was found in
/// <c>PdfTextExtractor</c> by the row that consolidated composite code splitting; that row reported
/// these two sites and correctly declined to touch files outside its scope.</para>
///
/// <para>These assert the advance through the same <c>SplitCodes</c> + <c>GetWidth</c> pair the
/// production code uses, on a font whose encoding makes CID differ from code. A pixel-comparing
/// test could not see this: the glyph drawn is right, only the distance to the next one is
/// wrong.</para>
///
/// <para><b>WHAT THESE DO NOT COVER — established by a surviving mutation, not assumed.</b>
/// Reverting <b>both</b> editors to <c>GetWidth(c.Value)</c> leaves every test in this file
/// <b>green</b>. They exercise the <c>PdfFont</c> primitives the editors are supposed to use —
/// <c>SplitCodes</c>, <c>GetWidth</c>, <c>IsWordSpaceCode</c> — and never invoke
/// <c>PdfTemplateEngine</c> or <c>PdfRedactionEngine</c> at all, so nothing here can fail if an
/// editor stops using them. The class is named for what it actually covers rather than for the
/// row that produced it.</para>
///
/// <para>An editor-level test — a redaction over a page whose text is set in a non-Identity
/// composite font, asserting the covered glyph really is destroyed — is still owed and is filed as
/// its own row. The production fix itself is not in doubt: it is the same defect, in the same
/// expression, that the composite-splitting row proved and fixed in <c>PdfTextExtractor</c> with an
/// establishing run before it changed anything.</para>
/// </summary>
public class CompositeFontAdvancePrimitivesTests
{
    /// <summary>
    /// Builds a one-page document whose font F0 is either a Type 0 font whose <c>/Encoding</c> CMap
    /// maps code <c>0x0041</c> to <b>CID 5</b>, or a simple Type 1 font.
    ///
    /// <para><c>/W</c> gives CID 5 a width of <b>800</b> and code 65 a width of <b>200</b>. Those
    /// two numbers are the entire point: they are what separates a CID-keyed read from a
    /// code-keyed one, and a fixture using Identity-H could not tell them apart.</para>
    /// </summary>
    private static byte[] DocumentWithFont(bool composite)
    {
        const string cmap =
            "/CIDInit /ProcSet findresource begin\n" +
            "begincmap\n" +
            "begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 begincidrange\n<0041> <0041> 5\nendcidrange\n" +
            "endcmap\n";

        string font = composite
            ? "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 6 0 R /DescendantFonts [7 0 R] >>"
            : "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F0 4 0 R >> >> >>",
            font,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",   // filler, keeps numbering stable
        };
        if (composite)
        {
            bodies.Add($"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream");
            bodies.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test "
                       + "/W [5 [800] 65 [200]] /DW 1000 >>");
        }

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PdfFont LoadF0(PdfDocument doc)
    {
        var fonts = doc.Pages[0].Resources.GetDictionary(PdfName.Font);
        return PdfFont.Create(doc.ResolveObject(fonts[new PdfName("F0")]) as PdfDictionary, doc);
    }

    /// <summary>
    /// The defect, expressed exactly as the editors express it: split the string, then ask for the
    /// width. Keyed on <c>Cid</c> this is 800; keyed on <c>Value</c> it was 200.
    /// </summary>
    [Fact]
    public void TheAdvanceComesFromTheCidWidthNotTheRawCodeWidth()
    {
        using var doc = PdfDocument.Open(new MemoryStream(DocumentWithFont(composite: true)));
        var font = LoadF0(doc);

        var code = Assert.Single(font.SplitCodes(new byte[] { 0x00, 0x41 }));
        Assert.Equal(0x0041, code.Value);
        Assert.Equal(5, code.Cid);

        Assert.Equal(800, font.GetWidth(code.Cid));
        // The trap must still be live, or this fixture has stopped separating the two reads.
        Assert.NotEqual(font.GetWidth(code.Cid), font.GetWidth(code.Value));
    }

    /// <summary>
    /// The control that keeps the assertion above from being about this fixture alone: for a
    /// SIMPLE font <c>SplitCodes</c> sets <c>Cid == Value</c>, so a caller keyed on <c>Cid</c> is
    /// correct for both kinds and the fix cannot have broken simple fonts.
    /// </summary>
    [Fact]
    public void ASimpleFontsCidIsItsCodeSoKeyingOnCidIsCorrectForBothKinds()
    {
        using var doc = PdfDocument.Open(new MemoryStream(DocumentWithFont(composite: false)));
        var font = LoadF0(doc);

        var codes = font.SplitCodes(new byte[] { 0x41, 0x42 });

        Assert.Equal(2, codes.Count);
        Assert.All(codes, c => Assert.Equal(c.Value, c.Cid));
    }

    /// <summary>
    /// §9.3.3 restricts word spacing to the <b>single-byte</b> code 32. Both editors re-implemented
    /// that inline as <c>len == 1 &amp;&amp; code == 32</c>; the shared
    /// <c>CMapCodec.Code.IsWordSpaceCode</c> is the one rule.
    ///
    /// <para>Both halves are asserted now. The malformed one used to be an open defect and is
    /// recorded here because it is the interesting case: an odd trailing byte with no room for the
    /// two-byte code it belongs to was emitted with <c>ByteLength == 1</c>, so
    /// <c>IsWordSpaceCode</c> answered <b>true</b> for it — in a font whose codespace defines no
    /// single-byte code at all. <c>ByteLength</c> answers "how many bytes were consumed", which a
    /// fragment satisfies without ever matching a one-byte range.</para>
    /// </summary>
    [Fact]
    public void AWholeTwoByteCodeWhoseLowByteIs32IsNotAWordSpace()
    {
        using var doc = PdfDocument.Open(new MemoryStream(DocumentWithFont(composite: true)));
        var font = LoadF0(doc);

        var code = Assert.Single(font.SplitCodes(new byte[] { 0x00, 0x20 }));
        Assert.Equal(2, code.ByteLength);
        Assert.Equal(0x0020, code.Value);
        Assert.False(code.IsWordSpaceCode);
    }

    /// <summary>
    /// The malformed half: a trailing byte the two-byte codespace has no room to complete. It
    /// consumes one byte, so <c>ByteLength == 1</c> — but it matched no one-byte codespace range,
    /// and §9.3.3 applies word spacing only to a font that "defines code 32 as a single-byte
    /// code". This font defines none.
    /// </summary>
    [Fact]
    public void AnOrphanTrailingByte32IsNotAWordSpaceInATwoByteOnlyFont()
    {
        using var doc = PdfDocument.Open(new MemoryStream(DocumentWithFont(composite: true)));
        var font = LoadF0(doc);

        var codes = font.SplitCodes(new byte[] { 0x00, 0x41, 0x20 });

        Assert.Equal(2, codes.Count);
        var orphan = codes[1];
        Assert.Equal(1, orphan.ByteLength);      // one byte WAS consumed …
        Assert.Equal(32, orphan.Value);
        Assert.False(orphan.IsWordSpaceCode);    // … and it is still not a single-byte code
    }

    /// <summary>
    /// The positive half, so "never a word space" is not satisfied by a predicate that is always
    /// false: a simple font's single-byte code 32 IS one.
    /// </summary>
    [Fact]
    public void ASingleByteCode32IsAWordSpace()
    {
        using var doc = PdfDocument.Open(new MemoryStream(DocumentWithFont(composite: false)));
        var font = LoadF0(doc);

        Assert.True(Assert.Single(font.SplitCodes(new byte[] { 0x20 })).IsWordSpaceCode);
    }
}
