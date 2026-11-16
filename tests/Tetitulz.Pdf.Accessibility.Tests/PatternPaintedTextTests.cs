// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The ruling on text painted with a tiling pattern (GAPS.md §5c, §20d): it is <b>real content</b>.
/// What makes content real is what it says, not how its ink is laid down — ISO 14289-1 §7.1 makes
/// every piece of content either tagged or an artifact, and the Matterhorn Protocol's checkpoint 01
/// fails "real content is marked as artifact" (01-002) and "content is neither marked as Artifact
/// nor tagged as real content" (01-005) with no exception for a paint. A pattern fill is a paint,
/// as a colour is. So a pattern-painted run outside every marked-content sequence is UA17, exactly
/// as a flat-coloured one is, and one the producer marked as an artifact is accepted as that
/// producer's statement — the checker cannot read meaning, and does not try.
/// </summary>
public class PatternPaintedTextTests
{
    [Fact]
    public void PatternPaintedText_OutsideEveryMarkedContentSequence_IsUntaggedContent()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(Pdf(PatternText("BT /F1 24 Tf 72 700 Td (Pattern text) Tj ET"))));
        AccessibilityIssue ua17 = Assert.Single(report.Issues, i => i.Code == "UA17");
        Assert.Equal(IssueSeverity.Error, ua17.Severity);
        Assert.Contains("1 text-showing operator(s)", ua17.Message);

        // The control: the same run in a flat colour is reported the same way.
        var flat = PdfAccessibility.Audit(new MemoryStream(Pdf("0 g BT /F1 24 Tf 72 700 Td (Flat text) Tj ET")));
        Assert.Single(flat.Issues, i => i.Code == "UA17");
    }

    [Fact]
    public void PatternPaintedText_MarkedAsAnArtifact_IsNotReportedAsUntagged()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(Pdf(
            "/Artifact BMC " + PatternText("BT /F1 24 Tf 72 700 Td (Pattern text) Tj ET") + " EMC")));
        Assert.DoesNotContain(report.Issues, i => i.Code == "UA17");
        // Potency: the audit saw the page and said something about the document.
        Assert.NotEmpty(report.Issues);
    }

    private static string PatternText(string text) => "/Pattern cs /P1 scn " + text;

    /// <summary>A document that claims to be tagged, one page, a tiling pattern resource.</summary>
    private static byte[] Pdf(string content)
    {
        const string cell = "0 0 1 rg 0 0 4 4 re f";
        var sb = new StringBuilder();
        var offsets = new int[8];
        sb.Append("%PDF-1.7\n");
        void Obj(int n, string body) { offsets[n] = sb.Length; sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /Pattern << /P1 7 0 R >> >> /Contents 4 0 R >>");
        Obj(4, "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj(6, "<< /Type /StructTreeRoot /K [] >>");
        Obj(7, "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 8 8] /XStep 8 /YStep 8 /Resources << >> /Length " + cell.Length + " >>\nstream\n" + cell + "\nendstream");
        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i < 8; i++) sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
