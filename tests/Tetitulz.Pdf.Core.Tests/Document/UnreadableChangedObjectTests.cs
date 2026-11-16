// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A changed object the analyzer cannot read is a violation, never a change it can classify. The
/// parser answers an unparseable object with the null object, which is also what "the certified
/// revision had nothing there" yields — so an unreadable rewrite read as a <i>deletion</i> and an
/// unreadable certified value as <i>never defined</i>: at DocMDP /P 3, where deleting an annotation
/// is permitted, a rewrite of an annotation into garbage passed as a permitted deletion, and freeing
/// an object whose certified value could not be read passed as a free-list slot.
/// </summary>
public sealed class UnreadableChangedObjectTests
{
    private const string Annotation = "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>";
    // A stray array close: the one shape the tolerant parser refuses outright (an unterminated
    // dictionary reads on into whatever follows and comes back as a dictionary).
    private const string Garbage = "]";

    [Fact]
    public void AnAnnotationRewrittenIntoSomethingUnreadable_IsAViolationAtP3_NotAPermittedDeletion()
    {
        byte[] pdf = Build(certifiedObject5: Annotation, laterObject5: Garbage, out long signedEnd);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        DocMdpAnalysis analysis = DocMdpAnalyzer.AnalyzeChangesSince(doc, signedEnd, pdf.Length, permission: 3);

        Assert.False(analysis.IsCompliant, analysis.Summary);
        string violation = Assert.Single(analysis.Violations, v => v.Contains("obj 5", StringComparison.Ordinal));
        Assert.Contains("current revision's object could not be read", violation, StringComparison.Ordinal);
    }

    /// <summary>The control: the same annotation genuinely deleted is the change /P 3 permits.</summary>
    [Fact]
    public void AnAnnotationFreed_IsPermittedAtP3()
    {
        byte[] pdf = Build(certifiedObject5: Annotation, laterObject5: null, out long signedEnd);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        DocMdpAnalysis analysis = DocMdpAnalyzer.AnalyzeChangesSince(doc, signedEnd, pdf.Length, permission: 3);

        Assert.True(analysis.IsCompliant, analysis.Summary);
    }

    [Fact]
    public void FreeingAnObjectWhoseCertifiedValueCannotBeRead_IsAViolation_NotAFreeListSlot()
    {
        byte[] pdf = Build(certifiedObject5: Garbage, laterObject5: null, out long signedEnd);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        DocMdpAnalysis analysis = DocMdpAnalyzer.AnalyzeChangesSince(doc, signedEnd, pdf.Length, permission: 3);

        Assert.False(analysis.IsCompliant, analysis.Summary);
        string violation = Assert.Single(analysis.Violations, v => v.Contains("obj 5", StringComparison.Ordinal));
        Assert.Contains("certified value could not be read", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void RewritingAnObjectWhoseCertifiedValueCannotBeRead_IsAViolation_NotAPermittedAddition()
    {
        byte[] pdf = Build(certifiedObject5: Garbage, laterObject5: Annotation, out long signedEnd);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        DocMdpAnalysis analysis = DocMdpAnalyzer.AnalyzeChangesSince(doc, signedEnd, pdf.Length, permission: 3);

        Assert.False(analysis.IsCompliant, analysis.Summary);
        string violation = Assert.Single(analysis.Violations, v => v.Contains("obj 5", StringComparison.Ordinal));
        Assert.Contains("certified revision's object could not be read", violation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Revision 1 — the certified one — has a page with one annotation, object 5, whose body is
    /// <paramref name="certifiedObject5"/>. Revision 2 rewrites object 5 as <paramref name="laterObject5"/>,
    /// or frees it when that is null, through a well-formed cross-reference section.
    /// </summary>
    private static byte[] Build(string certifiedObject5, string laterObject5, out long signedEnd)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[7];
        void Obj(int n, string body) { offsets[n] = sb.Length; sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>");
        Obj(4, "<< /Length 0 >>\nstream\n\nendstream");
        Obj(5, certifiedObject5);
        int xref1 = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int n = 1; n <= 5; n++) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[n]);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref1).Append("\n%%EOF\n");
        signedEnd = sb.Length;

        if (laterObject5 != null) Obj(5, laterObject5);
        int xref2 = sb.Length;
        sb.Append("xref\n5 1\n");
        if (laterObject5 != null) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[5]);
        else sb.Append("0000000000 00001 f \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R /Prev ").Append(xref1).Append(" >>\nstartxref\n").Append(xref2).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
