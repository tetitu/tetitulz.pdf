// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Tagging text inside a Form XObject that <b>more than one page</b> draws (plan 08 item 49), and
/// the two inventory defects that had to be fixed before it could work at all.
/// </summary>
/// <remarks>
/// <para>
/// A Form XObject has one stream dictionary and therefore one <c>/StructParents</c>, so its marked
/// content can belong to exactly one page's parent-tree entry. The escape is to give each invoking
/// page its own copy — which means repointing that page's <c>/XObject</c> resource entry, which
/// means the page must own the dictionary being edited.
/// </para>
/// <para>
/// <b>The three sharing shapes below fail differently and all three are needed.</b> A repointer
/// that copies <c>/Resources</c> but mutates the <c>/XObject</c> sub-dictionary in place passes
/// every assertion against separate-resources and inherited-resources fixtures, and is caught only
/// by the shared-<c>/XObject</c> one.
/// </para>
/// </remarks>
public class SharedFormTaggingTests
{
    // =====================================================================
    //  1. Inventory completeness — both defects are red on pre-item-49 code
    // =====================================================================

    /// <summary>
    /// A page that <b>inherits</b> its <c>/Resources</c> from the <c>/Pages</c> node still gets the
    /// text inside the form it draws marked.
    /// </summary>
    /// <remarks>
    /// An inheriting page has no <i>own</i> <c>/Resources</c> key, so reading that key when
    /// collecting the names a page invokes would yield null, the form would never be inventoried,
    /// and the page would be reported partially tagged with "an XObject was invoked but no
    /// /XObject resource dictionary resolves" — a message about the file, describing a limitation
    /// of the reader. Form tagging would do nothing for any document laying its resources out this
    /// way, which is a common and entirely valid layout.
    /// </remarks>
    [Fact]
    public void AutoTag_PageInheritingItsResources_TagsTheFormItDraws()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(InheritedResourcesOneFormPdf());

        Assert.True(result.ContentMarked);
        Assert.Equal(0, result.PagesPartiallyTagged);

        using PdfDocument doc = Open(tagged);
        PdfStream form = FormOnPage(doc, 0, "Fm0");

