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
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A split into single pages reads the catalog's <c>/Names</c>, the outline and the field tree once
/// (<see cref="PdfSplitter.SplitWalks"/>), not once a page: 800 pages with a named destination, an
/// outline item and a field each took 1.0 s, of which 45 ms was the pages themselves.
/// </summary>
/// <remarks>
/// The answer cannot show which way the walks ran, so the evidence is twofold: every page of a split
/// is byte for byte the page extracted alone (identifier aside), and the split's own counters say how
/// often it walked the document and how much of the field tree each page's filter examined.
/// </remarks>
public class SplitSharedWalksTests
{
    // ------------------------------------------------------------------
    //  The split and the page alone agree
    // ------------------------------------------------------------------

    [Fact]
    public void EachPage_IsByteForByteThePageExtractedAlone()
    {
        byte[] pdf = RichDocument();
        List<byte[]> split;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            split = PdfSplitter.EachPage(doc);

        Assert.Equal(4, split.Count);
        for (int page = 1; page <= split.Count; page++)
        {
            using var input = new MemoryStream(pdf);
            using var alone = new MemoryStream();
            PdfSplitter.ExtractPages(input, alone, new[] { page });

            Assert.Equal(WithoutIdentifier(alone.ToArray()), WithoutIdentifier(split[page - 1]));
        }
    }

    [Fact]
    public void EachPage_OfTheRichDocument_KeepsWhatEachPageCanStillShow()
    {
        // The potency control for the comparison above: two empty outputs also agree.
        byte[] pdf = RichDocument();
        List<byte[]> split;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            split = PdfSplitter.EachPage(doc);

        using var first = PdfDocument.Open(new MemoryStream(split[0]));
        Assert.Equal(new[] { "a", "c" }, DestinationNames(first));
        // One's own destination is page 1, and Web's /URI names no page here, so both stay.
        Assert.Equal(new[] { "One", "Web" }, OutlineTitles(first));
        Assert.Contains("Street", FieldTitles(first));
        Assert.DoesNotContain("City", FieldTitles(first));

        using var second = PdfDocument.Open(new MemoryStream(split[1]));
        Assert.Equal(new[] { "b" }, DestinationNames(second));
        Assert.Contains("City", FieldTitles(second));
        Assert.DoesNotContain("Street", FieldTitles(second));

        using var third = PdfDocument.Open(new MemoryStream(split[2]));
        Assert.Equal(new[] { "d" }, DestinationNames(third));
        Assert.Contains("W62", FieldTitles(third));
    }

    // ------------------------------------------------------------------
    //  Read once — the counters are the evidence
    // ------------------------------------------------------------------

    [Fact]
    public void EachPage_WalksTheDocumentOnce_AndEachPageExaminesOnlyItsOwnField()
    {
        const int pages = 120;
        using var doc = PdfDocument.Open(new MemoryStream(OneOfEachPerPage(pages)));
        var walks = new PdfSplitter.SplitWalks(doc);

        var split = PdfSplitter.EachPage(doc, walks);

        Assert.Equal(pages, split.Count);
        // The names, the outline and the field tree: once each, however many pages.
        Assert.Equal(3, walks.Walks);
        // Each page's filter reads its one annotation and its one field: about two units a page. The
        // whole walk per page read every field, pages² in all — 14 520 units here.
        Assert.InRange(walks.FieldEdgesExamined, pages, 4L * pages);
    }

    [Fact]
    public void EachPage_StillKeepsEachPagesOwnDestinationOutlineItemAndField()
    {
        // The control for the counters: a split that examined nothing would also examine little.
        const int pages = 30;
        using var doc = PdfDocument.Open(new MemoryStream(OneOfEachPerPage(pages)));
        var split = PdfSplitter.EachPage(doc);

        for (int p = 0; p < pages; p += 7)
        {
            using var page = PdfDocument.Open(new MemoryStream(split[p]));
            Assert.Equal(new[] { "d" + p.ToString("D6", CultureInfo.InvariantCulture) }, DestinationNames(page));
            Assert.Equal(new[] { "item " + p.ToString(CultureInfo.InvariantCulture) }, OutlineTitles(page));
            Assert.Equal(new[] { "f" + p.ToString(CultureInfo.InvariantCulture) }, FieldTitles(page));
        }
    }

