// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Completing a page that already carries marked content, instead of refusing it.
/// </summary>
/// <remarks>
/// <para><b>The refusal was a boolean and the fear behind it was real.</b> A page carrying any
/// <c>BDC</c>/<c>BMC</c>/<c>EMC</c> was skipped, because splicing a second set of MCIDs on top of
/// an existing one would produce duplicate identifiers and a <c>/ParentTree</c> resolving content
/// to the wrong element. The conclusion was one step short: <b>the identifiers only collide if you
/// start counting at zero.</b></para>
///
/// <para><b>Two things make completion safe, and both are asserted here.</b> The page's existing
/// sequences have to be readable exactly — <c>MarkedContentInventory</c> refuses nesting, an
/// unbalanced <c>EMC</c>, a duplicate MCID, an unresolvable property list and a non-artifact
/// sequence with no MCID — and the document's existing structure tree must claim none of them, or
/// the adopted content would end up with two owners.</para>
///
/// <para><b>An adopted sequence keeps its producer's tag.</b> §14.6.1 makes a marked-content tag
/// the structure type the producer intended, so the page has already answered the question the
/// classifier exists to guess at; re-classifying by font size would overrule a <c>/Figure</c> or a
/// <c>/TD</c> — types this tagger cannot infer at all — with a <c>/P</c>.</para>
/// </remarks>
public class PartialTaggingCompletionTests
{
    // =====================================================================
    //  Completion
    // =====================================================================

    /// <summary>
    /// A page whose marked content is readable is completed, not skipped.
    /// </summary>
    [Fact]
    public void AutoTag_PageWithReadableMarkedContent_IsCompletedRatherThanSkipped()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(HalfMarkedPagePdf());

        Assert.Equal(0, result.PagesSkipped);
        Assert.True(result.ContentMarked, string.Join(" | ", result.SkippedPageReasons));

