// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Review-fix coverage for xref-stream robustness and scan-recovery semantics.
///
/// 1. A non-numeric <c>/W</c> or <c>/Index</c> element (e.g. <c>/W [/Foo 1 0]</c>
///    or <c>/Index [0 2 0 R]</c>) used to throw an uncaught
///    <c>InvalidOperationException</c> from <c>PdfArray.GetInteger</c>.  Only
///    <see cref="PdfParseException"/> is caught by ParseCrossReferences, so the
///    open hard-failed instead of falling into scan recovery.  The fix re-wraps
///    these as <see cref="PdfParseException"/> so recovery engages.
///
/// 2. The scan-recovery path used <c>AddEntry</c> (first-wins) while scanning the
///    file front-to-back, so for an incrementally-updated PDF the OLDEST revision
///    of a duplicated object won.  The fix uses <c>SetEntry</c> (last-wins).
/// </summary>
public class ReviewFixCore_XrefStreamRobustnessTests
{
    [Fact]
    public void NonNumericW_TriggersRecovery_NotUncaughtThrow()
    {
        // /W [/Foo 1 0]: element 0 is a name, not a number.  Pre-fix this threw
        // InvalidOperationException straight out of document open.  Post-fix it
        // becomes a PdfParseException that ParseCrossReferences catches, and the
        // scan-recovery rebuilds the xref from the "N G obj" bodies — so Open
        // succeeds and the catalog is resolvable.
        byte[] pdf = BuildXrefStreamPdfWithKeys("/W [/Foo 1 0]");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.NotNull(doc.Catalog);
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void IndirectRefIndex_TriggersRecovery_NotUncaughtThrow()
    {
        // /Index [0 2 0 R]: the second element is the start of an indirect
        // reference "2 0 R", which PdfArray.GetInteger cannot read.  Must
        // recover rather than throw uncaught.
        byte[] pdf = BuildXrefStreamPdfWithKeys("/W [1 1 0] /Index [0 2 0 R]");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.NotNull(doc.Catalog);
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void RecoveryWithTwoRevisions_ReturnsLaterRevision()
    {
        // Force scan-recovery (no startxref) over a file that defines object 1
        // twice.  The first (earlier in the file) definition is an integer 100;
        // the second is an integer 999.  Last-wins recovery must resolve object 1
        // to the LATER revision (999) — the value a conforming reader following
        // the (here, broken) incremental-update chain would see.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        // First revision of object 1.
        sb.Append("1 0 obj\n100\nendobj\n");
        // A catalog + page tree so the document is structurally complete after recovery.
        sb.Append("2 0 obj\n<< /Type /Catalog /Pages 3 0 R >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Pages /Kids [4 0 R] /Count 1 >>\nendobj\n");
        sb.Append("4 0 obj\n<< /Type /Page /Parent 3 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        // Second (newer) revision of object 1, appended later in the byte stream.
        sb.Append("1 0 obj\n999\nendobj\n");
        // A trailer dict so recovery can find /Root, but NO startxref keyword —
        // FindStartXref fails and RecoverXrefByScanning runs.
        sb.Append("trailer\n<< /Size 5 /Root 2 0 R >>\n%%EOF\n");

        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var obj1 = doc.GetObject(1);
        var num = Assert.IsType<PdfNumber>(obj1);
        Assert.Equal(999, num.IntValue);
    }

    /// <summary>
    /// Builds a PDF that points <c>startxref</c> at an xref stream whose dictionary
    /// carries the supplied (pathological) /W and/or /Index keys.  The file also
    /// contains fully parseable "N G obj" bodies plus a classic <c>trailer</c> dict,
    /// so when the xref-stream parse fails the scan-recovery path can rebuild the
    /// xref from the object bodies and recover /Root from the trailer.
    /// </summary>
    private static byte[] BuildXrefStreamPdfWithKeys(string extraDictKeys)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");

        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [4 0 R] /Count 1 >>\nendobj\n");
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // A small xref-stream body (content is irrelevant — the /W or /Index parse
        // fails before the body is interpreted).
        byte[] body = new byte[] { 0, 0, 1, 0, 1, 0 };

        long xrefStreamOffset = sb.Length;
        sb.Append("3 0 obj\n<< /Type /XRef /Size 5 ");
        sb.Append(extraDictKeys);
        sb.Append(" /Length ");
        sb.Append(body.Length);
        sb.Append(" /Root 1 0 R >>\nstream\n");

        var preamble = Encoding.ASCII.GetBytes(sb.ToString());
        using var ms = new MemoryStream();
        ms.Write(preamble, 0, preamble.Length);
        ms.Write(body, 0, body.Length);

        // A classic trailer dict gives the scan-recovery path a /Root to latch onto
        // (the recovery only mines /Root from a "trailer" keyword, not from the
        // xref-stream dict).
        var tail = Encoding.ASCII.GetBytes(
            "\nendstream\nendobj\ntrailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n"
            + xrefStreamOffset + "\n%%EOF");
        ms.Write(tail, 0, tail.Length);

        return ms.ToArray();
    }
}