    [Fact]
    public void Split_ToADirectory_WritesEveryPageFromOneWalk_AsEachPageDoes()
    {
        // The public file door loops over the pages too, and shares the same walks.
        string dir = Path.Combine(Path.GetTempPath(), "w22-split-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(dir, "in.pdf");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] pdf = RichDocument();
            File.WriteAllBytes(input, pdf);
            PdfSplitter.Split(input, Path.Combine(dir, "out"));

            List<byte[]> split;
            using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
                split = PdfSplitter.EachPage(doc);
            for (int page = 1; page <= split.Count; page++)
            {
                byte[] written = File.ReadAllBytes(Path.Combine(dir, "out", $"page_{page:D3}.pdf"));
                Assert.Equal(WithoutIdentifier(split[page - 1]), WithoutIdentifier(written));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NameWalk_HandsEachExtractionItsOwnLists()
    {
        // An extraction sorts its lists in place; one handed the walk's own list would hand the next
        // extraction a list already sorted, and an unstable sort need not order equal keys alike.
        using var doc = PdfDocument.Open(new MemoryStream(RichDocument()));
        var walks = new PdfSplitter.SplitWalks(doc);
        var keptPages = new HashSet<int>(doc.Pages.Select(p => p.Dictionary.ObjectNumber));

        var first = walks.Names.KeptFor(keptPages);
        int[] counts = first.Select(c => c.Entries.Count).ToArray();
        foreach (var (_, entries) in first) entries.Clear();

        var second = walks.Names.KeptFor(keptPages);
        Assert.Equal(counts, second.Select(c => c.Entries.Count).ToArray());
        Assert.True(counts.Sum() > 0);
    }

    // ------------------------------------------------------------------
    //  A parent field placed by its own /P
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_AParentFieldPlacedByItsOwnSlashP_KeepsItsKidOnTheKeptPage()
    {
        // Object 53 is a non-terminal field that carries a /P naming page 1, and its kid 64 is a
        // widget page 1 lists. The filter found 53 placed and kept it — but never looked below a
        // placed node, so its kids had no answer and every one was dropped: the field travelled
        // without the widget the recipient sees on the page.
        byte[] pdf = RichDocument();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var parent = Fields(doc).Select(f => doc.ResolveObject(f) as PdfDictionary)
            .Single(f => Text(doc, f[PdfName.T]) == "PP");
        var kids = doc.ResolveObject(parent[PdfName.Kids]) as PdfArray;
        Assert.NotNull(kids);
        Assert.Equal(new[] { "Kid64" },
            kids.Select(k => Text(doc, (doc.ResolveObject(k) as PdfDictionary)?[PdfName.T])).ToArray());
    }

    [Fact]
    public void SplitRange_AParentFieldPlacedByItsOwnSlashP_StillDropsItsKidOnADroppedPage()
    {
        // The direct kid of object 53 names page 2; extracting page 1 must leave it and its value.
        byte[] pdf = RichDocument();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);

        Assert.DoesNotContain("VDIRECT", Encoding.ASCII.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  The field index: the same plan as the whole walk
    // ------------------------------------------------------------------

    [Fact]
    public void TheIndexedFieldWalk_PlansExactlyAsTheWholeWalk_OnRandomFieldTrees()
    {
        int compared = 0, narrowed = 0;
        for (int seed = 0; seed < 150; seed++)
        {
            var random = new Random(seed);
            byte[] pdf = RandomForm(random, out int pageCount);
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var form = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
            var index = AcroFormFieldIndex.TryBuild(form, doc);
            Assert.NotNull(index);

            var keptSets = Enumerable.Range(0, pageCount).Select(p => new[] { p }).ToList();
            keptSets.Add(Enumerable.Range(0, pageCount).Where(_ => random.Next(2) == 0).ToArray());
            keptSets.Add(new int[0]);
            foreach (var kept in keptSets)
            {
                var pages = kept.Select(p => doc.Pages[p]).ToList();
                var numbers = new HashSet<int>(pages.Select(p => p.Dictionary.ObjectNumber));

                var whole = AcroFormPageFilter.Filter(form, doc, numbers, pages);
                var indexed = AcroFormPageFilter.Filter(form, doc, numbers, pages, index: index);

                AssertSamePlan(whole, indexed, $"seed {seed}, pages [{string.Join(",", kept)}]");
                Assert.True(indexed.EdgesExamined <= whole.EdgesExamined);
                if (indexed.EdgesExamined < whole.EdgesExamined) narrowed++;
                compared++;
            }
        }

        // Potency: the comparison ran, and the index did narrow most walks.
        Assert.True(compared > 600, compared.ToString(CultureInfo.InvariantCulture));
        Assert.True(narrowed > compared / 2, $"{narrowed} of {compared}");
    }

    [Fact]
    public void FieldIndex_IsRefusedForACycle()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
            (4, "<< /Fields [5 0 R] >>"),
            (5, "<< /T (A) /Kids [6 0 R] >>"),
            (6, "<< /T (B) /Kids [5 0 R] >>"))));

        Assert.Null(AcroFormFieldIndex.TryBuild(FormOf(doc), doc));
    }

