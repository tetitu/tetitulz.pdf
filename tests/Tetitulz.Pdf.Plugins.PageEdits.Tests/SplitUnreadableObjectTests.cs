// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// An object the kept pages reference that cannot be parsed is absent from a split's output, and the
/// output's references to it dangle — which the caller could not tell from an object the source never
/// had, since the parser answers both with the null object. <see cref="PdfSplitter.LastObjectWarning"/>
/// names the unreadable ones; a null object the file really holds (§7.3.9) is not one.
/// </summary>
public sealed class SplitUnreadableObjectTests
{
    [Fact]
    public void SplitRange_AResourceObjectThatCannotBeParsed_IsReported_AndThePageIsStillWritten()
    {
        using var input = new MemoryStream(Pdf("]"));
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);

        Assert.NotNull(PdfSplitter.LastObjectWarning);
        Assert.Contains("object 5", PdfSplitter.LastObjectWarning, StringComparison.Ordinal);
        Assert.Contains("1 object(s)", PdfSplitter.LastObjectWarning, StringComparison.Ordinal);

        using var written = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(1, written.Pages.Count);
    }

    /// <summary>The control: the null object is what the file holds, and nothing is reported.</summary>
    [Fact]
    public void SplitRange_AResourceThatIsTheNullObject_ReportsNothing()
    {
        using var input = new MemoryStream(Pdf("null"));
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);

        Assert.Null(PdfSplitter.LastObjectWarning);
        using var written = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(1, written.Pages.Count);
    }

    /// <summary>One page whose <c>/Resources</c> is object 5, with <paramref name="object5"/> as its body.</summary>
    private static byte[] Pdf(string object5)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources 5 0 R >>",
            "<< /Length 5 >>\nstream\n0 0 m\nendstream",
            object5,
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
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
