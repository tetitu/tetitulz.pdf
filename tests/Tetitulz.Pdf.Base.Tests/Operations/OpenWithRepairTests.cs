// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What the parser recovers on its own, and what <see cref="PdfDocumentOpener.OpenWithRepair"/>
/// adds on top.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written to correct a wrong conclusion.</b> A malformed-input probe reported that a document
/// with a bad <c>startxref</c> "opens with zero pages", and plan 09 proposed automatic repair on
/// that basis. The probe fixture contained <i>no page objects at all</i>, so its zero was correct
/// and said nothing about recovery. The first test below is what actually happens, and it is the
/// opposite: <see cref="PdfDocument.Open"/> rebuilds by scanning and finds the page unaided.
/// </para>
/// <para>
/// That makes these tests worth more than the feature that prompted them: the parser's own
/// recovery was undocumented and untested, and it is the behaviour most callers actually depend on.
/// </para>
/// </remarks>
public class OpenWithRepairTests
{
    private const string Marker = "RECOVERABLETEXT";

    /// <summary>
    /// The parser rebuilds an unusable cross-reference table by scanning, with no repair pass and
    /// no caller involvement.
    /// </summary>
    [Fact]
    public void PlainOpen_BrokenStartxrefButIntactObjects_AlreadyRecoversThePage()
    {
        using var doc = PdfDocument.Open(new MemoryStream(BuildPdf(brokenStartxref: true)));

        Assert.Single(doc.Pages);
        Assert.Contains(Marker, Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc));
    }

    /// <summary>
    /// A healthy document is returned untouched and reports no repair, so the diagnostic means
    /// something when it is non-null.
    /// </summary>
    [Fact]
    public void OpenWithRepair_HealthyDocument_IsUntouchedAndReportsNoRepair()
    {
        using var doc = PdfDocumentOpener.OpenWithRepair(new MemoryStream(BuildPdf(brokenStartxref: false)));

        Assert.Single(doc.Pages);
        Assert.Null(PdfDocumentOpener.LastRepairOutcome);
    }

    /// <summary>
    /// Repair cannot invent pages. When there is nothing to recover the caller gets their own
    /// document back, empty but honest, and no repair is claimed.
    /// </summary>
    /// <remarks>
    /// This pins the "never make it worse" half of the fallback: returning a differently-broken
    /// document in place of the caller's would be worse than returning theirs.
    /// </remarks>
    [Fact]
    public void OpenWithRepair_NothingRecoverable_ReturnsTheOriginalWithoutClaimingARepair()
    {
        byte[] pdf = Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< >>\nendobj\ntrailer\n<< /Root 1 0 R >>\nstartxref\n999999\n%%EOF\n");

        using var doc = PdfDocumentOpener.OpenWithRepair(new MemoryStream(pdf));

        Assert.Empty(doc.Pages);
        Assert.Null(PdfDocumentOpener.LastRepairOutcome);
    }

    /// <summary>
    /// A one-page document with real text. When <paramref name="brokenStartxref"/> the offset points
    /// past the end of the file, so the table cannot be read — but every object is intact.
    /// </summary>
    private static byte[] BuildPdf(bool brokenStartxref)
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string content = $"BT /F1 24 Tf 20 100 Td ({Marker}) Tj ET";

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
            "/Contents 5 0 R /Resources << /Font << /F1 4 0 R >> >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", offsets.Count + 1);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", brokenStartxref ? 999999 : xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