        // Both the sequence the producer wrote and the one this pass added are owned.
        Assert.Equal(new[] { 0, 1 }, ClaimedMcids(tagged).OrderBy(m => m).ToArray());
    }

    /// <summary>
    /// The adopted element carries the producer's identifier and the producer's tag.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because getting either wrong is silent.</b> A different identifier makes the
    /// element point at content the page does not have; a different tag replaces what the producer
    /// said with what a font-size heuristic guessed. The fixture's existing sequence is tagged
    /// <c>/H1</c> at body size precisely so the two disagree: the classifier would call it a
    /// paragraph.
    /// </remarks>
    [Fact]
    public void AutoTag_AdoptedSequence_KeepsItsProducersIdentifierAndTag()
    {
        (_, byte[] tagged) = AutoTag(HalfMarkedPagePdf());

        Assert.Equal("H1", RoleOfMcid(tagged, 0));
        Assert.Equal("P", RoleOfMcid(tagged, 1));
    }

    /// <summary>
    /// The existing sequence is left byte-for-byte alone; only the unmarked text gains operators.
    /// </summary>
    /// <remarks>
    /// Counting the operators is what separates "completed" from "re-marked". A pass that wrapped
    /// the already-marked text again would still produce a tree that validates — the outer sequence
    /// would simply own the inner one's content — and the page would carry two identifiers for one
    /// run of glyphs.
    /// </remarks>
    [Fact]
    public void AutoTag_ExistingSequence_IsNotWrappedASecondTime()
    {
        (_, byte[] tagged) = AutoTag(HalfMarkedPagePdf());

        string content = PageContent(tagged, 0);

        Assert.Equal(2, Occurrences(content, "BDC"));
        Assert.Equal(2, Occurrences(content, "EMC"));

        // The producer's own operator survives verbatim, so nothing was rewritten around it.
        Assert.Contains("/H1 << /MCID 0 >> BDC", content, StringComparison.Ordinal);
    }

    /// <summary>An artifact sequence is left alone and gains no element.</summary>
    /// <remarks>
    /// §14.8.2.2 makes artifact content deliberately unreachable from the structure, so it claims
    /// no identifier and needs no element — which is also what makes leaving it exactly where it is
    /// the correct handling rather than the lazy one.
    /// </remarks>
    [Fact]
    public void AutoTag_ArtifactSequence_IsLeftAloneAndClaimsNoElement()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(ArtifactAndUnmarkedPagePdf());

        Assert.True(result.ContentMarked, string.Join(" | ", result.SkippedPageReasons));

        // Only the previously unmarked text is owned, and it starts at 0 because the artifact
        // claimed no identifier for it to count past.
        Assert.Equal(new[] { 0 }, ClaimedMcids(tagged).OrderBy(m => m).ToArray());

        string content = PageContent(tagged, 0);
        Assert.Contains("/Artifact BMC", content, StringComparison.Ordinal);
    }

    /// <summary>A property list named through <c>/Resources /Properties</c> is resolved.</summary>
    /// <remarks>
    /// The scanner cannot answer this on its own — it is handed bytes and no resources — so the
    /// page's <c>/Properties</c> is passed down. Without it every producer that names its property
    /// lists (which is legal and common) would fall into the refusal, and the feature would work
    /// only on the inline spelling.
    /// </remarks>
    [Fact]
    public void AutoTag_NamedPropertyList_IsResolvedThroughThePagesResources()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(NamedPropertyListPagePdf());

        Assert.Equal(0, result.PagesSkipped);
        Assert.Equal("P", RoleOfMcid(tagged, 7));
        Assert.Equal(new[] { 7, 8 }, ClaimedMcids(tagged).OrderBy(m => m).ToArray());
    }

    /// <summary>
    /// Adopted and newly marked runs are emitted in <b>content</b> order, not in identifier order.
    /// </summary>
    /// <remarks>
    /// <para><b>The element order in the tree is the reading order</b>, and the two sources of runs
    /// arrive in the wrong one: the pass walks text objects first, collecting the sequences it
    /// creates, and only then adopts the ones already on the page. On this fixture the adopted
    /// sequence is <i>above</i> the paragraph and its identifier is <i>below</i> it — MCID 0
    /// allocated by the producer, MCID 1 allocated here — so an unsorted list and a list sorted by
    /// identifier give opposite answers, and only one of them is what a reader meets.</para>
    ///
    /// <para>A mutation that reversed the sort survived every other assertion in this file, because
    /// they all ask <i>which</i> identifiers are claimed rather than in what order.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_AdoptedAndNewRuns_AreEmittedInContentOrder()
    {
        (_, byte[] tagged) = AutoTag(HalfMarkedPagePdf());

        Assert.Equal(new[] { 0, 1 }, ClaimedMcids(tagged).ToArray());
    }

    /// <summary>
    /// An artifact sequence that carries an MCID is still not given an element — but its identifier
    /// is still counted as taken.
    /// </summary>
    /// <remarks>
    /// <para><b>Both halves come apart, and a mutation showed it.</b> Skipping a sequence "with no
    /// MCID" and skipping an artifact are the same test on the usual <c>/Artifact BMC</c> spelling,
    /// so deleting the artifact clause changed nothing there. An artifact whose property list
    /// <i>does</i> carry an <c>/MCID</c> separates them: §14.8.2.2 puts artifact content
    /// deliberately outside the structure, so giving it an element would contradict the marking the
    /// producer wrote, while the identifier is nonetheless spent and cannot be handed out
    /// again.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_ArtifactCarryingAnMcid_GetsNoElementButStillSpendsTheIdentifier()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(ArtifactWithMcidPagePdf());

        Assert.True(result.ContentMarked, string.Join(" | ", result.SkippedPageReasons));

        // 0 belongs to the artifact and is not claimed; the paragraph gets the next one.
        Assert.Equal(new[] { 1 }, ClaimedMcids(tagged).ToArray());
        Assert.Equal("P", RoleOfMcid(tagged, 1));
    }

    /// <summary>One page: a pagination artifact that claims MCID 0, and an unmarked paragraph.</summary>
    private static byte[] ArtifactWithMcidPagePdf() => PageWithContent(
        "/Artifact << /MCID 0 /Type /Pagination >> BDC "
        + "BT /F1 9 Tf 72 762 Td (Acme Corporation) Tj ET EMC\n"
        + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region.) Tj ET\n");

    // =====================================================================
    //  Refusals — each names the shape it refuses
    // =====================================================================

    /// <summary>
    /// A shape the inventory cannot read exactly is refused, and the reason says which.
    /// </summary>
    /// <remarks>
    /// <b>Each row is refused by a different clause, and the message is asserted per row</b> so a
    /// single over-broad refusal cannot satisfy the whole theory. That is the rule for a file of
    /// refusals: an input only the named guard can reject, and a message that names it.
    /// </remarks>
    [Theory]
    [InlineData("/P << /MCID 0 >> BDC /Span << /MCID 1 >> BDC BT /F1 10 Tf 72 400 Td (x) Tj ET EMC EMC\n",
                "nests one marked-content sequence inside another")]
    [InlineData("BT /F1 10 Tf 72 400 Td (x) Tj ET EMC\n",
                "closes a marked-content sequence it never opened")]
    [InlineData("/P << /MCID 0 >> BDC BT /F1 10 Tf 72 400 Td (x) Tj ET\n",
                "opens a marked-content sequence it never closes")]
    [InlineData("/P << /MCID 0 >> BDC BT /F1 10 Tf 72 400 Td (x) Tj ET EMC\n"
                + "/P << /MCID 0 >> BDC BT /F1 10 Tf 72 380 Td (y) Tj ET EMC\n",
                "uses MCID 0 twice")]
    [InlineData("/P /Missing BDC BT /F1 10 Tf 72 400 Td (x) Tj ET EMC\n",
                "does not define")]
    [InlineData("/Span BMC BT /F1 10 Tf 72 400 Td (x) Tj ET EMC\n",
                "marks content with no MCID")]
    [InlineData("/Widget << /MCID 0 >> BDC BT /F1 10 Tf 72 400 Td (x) Tj ET EMC\n",
                "not a standard structure type")]
    public void AutoTag_MalformedOrUnreadableMarkedContent_IsRefusedWithAReasonThatNamesIt(
        string pageContent, string expected)
    {
        (AutoTagResult result, _) = AutoTag(PageWithContent(pageContent));

        string reasons = string.Join(" | ", result.SkippedPageReasons);
        Assert.Contains(expected, reasons, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document whose structure tree already claims content still refuses an already-marked page.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the gate that keeps completion from producing two owners for one range.</b>
    /// Adopting a sequence means writing an element that claims its MCID; if an element already
    /// does, the page ends with two. The test is the whole tree rather than the page — conservative
    /// in the safe direction, and free where it matters, because the remediation pipeline only
    /// reaches this pass when the tree names no content at all.</para>
    ///
    /// <para>The control is the same bytes with the tree's link removed, so the refusal is the
    /// gate's and not the fixture's.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_TreeThatAlreadyClaimsContent_StillRefusesAnAlreadyMarkedPage()
    {
        (AutoTagResult claimed, _) = AutoTag(HalfMarkedPagePdf(treeClaimsContent: true));
        Assert.Equal(1, claimed.PagesSkipped);
        Assert.Contains("already contains marked content",
            string.Join(" | ", claimed.SkippedPageReasons), StringComparison.Ordinal);

        (AutoTagResult unclaimed, _) = AutoTag(HalfMarkedPagePdf());
        Assert.Equal(0, unclaimed.PagesSkipped);
    }

    // =====================================================================
    //  A sequence the producer opened inside a text object (Wave 17, R8.1)
    // =====================================================================

    /// <summary>
    /// A producer's sequence that opens <em>inside</em> a text object is neither wrapped around nor
    /// nested into, and the text outside it in the same object is still marked.
    /// </summary>
    /// <remarks>
    /// <para>ISO 32000-2 §14.7.5.1: "A marked-content sequence corresponding to a structure content
    /// item shall not have another marked-content sequence for a structure content item nested
    /// within it." tagpdf and other producers write <c>BDC</c> after <c>BT</c>, so the object's
    /// first byte is outside every sequence and this pass saw an object nobody had marked: it
    /// wrapped the object whole — a structure item around the producer's — or, when the object
    /// split into segments, wrapped the marked line's show operator a second time <em>inside</em>
    /// the producer's sequence. Both passed the balance check, because they balance; both put one
    /// run of glyphs under two MCIDs, which is what the clause forbids.</para>
    /// <para>The depth walk is the assertion that the clause holds; the operator count beside it is
    /// the potency check — a page refused outright also nests nothing.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_SequenceOpenedInsideATextObject_IsNotNestedInsideOrWrappedAround()
    {
        const string content =
            "BT /F1 10 Tf 72 700 Td /H1 << /MCID 0 >> BDC (Quarterly Results) Tj EMC\n"
            + "0 -300 Td (Revenue grew in every region.) Tj ET\n";

        (AutoTagResult result, byte[] tagged) = AutoTag(PageWithContent(content));
        Assert.True(result.ContentMarked, string.Join(" | ", result.SkippedPageReasons));

        string produced = PageContent(tagged, 0);

        // The producer's sequence survives verbatim, with nothing of ours inside it.
        Assert.Contains("/H1 << /MCID 0 >> BDC (Quarterly Results) Tj EMC", produced, StringComparison.Ordinal);

        // One sequence was added, for the line the producer never marked, and no sequence sits
        // inside another.
        Assert.Equal(2, Occurrences(produced, "BDC"));
        Assert.Equal(2, Occurrences(produced, "EMC"));
        Assert.Equal(1, MaxMarkedContentDepth(produced));

        // Both lines are owned, each by the element the page says.
        Assert.Equal(new[] { 0, 1 }, ClaimedMcids(tagged).OrderBy(m => m).ToArray());
        Assert.Equal("H1", RoleOfMcid(tagged, 0));
        Assert.Equal("P", RoleOfMcid(tagged, 1));
    }

    /// <summary>
    /// A line with one show inside the producer's sequence and one outside is left to the producer
    /// whole: half a line can be marked only by nesting into the sequence or interleaving with it.
    /// </summary>
    [Fact]
    public void AutoTag_LineHalfInsideAProducersSequence_IsLeftToTheProducer()
    {
        const string content =
            "BT /F1 10 Tf 72 700 Td /H1 << /MCID 0 >> BDC (Quarterly) Tj EMC ( Results) Tj\n"
            + "0 -300 Td (Revenue grew in every region.) Tj ET\n";

        (AutoTagResult result, byte[] tagged) = AutoTag(PageWithContent(content));
        Assert.True(result.ContentMarked, string.Join(" | ", result.SkippedPageReasons));

        string produced = PageContent(tagged, 0);
        Assert.Contains("BDC (Quarterly) Tj EMC ( Results) Tj", produced, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(produced, "BDC"));
        Assert.Equal(1, MaxMarkedContentDepth(produced));
        Assert.Equal(new[] { 0, 1 }, ClaimedMcids(tagged).OrderBy(m => m).ToArray());
    }

    /// <summary>
    /// An empty producer sequence inside a text object still forbids wrapping the object whole: a
    /// structure item would enclose it (§14.7.5.1), empty or not. Ours opens after it closes, around
    /// the show alone.
    /// </summary>
    [Fact]
    public void AutoTag_EmptySequenceInsideATextObject_StillForbidsTheWholeObjectWrap()
    {
        const string content =
            "BT /F1 10 Tf 72 700 Td /Span << /MCID 0 >> BDC EMC (Revenue grew in every region.) Tj ET\n";

        (AutoTagResult result, byte[] tagged) = AutoTag(PageWithContent(content));
        Assert.True(result.ContentMarked, string.Join(" | ", result.SkippedPageReasons));

        string produced = PageContent(tagged, 0);
        Assert.Equal(2, Occurrences(produced, "BDC"));
        Assert.Equal(1, MaxMarkedContentDepth(produced));
        Assert.Contains(
            "BDC EMC \n/P <</MCID 1>> BDC\n(Revenue grew in every region.) Tj\nEMC\n ET",
            produced, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deepest marked-content nesting in <paramref name="content"/>: <c>BDC</c>/<c>BMC</c>
    /// open, <c>EMC</c> closes. The fixtures here put none of the three inside a string.
    /// </summary>
    private static int MaxMarkedContentDepth(string content)
    {
        int depth = 0, max = 0;
        foreach (string token in content.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "BDC" || token == "BMC") { depth++; if (depth > max) max = depth; }
            else if (token == "EMC") depth--;
        }
        return max;
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    /// <summary>
    /// One page: an <c>/H1</c> sequence the producer wrote, and a paragraph it never marked.
    /// </summary>
    /// <param name="treeClaimsContent">
    /// When true the document carries a structure tree whose element names MCID 0, which is the
    /// state that must switch completion off.
    /// </param>
    private static byte[] HalfMarkedPagePdf(bool treeClaimsContent = false)
    {
        const string content =
            "/H1 << /MCID 0 >> BDC BT /F1 10 Tf 72 700 Td (Quarterly Results) Tj ET EMC\n"
            + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region.) Tj ET\n";

        var bodies = new List<string>
        {
            treeClaimsContent
                ? "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"
                : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(content),
            Helvetica,
        };

        if (treeClaimsContent)
        {
            bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>");
            bodies.Add("<< /Type /StructElem /S /H1 /P 6 0 R /Pg 3 0 R /K 0 >>");
            bodies.Add("<< /Nums [0 [7 0 R]] >>");
        }

        return BuildPdf(bodies);
    }

    /// <summary>One page: an artifact sequence and an unmarked paragraph.</summary>
    private static byte[] ArtifactAndUnmarkedPagePdf() => PageWithContent(
        "/Artifact BMC BT /F1 9 Tf 72 762 Td (Acme Corporation) Tj ET EMC\n"
        + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region.) Tj ET\n");

    /// <summary>
    /// One page whose <c>BDC</c> names its property list through <c>/Resources /Properties</c>,
    /// with MCIDs that do not start at 0 so the continuation is observable.
    /// </summary>
    private static byte[] NamedPropertyListPagePdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
            + "/Resources << /Font << /F1 5 0 R >> /Properties << /Pr7 6 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P /Pr7 BDC BT /F1 10 Tf 72 700 Td (Already marked) Tj ET EMC\n"
                   + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region.) Tj ET\n"),
        Helvetica,
        "<< /MCID 7 >>",
    });

    /// <summary>One untagged page carrying <paramref name="content"/>.</summary>
    private static byte[] PageWithContent(string content) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject(content),
        Helvetica,
    });

    private const string Helvetica =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static (AutoTagResult Result, byte[] Bytes) AutoTag(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(input, output, new AutoTagOptions());
        return (result, output.ToArray());
    }

    /// <summary>The MCIDs some structure element's <c>/K</c> names, read out of the output.</summary>
    private static List<int> ClaimedMcids(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var claimed = new List<int>();

        foreach (PdfDictionary element in Elements(doc))
            foreach (int mcid in McidsOf(doc, element))
                claimed.Add(mcid);

        return claimed;
    }

    /// <summary>The <c>/S</c> of the element claiming <paramref name="mcid"/>, or null.</summary>
    private static string RoleOfMcid(byte[] pdf, int mcid)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        foreach (PdfDictionary element in Elements(doc))
            if (McidsOf(doc, element).Contains(mcid))
                return (doc.ResolveObject(element[new PdfName("S")]) as PdfName)?.Value;

        return null;
    }

    /// <summary>Every structure element in the produced tree, walked breadth-first.</summary>
    private static List<PdfDictionary> Elements(PdfDocument doc)
    {
        var found = new List<PdfDictionary>();
        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;
        if (root == null) return found;

        var queue = new Queue<PdfObject>();
        queue.Enqueue(root[new PdfName("K")]);

        var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            PdfObject resolved = doc.ResolveObject(queue.Dequeue());
            if (resolved is PdfArray array)
            {
                foreach (PdfObject item in array) queue.Enqueue(item);
                continue;
            }

            if (!(resolved is PdfDictionary dict)) continue;
            if (dict.ObjectNumber > 0 && !seen.Add(dict.ObjectNumber)) continue;

            found.Add(dict);
            if (dict[new PdfName("K")] != null) queue.Enqueue(dict[new PdfName("K")]);
        }

        return found;
    }

    /// <summary>The bare MCID integers in an element's <c>/K</c>.</summary>
    private static List<int> McidsOf(PdfDocument doc, PdfDictionary element)
    {
        var mcids = new List<int>();
        PdfObject kids = doc.ResolveObject(element[new PdfName("K")]);

        if (kids is PdfNumber single) { mcids.Add(single.IntValue); return mcids; }
        if (kids is PdfArray array)
            foreach (PdfObject kid in array)
                if (doc.ResolveObject(kid) is PdfNumber number) mcids.Add(number.IntValue);

        return mcids;
    }

    /// <summary>The decoded content stream of one page of the produced document.</summary>
    private static string PageContent(byte[] pdf, int pageIndex)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        byte[] data = doc.Pages[pageIndex].GetContentStreamData();
        return Encoding.GetEncoding("iso-8859-1").GetString(data ?? Array.Empty<byte>());
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

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

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
