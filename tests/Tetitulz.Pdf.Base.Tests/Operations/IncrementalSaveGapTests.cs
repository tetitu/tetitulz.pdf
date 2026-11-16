// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>A report that says a signature covers the file but its <c>/Contents</c> reads what the gap holds.</summary>
public sealed class IncrementalSaveGapTests
{
    /// <summary>A signed field whose dictionary's <c>/Contents</c> is written as given, its range the whole file around the gap.</summary>
    private static byte[] Pdf(string contentsEntry, string gapText)
    {
        byte[] Build(long a, long b, long c)
        {
            string sig = "<< /Type /Sig /ByteRange [0 " + a.ToString("D10", CultureInfo.InvariantCulture) + " " + b.ToString("D10", CultureInfo.InvariantCulture)
                         + " " + c.ToString("D10", CultureInfo.InvariantCulture) + "] /Contents " + contentsEntry + " >>";
            var bodies = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
                "<< /Type /Pages /Kids [] /Count 0 >>",
                sig,
                "<< /FT /Sig /T (s) /V 3 0 R >>",
            };
            var sb = new StringBuilder("%PDF-1.7\n");
            var offsets = new List<int>();
            for (int i = 0; i < bodies.Count; i++)
            {
                offsets.Add(sb.Length);
                sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
            }
            int xref = sb.Length;
            sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            return Encoding.Latin1.GetBytes(sb.ToString());
        }

        string text = Encoding.Latin1.GetString(Build(0, 0, 0));
        int start = text.IndexOf(gapText, System.StringComparison.Ordinal);
        int end = start + gapText.Length;
        return Build(start, end, text.Length - end);
    }

    private static SignatureByteRangeReport Report(byte[] pdf)
        => Assert.Single(PdfIncrementalSaveValidator.Inspect(new MemoryStream(pdf)).SignatureReports);

    [Fact]
    public void AGapHoldingTheContents_IsReportedAsCoveringTheFile()
    {
        var report = Report(Pdf("<ABCD00>", "<ABCD00>"));

        Assert.True(report.CoversWholeFile);
        Assert.Null(report.Issue);
    }

    /// <summary>The range is the same shape; what it leaves out is not this signature's <c>/Contents</c>.</summary>
    [Fact]
    public void AdversarialGapHoldingSomethingElse_IsNotReportedAsCoveringTheFile()
    {
        var report = Report(Pdf("<ABCD00>", "/Type /Sig"));

        Assert.False(report.CoversWholeFile);
        Assert.Contains("gap", report.Issue, System.StringComparison.Ordinal);
    }

    /// <summary>A literal string has the bytes, not the hex form a gap this size is allowed: the room forgery needs.</summary>
    [Fact]
    public void AdversarialLiteralContents_InTheGap_IsNotReportedAsCoveringTheFile()
    {
        var report = Report(Pdf("(abc)", "(abc)"));

        Assert.False(report.CoversWholeFile);
        Assert.NotNull(report.Issue);
    }
}
