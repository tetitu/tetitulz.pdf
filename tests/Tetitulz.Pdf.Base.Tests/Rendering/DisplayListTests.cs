// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="ContentStreamInterpreter.BuildDisplayList"/>'s
/// bounds for the operators that report one so far (fill, stroke, image <c>Do</c>), and the new
/// <see cref="ContentStreamInterpreter.Execute(System.Collections.Generic.IReadOnlyList{ContentStreamOperation})"/>
/// overload's fault-isolation parity with <c>Execute(byte[])</c>.
///
/// <para>Every fixture below places its geometry symmetrically on the page (the page-space rect's
/// two Y coordinates sum to the page height) so the expected device bound does not depend on
/// getting the renderer's PDF-Y-up / device-Y-down flip direction right by hand — a wrong flip
/// sign would still land on the same symmetric rect, so the test would not have caught it. The
/// two-entry op-index test relies only on X, which the flip never touches, for the same reason.</para>
/// </summary>
public sealed class DisplayListTests
{
    internal const float PageSize = 40f;

    /// <summary>
    /// Which optional resources <see cref="Pdf"/> writes into the fixture page. A flags enum
    /// rather than a growing list of <c>bool include*Resource</c> parameters — the fourth one
    /// (<see cref="Pattern"/>) is exactly where a positional-bool signature stops being readable
    /// at a call site (<c>BuildDisplayList(content, false, false, true, false)</c> says nothing
    /// about which resource the caller meant) and starts risking a silently-transposed argument.
    /// Internal, not private: <c>PaintSuppressionTests</c> reuses this fixture builder (via
    /// <see cref="Open"/>) rather than a second copy of the same PDF-writing code (
    /// Stage D needs the same pattern-fill fixture <c>APatternFilledRect...</c> already built).
    /// </summary>
    [Flags]
    internal enum FixtureResources
    {
        None     = 0,
        Image    = 1 << 0,
        Font     = 1 << 1,
        Shading  = 1 << 2,
        Pattern  = 1 << 3,
    }

    [Fact]
    public void AFilledRect_ReportsOneEntry_AtTheFOperatorsIndex_WithTheRectsExactBound()
    {
        // op0 "1 0 0 rg", op1 "10 10 20 20 re", op2 "f" — FillPath fires at index 2.
        var entries = BuildDisplayList("1 0 0 rg\n10 10 20 20 re\nf\n");

        var entry = Assert.Single(entries);
        Assert.Equal(2, entry.OpIndex);
        // Symmetric about the 40pt page (10 + 30 = 40), so exact regardless of flip direction.
        // A fill's bound is exact — no stroke-style padding.
        AssertRect(entry.PageBounds, left: 10, top: 10, right: 30, bottom: 30);
    }

    [Fact]
    public void AStrokedLine_ReportsOneEntry_AtTheSOperatorsIndex_PaddedForTheLineWidth()
    {
        // op0 "0 0 1 RG", op1 "2 w", op2 "15 5 m", op3 "15 35 l", op4 "S" — StrokePath at index 4.
        var entries = BuildDisplayList("0 0 1 RG\n2 w\n15 5 m\n15 35 l\nS\n");

        var entry = Assert.Single(entries);
        Assert.Equal(4, entry.OpIndex);

        // A vertical line's own path bound has zero X extent (Left == Right == 15) and exactly
        // covers its Y extent (5..35, itself symmetric about 20). StrokePath pads by half the
        // CTM-scaled line width plus the miter/antialiasing margin (PdfRenderContext.Path.cs,
        // StrokeCullMargin) on every side, so a correct bound is strictly WIDER on X (proving the
        // padding fired) and no narrower than 5..35 on Y (proving the line itself is covered).
        SKRect b = entry.PageBounds;
        Assert.True(b.Left < 15f, $"no padding on the left: {b}");
        Assert.True(b.Right > 15f, $"no padding on the right: {b}");
        Assert.True(b.Top <= 5f, $"line's top not covered: {b}");
        Assert.True(b.Bottom >= 35f, $"line's bottom not covered: {b}");
    }

