// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// An object served from the recovery scan — a cross-reference table rebuilt from the headers, or a
/// gap filled from one — carries the offset of the header it was read from, and the change analysis
/// classifies by that offset: a copy appended past a signature's end is a change after the signature,
/// however the table came to name it (the review of batch 131 asked that this be so).
/// </summary>
public class RecoveredObjectChangeTests
{
    /// <summary>
    /// Revision 1 is the signed one; revision 2 appends a new object and a damaged section, so the whole
    /// table is rebuilt from the headers. At DocMDP level 1 the appended object is a violation; the same
    /// signed revision with the damage but nothing appended is compliant.
    /// </summary>
    [Fact]
    public void AnObjectServedFromItsHeader_PastTheSignedEnd_IsAChangeAfterTheSignature()
    {
        byte[] changed = Build(appendObject: true, out long signedEnd);
        using (var doc = PdfDocument.Open(new MemoryStream(changed)))
        {
            DocMdpAnalysis analysis = DocMdpAnalyzer.AnalyzeChangesSince(doc, signedEnd, changed.Length, permission: 1);
            Assert.True(analysis.HasIncrementalUpdates);
            Assert.False(analysis.IsCompliant);
            Assert.Contains(analysis.Violations, v => v.Contains("obj 6", StringComparison.Ordinal));
        }

        byte[] unchanged = Build(appendObject: false, out long end);
        using (var doc = PdfDocument.Open(new MemoryStream(unchanged)))
        {
            DocMdpAnalysis analysis = DocMdpAnalyzer.AnalyzeChangesSince(doc, end, unchanged.Length, permission: 1);
            Assert.True(analysis.IsCompliant, analysis.Summary);
        }
    }

    private static byte[] Build(bool appendObject, out long signedEnd)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[8];
        void Obj(int n, string body) { offsets[n] = sb.Length; sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        Obj(4, "<< /Length 0 >>\nstream\n\nendstream");
        int xref1 = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        for (int n = 1; n <= 4; n++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[n]);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref1).Append("\n%%EOF\n");
        signedEnd = sb.Length;

        if (appendObject) Obj(6, "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>");
        // The appended section is damaged, so the table is rebuilt from every header in the file.
        int xref2 = sb.Length;
        sb.Append("xref\n0 1\nDAMAGED\n");
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R /Prev ").Append(xref1).Append(" >>\nstartxref\n").Append(xref2).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
