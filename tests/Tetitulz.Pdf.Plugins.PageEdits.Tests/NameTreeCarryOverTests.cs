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
/// Merge and split now carry the catalog <c>/Names</c> trees (ISO 32000-1 §7.9.6), and split also
/// carries <c>/Outlines</c> pruned to the pages it extracted.
///
/// <para>The assertions here are deliberately about <b>key bytes and resolved page numbers</b>
/// rather than "a /Names entry exists": a name tree that survives with the wrong ordering, or whose
/// destinations point at the wrong page, is exactly as broken as one that was dropped, and looks
/// identical to a shape-only test.</para>
/// </summary>
public class NameTreeCarryOverTests
{
    // ------------------------------------------------------------------
    //  Merge — item 37
    // ------------------------------------------------------------------

    [Fact]
    public void Merge_SortsNamedDestinationsByOrdinalByteValueNotDecodedText()
    {
        // "Zed" and a UTF-16BE key <FEFF 0041> = "A". By BYTE value 'Z' (0x5A) precedes 0xFE, so
        // the UTF-16BE key sorts last; by DECODED TEXT "A" would sort first. The two orderings
        // disagree, which is the whole point — §7.9.6 mandates the byte one.
        byte[] a = SourceWithDests(("(Zed)", 3), ("<FEFF0041>", 3));
        byte[] b = SourceWithDests(("(Mid)", 3));

        using var merged = Merge(a, b);

        var keys = DestKeys(merged);
        Assert.Equal(3, keys.Count);
        Assert.Equal(new byte[] { 0x4D, 0x69, 0x64 }, keys[0]);                   // Mid
        Assert.Equal(new byte[] { 0x5A, 0x65, 0x64 }, keys[1]);                   // Zed
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, keys[2]);             // UTF-16BE "A"

