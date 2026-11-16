// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The accessibility findings the 2026 assembly scan (plan 10) raised at LOW.
/// </summary>
/// <remarks>
/// Two of the seven are not here and are not oversights. The <c>PageContentRewriter.StructParents</c>
/// doc comment ("Not present in <c>PdfName</c>'s constant set, so it is interned here once", of a
/// field initialised <c>= PdfName.StructParents</c>) is a comment-only correction with no
/// observable behaviour, so nothing here could fail for it. And
/// <c>PdfReadingOrderAnalyzer</c>'s eager <c>TryExtractStructuredText</c> is labelled an unmeasured
/// performance hypothesis whose own remedy begins "Benchmark both … before and after", which is
/// the one thing a test in this file cannot stand in for.
/// </remarks>
public class AssemblyScanLowFollowUpTests
{
    // =====================================================================
    //  1. A form inside a NON-artifact sequence is still the page's content
    // =====================================================================

    /// <summary>
    /// A page with no <c>/StructParents</c> whose content is <c>/OC /MC0 BDC  /Fm0 Do  EMC</c>,
    /// where <c>Fm0</c> opens a structural sequence, is reported under UA18.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>DescendIntoForm</c> was told
    /// <c>enclosedByArtifact: artifactDepth &gt; 0 || markedStack.Count &gt; 0</c> — a parameter
    /// asserting "artifact" over a condition meaning "inside any BDC". An <c>/OC</c> wrapper is a
    /// non-artifact sequence naming no MCID, so the page's own
    /// <c>StructuralMarkedSequences</c> stayed 0, the form's were counted into a scratch
    /// <c>PageInfo</c> that the roll-up discards, and <c>CheckPageStructParents</c> hit
    /// <c>if (StructuralMarkedSequences == 0 &amp;&amp; UnmarkedTextShowingOperators == 0) continue;</c>
    /// and said nothing. UA14's reverse orphan check skips such a page too (it invokes a form), so
    /// nothing else caught it: a page really holding a content item no <c>/StructParents</c> key
    /// can address passed in silence.</para>
    /// </remarks>
    [Fact]
    public void Ua18_StructuralSequenceInsideAFormUnderAnOcWrapper_IsAttributedToThePage()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(FormUnderOptionalContentPdf());

        AccessibilityIssue ua18 = Assert.Single(issues.Where(i => i.Code == "UA18"));
        Assert.Equal(IssueSeverity.Error, ua18.Severity);
        Assert.Equal(1, ua18.PageNumber);
        Assert.Contains("marked-content sequence(s) naming an /MCID", ua18.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The artifact case is unchanged, and it is the reason the two conditions had to be split
    /// rather than merged the other way: a form drawn inside <c>/Artifact BDC</c> is artifact
    /// content, its sequences are deliberately unreachable from the structure tree (§14.8.2), and
    /// the page is <b>not</b> accused of holding untracked structural content.
    /// </summary>
    [Fact]
    public void Ua18_StructuralSequenceInsideAFormUnderAnArtifact_IsStillNotAttributedToThePage()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(FormUnderArtifactPdf());

        Assert.DoesNotContain(issues, i => i.Code == "UA18");
    }

    /// <summary>
    /// A form with a <c>/StructParents</c> of its own keys its content items through that entry
    /// (ISO 32000-1 §14.7.4.4), so they do not make the page that draws it need one (the published profile's
    /// 7.20-t02-pass-a, which UA18 failed); the same form without the key still does.
    /// </summary>
    [Fact]
    public void Ua18_StructuralSequenceInsideAFormWithItsOwnStructParents_IsNotAttributedToThePage()
    {
        Assert.DoesNotContain(DeepCheck(FormEnclosedByPdf("OC", "/StructParents 0 ")), i => i.Code == "UA18");
        Assert.Single(DeepCheck(FormEnclosedByPdf("OC")).Where(i => i.Code == "UA18"));
    }

