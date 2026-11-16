// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// What a split or merge does to the objects it did not make, the pages it could not number, and
/// the warnings of a split into a document a page.
/// </summary>
public class SplitObjectIdentityTests
{
    // ------------------------------------------------------------------
    //  A value DeepClone hands back as it was is not renumbered
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_TopLevelTrueAndNullObjects_LeaveTheSharedInstancesUnnumbered()
    {
        // `7 0 obj true endobj` copies as the process-wide true and `8 0 obj 99 0 R endobj` as the
        // process-wide null; stamping the output number on them renumbered both for every reader.
        // Nothing may number them (PdfParser.StampIdentity refuses to), so they stay at zero.
        using var input = new MemoryStream(ScalarObjects());
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);

        Assert.Equal(0, PdfBoolean.True.ObjectNumber);
        Assert.Equal(0, PdfNull.Instance.ObjectNumber);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var page = doc.Pages[0].Dictionary;
        Assert.True((doc.ResolveObject(page[new PdfName("Flag")]) as PdfBoolean)?.Value);
        Assert.Equal("hello", Encoding.ASCII.GetString((doc.ResolveObject(page[new PdfName("Label")]) as PdfString).RawBytes));
    }

    [Fact]
    public void EachPage_LeavesTheSourcesCachedObjectsTheirOwnNumbers()
    {
        // The source document lives on across the pages of a split: a string it caches as object 9
        // was renumbered by the first page's copy.
        using var doc = PdfDocument.Open(new MemoryStream(ScalarObjects()));
        Assert.Equal(9, doc.GetObject(9).ObjectNumber);

        PdfSplitter.EachPage(doc);

        Assert.Equal(9, doc.GetObject(9).ObjectNumber);
    }

    [Fact]
    public void Merge_TopLevelTrueAndNullObjects_LeaveTheSharedInstancesUnnumbered()
    {
        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(ScalarObjects()), new MemoryStream(ScalarObjects()));

        Assert.Equal(0, PdfBoolean.True.ObjectNumber);
        Assert.Equal(0, PdfNull.Instance.ObjectNumber);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(2, doc.Pages.Count);
    }

    // ------------------------------------------------------------------
    //  A page held directly in /Kids
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_APageHeldDirectlyInKids_IsExtracted()
    {
        byte[] extracted = Split(DirectFirstPage(), 1, 1);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Equal(1, doc.Pages.Count);
        Assert.Contains("DIRECT", Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
    }

    [Fact]
    public void SplitRange_ADirectPageAndAnIndirectOne_KeepTheirOrder()
    {
        byte[] extracted = Split(DirectFirstPage(), 1, 2);

        using var doc = PdfDocument.Open(new MemoryStream(extracted));
        Assert.Equal(new[] { "DIRECT", "INDIRECT" },
            doc.Pages.Select(p => Encoding.ASCII.GetString(p.GetContentStreamData()).Contains("INDIRECT") ? "INDIRECT" : "DIRECT").ToArray());
        var pages = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Pages]) as PdfDictionary;
        foreach (var page in doc.Pages)
            Assert.Same(pages, doc.ResolveObject(page.Dictionary[PdfName.Parent]));
    }

    // ------------------------------------------------------------------
    //  The warnings of a split into a document a page
    // ------------------------------------------------------------------

    [Fact]
    public void EachPage_AnUnreadableObjectOnTheFirstPage_IsStillReportedAfterTheLast()
    {
        // Each page's extraction reset every slot, so page 2's clean extract erased page 1's report.
        using var doc = PdfDocument.Open(new MemoryStream(UnreadableObjectOnPageOne()));
        PdfSplitter.EachPage(doc);

        Assert.NotNull(PdfSplitter.LastObjectWarning);
        Assert.StartsWith("Page 1: ", PdfSplitter.LastObjectWarning, StringComparison.Ordinal);
        Assert.Contains("object 5", PdfSplitter.LastObjectWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_ToADirectory_AnUnreadableObjectOnTheFirstPage_IsStillReportedAfterTheLast()
    {
        string dir = Path.Combine(Path.GetTempPath(), "w22-split-warn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(input, UnreadableObjectOnPageOne());
            PdfSplitter.Split(input, Path.Combine(dir, "out"));

            Assert.StartsWith("Page 1: ", PdfSplitter.LastObjectWarning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EachPage_ACleanDocument_ReportsNothing()
    {
        // The control for the two above.
        using var doc = PdfDocument.Open(new MemoryStream(ScalarObjects()));
        PdfSplitter.EachPage(doc);
        Assert.Null(PdfSplitter.LastObjectWarning);
    }

    [Fact]
    public void SplitRange_AnInputThatWillNotOpen_LeavesNoWarningOfTheCallBefore()
    {
        using (var doc = PdfDocument.Open(new MemoryStream(UnreadableObjectOnPageOne())))
            PdfSplitter.EachPage(doc);
        Assert.NotNull(PdfSplitter.LastObjectWarning);

        Assert.ThrowsAny<Exception>(() =>
            PdfSplitter.SplitRange(new MemoryStream(Encoding.ASCII.GetBytes("not a pdf")), new MemoryStream(), 1, 1));
        Assert.Null(PdfSplitter.LastObjectWarning);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] Split(byte[] pdf, int first, int last)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, first, last);
        return output.ToArray();
    }

    // A page naming three top-level scalar objects: a boolean, a reference to nothing, a string.
    private static byte[] ScalarObjects() => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Flag 7 0 R /Gone 8 0 R /Label 9 0 R >>"),
        (7, "true"),
        (8, "99 0 R"),
        (9, "(hello)"));

    // Page 1 held directly in /Kids, with no /Type, page 2 as an object of its own.
    private static byte[] DirectFirstPage() => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [<< /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >> 5 0 R] /Count 2 >>"),
        (4, "<< /Length 17 >>\nstream\nBT (DIRECT) Tj ET\nendstream"),
        (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 6 0 R >>"),
        (6, "<< /Length 19 >>\nstream\nBT (INDIRECT) Tj ET\nendstream"));

    // Two pages; page 1's /Resources is object 5, which does not parse (a stray `]`).
    private static byte[] UnreadableObjectOnPageOne() => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
        (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 5 0 R >>"),
        (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"),
        (5, "]"));

    private static byte[] Pdf(params (int Number, string Body)[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in objects.OrderBy(o => o.Number))
        {
            offsets[number] = sb.Length;
            sb.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int size = objects.Max(o => o.Number) + 1;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(size).Append('\n');
        for (int n = 0; n < size; n++)
            sb.Append(offsets.TryGetValue(n, out int offset)
                ? offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"
                : "0000000000 65535 f \n");
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