        Assert.NotNull(form[PdfName.StructParents]);
        Assert.Contains("BDC", Decoded(form), StringComparison.Ordinal);
    }

    /// <summary>
    /// A form drawn by one taggable page <b>and</b> one the tagger skipped must never be rewritten
    /// in place: the skipped page would then render marked content belonging to another page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Page 2 here carries marked content the tagger cannot read exactly — one sequence nested
    /// inside another — so it declines the page. Were the names it invokes recorded only
    /// <i>after</i> that decline returned, the form would look as though a single page drew it.
    /// (Merely <i>having</i> marked content is not a refusal: a page whose sequences can be read is
    /// completed instead, which is what
    /// <see cref="AutoTag_FormAlsoDrawnByACompletedPage_IsDuplicatedSoBothPagesAreTagged"/>
    /// covers. Nesting is one of the shapes that cannot be reasoned about.) Tagged in place, page 2
    /// would end up drawing content whose owning structure element names page 1 as its
    /// <c>/Pg</c>: a wrong tree that validates, the same failure class <c>/Stm</c> exists to
    /// prevent.
    /// </para>
    /// <para>
    /// <b>The assertion is on the object page 2 actually draws</b>, not on the tagger's report, so
    /// it holds whether the form is left untagged or page 1 draws a clone while page 2 keeps the
    /// pristine original. A skipped page is never given a copy: it has no
    /// page-level <c>/StructParents</c>, so structural marked content inside a form it draws would
    /// manufacture a UA18 finding.
    /// </para>
    /// </remarks>
    [Fact]
    public void AutoTag_FormAlsoDrawnByASkippedPage_LeavesThatPagesCopyUnmarked()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(SharedWithARefusedPagePdf());

        Assert.Equal(1, result.PagesSkipped);

        using PdfDocument doc = Open(tagged);
        PdfStream drawnByTheSkippedPage = FormOnPage(doc, 1, "Fm0");

        Assert.Null(drawnByTheSkippedPage[PdfName.StructParents]);
        Assert.DoesNotContain("BDC", Decoded(drawnByTheSkippedPage), StringComparison.Ordinal);
    }

    /// <summary>
    /// A form drawn by a page that is <b>completed</b> rather than skipped is duplicated, and both
    /// pages end up tagged.
    /// </summary>
    /// <remarks>
    /// <b>The counterpart to the refusal above, and the reason that one had to change its
    /// fixture.</b> A page carrying readable marked content is no longer skipped, so a form it
    /// draws is now drawn by two <i>tagged</i> pages — which is exactly the sharing shape that has
    /// to duplicate. A page-invocation record that were still keyed off "was this page skipped"
    /// would give one of them a form whose owning element names the other page as its
    /// <c>/Pg</c>.
    /// </remarks>
    [Fact]
    public void AutoTag_FormAlsoDrawnByACompletedPage_IsDuplicatedSoBothPagesAreTagged()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(SharedWithACompletablePagePdf());

        Assert.Equal(0, result.PagesSkipped);
        Assert.True(result.ContentMarked, string.Join(" | ", result.SkippedPageReasons));

        using PdfDocument doc = Open(tagged);
        PdfStream first = FormOnPage(doc, 0, "Fm0");
        PdfStream second = FormOnPage(doc, 1, "Fm0");

        Assert.NotEqual(first.ObjectNumber, second.ObjectNumber);
        Assert.NotNull(first[PdfName.StructParents]);
        Assert.NotNull(second[PdfName.StructParents]);
    }

    /// <summary>
    /// Two pages drawing one shared form; page 2 already carries a readable marked-content
    /// sequence, so the tagger completes that page instead of declining it.
    /// </summary>
    private static byte[] SharedWithACompletablePagePdf()
    {
        const string alreadyMarked =
            "/P << /MCID 0 >> BDC BT /F1 10 Tf 72 400 Td (already marked) Tj ET EMC\n"
            + "BT /F1 10 Tf 72 380 Td (not yet marked) Tj ET\n"
            + "q 1 0 0 1 0 0 cm /Fm0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
            StreamObject("", PageText),
            StreamObject("", alreadyMarked),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 7 0 R >> >>", FormText),
        });
    }

    // =====================================================================
    //  2. Duplication — each invoking page gets its own copy
    // =====================================================================

    /// <summary>Two pages sharing one form are both fully tagged once the form is duplicated.</summary>
    [Fact]
    public void AutoTag_FormSharedByTwoPages_IsDuplicatedSoBothPagesAreFullyTagged()
    {
        (AutoTagResult result, _) = AutoTag(TwoPagesSharingOneFormPdf());

        Assert.True(result.ContentMarked);
        Assert.Equal(0, result.PagesPartiallyTagged);
        Assert.Equal(1, result.SharedFormsDuplicated);
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("still untagged", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// Three pages sharing one form end up drawing <b>three pairwise-distinct</b> streams, each
    /// with its own <c>/StructParents</c>, and every one of those keys resolves in the
    /// <c>/ParentTree</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pairwise distinctness is the load-bearing word.</b> "Page 2 differs from page 1" alone
    /// passes an implementation that allocates one copy and hands it to every page after the first
    /// — which is precisely the collision duplication exists to prevent, and which no audit code
    /// can see, because the content really is marked and the tree really does parse.
    /// </para>
    /// <para>
    /// The <c>/ParentTree</c> assertion is what makes the distinct keys mean something: a key with
    /// no entry addresses nothing, so three distinct keys and two entries would still be wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormOnThreePages_GivesEachPageItsOwnCopyAndItsOwnParentTreeEntry()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(ThreePagesSharingOneFormPdf());

        Assert.Equal(0, result.PagesPartiallyTagged);

        using PdfDocument doc = Open(tagged);

        var streams = new List<PdfStream>
        {
            FormOnPage(doc, 0, "Fm0"), FormOnPage(doc, 1, "Fm0"), FormOnPage(doc, 2, "Fm0"),
        };

        var objectNumbers = new List<int>();
        var keys = new List<int>();
        foreach (PdfStream stream in streams)
        {
            objectNumbers.Add(stream.ObjectNumber);
            keys.Add(IntValue(doc, stream[PdfName.StructParents]));
            Assert.Contains("BDC", Decoded(stream), StringComparison.Ordinal);
        }

        Assert.Equal(3, new HashSet<int>(objectNumbers).Count);
        Assert.Equal(3, new HashSet<int>(keys).Count);
        Assert.DoesNotContain(-1, keys);

        foreach (int key in keys) Assert.NotNull(ParentTreeEntry(doc, key));
    }

    /// <summary>
    /// When the shared form's pages <b>inherit</b> their resources from the <c>/Pages</c> node, the
    /// repointed page gets its own <c>/Resources</c> — carrying the inherited entries with it — and
    /// the first page is left drawing the original through the untouched inherited dictionary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>/F1</c> assertion is the point of this test.</b> A page's own <c>/Resources</c>
    /// <i>replaces</i> the inherited one rather than merging with it (ISO 32000-1 §7.7.3.4), so a
    /// repointer that builds the page's copy from the page's own <c>/Resources</c> key — which an
    /// inheriting page does not have — hands it a dictionary holding nothing but <c>/XObject</c>
    /// and silently takes away every font the page was drawing with. That mistake is live in the
    /// tree elsewhere and is easy to copy.
    /// </para>
    /// <para>
    /// The first page keeping the original also has to be asserted: without it, an implementation
    /// that copies the resources onto the <c>/Pages</c> node instead of onto the page would pass
    /// every "page 2 has its own copy" check while quietly repointing page 1 as well.
    /// </para>
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormUnderInheritedResources_KeepsTheInheritedEntriesAndTheFirstPage()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(TwoInheritingPagesSharingOneFormPdf());

        Assert.Equal(0, result.PagesPartiallyTagged);

        using PdfDocument doc = Open(tagged);

        PdfStream first  = FormOnPage(doc, 0, "Fm0");
        PdfStream second = FormOnPage(doc, 1, "Fm0");
        Assert.NotEqual(first.ObjectNumber, second.ObjectNumber);

        // Page 1 was never touched: it still has no /Resources of its own and still reaches the
        // original form through the inherited dictionary.
        Assert.Null(doc.Pages[0].Dictionary[PdfName.Resources]);

        // Page 2 owns its resources now — and they still carry the font it inherited.
        Assert.NotNull(doc.Pages[1].Dictionary[PdfName.Resources]);
        var fonts = doc.ResolveObject(doc.Pages[1].Resources[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        Assert.NotNull(fonts[new PdfName("F1")]);
    }

    /// <summary>
    /// Two pages whose distinct <c>/Resources</c> name one <b>shared <c>/XObject</c> dictionary</b>
    /// are separated too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A third sharing layout, distinct from separate and from inherited resources: the copy of
    /// <c>/Resources</c> is a fresh dictionary that still <i>points at</i> one shared
    /// <c>/XObject</c>.
    /// </para>
    /// <para>
    /// <b>What this test does not pin, contrary to the obvious reading.</b> Mutation testing showed
    /// that writing into the resolved <c>/XObject</c> in place instead of copying it leaves this
    /// green — the mutation is equivalent here. The incremental writer emits only the objects the
    /// pass registered, and the shared <c>/XObject</c> is not one of them, so an in-place edit to
    /// it never reaches the output; the repointed page gets the entry because its copied
    /// <c>/Resources</c> is serialised inline. The copy in
    /// <c>PageContentRewriter.TryRepointXObjectNames</c> is therefore defensive rather than
    /// load-bearing <i>today</i>, and it stays: it stops the pass mutating a parsed object its
    /// caller still holds, and it becomes observable the moment anything registers that dictionary
    /// for emission.
    /// </para>
    /// <para>
    /// What it does pin is the behaviour in the name — page 1 keeps the original — which fails for
    /// any implementation that repoints through a route page 1 shares.
    /// </para>
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormUnderASharedXObjectDictionary_LeavesTheFirstPageDrawingTheOriginal()
    {
        byte[] original = TwoPagesSharingOneXObjectDictionaryPdf();

        int originalFormNumber;
        using (PdfDocument before = Open(original))
            originalFormNumber = FormOnPage(before, 0, "Fm0").ObjectNumber;

        (AutoTagResult result, byte[] tagged) = AutoTag(original);
        Assert.Equal(0, result.PagesPartiallyTagged);

        using PdfDocument doc = Open(tagged);

        Assert.Equal(originalFormNumber, FormOnPage(doc, 0, "Fm0").ObjectNumber);
        Assert.NotEqual(originalFormNumber, FormOnPage(doc, 1, "Fm0").ObjectNumber);
    }

    /// <summary>
    /// A page naming one form under <b>two</b> resource names has both repointed at its copy.
    /// </summary>
    /// <remarks>
    /// Repointing only the first name would leave the page drawing its own copy under <c>/A</c> and
    /// the first page's copy under <c>/B</c> — the same content marked twice, under two different
    /// parent-tree entries, one of them belonging to another page.
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormAliasedUnderTwoNamesOnOnePage_RepointsEveryName()
    {
        (_, byte[] tagged) = AutoTag(SecondPageAliasingTheSharedFormPdf());

        using PdfDocument doc = Open(tagged);

        int firstPageForm = FormOnPage(doc, 0, "Fm0").ObjectNumber;
        int viaA = FormOnPage(doc, 1, "A").ObjectNumber;
        int viaB = FormOnPage(doc, 1, "B").ObjectNumber;

        Assert.Equal(viaA, viaB);
        Assert.NotEqual(firstPageForm, viaA);
    }

    /// <summary>
    /// A page drawing <b>two</b> different shared forms keeps both repoints, and keeps the
    /// <c>/Contents</c> and <c>/StructParents</c> its own content rewrite wrote.
    /// </summary>
    /// <remarks>
    /// Each repoint revises the same page dictionary, and so did the page's own content rewrite
    /// before them. Building each revision from the page as parsed rather than from the revision
    /// already pending would make every step discard the one before it — the second form's repoint
    /// undoing the first, and both undoing the page's marked content.
    /// </remarks>
    [Fact]
    public void AutoTag_PageDrawingTwoSharedForms_KeepsBothRepointsAndItsOwnContentEdit()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(TwoPagesSharingTwoFormsPdf());

        Assert.Equal(0, result.PagesPartiallyTagged);
        Assert.Equal(2, result.SharedFormsDuplicated);

        using PdfDocument doc = Open(tagged);

        Assert.NotEqual(FormOnPage(doc, 0, "Fm0").ObjectNumber, FormOnPage(doc, 1, "Fm0").ObjectNumber);
        Assert.NotEqual(FormOnPage(doc, 0, "Fm1").ObjectNumber, FormOnPage(doc, 1, "Fm1").ObjectNumber);

        PdfDictionary page = doc.Pages[1].Dictionary;
        Assert.NotNull(page[PdfName.Contents]);
        Assert.True(IntValue(doc, page[new PdfName("StructParents")]) >= 0,
            "the page must keep the /StructParents its own content rewrite wrote");
        Assert.Contains("BDC", Decoded(doc, page[PdfName.Contents]), StringComparison.Ordinal);
    }

    // =====================================================================
    //  3. What duplication refuses
    // =====================================================================

    /// <summary>
    /// A form one page draws directly and another reaches from <b>inside another form</b> is left
    /// untagged, and reported.
    /// </summary>
    /// <remarks>
    /// The second page's copy would have to be substituted in the parent form's resources, which
    /// every page drawing that parent shares — so duplicating it would change pages the tagger was
    /// not working on. The test is per invocation, not per form: duplicating for the direct page
    /// only would leave the other page drawing the tagged original, which is the outcome the
    /// all-or-nothing rule exists to forbid.
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormAlsoReachedThroughAnotherForm_IsLeftUntaggedAndReported()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(SharedFormReachedThroughAnotherFormPdf());

        Assert.True(result.PagesPartiallyTagged > 0);

        using PdfDocument doc = Open(tagged);
        Assert.Null(FormOnPage(doc, 0, "Fm0")[PdfName.StructParents]);
    }

    /// <summary>
    /// A form one page draws <b>both directly and through another form</b> is left untagged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case the per-invocation <c>ReachedThroughAnotherForm</c> flag exists for, and the only
    /// fixture that isolates it. Where a page reaches the form <i>only</i> through a wrapper, the
    /// "no direct resource name to repoint" check refuses it first — so deleting the nesting check
    /// entirely leaves that test green, which is how this gap was found.
    /// </para>
    /// <para>
    /// Repointing page 2's direct name would tag the form while its wrapper still drew the
    /// original, so the page would render the text twice: once marked and once not.
    /// </para>
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormDrawnBothDirectlyAndThroughAnotherForm_IsLeftUntagged()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(FormDrawnDirectlyAndThroughAWrapperPdf());

        Assert.True(result.PagesPartiallyTagged > 0);

        using PdfDocument doc = Open(tagged);
        Assert.Null(FormOnPage(doc, 0, "Fm0")[PdfName.StructParents]);
    }

    /// <summary>
    /// A form too large to copy for every page that draws it is reported with the budget as the
    /// reason, and left exactly as it was.
    /// </summary>
    /// <remarks>
    /// Duplication multiplies output bytes by a factor the <b>file</b> chooses — a form drawn from
    /// a thousand pages asks for a thousand copies of it. The assertion is on the guard's own
    /// evidence (the form carries no <c>/StructParents</c>, and the warning names the budget),
    /// never on elapsed time: a guard test that finishes quickly has quite possibly not run.
    /// </remarks>
    [Fact]
    public void AutoTag_SharedFormOversizedForTheDuplicationBudget_IsRefusedAndSaysSo()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(OversizedSharedFormPdf());

        Assert.Contains(result.Warnings,
            w => w.IndexOf("budget for duplicated form content", StringComparison.Ordinal) >= 0);

        using PdfDocument doc = Open(tagged);
        Assert.Null(FormOnPage(doc, 0, "Fm0")[PdfName.StructParents]);
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    private const string FormText = "BT /F1 10 Tf 10 10 Td (text inside the form) Tj ET\n";
    private const string PageText = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\nq 1 0 0 1 0 0 cm /Fm0 Do Q\n";

    /// <summary>One page drawing one form, with <c>/Resources</c> only on the <c>/Pages</c> node.</summary>
    private static byte[] InheritedResourcesOneFormPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 "
            + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        StreamObject("", PageText),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
            + "/Resources << /Font << /F1 5 0 R >> >>", FormText),
    });

    /// <summary>
    /// Two pages drawing one shared form; page 2's own content carries <em>nested</em> marked
    /// content, which the tagger declines because it cannot bound a new sequence inside it.
    /// </summary>
    private static byte[] SharedWithARefusedPagePdf()
    {
        // Nested sequences: readable as bytes, not reasonable about as ranges, so the tagger
        // refuses the page rather than guessing where a new sequence could go.
        const string alreadyMarked =
            "/P << /MCID 0 >> BDC /Span << /MCID 1 >> BDC "
            + "BT /F1 10 Tf 72 400 Td (already marked) Tj ET EMC EMC\n"
            + "q 1 0 0 1 0 0 cm /Fm0 Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
            StreamObject("", PageText),
            StreamObject("", alreadyMarked),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 7 0 R >> >>", FormText),
        });
    }

    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                              + "/Encoding /WinAnsiEncoding >>";

    private static string FormObject(string fontRef, string content = FormText)
        => StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
             + "/Resources << /Font << /F1 " + fontRef + " >> >>", content);

    /// <summary>Two pages, each with its own <c>/Resources</c>, drawing one shared form.</summary>
    private static byte[] TwoPagesSharingOneFormPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        Page("5 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >>"),
        Page("6 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >>"),
        StreamObject("", PageText),
        StreamObject("", PageText),
        Font,
        FormObject("7 0 R"),
    });

    private static byte[] ThreePagesSharingOneFormPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        Page("6 0 R", "/Resources << /Font << /F1 9 0 R >> /XObject << /Fm0 10 0 R >> >>"),
        Page("7 0 R", "/Resources << /Font << /F1 9 0 R >> /XObject << /Fm0 10 0 R >> >>"),
        Page("8 0 R", "/Resources << /Font << /F1 9 0 R >> /XObject << /Fm0 10 0 R >> >>"),
        StreamObject("", PageText),
        StreamObject("", PageText),
        StreamObject("", PageText),
        Font,
        FormObject("9 0 R"),
    });

    /// <summary>Two pages with no <c>/Resources</c> of their own; it lives on the <c>/Pages</c> node.</summary>
    private static byte[] TwoInheritingPagesSharingOneFormPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 "
            + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >> >>",
        Page("5 0 R", null),
        Page("6 0 R", null),
        StreamObject("", PageText),
        StreamObject("", PageText),
        Font,
        FormObject("7 0 R"),
    });

    /// <summary>
    /// Two pages with <b>distinct</b> <c>/Resources</c> that both name one indirect <c>/XObject</c>
    /// dictionary.
    /// </summary>
    private static byte[] TwoPagesSharingOneXObjectDictionaryPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        Page("5 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject 9 0 R >>"),
        Page("6 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject 9 0 R >>"),
        StreamObject("", PageText),
        StreamObject("", PageText),
        Font,
        FormObject("7 0 R"),
        "<< /Fm0 8 0 R >>",
    });

    /// <summary>Page 2 reaches the shared form under two names and draws it under both.</summary>
    private static byte[] SecondPageAliasingTheSharedFormPdf()
    {
        const string aliased = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                             + "q /A Do Q q 1 0 0 1 0 60 cm /B Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            Page("5 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >>"),
            Page("6 0 R", "/Resources << /Font << /F1 7 0 R >> "
                        + "/XObject << /A 8 0 R /B 8 0 R >> >>"),
            StreamObject("", PageText),
            StreamObject("", aliased),
            Font,
            FormObject("7 0 R"),
        });
    }

    /// <summary>Two pages, each drawing the same two shared forms.</summary>
    private static byte[] TwoPagesSharingTwoFormsPdf()
    {
        const string bothForms = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                               + "q /Fm0 Do Q q 1 0 0 1 0 60 cm /Fm1 Do Q\n";
        const string resources = "/Resources << /Font << /F1 7 0 R >> "
                               + "/XObject << /Fm0 8 0 R /Fm1 9 0 R >> >>";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            Page("5 0 R", resources),
            Page("6 0 R", resources),
            StreamObject("", bothForms),
            StreamObject("", bothForms),
            Font,
            FormObject("7 0 R"),
            FormObject("7 0 R", "BT /F1 10 Tf 10 10 Td (second form text) Tj ET\n"),
        });
    }

    /// <summary>
    /// Page 1 draws the form directly; page 2 draws a wrapper form that draws the same form.
    /// </summary>
    private static byte[] SharedFormReachedThroughAnotherFormPdf()
    {
        const string viaWrapper = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\nq /Wrap Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            Page("5 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >>"),
            Page("6 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject << /Wrap 9 0 R >> >>"),
            StreamObject("", PageText),
            StreamObject("", viaWrapper),
            Font,
            FormObject("7 0 R"),

            // The wrapper holds no text of its own, so it is not a tagging candidate; it exists
            // only to make page 2 reach Fm0 at depth 1.
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >>",
                "q 1 0 0 1 0 0 cm /Fm0 Do Q\n"),
        });
    }

    /// <summary>
    /// Page 2 draws the shared form directly <b>and</b> through a wrapper that also draws it.
    /// </summary>
    private static byte[] FormDrawnDirectlyAndThroughAWrapperPdf()
    {
        const string bothWays = "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n"
                              + "q /Fm0 Do Q q 1 0 0 1 0 60 cm /Wrap Do Q\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            Page("5 0 R", "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >>"),
            Page("6 0 R", "/Resources << /Font << /F1 7 0 R >> "
                        + "/XObject << /Fm0 8 0 R /Wrap 9 0 R >> >>"),
            StreamObject("", PageText),
            StreamObject("", bothWays),
            Font,
            FormObject("7 0 R"),
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 7 0 R >> /XObject << /Fm0 8 0 R >> >>",
                "q 1 0 0 1 0 0 cm /Fm0 Do Q\n"),
        });
    }

    /// <summary>
    /// Nine pages sharing one form whose decoded content is over a megabyte, so copying it for the
    /// eight pages after the first would exceed the duplication budget.
    /// </summary>
    /// <remarks>
    /// Padded with whitespace rather than with operators: the scanner skips it in one pass, so the
    /// fixture is large in bytes without being slow, and it exercises the budget rather than the
    /// tagger's classification.
    /// </remarks>
    private static byte[] OversizedSharedFormPdf()
    {
        const int PageCount = 9;
        string bulky = FormText + new string(' ', 1_200_000);

        var bodies = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };

        var kids = new StringBuilder();
        for (int i = 0; i < PageCount; i++)
            kids.Append(3 + i).Append(" 0 R ");
        bodies.Add("<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count "
                   + PageCount.ToString(CultureInfo.InvariantCulture) + " >>");

        int firstContent = 3 + PageCount;
        int fontNumber   = firstContent + PageCount;
        int formNumber   = fontNumber + 1;

        for (int i = 0; i < PageCount; i++)
            bodies.Add(Page((firstContent + i).ToString(CultureInfo.InvariantCulture) + " 0 R",
                "/Resources << /Font << /F1 " + fontNumber.ToString(CultureInfo.InvariantCulture)
                + " 0 R >> /XObject << /Fm0 " + formNumber.ToString(CultureInfo.InvariantCulture)
                + " 0 R >> >>"));

        for (int i = 0; i < PageCount; i++) bodies.Add(StreamObject("", PageText));

        bodies.Add(Font);
        bodies.Add(FormObject(fontNumber.ToString(CultureInfo.InvariantCulture) + " 0 R", bulky));

        return BuildPdf(bodies);
    }

    private static string Page(string contentsRef, string extra)
        => "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents " + contentsRef
         + (string.IsNullOrEmpty(extra) ? "" : " " + extra) + " >>";

    // =====================================================================
    //  Helpers
    // =====================================================================

    /// <summary>
    /// Runs the tagger and hands back both its report and its output. Compression is off so a test
    /// can read the spliced content stream as ASCII.
    /// </summary>
    private static (AutoTagResult Result, byte[] Bytes) AutoTag(byte[] pdf)
    {
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        AutoTagResult result = PdfAutoTagger.AutoTag(input, output,
            new AutoTagOptions { CompressRewrittenStreams = false });

        return (result, output.ToArray());
    }

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    /// <summary>The stream a given page's resources reach under <paramref name="name"/>.</summary>
    /// <remarks>
    /// Goes through <see cref="PdfPage.Resources"/>, which resolves the page's own <c>/Resources</c>
    /// when it has one and falls back to the inherited dictionary when it does not — so it answers
    /// "what does this page actually draw" both before and after a page is given its own copy.
    /// </remarks>
    private static PdfStream FormOnPage(PdfDocument doc, int pageIndex, string name)
    {
        PdfDictionary resources = doc.Pages[pageIndex].Resources;
        Assert.NotNull(resources);

        var xobjects = doc.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);

        var form = doc.ResolveObject(xobjects[new PdfName(name)]) as PdfStream;
        Assert.NotNull(form);
        return form;
    }

    private static string Decoded(PdfStream stream)
        => Encoding.ASCII.GetString(stream.GetDecodedData() ?? Array.Empty<byte>());

    private static string Decoded(PdfDocument doc, PdfObject reference)
        => doc.ResolveObject(reference) is PdfStream stream ? Decoded(stream) : string.Empty;

    /// <summary>The integer a possibly-indirect object holds, or −1.</summary>
    private static int IntValue(PdfDocument doc, PdfObject obj)
        => doc.ResolveObject(obj) is PdfNumber number ? number.IntValue : -1;

    /// <summary>
    /// The <c>/ParentTree</c> value for <paramref name="key"/>, or null when the tree declares no
    /// such key — which is what makes a distinct <c>/StructParents</c> mean something.
    /// </summary>
    private static PdfObject ParentTreeEntry(PdfDocument doc, int key)
    {
        var root = doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.StructTreeRoot]) as PdfDictionary;
        var tree = doc.ResolveObject(root?[new PdfName("ParentTree")]) as PdfDictionary;
        var nums = doc.ResolveObject(tree?[new PdfName("Nums")]) as PdfArray;
        if (nums == null) return null;

        for (int i = 0; i + 1 < nums.Count; i += 2)
            if (IntValue(doc, nums[i]) == key) return nums[i + 1];

        return null;
    }

    /// <summary>Assembles a classic-xref PDF; bodies[0] is object 1 and is always the catalog.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

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
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";
}