    [Theory]
    [InlineData(AcroFormPageFilter.MaxFieldDepth, true)]
    [InlineData(AcroFormPageFilter.MaxFieldDepth + 1, false)]
    public void FieldIndex_IsRefusedForAPathDeeperThanTheWalksDepthCap(int edges, bool indexed)
    {
        // The whole walk reports a node met deeper than MaxFieldDepth; a chain of that many edges is
        // the deepest tree it reads without reporting.
        using var doc = PdfDocument.Open(new MemoryStream(Chain(edges)));
        Assert.Equal(indexed, AcroFormFieldIndex.TryBuild(FormOf(doc), doc) != null);
    }

    [Fact]
    public void FieldIndex_IsRefusedForALongPathThroughANodeTheWalkReadBefore()
    {
        // /Fields [A B]. A, which page 1 lists, leads 25 levels down to a widget on page 2; B leads 25
        // levels into A's first kid: 51 edges, a path the index's own descent never takes, since it
        // reads that kid under A first. An extract of page 1 stops at A, placed, so its walk meets the
        // kid through B, past the depth cap, and reports it — which a narrowed walk, never leaving A,
        // would not. So the index must refuse the tree.
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>"),
            (3, "<< /Type /Page /Parent 2 0 R /Annots [10 0 R] >>"),
            (4, "<< /Type /Page /Parent 2 0 R /Annots [90 0 R] >>"),
            (5, "<< /Fields [10 0 R 11 0 R] >>"),
            (10, "<< /T (A) /Kids [20 0 R] >>"),
            (11, "<< /T (B) /Kids [50 0 R] >>"),
            (90, "<< /Type /Annot /Subtype /Widget /T (W) " + Rect + " /P 4 0 R >>"),
        };
        for (int i = 0; i < 25; i++) objects.Add((20 + i, $"<< /T (c{i}) /Kids [{(i < 24 ? 21 + i : 90)} 0 R] >>"));
        for (int i = 0; i < 25; i++) objects.Add((50 + i, $"<< /T (b{i}) /Kids [{(i < 24 ? 51 + i : 20)} 0 R] >>"));
        byte[] pdf = Pdf(objects.ToArray());

        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            Assert.Null(AcroFormFieldIndex.TryBuild(FormOf(doc), doc));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, 1, 1);
        Assert.Contains("nesting deeper", PdfSplitter.LastFormWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldIndex_IsRefusedForATreeTheBudgetCannotPayFor()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Chain(5)));
        var form = FormOf(doc);
        var built = AcroFormFieldIndex.TryBuild(form, doc);

        Assert.NotNull(built);
        // One /Fields entry, two per /Kids entry, and one per annotation a page lists (the end widget).
        Assert.Equal(1 + 2 * 5 + 1, built.FixedSpend);
        Assert.NotNull(AcroFormFieldIndex.TryBuild(form, doc, nodeBudget: 12));
        Assert.Null(AcroFormFieldIndex.TryBuild(form, doc, nodeBudget: 11));
    }

    [Fact]
    public void AnIndex_IsNotUsed_WhenTheWholeWalkCouldRunOutOfItsBudget()
    {
        // One unit short of what the whole walk may spend, the narrowed walk could finish where the
        // whole one stops — so the filter walks whole, and the two plans stay alike.
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [11 0 R] >>"),
            (4, "<< /Fields [10 0 R 20 0 R] >>"),
            (10, "<< /T (a) /Kids [11 0 R] >>"),
            (11, "<< /Type /Annot /Subtype /Widget /T (on) " + Rect + " /P 3 0 R >>"),
            (20, "<< /T (b) /Kids [21 0 R 22 0 R 23 0 R] >>"),
            (21, "<< /T (b1) >>"),
            (22, "<< /T (b2) >>"),
            (23, "<< /T (b3) >>"))));
        var form = FormOf(doc);
        var index = AcroFormFieldIndex.TryBuild(form, doc);
        var pages = new[] { doc.Pages[0] };
        var numbers = new HashSet<int> { doc.Pages[0].Dictionary.ObjectNumber };
        int annotations = 1;

        // Two /Fields entries, two per /Kids entry, and the one annotation the page lists.
        Assert.Equal(2 + 2 * 4 + 1, index.FixedSpend);
        int enough = annotations + (int)index.FixedSpend;
        var narrowed = AcroFormPageFilter.Filter(form, doc, numbers, pages, nodeBudget: enough, index: index);
        var whole = AcroFormPageFilter.Filter(form, doc, numbers, pages, nodeBudget: enough - 1, index: index);
        var wholeWithout = AcroFormPageFilter.Filter(form, doc, numbers, pages, nodeBudget: enough - 1);

        Assert.True(narrowed.EdgesExamined < whole.EdgesExamined);
        Assert.Equal(wholeWithout.EdgesExamined, whole.EdgesExamined);
        AssertSamePlan(wholeWithout, whole, "one unit short");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static void AssertSamePlan(AcroFormPageFilterResult expected, AcroFormPageFilterResult actual, string because)
    {
        Assert.True(expected.AnyFieldSurvives == actual.AnyFieldSurvives, because);
        Assert.True(expected.Warning == actual.Warning, because);
        Assert.True(expected.KeptNodes.Count == actual.KeptNodes.Count, because);
        for (int i = 0; i < expected.KeptNodes.Count; i++)
            Assert.True(ReferenceEquals(expected.KeptNodes[i], actual.KeptNodes[i]), because + $", kept node {i}");
        Assert.True(expected.KeptChildIndices.Count == actual.KeptChildIndices.Count, because);
        foreach (var pair in expected.KeptChildIndices)
        {
            Assert.True(actual.KeptChildIndices.TryGetValue(pair.Key, out var positions), because);
            Assert.True(pair.Value.SequenceEqual(positions), because);
        }
    }

    // The trailer's /ID is fresh for every document written, so it is the one difference allowed.
    private static string WithoutIdentifier(byte[] pdf) =>
        Regex.Replace(Encoding.Latin1.GetString(pdf), @"/ID\s*\[\s*<[0-9A-Fa-f]*>\s*<[0-9A-Fa-f]*>\s*\]",
            "/ID []", RegexOptions.None, TimeSpan.FromSeconds(10));

    private static PdfDictionary FormOf(PdfDocument doc) =>
        doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;

    private static PdfArray Fields(PdfDocument doc) =>
        doc.ResolveObject(FormOf(doc)?[PdfName.Fields]) as PdfArray ?? new PdfArray();

    private static string Text(PdfDocument doc, PdfObject obj) =>
        doc.ResolveObject(obj) is PdfString s ? Encoding.ASCII.GetString(s.RawBytes) : null;

    private static List<string> FieldTitles(PdfDocument doc)
    {
        var titles = new List<string>();
        var pending = new Stack<PdfObject>(Fields(doc).Reverse());
        while (pending.Count > 0)
        {
            if (!(doc.ResolveObject(pending.Pop()) is PdfDictionary field)) continue;
            titles.Add(Text(doc, field[PdfName.T]));
            if (doc.ResolveObject(field[PdfName.Kids]) is PdfArray kids)
                foreach (var kid in kids.Reverse()) pending.Push(kid);
        }
        return titles;
    }

    private static List<string> DestinationNames(PdfDocument doc)
    {
        var names = new List<string>();
        var nameDict = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]) as PdfDictionary;
        var dests = doc.ResolveObject(nameDict?[PdfName.Dests]) as PdfDictionary;
        if (doc.ResolveObject(dests?[PdfName.Names]) is PdfArray leaf)
            for (int i = 0; i + 1 < leaf.Count; i += 2) names.Add(Text(doc, leaf[i]));
        return names;
    }

    private static List<string> OutlineTitles(PdfDocument doc)
    {
        var titles = new List<string>();
        var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var item = doc.ResolveObject(root?[PdfName.First]) as PdfDictionary;
        for (int guard = 0; item != null && guard < 1000; guard++)
        {
            titles.Add(Text(doc, item[PdfName.Title]));
            item = doc.ResolveObject(item[PdfName.Next]) as PdfDictionary;
        }
        return titles;
    }

    private const string Rect = "/Rect [72 700 200 720]";

    private static string Page(string annots) =>
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 20 0 R" +
        (annots == null ? "" : " /Annots [" + annots + "]") + " >>";

    /// <summary>
    /// Four pages with everything a split prunes: named destinations in a two-leaf tree (an array, an
    /// indirect << /D >>, a string naming another, a duplicated key, one landing on no page), a
    /// /JavaScript category carried whole, a nested outline (a named-destination item, a /GoTo item,
    /// a /URI item, an item whose own page is gone but whose child's is not), and a form with a
    /// parent over kids on two pages, a kid two parents share, a /P-only widget, a parent placed by
    /// its own /P, and a direct kid.
    /// </summary>
    private static byte[] RichDocument() => Pdf(
        (1, "<< /Type /Catalog /Pages 2 0 R /Names 3 0 R /Outlines 4 0 R /AcroForm 5 0 R >>"),
        (2, "<< /Type /Pages /Kids [10 0 R 11 0 R 12 0 R 13 0 R] /Count 4 >>"),
        (3, "<< /Dests 6 0 R /JavaScript 7 0 R >>"),
        (4, "<< /Type /Outlines /First 40 0 R /Last 43 0 R /Count 6 >>"),
        (5, "<< /Fields [50 0 R 51 0 R 52 0 R 53 0 R] /CO [60 0 R 61 0 R] /DA (/Helv 0 Tf 0 g) >>"),
        (6, "<< /Kids [8 0 R 9 0 R] >>"),
        (7, "<< /Names [(js1) 31 0 R] >>"),
        (8, "<< /Names [(a) [10 0 R /Fit] (b) 30 0 R (c) (a)] /Limits [(a) (c)] >>"),
        (9, "<< /Names [(d) [12 0 R /Fit] (d) [13 0 R /Fit] (e) [99 0 R /Fit]] /Limits [(d) (e)] >>"),
        (10, Page("60 0 R 64 0 R 70 0 R")),
        (11, Page("61 0 R")),
        (12, Page("62 0 R")),
        (13, Page(null)),
        (20, "<< /Length 0 >>\nstream\n\nendstream"),
        (30, "<< /D [11 0 R /XYZ 0 0 0] >>"),
        (31, "<< /S /JavaScript /JS (app.alert\\(1\\)) >>"),
        (40, "<< /Title (One) /Parent 4 0 R /Dest [10 0 R /Fit] /Next 41 0 R /First 44 0 R /Last 45 0 R /Count 2 >>"),
        (41, "<< /Title (Web) /Parent 4 0 R /Prev 40 0 R /Next 42 0 R /A << /S /URI /URI (http://example.com) >> >>"),
        (42, "<< /Title (Nowhere) /Parent 4 0 R /Prev 41 0 R /Next 43 0 R /Dest [99 0 R /Fit] /First 46 0 R /Last 46 0 R /Count 1 >>"),
        (43, "<< /Title (Last) /Parent 4 0 R /Prev 42 0 R /Dest (d) >>"),
        (44, "<< /Title (One.a) /Parent 40 0 R /Dest (b) /Next 45 0 R >>"),
        (45, "<< /Title (One.b) /Parent 40 0 R /Prev 44 0 R /A << /S /GoTo /D [12 0 R /Fit] >> >>"),
        (46, "<< /Title (Nowhere.deep) /Parent 42 0 R /Dest [13 0 R /Fit] >>"),
        (50, "<< /FT /Tx /T (Addr) /Kids [60 0 R 61 0 R] >>"),
        (51, "<< /FT /Tx /T (Shared) /Kids [62 0 R] >>"),
        (52, "<< /FT /Tx /T (Shared2) /Kids [62 0 R 63 0 R] >>"),
        (53, "<< /FT /Tx /T (PP) /P 10 0 R /Kids [64 0 R << /Type /Annot /Subtype /Widget /T (Direct) /V (VDIRECT) " +
             Rect + " /P 11 0 R >>] >>"),
        (60, "<< /Type /Annot /Subtype /Widget /T (Street) /V (S1) /Parent 50 0 R " + Rect + " /P 10 0 R >>"),
        (61, "<< /Type /Annot /Subtype /Widget /T (City) /V (C1) /Parent 50 0 R " + Rect + " /P 11 0 R >>"),
        (62, "<< /Type /Annot /Subtype /Widget /T (W62) /V (V62) " + Rect + " /P 12 0 R >>"),
        (63, "<< /Type /Annot /Subtype /Widget /T (W63) /V (V63) " + Rect + " /P 13 0 R >>"),
        (64, "<< /Type /Annot /Subtype /Widget /T (Kid64) /V (V64) /Parent 53 0 R " + Rect + " >>"),
        (70, "<< /Type /Annot /Subtype /Link " + Rect + " /A << /S /GoTo /D [11 0 R /Fit] >> >>"));

    /// <summary>Pages each with a named destination, an outline item and a text field.</summary>
    private static byte[] OneOfEachPerPage(int pages)
    {
        var objects = new List<(int, string)>();
        int firstPage = 6, firstField = firstPage + pages, firstItem = firstField + pages, content = firstItem + pages;
        var kids = new StringBuilder();
        var fields = new StringBuilder();
        var names = new StringBuilder();
        for (int p = 0; p < pages; p++)
        {
            kids.Append(firstPage + p).Append(" 0 R ");
            fields.Append(firstField + p).Append(" 0 R ");
            names.Append("(d").Append(p.ToString("D6", CultureInfo.InvariantCulture)).Append(") [")
                 .Append(firstPage + p).Append(" 0 R /Fit] ");
        }
        objects.Add((1, "<< /Type /Catalog /Pages 2 0 R /Names 3 0 R /Outlines 4 0 R /AcroForm 5 0 R >>"));
        objects.Add((2, $"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] >>"));
        objects.Add((3, $"<< /Dests << /Names [{names}] >> >>"));
        objects.Add((4, $"<< /Type /Outlines /First {firstItem} 0 R /Last {firstItem + pages - 1} 0 R /Count {pages} >>"));
        objects.Add((5, $"<< /Fields [{fields}] >>"));
        for (int p = 0; p < pages; p++)
            objects.Add((firstPage + p, $"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R /Annots [{firstField + p} 0 R] >>"));
        for (int p = 0; p < pages; p++)
            objects.Add((firstField + p,
                $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (f{p}) /V (v{p}) {Rect} /P {firstPage + p} 0 R >>"));
        for (int p = 0; p < pages; p++)
        {
            string prev = p > 0 ? $" /Prev {firstItem + p - 1} 0 R" : "";
            string next = p < pages - 1 ? $" /Next {firstItem + p + 1} 0 R" : "";
            objects.Add((firstItem + p, $"<< /Title (item {p}) /Parent 4 0 R /Dest [{firstPage + p} 0 R /Fit]{prev}{next} >>"));
        }
        objects.Add((content, "<< /Length 0 >>\nstream\n\nendstream"));
        return Pdf(objects.ToArray());
    }

    /// <summary>A form whose only field is a chain of <paramref name="edges"/> /Kids links, its end on the page.</summary>
    private static byte[] Chain(int edges)
    {
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{10 + edges} 0 R] >>"),
            (4, "<< /Fields [10 0 R] >>"),
        };
        for (int i = 0; i < edges; i++) objects.Add((10 + i, $"<< /T (n{i}) /Kids [{11 + i} 0 R] >>"));
        objects.Add((10 + edges, $"<< /Type /Annot /Subtype /Widget /T (end) {Rect} /P 3 0 R >>"));
        return Pdf(objects.ToArray());
    }

    /// <summary>
    /// A random acyclic field graph over a few pages: shared kids, repeated entries, entries that are
    /// not dictionaries, direct kids, parents with a /P of their own, widgets placed by /Annots only,
    /// by /P only, or by both on different pages.
    /// </summary>
    private static byte[] RandomForm(Random random, out int pageCount)
    {
        pageCount = random.Next(2, 6);
        int nodes = random.Next(4, 30);
        const int firstPage = 10, firstNode = 100;

        var annots = new List<string>[pageCount];
        for (int p = 0; p < pageCount; p++) annots[p] = new List<string>();
        string PageRef(int p) => (firstPage + p).ToString(CultureInfo.InvariantCulture) + " 0 R";

        var bodies = new string[nodes];
        for (int n = 0; n < nodes; n++)
        {
            var body = new StringBuilder("<< /T (n").Append(n).Append(')');
            if (random.Next(3) == 0) body.Append(" /P ").Append(PageRef(random.Next(pageCount)));

            var kids = new List<string>();
            int kidCount = n < nodes - 1 ? random.Next(0, 4) : 0;
            for (int k = 0; k < kidCount; k++)
            {
                int roll = random.Next(10);
                if (roll == 0) kids.Add("0");
                else if (roll == 1)
                    kids.Add("<< /T (d" + n + "_" + k + ") /P " + PageRef(random.Next(pageCount)) + " >>");
                else kids.Add((firstNode + random.Next(n + 1, nodes)).ToString(CultureInfo.InvariantCulture) + " 0 R");
            }
            if (kidCount > 1 && random.Next(4) == 0) kids.Add(kids[0]);
            if (kids.Count > 0) body.Append(" /Kids [").Append(string.Join(" ", kids)).Append(']');
            body.Append(" >>");
            bodies[n] = body.ToString();

            if (random.Next(3) == 0)
                annots[random.Next(pageCount)].Add((firstNode + n).ToString(CultureInfo.InvariantCulture) + " 0 R");
        }

        var top = new List<string>();
        for (int n = 0; n < nodes; n++)
            if (random.Next(5) < 2) top.Add((firstNode + n).ToString(CultureInfo.InvariantCulture) + " 0 R");
        if (top.Count > 1 && random.Next(3) == 0) top.Add(top[top.Count - 1]);
        if (random.Next(4) == 0) top.Add("<< /T (topdirect) /P " + PageRef(random.Next(pageCount)) + " >>");

        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>"),
            (2, "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pageCount).Select(PageRef)) +
                "] /Count " + pageCount + " >>"),
            (4, "<< /Fields [" + string.Join(" ", top) + "] >>"),
        };
        for (int p = 0; p < pageCount; p++)
        {
            if (random.Next(4) == 0) annots[p].Add("<< /Type /Annot /Subtype /Widget " + Rect + " >>");
            objects.Add((firstPage + p, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [" +
                                        string.Join(" ", annots[p]) + "] >>"));
        }
        for (int n = 0; n < nodes; n++) objects.Add((firstNode + n, bodies[n]));
        return Pdf(objects.ToArray());
    }

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
