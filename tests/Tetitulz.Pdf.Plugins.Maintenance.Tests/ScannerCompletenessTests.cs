// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Both content scanners feed decisions that destroy data — which glyphs a font program may keep,
/// how many samples an image may lose — so the question these tests ask is never "did the scan find
/// something" but "does the scan say when it could not look". A scan that stops early and reports
/// nothing is indistinguishable downstream from a document that genuinely draws nothing there, and
/// the difference is a file whose glyphs are pruned away or whose images are resampled against a
/// placement that was never the largest.
///
/// <para>The object graph is built directly rather than as PDF bytes: the scanners take
/// <c>Dictionary&lt;int, PdfObject&gt;</c> plus a trailer, so a fixture nesting seventeen form
/// XObjects or carrying a four-megabyte content stream costs a few lines and no parser.</para>
/// </summary>
public class ScannerCompletenessTests
{
    // FontGlyphUsageScanner.MaxDepth and ImagePlacementScanner.MaxFormDepth, which are private.
    private const int MaxDepth = 16;

    // Both scanners bound themselves at 2 000 000 operations and refuse a stream longer than two
    // bytes per remaining operation — the fewest one operation can occupy.
    private const int ParseCeilingBytes = 2_000_000 * 2;

    // ------------------------------------------------------------------
    //  FontGlyphUsageScanner — a refusal to walk must be visible
    // ------------------------------------------------------------------

    /// <summary>
    /// The paired half of the depth test: at a nesting the walk does follow, the innermost form's
    /// glyph is recorded and no reason is set. Without this, the depth test below would pass for a
    /// fixture whose forms were never reachable at all.
    /// </summary>
    [Fact]
    public void FontScan_RecordsAGlyphDrawnByTheDeepestFormItStillFollows()
    {
        var graph = new Graph();
        BuildNestedForms(graph, MaxDepth, "BT /F1 12 Tf <0005> Tj ET");

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
        Assert.True(result.UsedGlyphs.TryGetValue(Graph.FontFileObject, out var glyphs),
            "the fixture never reached the font program at all");
        Assert.Contains(5, glyphs);
    }

    /// <summary>
    /// One level deeper the walk refuses — and refusing silently is what made the Type 3
    /// <c>/CharProcs</c> walk unrepeatable, since those procedures are walked once, on the cache
    /// miss that first analyses the font. A refusal that sets no reason leaves the glyphs the
    /// unwalked stream drew unrecorded, and unrecorded is exactly what gets pruned.
    /// </summary>
    [Fact]
    public void FontScan_ReportsAReasonWhenAStreamIsNestedDeeperThanItWalks()
    {
        var graph = new Graph();
        BuildNestedForms(graph, MaxDepth + 1, "BT /F1 12 Tf <0005> Tj ET");

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("nested", result.Reason, StringComparison.Ordinal);

        // And the glyph the unwalked stream drew is genuinely absent, which is why the reason has
        // to be there: without it the caller would prune glyph 5 away.
        if (result.UsedGlyphs.TryGetValue(Graph.FontFileObject, out var glyphs))
            Assert.DoesNotContain(5, glyphs);
    }

    // ------------------------------------------------------------------
    //  Soft-mask groups — one boolean served two questions
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>A soft-mask group that declares no <c>/Subtype</c> is walked, and its glyphs counted.</b>
    /// </summary>
    /// <remarks>
    /// <para>This is the case that silently deleted glyphs. <c>Do</c> and <c>gs</c> shared one
    /// <c>formOnly</c> flag, and the rule that flag encoded was written for <c>Do</c>: skip
    /// anything that is not <c>/Subtype /Form</c>, silently, because <c>Do</c> may legitimately
    /// name an image and disclaiming that on every page would be noise. Applied to a soft mask it
    /// meant a group with no subtype vanished from the scan <b>with no reason set</b> — so
    /// <c>PdfOptimizer</c> read the scan as complete and <c>TrueTypeGlyphPruner</c> blanked a glyph
    /// the file draws. §11.6.5.2 Table 144 requires that <c>/G</c> to be a form, so a missing
    /// subtype is malformed, and for a scan whose output decides what to delete the conservative
    /// answer is to look.</para>
    /// </remarks>
    [Fact]
    public void FontScan_CountsGlyphsInsideASoftMaskGroupThatDeclaresNoSubtype()
    {
        var graph = new Graph();
        var group = graph.AddStream(Bytes("BT /F1 12 Tf <0009> Tj ET"));
        group.Set(PdfName.Resources, graph.FontResources());
        // No /Subtype at all — malformed, and the shape that used to disappear.

        graph.SetPageContent("/GS0 gs", graph.ResourcesWithSoftMask("GS0", group.Number));

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.True(result.UsedGlyphs.TryGetValue(Graph.FontFileObject, out var glyphs),
            "the soft-mask group's font program was never reached");
        Assert.Contains(9, glyphs);
    }

