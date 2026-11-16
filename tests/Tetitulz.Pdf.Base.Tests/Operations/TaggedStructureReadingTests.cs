// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Reading a document in <b>structure</b> order — what a screen reader is told, and what a caller
/// asking a tagged document for its text receives.
/// </summary>
/// <remarks>
/// <para><b>A sweep of <see cref="PdfTaggedTextExtractor"/> left thirteen of its seventeen decisions
/// standing — the highest proportion of any file in this folder.</b> The two fixtures that existed
/// covered the <c>/Stm</c> scoping defect and the <c>/K</c> edge budget; everything between an
/// element and its text was unasserted.</para>
///
/// <para><b>The harm is quiet in both directions.</b> Text attributed to the wrong element reads as
/// the wrong heading or the wrong paragraph; text dropped entirely is indistinguishable from an
/// untagged page. Neither shows up in a document that merely opens.</para>
/// </remarks>
public class TaggedStructureReadingTests
{
    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);
    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string dictExtras, string content)
        => $"{n} 0 obj\n<< /Length {Latin1(content).Length}{dictExtras} >>\nstream\n{content}\nendstream\nendobj\n";

    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        using var ms = new MemoryStream();
        var head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            var b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static IReadOnlyList<PdfTextBlock> Blocks(byte[] pdf)
        => PdfTaggedTextExtractor.Extract(new MemoryStream(pdf, writable: false));

    // ==================================================================
    //  One document, five rules
    // ==================================================================

    /// <summary>
    /// A tagged page carrying, in order: a heading whose <c>/ActualText</c> disagrees with its
    /// glyphs; a figure with <c>/Alt</c> and no content; a paragraph assembled from two marked
    /// sequences; a paragraph whose kid is an object reference; one whose kid is an unclassifiable
    /// dictionary; and one that names an empty sequence.
    /// </summary>
    /// <remarks>
    /// The last two kids each hang a <c>/K</c> on content that must <b>not</b> be reached: an
    /// extractor that walked them would attribute "Hidden" and "Secret" to a paragraph that does not
    /// contain them.
    /// </remarks>
    private static byte[] RichTaggedPage() => Assemble(new List<string>
    {
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"),
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
               "/Resources << /Font << /F1 5 0 R >> >> >>"),
        StreamObj(4, "",
            "/H1 << /MCID 0 >> BDC BT /F1 24 Tf 72 700 Td (Glyphs) Tj ET EMC\n" +
            "/P << /MCID 1 >> BDC BT /F1 12 Tf 72 660 Td (One) Tj ET EMC\n" +
            "/P << /MCID 2 >> BDC BT /F1 12 Tf 72 640 Td (Two) Tj ET EMC\n" +
            "/P << /MCID 3 >> BDC BT /F1 12 Tf 72 620 Td (Hidden) Tj ET EMC\n" +
            "/P << /MCID 4 >> BDC BT /F1 12 Tf 72 600 Td (Secret) Tj ET EMC\n" +
            "/P << /MCID 5 >> BDC BT /F1 12 Tf 72 580 Td ( ) Tj ET EMC\n" +
            "/P << /MCID 6 >> BDC BT /F1 12 Tf 72 560 Td (Six) Tj ET EMC\n"),
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        Obj(6, "<< /Type /StructTreeRoot /K [7 0 R] >>"),
        Obj(7, "<< /Type /StructElem /S /Document /P 6 0 R " +
               "/K [8 0 R 9 0 R 10 0 R 11 0 R 12 0 R 13 0 R] >>"),
        // The glyphs say "Glyphs"; the producer says the text is "Real Heading".
        Obj(8, "<< /Type /StructElem /S /H1 /P 7 0 R /Pg 3 0 R /K 0 /ActualText (Real Heading) >>"),
        // A figure with alternate text and no content of its own.
        Obj(9, "<< /Type /StructElem /S /Figure /P 7 0 R /Pg 3 0 R /Alt (A chart) >>"),
        // Two sequences, one paragraph.
        Obj(10, "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K [1 2] >>"),
        // An object reference — no extractable text. Its /K must not be followed.
        Obj(11, "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R " +
                "/K << /Type /OBJR /Obj 3 0 R /K 4 >> >>"),
        // Not a kid of any kind: no /S, no /Type, no /MCID. Its /K must not be followed either.
        Obj(12, "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K << /K 3 >> >>"),
        // MCID 5 drew one space, so its entry exists and is empty. (Beside its own sequence: a
        // sequence has one parent, §14.7.4.4, so naming element 10's MCID 1 here gives it nothing.)
        Obj(13, "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K [6 5] >>"),
    });

    /// <summary>
    /// <b>§14.9.4's <c>/ActualText</c> replaces the element's glyphs</b> — it is what the producer
    /// says the text <i>is</i>, and the two disagree precisely where it mattered enough to say so.
    /// </summary>
    [Fact]
    public void ActualTextReplacesTheGlyphsOfItsElement()
    {
        PdfTextBlock h1 = Assert.Single(Blocks(RichTaggedPage()), b => b.StructureType == "H1");

        Assert.Equal("Real Heading", h1.Text);
        Assert.DoesNotContain("Glyphs", h1.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>An element with <c>/Alt</c> and no text of its own still produces a block.</b> A figure's
    /// alternate text is the only thing a reader has for it, so dropping the element because it drew
    /// no glyphs discards exactly the content the tag exists to carry.
    /// </summary>
    [Fact]
    public void AnElementWithOnlyAlternateTextStillProducesABlock()
    {
        PdfTextBlock figure = Assert.Single(Blocks(RichTaggedPage()), b => b.StructureType == "Figure");

        Assert.Equal("A chart", figure.AltText);
        Assert.Equal(string.Empty, figure.Text);
    }

    /// <summary>
    /// <b>Two marked sequences in one element are joined by a space</b>, not run together.
    /// </summary>
    /// <remarks>
    /// A paragraph split across sequences is the normal shape for a line broken by a style change,
    /// so running them together produces "OneTwo" — a word that is in no document and that no
    /// search will find.
    /// </remarks>
    [Fact]
    public void TwoSequencesInOneElementAreJoinedByASpace()
    {
        var paragraphs = Blocks(RichTaggedPage()).Where(b => b.StructureType == "P").ToList();

        Assert.Contains(paragraphs, p => p.Text == "One Two");
    }

    /// <summary>
    /// <b>Neither an object reference nor an unclassifiable dictionary is walked as a structure
    /// element.</b>
    /// </summary>
    /// <remarks>
    /// <para>Both kids below hang a <c>/K</c> on real marked content. Following them would attribute
    /// that content to the paragraph holding the kid — and for the unclassified case it would also
    /// spend a visited slot and a depth level on a dictionary an adversarial file chose.</para>
    ///
    /// <para>The assertion names the text rather than the block count, because the failure is
    /// <i>attribution</i>: the block still exists either way, it simply acquires words the page put
    /// somewhere else.</para>
    /// </remarks>
    [Fact]
    public void ObjectReferenceAndUnclassifiedKidsAreNotFollowed()
    {
        string all = string.Concat(Blocks(RichTaggedPage()).Select(b => b.Text));

        Assert.DoesNotContain("Secret", all, StringComparison.Ordinal);   // behind the /OBJR
        Assert.DoesNotContain("Hidden", all, StringComparison.Ordinal);   // behind the unclassified kid

        // The control: content that IS properly referenced comes through, so the two absences above
        // are not simply an extractor that read nothing.
        Assert.Contains("One", all, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A sequence that produced no text contributes no separator either.</b>
    /// </summary>
    /// <remarks>
    /// An empty sequence — one that drew only a space — has an entry in the marked-content map whose
    /// value is the empty string. Appending a separator for it leaves a trailing space on the
    /// element's text, which is invisible in a diff and changes every offset a caller derives.
    /// </remarks>
    [Fact]
    public void AnEmptySequenceAddsNoSeparator()
    {
        var paragraphs = Blocks(RichTaggedPage()).Where(b => b.StructureType == "P").ToList();

        // Asserted untrimmed: the whole difference is one trailing space.
        Assert.Contains(paragraphs, p => p.Text == "Six");
        Assert.DoesNotContain(paragraphs, p => p.Text == "Six ");
    }

    // ==================================================================
    //  The two guards on the walk
    // ==================================================================

    /// <summary>
    /// <b>A structure tree nested past the depth cap is abandoned, and now says so.</b>
    /// </summary>
    /// <remarks>
    /// <para>Until this test the cap returned in silence — a branch dropped at 1000 levels was
    /// indistinguishable from a tree that simply ends, which is the same shape the form-depth caps in
    /// the other two extractors had. The edge budget beside it always reported; the two guards on one
    /// walk disagreed about whether being stopped is worth mentioning.</para>
    ///
    /// <para>The reason is its own, not the edge budget's: a document nested 1001 deep has come
    /// nowhere near a two-million-slot array ceiling, and reporting that would send the reader to the
    /// wrong question.</para>
    /// </remarks>
    [Fact]
    public void AStructureTreeNestedPastTheDepthCapIsReported()
    {
        // One element per level, the innermost naming the only marked content on the page.
        int levels = PdfLimits.MaxStructTreeDepth + 2;
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                   "/Resources << /Font << /F1 5 0 R >> >> >>"),
            StreamObj(4, "", "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (Deep) Tj ET EMC\n"),
            Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Obj(6, "<< /Type /StructTreeRoot /K [7 0 R] >>"),
        };
        for (int i = 0; i < levels; i++)
        {
            string kid = i == levels - 1 ? "/K 0" : $"/K [{8 + i} 0 R]";
            bodies.Add(Obj(7 + i, $"<< /Type /StructElem /S /P /Pg 3 0 R {kid} >>"));
        }

        var blocks = Blocks(Assemble(bodies));

        Assert.DoesNotContain("Deep", string.Concat(blocks.Select(b => b.Text)), StringComparison.Ordinal);
        Assert.Contains("nested deeper than", PdfTaggedTextExtractor.LastError ?? string.Empty,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Two elements whose <c>/K</c> point at each other are walked once, not once per level.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>Without the visited set this does not hang</b> — the depth cap stops it — so the
    /// symptom is not a timeout but <i>duplication</i>: the cycle is walked down to 1000 levels and
    /// the element carrying text emits a block at each one. A reader would be handed the same
    /// paragraph hundreds of times.</para>
    ///
    /// <para>That the depth cap makes a missing cycle guard survivable is why this reads as covered
    /// to anything that only checks termination.</para>
    /// </remarks>
    [Fact]
    public void AKidCycleIsWalkedOnceRatherThanOncePerLevel()
    {
        byte[] pdf = Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                   "/Resources << /Font << /F1 5 0 R >> >> >>"),
            StreamObj(4, "", "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (Loop) Tj ET EMC\n"),
            Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Obj(6, "<< /Type /StructTreeRoot /K [7 0 R] >>"),
            Obj(7, "<< /Type /StructElem /S /P /Pg 3 0 R /K [8 0 R] >>"),
            Obj(8, "<< /Type /StructElem /S /P /Pg 3 0 R /K [7 0 R 0] >>"),
        });

        var blocks = Blocks(pdf);

        Assert.Single(blocks, b => b.Text.Contains("Loop", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>Untagged glyphs never reach a structure element</b>, including one that names a negative
    /// MCID.
    /// </summary>
    /// <remarks>
    /// <para><b>Two guards stand between these two things and neither can be tested alone, because
    /// each masks the other.</b> `BuildMcidText` keeps glyphs with a negative MCID out of the map;
    /// `AppendMcid` refuses to look one up. Remove either and the survivor still does the work, so a
    /// mutation of one is unkillable by construction — which is why both survived the sweep and why
    /// neither is coverage debt in the usual sense.</para>
    ///
    /// <para>This test is the acceptance control for the pair: with both gone, the paragraph below
    /// acquires every untagged glyph on the page — text the producer attributed to nothing, handed to
    /// a reader as that element's content.</para>
    /// </remarks>
    [Fact]
    public void AnElementNamingANegativeMcidGetsNoUntaggedText()
    {
        byte[] pdf = Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                   "/Resources << /Font << /F1 5 0 R >> >> >>"),
            // "Untagged" is deliberately outside any BDC; "Tagged" is MCID 0, the control.
            StreamObj(4, "",
                "BT /F1 12 Tf 72 700 Td (Untagged) Tj ET\n" +
                "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 660 Td (Tagged) Tj ET EMC\n"),
            Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Obj(6, "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] >>"),
            Obj(7, "<< /Type /StructElem /S /P /Pg 3 0 R /K [-1] >>"),
            Obj(8, "<< /Type /StructElem /S /H1 /Pg 3 0 R /K [0] >>"),
        });

        var blocks = Blocks(pdf);

        Assert.DoesNotContain("Untagged", string.Concat(blocks.Select(b => b.Text)), StringComparison.Ordinal);

        // The control: the page's marked content IS read, so the absence above is not an extractor
        // that read nothing at all.
        PdfTextBlock h1 = Assert.Single(blocks, b => b.StructureType == "H1");
        Assert.Equal("Tagged", h1.Text);
    }

    // ==================================================================
    //  Two pages: which page, and which stream
    // ==================================================================

    /// <summary>
    /// A two-page document whose tagged content is on the <b>second</b> page, so that a page index
    /// of 0 cannot stand in for the right answer by coincidence.
    /// </summary>
    private static byte[] TaggedSecondPage(string elementKey) => Assemble(new List<string>
    {
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R >>"),
        Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
               "/Resources << /Font << /F1 6 0 R >> >> >>"),
        StreamObj(4, "", "BT /F1 12 Tf 72 700 Td (PageOne) Tj ET\n"),
        Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 9 0 R " +
               "/Resources << /Font << /F1 6 0 R >> >> >>"),
        Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        Obj(7, "<< /Type /StructTreeRoot /K [8 0 R] >>"),
        Obj(8, $"<< /Type /StructElem /S /P /P 7 0 R /Pg 5 0 R {elementKey} >>"),
        StreamObj(9, "", "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (OnPageTwo) Tj ET EMC\n"),
    });

    /// <summary>
    /// <b>A bare integer <c>/K</c> names an MCID in the page's own content stream</b> (§14.7.4.2),
    /// whose scope is 0 — never the page's index. Asserted in <b>both</b> spellings, because the
    /// rule is implemented twice.
    /// </summary>
    /// <remarks>
    /// <para>On page 0 the stream scope and the page index are both 0, so only a document whose
    /// tagged content is on a later page can tell the correct answer from the coincidence. Every
    /// existing fixture is single-page.</para>
    ///
    /// <para><b>`/K 0` and `/K [0]` reach different code.</b> A direct number is handled by
    /// <c>ProcessKContent</c>; a number inside an array goes through <c>DispatchKItem</c>. They
    /// carry the same rule in two places, so a fixture using one spelling leaves the other's copy
    /// unasserted — which is exactly what happened: a mutation of the array path survived a test
    /// written with the direct one.</para>
    /// </remarks>
    [Theory]
    [InlineData("/K 0")]      // ProcessKContent's PdfNumber branch
    [InlineData("/K [0]")]    // DispatchKItem's PdfNumber branch
    public void ABareIntegerKidIsScopedToThePagesOwnStream(string elementKey)
    {
        PdfTextBlock p = Assert.Single(Blocks(TaggedSecondPage(elementKey)), b => b.StructureType == "P");

        Assert.Equal("OnPageTwo", p.Text);
    }

    /// <summary>
    /// <b>Two lines inside one marked-content sequence are joined by a space.</b>
    /// </summary>
    /// <remarks>
    /// A sequence spanning a line break is the normal shape for a wrapped paragraph, and the
    /// marked-content map assembles its glyphs through the same line builder the page uses. With one
    /// line per sequence the join is unobservable, and every existing fixture has one line per
    /// sequence.
    /// </remarks>
    [Fact]
    public void TwoLinesInOneSequenceAreJoinedByASpace()
    {
        byte[] pdf = Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                   "/Resources << /Font << /F1 5 0 R >> >> >>"),
            StreamObj(4, "",
                "/P << /MCID 0 >> BDC " +
                "BT /F1 12 Tf 72 700 Td (Alpha) Tj ET " +
                "BT /F1 12 Tf 72 660 Td (Beta) Tj ET EMC\n"),
            Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Obj(6, "<< /Type /StructTreeRoot /K [7 0 R] >>"),
            Obj(7, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>"),
        });

        PdfTextBlock p = Assert.Single(Blocks(pdf), b => b.StructureType == "P");

        Assert.Equal("Alpha Beta", p.Text);
    }

    /// <summary>
    /// <b>A marked-content reference's own <c>/Pg</c> decides which page it is looked up on</b>
    /// (Table 324), overriding the element's.
    /// </summary>
    /// <remarks>
    /// The element below claims page 2 while its reference names page 2 explicitly; an extractor
    /// that ignored the reference's <c>/Pg</c> and inherited the element's would still be right
    /// here — so the element is deliberately given the <i>wrong</i> page, and only reading the
    /// reference's own entry finds the text.
    /// </remarks>
    [Fact]
    public void AMarkedContentReferenceUsesItsOwnPage()
    {
        // The element says page 1 (object 3); the reference says page 2 (object 5), where the
        // content actually is.
        byte[] pdf = Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                   "/Resources << /Font << /F1 6 0 R >> >> >>"),
            StreamObj(4, "", "BT /F1 12 Tf 72 700 Td (PageOne) Tj ET\n"),
            Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 9 0 R " +
                   "/Resources << /Font << /F1 6 0 R >> >> >>"),
            Obj(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            Obj(7, "<< /Type /StructTreeRoot /K [8 0 R] >>"),
            Obj(8, "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R " +
                   "/K << /Type /MCR /Pg 5 0 R /MCID 0 >> >>"),
            StreamObj(9, "", "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (OnPageTwo) Tj ET EMC\n"),
        });

        PdfTextBlock p = Assert.Single(Blocks(pdf), b => b.StructureType == "P");

        Assert.Equal("OnPageTwo", p.Text);
    }
}