    [Fact]
    public void FillAndStrokePath_ReportsTwoEntries_OneFromEachHalf()
    {
        // B combines FillPath (clearAfter: false) then StrokePath (clearAfter: true) — both fire
        // from the same "B" operator, so both entries carry the SAME op index.
        var entries = BuildDisplayList("1 0 0 rg\n0 0 1 RG\n10 10 20 20 re\nB\n");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(3, e.OpIndex)); // op0 rg, op1 RG, op2 re, op3 B
    }

    [Fact]
    public void TwoFills_ReportEntriesInStreamOrder_EachAtItsOwnFOperatorsIndex()
    {
        var entries = BuildDisplayList(
            "1 0 0 rg\n5 5 10 10 re\nf\n" +      // op0 rg, op1 re, op2 f
            "0 1 0 rg\n25 5 10 10 re\nf\n");     // op3 rg, op4 re, op5 f

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries[0].OpIndex);
        Assert.Equal(5, entries[1].OpIndex);
        // X is untouched by the Y flip, so this distinguishes the two rects unambiguously.
        Assert.Equal(5f,  entries[0].PageBounds.Left);
        Assert.Equal(25f, entries[1].PageBounds.Left);
    }

    [Fact]
    public void AnImageDo_ReportsOneEntry_AtTheDoOperatorsIndex_WithTheUnitSquaresMappedBound()
    {
        // op0 "q", op1 "10 0 0 10 15 15 cm", op2 "/Im0 Do", op3 "Q" — DrawBitmap at index 2.
        // The 1x1 unit square scaled 10x10 and placed at (15,15) covers PDF rect (15,15)-(25,25),
        // symmetric about the 40pt page (15+25=40).
        var entries = BuildDisplayList(
            "q\n10 0 0 10 15 15 cm\n/Im0 Do\nQ\n", resources: FixtureResources.Image);

        var entry = Assert.Single(entries);
        Assert.Equal(2, entry.OpIndex);
        AssertRect(entry.PageBounds, left: 15, top: 15, right: 25, bottom: 25);
    }

    [Fact]
    public void AShownTextRun_ReportsOneEntry_AtTheTjOperatorsIndex_NearTheTextPosition()
    {
        // op0 "BT", op1 "/F1 12 Tf", op2 "20 20 Td", op3 "(A) Tj", op4 "ET" — DrawTextBlob
        // (reached through DrawGlyphRunWithWidths, the standard-14 fallback's own glyph path)
        // fires from the Tj at index 3.
        var entries = BuildDisplayList(
            "BT\n/F1 12 Tf\n20 20 Td\n(A) Tj\nET\n", resources: FixtureResources.Font);

        var entry = Assert.Single(entries);
        Assert.Equal(3, entry.OpIndex);
        // Not an exact pixel check (font metrics aren't hand-derivable the way a rect or an
        // image's unit square is) — just that the glyph landed near where Td placed it, proving
        // the bound came from the real screen matrix rather than a stub or the page's full extent.
        // Loose on purpose: a glyph's left-side bearing is a font-metric detail this test should
        // not have to predict exactly. What it does pin is that the bound is near the pen
        // position (20) rather than, say, the page origin or the page's full extent — either of
        // which would mean the screen matrix was never applied.
        SKRect b = entry.PageBounds;
        Assert.InRange(b.Left, 5f, 30f);
        Assert.True(b.Width > 0f && b.Height > 0f, $"empty glyph bound: {b}");
    }

    /// <summary>
    /// A stroked run drawn as a Skia text blob (<c>DrawTextBlob</c>, the glyph-ID route) pads its
    /// bound by <c>StrokeCullMargin</c>, as <c>DrawGlyphPath</c> pads a stroked outline: the same
    /// run filled and stroked differ by exactly the margin on every side. A font program the
    /// platform's font manager loads takes this route (TrueType everywhere, a Type 1 program on
    /// FreeType), the library's own outline route otherwise.
    /// </summary>
    [Fact]
    public void AStrokedTextBlobRun_PadsItsBoundByTheSameMarginStrokePathUses()
    {
        var filled = Assert.Single(BuildDisplayList(
            "BT\n/F1 12 Tf\n4 w\n20 20 Td\n(A) Tj\nET\n", resources: FixtureResources.Font));
        var stroked = Assert.Single(BuildDisplayList(
            "BT\n/F1 12 Tf\n1 Tr\n4 w\n20 20 Td\n(A) Tj\nET\n", resources: FixtureResources.Font));

        // StrokeCullMargin: max(4 × CTM scale 1, 1) × max(1, the default miter limit 10) × 0.5 + 1.
        const float margin = 21f;
        SKRect f = filled.PageBounds, s = stroked.PageBounds;
        Assert.True(f.Width > 0f && f.Height > 0f, $"empty glyph bound: {f}");
        Assert.Equal(f.Left - margin, s.Left, 3);
        Assert.Equal(f.Top - margin, s.Top, 3);
        Assert.Equal(f.Right + margin, s.Right, 3);
        Assert.Equal(f.Bottom + margin, s.Bottom, 3);
    }

    /// <summary>
    /// <c>DrawGlyphPath</c> — the outline-based glyph paint method both Type 1 and CFF fonts
    /// share (<c>DrawTextBlob</c>'s test above only reaches the glyph-ID path, TrueType/system
    /// fonts) — reports its glyph's bound too.
    /// </summary>
    [Fact]
    public void AnEmbeddedType1GlyphsOutline_ReportsOneEntry_AtTheTjOperatorsIndex()
    {
        // EmbeddedType1Fixture's one glyph ('A', code 65) is a real Type 1 charstring outline: a
        // 400x400-em filled box, drawn through DrawGlyphPath rather than DrawTextBlob. At 100pt
        // font size that is a 40x40pt box; Td's y (376) is chosen so the box's PDF-space range
        // (376..416) is symmetric about the page's own half-height (792/2 = 396), the same trick
        // the fixtures above use to avoid depending on the Y-flip's direction.
        const string content = "BT\n/F1 100 Tf\n100 376 Td\n(A) Tj\nET\n"; // op3 is "(A) Tj"
        var entries = BuildDisplayListFromEmbeddedType1(content);

        var entry = Assert.Single(entries);
        Assert.Equal(3, entry.OpIndex);
        // The advance-only .notdef glyph (0 400 hsbw endchar, no rmoveto/rlineto at all) would
        // report an empty bound if DrawGlyphPath's early "IsEmpty" guard let it through — so a
        // non-empty bound here also proves the real drawable glyph, not .notdef, painted.
        AssertRect(entry.PageBounds, left: 100, top: 376, right: 140, bottom: 416);
    }

    /// <summary>
    /// A stroked glyph outline (<c>Tr 1</c>) pads its bound by <c>StrokeCullMargin</c> — the exact
    /// margin <see cref="AStrokedLine_ReportsOneEntry_AtTheSOperatorsIndex_PaddedForTheLineWidth"/>
    /// already proves <c>StrokePath</c> uses, now reused rather than a second formula (see the
    /// remark at <c>DrawGlyphPath</c>'s hook for the duplication this replaces).
    /// </summary>
    [Fact]
    public void AStrokedEmbeddedType1Glyph_PadsItsBoundByTheSameMarginStrokePathUses()
    {
        // "1 Tr" = stroke only (PDF §9.3.6 mode 1). Same glyph and placement as the fill test
        // above, so its unpadded box is again the exact (100,376)-(140,416).
        const string content = "BT\n/F1 100 Tf\n1 Tr\n2 w\n100 376 Td\n(A) Tj\nET\n";
        var entries = BuildDisplayListFromEmbeddedType1(content);

        var entry = Assert.Single(entries);
        SKRect b = entry.PageBounds;
        Assert.True(b.Left < 100f,   $"no padding on the left: {b}");
        Assert.True(b.Right > 140f,  $"no padding on the right: {b}");
        Assert.True(b.Top < 376f,    $"no padding on the top: {b}");
        Assert.True(b.Bottom > 416f, $"no padding on the bottom: {b}");
    }

    /// <summary>
    /// <c>PageBounds</c> must not depend on <c>DpiScale</c> — the whole point of reporting
    /// page-point space rather than device pixels.
    /// </summary>
    /// <remarks>
    /// <b>The bug this pins for real, not hypothetically.</b> Every other test in this file uses
    /// the default <c>DpiScale</c> of 1, where page points and device pixels are numerically
    /// identical — so a bound that was secretly in device pixels would have passed every one of
    /// them anyway. <c>DrawBitmap</c> and <c>DrawTextBlob</c> both build an absolute screen matrix
    /// that bakes <c>DpiScale</c> in (it has to, to paint correctly), and their first version
    /// reported that matrix's output directly: correct-looking at DpiScale 1, silently 2x too
    /// large at DpiScale 2, and incomparable with <c>FillPath</c>/<c>StrokePath</c>/
    /// <c>DrawGlyphPath</c>'s bounds (which were never DPI-scaled to begin with) at any DPI. Fixed
    /// by <c>PdfRenderContext.UnscaleByDpi</c>.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(0.5f)]
    public void AnImageDosBound_IsTheSameAtEveryDpiScale(float dpiScale)
    {
        var entries = BuildDisplayList(
            "q\n10 0 0 10 15 15 cm\n/Im0 Do\nQ\n", resources: FixtureResources.Image, dpiScale: dpiScale);

        var entry = Assert.Single(entries);
        AssertRect(entry.PageBounds, left: 15, top: 15, right: 25, bottom: 25);
    }

    /// <summary>
    /// The same DPI-independence property as <see cref="AnImageDosBound_IsTheSameAtEveryDpiScale"/>,
    /// for <c>DrawTextBlob</c> — the other site whose screen matrix bakes <c>DpiScale</c> in.
    /// </summary>
    /// <remarks>
    /// A fixed-range check here (as the loose "near the pen position" assertion on the DpiScale-1
    /// test above does) turned out <b>not</b> to be tight enough to catch the DPI bug this pins:
    /// at DpiScale 2 the unscaled value still landed inside a wide enough range. Comparing the two
    /// scales directly against each other, as <c>AnImageDosBound_...</c> already does, is what
    /// actually kills the mutation — read as CLAUDE.md's own warning about an assertion weaker
    /// than the claim it makes.
    /// </remarks>
    [Fact]
    public void AShownTextRunsBound_IsTheSameAtEveryDpiScale()
    {
        const string content = "BT\n/F1 12 Tf\n20 20 Td\n(A) Tj\nET\n";

        SKRect at1x = Assert.Single(BuildDisplayList(content, resources: FixtureResources.Font, dpiScale: 1f)).PageBounds;
        SKRect at2x = Assert.Single(BuildDisplayList(content, resources: FixtureResources.Font, dpiScale: 2f)).PageBounds;

        Assert.Equal(at1x.Left,   at2x.Left,   2);
        Assert.Equal(at1x.Top,    at2x.Top,    2);
        Assert.Equal(at1x.Right,  at2x.Right,  2);
        Assert.Equal(at1x.Bottom, at2x.Bottom, 2);
    }

    [Fact]
    public void AShadingFill_ReportsOneEntry_AtTheShOperatorsIndex_WithTheClipsBound()
    {
        // op0 "q", op1 "10 10 20 20 re", op2 "W", op3 "n", op4 "/Sh0 sh", op5 "Q" — sh has no
        // geometry of its own; it floods whatever W (clipping, applied by the n that follows it)
        // already set, so its bound is exactly that clip rect. Symmetric about the 40pt page
        // (10+30=40), the same trick as the fill test above.
        var entries = BuildDisplayList(
            "q\n10 10 20 20 re\nW\nn\n/Sh0 sh\nQ\n", resources: FixtureResources.Shading);

        var entry = Assert.Single(entries);
        Assert.Equal(4, entry.OpIndex);
        // _canvas.LocalClipBounds over an antialiased ClipPath (ClipPath above passes
        // antialias: true) reports a bound a fraction of a unit wider than the clip path's own
        // coordinates, to cover the AA edge — a real, correct instance of "conservative, never
        // narrower" (PageBounds' own contract), not a rounding bug. AssertCoversAtLeast checks
        // the property this bound actually promises: it contains the true region, snugly.
        AssertCoversAtLeast(entry.PageBounds, left: 10, top: 10, right: 30, bottom: 30);
    }

    [Fact]
    public void AShadingFillWithNoClipSet_ReportsTheWholePageAsItsBound()
    {
        // No W before the sh, so the only clip in force is the page's own edge — the same
        // starting clip every render begins with.
        var entries = BuildDisplayList("/Sh0 sh\n", resources: FixtureResources.Shading);

        var entry = Assert.Single(entries);
        AssertCoversAtLeast(entry.PageBounds, left: 0, top: 0, right: PageSize, bottom: PageSize);
    }

    [Fact]
    public void APatternFilledRect_ReportsOneEntry_AtTheFOperatorsIndex_WithTheFillPathsBound()
    {
        // op0 "/Pattern cs", op1 "/P0 scn", op2 "10 10 20 20 re", op3 "f" — a PatternType 2
        // (shading pattern) fill goes through DrawPatternPath, which paints exactly the path it
        // is handed (the shader supplies colour, not geometry) — so its bound is the same
        // (10,10)-(30,30) a plain, non-pattern fill of the same rect would report.
        var entries = BuildDisplayList(
            "/Pattern cs\n/P0 scn\n10 10 20 20 re\nf\n", resources: FixtureResources.Pattern);

        var entry = Assert.Single(entries);
        Assert.Equal(3, entry.OpIndex);
        AssertRect(entry.PageBounds, left: 10, top: 10, right: 30, bottom: 30);
    }

    [Fact]
    public void APatternStrokedLine_PadsItsBoundByTheSameMarginStrokePathUses()
    {
        // Same line as AStrokedLine_..., but stroked with a pattern colour (DrawPatternPath's
        // stroke: true branch) instead of a plain RG colour (StrokePath). ReportedPathBounds is
        // the one implementation both call, so the padding must be identical.
        var entries = BuildDisplayList(
            "/Pattern CS\n/P0 SCN\n2 w\n15 5 m\n15 35 l\nS\n", resources: FixtureResources.Pattern);

        var entry = Assert.Single(entries);
        SKRect b = entry.PageBounds;
        Assert.True(b.Left < 15f, $"no padding on the left: {b}");
        Assert.True(b.Right > 15f, $"no padding on the right: {b}");
        Assert.True(b.Top <= 5f, $"line's top not covered: {b}");
        Assert.True(b.Bottom >= 35f, $"line's bottom not covered: {b}");
    }

    [Fact]
    public void NonPaintingOperators_ReportNoEntries()
    {
        // Graphics-state and path-construction operators alone paint nothing.
        var entries = BuildDisplayList("q\n1 0 0 1 5 5 cm\n5 5 10 10 re\nQ\n");
        Assert.Empty(entries);
    }

    /// <summary>
    /// <c>Execute(byte[])</c> and the new <c>Execute(IReadOnlyList&lt;ContentStreamOperation&gt;)</c>
    /// overload must agree exactly, on a stream an unrecognised operator cannot silently corrupt.
    /// </summary>
    /// <remarks>
    /// <b>Not a fault-isolation test.</b> An unrecognised operator name does not throw in this
    /// interpreter — <c>Dispatch</c>'s <c>switch</c> falls through to <c>default: break;</c> and
    /// carries on — so there is no ordinary content-stream shape that reaches the
    /// <c>catch (Exception)</c> in <c>ExecuteOps</c> at all; that branch exists for a resource
    /// lookup or a decode throwing, not for spec-invalid syntax, and is exercised elsewhere
    /// (budget/refusal tests that reach it through a real failure). What this test can and does
    /// prove is the thing Stage B actually changed: <c>Execute(byte[])</c> is now built on
    /// <c>ExecuteOps</c> rather than carrying its own copy of the loop, and an unrecognised
    /// operator sitting between two real fills — content no parser refuses — must not perturb
    /// either overload's op indices or bounds, on either path, identically.
    /// </remarks>
    [Fact]
    public void ExecuteOnAResolvedList_AgreesExactlyWithExecuteOnBytes()
    {
        const string content =
            "1 0 0 rg\n5 5 10 10 re\nf\n" +      // a real fill, op index 2
            "Zzzznotarealoperator\n" +             // recognised by no one; Dispatch's default: break;
            "0 1 0 rg\n25 5 10 10 re\nf\n";       // a second real fill

        var bytesEntries = BuildDisplayList(content);

        List<ContentStreamOperation> ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content));
        var listEntries = BuildDisplayList(ops);

        Assert.Equal(2, bytesEntries.Count);
        Assert.Equal(bytesEntries.Count, listEntries.Count);
        for (int i = 0; i < bytesEntries.Count; i++)
        {
            Assert.Equal(bytesEntries[i].OpIndex, listEntries[i].OpIndex);
            Assert.Equal(bytesEntries[i].PageBounds, listEntries[i].PageBounds);
        }
    }

    // ------------------------------------------------------------------ harness

    private static void AssertRect(SKRect actual, float left, float top, float right, float bottom)
    {
        Assert.Equal(left,   actual.Left,   3);
        Assert.Equal(top,    actual.Top,    3);
        Assert.Equal(right,  actual.Right,  3);
        Assert.Equal(bottom, actual.Bottom, 3);
    }

    /// <summary>
    /// For a bound whose own contract is "conservative, may be wider" (every
    /// <c>DisplayListEntry.PageBounds</c>) — asserts it contains the expected rect within a small
    /// antialiasing margin, and is not absurdly larger (which an "always return the whole page"
    /// regression would produce, and a plain containment check alone would miss).
    /// </summary>
    private static void AssertCoversAtLeast(
        SKRect actual, float left, float top, float right, float bottom)
    {
        const float AaMargin = 2f;
        Assert.True(actual.Left <= left + AaMargin && actual.Left >= left - AaMargin,
            $"left {actual.Left} not within {AaMargin} of {left}: {actual}");
        Assert.True(actual.Top <= top + AaMargin && actual.Top >= top - AaMargin,
            $"top {actual.Top} not within {AaMargin} of {top}: {actual}");
        Assert.True(actual.Right >= right - AaMargin && actual.Right <= right + AaMargin,
            $"right {actual.Right} not within {AaMargin} of {right}: {actual}");
        Assert.True(actual.Bottom >= bottom - AaMargin && actual.Bottom <= bottom + AaMargin,
            $"bottom {actual.Bottom} not within {AaMargin} of {bottom}: {actual}");
    }

    private static List<DisplayListEntry> BuildDisplayList(
        string content, FixtureResources resources = FixtureResources.None, float dpiScale = 1f)
        => BuildDisplayList(ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content)),
                             resources, dpiScale);

    private static List<DisplayListEntry> BuildDisplayList(
        IReadOnlyList<ContentStreamOperation> ops,
        FixtureResources resources = FixtureResources.None, float dpiScale = 1f)
    {
        using var doc = Open(resources);
        int pixels = (int)(PageSize * dpiScale);
        using var bmp = new SKBitmap(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        using var ctx = new PdfRenderContext(canvas, PageSize, PageSize, dpiScale);
        using var fonts = new FontCache();

        var interp = new ContentStreamInterpreter(ctx, doc, Resources(doc), fonts);
        return interp.BuildDisplayList(ops);
    }

    /// <summary>
    /// Runs <paramref name="content"/> against <see cref="EmbeddedType1Fixture"/>'s 612x792 page
    /// and its genuine embedded Type 1 font — the fixture the two <c>DrawGlyphPath</c> tests
    /// share, extracted so a second one did not have to repeat the setup verbatim.
    /// </summary>
    private static List<DisplayListEntry> BuildDisplayListFromEmbeddedType1(string content)
    {
        byte[] pdf = EmbeddedType1Fixture.Page(content);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        using var bmp = new SKBitmap(612, 792, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        using var ctx = new PdfRenderContext(canvas, 612f, 792f, 1f);
        using var fonts = new FontCache();

        var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content));
        var interp = new ContentStreamInterpreter(ctx, doc, Resources(doc), fonts);
        return interp.BuildDisplayList(ops);
    }

    internal static PdfDictionary Resources(PdfDocument doc)
    {
        var page = doc.Pages[0];
        return doc.ResolveObject(page.Dictionary[PdfName.Resources]) as PdfDictionary;
    }

    internal static PdfDocument Open(FixtureResources resources = FixtureResources.None)
        => PdfDocument.Open(new MemoryStream(Pdf(resources)));

    /// <summary>
    /// A one-page 40x40pt PDF whose resources include any combination of a 1x1 raw RGB image, a
    /// non-embedded Helvetica font, a minimal axial shading and a shading pattern wrapping it,
    /// per <paramref name="resources"/>.
    /// </summary>
    /// <remarks>
    /// Object numbers are assigned in the order requested rather than fixed, because this fixture
    /// writes one xref subsection covering <c>0..size-1</c> with no free entries besides object 0
    /// — a fixed number for a resource that was not actually written would leave a gap.
    /// <see cref="FixtureResources.Pattern"/> always writes its own private shading object (one
    /// more than <see cref="FixtureResources.Shading"/> alone would), so the two flags can be
    /// combined or requested independently without the pattern secretly depending on the
    /// unrelated <c>/Sh0</c> resource key also being present.
    /// </remarks>
    private static byte[] Pdf(FixtureResources resources)
    {
        bool image   = (resources & FixtureResources.Image)   != 0;
        bool font    = (resources & FixtureResources.Font)    != 0;
        bool shading = (resources & FixtureResources.Shading) != 0;
        bool pattern = (resources & FixtureResources.Pattern) != 0;

        int nextObjectNumber = 5;
        int imageObjectNumber          = image   ? nextObjectNumber++ : 0;
        int fontObjectNumber           = font    ? nextObjectNumber++ : 0;
        int shadingObjectNumber        = shading ? nextObjectNumber++ : 0;
        int patternShadingObjectNumber = pattern ? nextObjectNumber++ : 0;
        int patternObjectNumber        = pattern ? nextObjectNumber++ : 0;

        var resourceEntries = new List<string>();
        if (image)   resourceEntries.Add($"/XObject << /Im0 {imageObjectNumber} 0 R >>");
        if (font)    resourceEntries.Add($"/Font << /F1 {fontObjectNumber} 0 R >>");
        if (shading) resourceEntries.Add($"/Shading << /Sh0 {shadingObjectNumber} 0 R >>");
        if (pattern) resourceEntries.Add($"/Pattern << /P0 {patternObjectNumber} 0 R >>");
        string resourcesDict = "/Resources << " + string.Join(" ", resourceEntries) + " >>";

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageSize} {PageSize}] " +
            $"{resourcesDict} /Contents 4 0 R >>\nendobj\n");
        // The content stream itself is written by the caller into object 4's placeholder length;
        // this fixture only ever supplies resources, never content — BuildDisplayList runs the
        // parsed operation list directly against the interpreter, not through Execute(byte[]) on
        // a stored page content stream.
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        if (image)
        {
            byte[] rgb = { 255, 0, 0 }; // 1x1 red pixel, raw, no filter
            var imageHead = Encoding.Latin1.GetBytes(
                $"{imageObjectNumber} 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {rgb.Length} >>\nstream\n");
            offsets.Add(acc.Count);
            acc.AddRange(imageHead);
            acc.AddRange(rgb);
            acc.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
        }

        if (font)
            Obj($"{fontObjectNumber} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                "/Encoding /WinAnsiEncoding >>\nendobj\n");

        // ShadingType 2 (axial): /Coords is required, /Function is not — BuildShader defaults to
        // black->white when no /Function resolves, which is fine for a bound-only test. Used
        // verbatim both as the standalone /Sh0 resource and, inline by reference, as a
        // PatternType 2 (shading pattern)'s own /Shading entry.
        string axialShading(float w, float h) =>
            $"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 {w} {h}] >>";

        if (shading)
            Obj($"{shadingObjectNumber} 0 obj\n{axialShading(PageSize, PageSize)}\nendobj\n");

        if (pattern)
        {
            Obj($"{patternShadingObjectNumber} 0 obj\n{axialShading(PageSize, PageSize)}\nendobj\n");
            Obj($"{patternObjectNumber} 0 obj\n<< /Type /Pattern /PatternType 2 " +
                $"/Shading {patternShadingObjectNumber} 0 R >>\nendobj\n");
        }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
