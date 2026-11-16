// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Merging carries the logical structure across: one <c>/StructTreeRoot</c>, one
/// <c>/ParentTree</c>, and every source's keys shifted clear of the others.
/// </summary>
/// <remarks>
/// <para><b>Before this, every merge of two tagged documents produced an untagged one.</b> The
/// structure objects were copied like everything else and nothing pointed at them, so the output
/// had no <c>/StructTreeRoot</c> — a caller who had remediated two documents to PDF/UA got back one
/// that failed UA01, with no report of any kind.</para>
///
/// <para><b>The assertions follow the key, not the presence of a tree.</b> A merge that concatenated
/// two <c>/ParentTree</c>s without shifting their keys produces a perfectly well-formed structure
/// tree in which the second document's page owns the first document's elements — valid to every
/// structural check and wrong about what the document says. So the tests read the entry a given
/// page's <c>/StructParents</c> actually names and assert <b>which element</b> it is.</para>
/// </remarks>
public sealed class StructureTreeMergeTests
{
    /// <summary>
    /// A one-page tagged document: catalog, page, a <c>/Document</c> element wrapping one leaf, a
    /// <c>/ParentTree</c> naming that leaf, and a <c>/RoleMap</c>.
    /// </summary>
    /// <param name="tag">The leaf element's <c>/S</c>, so the two sources are distinguishable.</param>
    /// <param name="marked">Whether the catalog declares <c>/MarkInfo /Marked true</c>.</param>
    /// <param name="roleMapFrom">The custom tag the <c>/RoleMap</c> maps.</param>
    /// <param name="roleMapTo">What it maps to.</param>
    /// <param name="declaredNextKey">
    /// What the source claims in <c>/ParentTreeNextKey</c>. Deliberately settable below its real
    /// largest key, which is the case that decides whether the shift is computed from the file's
    /// claim or from its contents.
    /// </param>
    /// <param name="borderedClass">
    /// When set, the root's <c>/ClassMap</c> maps <c>/Bordered</c> to object 10, this attribute
    /// object — held indirectly, as producers write a shared one.
    /// </param>
    /// <param name="elementId">
    /// When set, the leaf element carries it as its <c>/ID</c> and the root's <c>/IDTree</c> names the
    /// element under it (Table 322).
    /// </param>
    /// <param name="namespaceUri">
    /// When set, a namespace dictionary (Table 355) with this <c>/NS</c> is listed in the root's
    /// <c>/Namespaces</c> and named by the leaf element's <c>/NS</c>.
    /// </param>
    private static byte[] TaggedDocument(
        string tag, bool marked = true,
        string roleMapFrom = "Doc", string roleMapTo = "Document",
        int declaredNextKey = 1, bool rootKidsAsArray = false, string borderedClass = null,
        string elementId = null, string namespaceUri = null)
    {
        const string content =
            "BT /F1 12 Tf 72 700 Td /P << /MCID 0 >> BDC (tagged) Tj EMC ET\n";
        int namespaceNum = borderedClass != null ? 11 : 10;

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" +
            (marked ? " /MarkInfo << /Marked true >>" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> /StructParents 0 >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K " + (rootKidsAsArray ? "[7 0 R]" : "7 0 R") + " /ParentTree 8 0 R " +
            $"/ParentTreeNextKey {declaredNextKey} /RoleMap << /{roleMapFrom} /{roleMapTo} >>" +
            (borderedClass != null ? " /ClassMap << /Bordered 10 0 R >>" : "") +
            (elementId != null ? $" /IDTree << /Names [({elementId}) 9 0 R] >>" : "") +
            (namespaceUri != null ? $" /Namespaces [{namespaceNum} 0 R]" : "") + " >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [9 0 R] >>",
            "<< /Nums [0 [9 0 R]] >>",
            $"<< /Type /StructElem /S /{tag} /P 7 0 R /Pg 3 0 R /K 0" +
            (elementId != null ? $" /ID ({elementId})" : "") +
            (namespaceUri != null ? $" /NS {namespaceNum} 0 R" : "") + " >>",
        };
        if (borderedClass != null) bodies.Add(borderedClass);
        if (namespaceUri != null) bodies.Add($"<< /Type /Namespace /NS ({namespaceUri}) >>");

        return Assemble(bodies);
    }

