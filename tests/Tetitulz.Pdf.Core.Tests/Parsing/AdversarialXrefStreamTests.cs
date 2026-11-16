// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Defensive parser-bounds tests added in GAPS §39.C1, §39.C2, §40.P13.
/// Each test crafts a minimal in-memory PDF whose cross-reference stream
/// or object stream contains a bounds-violating value and asserts the
/// parser refuses the section instead of looping forever / silently truncating /
/// wrapping integer arithmetic.
///
/// <para><b>The evidence of the refusal is <see cref="PdfParser.LastXrefSectionError"/>, not an
/// exception out of <see cref="PdfDocument.Open"/>.</b> These tests used to assert the throw,
/// which was only ever an indirect proxy: <c>ParseCrossReferences</c> catches a bad section,
/// records the reason and falls back to the recovery scan, and the exception escaped merely
/// because that scan could not then produce a <c>/Root</c> for an xref-stream file — it looked
/// only for the <c>trailer</c> keyword, which such a file does not contain. The scan now reads
/// <c>/Root</c> out of the recovered <c>/Type /XRef</c> dictionary, so the document opens and the
/// refusal has to be asserted where it is actually recorded. Deleting the /W guard makes
/// <c>LastXrefSectionError</c> null, so each of these still fails when the guard is removed.</para>
/// </summary>
public class AdversarialXrefStreamTests
{
    /// <summary>
    /// Opens <paramref name="pdf"/> and asserts that the cross-reference section was refused and
    /// the file was rebuilt by scanning: object 1 is reachable and is the catalog it declares.
    /// </summary>
    private static void AssertSectionRefusedAndRecovered(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.NotNull(PdfParser.LastXrefSectionError);
        Assert.Contains("xref section at", PdfParser.LastXrefSectionError);

        // Recovered, not merely "opened": the catalog resolves to the object the file declares and
        // its /Pages points at object 2. A table that had swallowed the malformed /W would resolve
        // object 1 to whatever byte the bad entry width computed.
        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        var pages = Assert.IsType<PdfReference>(doc.Catalog.Dictionary[PdfName.Pages]);
        Assert.Equal(2, pages.ReferenceObjectNumber);
    }

    // ==================================================================
    //  Xref-stream /W field width validation (§39.C1)
    // ==================================================================

    [Fact]
    public void XrefStream_NegativeFieldWidth_Rejected()
    {
        // Build a minimal PDF whose xref stream's /W array contains -1.
        // Without the §39.C1 guard, this would underflow entrySize and
        // either loop forever or read out-of-bounds.
        AssertSectionRefusedAndRecovered(BuildXrefStreamPdf("/W [-1 1 0]"));
    }

    [Fact]
    public void XrefStream_OversizedFieldWidth_Rejected()
    {
        // /W field > 8 bytes is implausible; rejected at parse time.
        AssertSectionRefusedAndRecovered(BuildXrefStreamPdf("/W [0 9 0]"));
    }

    [Fact]
    public void XrefStream_ZeroEntrySize_Rejected()
    {
        // /W [0 0 0] means each entry is 0 bytes — would cause infinite loop.
        AssertSectionRefusedAndRecovered(BuildXrefStreamPdf("/W [0 0 0]"));
    }

    // ==================================================================
    //  Xref-stream /Index bounds (§39.C2)
    // ==================================================================

    [Fact]
    public void XrefStream_NegativeIndexCount_Rejected()
    {
        // Negative /Index count would wrap on (long)start + count.
        AssertSectionRefusedAndRecovered(BuildXrefStreamPdf("/W [1 1 0] /Index [0 -1]"));
    }

    /// <summary>
    /// The honest negative: a well-formed cross-reference stream must refuse nothing. Without this
    /// the four tests above would still pass if <c>LastXrefSectionError</c> were set unconditionally.
    /// </summary>
    [Fact]
    public void XrefStream_WellFormed_RefusesNothing()
    {
        byte[] pdf = BuildXrefStreamPdf("/W [1 1 0]");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>
    /// Builds a minimal PDF that uses an xref stream (rather than a
    /// classic <c>xref</c> table) so the /W validation path is taken.
    /// The <paramref name="extraDictKeys"/> string is interpolated into
    /// the xref stream's dict so callers can supply pathological values.
    /// </summary>
    private static byte[] BuildXrefStreamPdf(string extraDictKeys)
    {
        // Object 1: the catalog
        // Object 2: the page tree (empty, zero pages)
        // Object 3: the xref stream itself, referenced by /startxref
        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");

        long o1Offset = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long o2Offset = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        // Build a minimal xref-stream body with one normal entry pointing at o1.
        // /W [1 1 0] means each entry is 2 bytes: 1-byte type + 1-byte offset.
        // We supply 4 entries (0..3): free-list head, o1, o2, the xref-stream itself.
        // That's 8 bytes of body data.
        byte[] body = new byte[]
        {
            0, 0,        // entry 0: free, offset 0
            1, (byte)o1Offset,   // entry 1: normal, offset of o1 (low byte only)
            1, (byte)o2Offset,   // entry 2: normal, offset of o2
            1, 0,        // entry 3: normal, offset placeholder (we'll patch below)
        };

        long xrefStreamOffset = sb.Length;
        body[7] = (byte)xrefStreamOffset;

        sb.Append("3 0 obj\n<< /Type /XRef /Size 4 ");
        sb.Append(extraDictKeys);
        sb.Append(" /Length ");
        sb.Append(body.Length);
        sb.Append(" /Root 1 0 R >>\nstream\n");
        long streamDataOffset = sb.Length;

        // Convert sb to bytes, append body, then continue with endstream + trailer.
        var preamble = Encoding.ASCII.GetBytes(sb.ToString());
        using var ms = new MemoryStream();
        ms.Write(preamble, 0, preamble.Length);
        ms.Write(body, 0, body.Length);

        var tail = Encoding.ASCII.GetBytes("\nendstream\nendobj\nstartxref\n" + xrefStreamOffset + "\n%%EOF");
        ms.Write(tail, 0, tail.Length);

        return ms.ToArray();
    }
}
