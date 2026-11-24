// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfPageLabels.SetLabels"/>' checks and ordering, the reader's <c>/St</c>, and the
/// facade's formatting of a start the file chose.
/// </summary>
public class PageLabelWriteAndFormatTests
{
    // ------------------------------------------------------------------ writing

    /// <summary>
    /// §7.9.7: a number tree's keys ascend, whatever order the caller listed the ranges in. Read
    /// from the raw <c>/Nums</c> array, because the reader — and the facade, which sorts — would
    /// hide a writer that got the order wrong.
    /// </summary>
    [Fact]
    public void RangesAreWrittenInAscendingKeyOrder_WhateverOrderTheyArriveIn()
    {
        byte[] pdf = SetLabels(Pages(6),
            new PageLabelRange { StartPageIndex = 4, Style = "D" },
            new PageLabelRange { StartPageIndex = 0, Style = "r" },
            new PageLabelRange { StartPageIndex = 2, Style = "A" });

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var tree = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.PageLabels])!;
        var nums = (PdfArray)doc.ResolveObject(tree[PdfName.Nums])!;
        Assert.Equal(new long[] { 0, 2, 4 },
            Enumerable.Range(0, nums.Count / 2).Select(i => ((PdfNumber)nums[2 * i]).LongValue));

        Assert.Equal(new[] { "i", "ii", "A", "B", "1", "2" }, PdfOperations.GetPageLabels(new MemoryStream(pdf)));
    }

    /// <summary>
    /// Each range §12.4.2 does not allow is refused before a byte is written, for its own reason.
    /// </summary>
    [Theory]
    [InlineData("negative", "cannot be negative")]
    [InlineData("zero start", "at least 1")]
    [InlineData("unknown style", "allows D, R, r, A and a")]
    [InlineData("lower-case d", "allows D, R, r, A and a")]
    [InlineData("duplicate", "Two page-label ranges start on page 2")]
    [InlineData("no page 0", "No page-label range starts on page 0")]
    [InlineData("null", "is null")]
    public void ARangeTheSpecificationDoesNotAllowIsRefused_BeforeAnythingIsWritten(string kind, string reason)
    {
        var ranges = new List<PageLabelRange> { new PageLabelRange { StartPageIndex = 0, Style = "D" } };
        switch (kind)
        {
            case "negative":      ranges.Add(new PageLabelRange { StartPageIndex = -1 }); break;
            case "zero start":    ranges.Add(new PageLabelRange { StartPageIndex = 2, StartValue = 0 }); break;
            case "unknown style": ranges.Add(new PageLabelRange { StartPageIndex = 2, Style = "X" }); break;
            case "lower-case d":  ranges.Add(new PageLabelRange { StartPageIndex = 2, Style = "d" }); break;
            case "duplicate":
                ranges.Add(new PageLabelRange { StartPageIndex = 2, Style = "r" });
                ranges.Add(new PageLabelRange { StartPageIndex = 2, Style = "A" });
                break;
            case "no page 0":     ranges[0] = new PageLabelRange { StartPageIndex = 1, Style = "D" }; break;
            case "null":          ranges.Add(null!); break;
        }

        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(
            () => PdfPageLabels.SetLabels(new MemoryStream(Pages(4)), output, ranges));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The acceptance control for the refusals: every style, a prefix-only range with no style, and
    /// a start above 1 are all written.
    /// </summary>
    [Fact]
    public void EveryStyleAndAPrefixOnlyRangeAreAccepted()
    {
        byte[] pdf = SetLabels(Pages(6),
            new PageLabelRange { StartPageIndex = 0, Prefix = "Cover" },
            new PageLabelRange { StartPageIndex = 1, Style = "R" },
            new PageLabelRange { StartPageIndex = 2, Style = "r", StartValue = 4 },
            new PageLabelRange { StartPageIndex = 3, Style = "A" },
            new PageLabelRange { StartPageIndex = 4, Style = "a" },
            new PageLabelRange { StartPageIndex = 5, Style = "D", Prefix = "p-" });

        Assert.Equal(new[] { "Cover", "I", "iv", "A", "a", "p-1" }, PdfOperations.GetPageLabels(new MemoryStream(pdf)));
    }

    /// <summary>
    /// The facade writes through the same checks: it could read labels and not set them, so a
    /// facade caller had to leave it for one operation. A refusal and a write, both through it.
    /// </summary>
    [Fact]
    public void TheFacadeWritesLabels_ThroughTheSameChecks()
    {
        Assert.Throws<ArgumentException>(() => PdfOperations.SetPageLabels(new MemoryStream(Pages(2)), new MemoryStream(),
            new[] { new PageLabelRange { StartPageIndex = 1, Style = "D" } }));

        using var output = new MemoryStream();
        PdfOperations.SetPageLabels(new MemoryStream(Pages(2)), output,
            new[] { new PageLabelRange { StartPageIndex = 0, Style = "A", Prefix = "App-" } });

        Assert.Equal(new[] { "App-A", "App-B" }, PdfOperations.GetPageLabels(new MemoryStream(output.ToArray())));
    }

    /// <summary>
    /// An empty list removes the labels: a tree with no entry for page 0 is not one §12.4.2 allows,
    /// so "no ranges" is written as no tree. The input had labels, so the removal is observable.
    /// </summary>
    [Fact]
    public void AnEmptyListRemovesTheDocumentsPageLabels()
    {
        byte[] labelled = Pages(3, "/PageLabels << /Nums [0 << /S /r >>] >>");
        Assert.NotEmpty(PdfPageLabels.GetLabels(new MemoryStream(labelled)));

        byte[] pdf = SetLabels(labelled);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Null(doc.Catalog.Dictionary[PdfName.PageLabels]);
        Assert.Empty(PdfPageLabels.GetLabels(new MemoryStream(pdf)));
        Assert.Equal(3, doc.Pages.Count);
    }

    // ------------------------------------------------------------------ reading /St

    /// <summary>
    /// §12.4.2: <c>/St</c> is an integer of at least 1. Anything else reads as the default, 1 —
    /// not truncated, and not negative, which the facade's letter formatter could not take.
    /// </summary>
    [Theory]
    [InlineData("2.5", 1)]
    [InlineData("-100", 1)]
    [InlineData("0", 1)]
    [InlineData("99999999999", 1)]
    [InlineData("7", 7)]
    public void StartValueIsReadOnlyWhenItIsAnIntegerOfAtLeastOne(string st, int expected)
    {
        byte[] pdf = Pages(1, "/PageLabels << /Nums [0 << /S /D /St " + st + " >>] >>");

        Assert.Equal(expected, Assert.Single(PdfPageLabels.GetLabels(new MemoryStream(pdf))).StartValue);
    }

    /// <summary>
    /// The facade does not throw on a negative start, which would otherwise reach the letter
    /// formatter as a negative repeat count.
    /// </summary>
    [Fact]
    public void ANegativeStartLabelsFromTheDefault_RatherThanFailingTheWholeRead()
    {
        byte[] pdf = Pages(3, "/PageLabels << /Nums [0 << /S /a /St -100 >>] >>");

        Assert.Equal(new[] { "a", "b", "c" }, PdfOperations.GetPageLabels(new MemoryStream(pdf)));
    }

    // ------------------------------------------------------------------ formatting a start the file chose

    /// <summary>
    /// Table 159's letters repeat — 27 is <c>aa</c>, 28 is <c>bb</c> — which is what makes their
    /// length grow with the number and needs the cap below.
    /// </summary>
    [Fact]
    public void AlphabeticLabelsRepeatTheLetter()
    {
        byte[] pdf = Pages(3, "/PageLabels << /Nums [0 << /S /a /St 26 >>] >>");

        Assert.Equal(new[] { "z", "aa", "bb" }, PdfOperations.GetPageLabels(new MemoryStream(pdf)));
    }

    /// <summary>
    /// Both sides of the length cap for letters: 26 624 is 1 024 z's — the longest written as
    /// letters — and the page after it is decimal. <c>/St int.MaxValue</c> is 82 million letters
    /// per page without the cap, and its successor overflowed an <c>int</c> into a negative count.
    /// </summary>
    [Fact]
    public void AnAlphabeticLabelPastTheLengthCapIsWrittenAsDecimal()
    {
        var atTheCap = PdfOperations.GetPageLabels(new MemoryStream(
            Pages(2, "/PageLabels << /Nums [0 << /S /a /St 26624 >>] >>")));
        Assert.Equal(new string('z', 1024), atTheCap[0]);
        Assert.Equal("26625", atTheCap[1]);

        var huge = PdfOperations.GetPageLabels(new MemoryStream(
            Pages(2, "/PageLabels << /Nums [0 << /S /A /St 2147483647 >>] >>")));
        Assert.Equal(new[] { "2147483647", "2147483648" }, huge);
    }

    /// <summary>
    /// Both sides of the cap for roman numerals, which write an <c>m</c> per thousand: 1 024 999 is
    /// the last written as a numeral, 1 025 000 is decimal.
    /// </summary>
    [Fact]
    public void ARomanLabelPastTheLengthCapIsWrittenAsDecimal()
    {
        var labels = PdfOperations.GetPageLabels(new MemoryStream(
            Pages(2, "/PageLabels << /Nums [0 << /S /r /St 1024999 >>] >>")));

        Assert.Equal(new string('m', 1024) + "cmxcix", labels[0]);
        Assert.Equal("1025000", labels[1]);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] SetLabels(byte[] pdf, params PageLabelRange[] ranges)
    {
        using var output = new MemoryStream();
        PdfPageLabels.SetLabels(new MemoryStream(pdf), output, ranges);
        return output.ToArray();
    }

    /// <summary>A document of <paramref name="count"/> empty pages, with extra catalog entries.</summary>
    private static byte[] Pages(int count, string catalogExtra = "")
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(3, count).Select(n => n + " 0 R")) +
            "] /Count " + count + " >>",
        };
        for (int i = 0; i < count; i++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        return FormPdfFixtures.Build(bodies.ToArray());
    }
}
