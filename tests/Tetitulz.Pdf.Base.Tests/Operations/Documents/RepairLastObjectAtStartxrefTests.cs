// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The last object of a file ends at <c>startxref</c> when it carries no <c>endobj</c> of its own.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> <c>RawObjectScanner</c> treats <c>xref</c>, <c>trailer</c> and
/// <c>startxref</c> as the keywords that follow the last object of a file section. A mutation sweep
/// over CLAUDE.md's shared decision points removed <c>startxref</c> from that list and <b>nothing
/// reddened</b> — not the Maintenance plugin's repair suite, whose <c>PdfRepair</c> is the scanner's
/// main consumer, and not Base's own repair tests either. Every fixture in both ends its last object
/// with an <c>endobj</c>, or puts a <c>trailer</c> between it and the <c>startxref</c>.</para>
///
/// <para><b>A file with no <c>trailer</c> keyword is the ordinary modern shape, not a curiosity.</b>
/// A document using a cross-reference <i>stream</i> has no <c>trailer</c> at all: it ends
/// <c>… endobj / startxref / N / %%EOF</c>. Truncate or damage such a file so the final
/// <c>endobj</c> is lost — which is exactly the damage repair exists for — and <c>startxref</c> is
/// the only thing left marking where that object stops.</para>
///
/// <para>The catalog is deliberately the last object, so losing it is observable as a document with
/// no pages rather than as a subtly different byte count.</para>
/// </remarks>
public sealed class RepairLastObjectAtStartxrefTests
{
    [Fact]
    public void ALastObjectEndingAtStartxref_SurvivesRepair()
    {
        byte[] repaired = Repair(PdfEndingAtStartxref(withEndobj: false));

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(repaired, writable: false));
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// The acceptance control: the identical file whose last object <i>does</i> carry its
    /// <c>endobj</c>. Without it this would pass on a repair that recovered the catalog by some
    /// other route entirely.
    /// </summary>
    [Fact]
    public void TheSameFileWithItsEndobj_SurvivesRepairToo()
    {
        byte[] repaired = Repair(PdfEndingAtStartxref(withEndobj: true));

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(repaired, writable: false));
        Assert.Single(doc.Pages);
    }

    // ------------------------------------------------------------------

    private static byte[] Repair(byte[] pdf)
    {
        using var output = new MemoryStream();
        PdfRepair.Repair(new MemoryStream(pdf, writable: false), output);
        return output.ToArray();
    }

    /// <summary>
    /// A one-page file with <b>no <c>trailer</c> keyword and no cross-reference table</b> — the
    /// shape a cross-reference-stream document has once its xref is gone — whose catalog is the last
    /// object and is followed directly by <c>startxref</c>.
    /// </summary>
    private static byte[] PdfEndingAtStartxref(bool withEndobj)
    {
        var sb = new StringBuilder("%PDF-1.5\n");

        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        sb.Append("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        // The catalog last, so losing it is visible as a document with no pages.
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\n");
        if (withEndobj) sb.Append("endobj\n");

        sb.Append("startxref\n0\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
