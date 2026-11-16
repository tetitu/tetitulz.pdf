// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>AutoTagOptions.MergeIntoExistingTree</c> — extending a structure tree instead of
/// superseding it.
/// </summary>
/// <remarks>
/// <para><b>The default supersedes, and every test here has to say which it is asserting.</b>
/// Without the option a document that already has a tree gets a brand new one and the old elements
/// become unreachable; with it, the old root is kept and the new elements are appended. A fixture
/// that did not check <i>which</i> root came back would pass under both.</para>
///
/// <para><b>The interesting document is the half-tagged one</b> — a tree that reaches page 1 and
/// not page 2 — because that is the case the pipeline previously refused outright. Page 1 must come
/// back untouched and page 2 must come back tagged, and the parent tree must define both their
/// keys: one entry it inherited and one it wrote.</para>
/// </remarks>
public class StructureTreeMergeTests
{
    /// <summary>A page the existing tree reaches is left alone; one it does not is tagged.</summary>
    /// <remarks>
    /// <b>Both halves are the claim.</b> "The claimed page was skipped" alone is satisfied by a
    /// pass that skipped everything, and "the other page was tagged" alone by one that ignored the
    /// existing tree — which is precisely the superseding behaviour this option exists to avoid.
    /// </remarks>
    [Fact]
    public void MergingTagsTheUnclaimedPageAndLeavesTheClaimedOne()
    {
        byte[] pdf = HalfTaggedTwoPagePdf();

        AutoTagResult result = Merge(pdf, out byte[] tagged);

        Assert.True(result.MergedIntoExistingTree);
        Assert.Equal(1, result.PagesAlreadyTagged);
        Assert.Contains(result.SkippedPageReasons,
            r => r.IndexOf("already reaches this page", StringComparison.OrdinalIgnoreCase) >= 0);

        // And the other page really was marked: its content now carries an MCID it did not have.
        Assert.True(result.McidCount > 0, "the unclaimed page was not marked");
        Assert.True(result.ContentMarked);
        Assert.NotEmpty(tagged);
    }

    /// <summary>The existing root object survives, rather than being replaced by a new one.</summary>
    /// <remarks>
    /// <b>The object number is the test, not the presence of a root.</b> A superseding pass also
    /// leaves the catalog naming a <c>/StructTreeRoot</c> — a different one — so "there is a tree"
    /// cannot tell the two apart. This asserts the catalog still names object 6, which is the root
    /// the fixture wrote.
    /// </remarks>
    [Fact]
    public void MergingKeepsTheExistingRootObject()
    {
        byte[] pdf = HalfTaggedTwoPagePdf();

        Merge(pdf, out byte[] tagged);

        using var doc = PdfDocument.Open(new MemoryStream(tagged, writable: false));
        PdfObject raw = doc.Catalog.Dictionary[new PdfName("StructTreeRoot")];

        var reference = Assert.IsType<PdfReference>(raw);
        Assert.Equal(ExistingRootObjectNumber, reference.ReferenceObjectNumber);
    }

    /// <summary>
    /// A kept root is referred to at its own generation. The catalog's <c>/StructTreeRoot</c> and
    /// the new top-level element's <c>/P</c> were both written <c>6 0 R</c>, which names no object
    /// when the root is at generation 1 (§7.3.10). A strict reader then found no tree, and an
    /// element with no parent.
    /// </summary>
    /// <remarks>
    /// Read from the appended bytes, since this library's parser resolves by number alone and would
    /// reopen either spelling. <see cref="MergingKeepsTheExistingRootObject"/> is the generation-0
    /// control.
    /// </remarks>
    [Fact]
    public void MergingRefersToAKeptRootAtItsGeneration()
    {
        byte[] pdf = RootAtGenerationOne(HalfTaggedTwoPagePdf());

        AutoTagResult result = Merge(pdf, out byte[] tagged);

        Assert.True(result.MergedIntoExistingTree);
        string appended = Encoding.Latin1.GetString(tagged, pdf.Length, tagged.Length - pdf.Length);
        Assert.Contains("/StructTreeRoot 6 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/StructTreeRoot 6 0 R", appended, StringComparison.Ordinal);
        Assert.Matches(@"/P 6 1 R\b", appended);
        Assert.DoesNotMatch(@"/P 6 0 R\b", appended);
    }

