// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// What a merge shifts by each source's pages — page-label and parent-tree keys — and the field names it
/// keeps unique. A page-label key past its source's last page landed inside the next source's range; a
/// declared <c>/ParentTreeNextKey</c> of <c>long.MaxValue</c> wrapped every later key negative; two
/// untitled parents each of a <c>Total</c> merged into two fields of one name; and each tagged source's
/// shift walked every object of the merge.
/// </summary>
public sealed class MergeKeysAndNamesTests
{
    // ---------------------------------------------------------------- page labels

    [Fact]
    public void APageLabelKeyPastItsSourcesPages_IsDropped_AndTheKeysStayInOrder()
    {
        using var merged = Merge(Labelled(3, "[0 << /S /D >> 10 << /S /r >>]"), Labelled(2, "[0 << /S /A >>]"));

        Assert.Equal(new long[] { 0, 3 }, LabelKeys(merged));
        Assert.Contains("outside its 3 page(s)", PdfMerger.LastPageLabelWarning ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A source whose first range starts after its first page gets the default before it, as a source
    /// with none does — the previous source's last range ran on over its first page.
    /// </summary>
    [Fact]
    public void ASourceWhoseLabelsStartLate_GetsTheDefaultBeforeThem()
    {
        using var merged = Merge(Labelled(3, "[0 << /S /r >>]"), Labelled(2, "[1 << /S /A >>]"));

        Assert.Equal(new long[] { 0, 3, 4 }, LabelKeys(merged));
    }

    /// <summary>A source's keys out of order are put in order, and a key it repeats keeps its first range.</summary>
    [Fact]
    public void ASourcesKeysOutOfOrderOrRepeated_AreWrittenInOrderOnceEach()
    {
        using var merged = Merge(Labelled(3, "[2 << /S /r >> 0 << /S /D >> 2 << /S /A >>]"), Labelled(1, "[0 << /S /D >>]"));

        Assert.Equal(new long[] { 0, 2, 3 }, LabelKeys(merged));
        var nums = (PdfArray)merged.ResolveObject(((PdfDictionary)merged.ResolveObject(merged.Catalog.Dictionary[PdfName.PageLabels]))[PdfName.Nums]);
        Assert.Equal("r", ((PdfName)((PdfDictionary)merged.ResolveObject(nums[3]))[PdfName.S]).Value);
    }

    /// <summary>The control: keys within their sources are shifted and kept as they were.</summary>
    [Fact]
    public void PageLabelKeysWithinTheirSources_AreShifted()
    {
        using var merged = Merge(Labelled(3, "[0 << /S /D >> 2 << /S /r >>]"), Labelled(2, "[0 << /S /A >>]"));

        Assert.Equal(new long[] { 0, 2, 3 }, LabelKeys(merged));
        Assert.DoesNotContain("outside", PdfMerger.LastPageLabelWarning ?? "", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the parent tree

    [Fact]
    public void AdversarialParentTreeNextKey_DoesNotWrapTheNextSourcesKeys()
    {
        using var merged = Merge(Tagged("9223372036854775807"), Tagged("1"));

        Assert.Equal(new long[] { 0, 1 }, ParentTreeKeys(merged));
        Assert.Equal(new long[] { 0, 1 }, merged.Pages.Select(p => ((PdfNumber)p.Dictionary[PdfName.StructParents]).LongValue).ToArray());
    }

    /// <summary>
    /// A /ParentTree key past the integer range names nothing a /StructParents can: it is dropped,
    /// said, and does not widen the next source's shift to three billion.
    /// </summary>
    [Fact]
    public void AdversarialParentTreeKeyPastTheRange_IsDropped()
    {
        using var merged = Merge(Tagged("1", nums: "[0 [5 0 R] 3000000000 [5 0 R]]"), Tagged("1"));

        Assert.Equal(1L, ((PdfNumber)merged.Pages[1].Dictionary[PdfName.StructParents]).LongValue);
        Assert.Contains("a key outside 0 to", PdfMerger.LastStructureWarning ?? "", StringComparison.Ordinal);
    }

    /// <summary>The control: a declared next key within range still widens the shift.</summary>
    [Fact]
    public void ADeclaredNextKeyInRange_WidensTheShift()
    {
        using var merged = Merge(Tagged("5"), Tagged("1"));

        Assert.Equal(new long[] { 0, 5 }, ParentTreeKeys(merged));
        Assert.Equal(5L, ((PdfNumber)merged.Pages[1].Dictionary[PdfName.StructParents]).LongValue);
    }

    /// <summary>
    /// A page naming a key past its tree's — /StructParents 3 over a tree holding 0, declared next
    /// key 0 — still has the next source shifted clear of it.
    /// </summary>
    [Fact]
    public void AStructParentsPastTheTreesKeys_WidensTheShift()
    {
        using var merged = Merge(Tagged("0", structParents: 3), Tagged("1"));

        Assert.Equal(4L, ((PdfNumber)merged.Pages[1].Dictionary[PdfName.StructParents]).LongValue);
    }

    /// <summary>Each source's dictionaries are visited once by the shift, not every object of the merge once a source.</summary>
    [Fact]
    public void AdversarialManyTaggedSources_AreShiftedOnceADictionary()
    {
        byte[][] sources = Enumerable.Range(0, 30).Select(_ => Tagged("1")).ToArray();

        PdfMerger.ShiftStructParentsSteps = 0;
        using var merged = Merge(sources);

        int objects = merged.Parser.EnumerateAllObjects().Count();
        Assert.InRange(PdfMerger.ShiftStructParentsSteps, 1, objects);
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (long)i).ToArray(), ParentTreeKeys(merged));
    }

    // ---------------------------------------------------------------- field names

    [Fact]
    public void TheKidsOfAnUntitledParent_AreKeptUnique()
    {
        using var merged = Merge(UntitledParent(), UntitledParent());

        var fields = (PdfArray)merged.ResolveObject(AcroForm(merged)[PdfName.Fields]);
        var names = fields.SelectMany(f => Kids(merged, f)).Select(k => ((PdfString)k[PdfName.T]).GetText()).ToArray();
        Assert.Equal(new[] { "Total", "Total.1" }, names);
    }

    /// <summary>
    /// An untitled node is read once a source: a chain of twenty untitled levels, each listing the
    /// next twice, was 2^20 visits walked once a path.
    /// </summary>
    [Fact]
    public void AdversarialUntitledSubtreeSharedAtEveryLevel_IsReadOnce()
    {
        const int levels = 20;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        };
        for (int i = 0; i < levels; i++)
        {
            int next = 5 + i;
            bodies.Add(i < levels - 1 ? $"<< /Kids [{next} 0 R {next} 0 R] >>" : "<< /FT /Tx /T (Leaf) >>");
        }
        byte[] chain = Assemble(bodies);

        PdfMerger.FieldNameVisits = 0;
        using var merged = Merge(chain, chain);

        Assert.InRange(PdfMerger.FieldNameVisits, levels, 8L * levels);
    }

    // ----------------------------------------------------------------

    private static PdfDictionary AcroForm(PdfDocument doc)
        => (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);

    private static IEnumerable<PdfDictionary> Kids(PdfDocument doc, PdfObject field)
        => ((PdfArray)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(field))[PdfName.Kids]))
           .Select(k => (PdfDictionary)doc.ResolveObject(k));

    private static long[] LabelKeys(PdfDocument doc) => Keys(doc, (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.PageLabels]));

    private static long[] ParentTreeKeys(PdfDocument doc)
    {
        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        return Keys(doc, (PdfDictionary)doc.ResolveObject(root[PdfName.ParentTree]));
    }

    private static long[] Keys(PdfDocument doc, PdfDictionary tree)
    {
        var nums = (PdfArray)doc.ResolveObject(tree[PdfName.Nums]);
        var keys = new List<long>();
        for (int i = 0; i < nums.Count; i += 2) keys.Add(((PdfNumber)nums[i]).LongValue);
        return keys.ToArray();
    }

    // `pages` pages with the page-label /Nums given.
    private static byte[] Labelled(int pages, string nums)
    {
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => (3 + i) + " 0 R"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums " + nums + " >> >>",
            "<< /Type /Pages /Kids [" + kids + "] /Count " + pages + " >>",
        };
        for (int i = 0; i < pages; i++) bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        return Assemble(bodies);
    }

    // One tagged page, /StructParents 0, its parent tree [0 [the paragraph]], the root's
    // /ParentTreeNextKey as given.
    private static byte[] Tagged(string nextKey, int structParents = 0, string nums = "[0 [5 0 R]]") => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /StructParents " + structParents + " >>",
        "<< /Type /StructTreeRoot /K [5 0 R] /ParentTree 6 0 R /ParentTreeNextKey " + nextKey + " >>",
        "<< /Type /StructElem /S /P /P 4 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums " + nums + " >>",
    });

    // A form whose one top-level field has no /T and one text kid, /T (Total), which is its widget.
    private static byte[] UntitledParent() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [5 0 R] >>",
        "<< /Kids [5 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Total) /Parent 4 0 R /Rect [10 10 60 30] /P 3 0 R >>",
    });

    private static PdfDocument Merge(params byte[][] sources)
    {
        using var output = new MemoryStream();
        PdfMerger.Merge(output, sources.Select(s => (Stream)new MemoryStream(s)).ToArray());
        return PdfDocument.Open(new MemoryStream(output.ToArray()));
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
