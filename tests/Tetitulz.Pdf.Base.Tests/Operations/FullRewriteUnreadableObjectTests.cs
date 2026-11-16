// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A reachable object the full rewriter cannot parse is absent from its output and its references
/// dangle there — a difference from the input the caller could not tell from "the input never had
/// it", since the parser answers both with the null object. The rewriter now reports the unreadable
/// ones through the Base sink; a null object the file really holds (§7.3.9) is not one.
/// </summary>
public sealed class FullRewriteUnreadableObjectTests
{
    [Fact]
    public void AReachableObjectThatCannotBeParsed_IsReported_AndTheRestIsStillWritten()
    {
        List<string> events = Rewrite("]", out byte[] output);

        string report = Assert.Single(events, e => e.Contains("could not be read", StringComparison.Ordinal));
        Assert.Contains("object 5", report, StringComparison.Ordinal);
        Assert.Contains("1 reachable object", report, StringComparison.Ordinal);

        using var written = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(1, written.Pages.Count);
    }

    /// <summary>The control: the null object is what the file holds, and nothing is reported.</summary>
    [Fact]
    public void AReachableNullObject_IsNotReported()
    {
        List<string> events = Rewrite("null", out byte[] output);

        Assert.DoesNotContain(events, e => e.Contains("could not be read", StringComparison.Ordinal));
        using var written = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(1, written.Pages.Count);
    }

    // ----------------------------------------------------------------

    /// <summary>Rewrites a one-page document whose page's <c>/Resources</c> is object 5, with <paramref name="object5"/> as its body, capturing this thread's edit diagnostics.</summary>
    private static List<string> Rewrite(string object5, out byte[] output)
    {
        var events = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Handler(string message)
        {
            if (Environment.CurrentManagedThreadId == thread) events.Add(message);
        }

        DocumentEditDiagnostics.OnEvent += Handler;
        try
        {
            using var doc = PdfDocument.Open(new MemoryStream(Pdf(object5)));
            using var ms = new MemoryStream();
            Assert.True(PdfFullRewriter.TryFullRewrite(doc, ms));
            output = ms.ToArray();
        }
        finally
        {
            DocumentEditDiagnostics.OnEvent -= Handler;
        }
        return events;
    }

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
