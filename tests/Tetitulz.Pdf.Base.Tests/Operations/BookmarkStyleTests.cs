// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A bookmark's position, open state and title style (ISO 32000-1 Tables 152–154): read from the
/// outline, and written back so a read-edit-write round trip keeps what the author set.
/// </summary>
public sealed class BookmarkStyleTests
{
    /// <summary>
    /// Pages 3 and 4; outline items: "Open" (open, red, bold and italic, /XYZ top 500) with child
    /// "Named" (the named destination "n": /FitH 300 on page 2); "Closed" (closed, child "Inner");
    /// "Plain" (no destination, no style).
    /// </summary>
    private static byte[] StyledOutline(string openColor = "[1 0 0]") => ActionTargetAgreementTests.Build(
        "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R /Names << /Dests << /Names [(n) [4 0 R /FitH 300]] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Outlines /First 6 0 R /Last 9 0 R /Count 4 >>",
        "<< /Title (Open) /Parent 5 0 R /Next 8 0 R /First 7 0 R /Last 7 0 R /Count 1 /C " + openColor
            + " /F 3 /Dest [3 0 R /XYZ 0 500 null] >>",
        "<< /Title (Named) /Parent 6 0 R /Dest (n) >>",
        "<< /Title (Closed) /Parent 5 0 R /Prev 6 0 R /Next 9 0 R /First 10 0 R /Last 10 0 R /Count -1 /Dest [4 0 R /Fit] >>",
        "<< /Title (Plain) /Parent 5 0 R /Prev 8 0 R >>",
        "<< /Title (Inner) /Parent 8 0 R /Dest [4 0 R /XYZ null 120 null] >>");

    [Fact]
    public void ReadingTheOutline_GivesEachItemsPositionOpenStateAndStyle()
    {
        IReadOnlyList<PdfBookmark> marks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(StyledOutline()));

        PdfBookmark open = marks[0];
        Assert.Equal(("Open", 0, 500f), (open.Title, open.PageNumber!.Value, open.Top!.Value));
        Assert.True(open.IsOpen);
        Assert.Equal(new[] { 1f, 0f, 0f }, open.Color);
        Assert.True(open.IsBold);
        Assert.True(open.IsItalic);

        PdfBookmark named = open.Children[0];
        Assert.Equal((1, 300f), (named.PageNumber!.Value, named.Top!.Value));     // through the named destination

        PdfBookmark closed = marks[1];
        Assert.False(closed.IsOpen);
        Assert.Null(closed.Top);                                                  // /Fit: the page's top
        Assert.Equal(120f, closed.Children[0].Top);

        PdfBookmark plain = marks[2];
        Assert.Equal((false, false, false), (plain.IsOpen, plain.IsBold, plain.IsItalic));
        Assert.Null(plain.Color);
        Assert.Null(plain.PageNumber);
        Assert.Null(plain.Top);
    }

    [Theory]
    [InlineData("[2 -1 0.5]", new[] { 1f, 0f, 0.5f })]   // clamped
    // A real of 10^40: finite as a double, infinite as a float (PDF has no exponent notation, and an
    // integer that long saturates to a long, which a float holds)
    [InlineData("[10000000000000000000000000000000000000000.0 0 -10000000000000000000000000000000000000000.0]", new[] { 1f, 0f, 0f })]
    [InlineData("[1 0]", null)]                            // not three numbers
    [InlineData("[1 0 /Red]", null)]
    [InlineData("(red)", null)]
    public void AFilesColour_IsClampedOrIgnored_NeverRefused(string color, float[]? expected)
    {
        IReadOnlyList<PdfBookmark> marks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(StyledOutline(color)));
        Assert.Equal(expected, marks[0].Color);
        Assert.Equal("Open", marks[0].Title);                                     // the item is still read
    }

    [Fact]
    public void ACallersBookmark_IsCheckedAndItsColourClamped()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(top: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(top: float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => Make(color: new[] { 1f, 0f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(color: new[] { 1f, float.NaN, 0f }));

        PdfBookmark ok = Make(top: 10, color: new[] { 1.5f, -2f, 0.25f });      // the control
        Assert.Equal(10f, ok.Top);
        Assert.Equal(new[] { 1f, 0f, 0.25f }, ok.Color);

        static PdfBookmark Make(float? top = null, float[]? color = null)
            => new PdfBookmark("t", 0, 0, null, top, isOpen: false, color, isBold: false, isItalic: false);
    }

    /// <summary>
    /// Written back, every item keeps what it was read with — and the counts are the visible
    /// descendants, positive when open (Tables 152, 153), not every item collapsed.
    /// </summary>
    [Fact]
    public void WritingTheOutline_KeepsEveryItemsStyle_AndCountsWhatIsVisible()
    {
        // A open [ B open [ C ], D ], E closed [ F ]
        var c = Mark("C", 1, 2);
        var b = Mark("B", 0, 1, open: true, children: c);
        var d = Mark("D", 1, 1);
        var a = new PdfBookmark("A", 0, 0, new[] { b, d }, top: 640, isOpen: true,
            color: new[] { 0f, 0.5f, 1f }, isBold: true, isItalic: false);
        var f = Mark("F", 1, 1);
        var e = new PdfBookmark("E", 1, 0, new[] { f }, top: null, isOpen: false, color: null,
            isBold: false, isItalic: true);

        byte[] written = Write(new[] { a, e });

        // The raw counts: B shows C (1); A shows B, C, D (3); E closed over F (-1); the root shows
        // A, B, C, D and E (5).
        using (var doc = PdfDocument.Open(new MemoryStream(written)))
        {
            var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]);
            var itemA = (PdfDictionary)doc.ResolveObject(root[PdfName.First]);
            var itemB = (PdfDictionary)doc.ResolveObject(itemA[PdfName.First]);
            var itemE = (PdfDictionary)doc.ResolveObject(itemA[PdfName.Next]);
            Assert.Equal(5, Count(doc, root));
            Assert.Equal(3, Count(doc, itemA));
            Assert.Equal(1, Count(doc, itemB));
            Assert.Equal(-1, Count(doc, itemE));
        }

        IReadOnlyList<PdfBookmark> back = PdfBookmarksManager.ReadBookmarks(new MemoryStream(written));
        PdfBookmark a2 = back[0], e2 = back[1];
        Assert.Equal(("A", 0, 640f, true, true, false), (a2.Title, a2.PageNumber!.Value, a2.Top!.Value, a2.IsOpen, a2.IsBold, a2.IsItalic));
        Assert.Equal(new[] { 0f, 0.5f, 1f }, a2.Color);
        Assert.True(a2.Children[0].IsOpen);
        Assert.Equal(("E", false, false, true), (e2.Title, e2.IsOpen, e2.IsBold, e2.IsItalic));
        Assert.Null(e2.Top);
        Assert.Null(e2.Color);

        static PdfBookmark Mark(string title, int page, int depth, bool open = false, params PdfBookmark[] children)
            => new PdfBookmark(title, page, depth, children, top: null, isOpen: open, color: null, isBold: false, isItalic: false);
        static long Count(PdfDocument doc, PdfDictionary dict) => ((PdfNumber)doc.ResolveObject(dict[PdfName.Count])).LongValue;
    }

    private static byte[] Write(IReadOnlyList<PdfBookmark> marks)
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        using var output = new MemoryStream();
        PdfBookmarksManager.WriteBookmarks(new MemoryStream(pdf), output, marks);
        return output.ToArray();
    }
}
