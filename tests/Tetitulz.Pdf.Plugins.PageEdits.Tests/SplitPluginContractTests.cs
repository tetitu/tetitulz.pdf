// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// <see cref="SplitPlugin"/> — the <see cref="ISplitPlugin"/> contract — must honour the
/// <b>zero-based</b> page indices that interface documents.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PdfSplitter.SplitRange(Stream, Stream, int, int)"/>'s contract is <b>one-based</b>
/// (<c>Split</c> goes through <c>PdfSplitter.EachPage</c>, which numbers the pages itself).
/// Forwarding the caller's index unchanged would make <c>Split</c> throw
/// <see cref="ArgumentOutOfRangeException"/> on its very first iteration (<c>i = 0</c>) and
/// <c>SplitRange(stream, 1, 1)</c> silently return the document's first page where the contract
/// promises the second.
/// </para>
/// <para>
/// These assert <i>which page</i> came back, by its extracted text — not that a non-empty byte array
/// was produced. Every single-page extract of a multi-page document is a valid PDF, so an
/// off-by-one is invisible to any "did it return something" check.
/// </para>
/// </remarks>
public class SplitPluginContractTests
{
    private static string PageTextOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfOperations.ExtractText(ms).Trim();
    }

    /// <summary>
    /// The loop starts at index 0, which the one-based splitter would reject outright.
    /// </summary>
    [Fact]
    public void Split_ReturnsOnePdfPerPage_InDocumentOrderStartingAtTheFirstPage()
    {
        ISplitPlugin plugin = new SplitPlugin();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));

        var pages = plugin.Split(input);

        Assert.Equal(3, pages.Count);
        Assert.Equal("Page 1", PageTextOf(pages[0]));
        Assert.Equal("Page 2", PageTextOf(pages[1]));
        Assert.Equal("Page 3", PageTextOf(pages[2]));
    }

    /// <summary>
    /// Index 0 is the first page and index 1 the second, per <see cref="ISplitPlugin"/>. The old
    /// forwarding made 0 throw and 1 return page one — the wrong page, with nothing to catch.
    /// </summary>
    [Theory]
    [InlineData(0, 0, "Page 1")]
    [InlineData(1, 1, "Page 2")]
    [InlineData(3, 3, "Page 4")]
    public void SplitRange_ZeroBasedIndexSelectsTheDocumentedPage(int start, int end, string expected)
    {
        ISplitPlugin plugin = new SplitPlugin();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(4));

        byte[] extracted = plugin.SplitRange(input, start, end);

        Assert.Equal(expected, PageTextOf(extracted));
    }

    [Fact]
    public void SplitRange_ZeroBasedRangeIsInclusiveAtBothEnds()
    {
        ISplitPlugin plugin = new SplitPlugin();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(5));

        byte[] extracted = plugin.SplitRange(input, 1, 3);

        string text = PageTextOf(extracted);
        Assert.Contains("Page 2", text, StringComparison.Ordinal);
        Assert.Contains("Page 4", text, StringComparison.Ordinal);
        // The pages either side of the range are the reason this is an identity check and not a
        // count: dropping one and keeping a neighbour also yields three pages.
        Assert.DoesNotContain("Page 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Page 5", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plugin's own zero-based argument validation stays as it was: −1 is out of range here,
    /// where 0 is the first page.
    /// </summary>
    [Fact]
    public void SplitRange_NegativeStartOrInvertedRange_ThrowsArgumentOutOfRange()
    {
        ISplitPlugin plugin = new SplitPlugin();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));

        Assert.Throws<ArgumentOutOfRangeException>(() => plugin.SplitRange(input, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => plugin.SplitRange(input, 2, 1));
    }

    /// <summary>
    /// A zero-based index one past the last page must still be refused by the underlying splitter's
    /// range check rather than translated into a silently valid one-based page.
    /// </summary>
    [Fact]
    public void SplitRange_IndexPastTheLastPage_ThrowsArgumentOutOfRange()
    {
        ISplitPlugin plugin = new SplitPlugin();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));

        Assert.Throws<ArgumentOutOfRangeException>(() => plugin.SplitRange(input, 3, 3));
    }

    /// <summary>
    /// Every page is extracted from the one open document, so the input is read a few times over
    /// in all (measured 3.8×: the open, then each page's own objects as it is extracted). It was
    /// opened again for each page — the whole file parsed once a page, forty times here, pages².
    /// </summary>
    [Fact]
    public void AdversarialManyPages_SplitOpensTheInputOnce()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(40);
        using var input = new CountingStream(pdf);

        var pages = new SplitPlugin().Split(input);

        Assert.Equal(40, pages.Count);
        Assert.Equal("Page 40", PageTextOf(pages[39]));
        Assert.InRange(input.BytesRead, pdf.Length, 8L * pdf.Length);
    }

    /// <summary>
    /// An encrypted input has no readable page tree, so it reads as no pages; splitting it is
    /// refused rather than answered with an empty list. The control is a plain document's pages.
    /// </summary>
    [Fact]
    public void Split_AnEncryptedInput_IsRefused()
    {
        Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(
            () => new SplitPlugin().Split(new MemoryStream(EncryptedOnePagePdf())));

        Assert.Single(new SplitPlugin().Split(new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(1))));
    }

    private sealed class CountingStream : MemoryStream
    {
        public CountingStream(byte[] bytes) : base(bytes) { }

        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = base.Read(buffer);
            BytesRead += n;
            return n;
        }

        public override int ReadByte()
        {
            int b = base.ReadByte();
            if (b >= 0) BytesRead++;
            return b;
        }
    }

    /// <summary>One page under a <c>/Standard</c> handler — <c>MergeEncryptedInputTests</c>' fixture.</summary>
    private static byte[] EncryptedOnePagePdf()
    {
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 "
            + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
            + "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R /Encrypt 5 0 R /ID [<0102> <0102>] >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
