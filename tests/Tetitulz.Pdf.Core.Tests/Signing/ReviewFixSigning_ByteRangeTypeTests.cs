// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// §88 H2: PdfSignatureVerifier must return a clean validation error (not throw
/// InvalidOperationException) when a signature dictionary's /ByteRange array
/// contains a non-numeric element such as a PDF null literal.
///
/// Pre-fix: byteRangeArray.GetLong(i) on a PdfNull element threw
/// InvalidOperationException, which propagated as an unhandled exception.
/// Post-fix: the type is validated first and a descriptive validation error is
/// recorded instead.
/// </summary>
public class ReviewFixSigning_ByteRangeTypeTests
{
    [Fact]
    public void VerifyAll_ByteRangeWithNullElement_ReturnsValidationErrorNotException()
    {
        byte[] pdf = BuildPdfWithNullInByteRange();

        using var stream = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(stream);

        var verifier = new PdfSignatureVerifier();

        // Must not throw — pre-fix this raised InvalidOperationException.
        var results = verifier.VerifyAll(doc);

        Assert.Single(results);
        var r = results[0];
        Assert.False(r.IsIntegrityValid);
        Assert.Contains(r.ValidationErrors,
            e => e.Contains("ByteRange", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifyAll_ByteRangeWithAllNullElements_ReturnsValidationErrorNotException()
    {
        byte[] pdf = BuildPdfWithAllNullByteRange();

        using var stream = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(stream);

        var verifier = new PdfSignatureVerifier();
        var results = verifier.VerifyAll(doc);

        Assert.Single(results);
        Assert.False(results[0].IsIntegrityValid);
        Assert.NotEmpty(results[0].ValidationErrors);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal PDF that contains one signature field whose /ByteRange
    /// is [0 null 100 200] — a null literal at index 1.
    /// </summary>
    private static byte[] BuildPdfWithNullInByteRange()
        => BuildPdfWithByteRange("[0 null 100 200]");

    /// <summary>Builds a minimal PDF with /ByteRange [null null null null].</summary>
    private static byte[] BuildPdfWithAllNullByteRange()
        => BuildPdfWithByteRange("[null null null null]");

    private static byte[] BuildPdfWithByteRange(string byteRangeTokens)
    {
        // Build with exact byte offsets so the xref table is valid.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1Off = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>\nendobj\n");

        int obj2Off = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3Off = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        int obj4Off = sb.Length;
        sb.Append("4 0 obj\n<< /Fields [5 0 R] >>\nendobj\n");

        int obj5Off = sb.Length;
        // Signature widget: /FT /Sig causes PdfFormField.Create() to return PdfSignatureField.
        sb.Append("5 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /Rect [0 0 0 0] /V 6 0 R >>\nendobj\n");

        int obj6Off = sb.Length;
        // Signature dictionary with a malformed /ByteRange (contains null literals).
        sb.Append("6 0 obj\n");
        sb.Append($"<< /Type /Sig /SubFilter /adbe.pkcs7.detached /ByteRange {byteRangeTokens} >>\n");
        sb.Append("endobj\n");

        int xrefOff = sb.Length;
        sb.Append("xref\n0 7\n");
        sb.AppendFormat("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1Off);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2Off);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3Off);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4Off);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5Off);
        sb.AppendFormat("{0:D10} 00000 n \n", obj6Off);
        sb.Append("trailer\n<< /Root 1 0 R /Size 7 >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