    /// <summary>
    /// A group declaring some <i>other</i> subtype genuinely has no content stream to walk — so it
    /// is skipped, but the refusal is reported rather than silent.
    /// </summary>
    [Fact]
    public void FontScan_ReportsAReasonWhenASoftMaskGroupIsNotAForm()
    {
        var graph = new Graph();
        var group = graph.AddStream(Bytes("BT /F1 12 Tf <0009> Tj ET"));
        group.Set(PdfName.Subtype, new PdfName("Image"));
        group.Set(PdfName.Resources, graph.FontResources());

        graph.SetPageContent("/GS0 gs", graph.ResourcesWithSoftMask("GS0", group.Number));

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("soft-mask group", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The other half of the split, and the reason the flag could not simply be inverted.</b>
    /// A <c>Do</c> naming an image must stay silent: an image has no content stream, that is the
    /// ordinary case, and a scanner reporting it would disclaim something on nearly every page.
    /// </summary>
    [Fact]
    public void FontScan_IsSilentWhenDoNamesAnImageRatherThanAForm()
    {
        var graph = new Graph();
        var image = graph.AddImage(4, 4);
        graph.SetPageContent("/Im0 Do", graph.ResourcesWith("Im0", image.Number));

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
    }

    /// <summary>
    /// A stream named by <c>Do</c> that declares no subtype cannot be classified as a form to walk
    /// or an image to skip, so it <i>is</i> reported — the opposite default from a soft-mask group,
    /// and the sibling <c>ImagePlacementScanner</c> already answered it this way.
    /// </summary>
    [Fact]
    public void FontScan_ReportsAReasonWhenDoNamesAStreamWithNoSubtype()
    {
        var graph = new Graph();
        var unclassifiable = graph.AddStream(Bytes("BT /F1 12 Tf <0009> Tj ET"));
        unclassifiable.Set(PdfName.Resources, graph.FontResources());

        graph.SetPageContent("/X0 Do", graph.ResourcesWith("X0", unclassifiable.Number));

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("no /Subtype", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A form that invokes itself is refused by the cycle guard, which is correct — and was silent.
    /// The re-entrant invocation runs in the caller's graphics state, so the text it shows need not
    /// be the text the first pass through recorded.
    /// </summary>
    [Fact]
    public void FontScan_ReportsAReasonWhenAFormDrawsItself()
    {
        var graph = new Graph();
        var form = graph.AddStream(Bytes("BT /F1 12 Tf <0005> Tj ET\n/Fm0 Do"));
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.Resources, graph.ResourcesWith("Fm0", form.Number));
        graph.SetPageContent("/Fm0 Do", graph.ResourcesWith("Fm0", form.Number));

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("draws itself", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ContentStreamParser.Parse</c> materialises the whole operation list before the caller sees
    /// any of it — its own doc comment says "check the byte count before calling" — so decrementing
    /// the operator budget per operation bounded nothing: a stream of <c>q Q q Q …</c> within
    /// <c>MaxStreamDecodeBytes</c> allocated millions of operation objects first. The assertion is
    /// on the guard's own evidence — the refusal names the ceiling, and it is deliberately worded
    /// differently from "the operator budget was exhausted", which is what the walk reports when it
    /// really did parse the stream and run out partway through. A test matching only the two words
    /// they share would pass either way, i.e. with this guard deleted.
    /// </summary>
    [Fact]
    public void FontScan_RefusesAnOversizedContentStreamBeforeParsingIt()
    {
        var graph = new Graph();
        graph.SetPageContent(Repeat("q Q ", ParseCeilingBytes + 1), graph.EmptyResources());

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("larger than the remaining operator budget", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream of exactly the ceiling is parsed, so the refusal above is the guard firing at the
    /// size it claims rather than the fixture never arriving. The content is one long comment: the
    /// point is the byte count, and materialising two million operations to prove it would be the
    /// very allocation the guard exists to prevent.
    /// </summary>
    [Fact]
    public void FontScan_ParsesAContentStreamOfExactlyTheCeiling()
    {
        var graph = new Graph();
        graph.SetPageContent(Comment(ParseCeilingBytes), graph.EmptyResources());

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
    }

    /// <summary>
    /// A form re-walked many times is charged for its tokenizing, so the scan refuses once the bytes it
    /// has parsed over all walks pass the budget. The per-operation budget is spent only on parsed
    /// operations, so a form of whitespace parses to nothing and cost nothing — a page of
    /// <c>/Fm0 Do</c> repeated over a large whitespace form re-tokenized it every time, hundreds of
    /// megabytes to a terabyte, for free. The refusal names the tokenizing, not the operator budget.
    /// </summary>
    /// <summary>A content stream of <paramref name="bytes"/> spaces — whitespace, so it parses to no operations.</summary>
    private static byte[] Spaces(int bytes)
    {
        var a = new byte[bytes];
        Array.Fill(a, (byte)0x20);
        return a;
    }

    [Fact]
    public void FontScan_ReportsAReasonWhenAWhitespaceFormIsReWalkedPastTheTokenizeBudget()
    {
        var graph = new Graph();
        var form = graph.AddStream(Spaces(2 * 1024 * 1024));   // 2 MiB of spaces — parses to no operations
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.Resources, graph.EmptyResources());
        // About 275 invocations: 275 x 2 MiB = 550 MiB, past the 512 MiB tokenize budget.
        graph.SetPageContent(Repeat("/Fm0 Do ", 275 * 8), graph.ResourcesWith("Fm0", form.Number));

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("tokenize", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: a whitespace form invoked a few times is walked and sets no reason — the tokenize
    /// budget refuses a hostile re-walk, not an ordinary one.
    /// </summary>
    [Fact]
    public void FontScan_AFormInvokedAFewTimes_IsNotRefused()
    {
        var graph = new Graph();
        var form = graph.AddStream(Spaces(2 * 1024 * 1024));
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.Resources, graph.EmptyResources());
        graph.SetPageContent(Repeat("/Fm0 Do ", 10 * 8), graph.ResourcesWith("Fm0", form.Number));

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
    }

    /// <summary>
    /// Under Identity-H every code is two bytes (§9.7.5.2), so an odd-length string is malformed and
    /// its last byte is a code this scanner cannot read. Readers that zero-pad the remnant draw a
    /// glyph for it; the loop stepped past it in silence, left <c>Reason</c> null, and the
    /// surrounding glyph set was still used to prune — so that glyph was deleted and the character
    /// rendered blank.
    /// </summary>
    [Fact]
    public void FontScan_ReportsAReasonWhenAnIdentityHStringHasAnOddByteLength()
    {
        var graph = new Graph();
        // Ten hex digits: 00 41 00 42 43 — five bytes, so the last has no partner.
        graph.SetPageContent("BT /F1 12 Tf <0041004243> Tj ET", graph.FontResources());

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("odd byte length", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The well-formed string of the same shape scans clean, so the reason above is the odd length
    /// and not the fixture.
    /// </summary>
    [Fact]
    public void FontScan_ReportsNoReasonForAnEvenLengthIdentityHString()
    {
        var graph = new Graph();
        graph.SetPageContent("BT /F1 12 Tf <00410042> Tj ET", graph.FontResources());

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
        Assert.True(result.UsedGlyphs.TryGetValue(Graph.FontFileObject, out var glyphs),
            "the fixture never reached the font program at all");
        Assert.Contains(0x0041, glyphs);
        Assert.Contains(0x0042, glyphs);
    }

    // ------------------------------------------------------------------
    //  ImagePlacementScanner
    // ------------------------------------------------------------------

    /// <summary>
    /// §7.7.3.4 makes <c>/Resources</c> inheritable and the reference renderer and older Word exporters both
    /// hoist it onto the <c>/Pages</c> node. This scanner read <c>page[/Resources]</c> and nothing
    /// else, so on those files every page presented as one with no <c>/XObject</c> dictionary, no
    /// placement was recorded for any image in the document, and <c>TargetDpi</c> silently did
    /// nothing while reporting a completed scan — the same shape of bug the file's own header says
    /// it was written to fix. The sibling font scanner had implemented the inheritance all along.
    /// </summary>
    [Fact]
    public void PlacementScan_SeesAnImageDrawnByAPageThatInheritsItsResources()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.PageTree.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));
        graph.SetPageContent("q 96 0 0 96 0 0 cm /Im0 Do Q", resources: null);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        // 400 pixels across 96 points is 300 dpi.
        Assert.True(placements.TryGetValue(image.Number, out double dpi),
            "no placement was recorded for an image drawn from an inherited /Resources");
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>
    /// A stamp annotation's appearance stream paints on the page, so an image inside it is an image
    /// the document draws. §12.5.5 maps the appearance's <c>/BBox</c> onto the annotation's
    /// <c>/Rect</c>, which is what turns the appearance's own coordinates into the size it is
    /// actually drawn at — measuring against the raw <c>/BBox</c> would report a different
    /// resolution entirely.
    /// </summary>
    [Fact]
    public void PlacementScan_SeesAnImageDrawnByAnAnnotationAppearanceStream()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent("", graph.EmptyResources());

        // The appearance draws the image across its whole 200-unit BBox; the /Rect is 96 points
        // wide, so the BBox is scaled by 96/200 and the image lands on 96 points = 300 dpi.
        var appearance = graph.AddStream(Bytes("q 200 0 0 200 0 0 cm /Im0 Do Q"));
        appearance.Set(PdfName.Subtype, new PdfName("Form"));
        appearance.Set(PdfName.BBox, Rect(0, 0, 200, 200));
        appearance.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));
        graph.AddAnnotationWithAppearance(Rect(10, 10, 106, 106), appearance.Number);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi),
            "no placement was recorded for an image drawn from an annotation appearance");
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>
    /// The same algorithm with a non-identity <c>/Matrix</c>, which is the half of §12.5.5
    /// Algorithm 8.1 the fixture above cannot reach: the four <c>/BBox</c> corners are mapped
    /// through <c>/Matrix</c> <b>before</b> the bounding box is fitted onto <c>/Rect</c>, and
    /// <c>/Matrix</c> stays composed in front of that fit rather than being replaced by it.
    ///
    /// <para><b>Why this exists.</b> This is the fourth body of Algorithm 8.1 in the tree —
    /// <c>PdfPageRenderer</c>, <c>PdfSvgRenderer</c> and <c>PdfFormFlattener</c> carry the other
    /// three — and the consolidation into a shared Core helper cannot happen from inside this
    /// assembly. Until it does, this pins the answer this copy gives, so a later merge has
    /// something to be equal to. The existing fixture leaves <c>/Matrix</c> at identity, under
    /// which dropping the composition, dropping the corner mapping, and swapping the two are all
    /// invisible.</para>
    ///
    /// <para>The numbers: <c>/Matrix</c> turns the appearance 90°, so the 100x50 <c>/BBox</c>
    /// occupies 50 wide by 100 tall before the fit; <c>/Rect</c> is 200x100, so the fit is 4x in x
    /// and 1x in y. The appearance paints the image across its whole <c>/BBox</c>, so the image's
    /// 400 samples across land on 100 points (288 dpi) and its 400 samples down on 200 points
    /// (144 dpi) — <c>Math.Max</c> within one placement keeps 288. The scale is deliberately
    /// non-uniform: with a square <c>/Rect</c> the two matrices commute and composing them the
    /// wrong way round gives the same answer.</para>
    /// </summary>
    [Fact]
    public void PlacementScan_ComposesTheAppearanceMatrixAheadOfTheBBoxToRectFit()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent("", graph.EmptyResources());

        var appearance = graph.AddStream(Bytes("q 100 0 0 50 0 0 cm /Im0 Do Q"));
        appearance.Set(PdfName.Subtype, new PdfName("Form"));
        appearance.Set(PdfName.BBox, Rect(0, 0, 100, 50));
        appearance.Set(PdfName.Matrix, Numbers(0, 1, -1, 0, 0, 0));
        appearance.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));
        graph.AddAnnotationWithAppearance(Rect(0, 0, 200, 100), appearance.Number);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi),
            "no placement was recorded for an image drawn from a rotated annotation appearance");
        Assert.Equal(288.0, dpi, 3);
    }

    /// <summary>
    /// A tiling pattern's content stream paints too. §8.7.3.1 maps its <c>/Matrix</c> onto the
    /// default space of the stream that uses the pattern, not onto the CTM at the <c>scn</c> — the
    /// fixture puts a <c>cm</c> in front of the <c>scn</c> so a walk that composed with the current
    /// CTM would report double the resolution.
    /// </summary>
    [Fact]
    public void PlacementScan_SeesAnImageDrawnByATilingPattern()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);

        var pattern = graph.AddStream(Bytes("q 96 0 0 96 0 0 cm /Im0 Do Q"));
        pattern.Set(PdfName.PatternType, new PdfNumber(1));
        pattern.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));

        var resources = graph.EmptyResources();
        var patterns = new PdfDictionary();
        patterns.Set(new PdfName("P0"), new PdfReference(pattern.Number, 0));
        resources.Set(PdfName.Pattern, patterns);
        graph.SetPageContent("q 2 0 0 2 0 0 cm /Pattern cs /P0 scn Q", resources);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi),
            "no placement was recorded for an image drawn from a tiling pattern");
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>
    /// A Type 3 glyph procedure is a content stream and may paint an image, but the CTM inside one
    /// is the font matrix composed with the text matrix and this scanner tracks neither. It says so
    /// rather than guessing — a fabricated placement is worse than an admitted hole, because the
    /// caller can act on the second.
    /// </summary>
    [Fact]
    public void PlacementScan_ReportsAReasonWhenAType3FontIsSelected()
    {
        var graph = new Graph();
        var proc = graph.AddStream(Bytes("0 0 1 1 d0"));

        var charProcs = new PdfDictionary();
        charProcs.Set(new PdfName("a"), new PdfReference(proc.Number, 0));

        var font = new PdfDictionary();
        font.Set(PdfName.Subtype, new PdfName("Type3"));
        font.Set(PdfName.CharProcs, charProcs);

        var resources = graph.EmptyResources();
        var fonts = new PdfDictionary();
        fonts.Set(new PdfName("T3"), font);
        resources.Set(PdfName.Font, fonts);
        graph.SetPageContent("BT /T3 12 Tf (a) Tj ET", resources);

        ImagePlacementScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions(),
            out string reason);

        Assert.NotNull(reason);
        Assert.Contains("Type 3", reason, StringComparison.Ordinal);
    }

    /// <summary>The parse ceiling again, on the other scanner — the two shared the defect.</summary>
    [Fact]
    public void PlacementScan_RefusesAnOversizedContentStreamBeforeParsingIt()
    {
        var graph = new Graph();
        graph.SetPageContent(Repeat("q Q ", ParseCeilingBytes + 1), graph.EmptyResources());

        ImagePlacementScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions(),
            out string reason);

        Assert.NotNull(reason);
        Assert.Contains("larger than the remaining operator budget", reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  ImagePlacementScanner — boundaries the 2026-10-10 sweep found unpinned
    // ------------------------------------------------------------------

    /// <summary>A content stream exactly at the byte ceiling is walked; the image at its end is recorded.</summary>
    [Fact]
    public void PlacementScan_AContentStreamExactlyAtTheByteCeiling_IsWalked()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent(new string(' ', ParseCeilingBytes - 7) + "/Im0 Do",
            graph.ResourcesWith("Im0", image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.ContainsKey(image.Number),
            "the image at the end of a stream exactly at the ceiling was not recorded");
    }

    /// <summary>One byte over the ceiling is refused before parsing, and the image at the end is not recorded.</summary>
    [Fact]
    public void PlacementScan_AContentStreamOneByteOverTheByteCeiling_IsRefusedWithItsImageUnrecorded()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent(new string(' ', ParseCeilingBytes - 6) + "/Im0 Do",
            graph.ResourcesWith("Im0", image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.NotNull(reason);
        Assert.Contains("larger than the remaining operator budget", reason, StringComparison.Ordinal);
        Assert.False(placements.ContainsKey(image.Number));
    }

    /// <summary>
    /// The operator budget spent to exactly zero: a form that uses all but one operation leaves the
    /// page's next image unwalked, because the scan had no budget left to reach it.
    /// </summary>
    [Fact]
    public void PlacementScan_ABudgetSpentToZero_StopsBeforeTheNextOperation()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        var form = graph.AddStream(Bytes(Repeat("q ", 2 * (2_000_000 - 2))));
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.Resources, graph.EmptyResources());
        graph.SetPageContent("/Fm0 Do /Im0 Do", ResourcesWithForm(graph, form.Number, image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.NotNull(reason);
        Assert.Contains("budget", reason, StringComparison.Ordinal);
        Assert.False(placements.ContainsKey(image.Number));
    }

    /// <summary>The control for the budget test: one operation fewer and the page's image is walked.</summary>
    [Fact]
    public void PlacementScan_ABudgetWithOneOperationLeft_WalksTheNextOperation()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        var form = graph.AddStream(Bytes(Repeat("q ", 2 * (2_000_000 - 4))));
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.Resources, graph.EmptyResources());
        graph.SetPageContent("/Fm0 Do /Im0 Do", ResourcesWithForm(graph, form.Number, image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out _);

        Assert.True(placements.ContainsKey(image.Number),
            "the image was not recorded although the budget still allowed it");
    }

    private static PdfDictionary ResourcesWithForm(Graph graph, int formNumber, int imageNumber)
    {
        var resources = graph.ResourcesWith("Im0", imageNumber);
        ((PdfDictionary)resources[PdfName.XObject]).Set(new PdfName("Fm0"), new PdfReference(formNumber, 0));
        return resources;
    }

    /// <summary>A <c>Q</c> with no matching <c>q</c> does not stop the walk.</summary>
    [Fact]
    public void PlacementScan_AQWithNoMatchingQ_LeavesTheScanRunning()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent("Q q 96 0 0 96 0 0 cm /Im0 Do Q", graph.ResourcesWith("Im0", image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi),
            "an unmatched Q stopped the walk before the image it preceded");
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>A Type 3 font selected by a malformed <c>Tf</c> (one operand) still reports the hole.</summary>
    [Fact]
    public void PlacementScan_AMalformedTfNamingAType3Font_StillReportsIt()
    {
        var graph = new Graph();
        var proc = graph.AddStream(Bytes("0 0 1 1 d0"));
        var charProcs = new PdfDictionary();
        charProcs.Set(new PdfName("a"), new PdfReference(proc.Number, 0));
        var font = new PdfDictionary();
        font.Set(PdfName.Subtype, new PdfName("Type3"));
        font.Set(PdfName.CharProcs, charProcs);
        var resources = graph.EmptyResources();
        var fonts = new PdfDictionary();
        fonts.Set(new PdfName("T3"), font);
        resources.Set(PdfName.Font, fonts);
        graph.SetPageContent("/T3 Tf", resources);

        ImagePlacementScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions(),
            out string reason);

        Assert.NotNull(reason);
        Assert.Contains("Type 3", reason, StringComparison.Ordinal);
    }

    /// <summary>An image painted from a form XObject by <c>Do</c> is walked.</summary>
    [Fact]
    public void PlacementScan_SeesAnImageInAFormXObjectDrawnByDo()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        var form = graph.AddStream(Bytes("/Im0 Do"));
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));
        graph.SetPageContent("q 96 0 0 96 0 0 cm /Fm0 Do Q",
            ResourcesWithForm(graph, form.Number, image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi),
            "no placement was recorded for an image inside a form XObject");
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>A form XObject whose <c>/Matrix</c> has exactly six numbers has it applied.</summary>
    [Fact]
    public void PlacementScan_AFormMatrixOfExactlySixNumbers_IsApplied()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        var form = graph.AddStream(Bytes("/Im0 Do"));
        form.Set(PdfName.Subtype, new PdfName("Form"));
        form.Set(PdfName.Matrix, Numbers(96, 0, 0, 96, 0, 0));
        form.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));
        graph.SetPageContent("/Fm0 Do", ResourcesWithForm(graph, form.Number, image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi),
            "no placement was recorded for an image inside a form with a six-number /Matrix");
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>A form nested exactly at the depth cap is still walked.</summary>
    [Fact]
    public void PlacementScan_AFormNestedExactlyAtTheDepthCap_IsWalked()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        BuildImageForms(graph, MaxDepth, image.Number);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.ContainsKey(image.Number),
            "an image at the deepest nesting the scan follows was not recorded");
    }

    /// <summary>One level deeper the scan refuses with a reason, and records nothing from there.</summary>
    [Fact]
    public void PlacementScan_AFormNestedOneLevelPastTheDepthCap_IsRefusedWithAReason()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        BuildImageForms(graph, MaxDepth + 1, image.Number);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.NotNull(reason);
        Assert.Contains("nested", reason, StringComparison.Ordinal);
        Assert.False(placements.ContainsKey(image.Number));
    }

    // Forms nested formDepth deep, the innermost painting the image. Built inside-out.
    private static void BuildImageForms(Graph graph, int formDepth, int imageNumber)
    {
        PdfStreamEntry inner = null;
        for (int level = formDepth; level >= 1; level--)
        {
            var form = graph.AddStream(Bytes(inner == null ? "/Im0 Do" : "/Fm0 Do"));
            form.Set(PdfName.Subtype, new PdfName("Form"));
            form.Set(PdfName.Resources, inner == null
                ? graph.ResourcesWith("Im0", imageNumber)
                : graph.ResourcesWith("Fm0", inner.Number));
            inner = form;
        }
        graph.SetPageContent("/Fm0 Do", graph.ResourcesWith("Fm0", inner.Number));
    }

    /// <summary>An image with no pixel width is not placed; a valid image beside it is.</summary>
    [Fact]
    public void PlacementScan_AnImageWithZeroPixelWidth_IsNotPlaced()
    {
        var graph = new Graph();
        var empty = graph.AddImage(0, 400);
        var image = graph.AddImage(400, 400);
        var resources = graph.ResourcesWith("Im0", empty.Number);
        ((PdfDictionary)resources[PdfName.XObject]).Set(new PdfName("Im1"), new PdfReference(image.Number, 0));
        graph.SetPageContent("q 96 0 0 96 0 0 cm /Im0 Do /Im1 Do Q", resources);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.False(placements.ContainsKey(empty.Number));
        Assert.True(placements.TryGetValue(image.Number, out double dpi));
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>A painted extent of exactly 0.0001 is degenerate and not placed; the next size up is.</summary>
    [Fact]
    public void PlacementScan_AnImageExactlyAtTheDegenerateExtent_IsNotPlaced()
    {
        var graph = new Graph();
        var thin = graph.AddImage(400, 400);
        var image = graph.AddImage(400, 400);
        var resources = graph.ResourcesWith("Im0", thin.Number);
        ((PdfDictionary)resources[PdfName.XObject]).Set(new PdfName("Im1"), new PdfReference(image.Number, 0));
        graph.SetPageContent("q 0.0001 0 0 96 0 0 cm /Im0 Do Q q 96 0 0 96 0 0 cm /Im1 Do Q", resources);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.False(placements.ContainsKey(thin.Number));
        Assert.True(placements.TryGetValue(image.Number, out double dpi));
        Assert.Equal(300.0, dpi, 3);
    }

    /// <summary>An image drawn twice keeps its largest placement, which is the lower dpi.</summary>
    [Fact]
    public void PlacementScan_AnImageDrawnTwice_KeepsTheLowerDpi()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent(
            "q 96 0 0 96 0 0 cm /Im0 Do Q q 400 0 0 400 0 0 cm /Im0 Do Q",
            graph.ResourcesWith("Im0", image.Number));

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi));
        Assert.Equal(72.0, dpi, 3);
    }

    /// <summary>An appearance whose box is exactly 0.0001 wide is refused, with a reason.</summary>
    [Fact]
    public void PlacementScan_AnAppearanceBoxExactlyTheDegenerateWidth_IsRefusedWithAReason()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent("", graph.EmptyResources());

        var appearance = graph.AddStream(Bytes("q 0.0001 0 0 100 0 0 cm /Im0 Do Q"));
        appearance.Set(PdfName.Subtype, new PdfName("Form"));
        appearance.Set(PdfName.BBox, Rect(0, 0, 0.0001, 100));
        appearance.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));
        graph.AddAnnotationWithAppearance(Rect(0, 0, 96, 96), appearance.Number);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.NotNull(reason);
        Assert.Contains("/Rect and /BBox", reason, StringComparison.Ordinal);
        Assert.False(placements.ContainsKey(image.Number));
    }

    /// <summary>
    /// A small box fitted onto a larger rectangle takes the rectangle-over-box scale with no fudge
    /// term. The 0.001-unit box makes a 0.0001 fudge a 10% change, which the three-place tolerance
    /// elsewhere would hide. The image is square, so this one test kills the x and the y variant alike.
    /// </summary>
    [Fact]
    public void PlacementScan_AnAppearanceBoxFittedOntoItsRect_UsesTheExactScale()
    {
        var graph = new Graph();
        var image = graph.AddImage(400, 400);
        graph.SetPageContent("", graph.EmptyResources());

        var appearance = graph.AddStream(Bytes("q 0.001 0 0 0.001 0 0 cm /Im0 Do Q"));
        appearance.Set(PdfName.Subtype, new PdfName("Form"));
        appearance.Set(PdfName.BBox, Rect(0, 0, 0.001, 0.001));
        appearance.Set(PdfName.Resources, graph.ResourcesWith("Im0", image.Number));
        graph.AddAnnotationWithAppearance(Rect(0, 0, 96, 96), appearance.Number);

        var placements = ImagePlacementScanner.Scan(graph.Objects, graph.Trailer,
            new OptimizationOptions(), out string reason);

        Assert.Null(reason);
        Assert.True(placements.TryGetValue(image.Number, out double dpi));
        Assert.Equal(300.0, dpi, 6);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static string Repeat(string unit, int atLeastBytes)
    {
        var builder = new StringBuilder(atLeastBytes + unit.Length);
        while (builder.Length < atLeastBytes) builder.Append(unit);
        return builder.ToString();
    }

    /// <summary>A content stream of exactly <paramref name="bytes"/> bytes holding no operations.</summary>
    private static string Comment(int bytes) => "%" + new string('x', bytes - 2) + "\n";

    private static PdfArray Rect(double x0, double y0, double x1, double y1)
    {
        var array = new PdfArray();
        array.Add(new PdfNumber(x0));
        array.Add(new PdfNumber(y0));
        array.Add(new PdfNumber(x1));
        array.Add(new PdfNumber(y1));
        return array;
    }

    /// <summary>A numeric array — a form <c>/Matrix</c> in the order §8.3.3 writes it.</summary>
    private static PdfArray Numbers(params double[] values)
    {
        var array = new PdfArray();
        foreach (double value in values) array.Add(new PdfNumber(value));
        return array;
    }

    /// <summary>
    /// Page 1 selects /F1 at the innermost of <paramref name="formDepth"/> nested form XObjects, so
    /// the text is shown at exactly that depth and nowhere shallower.
    /// </summary>
    private static void BuildNestedForms(Graph graph, int formDepth, string innermost)
    {
        var fontResources = graph.FontResources();

        // Built inside-out: the deepest form holds the text, each outer one invokes the next.
        PdfStreamEntry inner = null;
        for (int level = formDepth; level >= 1; level--)
        {
            string content = inner == null ? innermost : "/Fm0 Do";
            var form = graph.AddStream(Bytes(content));
            form.Set(PdfName.Subtype, new PdfName("Form"));
            form.Set(PdfName.Resources,
                inner == null ? fontResources : graph.ResourcesWith("Fm0", inner.Number));
            inner = form;
        }

        graph.SetPageContent("/Fm0 Do", graph.ResourcesWith("Fm0", inner.Number));
    }

    private sealed class PdfStreamEntry
    {
        internal PdfStream Stream;
        internal int Number;

        internal void Set(PdfName key, PdfObject value) => Stream.Set(key, value);
    }

    // ------------------------------------------------------------------
    //  A font an ExtGState selects (§8.4.5 Table 58) — Wave 22, Maintenance-1:7 and 3:2
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>/F1</c> is selected by <c>Tf</c>, then <c>/GS0</c>'s <c>/Font</c> selects a second font whose
    /// program is object 60, and a glyph is shown. Both renderers draw it in the second font, so its
    /// program records the glyph and the first's does not. Read past, the glyph went to object 4's
    /// program and object 60 recorded nothing, which the pruner reads as "nothing used".
    /// </summary>
    [Fact]
    public void FontScan_RecordsAGlyphUnderTheFontAGraphicsStateSelects()
    {
        var graph = new Graph();
        PdfDictionary resources = graph.FontResources();
        // The second font is /F1's shape over another program.
        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.FontFile2, new PdfReference(60, 0));
        var descendant = new PdfDictionary();
        descendant.Set(PdfName.Subtype, new PdfName("CIDFontType2"));
        descendant.Set(PdfName.FontDescriptor, descriptor);
        descendant.Set(PdfName.CIDToGIDMap, new PdfName("Identity"));
        var descendants = new PdfArray();
        descendants.Add(descendant);
        var second = new PdfDictionary();
        second.Set(PdfName.Subtype, new PdfName("Type0"));
        second.Set(PdfName.Encoding, new PdfName("Identity-H"));
        second.Set(PdfName.DescendantFonts, descendants);
        graph.Objects[60] = new PdfStream { RawData = Array.Empty<byte>() };
        graph.Objects[61] = second;
        resources.Set(PdfName.ExtGState, GraphicsStates("GS0", new PdfReference(61, 0)));
        graph.SetPageContent("BT /F1 1 Tf /GS0 gs <0007> Tj ET", resources);

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
        Assert.True(result.UsedGlyphs.TryGetValue(60, out var selected), "the selected font's program recorded nothing");
        Assert.Contains(7, selected);
        Assert.False(result.UsedGlyphs.TryGetValue(Graph.FontFileObject, out var first) && first.Contains(7));
    }

    /// <summary>A <c>gs</c> naming a state that does not resolve could select any font, so the scan says so.</summary>
    [Fact]
    public void FontScan_RefusesAGraphicsStateItCannotResolve()
    {
        var graph = new Graph();
        graph.SetPageContent("BT /F1 1 Tf /Missing gs <0007> Tj ET", graph.FontResources());

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("graphics state /Missing could not be resolved", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>The control: a state with no <c>/Font</c> leaves the font as it was.</summary>
    [Fact]
    public void FontScan_KeepsTheFontAcrossAGraphicsStateThatSetsNone()
    {
        var graph = new Graph();
        PdfDictionary resources = graph.FontResources();
        resources.Set(PdfName.ExtGState, GraphicsStates("GS0", null));
        graph.SetPageContent("BT /F1 1 Tf /GS0 gs <0007> Tj ET", resources);

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
        Assert.True(result.UsedGlyphs.TryGetValue(Graph.FontFileObject, out var glyphs));
        Assert.Contains(7, glyphs);
    }

    /// <summary>
    /// A Type 3 font a graphics state selects runs its glyph procedures, which may paint an image the
    /// page content never names. The image scan does not walk those procedures, so it reports the hole —
    /// and the unused-XObject pass, which rides on it, keeps the image (it removed it).
    /// </summary>
    [Fact]
    public void ImageScan_ReportsAType3FontAGraphicsStateSelects_AndTheImageItPaintsIsKept()
    {
        var graph = new Graph();
        var image = graph.AddImage(4, 4);
        var proc = graph.AddStream(Bytes("1000 0 d0 /Im0 Do"));
        var procs = new PdfDictionary();
        procs.Set(new PdfName("a"), new PdfReference(proc.Number, 0));
        var type3 = new PdfDictionary();
        type3.Set(PdfName.Type, PdfName.Font);
        type3.Set(PdfName.Subtype, new PdfName("Type3"));
        type3.Set(PdfName.CharProcs, procs);
        graph.Objects[70] = type3;

        PdfDictionary resources = graph.ResourcesWith("Im0", image.Number);
        resources.Set(PdfName.ExtGState, GraphicsStates("GS0", new PdfReference(70, 0)));
        type3.Set(PdfName.Resources, resources);
        graph.SetPageContent("BT /GS0 gs <00> Tj ET", resources);

        ImagePlacementScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions(), out string reason);
        int removed = UnusedXObjectResources.Remove(graph.Objects, graph.Trailer, new OptimizationOptions(), out string refusal);

        Assert.NotNull(reason);
        Assert.Contains("is a Type 3 font", reason, StringComparison.Ordinal);
        Assert.Equal(0, removed);
        Assert.NotNull(refusal);
        Assert.NotNull(((PdfDictionary)resources[PdfName.XObject])[new PdfName("Im0")]);
    }

    // ------------------------------------------------------------------
    //  The interactive form blocker fails closed — Wave 22, Maintenance-1:8
    // ------------------------------------------------------------------

    /// <summary>
    /// A field below the walk's depth was skipped with no reason, and a field there may hold a value a
    /// viewer draws from <c>/DA</c>. Eighteen levels are refused; the control, ten, is walked whole.
    /// </summary>
    [Theory]
    [InlineData(18, true)]
    [InlineData(10, false)]
    public void FormBlocker_RefusesAFieldTreeDeeperThanItWalks(int levels, bool refused)
    {
        var graph = new Graph();
        PdfObject child = null;
        for (int level = levels; level > 0; level--)
        {
            var field = new PdfDictionary();
            field.Set(new PdfName("T"), new PdfString("f" + level));
            if (child != null)
            {
                var kids = new PdfArray();
                kids.Add(child);
                field.Set(PdfName.Kids, kids);
            }
            graph.Objects[200 + level] = field;
            child = new PdfReference(200 + level, 0);
        }
        SetFields(graph, child);
        graph.SetPageContent("q Q", graph.EmptyResources());

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        if (refused)
        {
            Assert.NotNull(result.Reason);
            Assert.Contains("nests more than 16 levels", result.Reason, StringComparison.Ordinal);
        }
        else Assert.Null(result.Reason);
    }

    /// <summary>A field the object map lacks may be one with a value: the scan says so rather than skip it.</summary>
    [Fact]
    public void FormBlocker_RefusesAFieldItCannotRead()
    {
        var graph = new Graph();
        SetFields(graph, new PdfReference(299, 0));
        graph.SetPageContent("q Q", graph.EmptyResources());

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("(object 299) could not be read", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A widget a page lists but <c>/Fields</c> does not reach, with no appearance, whose value its
    /// parent holds: a viewer draws that value. The field tree's descent never climbs <c>/Parent</c>, so
    /// it was not seen. The control: the same widget with an appearance is drawn from the appearance.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FormBlocker_ReadsAPageWidgetsInheritedValue(bool hasAppearance, bool refused)
    {
        var graph = new Graph();
        var parent = new PdfDictionary();
        parent.Set(new PdfName("T"), new PdfString("name"));
        parent.Set(PdfName.V, new PdfString("Müller"));
        graph.Objects[300] = parent;
        var widget = new PdfDictionary();
        widget.Set(PdfName.Subtype, new PdfName("Widget"));
        widget.Set(PdfName.Parent, new PdfReference(300, 0));
        if (hasAppearance)
        {
            var ap = new PdfDictionary();
            ap.Set(new PdfName("N"), new PdfReference(graph.AddStream(Bytes("")).Number, 0));
            widget.Set(PdfName.AP, ap);
        }
        var annots = new PdfArray();
        annots.Add(widget);
        SetFields(graph, null);
        graph.SetPageContent("q Q", graph.EmptyResources());
        ((PdfDictionary)graph.Objects[3]).Set(PdfName.Annots, annots);

        var result = FontGlyphUsageScanner.Scan(graph.Objects, graph.Trailer, new OptimizationOptions());

        if (refused)
        {
            Assert.NotNull(result.Reason);
            Assert.Contains("has a value (/V) but no appearance stream", result.Reason, StringComparison.Ordinal);
        }
        else Assert.Null(result.Reason);
    }

    /// <summary>The catalog's <c>/AcroForm</c> with a <c>/Fields</c> of the one entry (empty when null).</summary>
    private static void SetFields(Graph graph, PdfObject field)
    {
        var fields = new PdfArray();
        if (field != null) fields.Add(field);
        var form = new PdfDictionary();
        form.Set(PdfName.Fields, fields);
        ((PdfDictionary)graph.Objects[1]).Set(PdfName.AcroForm, form);
    }

    /// <summary>An <c>/ExtGState</c> of one state, <paramref name="name"/>, whose <c>/Font</c> is <c>[font 12]</c> (none when null).</summary>
    private static PdfDictionary GraphicsStates(string name, PdfReference font)
    {
        var gs = new PdfDictionary();
        gs.Set(new PdfName("LW"), new PdfNumber(1));
        if (font != null)
        {
            var entry = new PdfArray();
            entry.Add(font);
            entry.Add(new PdfNumber(12));
            gs.Set(PdfName.Font, entry);
        }
        var states = new PdfDictionary();
        states.Set(new PdfName(name), gs);
        return states;
    }

    /// <summary>
    /// A minimal object graph: catalog (1), page tree (2), page (3), then whatever a test adds.
    /// Object 4 is always the <c>/FontFile2</c> both font fixtures prune against.
    /// </summary>
    private sealed class Graph
    {
        internal const int FontFileObject = 4;

        internal readonly Dictionary<int, PdfObject> Objects = new Dictionary<int, PdfObject>();
        internal readonly PdfDictionary Trailer = new PdfDictionary();
        internal readonly PdfDictionary PageTree = new PdfDictionary();

        private readonly PdfDictionary _page = new PdfDictionary();
        private int _next = 5;

        internal Graph()
        {
            var catalog = new PdfDictionary();
            catalog.Set(PdfName.Type, new PdfName("Catalog"));
            catalog.Set(PdfName.Pages, new PdfReference(2, 0));
            Objects[1] = catalog;

            PageTree.Set(PdfName.Type, new PdfName("Pages"));
            var kids = new PdfArray();
            kids.Add(new PdfReference(3, 0));
            PageTree.Set(PdfName.Kids, kids);
            Objects[2] = PageTree;

            _page.Set(PdfName.Type, new PdfName("Page"));
            Objects[3] = _page;

            // The font program itself is never read by the scanner — only its object number is —
            // so an empty stream is enough to make it a resolvable /FontFile2 target.
            Objects[FontFileObject] = new PdfStream { RawData = Array.Empty<byte>() };

            Trailer.Set(PdfName.Root, new PdfReference(1, 0));
        }

        internal PdfStreamEntry AddStream(byte[] data)
        {
            var stream = new PdfStream { RawData = data };
            int number = _next++;
            Objects[number] = stream;
            return new PdfStreamEntry { Stream = stream, Number = number };
        }

        internal PdfStreamEntry AddImage(int width, int height)
        {
            var entry = AddStream(Array.Empty<byte>());
            entry.Set(PdfName.Subtype, new PdfName("Image"));
            entry.Set(PdfName.Width, new PdfNumber(width));
            entry.Set(PdfName.Height, new PdfNumber(height));
            return entry;
        }

        internal void SetPageContent(string content, PdfDictionary resources)
        {
            var stream = AddStream(Bytes(content));
            _page.Set(PdfName.Contents, new PdfReference(stream.Number, 0));
            if (resources != null) _page.Set(PdfName.Resources, resources);
        }

        internal void AddAnnotationWithAppearance(PdfArray rect, int appearanceObject)
        {
            var appearances = new PdfDictionary();
            appearances.Set(new PdfName("N"), new PdfReference(appearanceObject, 0));

            var annot = new PdfDictionary();
            annot.Set(PdfName.Rect, rect);
            annot.Set(PdfName.AP, appearances);

            var annots = new PdfArray();
            annots.Add(annot);
            _page.Set(PdfName.Annots, annots);
        }

        internal PdfDictionary EmptyResources() => new PdfDictionary();

        /// <summary>
        /// A <c>/Resources</c> whose <c>/ExtGState</c> maps <paramref name="name"/> onto a
        /// luminosity soft mask whose <c>/G</c> is the given object — the route <c>gs</c> takes.
        /// </summary>
        internal PdfDictionary ResourcesWithSoftMask(string name, int groupObject)
        {
            var smask = new PdfDictionary();
            smask.Set(new PdfName("S"), new PdfName("Luminosity"));
            smask.Set(new PdfName("G"), new PdfReference(groupObject, 0));

            var gs = new PdfDictionary();
            gs.Set(PdfName.SMask, smask);

            var group = new PdfDictionary();
            group.Set(new PdfName(name), gs);

            var resources = new PdfDictionary();
            resources.Set(PdfName.ExtGState, group);
            return resources;
        }

        /// <summary>A <c>/Resources</c> whose <c>/XObject</c> maps <paramref name="name"/> onto an object.</summary>
        internal PdfDictionary ResourcesWith(string name, int objectNumber)
        {
            var group = new PdfDictionary();
            group.Set(new PdfName(name), new PdfReference(objectNumber, 0));

            var resources = new PdfDictionary();
            resources.Set(PdfName.XObject, group);
            return resources;
        }

        /// <summary>
        /// /F1: a Type0/Identity-H font over a CIDFontType2 descendant whose /FontFile2 is object 4,
        /// which is the one shape <see cref="FontGlyphUsageScanner"/> maps.
        /// </summary>
        internal PdfDictionary FontResources()
        {
            var descriptor = new PdfDictionary();
            descriptor.Set(PdfName.FontFile2, new PdfReference(FontFileObject, 0));

            var descendant = new PdfDictionary();
            descendant.Set(PdfName.Subtype, new PdfName("CIDFontType2"));
            descendant.Set(PdfName.FontDescriptor, descriptor);
            descendant.Set(PdfName.CIDToGIDMap, new PdfName("Identity"));

            var descendants = new PdfArray();
            descendants.Add(descendant);

            var font = new PdfDictionary();
            font.Set(PdfName.Subtype, new PdfName("Type0"));
            font.Set(PdfName.Encoding, new PdfName("Identity-H"));
            font.Set(PdfName.DescendantFonts, descendants);

            var fonts = new PdfDictionary();
            fonts.Set(new PdfName("F1"), font);

            var resources = new PdfDictionary();
            resources.Set(PdfName.Font, fonts);
            return resources;
        }
    }
}
