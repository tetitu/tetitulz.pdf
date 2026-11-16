// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page whose <c>/Contents</c> is an <b>indirect reference to an array</b> must keep every
/// original stream when something is appended to it.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> Both writers that extend a page's content — <c>PdfPageContentAppender</c>
/// (watermarks, stamps, headers) and <c>PdfFormFlattener</c> — branched on the <i>unresolved</i>
/// value: <c>raw is PdfReference</c> matched first, so <c>/Contents 5 0 R</c> where object 5 is an
/// array went into the new array whole, giving <c>/Contents [5 0 R newRef]</c>. §7.7.3.3 permits a
/// stream or an array of streams, not an array of arrays, and <c>PdfPage.GetContentStreamData</c>
/// skips any element that does not resolve to a stream — so **the page's entire original content
/// disappeared from our own reader**, leaving only the thing just appended. Two copies, written out
/// separately, wrong the same way.</para>
///
/// <para><b>Why nothing caught it.</b> The overwhelmingly common spelling is a direct
/// <c>/Contents [3 0 R 4 0 R]</c> array, which took the second branch and worked; and every
/// existing fixture writes it that way. The indirect spelling is legal, is what several real
/// producers emit, and is a single reference away from the tested one.</para>
///
/// <para><b>The assertion is the recovered text, not the byte count.</b> A file that lost its
/// original content is still a perfectly valid PDF of the right size with a real content stream in
/// it — asserting "output is non-empty", or even "output is larger than input", passes with the
/// defect present. These read the page back and require the original operators to still be
/// there.</para>
/// </remarks>
public class IndirectContentsArrayTests
{
    /// <summary>
    /// Appending to a page whose <c>/Contents</c> is <c>5 0 R</c> → <c>[3 0 R 4 0 R]</c> keeps both
    /// original streams and adds the new one.
    /// </summary>
    [Fact]
    public void AppendContent_IndirectContentsArray_KeepsTheOriginalStreams()
    {
        using var input = new MemoryStream(IndirectContentsArrayPdf());
        using var output = new MemoryStream();

        PdfPageContentAppender.AppendContent(input, output, 0, b => b.BeginText());

        string text = PageContent(output.ToArray());

        // Both originals survive...
        Assert.Contains("ORIGINAL_ONE", text);
        Assert.Contains("ORIGINAL_TWO", text);
    }

    /// <summary>
    /// The control that proves the fixture reaches the defect: the same page written with a
    /// <b>direct</b> <c>/Contents</c> array — the spelling every other fixture in the suite uses —
    /// already worked, so a test built on it could never have gone red.
    /// </summary>
    [Fact]
    public void AppendContent_DirectContentsArray_KeepsTheOriginalStreams()
    {
        using var input = new MemoryStream(DirectContentsArrayPdf());
        using var output = new MemoryStream();

        PdfPageContentAppender.AppendContent(input, output, 0, b => b.BeginText());

        string text = PageContent(output.ToArray());

        Assert.Contains("ORIGINAL_ONE", text);
        Assert.Contains("ORIGINAL_TWO", text);
    }

    // ------------------------------------------------------------------

    /// <summary>The page's decoded content as read back through Core, which is the reader that was losing it.</summary>
    private static string PageContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        byte[] data = doc.Pages[0].GetContentStreamData();
        Assert.NotNull(data);
        return Encoding.Latin1.GetString(data);
    }

    /// <summary>Object 5 is the array; the page points at it indirectly.</summary>
    private static byte[] IndirectContentsArrayPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [6 0 R] /Count 1 >>",
        StreamObj("% ORIGINAL_ONE\n"),
        StreamObj("% ORIGINAL_TWO\n"),
        "[3 0 R 4 0 R]",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << >> >>");

    /// <summary>The same page with the array written directly — the shape the suite already covered.</summary>
    private static byte[] DirectContentsArrayPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [6 0 R] /Count 1 >>",
        StreamObj("% ORIGINAL_ONE\n"),
        StreamObj("% ORIGINAL_TWO\n"),
        "null",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents [3 0 R 4 0 R] /Resources << >> >>");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
