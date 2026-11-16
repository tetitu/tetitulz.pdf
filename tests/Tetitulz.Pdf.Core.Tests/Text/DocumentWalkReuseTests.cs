// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// The whole-document walk shares one <see cref="TextExtractionCache"/> across its pages: fonts by
/// their dictionary, and the duplicate-overlap index's storage, emptied between pages
/// (<see cref="OverlappingTextIndex{TKey}.Reset"/>). On a thousand short pages, the index rebuilt
/// from empty and a font made per page were most of what plain extraction allocated beyond
/// tokenizing (GAPS.md §13b). What must not change: a page's text is its own — the same line at the
/// same place on two pages is two lines, not a copy.
/// </summary>
public class DocumentWalkReuseTests
{
    [Fact]
    public void AReset_EmptiesTheIndex_AndItStillFindsCopiesAfter()
    {
        var index = new OverlappingTextIndex<string>();
        Assert.False(index.IsCopyOfEarlier("fox", 100, 500, 30, 3));
        Assert.True(index.IsCopyOfEarlier("fox", 100, 500, 30, 3));

        Assert.True(index.Reset());
        Assert.False(index.IsCopyOfEarlier("fox", 100, 500, 30, 3));   // the last page's drawing is gone
        Assert.True(index.IsCopyOfEarlier("fox", 100, 500, 30, 3));    // and this page's is remembered
    }

    [Fact]
    public void AnIndexPastItsKeyCeiling_IsNotKept()
    {
        var index = new OverlappingTextIndex<string>();
        for (int k = 0; k <= OverlappingTextIndex<string>.MaxRetainedKeys; k++)
            index.IsCopyOfEarlier("k" + k, 0, 0, 10, 1);
        Assert.False(index.Reset());

        var small = new OverlappingTextIndex<string>();
        small.IsCopyOfEarlier("a", 0, 0, 10, 1);
        Assert.True(small.Reset());
    }

    [Fact]
    public void AnIndexPastItsPositionCeiling_IsNotKept()
    {
        var index = new OverlappingTextIndex<string>();
        for (int i = 0; i <= OverlappingTextIndex<string>.MaxRetainedPositions; i++)
            index.IsCopyOfEarlier("a", i * 10.0, 0, 10, 1);
        Assert.False(index.Reset());
    }

    /// <summary>
    /// The reuse, seen: two pages through two resource dictionaries naming one font make one font
    /// and one index for the whole walk — where each page made its own.
    /// </summary>
    [Fact]
    public void TheWalkMakesOneFontAndOneIndex_ForTwoPages()
    {
        using var doc = PdfDocument.Open(new MemoryStream(TwoPages(sameResources: false)));
        var cache = new TextExtractionCache();
        PdfTextExtractor.ExtractText(doc, null, cache);

        Assert.Equal(1, cache.FontObjectCount);
        Assert.Equal(1, cache.IndexesMade);
    }

    [Fact]
    public void TheCache_LendsBackTheIndexAPageReturned_AndKeepsOneFontPerDictionary()
    {
        var cache = new TextExtractionCache();
        var first = cache.RentIndex();
        first.IsCopyOfEarlier("a", 0, 0, 10, 1);
        cache.ReturnIndex(first);
        Assert.Same(first, cache.RentIndex());
        Assert.NotSame(first, cache.RentIndex());   // lent out, so a nested walk gets its own

        using var doc = PdfDocument.Open(new MemoryStream(TwoPages(sameResources: false)));
        PdfDictionary fontDict = (PdfDictionary)doc.ResolveObject(new PdfReference(5, 0));
        Assert.False(cache.TryGetFont(fontDict, out _));
        var font = Tetitulz.Pdf.Core.Fonts.PdfFont.Create(fontDict, doc);
        cache.AddFont(fontDict, font);
        Assert.True(cache.TryGetFont(fontDict, out var again));
        Assert.Same(font, again);
        Assert.Equal(1, cache.FontObjectCount);
    }

    /// <summary>
    /// Two pages drawing the same line at the same place, through two resource dictionaries that
    /// name one font: the document's text holds it twice — the index shared across the pages is
    /// emptied between them — and is the pages' own texts, joined.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoIdenticalPages_EachKeepTheirText(bool sameResources)
    {
        using var doc = PdfDocument.Open(new MemoryStream(TwoPages(sameResources)));
        string all = PdfTextExtractor.ExtractText(doc);

        Assert.Equal(2, Occurrences(all, "The same line"));
        Assert.Equal(string.Join(Environment.NewLine, doc.Pages.Select(PdfTextExtractor.ExtractText)), all);
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static byte[] TwoPages(bool sameResources)
    {
        const string content = "BT /F1 12 Tf 72 700 Td (The same line) Tj ET";
        string resources = sameResources ? "/Resources 8 0 R" : "/Resources << /Font << /F1 5 0 R >> >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R {resources} >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R {resources} >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Font << /F1 5 0 R >> >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}