        // …and each destination still points at the page it came from, after the merge remap.
        int firstDocPage = merged.Pages[0].Dictionary.ObjectNumber;
        int secondDocPage = merged.Pages[1].Dictionary.ObjectNumber;
        Assert.Equal(secondDocPage, DestPage(merged, "Mid"));
        Assert.Equal(firstDocPage, DestPage(merged, "Zed"));
    }

    [Fact]
    public void Merge_CollidingDestinationName_SuffixesLaterSourceAndRepointsItsBookmark()
    {
        byte[] a = SourceWithDests(("(Home)", 3));
        byte[] b = SourceWithDestsAndBookmark("(Home)", "(Home)");

        using var merged = Merge(a, b);

        var keys = DestKeys(merged);
        // Never overwrite: overwriting would leave one entry and silently re-point the second
        // document's bookmark at the first document's page.
        Assert.Equal(2, keys.Count);
        Assert.Equal("Home", Encoding.ASCII.GetString(keys[0]));
        Assert.Equal("Home.1", Encoding.ASCII.GetString(keys[1]));

        Assert.Equal(merged.Pages[0].Dictionary.ObjectNumber, DestPage(merged, "Home"));
        Assert.Equal(merged.Pages[1].Dictionary.ObjectNumber, DestPage(merged, "Home.1"));

        // The second source's bookmark must follow the rename, or its entry is present but
        // unreachable — the same invisible failure as overwriting it.
        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var item = merged.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        Assert.Equal("Home.1", Encoding.ASCII.GetString(((PdfString)item[PdfName.Dest]).RawBytes));

        Assert.Contains("collided", PdfMerger.LastNameTreeWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_CollidingGoToActionDestination_FollowsTheRename()
    {
        byte[] a = SourceWithDests(("(Home)", 3));
        byte[] b = SourceWithDestsAndGoToActionBookmark("(Home)", "(Home)");

        using var merged = Merge(a, b);

        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var item = merged.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        var action = merged.ResolveObject(item[PdfName.A]) as PdfDictionary;
        Assert.Equal("Home.1", Encoding.ASCII.GetString(((PdfString)action[PdfName.D]).RawBytes));
    }

    /// <summary>
    /// A bookmark whose <c>/Dest</c> is an indirect string follows the rename too.
    /// </summary>
    /// <remarks>
    /// §7.3.10 lets the name be its own object, and the rename walk only rewrote a string it found
    /// in place: <c>/Dest 9 0 R</c> is a reference, so the walk stepped past it and the object it
    /// names still read <c>(Home)</c> — a key the merge had given to the FIRST document. The
    /// bookmark did not go nowhere; it went to the wrong document's page.
    /// </remarks>
    [Fact]
    public void Merge_CollidingDestinationName_RepointsABookmarkWhoseDestIsIndirect()
    {
        byte[] a = SourceWithDests(("(Home)", 3));
        byte[] b = SourceWithDestsAndBookmark("(Home)", "9 0 R", "(Home)");

        using var merged = Merge(a, b);

        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var item = merged.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        var dest = Assert.IsType<PdfString>(merged.ResolveObject(item[PdfName.Dest]));
        Assert.Equal("Home.1", Encoding.ASCII.GetString(dest.RawBytes));
        Assert.Equal(merged.Pages[1].Dictionary.ObjectNumber, DestPage(merged, "Home.1"));
    }

    /// <summary>The same for a <c>/GoTo</c> action whose <c>/D</c> is an indirect string.</summary>
    [Fact]
    public void Merge_CollidingGoToActionDestination_FollowsTheRenameWhenDIsIndirect()
    {
        byte[] a = SourceWithDests(("(Home)", 3));
        byte[] b = SourceWithDestsAndGoToActionBookmark("(Home)", "10 0 R", "(Home)");

        using var merged = Merge(a, b);

        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var item = merged.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        var action = merged.ResolveObject(item[PdfName.A]) as PdfDictionary;
        var dest = Assert.IsType<PdfString>(merged.ResolveObject(action[PdfName.D]));
        Assert.Equal("Home.1", Encoding.ASCII.GetString(dest.RawBytes));
    }

    /// <summary>
    /// A destination name reached through a <b>chain</b> of references follows the rename too — for a
    /// bookmark's <c>/Dest</c> and for a <c>/GoTo</c> action's <c>/D</c>.
    /// </summary>
    /// <remarks>
    /// §7.3.10 lets an indirect object's body be another reference: <c>9 0 obj 10 0 R endobj</c>.
    /// Taking one hop would hand back the second reference, which is not a string, and leave the name
    /// as the key the merge had given the other document. The merger and the splitter share one helper,
    /// which the splitter's <c>IndirectDestinationChainTests</c> pins too.
    /// </remarks>
    [Theory]
    [InlineData(false)]   // /Dest 9 0 R, 9 = 10 0 R, 10 = (Home)
    [InlineData(true)]    // /A << /S /GoTo /D 10 0 R >>, 10 = 11 0 R, 11 = (Home)
    public void Merge_CollidingDestinationName_FollowsTheRenameThroughAReferenceChain(bool viaGoToAction)
    {
        byte[] a = SourceWithDests(("(Home)", 3));
        byte[] b = viaGoToAction
            ? SourceWithDestsAndGoToActionBookmark("(Home)", "10 0 R", "11 0 R", "(Home)")
            : SourceWithDestsAndBookmark("(Home)", "9 0 R", "10 0 R", "(Home)");

        using var merged = Merge(a, b);

        var outlines = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var item = merged.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        PdfObject named = viaGoToAction
            ? (merged.ResolveObject(item[PdfName.A]) as PdfDictionary)?[PdfName.D]
            : item[PdfName.Dest];
        var dest = Assert.IsType<PdfString>(merged.ResolveObject(named));
        Assert.Equal("Home.1", Encoding.ASCII.GetString(dest.RawBytes));
    }

    [Fact]
    public void Merge_NonDestinationCategoryIsCarriedOverToo()
    {
        // /JavaScript values are dictionaries, not destinations — nothing about them is page-bound,
        // so both sources' entries survive untouched.
        byte[] a = SourceWithJavaScript("(scriptA)");
        byte[] b = SourceWithJavaScript("(scriptB)");

        using var merged = Merge(a, b);

        var names = merged.ResolveObject(merged.Catalog.Dictionary[PdfName.Names]) as PdfDictionary;
        var leaf = merged.ResolveObject(names[PdfName.JavaScript]) as PdfDictionary;
        var pairs = merged.ResolveObject(leaf[PdfName.Names]) as PdfArray;
        Assert.Equal(4, pairs.Count);
        Assert.Equal("scriptA", Encoding.ASCII.GetString(((PdfString)pairs[0]).RawBytes));
        Assert.Equal("scriptB", Encoding.ASCII.GetString(((PdfString)pairs[2]).RawBytes));
        // The value has to survive as a real dictionary; an unmapped reference would arrive as null.
        var action = merged.ResolveObject(pairs[1]) as PdfDictionary;
        Assert.Equal("JavaScript", (merged.ResolveObject(action[PdfName.S]) as PdfName)?.Value);
    }

    [Fact]
    public void Merge_NoSourceHasNames_WritesNoNameDictionary()
    {
        byte[] a = SourceWithDests();
        byte[] b = SourceWithDests();

        using var merged = Merge(a, b);

        Assert.Null(merged.Catalog.Dictionary[PdfName.Names]);
        Assert.Null(PdfMerger.LastNameTreeWarning);
    }

    [Fact]
    public void Merge_NameTreeKidsCycle_DoesNotHangAndEmitsEachEntryOnce()
    {
        // Object 6 is a name-tree node whose /Kids points back at itself. Depth alone does not
        // bound this — the cycle is one node deep — so the visited set is what terminates it, and
        // the entry must appear exactly once rather than once per revisit.
        byte[] hostile = SourceWithSelfReferencingNameTree();

        using var merged = Merge(hostile, SourceWithDests());

        var keys = DestKeys(merged);
        Assert.Single(keys);
        Assert.Equal("a", Encoding.ASCII.GetString(keys[0]));
    }

    // ------------------------------------------------------------------
    //  Split — item 38
    // ------------------------------------------------------------------

    [Fact]
    public void SplitRange_KeepsOnlyTheOutlineItemsWhosePagesWereExtracted()
    {
        using var extracted = Split(ThreePagePdf(), 2, 2);

        var outlines = extracted.ResolveObject(extracted.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        Assert.NotNull(outlines);
        Assert.Equal(1, (int)((PdfNumber)extracted.ResolveObject(outlines[PdfName.Count])).DoubleValue);

        var titles = OutlineTitles(extracted);
        Assert.Equal(new[] { "B2" }, titles);

        // The survivor's chain must be rebuilt around the dropped siblings, not left dangling at
        // the object numbers they occupied.
        var item = extracted.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        Assert.Null(item[PdfName.Prev]);
        Assert.Null(item[PdfName.Next]);
        Assert.Equal(item.ObjectNumber, ((PdfReference)outlines[PdfName.Last]).ReferenceObjectNumber);

        var dest = extracted.ResolveObject(item[PdfName.Dest]) as PdfArray;
        Assert.Equal(extracted.Pages[0].Dictionary.ObjectNumber,
            ((PdfReference)dest[0]).ReferenceObjectNumber);
    }

    [Fact]
    public void SplitRange_KeepsAParentForItsSurvivingChildAndStripsTheParentsOwnDestination()
    {
        using var extracted = Split(NestedOutlinePdf(), 2, 2);

        var outlines = extracted.ResolveObject(extracted.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var parent = extracted.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        Assert.Equal("Parent", Encoding.ASCII.GetString(((PdfString)parent[PdfName.Title]).RawBytes));

        // The parent's own destination was page 1, which is not in the output. Keeping it would
        // leave /Dest [null /Fit]; the item stays only as a grouping node.
        Assert.Null(parent[PdfName.Dest]);
        Assert.Null(parent[PdfName.A]);

        var child = extracted.ResolveObject(parent[PdfName.First]) as PdfDictionary;
        Assert.Equal("Child", Encoding.ASCII.GetString(((PdfString)child[PdfName.Title]).RawBytes));
        Assert.Equal(parent.ObjectNumber, ((PdfReference)child[PdfName.Parent]).ReferenceObjectNumber);

        var dest = extracted.ResolveObject(child[PdfName.Dest]) as PdfArray;
        Assert.Equal(extracted.Pages[0].Dictionary.ObjectNumber,
            ((PdfReference)dest[0]).ReferenceObjectNumber);

        Assert.Equal(2, (int)((PdfNumber)extracted.ResolveObject(outlines[PdfName.Count])).DoubleValue);
    }

    [Fact]
    public void SplitRange_DropsNamedDestinationsOutsideTheExtractedRange()
    {
        using var extracted = Split(ThreePagePdf(), 2, 2);

        var keys = DestKeys(extracted);
        Assert.Single(keys);
        Assert.Equal("d2", Encoding.ASCII.GetString(keys[0]));
        Assert.Equal(extracted.Pages[0].Dictionary.ObjectNumber, DestPage(extracted, "d2"));
    }

    [Theory]
    [InlineData(2, true)]   // (d2) names page 2 — extracted, so the bookmark keeps its target
    [InlineData(1, false)]  // page 1 extracted — (d2) is unreachable, so the bookmark goes
    public void SplitRange_ResolvesANamedDestinationBeforeDecidingToKeepTheBookmark(int page, bool expectKept)
    {
        using var extracted = Split(NamedDestinationBookmarkPdf(), page, page);

        var outlines = extracted.ResolveObject(extracted.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        if (!expectKept)
        {
            Assert.Null(outlines);
            return;
        }

        var item = extracted.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        Assert.Equal("ByName", Encoding.ASCII.GetString(((PdfString)item[PdfName.Title]).RawBytes));
        // The bookmark still names its destination, and the destination still resolves — the two
        // halves have to agree, which is why /Names is filtered before the outline is pruned.
        Assert.Equal("d2", Encoding.ASCII.GetString(((PdfString)item[PdfName.Dest]).RawBytes));
        Assert.Equal(extracted.Pages[0].Dictionary.ObjectNumber, DestPage(extracted, "d2"));
    }

    [Fact]
    public void SplitRange_TrailerSizeCoversTheObjectsAllocatedAfterTheCatalog()
    {
        byte[] output = SplitToBytes(ThreePagePdf(), 2, 2);

        int highest = 0;
        foreach (Match m in Regex.Matches(Encoding.ASCII.GetString(output), @"(?m)^(\d+) 0 obj"))
            highest = Math.Max(highest, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));

        using var ms = new MemoryStream(output);
        using var doc = PdfDocument.Open(ms);
        int size = (int)((PdfNumber)doc.Trailer[PdfName.Size]).DoubleValue;

        // /Size is one greater than the highest object number (§7.5.5). The snapshot this replaced
        // was taken before the /Names leaves and the outline root were numbered, so it under-counted
        // by exactly those objects.
        Assert.True(highest > 0, "no indirect objects were written");
        Assert.Equal(highest + 1, size);
    }

    [Fact]
    public void SplitRange_NoOutlineSurvives_WritesNoOutlinesEntry()
    {
        // Every bookmark in this fixture points at page 1 or 2; extracting page 3 leaves none.
        using var extracted = Split(ThreePagePdf(), 3, 3);

        Assert.Null(extracted.Catalog.Dictionary[PdfName.Outlines]);
        Assert.Null(extracted.Catalog.Dictionary[PdfName.Names]);
    }

    [Fact]
    public void SplitRange_CarriesNonDestinationCategoriesThrough()
    {
        // /JavaScript entries are not page-bound, so nothing filters them — but their values do
        // have to be collected and remapped, which is a different path from a /Dests array.
        using var extracted = Split(JavaScriptNamePdf(), 2, 2);

        var names = extracted.ResolveObject(extracted.Catalog.Dictionary[PdfName.Names]) as PdfDictionary;
        var leaf = extracted.ResolveObject(names[PdfName.JavaScript]) as PdfDictionary;
        var pairs = extracted.ResolveObject(leaf[PdfName.Names]) as PdfArray;
        Assert.Equal(2, pairs.Count);
        Assert.Equal("onOpen", Encoding.ASCII.GetString(((PdfString)pairs[0]).RawBytes));
        var action = extracted.ResolveObject(pairs[1]) as PdfDictionary;
        Assert.Equal("JavaScript", (extracted.ResolveObject(action[PdfName.S]) as PdfName)?.Value);
    }

    [Fact]
    public void SplitRange_NameTreeDepthOverflow_ReportsTruncationInsteadOfSilentlyDroppingEntries()
    {
        // A /Kids chain deeper than PdfLimits.MaxTreeDepth. The walk stops, so the one entry at the
        // bottom never arrives — and the caller has to be able to tell that from "the source had no
        // named destinations", which is exactly what the two look like in the output.
        using var extracted = Split(DeeplyNestedNameTreePdf(), 1, 1);

        Assert.Contains("incomplete", PdfSplitter.LastNameTreeWarning ?? "", StringComparison.Ordinal);
        Assert.Empty(DestKeys(extracted));
    }

    [Fact]
    public void SplitRange_OutlineNextCycle_DoesNotHangAndKeepsEachItemOnce()
    {
        // B2's /Next points back at B1. The chain has no end, so only the visited set stops it.
        using var extracted = Split(CyclicOutlinePdf(), 1, 2);

        Assert.Equal(new[] { "B1", "B2" }, OutlineTitles(extracted));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static PdfDocument Merge(byte[] a, byte[] b)
    {
        using var ma = new MemoryStream(a);
        using var mb = new MemoryStream(b);
        using var output = new MemoryStream();
        PdfMerger.Merge(output, ma, mb);
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static byte[] SplitToBytes(byte[] source, int startPage, int endPage)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.SplitRange(input, output, startPage, endPage);
        return output.ToArray();
    }

    private static PdfDocument Split(byte[] source, int startPage, int endPage) =>
        PdfDocument.Open(new MemoryStream(SplitToBytes(source, startPage, endPage)));

    // The raw key bytes of the catalog's /Names /Dests leaf, in the order they were written.
    private static List<byte[]> DestKeys(PdfDocument doc)
    {
        var result = new List<byte[]>();
        if (!(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]) is PdfDictionary names)) return result;
        if (!(doc.ResolveObject(names[PdfName.Dests]) is PdfDictionary leaf)) return result;
        if (!(doc.ResolveObject(leaf[PdfName.Names]) is PdfArray pairs)) return result;
        for (int i = 0; i + 1 < pairs.Count; i += 2)
            if (doc.ResolveObject(pairs[i]) is PdfString key) result.Add(key.RawBytes);
        return result;
    }

    // The page object number the named destination lands on, or 0.
    private static int DestPage(PdfDocument doc, string name)
    {
        var names = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]) as PdfDictionary;
        var leaf = doc.ResolveObject(names[PdfName.Dests]) as PdfDictionary;
        var pairs = doc.ResolveObject(leaf[PdfName.Names]) as PdfArray;
        for (int i = 0; i + 1 < pairs.Count; i += 2)
        {
            if (!(doc.ResolveObject(pairs[i]) is PdfString key)) continue;
            if (Encoding.ASCII.GetString(key.RawBytes) != name) continue;
            if (doc.ResolveObject(pairs[i + 1]) is PdfArray dest && dest.Count > 0 && dest[0] is PdfReference r)
                return r.ReferenceObjectNumber;
        }
        return 0;
    }

    private static List<string> OutlineTitles(PdfDocument doc)
    {
        var titles = new List<string>();
        if (!(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines]) is PdfDictionary outlines))
            return titles;
        var cur = doc.ResolveObject(outlines[PdfName.First]) as PdfDictionary;
        int guard = 0;
        while (cur != null && guard++ < 50)
        {
            if (cur[PdfName.Title] is PdfString t) titles.Add(Encoding.ASCII.GetString(t.RawBytes));
            cur = doc.ResolveObject(cur[PdfName.Next]) as PdfDictionary;
        }
        return titles;
    }

    // ------------------------------------------------------------------
    //  Fixtures — hand-built so the object numbers in the assertions above are readable
    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";

    // 1-page document; objects 1–4 are catalog/pages/page/contents, 5+ are the extras.
    private static byte[] OnePageBase(string catalogExtras, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtras}>>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        };
        bodies.AddRange(extraObjects);
        return Assemble(bodies);
    }

    private static byte[] SourceWithDests(params (string Key, int PageObj)[] dests)
    {
        if (dests.Length == 0) return OnePageBase("");
        string pairs = string.Join(" ", dests.Select(d => $"{d.Key} [{d.PageObj} 0 R /Fit]"));
        return OnePageBase("/Names 5 0 R ",
            "<< /Dests 6 0 R >>",
            $"<< /Names [ {pairs} ] >>");
    }

    // Objects 9, 10, … when given, are what an indirect bookmarkDest ("9 0 R") names.
    private static byte[] SourceWithDestsAndBookmark(string destKey, string bookmarkDest, params string[] fromObject9) =>
        OnePageBase("/Names 5 0 R /Outlines 7 0 R ",
            new[]
            {
                "<< /Dests 6 0 R >>",
                $"<< /Names [ {destKey} [3 0 R /Fit] ] >>",
                "<< /Type /Outlines /First 8 0 R /Last 8 0 R /Count 1 >>",
                $"<< /Title (B) /Parent 7 0 R /Dest {bookmarkDest} >>",
            }.Concat(fromObject9).ToArray());

    // Objects 10, 11, … when given, are what an indirect actionDest ("10 0 R") names.
    private static byte[] SourceWithDestsAndGoToActionBookmark(string destKey, string actionDest, params string[] fromObject10) =>
        OnePageBase("/Names 5 0 R /Outlines 7 0 R ",
            new[]
            {
                "<< /Dests 6 0 R >>",
                $"<< /Names [ {destKey} [3 0 R /Fit] ] >>",
                "<< /Type /Outlines /First 8 0 R /Last 8 0 R /Count 1 >>",
                "<< /Title (B) /Parent 7 0 R /A 9 0 R >>",
                $"<< /S /GoTo /D {actionDest} >>",
            }.Concat(fromObject10).ToArray());

    private static byte[] SourceWithJavaScript(string key) =>
        OnePageBase("/Names 5 0 R ",
            "<< /JavaScript 6 0 R >>",
            $"<< /Names [ {key} 7 0 R ] >>",
            "<< /S /JavaScript /JS (app.alert\\(1\\);) >>");

    private static byte[] SourceWithSelfReferencingNameTree() =>
        OnePageBase("/Names 5 0 R ",
            "<< /Dests 6 0 R >>",
            "<< /Kids [6 0 R] /Names [ (a) [3 0 R /Fit] ] >>");

    // 3 pages; bookmarks B1→page 1 and B2→page 2; named destinations d1→page 1, d2→page 2.
    private static byte[] ThreePagePdf() => ThreePageBase(
        "/Outlines 9 0 R /Names 12 0 R ",
        "<< /Type /Outlines /First 10 0 R /Last 11 0 R /Count 2 >>",
        "<< /Title (B1) /Parent 9 0 R /Next 11 0 R /Dest [3 0 R /Fit] >>",
        "<< /Title (B2) /Parent 9 0 R /Prev 10 0 R /Dest [5 0 R /Fit] >>",
        "<< /Dests 13 0 R >>",
        "<< /Names [ (d1) [3 0 R /Fit] (d2) [5 0 R /Fit] ] >>");

    // Parent→page 1 with one child→page 2.
    private static byte[] NestedOutlinePdf() => ThreePageBase(
        "/Outlines 9 0 R ",
        "<< /Type /Outlines /First 10 0 R /Last 10 0 R /Count 2 >>",
        "<< /Title (Parent) /Parent 9 0 R /Dest [3 0 R /Fit] /First 11 0 R /Last 11 0 R /Count 1 >>",
        "<< /Title (Child) /Parent 10 0 R /Dest [5 0 R /Fit] >>");

    // One bookmark whose destination is the NAME (d2), which the /Dests tree maps to page 2.
    private static byte[] NamedDestinationBookmarkPdf() => ThreePageBase(
        "/Outlines 9 0 R /Names 11 0 R ",
        "<< /Type /Outlines /First 10 0 R /Last 10 0 R /Count 1 >>",
        "<< /Title (ByName) /Parent 9 0 R /Dest (d2) >>",
        "<< /Dests 12 0 R >>",
        "<< /Names [ (d2) [5 0 R /Fit] ] >>");

    // B1→page 1, B2→page 2, and B2's /Next points back at B1.
    private static byte[] CyclicOutlinePdf() => ThreePageBase(
        "/Outlines 9 0 R ",
        "<< /Type /Outlines /First 10 0 R /Last 11 0 R /Count 2 >>",
        "<< /Title (B1) /Parent 9 0 R /Next 11 0 R /Dest [3 0 R /Fit] >>",
        "<< /Title (B2) /Parent 9 0 R /Next 10 0 R /Dest [5 0 R /Fit] >>");

    private static byte[] JavaScriptNamePdf() => ThreePageBase(
        "/Names 9 0 R ",
        "<< /JavaScript 10 0 R >>",
        "<< /Names [ (onOpen) 11 0 R ] >>",
        "<< /S /JavaScript /JS (app.alert\\(1\\);) >>");

    // A /Dests tree whose /Kids chain is far deeper than PdfLimits.MaxTreeDepth (100). Each level
    // costs two (reference, then dictionary), so 60 links overshoot it comfortably.
    private static byte[] DeeplyNestedNameTreePdf()
    {
        const int links = 60;
        var extras = new List<string> { "<< /Dests 10 0 R >>" };
        for (int i = 0; i < links; i++)
            extras.Add($"<< /Kids [{11 + i} 0 R] >>");
        extras.Add("<< /Names [ (deep) [3 0 R /Fit] ] >>");
        return ThreePageBase("/Names 9 0 R ", extras.ToArray());
    }

    // Objects 1–8 are catalog / pages / (page, contents) x3; extras start at 9.
    private static byte[] ThreePageBase(string catalogExtras, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtras}>>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R /Resources << >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        };
        bodies.AddRange(extraObjects);
        return Assemble(bodies);
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
