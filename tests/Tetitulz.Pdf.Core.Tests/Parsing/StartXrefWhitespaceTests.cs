// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// <c>PdfParser.FindStartXref</c> accepts all six of §7.2.3 Table 1's whitespace bytes between the
/// <c>startxref</c> keyword and its operand.
/// </summary>
/// <remarks>
/// <para>It accepted three — CR, LF and SPACE — so a conforming file writing <c>startxref\t123</c>
/// found no digits, threw <c>PdfParseException</c>, and was sent down <c>RecoverXrefByScanning</c>
/// with <c>StartXrefOffset</c> left at <b>0</b>. The document still opened, because recovery works,
/// which is exactly why nothing noticed: <b>the damage is to the offset, not to the pages.</b> A
/// zero offset then propagates — it is what every incremental writer and both signing writers are
/// handed as the section to chain to.</para>
///
/// <para>The inline three-byte set was the odd one out in its own file: <c>PdfParser</c> already
/// used <c>PdfConstants.IsWhitespace</c> in three other places. A survey for the <i>named</i>
/// predicate found four copies that all agreed and would never have found this one.</para>
/// </remarks>
public sealed class StartXrefWhitespaceTests
{
    /// <summary>
    /// The three that were already accepted, as the control — without them a reader cannot tell
    /// "the fix works" from "the fixture never exercised the gap".
    /// </summary>
    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\f")]
    [InlineData("\0")]
    public void TheOffsetIsReadAfterAnySpecWhitespace(string gap)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Whitespace");
        long expected = LocateXref(pdf);

        byte[] rewritten = RewriteGapAfterStartXref(pdf, gap);

        using var ms = new MemoryStream(rewritten);
        using var doc = PdfDocument.Open(ms);

        // The offset is the point. A zero here means the parse threw and scan recovery ran —
        // which still yields a usable document, so asserting on the pages alone would pass with
        // the defect present.
        Assert.Equal(expected, doc.Parser.StartXrefOffset);
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// The premise: the offset this asserts really is the one the file names, so a passing case
    /// cannot be a coincidence between two wrong numbers.
    /// </summary>
    [Fact]
    public void TheFixtureLocatesTheSectionItClaimsTo()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Whitespace");
        long offset = LocateXref(pdf);

        Assert.True(offset > 0);
        Assert.Equal("xref", Encoding.ASCII.GetString(pdf, (int)offset, 4));
    }

    // ------------------------------------------------------------------

    private static long LocateXref(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int keyword = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(keyword >= 0, "fixture: the builder must emit a startxref");

        int i = keyword + "startxref".Length;
        while (i < pdf.Length && (pdf[i] == (byte)'\r' || pdf[i] == (byte)'\n' || pdf[i] == (byte)' ')) i++;

        int end = i;
        while (end < pdf.Length && pdf[end] >= (byte)'0' && pdf[end] <= (byte)'9') end++;
        Assert.True(end > i, "fixture: startxref must be followed by an offset");

        return long.Parse(Encoding.ASCII.GetString(pdf, i, end - i));
    }

    /// <summary>
    /// Replaces the whitespace run between <c>startxref</c> and its digits with
    /// <paramref name="gap"/>. Everything before the keyword is untouched, so every byte offset the
    /// file names — the xref entries included — stays valid whatever the gap's length.
    /// </summary>
    private static byte[] RewriteGapAfterStartXref(byte[] pdf, string gap)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int keyword = text.LastIndexOf("startxref", StringComparison.Ordinal);

        int i = keyword + "startxref".Length;
        while (i < pdf.Length && (pdf[i] == (byte)'\r' || pdf[i] == (byte)'\n' || pdf[i] == (byte)' ')) i++;

        var head = new byte[i - (keyword + "startxref".Length)];
        Assert.True(head.Length > 0, "fixture: there must be a whitespace run to replace");

        using var ms = new MemoryStream();
        ms.Write(pdf, 0, keyword + "startxref".Length);
        byte[] gapBytes = Encoding.ASCII.GetBytes(gap);
        ms.Write(gapBytes, 0, gapBytes.Length);
        ms.Write(pdf, i, pdf.Length - i);
        return ms.ToArray();
    }
}
