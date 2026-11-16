// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// The tagged-PDF primitives in <see cref="PdfDocumentBuilder"/>: marked content in the stream,
/// a <c>/StructTreeRoot</c>, and a <c>/ParentTree</c> that maps each page's MCIDs back to the
/// element owning them.
///
/// <para>Every assertion reopens the produced file and reads the real objects. "The string
/// StructTreeRoot appears in the bytes" would pass against a tree that references the wrong
/// pages, which is precisely the failure mode these are here to catch.</para>
/// </summary>
public class PdfStructureTreeTests
{
    private static byte[] Build(Action<PdfDocumentBuilder> configure)
    {
        var doc = new PdfDocumentBuilder().WithCompression(false);
        configure(doc);
        using var ms = new MemoryStream();
        doc.Build(ms);
        return ms.ToArray();
    }

    private static PdfDictionary Catalog(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return doc.Catalog.Dictionary;
    }

    /// <summary>Reopens the file and walks the structure tree, returning the <c>/S</c> roles in order.</summary>
    private static List<string> StructureRoles(byte[] pdf)
    {
        var roles = new List<string>();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot)) as PdfDictionary;
        if (root == null) return roles;

        void Walk(PdfObject node, int depth)
        {
            if (depth > 32) return;
            var dict = doc.ResolveObject(node) as PdfDictionary;
            if (dict == null) return;
            if (doc.ResolveObject(dict.Get(PdfName.S)) is PdfName s) roles.Add(s.Value);
            var k = doc.ResolveObject(dict.Get(PdfName.K));
            if (k is PdfArray arr) foreach (var kid in arr) Walk(kid, depth + 1);
            else if (k != null) Walk(k, depth + 1);
        }

        var rootK = doc.ResolveObject(root.Get(PdfName.K));
        if (rootK is PdfArray ra) foreach (var kid in ra) Walk(kid, 0);
        else if (rootK != null) Walk(rootK, 0);
        return roles;
    }

    private static string Content(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    /// <summary>
    /// The decoded content streams of every page, concatenated — the only place a content-stream
    /// OPERATOR can legitimately appear.
    ///
    /// <para>Searching the whole file for <c>BDC</c> instead was a real flake, not a theoretical
    /// one. <see cref="PdfDocumentBuilder"/> writes a 16-byte <c>RandomNumberGenerator</c>
    /// <c>/ID</c>, twice, as uppercase hex (<c>b.ToString("X2")</c>) — and <c>B</c>, <c>D</c> and
    /// <c>C</c> are all hex digits, so the literal <c>BDC</c> turns up inside the identifier on
    /// its own. Sixty three-character windows at 1/16³ each is ≈1.5% of runs, or one in
    /// sixty-six. A test that fails for a reason unrelated to its name trains everyone to re-run
    /// rather than read, which is how a real regression gets waved through.</para>
    ///
    /// <para>Note this is strictly the STRONGER assertion as well as the stable one: it reads the
    /// operator where an operator would be, rather than hoping the byte soup never spells it.</para>
    /// </summary>
    private static string PageContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var sb = new StringBuilder();
        foreach (var page in doc.Pages)
        {
            byte[] data = page.GetContentStreamData();
            if (data != null) sb.Append(Encoding.Latin1.GetString(data));
        }
        return sb.ToString();
    }

    // ================================================================ opt-in

    /// <summary>
    /// The compatibility guarantee: a builder whose <c>Structure</c> is never touched writes
    /// exactly what it always wrote. Every existing caller — DocxToPdf included — depends on it.
    /// </summary>
    [Fact]
    public void UntouchedStructure_EmitsNoTaggingAtAll()
    {
        byte[] pdf = Build(d =>
        {
            var p = d.AddPage(200, 200);
            string f = p.AddStandardFont(StandardFonts.Helvetica);
            p.BeginText().SetFont(f, 12).SetTextPosition(10, 100).ShowText("Hello").EndText();
        });

        string raw = Content(pdf);
        Assert.DoesNotContain("/StructTreeRoot", raw);
        Assert.DoesNotContain("/MarkInfo", raw);
        Assert.DoesNotContain("/StructParents", raw);

        // BDC is scoped to the content stream: the three keys above cannot occur inside the
        // random /ID (none of them is a hex string), but "BDC" can and did. See PageContent.
        string content = PageContent(pdf);
        Assert.DoesNotContain("BDC", content);
        Assert.DoesNotContain("EMC", content);
    }

    /// <summary>
    /// A document that could not be tagged must not claim it was. Every conformance checker keys
    /// "does this assert Tagged-PDF conventions?" off <c>/StructTreeRoot</c> and
    /// <c>/MarkInfo /Marked</c> — writing them over an empty tree silences the true finding while
    /// switching on structure checks that then have nothing to examine.
    /// </summary>
    [Fact]
    public void StructureWithNoMarkedContent_WritesNeitherRootNorMarkInfo()
    {
        byte[] pdf = Build(d =>
        {
            d.Structure.Root.AddChild(PdfStructureTypes.P);   // an element, but nothing marked
            var p = d.AddPage(200, 200);
            string f = p.AddStandardFont(StandardFonts.Helvetica);
            p.BeginText().SetFont(f, 12).SetTextPosition(10, 100).ShowText("Hello").EndText();
        });

        string raw = Content(pdf);
        Assert.DoesNotContain("/StructTreeRoot", raw);
        Assert.DoesNotContain("/MarkInfo", raw);
    }

    [Fact]
    public void HollowSubtree_IsPruned()
    {
        // Three nested elements, none reaching content: the whole branch goes, not just the leaf.
        // Testing only the leaf would leave a tree whose every node is empty — the shape a
        // transitive hollow-tag check reports.
        byte[] pdf = Build(d =>
        {
            var outer = d.Structure.Root.AddChild(PdfStructureTypes.Sect);
            var mid = outer.AddChild(PdfStructureTypes.Div);
            mid.AddChild(PdfStructureTypes.P);

            var real = d.Structure.Root.AddChild(PdfStructureTypes.P);
            var page = d.AddPage(200, 200);
            string f = page.AddStandardFont(StandardFonts.Helvetica);
            page.BeginMarkedContent(real);
            page.BeginText().SetFont(f, 12).SetTextPosition(10, 100).ShowText("Hi").EndText();
            page.EndMarkedContent();
        });

        var roles = StructureRoles(pdf);
        Assert.Equal(new[] { PdfStructureTypes.Document, PdfStructureTypes.P }, roles);
        Assert.DoesNotContain(PdfStructureTypes.Sect, roles);
        Assert.DoesNotContain(PdfStructureTypes.Div, roles);
    }

    // ================================================================ a tagged document

    private static byte[] TaggedTwoParagraphs()
        => Build(d =>
        {
            d.Structure.Language = "en-GB";
            var h1 = d.Structure.Root.AddChild(PdfStructureTypes.H1);
            var para = d.Structure.Root.AddChild(PdfStructureTypes.P);

            var page = d.AddPage(300, 300);
            string f = page.AddStandardFont(StandardFonts.Helvetica);

            page.BeginMarkedContent(h1);
            page.BeginText().SetFont(f, 18).SetTextPosition(10, 250).ShowText("Title").EndText();
            page.EndMarkedContent();

            page.BeginMarkedContent(para);
            page.BeginText().SetFont(f, 12).SetTextPosition(10, 200).ShowText("Body").EndText();
            page.EndMarkedContent();
        });

    [Fact]
    public void TaggedDocument_HasTheExpectedRoleSequence()
    {
        Assert.Equal(
            new[] { PdfStructureTypes.Document, PdfStructureTypes.H1, PdfStructureTypes.P },
            StructureRoles(TaggedTwoParagraphs()));
    }

    [Fact]
    public void TaggedDocument_DeclaresMarkedAndLanguage()
    {
        var catalog = Catalog(TaggedTwoParagraphs());

        Assert.NotNull(catalog.Get(PdfName.StructTreeRoot));
        var markInfo = catalog.Get(PdfName.MarkInfo) as PdfDictionary;
        Assert.NotNull(markInfo);
        Assert.True((markInfo.Get(PdfName.Marked) as PdfBoolean)?.Value);
        Assert.Equal("en-GB", (catalog.Get(PdfName.Lang) as PdfString)?.GetText());
    }

    [Fact]
    public void TaggedDocument_EmitsMatchingBdcAndEmc()
    {
        // Scoped to the content stream for the reason on PageContent — these are operator claims,
        // and counting operators across the whole file counts the random /ID too.
        string content = PageContent(TaggedTwoParagraphs());

        Assert.Contains("/H1 <</MCID 0>> BDC", content);
        Assert.Contains("/P <</MCID 1>> BDC", content);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(content, @"\bBDC\b").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(content, @"\bEMC\b").Count);
    }

    [Fact]
    public void ParentTree_MapsEachPageKeyToItsOwningElements()
    {
        byte[] pdf = TaggedTwoParagraphs();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot)) as PdfDictionary;
        var parentTree = doc.ResolveObject(root.Get(PdfName.ParentTree)) as PdfDictionary;
        var nums = doc.ResolveObject(parentTree.Get(PdfName.Nums)) as PdfArray;

        Assert.NotNull(nums);
        Assert.Equal(2, nums.Count);                                  // one key, one array
        Assert.Equal(0, (nums[0] as PdfNumber).IntValue);

        var perMcid = doc.ResolveObject(nums[1]) as PdfArray;
        Assert.Equal(2, perMcid.Count);                               // MCID 0 and MCID 1

        // Index 0 must be the H1 and index 1 the P — the mapping is BY MCID, so a shifted array
        // would attribute the heading's content to the paragraph.
        Assert.Equal(PdfStructureTypes.H1,
            ((doc.ResolveObject(perMcid[0]) as PdfDictionary).Get(PdfName.S) as PdfName)?.Value);
        Assert.Equal(PdfStructureTypes.P,
            ((doc.ResolveObject(perMcid[1]) as PdfDictionary).Get(PdfName.S) as PdfName)?.Value);
    }

    [Fact]
    public void TaggedPage_CarriesItsStructParentsKey()
    {
        byte[] pdf = TaggedTwoParagraphs();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var page = doc.Pages[0];
        Assert.Equal(0, (doc.ResolveObject(page.Dictionary.Get(PdfName.StructParents)) as PdfNumber).IntValue);
    }

    // ================================================================ across pages

    /// <summary>
    /// A paragraph that spans a page break is ONE element owning MCIDs on two pages. Content on
    /// the element's own <c>/Pg</c> is a bare integer; content elsewhere needs an <c>/MCR</c> that
    /// names its page, because a bare integer is interpreted against <c>/Pg</c> and would
    /// silently address the wrong page's content.
    /// </summary>
    [Fact]
    public void ElementSpanningTwoPages_UsesAnMcrForTheSecond()
    {
        byte[] pdf = Build(d =>
        {
            var para = d.Structure.Root.AddChild(PdfStructureTypes.P);

            var first = d.AddPage(200, 200);
            string f1 = first.AddStandardFont(StandardFonts.Helvetica);
            first.BeginMarkedContent(para);
            first.BeginText().SetFont(f1, 12).SetTextPosition(10, 100).ShowText("Part one").EndText();
            first.EndMarkedContent();

            var second = d.AddPage(200, 200);
            string f2 = second.AddStandardFont(StandardFonts.Helvetica);
            second.BeginMarkedContent(para);
            second.BeginText().SetFont(f2, 12).SetTextPosition(10, 100).ShowText("Part two").EndText();
            second.EndMarkedContent();
        });

        // Exactly one paragraph element…
        var roles = StructureRoles(pdf);
        Assert.Equal(1, roles.Count(r => r == PdfStructureTypes.P));

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot)) as PdfDictionary;
        var docEl = doc.ResolveObject((doc.ResolveObject(root.Get(PdfName.K)) as PdfArray)[0]) as PdfDictionary;
        var pEl = doc.ResolveObject((doc.ResolveObject(docEl.Get(PdfName.K)) as PdfArray)[0]) as PdfDictionary;
        var kids = doc.ResolveObject(pEl.Get(PdfName.K)) as PdfArray;

        Assert.Equal(2, kids.Count);
        Assert.IsType<PdfNumber>(doc.ResolveObject(kids[0]));          // own page → bare MCID

        var mcr = doc.ResolveObject(kids[1]) as PdfDictionary;
        Assert.NotNull(mcr);
        Assert.Equal("MCR", (mcr.Get(PdfName.Type) as PdfName)?.Value);
        Assert.NotNull(mcr.Get(PdfName.Pg));                            // …and it names the other page

        // The two /Pg references must differ, or the MCR points back at the first page.
        // ReferenceObjectNumber, not ObjectNumber: the latter is inherited from PdfObject and
        // holds the number of the object CONTAINING the reference, which is 0 for a nested one —
        // so comparing it would compare 0 with 0 and pass no matter what the tree said.
        var ownPg = pEl.Get(PdfName.Pg) as PdfReference;
        var mcrPg = mcr.Get(PdfName.Pg) as PdfReference;
        Assert.NotNull(ownPg);
        Assert.NotNull(mcrPg);
        Assert.NotEqual(ownPg.ReferenceObjectNumber, mcrPg.ReferenceObjectNumber);
    }

    [Fact]
    public void BothPages_GetDistinctStructParentsKeys()
    {
        byte[] pdf = Build(d =>
        {
            var a = d.Structure.Root.AddChild(PdfStructureTypes.P);
            var b = d.Structure.Root.AddChild(PdfStructureTypes.P);
            foreach (var (el, _) in new[] { (a, 0), (b, 1) })
            {
                var page = d.AddPage(200, 200);
                string f = page.AddStandardFont(StandardFonts.Helvetica);
                page.BeginMarkedContent(el);
                page.BeginText().SetFont(f, 12).SetTextPosition(10, 100).ShowText("x").EndText();
                page.EndMarkedContent();
            }
        });

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        int k0 = (doc.ResolveObject(doc.Pages[0].Dictionary.Get(PdfName.StructParents)) as PdfNumber).IntValue;
        int k1 = (doc.ResolveObject(doc.Pages[1].Dictionary.Get(PdfName.StructParents)) as PdfNumber).IntValue;

        Assert.NotEqual(k0, k1);
    }

    // ================================================================ artifacts and attributes

    [Fact]
    public void Artifact_CarriesNoMcidAndBelongsToNoElement()
    {
        byte[] pdf = Build(d =>
        {
            var para = d.Structure.Root.AddChild(PdfStructureTypes.P);
            var page = d.AddPage(200, 200);
            string f = page.AddStandardFont(StandardFonts.Helvetica);

            page.BeginArtifact(PdfArtifactTypes.Pagination, PdfArtifactSubtypes.Header);
            page.BeginText().SetFont(f, 9).SetTextPosition(10, 190).ShowText("RUNNINGHEAD").EndText();
            page.EndMarkedContent();

            page.BeginMarkedContent(para);
            page.BeginText().SetFont(f, 12).SetTextPosition(10, 100).ShowText("Body").EndText();
            page.EndMarkedContent();
        });

        string content = PageContent(pdf);
        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Header>> BDC", content);
        // One MCID only, and it is the paragraph's: an artifact must not consume one. Counted in
        // the content stream, where an MCID is ALLOCATED — over the whole file this would also
        // count the /MCID of every /MCR in the structure tree.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(content, @"/MCID \d+"));
        Assert.Contains("/P <</MCID 0>> BDC", content);
    }

    [Fact]
    public void ElementAttributes_AltAndScope_AreWritten()
    {
        byte[] pdf = Build(d =>
        {
            var figure = d.Structure.Root.AddChild(PdfStructureTypes.Figure);
            figure.Alt = "A bar chart";

            var th = d.Structure.Root.AddChild(PdfStructureTypes.TH);
            var attrs = new PdfDictionary();
            attrs.Set(PdfName.O, new PdfName(PdfStructureTypes.Table));
            attrs.Set(PdfName.Scope, new PdfName("Column"));
            th.Attributes = attrs;

            var page = d.AddPage(200, 200);
            string f = page.AddStandardFont(StandardFonts.Helvetica);
            foreach (var el in new[] { figure, th })
            {
                page.BeginMarkedContent(el);
                page.BeginText().SetFont(f, 12).SetTextPosition(10, 100).ShowText("x").EndText();
                page.EndMarkedContent();
            }
        });

        string raw = Content(pdf);
        Assert.Contains("/Alt (A bar chart)", raw);
        Assert.Contains("/Scope /Column", raw);
    }

    [Fact]
    public void CellElement_IsKeptEvenWithNoContent()
    {
        // An empty cell must still appear, or the table looks ragged to a checker — but it needs
        // an MCID to satisfy the "every element reaches content" rule, which is why the caller
        // marks an empty sequence rather than skipping it.
        byte[] pdf = Build(d =>
        {
            var table = d.Structure.Root.AddChild(PdfStructureTypes.Table);
            var row = table.AddChild(PdfStructureTypes.TR);
            var filled = row.AddChild(PdfStructureTypes.TD);
            var empty = row.AddChild(PdfStructureTypes.TD);

            var page = d.AddPage(200, 200);
            string f = page.AddStandardFont(StandardFonts.Helvetica);

            page.BeginMarkedContent(filled);
            page.BeginText().SetFont(f, 12).SetTextPosition(10, 100).ShowText("x").EndText();
            page.EndMarkedContent();

            page.BeginMarkedContent(empty);      // marked, draws nothing
            page.EndMarkedContent();
        });

        var roles = StructureRoles(pdf);
        Assert.Equal(2, roles.Count(r => r == PdfStructureTypes.TD));
    }

    // ================================================================ guards

    [Fact]
    public void EndMarkedContent_WithoutBegin_Throws()
    {
        var doc = new PdfDocumentBuilder();
        var page = doc.AddPage(100, 100);
        Assert.Throws<InvalidOperationException>(() => page.EndMarkedContent());
    }

    [Fact]
    public void BeginMarkedContent_WithNullElement_Throws()
    {
        var doc = new PdfDocumentBuilder();
        var page = doc.AddPage(100, 100);
        Assert.Throws<ArgumentNullException>(() => page.BeginMarkedContent(null));
    }

    [Fact]
    public void MarkedContentCount_TracksAllocatedMcids()
    {
        var doc = new PdfDocumentBuilder();
        var el = doc.Structure.Root.AddChild(PdfStructureTypes.P);
        var page = doc.AddPage(100, 100);

        Assert.Equal(0, page.MarkedContentCount);
        page.BeginMarkedContent(el); page.EndMarkedContent();
        page.BeginMarkedContent(el); page.EndMarkedContent();
        Assert.Equal(2, page.MarkedContentCount);

        page.BeginArtifact(PdfArtifactTypes.Layout); page.EndMarkedContent();
        Assert.Equal(2, page.MarkedContentCount);   // an artifact consumes no MCID
    }

    [Fact]
    public void RoleWithADelimiter_IsEscapedRatherThanCorruptingTheStream()
    {
        // Adversarial: a caller-supplied role is written into the content stream as a name.
        byte[] pdf = Build(d =>
        {
            var el = d.Structure.Root.AddChild("Bad Name/With(Delims)");
            var page = d.AddPage(100, 100);
            string f = page.AddStandardFont(StandardFonts.Helvetica);
            page.BeginMarkedContent(el);
            page.BeginText().SetFont(f, 12).SetTextPosition(10, 50).ShowText("x").EndText();
            page.EndMarkedContent();
        });

        // The name reaches both the content stream and the structure tree; the corruption this
        // guards against is in the STREAM, so that is where it is read.
        string content = PageContent(pdf);
        Assert.DoesNotContain("/Bad Name/With(Delims) <<", content);
        Assert.Contains("#20", content);          // the space was hex-escaped
    }
}