    /// <summary>A one-page document with no structure at all.</summary>
    private static byte[] UntaggedDocument()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (plain) Tj ET\n";
        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        });
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Merge(params byte[][] inputs)
    {
        var streams = new List<Stream>();
        foreach (byte[] pdf in inputs) streams.Add(new MemoryStream(pdf, writable: false));
        try
        {
            using var output = new MemoryStream();
            PdfMerger.Merge(output, streams.ToArray());
            return output.ToArray();
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    /// <summary>The structure element a given page's <c>/StructParents</c> resolves to.</summary>
    /// <remarks>
    /// This is the whole point of the merge, expressed as one lookup: take the page's key, find that
    /// key in the merged <c>/ParentTree</c>, and read the element it names. A merge that shifts the
    /// tree but not the pages — or the pages but not the tree — fails here and passes every
    /// structural check.
    /// </remarks>
    private static PdfDictionary ElementOwning(PdfDocument doc, int pageIndex)
    {
        PdfDictionary page = doc.Pages[pageIndex].Dictionary;
        long key = ((PdfNumber)doc.ResolveObject(page[PdfName.StructParents])).LongValue;

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var tree = (PdfDictionary)doc.ResolveObject(root[PdfName.ParentTree]);
        var nums = (PdfArray)doc.ResolveObject(tree[PdfName.Nums]);

        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            if (!(doc.ResolveObject(nums[i]) is PdfNumber n) || n.LongValue != key) continue;

            PdfObject value = doc.ResolveObject(nums[i + 1]);
            // §14.7.4.4: a page's entry is an ARRAY of elements, one per MCID; an annotation's is a
            // single element. This fixture has one marked-content sequence per page.
            if (value is PdfArray array) value = doc.ResolveObject(array[0]);
            return value as PdfDictionary;
        }

        return null;
    }

    private static string TagOf(PdfDictionary element)
        => (element?[PdfName.S] as PdfName)?.Value;

    // ================================================================ the carry

    /// <summary>
    /// Each merged page still owns its own document's structure element — which is the claim a
    /// concatenated, unshifted <c>/ParentTree</c> would break while staying well-formed.
    /// </summary>
    [Fact]
    public void EachPageStillOwnsItsOwnDocumentsElement()
    {
        byte[] merged = Merge(TaggedDocument("H1"), TaggedDocument("Note"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal("H1", TagOf(ElementOwning(doc, 0)));
        Assert.Equal("Note", TagOf(ElementOwning(doc, 1)));
    }

    /// <summary>
    /// The second source's page key was shifted past the first source's, and the merged root's
    /// <c>/ParentTreeNextKey</c> covers both.
    /// </summary>
    [Fact]
    public void TheSecondSourcesKeysAreShiftedClearOfTheFirst()
    {
        byte[] merged = Merge(TaggedDocument("H1"), TaggedDocument("Note"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        long first = ((PdfNumber)doc.ResolveObject(
            doc.Pages[0].Dictionary[PdfName.StructParents])).LongValue;
        long second = ((PdfNumber)doc.ResolveObject(
            doc.Pages[1].Dictionary[PdfName.StructParents])).LongValue;

        Assert.Equal(0, first);
        Assert.Equal(1, second);

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        Assert.Equal(2, ((PdfNumber)doc.ResolveObject(root[PdfName.ParentTreeNextKey])).LongValue);
    }

    /// <summary>
    /// Both sources' top-level elements hang off the merged root, and each names it as its
    /// <c>/P</c>.
    /// </summary>
    /// <remarks>
    /// Leaving <c>/P</c> pointing at the source's own root would leave the tree walkable downward
    /// and broken upward — §14.7.2 Table 323 makes <c>/P</c> required, and a checker that climbs
    /// from an element would step out of the merged tree into a dictionary nothing references.
    /// </remarks>
    [Fact]
    public void TopLevelElementsAreReParentedOntoTheMergedRoot()
    {
        byte[] merged = Merge(TaggedDocument("H1"), TaggedDocument("Note"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var rootRef = (PdfReference)doc.Catalog.Dictionary[PdfName.StructTreeRoot];
        var root = (PdfDictionary)doc.ResolveObject(rootRef);
        var kids = (PdfArray)doc.ResolveObject(root[PdfName.K]);

        Assert.Equal(2, kids.Count);
        for (int i = 0; i < kids.Count; i++)
        {
            var element = (PdfDictionary)doc.ResolveObject(kids[i]);
            Assert.Equal("Document", TagOf(element));
            Assert.Equal(rootRef.ReferenceObjectNumber,
                ((PdfReference)element[PdfName.P]).ReferenceObjectNumber);
        }
    }

    /// <summary>
    /// The same when a source root's <c>/K</c> is an array of elements, the other form §14.7.2
    /// allows: each element is carried over once and re-parented.
    /// </summary>
    [Fact]
    public void ARootWhoseKidsAreAnArrayIsMergedTheSameWay()
    {
        byte[] merged = Merge(TaggedDocument("H1", rootKidsAsArray: true),
                              TaggedDocument("Note", rootKidsAsArray: true));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var kids = (PdfArray)doc.ResolveObject(root[PdfName.K]);
        Assert.Equal(2, kids.Count);
        Assert.Equal("H1", TagOf(ElementOwning(doc, 0)));
        Assert.Equal("Note", TagOf(ElementOwning(doc, 1)));
    }

    /// <summary>Both sources' role maps survive the merge.</summary>
    [Fact]
    public void RoleMapsAreUnioned()
    {
        byte[] merged = Merge(
            TaggedDocument("H1", roleMapFrom: "Alpha", roleMapTo: "H1"),
            TaggedDocument("Note", roleMapFrom: "Beta", roleMapTo: "Note"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var roleMap = (PdfDictionary)doc.ResolveObject(root[PdfName.RoleMap]);

        Assert.Equal("H1", (roleMap[new PdfName("Alpha")] as PdfName)?.Value);
        Assert.Equal("Note", (roleMap[new PdfName("Beta")] as PdfName)?.Value);
        Assert.Null(PdfMerger.LastStructureWarning);
    }

    /// <summary>
    /// Two sources mapping the same tag differently keep the first mapping and say so.
    /// </summary>
    /// <remarks>
    /// The losing document's elements still carry the tag, so taking the later mapping silently
    /// would change what those elements mean — the same reasoning <c>MergeDefaultResources</c>
    /// records for a colliding <c>/DR</c> key.
    /// </remarks>
    [Fact]
    public void AConflictingRoleMappingKeepsTheFirstAndReportsIt()
    {
        byte[] merged = Merge(
            TaggedDocument("H1", roleMapFrom: "Same", roleMapTo: "H1"),
            TaggedDocument("Note", roleMapFrom: "Same", roleMapTo: "Note"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var roleMap = (PdfDictionary)doc.ResolveObject(root[PdfName.RoleMap]);

        Assert.Equal("H1", (roleMap[new PdfName("Same")] as PdfName)?.Value);
        Assert.NotNull(PdfMerger.LastStructureWarning);
        Assert.Contains("RoleMap", PdfMerger.LastStructureWarning);
    }

    /// <summary>
    /// A <c>/ClassMap</c> attribute object held indirectly is carried with its source: the merged
    /// map's <c>/Bordered</c> is that attribute object. It was written with the source's number,
    /// which in the renumbered file names some other object.
    /// </summary>
    [Fact]
    public void AClassMapsIndirectAttributeObjectIsCarriedWithIt()
    {
        byte[] merged = Merge(
            TaggedDocument("H1", borderedClass: "<< /O /Layout /BorderStyle /Solid >>"),
            TaggedDocument("Note"));

        using var doc = PdfDocument.Open(new MemoryStream(merged, writable: false));
        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var classMap = (PdfDictionary)doc.ResolveObject(root[PdfName.ClassMap]);
        var bordered = Assert.IsType<PdfDictionary>(doc.ResolveObject(classMap[new PdfName("Bordered")]));

        Assert.Equal("Solid", (bordered[new PdfName("BorderStyle")] as PdfName)?.Value);
        Assert.Null(PdfMerger.LastStructureWarning);
    }

    /// <summary>
    /// Two inputs whose <c>/ClassMap</c> entries are references with the same number but different
    /// attributes conflict, and are reported; the same attributes are not. The test compared the
    /// text of the two references, which each document numbers its own way.
    /// </summary>
    [Theory]
    [InlineData("Dashed", true)]
    [InlineData("Solid", false)]
    public void ClassMapConflictsAreJudgedOnWhatTheEntriesSay(string secondStyle, bool conflict)
    {
        byte[] merged = Merge(
            TaggedDocument("H1", borderedClass: "<< /O /Layout /BorderStyle /Solid >>"),
            TaggedDocument("Note", borderedClass: $"<< /O /Layout /BorderStyle /{secondStyle} >>"));
        using var doc = PdfDocument.Open(new MemoryStream(merged, writable: false));
        Assert.Equal(2, doc.Pages.Count);

        if (conflict) Assert.Contains("ClassMap", PdfMerger.LastStructureWarning ?? "", StringComparison.Ordinal);
        else Assert.Null(PdfMerger.LastStructureWarning);
    }

    // ================================================================ honesty

    /// <summary>
    /// <c>/MarkInfo /Marked</c> is true only when every source was a marked, tagged document.
    /// </summary>
    /// <remarks>
    /// <c>/Marked true</c> claims the whole document's content is marked, which is false the moment
    /// one source contributed unmarked pages. The structure that does exist is still carried across,
    /// so the caller loses nothing by the claim being withheld — and
    /// <see cref="PdfMerger.LastStructureWarning"/> says why, because a false <c>/Marked</c> is
    /// invisible in the output.
    /// </remarks>
    [Fact]
    public void MarkedIsClaimedOnlyWhenEverySourceWasTagged()
    {
        byte[] bothTagged = Merge(TaggedDocument("H1"), TaggedDocument("Note"));
        using (var ms = new MemoryStream(bothTagged, writable: false))
        using (var doc = PdfDocument.Open(ms))
        {
            var markInfo = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.MarkInfo]);
            Assert.True(((PdfBoolean)doc.ResolveObject(markInfo[PdfName.Marked])).Value);
        }

        byte[] mixed = Merge(TaggedDocument("H1"), UntaggedDocument());
        using (var ms = new MemoryStream(mixed, writable: false))
        using (var doc = PdfDocument.Open(ms))
        {
            var markInfo = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.MarkInfo]);
            Assert.False(((PdfBoolean)doc.ResolveObject(markInfo[PdfName.Marked])).Value);

            // The structure still travelled, which is the half a caller would otherwise lose.
            Assert.Equal("H1", TagOf(ElementOwning(doc, 0)));
        }

        Assert.NotNull(PdfMerger.LastStructureWarning);
        Assert.Contains("Marked", PdfMerger.LastStructureWarning);
    }

    /// <summary>
    /// Merging untagged documents writes no structure tree, and does not claim one.
    /// </summary>
    /// <remarks>
    /// An empty <c>/StructTreeRoot</c> would be worse than none: it satisfies a checker looking for
    /// the key while reaching no content, which is exactly the hollow-tree case the accessibility
    /// checkers have a code for.
    /// </remarks>
    [Fact]
    public void MergingUntaggedDocumentsWritesNoStructureTree()
    {
        byte[] merged = Merge(UntaggedDocument(), UntaggedDocument());

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        Assert.Null(doc.Catalog.Dictionary[PdfName.MarkInfo]);
    }

    /// <summary>
    /// A source that under-declares <c>/ParentTreeNextKey</c> still gets a shift wide enough for its
    /// real keys.
    /// </summary>
    /// <remarks>
    /// <b>Trusting the declaration alone silently overwrites entries.</b> A file whose
    /// <c>/ParentTreeNextKey</c> is 0 while its tree holds key 0 would shift the next document by
    /// nothing, so both documents' page 0 would claim key 0 and the later write wins — and the
    /// result is a well-formed tree in which one document's page owns the other's elements. The
    /// span therefore comes from the larger of the declaration and the tree's own largest key.
    /// </remarks>
    [Fact]
    public void AnUnderDeclaredNextKeyDoesNotCollapseTheShift()
    {
        byte[] merged = Merge(
            TaggedDocument("H1", declaredNextKey: 0),   // claims no keys; actually holds key 0
            TaggedDocument("Note"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal("H1", TagOf(ElementOwning(doc, 0)));
        Assert.Equal("Note", TagOf(ElementOwning(doc, 1)));
    }

    /// <summary>
    /// Every source's <c>/IDTree</c> is carried into one tree on the merged root (Table 322 requires it
    /// once any element has an <c>/ID</c>): each identifier resolves to the element that carries it,
    /// in §7.9.6's byte order, and a source with no identifiers adds nothing.
    /// </summary>
    [Fact]
    public void ElementIdentifiersAreMergedIntoOneIdTree()
    {
        byte[] merged = Merge(
            TaggedDocument("TH", elementId: "th1"),
            TaggedDocument("Note"),
            TaggedDocument("Figure", elementId: "fig1"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var names = (PdfArray)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(root[PdfName.IDTree]))[PdfName.Names]);
        Assert.Equal(4, names.Count);
        Assert.Equal("fig1", ((PdfString)doc.ResolveObject(names[0])).GetText());
        Assert.Equal("th1", ((PdfString)doc.ResolveObject(names[2])).GetText());
        var figure = (PdfDictionary)doc.ResolveObject(names[1]);
        var header = (PdfDictionary)doc.ResolveObject(names[3]);
        Assert.Equal("Figure", TagOf(figure));
        Assert.Equal("fig1", ((PdfString)figure[PdfName.ID]).GetText());
        Assert.Equal("TH", TagOf(header));
        Assert.Equal("th1", ((PdfString)header[PdfName.ID]).GetText());
        Assert.Null(PdfMerger.LastStructureWarning);
    }

    /// <summary>
    /// An identifier two sources both declare names the first source's element — the earlier element
    /// keeps its name, as a colliding role mapping keeps its earlier meaning — and the merge says so.
    /// </summary>
    [Fact]
    public void ARepeatedElementIdentifierKeepsTheFirstElementAndIsReported()
    {
        byte[] merged = Merge(
            TaggedDocument("TH", elementId: "same"),
            TaggedDocument("TD", elementId: "same"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var names = (PdfArray)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(root[PdfName.IDTree]))[PdfName.Names]);
        Assert.Equal(2, names.Count);
        Assert.Equal("TH", TagOf((PdfDictionary)doc.ResolveObject(names[1])));
        Assert.Contains("/IDTree", PdfMerger.LastStructureWarning, StringComparison.Ordinal);
        Assert.Contains("input 1", PdfMerger.LastStructureWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every source's PDF 2.0 <c>/Namespaces</c> entry (Table 354) stays listed on the merged root,
    /// since each source's elements still name their own namespace dictionary; a source with none
    /// adds nothing, and the key is absent when no source had one.
    /// </summary>
    [Fact]
    public void NamespacesAreUnionedOntoTheMergedRoot()
    {
        byte[] merged = Merge(
            TaggedDocument("H1", namespaceUri: "http://iso.org/pdf2/ssn"),
            TaggedDocument("Note"),
            TaggedDocument("Chart", namespaceUri: "http://example.org/charts"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        var namespaces = (PdfArray)doc.ResolveObject(root[PdfName.Namespaces]);
        Assert.Equal(2, namespaces.Count);
        Assert.Equal("http://iso.org/pdf2/ssn", ((PdfString)((PdfDictionary)doc.ResolveObject(namespaces[0]))[PdfName.NS]).GetText());
        Assert.Equal("http://example.org/charts", ((PdfString)((PdfDictionary)doc.ResolveObject(namespaces[1]))[PdfName.NS]).GetText());
        // Each element's own /NS names the dictionary the root lists.
        var chart = ElementOwning(doc, 2);
        Assert.Equal("Chart", TagOf(chart));
        Assert.Equal("http://example.org/charts", ((PdfString)((PdfDictionary)doc.ResolveObject(chart[PdfName.NS]))[PdfName.NS]).GetText());

        byte[] plain = Merge(TaggedDocument("H1"), TaggedDocument("Note"));
        using var plainDoc = PdfDocument.Open(new MemoryStream(plain, writable: false));
        var plainRoot = (PdfDictionary)plainDoc.ResolveObject(plainDoc.Catalog.Dictionary[PdfName.StructTreeRoot]);
        Assert.Null(plainRoot[PdfName.Namespaces]);
        Assert.Null(plainRoot[PdfName.IDTree]);
    }

    /// <summary>Merging one document alone leaves its structure intact and reports nothing.</summary>
    /// <remarks>
    /// The degenerate case is worth pinning because the shift arithmetic runs for it too: an offset
    /// applied to the only source would move its pages away from its own tree.
    /// </remarks>
    [Fact]
    public void ASingleTaggedSourceIsUnchangedInSubstance()
    {
        byte[] merged = Merge(TaggedDocument("H1"));

        using var ms = new MemoryStream(merged, writable: false);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal("H1", TagOf(ElementOwning(doc, 0)));
        Assert.Equal(0, ((PdfNumber)doc.ResolveObject(
            doc.Pages[0].Dictionary[PdfName.StructParents])).LongValue);
        Assert.Null(PdfMerger.LastStructureWarning);
    }
}
