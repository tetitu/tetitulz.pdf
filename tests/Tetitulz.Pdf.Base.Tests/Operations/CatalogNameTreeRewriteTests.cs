// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The two Base writers that rebuild a catalog name tree — attachments and named destinations —
/// now go through one helper, <c>CatalogNameTree</c>. What each got wrong before: the attachment
/// writer replaced the whole <c>/Names</c> dictionary, which dropped every other name tree, and
/// both re-encoded every existing key from its text and sorted by text rather than by the bytes
/// §7.9.6 compares.
/// </summary>
public class CatalogNameTreeRewriteTests
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("payload");

    // ------------------------------------------------------------------ other categories survive

    /// <summary>
    /// Adding an attachment keeps the document's named destinations and document-level scripts.
    /// A new <c>/Names</c> holding only <c>/EmbeddedFiles</c> would lose them.
    /// </summary>
    [Fact]
    public void AddingAnAttachment_KeepsTheOtherNameTrees()
    {
        byte[] pdf = WithNames("/Dests 4 0 R /JavaScript 5 0 R",
            b => b.Obj(4, "<< /Names [(Chapter1) [3 0 R /Fit]] >>")
                  .Obj(5, "<< /Names [(init) 6 0 R] >>")
                  .Obj(6, "<< /S /JavaScript /JS (app.alert\\(1\\)) >>"));

        byte[] written = AddAttachment(pdf, "a.txt");

        Assert.Equal("a.txt", Assert.Single(PdfAttachmentManager.ListAttachments(new MemoryStream(written))).Filename);
        Assert.Equal("Chapter1", Assert.Single(PdfNamedDestinations.ListDestinations(new MemoryStream(written))).Name);
        Assert.Equal(new[] { "init" }, KeyTexts(written, PdfName.JavaScript));
    }

    /// <summary>
    /// Removing an attachment keeps the other name trees, and removing the last one removes the
    /// <c>/EmbeddedFiles</c> entry rather than leaving an empty tree: PDF/A-1 forbids the entry
    /// itself.
    /// </summary>
    [Fact]
    public void RemovingTheLastAttachment_KeepsTheOtherTreesAndDropsTheCategory()
    {
        byte[] pdf = WithNames("/Dests 4 0 R /EmbeddedFiles 5 0 R",
            b => b.Obj(4, "<< /Names [(Chapter1) [3 0 R /Fit]] >>")
                  .Obj(5, "<< /Names [(x.txt) 6 0 R] >>")
                  .Obj(6, "<< /Type /Filespec /F (x.txt) >>"));
        Assert.NotNull(NameDictionary(pdf)[PdfName.EmbeddedFiles]);

        using var output = new MemoryStream();
        PdfAttachmentManager.RemoveAttachment(new MemoryStream(pdf), output, "x.txt");
        byte[] written = output.ToArray();

        Assert.Empty(PdfAttachmentManager.ListAttachments(new MemoryStream(written)));
        Assert.Null(NameDictionary(written)[PdfName.EmbeddedFiles]);
        Assert.Equal("Chapter1", Assert.Single(PdfNamedDestinations.ListDestinations(new MemoryStream(written))).Name);
    }

    [Fact]
    public void SettingADestination_KeepsTheOtherNameTrees()
    {
        byte[] pdf = WithNames("/EmbeddedFiles 4 0 R /JavaScript 5 0 R",
            b => b.Obj(4, "<< /Names [(x.txt) 6 0 R] >>")
                  .Obj(5, "<< /Names [(init) 7 0 R] >>")
                  .Obj(6, "<< /Type /Filespec /F (x.txt) >>")
                  .Obj(7, "<< /S /JavaScript /JS (1) >>"));

        byte[] written = SetDestination(pdf, "zz");

        Assert.Equal("zz", Assert.Single(PdfNamedDestinations.ListDestinations(new MemoryStream(written))).Name);
        Assert.Equal(new[] { "x.txt" }, KeyTexts(written, PdfName.EmbeddedFiles));
        Assert.Equal(new[] { "init" }, KeyTexts(written, PdfName.JavaScript));
    }

    // ------------------------------------------------------------------ keys keep their bytes

    /// <summary>
    /// A key stored as UTF-16BE whose text is ASCII, and a PDFDocEncoded key holding a bullet
    /// (byte 0x80). Rebuilt from text, the first came back one byte per character and the second
    /// as UTF-16BE, so a <c>/GoTo</c> naming either by its stored bytes stopped matching.
    /// </summary>
    private static byte[] TwoAwkwardDestinationKeys() => WithNames("/Dests 4 0 R",
        b => b.Obj(4, "<< /Names [<FEFF00610062> [3 0 R /Fit] (\\200x) [3 0 R /Fit]] >>"));

    private static readonly byte[] Utf16Ab = { 0xFE, 0xFF, 0x00, 0x61, 0x00, 0x62 };
    private static readonly byte[] BulletX = { 0x80, 0x78 };

    [Fact]
    public void ExistingKeysKeepTheirBytes_WhenADestinationIsSet()
    {
        byte[] written = SetDestination(TwoAwkwardDestinationKeys(), "zz");

        var keys = KeyBytes(written, PdfName.Dests);
        Assert.Contains(keys, k => k.SequenceEqual(Utf16Ab));
        Assert.Contains(keys, k => k.SequenceEqual(BulletX));
        Assert.Contains(keys, k => k.SequenceEqual(Encoding.ASCII.GetBytes("zz")));
        Assert.Equal(3, keys.Count);
    }

    [Fact]
    public void ExistingKeysKeepTheirBytes_WhenAnAttachmentIsAdded()
    {
        byte[] pdf = WithNames("/EmbeddedFiles 4 0 R",
            b => b.Obj(4, "<< /Names [<FEFF00610062> 5 0 R (\\200x) 6 0 R] >>")
                  .Obj(5, "<< /Type /Filespec /F (ab) >>")
                  .Obj(6, "<< /Type /Filespec /F (bx) >>"));

        byte[] written = AddAttachment(pdf, "new.txt");

        var keys = KeyBytes(written, PdfName.EmbeddedFiles);
        Assert.Contains(keys, k => k.SequenceEqual(Utf16Ab));
        Assert.Contains(keys, k => k.SequenceEqual(BulletX));
        Assert.Equal(3, keys.Count);
    }

    /// <summary>
    /// §7.9.6 sorts by bytes, so a UTF-16BE key (which begins 0xFE) comes after every
    /// PDFDocEncoded one. Sorted as text, <c>a</c> came first.
    /// </summary>
    [Fact]
    public void TheLeafIsSortedByKeyBytes_NotByText()
    {
        byte[] pdf = WithNames("/Dests 4 0 R",
            b => b.Obj(4, "<< /Names [(b) [3 0 R /Fit] <FEFF0061> [3 0 R /Fit]] >>"));

        byte[] written = SetDestination(pdf, "c");

        Assert.Equal(new[] { "b", "c", "a" }, KeyTexts(written, PdfName.Dests));
    }

    /// <summary>§7.9.6 keys are strings; a name key is rewritten as a string of its text.</summary>
    [Fact]
    public void ANameKeyIsRewrittenAsAString()
    {
        byte[] pdf = WithNames("/Dests 4 0 R",
            b => b.Obj(4, "<< /Names [/Legacy [3 0 R /Fit]] >>"));

        byte[] written = SetDestination(pdf, "zz");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        var flat = Flatten(doc, PdfName.Dests);
        Assert.All(flat, pair => Assert.IsType<PdfString>(pair.Key));
        Assert.Equal(new[] { "Legacy", "zz" }, flat.Select(p => PdfNameTree.KeyText(p.Key)));
    }

    // ------------------------------------------------------------------ where the tree lives

    /// <summary>
    /// An indirect tree root is superseded in place. The name dictionary and the catalog that
    /// point at it are not rewritten, and the /Kids below it are folded into the one leaf.
    /// </summary>
    [Fact]
    public void AnIndirectTreeRootIsSupersededInPlace()
    {
        byte[] pdf = WithNames("/EmbeddedFiles 4 0 R",
            b => b.Obj(4, "<< /Kids [5 0 R] >>")
                  .Obj(5, "<< /Limits [(x.txt) (x.txt)] /Names [(x.txt) 6 0 R] >>")
                  .Obj(6, "<< /Type /Filespec /F (x.txt) >>"));

        byte[] written = AddAttachment(pdf, "a.txt");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        var names = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]);
        Assert.Equal(4, Assert.IsType<PdfReference>(names[PdfName.EmbeddedFiles]).ReferenceObjectNumber);
        var root = (PdfDictionary)doc.ResolveObject(names[PdfName.EmbeddedFiles]);
        Assert.Null(root[PdfName.Kids]);
        Assert.Equal(new[] { "a.txt", "x.txt" }, KeyTexts(written, PdfName.EmbeddedFiles));

        string appended = Encoding.ASCII.GetString(written, pdf.Length, written.Length - pdf.Length);
        Assert.Matches(new Regex("(?m)^4 0 obj"), appended);
        Assert.DoesNotMatch(new Regex("(?m)^1 0 obj"), appended);
    }

    /// <summary>An indirect name dictionary stays indirect, and is superseded where it lives.</summary>
    [Fact]
    public void AnIndirectNameDictionaryStaysIndirect()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /Names 4 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
            .Obj(4, "<< /Dests << /Names [(Chapter1) [3 0 R /Fit]] >> >>")
            .Finish();

        byte[] written = AddAttachment(pdf, "a.txt");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        Assert.Equal(4, Assert.IsType<PdfReference>(doc.Catalog.Dictionary[PdfName.Names]).ReferenceObjectNumber);
        Assert.Equal(new[] { "a.txt" }, KeyTexts(written, PdfName.EmbeddedFiles));
        Assert.Equal(new[] { "Chapter1" }, KeyTexts(written, PdfName.Dests));
    }

    /// <summary>
    /// A root reference to an object that is not a tree node is not overwritten. A new leaf is
    /// minted instead, since the object it names may be something else entirely.
    /// </summary>
    [Fact]
    public void ARootThatIsNotANodeIsLeftAlone_AndANewLeafIsMinted()
    {
        byte[] pdf = WithNames("/EmbeddedFiles 4 0 R", b => b.Obj(4, "[1 2 3]"));

        byte[] written = AddAttachment(pdf, "a.txt");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        Assert.Equal(3, Assert.IsType<PdfArray>(doc.ResolveObject(new PdfReference(4, 0))).Count);
        Assert.Equal(new[] { "a.txt" }, KeyTexts(written, PdfName.EmbeddedFiles));
    }

    /// <summary>
    /// Duplicate keys, which a malformed tree can hold, keep their stored order. The sort is by
    /// bytes, with ties broken by position, so which duplicate a reader finds first is not decided
    /// by the sort algorithm. Forty entries, because a list this short is insertion-sorted, and
    /// insertion sort would keep the order without the tie-break.
    /// </summary>
    [Fact]
    public void DuplicateKeysKeepTheirStoredOrder()
    {
        var leaf = new StringBuilder("<< /Names [");
        for (int i = 1; i <= 40; i++) leaf.Append("(k) ").Append(i).Append(' ');
        leaf.Append("] >>");
        byte[] pdf = WithNames("/Dests 4 0 R", b => b.Obj(4, leaf.ToString()));

        byte[] written = SetDestination(pdf, "zz");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        var values = Flatten(doc, PdfName.Dests)
            .Where(p => PdfNameTree.KeyText(p.Key) == "k")
            .Select(p => ((PdfNumber)p.Value).IntValue);
        Assert.Equal(Enumerable.Range(1, 40), values);
    }

    /// <summary>
    /// The helper's own contract: writing no entries for a category the document does not have
    /// changes nothing. It adds no objects and asks for no catalog.
    /// </summary>
    [Fact]
    public void WritingNoEntriesForAnAbsentCategoryChangesNothing()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Finish();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var objects = new List<Tetitulz.Pdf.Core.Writing.IndirectObject>();
        int next = 3;

        var catalog = CatalogNameTree.Write(doc, doc.Catalog.Dictionary, null, PdfName.Dests,
            new List<CatalogNameTree.Entry>(), objects, ref next);

        Assert.Null(catalog);
        Assert.Empty(objects);
        Assert.Equal(3, next);
    }

    // ------------------------------------------------------------------ a partial read refuses

    private static int TooDeep => PdfLimits.MaxTreeDepth + 20;

    /// <summary>
    /// An <c>/EmbeddedFiles</c> tree deeper than the walk's depth limit is refused, with nothing
    /// written, rather than the part read rewritten and the rest deleted.
    /// </summary>
    [Fact]
    public void AnAttachmentTreeTooDeepToReadIsRefused()
    {
        using var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfAttachmentManager.AddAttachment(new MemoryStream(DeepAttachmentTree(TooDeep)), output,
                "a.txt", Payload));
        Assert.Contains("could not be read in full", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The acceptance control: a readable depth keeps both old entries.</summary>
    [Fact]
    public void AnAttachmentTreeOfReadableDepthIsRewrittenWhole()
    {
        byte[] written = AddAttachment(DeepAttachmentTree(3), "a.txt");

        Assert.Equal(new[] { "a.txt", "deep.txt", "shallow.txt" }, KeyTexts(written, PdfName.EmbeddedFiles));
    }

    /// <summary>
    /// Objects: 1 catalog, 2 pages, 3 page, 4 the <c>/EmbeddedFiles</c> root, 5 and 6 the two
    /// file specifications, 7 the shallow leaf, then a /Kids chain <paramref name="depth"/> nodes
    /// long ending in the deep leaf.
    /// </summary>
    internal static byte[] DeepAttachmentTree(int depth)
    {
        const int FirstChainNode = 8;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Kids [7 0 R " + FirstChainNode + " 0 R] >>",
            "<< /Type /Filespec /F (shallow.txt) >>",
            "<< /Type /Filespec /F (deep.txt) >>",
            "<< /Names [(shallow.txt) 5 0 R] >>",
        };
        for (int i = 0; i < depth - 1; i++)
            bodies.Add("<< /Kids [" + (FirstChainNode + i + 1) + " 0 R] >>");
        bodies.Add("<< /Names [(deep.txt) 6 0 R] >>");
        return FormPdfFixtures.Build(bodies.ToArray());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One page (object 3) and a catalog whose <c>/Names</c> is <paramref name="names"/>.</summary>
    private static byte[] WithNames(string names, Func<PdfFixtureBuilder, PdfFixtureBuilder> objects)
        => objects(new PdfFixtureBuilder().Header()
                .Obj(1, "<< /Type /Catalog /Pages 2 0 R /Names << " + names + " >> >>")
                .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
                .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>"))
           .Finish();

    private static byte[] AddAttachment(byte[] pdf, string name)
    {
        using var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(new MemoryStream(pdf), output, name, Payload);
        return output.ToArray();
    }

    private static byte[] SetDestination(byte[] pdf, string name)
    {
        using var output = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(pdf), output, name, 0);
        return output.ToArray();
    }

    private static PdfDictionary NameDictionary(byte[] pdf)
    {
        var doc = PdfDocument.Open(new MemoryStream(pdf));
        return (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]);
    }

    private static List<KeyValuePair<PdfObject, PdfObject>> Flatten(PdfDocument doc, PdfName category)
    {
        var names = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]);
        var flat = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = PdfLimits.MaxNameTreeEntries;
        Assert.True(PdfNameTree.TryFlatten(names[category], doc, flat, ref budget));
        return flat;
    }

    /// <summary>The category's keys as text, in the order the tree stores them.</summary>
    private static string[] KeyTexts(byte[] pdf, PdfName category)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Flatten(doc, category).Select(p => PdfNameTree.KeyText(p.Key)).ToArray();
    }

    /// <summary>The category's keys as their stored bytes.</summary>
    private static List<byte[]> KeyBytes(byte[] pdf, PdfName category)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Flatten(doc, category).Select(p => ((PdfString)p.Key).RawBytes).ToList();
    }
}
