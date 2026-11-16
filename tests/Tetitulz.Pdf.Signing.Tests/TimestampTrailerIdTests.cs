// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The document timestamper copies the trailer <c>/ID</c> into the revision it appends. Those are
/// 16 random binary bytes, and they must arrive unchanged.
/// </summary>
/// <remarks>
/// The hand-rolled object writer it used emitted every <c>PdfString</c> as
/// <c>({EscapePdf(s.GetText())})</c> — a text DECODE followed by literal escaping, which does not
/// round-trip binary. A leading <c>FE FF</c> makes <c>GetText</c> read the remaining 14 bytes as
/// UTF-16, so the copied identifier is not the original one: incremental-update chains keyed on
/// <c>/ID</c>, and anything comparing it across revisions, see a different document.
/// </remarks>
public sealed class TimestampTrailerIdTests
{
    /// <summary>The identifier bytes: a UTF-16 BOM followed by values that are not valid text.</summary>
    private static readonly byte[] BinaryId =
    {
        0xFE, 0xFF, 0x00, 0x01, 0x80, 0x81, 0x0A, 0x0D,
        0x28, 0x29, 0x5C, 0xFF, 0x7F, 0x00, 0xAB, 0xCD,
    };

    [Fact]
    public void TheAppendedRevision_PreservesTheTrailerIdentifierBytes()
    {
        using var input = new MemoryStream(PdfWithBinaryId());
        using var output = new MemoryStream();

        // No TSA is reachable; the revision (and its trailer) is written before that call.
        Record.Exception(() =>
            PdfSigningOperations.AddDocumentTimestamp(input, output, "http://localhost:1/tsa"));

        byte[] written = output.ToArray();
        Assert.True(written.Length > 0, "no revision was written, so there is no /ID to compare");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        var idArray = doc.Trailer.GetArray(PdfName.ID);
        Assert.NotNull(idArray);
        var first = idArray![0] as PdfString;
        Assert.NotNull(first);

        Assert.Equal(BinaryId, first!.RawBytes);
    }

    private static byte[] PdfWithBinaryId()
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        string hex = BitConverter.ToString(BinaryId).Replace("-", "").ToLowerInvariant();

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat(
            "trailer\n<< /Size {0} /Root 1 0 R /ID [<{1}> <{1}>] >>\nstartxref\n{2}\n%%EOF\n",
            offsets.Count + 1, hex, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
