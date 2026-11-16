// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page whose <c>/Contents</c> is an indirect reference to an <b>array</b> of streams must be
/// redactable — the shape ladder that reads it matched the wrong branch and declared the page to
/// have no content at all.
/// </summary>
/// <remarks>
/// <para><b>The bug is in the order, not in a missing case.</b> <c>TrySubstitutePageContent</c>
/// tested <c>is PdfReference</c> first and demanded the target be a <c>PdfStream</c>. So
/// <c>/Contents 9 0 R</c> where object 9 is <c>[4 0 R 5 0 R]</c> <i>matched</i> that branch,
/// resolved to a <c>PdfArray</c>, added nothing, and fell out with no parts — never reaching the
/// array branch two lines below, which would have handled it correctly.</para>
///
/// <para>Both callers then throw, <i>after</i> the replacement bytes have already been computed:
/// <c>PdfRedactionEngine</c> raises "Target page has no content stream to redact." and
/// <c>PdfTemplateEngine</c> its own equivalent. A page with perfectly ordinary content is declared
/// to have none, and the operation is refused rather than silently wrong — which is the better of
/// the two failures, but still wrong.</para>
///
/// <para><c>PdfPageContentAppender.CopyExistingContents</c> resolves before branching for exactly
/// this reason, and says so in its own comment. This copy did not: the two implementations of
/// "read the page's contents" had already been consolidated once, and this is the third.</para>
///
/// <para>Asserted on the redacted output's text, not on "the call did not throw" — a refusal is a
/// throw, so <c>Record.Exception(…); Assert.Null(ex);</c> would be the whole test, and would pass
/// with the redaction doing nothing.</para>
/// </remarks>
public class IndirectContentsRewriteTests
{
    /// <summary>
    /// Redaction removes the word whichever way the page's <c>/Contents</c> is spelled.
    /// <c>Direct</c> is the control: an inline array always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Contents [4 0 R 9 0 R]
    [InlineData(true)]   // /Contents 10 0 R  ->  [4 0 R 9 0 R]
    public void RedactionRewritesThePage_WhicheverWayContentsIsSpelled(bool indirect)
    {
        using var input = new MemoryStream(TwoPartContentsPdf(indirect));
        using var output = new MemoryStream();

        PdfRedactionEngine.Redact(input, output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 0, Y = 0, Width = 200, Height = 200 },
        });

        output.Position = 0;
        string text = PdfOperations.ExtractText(output);

        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The floor: the word really is in the unredacted document, so "does not contain" is a claim
    /// about redaction rather than about a fixture that never had it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFixtureContainsTheWordBeforeRedaction(bool indirect)
    {
        using var ms = new MemoryStream(TwoPartContentsPdf(indirect));
        string text = PdfOperations.ExtractText(ms);

        Assert.Contains("SECRET", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A page whose content is split across two streams. Object 10 holds the array when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] TwoPartContentsPdf(bool indirect)
    {
        string contents = indirect ? "10 0 R" : "[4 0 R 9 0 R]";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents " + contents
                + " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", "BT /F1 12 Tf 20 150 Td (SECRET) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "null", "null", "null",
            Stream("", "BT /F1 12 Tf 20 100 Td (public) Tj ET\n"),
            "[4 0 R 9 0 R]");
    }

    private static string Stream(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "endstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