    /// <summary>
    /// UA20 exempts a Type 0 font over a published Adobe collection, whatever its CMap (ISO 14289-1
    /// 7.21.7; the published profile's 7.21.7-t01-pass-a, Identity-H over Adobe-Japan1 with no <c>/ToUnicode</c>) — the
    /// plugin's UA26 reads the same function — and still reports one over Adobe-Identity.
    /// </summary>
    [Theory]
    [InlineData("Japan1", false)]
    [InlineData("Korea1", false)]
    [InlineData("Identity", true)]
    public void Ua20_ATypeZeroFontOverAPublishedCollection_IsExempt(string ordering, bool reported)
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (ja) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("BT /F1 12 Tf 72 700 Td <0001> Tj ET\n"),
            "<< /Type /Font /Subtype /Type0 /BaseFont /K /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /K /CIDSystemInfo << /Registry (Adobe) /Ordering (" + ordering + ") /Supplement 0 >> " +
            "/FontDescriptor << /Type /FontDescriptor /FontName /K /Flags 4 >> >>",
        });
        bool ua20 = DeepCheck(pdf).Any(i => i.Code == "UA20" && i.Severity == IssueSeverity.Error);
        Assert.Equal(reported, ua20);
    }

    /// <summary>
    /// Nor may the split make the form's text look unmarked: the sequence the page had open really
    /// does enclose it (§14.6.1), so UA17 must stay silent. Without the seeded marked-content frame
    /// this fixture would trade one false negative for a false positive.
    /// </summary>
    [Fact]
    public void Ua17_TextInsideAFormUnderAnOcWrapper_IsNotReportedAsUnmarked()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(FormUnderOptionalContentPdf());

        Assert.DoesNotContain(issues, i => i.Code == "UA17");
    }

    // =====================================================================
    //  2. The concatenation ceiling must fit the buffer it sizes
    // =====================================================================

    /// <summary>
    /// A ceiling above <c>int.MaxValue</c> — which the public
    /// <c>AutoTagOptions.MaxConcatenatedContentBytes</c> is a <c>long</c> precisely to allow, and
    /// which its documentation invites — is clamped to <c>int.MaxValue</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> The assembled buffer is <c>new byte[(int)total]</c>, commented "the
    /// ceiling above guarantees it fits an int" — true only of the 512 MiB default. At 4 831 838 216
    /// the cast yields 536 870 920 and at 4 294 967 303 it yields 7, so the buffer is allocated far
    /// smaller than the parts and <c>Buffer.BlockCopy</c> throws out of a method contracted to
    /// report failure by returning null. From <c>MeasureFontSizesFromContentStreams</c> that
    /// exception is not caught per page: it escapes to <c>AutoTag</c>'s outermost handler and the
    /// whole document is copied through untagged rather than one page skipped with a reason.</para>
    /// <para>The arithmetic is the defect, so the arithmetic is what is asserted. A fixture cannot
    /// reach the truncating cast without materialising more than two gigabytes.</para>
    /// </remarks>
    [Theory]
    [InlineData(6L * 1024 * 1024 * 1024)]    // the value the option's own doc suggests
    [InlineData(4_831_838_216L)]             // truncates to 536 870 920
    [InlineData(4_294_967_303L)]             // truncates to 7
    [InlineData(long.MaxValue)]
    public void ConcatenationCeilingAboveIntMaxValue_IsClamped(long requested)
    {
        Assert.Equal(int.MaxValue, ContentStreamTagger.NormaliseConcatenationCeiling(requested));
    }

    /// <summary>
    /// Everything at or below <c>int.MaxValue</c> is passed through untouched, and zero or less
    /// still means "use the default" — the clamp must not become a second, quieter ceiling.
    /// </summary>
    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(1024L, 1024L)]
    [InlineData((long)int.MaxValue, int.MaxValue)]
    public void ConcatenationCeilingWithinRange_IsUnchanged(long requested, long expected)
    {
        Assert.Equal(expected, ContentStreamTagger.NormaliseConcatenationCeiling(requested));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ConcatenationCeilingOfZeroOrLess_MeansTheDefault(long requested)
    {
        Assert.Equal(ContentStreamTagger.MaxConcatenatedContentBytes,
                     ContentStreamTagger.NormaliseConcatenationCeiling(requested));
    }

    // =====================================================================
    //  3. A failed Commit must publish nothing
    // =====================================================================

    /// <summary>
    /// When a repoint fails part-way through <see cref="SharedFormDuplicator.Commit"/>, neither the
    /// tagged original nor any copy reaches the caller's edit sets.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>Commit</c> wrote each copy into <c>modifiedObjects</c> /
    /// <c>newObjects</c> before attempting its repoint and returned false on the first failure
    /// without undoing anything, while <c>TagForms</c> recorded a skip reason and moved on — leaving
    /// the tagged original stamped <c>/StructParents k</c> in the edit set, leaving the copy in it,
    /// and not advancing <c>nextParentTreeKey</c>, so <c>k</c> was issued again to the next form.
    /// The written revision then held two streams claiming one parent-tree entry and the abandoned
    /// form's marked content resolved to another form's structure elements: a wrong tree that
    /// validates, which duplication exists to avoid.</para>
    /// <para>The failure is induced by clearing the second copy's object number after planning.
    /// <c>Plan</c> proves every repoint possible, so nothing a document can contain makes this
    /// happen — which is exactly why the recovery path had never been exercised.</para>
    /// </remarks>
    [Fact]
    public void SharedFormCommit_FailingPartWayThrough_LeavesTheEditSetsEmpty()
    {
        (PdfDocument doc, PdfObjectIndex index, FormUsage form) = PlanFixture();

        FormDuplicationPlan plan = SharedFormDuplicator.Plan(
            doc, index, form, new PageTagOptions(), compressRewrittenStream: false,
            remainingBudget: SharedFormDuplicator.MaxDuplicatedFormBytes);

        Assert.True(plan.Succeeded, plan.SkipReason);
        Assert.Equal(2, plan.Copies.Count);

        // 0 is never a valid object number, so TryRepointXObjectNames refuses — after the copy's
        // stream has already been emitted.
        plan.Copies[1].ObjectNumber = 0;

        var modified = new Dictionary<int, PdfObject>();
        var created  = new Dictionary<int, PdfObject>();

        bool committed = SharedFormDuplicator.Commit(
            doc, index, form, plan, new[] { 7, 8 }, modified, created);

        Assert.False(committed);
        Assert.Empty(modified);
        Assert.Empty(created);

        doc.Dispose();
    }

    /// <summary>
    /// A commit that succeeds still publishes everything: the tagged original under its own object
    /// number, and one new stream per further page. Staging must not have swallowed the good case.
    /// </summary>
    [Fact]
    public void SharedFormCommit_Succeeding_PublishesTheOriginalAndEveryCopy()
    {
        (PdfDocument doc, PdfObjectIndex index, FormUsage form) = PlanFixture();

        FormDuplicationPlan plan = SharedFormDuplicator.Plan(
            doc, index, form, new PageTagOptions(), compressRewrittenStream: false,
            remainingBudget: SharedFormDuplicator.MaxDuplicatedFormBytes);

        Assert.True(plan.Succeeded, plan.SkipReason);

        var modified = new Dictionary<int, PdfObject>();
        var created  = new Dictionary<int, PdfObject>();

        Assert.True(SharedFormDuplicator.Commit(
            doc, index, form, plan, new[] { 7, 8 }, modified, created));

        // The original form (object 5) rewritten in place, plus the second page repointed at its
        // own copy.
        Assert.True(modified.ContainsKey(form.ObjectNumber));
        Assert.Single(created);

        // Each stream carries its own parent-tree key; sharing one is the failure duplication
        // exists to prevent.
        var keys = new List<int>();
        foreach (PdfObject o in modified.Values) AddStructParents(keys, o);
        foreach (PdfObject o in created.Values)  AddStructParents(keys, o);

        Assert.Equal(new[] { 7, 8 }, keys.OrderBy(k => k).ToArray());

        doc.Dispose();

        static void AddStructParents(List<int> into, PdfObject o)
        {
            if (o is PdfStream s && s[new PdfName("StructParents")] is PdfNumber n) into.Add(n.IntValue);
        }
    }

    // =====================================================================
    //  4. /MarkInfo /Marked read through an indirect reference
    // =====================================================================

    /// <summary>
    /// A document whose <c>/MarkInfo</c> is <c>&lt;&lt; /Marked 9 0 R &gt;&gt;</c> with object 9 =
    /// <c>true</c> is already marked, so the metadata step reports no change and appends no
    /// revision.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>IsMarkedTrue</c> tested <c>markInfo?[PdfName.Marked] is
    /// PdfBoolean</c> on the raw indexer result, which does not follow a reference (§7.3.10 — the
    /// rule CLAUDE.md states as "PdfResolve for every dictionary read"). An indirect value read as
    /// absent, so the pass cloned <c>/MarkInfo</c>, set <c>/Marked true</c>, incremented its changed
    /// count and appended a revision that altered nothing — reported to the caller as a change, and
    /// growing the file by one revision on every re-run, against <c>RemediateWithReport</c>'s own
    /// promise that a step changing nothing costs no revision at all.</para>
    /// </remarks>
    [Fact]
    public void Remediate_IndirectMarkedAndDisplayDocTitle_ReportNoChange()
    {
        RemediationStepResult metadata = MetadataStep(IndirectFlagsPdf());

        Assert.False(metadata.Failed, "the metadata step must have run: " + metadata.SkipReason);
        Assert.Equal(0, metadata.ItemsChanged);
    }

    /// <summary>
    /// The bytes agree with the count. This is the half a caller feels: the same input run twice
    /// grew by a revision each time, contradicting <c>RemediateWithReport</c>'s own promise that a
    /// step changing nothing costs no revision at all.
    /// </summary>
    [Fact]
    public void Remediate_IndirectMarkedAndDisplayDocTitle_AppendNoRevision()
    {
        byte[] input = IndirectFlagsPdf();

        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        PdfAccessibility.RemediateWithReport(inMs, outMs, MetadataOnlyOptions());

        Assert.Equal(input, outMs.ToArray());
    }

    /// <summary>
    /// The same document with both flags written <b>directly false</b> really is corrected, so the
    /// tests above are about the indirection and not about the step having been switched off.
    /// </summary>
    [Fact]
    public void Remediate_DirectFalseFlags_AreStillCorrected()
    {
        RemediationStepResult metadata = MetadataStep(DirectFalseFlagsPdf());

        Assert.Equal(2, metadata.ItemsChanged);
    }

    /// <summary>
    /// <c>PdfArtifactMarker.EnsureMarkInfo</c> reads the same flag through its own copy of the
    /// pattern and must agree with <c>IsMarkedTrue</c> about whether it is already set — the two
    /// writers disagreeing is what the shared rule exists to prevent.
    /// </summary>
    [Fact]
    public void MarkHeadersAndFooters_IndirectMarkedTrue_DoesNotRewriteMarkInfo()
    {
        // The page must really have a header to mark: EnsureMarkInfo is reached only when at least
        // one page changed, so a fixture with nothing in the band would never execute the code
        // under test and this assertion could not fail.
        byte[] input = IndirectMarkedWithHeaderPdf();

        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        ArtifactMarkResult marked = PdfAccessibility.MarkHeadersAndFooters(inMs, outMs);
        byte[] output = outMs.ToArray();

        Assert.True(marked.ArtifactsMarked > 0,
            "the fixture must reach EnsureMarkInfo, which runs only for a page that changed");

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var markInfo = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.MarkInfo]) as PdfDictionary;
        Assert.NotNull(markInfo);

        // Untouched: still the document's own indirect reference, not a direct `true` written over
        // it by a pass that could not read what was already there.
        Assert.IsType<PdfReference>(markInfo[PdfName.Marked]);
    }

    // =====================================================================
    //  Drivers
    // =====================================================================

    private static IReadOnlyList<AccessibilityIssue> DeepCheck(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf, writable: false));

    /// <summary>
    /// Every pipeline step off except the metadata one, so what the report attributes to
    /// <c>SetDocumentMetadata</c> is that step alone and no other pass can append a revision.
    /// </summary>
    private static RemediationOptions MetadataOnlyOptions() => new RemediationOptions
    {
        Language            = "en-US",
        Title               = "A Title",
        AutoTagIfUntagged   = false,
        SetFigureAltText    = false,
        FixHeadingHierarchy = false,
        MarkArtifacts       = false,
        FixReadingOrder     = false,
        FixTabOrder         = false,
        SetPdfUaIdentifier  = false,
        EmbedMissingFonts   = false,
    };

    private static RemediationStepResult MetadataStep(byte[] pdf)
    {
        using var inMs  = new MemoryStream(pdf, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report =
            PdfAccessibility.RemediateWithReport(inMs, outMs, MetadataOnlyOptions());

        return Assert.Single(report.Steps.Where(s => s.Name == "SetDocumentMetadata"));
    }

    /// <summary>
    /// Two pages drawing one small Form XObject (object 5) under the name <c>/Fm0</c>, opened and
    /// inventoried exactly as <c>PdfAutoTagger.TagForms</c> would.
    /// </summary>
    private static (PdfDocument Doc, PdfObjectIndex Index, FormUsage Form) PlanFixture()
    {
        PdfDocument doc = PdfDocument.Open(new MemoryStream(TwoPagesSharingOneFormPdf()));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);

        var invocations = new List<PageInvocations>();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            invocations.Add(new PageInvocations
            {
                PageIndex        = i,
                PageObjectNumber = index.GetObjectNumber(doc.Pages[i].Dictionary),
                PageDictionary   = doc.Pages[i].Dictionary,
                Resources        = doc.Pages[i].Resources,
                PageWasReMarked  = true,
                Names            = new List<string> { "Fm0" },
            });
        }

        FormInventory inventory = FormXObjectInventory.Collect(doc, index, invocations, 4 * 1024 * 1024);
        FormUsage form = Assert.Single(inventory.Forms);
        return (doc, index, form);
    }

    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + content + "endstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(2048);
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

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>
    /// One page with <b>no</b> <c>/StructParents</c> whose whole content is one Form XObject
    /// invocation wrapped in the marked-content sequence named by <paramref name="tag"/>. The
    /// wrapper names a property list that resolves and carries no <c>/MCID</c> — a real
    /// <c>/OC</c> block, and the shape that keeps <c>MarkedContentIdsIncomplete</c> false so the
    /// page is judged rather than excused. The structural sequence lives inside the form.
    /// </summary>
    private static byte[] FormEnclosedByPdf(string tag, string formExtra = "") => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Tabs /S /Contents 4 0 R "
        + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> "
        + "/Properties << /MC0 << /Type /OCMD /OCGs [] >> >> >> >>",
        StreamObject("/" + tag + " /MC0 BDC\n/Fm0 Do\nEMC\n"),
        HelveticaFont,
        "<< /Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 5 0 R >> >> " + formExtra
        + "/Length 58 >>\nstream\n/P <</MCID 0>> BDC\nBT /F1 9 Tf 5 5 Td (In form) Tj ET\nEMC\nendstream",
        "<< /Type /StructTreeRoot /K [8 0 R] /ParentTree 9 0 R >>",
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [] >>",
    });

    private static byte[] FormUnderOptionalContentPdf() => FormEnclosedByPdf("OC");
    private static byte[] FormUnderArtifactPdf()        => FormEnclosedByPdf("Artifact");

    /// <summary>Two pages, each with its own text, both drawing the shared form object 5.</summary>
    private static byte[] TwoPagesSharingOneFormPdf()
    {
        const string formContent = "BT /F1 9 Tf 10 10 Td (Letterhead) Tj ET\n";

        return BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",                       // 1
            "<< /Type /Pages /Kids [7 0 R 8 0 R] /Count 2 >>",                       // 2
            HelveticaFont,                                                           // 3
            StreamObject("BT /F1 12 Tf 72 700 Td (Page one) Tj ET\n/Fm0 Do\n"),      // 4
            StreamObject("BT /F1 12 Tf 72 700 Td (Page two) Tj ET\n/Fm0 Do\n"),      // 5
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 3 0 R >> >> "
            + "/Length " + formContent.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + formContent + "endstream",                           // 6 — the form
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 3 0 R >> /XObject << /Fm0 6 0 R >> >> >>", // 7
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R "
            + "/Resources << /Font << /F1 3 0 R >> /XObject << /Fm0 6 0 R >> >> >>", // 8
        });
    }

    /// <summary>
    /// A conformant tagged document that already holds every value the metadata pass would write,
    /// with <c>/MarkInfo /Marked</c> and <c>/ViewerPreferences /DisplayDocTitle</c> each stored as
    /// an <b>indirect</b> boolean — legal under §7.3.10, and read as absent by a bare indexer.
    /// </summary>
    private static byte[] IndirectFlagsPdf() => FlagsPdf("6 0 R", "6 0 R");

    /// <summary>
    /// The same document saying, directly, that it is neither marked nor title-displaying.
    /// </summary>
    private static byte[] DirectFalseFlagsPdf() => FlagsPdf("false", "false");

    private static byte[] FlagsPdf(string marked, string displayDocTitle) => BuildPdfWithInfo(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo 7 0 R /StructTreeRoot 8 0 R "
        + "/ViewerPreferences << /DisplayDocTitle " + displayDocTitle + " >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
        + "/Resources << /Font << /F1 5 0 R >> >> /Tabs /S >>",
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (Body text.) Tj ET\nEMC\n"),
        HelveticaFont,
        "true",                                        // 6 — the shared indirect boolean
        "<< /Marked " + marked + " >>",                // 7
        "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 10 0 R >>",
        "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [0 [9 0 R]] >>",
        "<< /Title (A Title) >>",                      // 11 — the trailer's /Info
    });

    /// <summary>
    /// A document whose <c>/MarkInfo /Marked</c> is an indirect <c>true</c> and whose page draws
    /// <b>unmarked</b> text inside the header band.
    /// </summary>
    /// <remarks>
    /// Both halves are required for the artifact marker to reach <c>EnsureMarkInfo</c>: it runs
    /// only when a page really changed, the tagger declines a page that already carries marked
    /// content, and the <c>/Marked</c> write is gated on the structure tree reaching content — which
    /// is a fact about the tree, so the element's <c>/K [0]</c> satisfies it even though the page
    /// content stream opens no sequence of its own.
    /// </remarks>
    private static byte[] IndirectMarkedWithHeaderPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo 7 0 R /StructTreeRoot 8 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
        + "/Resources << /Font << /F1 5 0 R >> >> /Tabs /S >>",
        StreamObject("BT /F1 10 Tf 72 762 Td (Page 1 of 1) Tj ET\n"
                   + "BT /F1 12 Tf 72 400 Td (Body text.) Tj ET\n"),
        HelveticaFont,
        "true",                                        // 6
        "<< /Marked 6 0 R >>",                         // 7
        "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 10 0 R >>",
        "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [0 [9 0 R]] >>",
    });

    /// <summary>
    /// As <see cref="BuildPdf"/>, with the <b>last</b> body used as the trailer's <c>/Info</c>
    /// dictionary — so the metadata pass finds the title already set and has nothing to write on
    /// that account either.
    /// </summary>
    private static byte[] BuildPdfWithInfo(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(2048);
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
          .Append(" /Root 1 0 R /Info ").Append(bodies.Count.ToString(CultureInfo.InvariantCulture))
          .Append(" 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