    /// <summary>
    /// <paramref name="pdf"/> with object 6, the root, at generation 1. Its header, its
    /// cross-reference row and every reference to it are rewritten in place at the same length, so
    /// every offset still holds.
    /// </summary>
    private static byte[] RootAtGenerationOne(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);

        Assert.Contains("\n6 0 obj", text, StringComparison.Ordinal);
        text = text.Replace("\n6 0 obj", "\n6 1 obj");

        int table = text.IndexOf("0000000000 65535 f \n", StringComparison.Ordinal);
        int row = table + 20 * ExistingRootObjectNumber;
        Assert.Equal(" 00000 n ", text.Substring(row + 10, 9));
        text = text.Substring(0, row + 10) + " 00001 n " + text.Substring(row + 19);

        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![0-9])6 0 R", "6 1 R");
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>The existing root's other entries survive the merge.</summary>
    /// <remarks>
    /// <b><c>/RoleMap</c> is the one that matters and the one a rebuild loses.</b> It maps a
    /// document's own element names onto the standard types, so a tree that kept its elements and
    /// lost its role map is a tree whose elements no longer mean anything — and nothing about the
    /// element count would say so.
    /// </remarks>
    [Fact]
    public void MergingKeepsTheExistingRootsRoleMap()
    {
        byte[] pdf = HalfTaggedTwoPagePdf();

        Merge(pdf, out byte[] tagged);

        using var doc = PdfDocument.Open(new MemoryStream(tagged, writable: false));
        PdfDictionary root = Root(doc);

        var roleMap = doc.ResolveObject(root[new PdfName("RoleMap")]) as PdfDictionary;
        Assert.NotNull(roleMap);
        Assert.Equal("H1", (roleMap![new PdfName("Chapter")] as PdfName)?.Value);
    }

    /// <summary>The new element is appended to the existing kids rather than replacing them.</summary>
    [Fact]
    public void MergingAppendsToTheExistingKids()
    {
        byte[] pdf = HalfTaggedTwoPagePdf();

        Merge(pdf, out byte[] tagged);

        using var doc = PdfDocument.Open(new MemoryStream(tagged, writable: false));
        var kids = doc.ResolveObject(Root(doc)[new PdfName("K")]) as PdfArray;

        Assert.NotNull(kids);
        Assert.True(kids!.Count >= 2,
            $"expected the original element and the new one, found {kids.Count}");

        // The original element, by object number, is still one of them.
        Assert.Contains(kids.OfType<PdfReference>(),
            r => r.ReferenceObjectNumber == ExistingElementObjectNumber);
    }

    /// <summary>
    /// The merged parent tree defines the existing key and the new one.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the entry that makes the merge a merge.</b> ISO 32000-1 §14.7.4.4 makes a
    /// page's <c>/StructParents</c> its key into the parent tree, so dropping the inherited entry
    /// would leave the untouched page's own <c>/StructParents</c> dangling — a file that was valid
    /// before this pass ran and is not afterwards, which is the failure the whole option exists to
    /// avoid.</para>
    ///
    /// <para>The keys are asserted, not the count: a tree with two entries at the same key would
    /// satisfy a count.</para>
    /// </remarks>
    [Fact]
    public void TheMergedParentTreeCarriesTheInheritedEntryAndTheNewOne()
    {
        byte[] pdf = HalfTaggedTwoPagePdf();

        Merge(pdf, out byte[] tagged);

        using var doc = PdfDocument.Open(new MemoryStream(tagged, writable: false));
        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 10_000;

        Assert.True(PdfNumberTree.TryFlatten(
            Root(doc)[new PdfName("ParentTree")], doc, pairs, ref budget));

        var keys = new HashSet<long>();
        foreach (KeyValuePair<PdfObject, PdfObject> pair in pairs)
            if (PdfNumberTree.TryKey(pair.Key, out long key)) keys.Add(key);

        Assert.Contains(0L, keys);                       // the entry the fixture wrote
        Assert.True(keys.Count >= 2, $"only {keys.Count} key(s) survived the merge");

        // Every page's /StructParents resolves to a key the tree defines -- the property §14.7.4.4
        // requires and the one a dropped entry breaks.
        //
        // The COUNT is asserted first, and that is not belt-and-braces: "every page's key resolves"
        // is vacuously true of a document with no page keys, which is precisely what the
        // supersede path's purge produces -- so without this the loop below passed while the keys
        // it was checking had been stripped.
        List<long> structParents = PageStructParents(doc);
        Assert.Equal(2, structParents.Count);
        Assert.Contains(0L, structParents);               // the claimed page kept its own key

        foreach (long key in structParents)
            Assert.Contains(key, keys);
    }

    /// <summary>Without the option, the existing tree is superseded as before.</summary>
    /// <remarks>
    /// <b>The acceptance control for every assertion above.</b> They are all of the form "the old
    /// thing survived", and a pass that never wrote anything would satisfy them; this is the same
    /// document through the same entry point with the option off, showing the old root does
    /// <i>not</i> survive and the difference is the option.
    /// </remarks>
    [Fact]
    public void WithoutTheOptionTheExistingTreeIsStillSuperseded()
    {
        byte[] pdf = HalfTaggedTwoPagePdf();

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output, new AutoTagOptions());

        Assert.False(result.MergedIntoExistingTree);
        Assert.Equal(0, result.PagesAlreadyTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
        var reference = doc.Catalog.Dictionary[new PdfName("StructTreeRoot")] as PdfReference;

        Assert.NotNull(reference);
        Assert.NotEqual(ExistingRootObjectNumber, reference!.ReferenceObjectNumber);
        Assert.Contains(result.Warnings, w => w.IndexOf("superseded", StringComparison.Ordinal) >= 0);
    }

    /// <summary>Asking to merge a document with no tree writes a fresh one and says so.</summary>
    /// <remarks>
    /// The option says what was wanted; <c>MergedIntoExistingTree</c> says what happened. A caller
    /// setting it across a mixed corpus needs the two to be different questions.
    /// </remarks>
    [Fact]
    public void MergingADocumentWithNoTreeWritesAFreshOne()
    {
        byte[] pdf = UntaggedTwoPagePdf();

        AutoTagResult result = Merge(pdf, out byte[] tagged);

        Assert.False(result.MergedIntoExistingTree);
        Assert.Equal(0, result.PagesAlreadyTagged);
        Assert.True(result.ContentMarked);
        Assert.NotEmpty(tagged);
    }

    /// <summary>
    /// A parent tree that cannot be read to the end refuses the merge rather than truncating it.
    /// </summary>
    /// <remarks>
    /// <b>Refusing is the safe direction and it is not the obvious one.</b> A merge built on a
    /// partial read writes a parent tree missing the entries it could not see, which turns links
    /// this pass merely failed to follow into links that no longer exist — strictly worse than
    /// leaving the document alone. The fixture nests <c>/Kids</c> past the walk's depth cap, which
    /// is a shape a real file can have and a hostile one certainly can.
    /// </remarks>
    [Fact]
    public void AnUnreadableParentTreeRefusesTheMergeRatherThanTruncatingIt()
    {
        byte[] pdf = DeeplyNestedParentTreePdf();

        AutoTagResult result = Merge(pdf, out _);

        Assert.False(result.MergedIntoExistingTree);
        Assert.False(result.ContentMarked);
        Assert.Contains(result.Warnings,
            w => w.IndexOf("could not be read to the end", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// A page whose <c>/StructParents</c> names a key the existing tree does not define is
    /// tagged, not skipped.
    /// </summary>
    /// <remarks>
    /// <para><b>Having a key and being claimed are different things, and the other fixtures cannot
    /// tell them apart.</b> Their unclaimed page carries no <c>/StructParents</c> at all, so
    /// <c>ClaimsPage</c> answers on the "is there a key" guard and never reaches the lookup — which
    /// made "claimed whatever the key" a surviving mutation.</para>
    ///
    /// <para>A dangling key is not exotic: it is what a document looks like after an editor removed
    /// structure elements without renumbering the pages that pointed at them. Treating it as
    /// claimed would leave such a page permanently untaggable.</para>
    /// </remarks>
    [Fact]
    public void APageWhoseKeyTheTreeDoesNotDefineIsTagged()
    {
        byte[] pdf = DanglingKeyTwoPagePdf();

        AutoTagResult result = Merge(pdf, out _);

        Assert.True(result.MergedIntoExistingTree);
        Assert.Equal(1, result.PagesAlreadyTagged);      // page 1 only -- the one key 0 reaches
        Assert.True(result.McidCount > 0, "the page with the dangling key was not tagged");
    }

    /// <summary>
    /// A <c>/StructTreeRoot</c> written inline in the catalog is refused rather than merged.
    /// </summary>
    /// <remarks>
    /// <b>There is no object to modify.</b> Merging appends to the existing root, which means
    /// rewriting it as an indirect object this pass can stage; a direct dictionary in the catalog
    /// has no number to stage it under. Refusing by name beats writing a second, disconnected tree
    /// beside it.
    /// </remarks>
    [Fact]
    public void AnInlineStructTreeRootIsRefusedRatherThanMerged()
    {
        byte[] pdf = InlineStructTreeRootPdf();

        AutoTagResult result = Merge(pdf, out _);

        Assert.False(result.MergedIntoExistingTree);
        Assert.False(result.ContentMarked);
        Assert.Contains(result.Warnings,
            w => w.IndexOf("not an indirect object", StringComparison.Ordinal) >= 0);
    }

    /// <summary>A root whose <c>/K</c> is a single element rather than an array is not dropped.</summary>
    /// <remarks>
    /// <b>§14.7.2 allows either form</b>, and every other fixture here writes an array — so a merge
    /// that assumed one would drop the single element and leave it parented to a root that no
    /// longer names it. An orphan that still claims a parent is worse than a missing element: the
    /// tree validates and the content is unreachable.
    /// </remarks>
    [Fact]
    public void ASingleElementKIsPromotedRatherThanDropped()
    {
        byte[] pdf = SingleElementKindPdf();

        Merge(pdf, out byte[] tagged);

        using var doc = PdfDocument.Open(new MemoryStream(tagged, writable: false));
        var kids = doc.ResolveObject(Root(doc)[new PdfName("K")]) as PdfArray;

        Assert.NotNull(kids);
        Assert.Contains(kids!.OfType<PdfReference>(),
            r => r.ReferenceObjectNumber == ExistingElementObjectNumber);
        Assert.True(kids.Count >= 2, $"the new element was not appended; found {kids.Count}");
    }

    /// <summary>
    /// A <c>/ParentTreeNextKey</c> that understates the keys in use does not cause a collision.
    /// </summary>
    /// <remarks>
    /// <b>The entry is the file's claim about itself and a file may be wrong.</b> Every other
    /// fixture's is truthful, so the defensive maximum never fired and deleting it survived them.
    /// Here the tree uses key 0 while declaring a next key of 0: taking the declaration at face
    /// value hands the same key to the new page, and the untouched page's content is then owned by
    /// an element that knows nothing about it.
    /// </remarks>
    [Fact]
    public void AParentTreeNextKeyThatUnderstatesTheTreeDoesNotCollide()
    {
        byte[] pdf = LyingNextKeyPdf();

        Merge(pdf, out byte[] tagged);

        using var doc = PdfDocument.Open(new MemoryStream(tagged, writable: false));
        var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 10_000;
        Assert.True(PdfNumberTree.TryFlatten(
            Root(doc)[new PdfName("ParentTree")], doc, pairs, ref budget));

        var seen = new List<long>();
        foreach (KeyValuePair<PdfObject, PdfObject> pair in pairs)
            if (PdfNumberTree.TryKey(pair.Key, out long key)) seen.Add(key);

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.True(seen.Count >= 2, $"expected the inherited key and a new one, got {seen.Count}");
    }

    // ================================================================ helpers

    private const int ExistingRootObjectNumber = 6;
    private const int ExistingElementObjectNumber = 7;

    private static AutoTagResult Merge(byte[] pdf, out byte[] tagged)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();

        AutoTagResult result = PdfAccessibility.AutoTag(input, output,
            new AutoTagOptions { MergeIntoExistingTree = true });

        tagged = output.ToArray();
        return result;
    }

    private static PdfDictionary Root(PdfDocument doc)
    {
        var root = doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("StructTreeRoot")])
            as PdfDictionary;
        Assert.NotNull(root);
        return root!;
    }

    /// <summary>Every page's <c>/StructParents</c> value, in page order.</summary>
    private static List<long> PageStructParents(PdfDocument doc)
    {
        var keys = new List<long>();
        foreach (PdfPage pdfPage in doc.Pages)
        {
            PdfDictionary page = pdfPage?.Dictionary;
            if (page == null) continue;
            if (doc.ResolveObject(page[new PdfName("StructParents")]) is PdfNumber n)
                keys.Add(n.LongValue);
        }
        return keys;
    }

    /// <summary>
    /// Two pages. Page 1 carries marked content owned by an existing tree (with a
    /// <c>/RoleMap</c>); page 2 is plain text nothing claims.
    /// </summary>
    private static byte[] HalfTaggedTwoPagePdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 9 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P << /MCID 0 >> BDC BT /F1 10 Tf 72 700 Td (Chapter One) Tj ET EMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1"
            + " /RoleMap << /Chapter /H1 >> >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [7 0 R]] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 10 0 R >>",
        StreamObject("BT /F1 10 Tf 72 700 Td (Revenue grew in every region.) Tj ET\n"),
    });

    /// <summary>
    /// Two pages, both with a <c>/StructParents</c>. The tree defines key 0 and not key 7, so the
    /// second page has a key that reaches nothing.
    /// </summary>
    private static byte[] DanglingKeyTwoPagePdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 9 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P << /MCID 0 >> BDC BT /F1 10 Tf 72 700 Td (Chapter One) Tj ET EMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [7 0 R]] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 7"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 10 0 R >>",
        StreamObject("BT /F1 10 Tf 72 700 Td (Revenue grew in every region.) Tj ET\n"),
    });

    /// <summary>A catalog whose <c>/StructTreeRoot</c> is a direct dictionary.</summary>
    private static byte[] InlineStructTreeRootPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot"
            + " << /Type /StructTreeRoot /K [6 0 R] /ParentTree 7 0 R /ParentTreeNextKey 1 >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P << /MCID 0 >> BDC BT /F1 10 Tf 72 700 Td (Chapter One) Tj ET EMC\n"),
        Helvetica,
        "<< /Type /StructElem /S /P /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [6 0 R]] >>",
    });

    /// <summary>As the half-tagged fixture, but the root's <c>/K</c> is a bare reference.</summary>
    private static byte[] SingleElementKindPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 9 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P << /MCID 0 >> BDC BT /F1 10 Tf 72 700 Td (Chapter One) Tj ET EMC\n"),
        Helvetica,
        // /K as a bare reference rather than [7 0 R] -- the form §14.7.2 also allows.
        "<< /Type /StructTreeRoot /K 7 0 R /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [7 0 R]] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 10 0 R >>",
        StreamObject("BT /F1 10 Tf 72 700 Td (Revenue grew in every region.) Tj ET\n"),
    });

    /// <summary>A tree that uses key 0 while declaring <c>/ParentTreeNextKey 0</c>.</summary>
    private static byte[] LyingNextKeyPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 9 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P << /MCID 0 >> BDC BT /F1 10 Tf 72 700 Td (Chapter One) Tj ET EMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 0 >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [7 0 R]] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 10 0 R >>",
        StreamObject("BT /F1 10 Tf 72 700 Td (Revenue grew in every region.) Tj ET\n"),
    });

    /// <summary>Two plain pages and no structure tree at all.</summary>
    private static byte[] UntaggedTwoPagePdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("BT /F1 10 Tf 72 700 Td (First page.) Tj ET\n"),
        Helvetica,
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
        StreamObject("BT /F1 10 Tf 72 700 Td (Second page.) Tj ET\n"),
    });

    /// <summary>
    /// A tree whose <c>/ParentTree</c> nests <c>/Kids</c> far past the walk's depth cap, so it
    /// cannot be flattened.
    /// </summary>
    private static byte[] DeeplyNestedParentTreePdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0"
                + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject("BT /F1 10 Tf 72 700 Td (Revenue grew in every region.) Tj ET\n"),
            Helvetica,
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        };

        // Object 8 onward: a chain of /Kids nodes 200 deep, ending in a leaf. Far past any
        // reasonable cap, and each node is individually well formed.
        const int depth = 200;
        for (int i = 0; i < depth; i++)
        {
            int next = 9 + i;
            bodies.Add("<< /Kids [" + next.ToString(CultureInfo.InvariantCulture) + " 0 R] >>");
        }
        bodies.Add("<< /Nums [0 [7 0 R]] >>");

        return BuildPdf(bodies);
    }

    private const string Helvetica =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    private static string StreamObject(string content) =>
        "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
        + content + "endstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];

        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ")
          .Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
