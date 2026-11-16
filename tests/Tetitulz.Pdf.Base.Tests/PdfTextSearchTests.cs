// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

public sealed class PdfTextSearchTests
{
    // ----------------------------------------------------------------
    //  Search — basic hit detection
    // ----------------------------------------------------------------

    [Fact]
    /// <summary>
    /// The name promises a <b>single</b> hit, so assert exactly that — plus what the hit says about
    /// itself. <c>NotEmpty</c> would pass on a search that returned one match per glyph, or one per
    /// page of a document that has only one page anyway.
    /// </summary>
    public void Search_ExactMatch_ReturnsSingleHit()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);

        var hit = Assert.Single(PdfOperations.SearchText(ms, "Hello World"));

        Assert.Equal("Hello World", hit.MatchText);
        Assert.Equal(0, hit.PageNumber);
        Assert.Equal(0, hit.CharOffset);
    }

    [Fact]
    public void Search_TextAbsent_ReturnsEmpty()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "NotPresent");
        Assert.Empty(hits);
    }

    [Fact]
    /// <summary>
    /// A case-insensitive search must find <b>the same occurrence</b> a correctly-cased search
    /// finds — same page, same offset — and report the text as it appears in the document rather
    /// than as it was typed in the query. Its `ignoreCase: false` sibling below asserts the
    /// opposite direction, so the pair pins the flag rather than the search.
    /// </summary>
    public void Search_CaseInsensitive_FindsMatch()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");

        using var lower = new MemoryStream(pdf);
        var insensitive = Assert.Single(PdfOperations.SearchText(lower, "hello world", ignoreCase: true));

        using var exact = new MemoryStream(pdf);
        var sensitive = Assert.Single(PdfOperations.SearchText(exact, "Hello World", ignoreCase: false));

        Assert.Equal(sensitive.PageNumber, insensitive.PageNumber);
        Assert.Equal(sensitive.CharOffset, insensitive.CharOffset);

        // The document's casing, not the query's.
        Assert.Equal("Hello World", insensitive.MatchText);
    }

    [Fact]
    public void Search_CaseSensitive_NoMatch()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "hello world", ignoreCase: false);
        Assert.Empty(hits);
    }

    [Fact]
    /// <summary>
    /// The positive half of the case-sensitivity pair: the exactly-cased query hits once, at the
    /// start of the text. (<c>Search_CaseSensitive_NoMatch</c> above is the negative half.)
    /// </summary>
    public void Search_CaseSensitive_ExactCaseHits()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);

        var hit = Assert.Single(PdfOperations.SearchText(ms, "Hello World", ignoreCase: false));

        Assert.Equal("Hello World", hit.MatchText);
        Assert.Equal(0, hit.CharOffset);
    }

    // ----------------------------------------------------------------
    //  Search — hit properties
    // ----------------------------------------------------------------

    [Fact]
    public void Search_Hit_HasCorrectPageNumber()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "Hello World");
        Assert.All(hits, h => Assert.Equal(0, h.PageNumber));
    }

    [Fact]
    public void Search_Hit_MatchTextEqualsQuery()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "Hello");
        Assert.NotEmpty(hits);
        Assert.Equal("Hello", hits[0].MatchText);
    }

    [Fact]
    public void Search_Hit_ContextSnippetContainsMatch()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "Hello");
        Assert.NotEmpty(hits);
        Assert.Contains("Hello", hits[0].ContextSnippet);
    }

    [Fact]
    public void Search_Hit_CharOffsetIsNonNegative()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "Hello");
        Assert.NotEmpty(hits);
        Assert.True(hits[0].CharOffset >= 0);
    }

    // ----------------------------------------------------------------
    //  Search — multi-page
    // ----------------------------------------------------------------

    [Fact]
    public void Search_MultiPage_FindsHitOnEachPage()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        // Each page has content "Page N". Search for "Page" should hit all 3.
        var hits = PdfOperations.SearchText(ms, "Page");
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Search_MultiPage_PageNumbersDistinct()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "Page");
        Assert.Contains(hits, h => h.PageNumber == 0);
        Assert.Contains(hits, h => h.PageNumber == 1);
        Assert.Contains(hits, h => h.PageNumber == 2);
    }

    [Fact]
    public void Search_OnlyFirstPage_HitOnPageZero()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        // "1" appears in "Page 1" on page 0.
        var hits = PdfOperations.SearchText(ms, "Page 1", ignoreCase: false);
        Assert.Single(hits);
        Assert.Equal(0, hits[0].PageNumber);
    }

    // ----------------------------------------------------------------
    //  Search — file-path overload
    // ----------------------------------------------------------------

    [Fact]
    public void Search_FilePath_ReturnsHits()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        string tmp = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(tmp, pdf);

            var fromPath = PdfOperations.SearchText(tmp, "Hello World");

            // The file-path overload is a convenience wrapper, so it must agree with the stream
            // overload hit for hit — "it returned something" would not notice it searching a
            // different document, or dropping every hit but the first.
            using var ms = new MemoryStream(pdf);
            var fromStream = PdfOperations.SearchText(ms, "Hello World");

            Assert.Equal(fromStream.Count, fromPath.Count);
            Assert.Single(fromPath);
            Assert.Equal(fromStream[0].MatchText,  fromPath[0].MatchText);
            Assert.Equal(fromStream[0].PageNumber, fromPath[0].PageNumber);
            Assert.Equal(fromStream[0].CharOffset, fromPath[0].CharOffset);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ----------------------------------------------------------------
    //  Search — empty query
    // ----------------------------------------------------------------

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        var hits = PdfOperations.SearchText(ms, "");
        Assert.Empty(hits);
    }

    // ----------------------------------------------------------------
    //  Contains
    // ----------------------------------------------------------------

    [Fact]
    public void Contains_PresentText_ReturnsTrue()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        Assert.True(PdfOperations.ContainsText(ms, "Hello"));
    }

    [Fact]
    public void Contains_AbsentText_ReturnsFalse()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        Assert.False(PdfOperations.ContainsText(ms, "NotHere"));
    }

    [Fact]
    public void Contains_CaseInsensitive_ReturnsTrue()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        Assert.True(PdfOperations.ContainsText(ms, "hello", ignoreCase: true));
    }

    [Fact]
    public void Contains_CaseSensitiveMismatch_ReturnsFalse()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        Assert.False(PdfOperations.ContainsText(ms, "hello", ignoreCase: false));
    }

    [Fact]
    public void Contains_EmptyQuery_ReturnsFalse()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms = new MemoryStream(pdf);
        Assert.False(PdfOperations.ContainsText(ms, ""));
    }

    // ----------------------------------------------------------------
    //  Error / null input
    // ----------------------------------------------------------------

    [Fact]
    public void Search_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => PdfOperations.SearchText((Stream)null!, "q"));

    [Fact]
    public void Search_NullQuery_Throws()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentNullException>(
            () => PdfOperations.SearchText(ms, (string)null!));
    }

    [Fact]
    public void Contains_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => PdfOperations.ContainsText((Stream)null!, "q"));
}
