// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The aggregate form-XObject budget on the <b>render</b> path, in both renderers.
/// </summary>
/// <remarks>
/// <para><b>The attack.</b> Take N forms where form <i>i</i>'s content is
/// <c>/F{i+1} Do /F{i+1} Do</c>. No form ever draws itself, so a cycle guard never fires. No path is
/// deeper than N, so <c>PdfLimits.MaxRenderRecursionDepth</c> (24) never fires. And the walk still
/// performs 2^(N-1) invocations — 18 one-line forms, a few kilobytes of PDF, is 131 072 renders of a
/// content stream, each with its own graphics-state push, resource resolve and stream decode.</para>
///
/// <para><b>One budget for every walk of forms.</b>
/// <c>PdfLimits.MaxFormXObjectInvocationsPerPage</c> exists for exactly this doubling chain, and
/// <c>PdfStructuredTextExtractor</c> and both renderers charge against it — so a document that
/// extraction refuses in milliseconds is not rendered to completion.</para>
///
/// <para><b>Why a depth cap cannot substitute.</b> Depth bounds one branch; the cost here is the
/// number of branches. That is the distinction <c>PdfLimits</c>' own remarks draw between
/// <c>MaxFormXObjectDepth</c> ("bounds depth and never total work") and this budget, and it is why
/// the guard is an aggregate counter rather than a deeper cap.</para>
/// </remarks>
public class RenderFormXObjectBudgetTests
{
    /// <summary>
    /// 2^17 = 131 072 invocations, comfortably past the 100 000 budget. Seventeen levels is 65 536
    /// and would sit <i>under</i> it, so this is the minimum that can demonstrate the refusal — do
    /// not lower it to make the test faster.
    /// </summary>
    private const int Levels = 18;

    [Fact]
    public void FormXObjectDoublingChain_IsRefusedByTheAggregateBudget_Raster()
    {
        byte[] pdf = DoublingChainPdf(Levels);

        var reported = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToPng(ms, 0);
        });

        AssertBudgetReported(reported, "raster");
    }

    [Fact]
    public void FormXObjectDoublingChain_IsRefusedByTheAggregateBudget_Svg()
    {
        byte[] pdf = DoublingChainPdf(Levels);

        var reported = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToSvg(ms, 0);
        });

        AssertBudgetReported(reported, "SVG");
    }

    /// <summary>
    /// The two renderers must refuse on the same terms. Every "silently wrong output" defect this
    /// repository has found in rendering was one of them lagging the other, and a budget is exactly
    /// the kind of thing that gets added to one and forgotten in the other.
    /// </summary>
    /// <remarks>
    /// <b>This asserts that both refuse THIS document; it cannot assert that they refuse the same
    /// SET of documents, and it once appeared to.</b> The final <c>Assert.Equal</c> compares two
    /// values that <see cref="BudgetDetail"/> can only ever return as
    /// <c>RenderBudget.RefusalMessage</c> — a constant built from
    /// <c>PdfLimits.MaxFormXObjectInvocationsPerPage</c> — so once the two <c>NotNull</c> calls
    /// pass it is one constant against itself. It is kept because the two <c>NotNull</c> calls are
    /// real (a renderer that did not charge at all would fail them), but it is not the parity
    /// claim. The claim that the two charge the same OPERATIONS lives in
    /// <c>RenderBudgetChargeParityTests</c>, which found raster charging soft-mask and
    /// pattern-cell builds that the SVG writer did not.
    /// </remarks>
    [Fact]
    public void FormXObjectDoublingChain_BothRenderersRefuseIdentically()
    {
        byte[] pdf = DoublingChainPdf(Levels);

        var raster = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToPng(ms, 0);
        });

        var svg = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToSvg(ms, 0);
        });

        string rasterDetail = BudgetDetail(raster);
        string svgDetail    = BudgetDetail(svg);

        Assert.NotNull(rasterDetail);
        Assert.NotNull(svgDetail);
        Assert.Equal(rasterDetail, svgDetail);
    }

    /// <summary>
    /// The floor. A guard that refuses legitimate input is a worse defect than the one it closes, so
    /// an ordinary page drawing an ordinary form must render its ink.
    /// </summary>
    /// <remarks>
    /// Asserts a <b>decoded pixel</b> rather than "a PNG came back": a blank page encodes to a
    /// perfectly valid non-empty PNG, so a byte-length assertion here would pass with the whole
    /// form-XObject path deleted.
    /// </remarks>
    [Fact]
    public void OrdinaryFormInvocation_IsNotRefusedByTheBudget()
    {
        byte[] pdf = SingleFormPdf();

        var reported = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, dpi: 72);

            // The form paints a black rectangle over most of the page. Find one dark pixel: that is
            // proof the fixture reached the form-XObject path at all.
            Assert.True(HasDarkPixel(page),
                "The form's ink never reached the bitmap, so this fixture does not exercise the " +
                "form-XObject path and the 'not refused' claim below is vacuous.");
        });

        Assert.DoesNotContain(reported, d =>
            d.Detail != null &&
            d.Detail.IndexOf("form-XObject invocation", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // ==================================================================
    //  A Type 3 glyph procedure is an invocation too
    // ==================================================================

    /// <summary>
    /// A Type 3 glyph that shows four glyphs of its own font is the doubling chain inside a font: no
    /// name repeats along a branch (it is one glyph), the depth cap stops every branch at 24, and
    /// 4^24 branches start. The structure fuzzer's first finding (GAPS.md §9, 2026-10-16): every
    /// generated <c>font-recursion</c> file, the ten-glyph one included, hung both renderers for the
    /// whole 120-second window. A glyph procedure is now charged to the page's invocation budget as
    /// a form is, in both renderers, and the refusal is the budget's.
    /// </summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void Type3GlyphShowingFourOfItsOwnFont_IsRefusedByTheAggregateBudget(string renderer)
    {
        byte[] pdf = Type3FanOutPdf(fanOut: 4);

        var reported = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            if (renderer == "raster") PdfOperations.RenderPageToPng(ms, 0);
            else PdfOperations.RenderPageToSvg(ms, 0);
        });

        AssertBudgetReported(reported, renderer);
    }

    /// <summary>
    /// The floor for the glyph charge: the same font showing <b>one</b> glyph of itself is a chain
    /// of 24 invocations, stopped by the depth cap and nowhere near the budget. Its ink is drawn and
    /// the budget says nothing — a charge that refused this would refuse every compound glyph.
    /// </summary>
    [Fact]
    public void Type3GlyphShowingOneOfItsOwnFont_IsNotRefusedByTheBudget()
    {
        byte[] pdf = Type3FanOutPdf(fanOut: 1);

        var reported = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            RenderedPage page = PdfPageRenderer.RenderPageToPixels(ms, 0, dpi: 72);
            Assert.True(HasDarkPixel(page),
                "The glyph's ink never reached the bitmap, so this fixture does not exercise the " +
                "Type 3 path and the 'not refused' claim below is vacuous.");
        });

        Assert.DoesNotContain(reported, d =>
            d.Detail != null &&
            d.Detail.IndexOf("form-XObject invocation", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// Ten glyphs of a Type 3 font whose one glyph procedure fills its square and shows
    /// <paramref name="fanOut"/> glyphs of the same font, through the font's own <c>/Resources</c>.
    /// </summary>
    private static byte[] Type3FanOutPdf(int fanOut)
    {
        string shown = new string('A', fanOut);
        return Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R " +
                   "/Resources << /Font << /T3 5 0 R >> >> >>"),
            StreamObj(4, "", "BT /T3 50 Tf 10 10 Td (AAAAAAAAAA) Tj ET\n"),
            Obj(5, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] " +
                   "/CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /a] >> " +
                   "/FirstChar 65 /LastChar 65 /Widths [100] /Resources << /Font << /T3 5 0 R >> >> >>"),
            StreamObj(6, "", "100 0 0 0 100 100 d1 0 0 100 100 re f BT /T3 1 Tf (" + shown + ") Tj ET\n"),
        });
    }

    // ------------------------------------------------------------------ assertions

    private static void AssertBudgetReported(
        IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> reported, string which)
    {
        string detail = BudgetDetail(reported);

        Assert.True(detail != null,
            $"The {which} renderer performed a 2^{Levels - 1} form-XObject fan-out without reporting " +
            $"a budget refusal. PdfLimits.MaxFormXObjectInvocationsPerPage " +
            $"({PdfLimits.MaxFormXObjectInvocationsPerPage:N0}) exists for this exact document and " +
            "is charged by the text extractor; neither renderer charges it, so a few kilobytes of " +
            "PDF buys an unbounded render. Diagnostics seen: " +
            (reported.Count == 0
                ? "(none)"
                : string.Join(" | ", reported.Select(r => $"[{r.Kind}] {r.Detail}"))));
    }

    /// <summary>The detail of the budget refusal, or null when none was reported.</summary>
    private static string BudgetDetail(IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> reported)
    {
        string wanted = PdfLimits.MaxFormXObjectInvocationsPerPage.ToString("N0", CultureInfo.InvariantCulture);

        foreach (var r in reported)
        {
            if (r.Detail == null) continue;
            if (r.Detail.IndexOf(wanted, StringComparison.Ordinal) >= 0 &&
                r.Detail.IndexOf("form-XObject", StringComparison.OrdinalIgnoreCase) >= 0)
                return r.Detail;
        }
        return null;
    }

    /// <summary>
    /// Collects render diagnostics raised <b>on the calling thread only</b>.
    /// </summary>
    /// <remarks>
    /// <c>RenderDiagnostics.OnEvent</c> is process-wide, so an unfiltered handler turns any
    /// assertion about the collection into a claim about the whole test run — a pre-existing flake
    /// class in this suite. The handler is held in a local so the same delegate instance can be
    /// removed again; unsubscribing a fresh lambda leaks the handler onto the static event and
    /// pollutes every later test.
    /// </remarks>
    internal static IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> CaptureDiagnostics(Action act)
    {
        int thread = Environment.CurrentManagedThreadId;
        var reported = new List<(RenderDiagnosticKind, string)>();

        Action<RenderDiagnosticKind, string, Exception> handler = (kind, detail, _) =>
        {
            if (Environment.CurrentManagedThreadId != thread) return;
            lock (reported) reported.Add((kind, detail));
        };

        RenderDiagnostics.OnEvent += handler;
        try { act(); }
        finally { RenderDiagnostics.OnEvent -= handler; }

        return reported;
    }

    private static bool HasDarkPixel(RenderedPage page)
    {
        byte[] px = page.Pixels;
        for (int i = 0; i + 3 < px.Length; i += 4)
            if (px[i] < 80 && px[i + 1] < 80 && px[i + 2] < 80 && px[i + 3] > 200)
                return true;
        return false;
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// <paramref name="levels"/> forms, each invoking the next <b>twice</b>. The page names every
    /// form in its own <c>/Resources</c>, so a form carrying no <c>/Resources</c> inherits them.
    /// </summary>
    // ------------------------------------------------------------------
    //  The OTHER guard: MaxRenderRecursionDepth, which this file's own remarks say the
    //  doubling chain never reaches
    // ------------------------------------------------------------------
    //
    //  The budget counts invocations and the depth cap counts nesting, so a fixture that trips one
    //  says nothing about the other. The doubling chain is 2^17 invocations at depth 18 — refused by
    //  the budget, never by the cap. Its complement is a LINEAR chain: thirty forms, each drawing
    //  the next exactly once, is thirty invocations (against a budget of 100 000) at depth thirty
    //  (against a cap of 24). Only the cap can stop it.
    //
    //  Found by mutation: making the SVG writer's inherited depth stop incrementing left the whole
    //  Base suite green, because nothing in the tree nested a form deeper than 24. The cap guards
    //  against an UNCATCHABLE StackOverflowException, so a guard nothing reaches is worth a fixture.

    /// <summary>Deeper than the cap, and nowhere near the invocation budget.</summary>
    private const int DeepLinearLevels = 30;

    /// <summary>Well inside both, so the chain demonstrably renders.</summary>
    private const int ShallowLinearLevels = 5;

    [Theory]
    [InlineData(ShallowLinearLevels, true)]
    [InlineData(DeepLinearLevels, false)]
    public void LinearFormChain_IsDrawnOnlyWhileInsideTheDepthCap_Svg(int levels, bool expectInnermost)
    {
        using var ms = new MemoryStream(LinearChainPdf(levels));
        string svg = PdfOperations.RenderPageToSvg(ms, 0);

        Assert.Equal(expectInnermost, SvgHasInnermostFill(svg));
    }

    /// <summary>The raster renderer applies the same cap to the same document.</summary>
    [Theory]
    [InlineData(ShallowLinearLevels)]
    [InlineData(DeepLinearLevels)]
    public void LinearFormChain_BothRenderersAgree(int levels)
    {
        using var svgStream = new MemoryStream(LinearChainPdf(levels));
        bool svgDrew = SvgHasInnermostFill(PdfOperations.RenderPageToSvg(svgStream, 0));

        using var rasterStream = new MemoryStream(LinearChainPdf(levels));
        byte[] png = PdfOperations.RenderPageToPng(rasterStream, 0);
        bool rasterDrew = PngHasInnermostColour(png);

        Assert.Equal(svgDrew, rasterDrew);
    }

    /// <summary>
    /// Whether the innermost form's fill reached the export. Parsed as XML, never substring-matched.
    /// </summary>
    private static bool SvgHasInnermostFill(string svg)
        => System.Xml.Linq.XDocument.Parse(svg).Descendants()
            .Any(e => string.Equals((string)e.Attribute("fill"), InnermostCss, StringComparison.Ordinal));

    private const string InnermostCss = "rgb(255,0,0)";

    /// <summary>The same question of the raster output, asked of decoded pixels.</summary>
    private static bool PngHasInnermostColour(byte[] png)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Red > 200 && c.Green < 60 && c.Blue < 60) return true;
            }
        return false;
    }

    /// <summary>
    /// <paramref name="levels"/> forms, each drawing the next exactly ONCE; the innermost fills a
    /// red square. Total invocations equal <paramref name="levels"/>, so the aggregate budget is
    /// never in play and the depth cap is the only guard that can refuse it.
    /// </summary>
    private static byte[] LinearChainPdf(int levels)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };

        string res = "<< /XObject << " +
            string.Join(" ", Enumerable.Range(1, levels).Select(i => $"/F{i} {3 + i} 0 R")) +
            " >> >>";

        bodies.Add(Obj(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents " +
            $"{3 + levels + 1} 0 R /Resources {res} >>"));

        for (int i = 1; i <= levels; i++)
        {
            string content = i < levels ? $"/F{i + 1} Do\n" : "1 0 0 rg 0 0 60 60 re f\n";
            bodies.Add(StreamObj(3 + i,
                " /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources " + res, content));
        }

        bodies.Add(StreamObj(3 + levels + 1, "", "/F1 Do\n"));

        return Assemble(bodies);
    }

    private static byte[] DoublingChainPdf(int levels)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };

        string res = "<< /XObject << " +
            string.Join(" ", Enumerable.Range(1, levels).Select(i => $"/F{i} {3 + i} 0 R")) +
            " >> >>";

        bodies.Add(Obj(3,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents " +
            $"{3 + levels + 1} 0 R /Resources {res} >>"));

        for (int i = 1; i <= levels; i++)
        {
            string content = i < levels ? $"/F{i + 1} Do /F{i + 1} Do\n" : "0 0 1 1 re f\n";
            bodies.Add(StreamObj(3 + i, " /Type /XObject /Subtype /Form /BBox [0 0 10 10]", content));
        }

        bodies.Add(StreamObj(3 + levels + 1, "", "/F1 Do\n"));

        return Assemble(bodies);
    }

    /// <summary>One page drawing one form that paints a black rectangle.</summary>
    private static byte[] SingleFormPdf() => Assemble(new List<string>
    {
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
               "/Resources << /XObject << /F1 5 0 R >> >> >>"),
        StreamObj(4, "", "/F1 Do\n"),
        StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200]",
                     "0 g 10 10 180 180 re f\n"),
    });

    // ==================================================================
    //  The budget is per PAGE, and an /AP stream is page content by another route
    // ==================================================================

    /// <summary>
    /// A page whose annotations each carry the doubling chain must exhaust <b>one</b> page budget,
    /// not one budget each.
    /// </summary>
    /// <remarks>
    /// <para>The raster renderer allocated <c>new RenderBudget()</c> <i>inside</i> its
    /// per-annotation loop, so N annotations got N times the allowance the constant names and N
    /// copies of the refusal — while the SVG renderer held exactly one budget per page on
    /// <c>SvgSharedState</c> and shared it with the content stream and every annotation. The
    /// aggregate cap the budget exists to impose was therefore per-annotation on one side and
    /// per-page on the other, so the two refused different documents.</para>
    ///
    /// <para><b>Counting the reports is what separates the two.</b> <c>RenderBudget</c> reports at
    /// most once per instance, so "exactly one report" is a statement about how many budgets
    /// existed. Asserting merely that a refusal was reported passes under both shapes.</para>
    /// </remarks>
    [Fact]
    public void AnnotationAppearances_ShareOnePageBudget_Raster()
    {
        byte[] pdf = AnnotatedDoublingChainPdf(Levels, annotations: 3);

        var reported = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToPng(ms, 0);
        });

        Assert.Single(reported.Where(r => IsBudgetRefusal(r.Detail)));
    }

    /// <summary>The SVG export already behaved this way; asserted so it cannot regress.</summary>
    [Fact]
    public void AnnotationAppearances_ShareOnePageBudget_Svg()
    {
        byte[] pdf = AnnotatedDoublingChainPdf(Levels, annotations: 3);

        var reported = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        });

        Assert.Single(reported.Where(r => IsBudgetRefusal(r.Detail)));
    }

    /// <summary>
    /// And the two agree on the count — the property neither per-renderer assertion states.
    /// </summary>
    [Fact]
    public void AnnotationAppearances_BothRenderersRefuseTheSameNumberOfTimes()
    {
        byte[] pdf = AnnotatedDoublingChainPdf(Levels, annotations: 3);

        int raster = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToPng(ms, 0);
        }).Count(r => IsBudgetRefusal(r.Detail));

        int svg = CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        }).Count(r => IsBudgetRefusal(r.Detail));

        Assert.Equal(svg, raster);
    }

    private static bool IsBudgetRefusal(string detail) =>
        detail != null &&
        detail.IndexOf(
            PdfLimits.MaxFormXObjectInvocationsPerPage.ToString("N0", CultureInfo.InvariantCulture),
            StringComparison.Ordinal) >= 0 &&
        detail.IndexOf("form-XObject", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// A page with <paramref name="annotations"/> annotations whose <c>/AP /N</c> streams each draw
    /// the head of a <paramref name="levels"/>-deep doubling chain. The page's own content stream is
    /// empty, so every charged invocation comes from an appearance.
    /// </summary>
    /// <remarks>
    /// Every form carries its own <c>/Resources</c> naming the next rather than relying on the
    /// page's: §12.7.3.3 gives an appearance stream without <c>/Resources</c> the AcroForm
    /// <c>/DR</c>, not the page's, so a fixture leaning on the page would be testing resource
    /// fallback instead of the budget.
    /// </remarks>
    private static byte[] AnnotatedDoublingChainPdf(int levels, int annotations)
    {
        int chainFirst = 5;                       // 1 catalog, 2 pages, 3 page, 4 page contents
        int apFirst    = chainFirst + levels;
        int annotFirst = apFirst + annotations;

        string annotRefs = string.Join(" ",
            Enumerable.Range(0, annotations).Select(i => $"{annotFirst + i} 0 R"));

        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                   "/Resources << >> /Annots [" + annotRefs + "] >>"),
            StreamObj(4, "", "\n"),
        };

        for (int i = 0; i < levels; i++)
        {
            int self = chainFirst + i;
            bool last = i == levels - 1;
            string content = last ? "0 0 1 1 re f\n" : "/F Do /F Do\n";
            string res = last
                ? " /Resources << >>"
                : " /Resources << /XObject << /F " + (self + 1) + " 0 R >> >>";
            bodies.Add(StreamObj(self,
                " /Type /XObject /Subtype /Form /BBox [0 0 10 10]" + res, content));
        }

        for (int i = 0; i < annotations; i++)
            bodies.Add(StreamObj(apFirst + i,
                " /Type /XObject /Subtype /Form /BBox [0 0 10 10]" +
                " /Resources << /XObject << /F " + chainFirst + " 0 R >> >>", "/F Do\n"));

        for (int i = 0; i < annotations; i++)
            bodies.Add(Obj(annotFirst + i,
                "<< /Type /Annot /Subtype /Stamp /Rect [10 10 60 60] " +
                "/F 4 /AP << /N " + (apFirst + i) + " 0 R >> >>"));

        return Assemble(bodies);
    }

    internal static string Obj(int num, string body) => $"{num} 0 obj\n{body}\nendobj\n";

    internal static string StreamObj(int num, string extraKeys, string content) =>
        $"{num} 0 obj\n<<{extraKeys} /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n";

    /// <summary>
    /// Assembles numbered object bodies into a file with a correct classic cross-reference table.
    /// </summary>
    internal static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        foreach (string body in bodies)
        {
            offsets.Add(sb.Length);
            sb.Append(body);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

/// <summary>
/// The budget's second ceiling: the decoded content a page's invocations run through
/// (<c>PdfLimits.MaxFormContentBytesPerPage</c>). The invocation count bounds how many times a form
/// runs and never how large each run is — a form of many <c>Do</c> operators drawing itself is parsed
/// whole at every invocation, and the structure fuzzer's probed run found the SVG writer, which caches
/// no parse, running four such files for its whole two-minute window while the count was still inside
/// its ceiling. Writes a process-wide limit, so it joins the <c>PdfLimits</c> collection and restores.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class RenderFormContentBudgetTests
{
    /// <summary>
    /// A form of 400 <c>Do</c> operators drawing itself — 400²⁴ invocations by count, some 2.8 KB of
    /// content each. Under a content ceiling of 64 KB both renderers refuse after a couple of dozen
    /// invocations, by the byte ceiling and naming it; the invocation ceiling, at its default, is
    /// never reached.
    /// </summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void WideFormDrawingItself_IsRefusedByTheContentCeiling_NotTheCount(string renderer)
    {
        byte[] pdf = SelfDrawingFormPdf(fanOut: 400);
        try
        {
            PdfLimits.MaxFormContentBytesPerPage = 64 * 1024;

            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                if (renderer == "raster") PdfOperations.RenderPageToPng(ms, 0);
                else PdfOperations.RenderPageToSvg(ms, 0);
            });

            string ceiling = (64 * 1024).ToString("N0", CultureInfo.InvariantCulture);
            Assert.Contains(reported, d => d.Detail != null
                && d.Detail.IndexOf("bytes of decoded content", StringComparison.Ordinal) >= 0
                && d.Detail.IndexOf(ceiling, StringComparison.Ordinal) >= 0);
            Assert.DoesNotContain(reported, d => d.Detail != null
                && d.Detail.IndexOf("form-XObject invocations", StringComparison.Ordinal) >= 0);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The floor under the same ceiling: a form drawing itself <b>once</b> is a chain the depth cap
    /// stops at 24 — a few hundred bytes of content in all — and neither ceiling refuses it.
    /// </summary>
    [Fact]
    public void NarrowFormDrawingItself_IsNotRefusedByEitherCeiling()
    {
        byte[] pdf = SelfDrawingFormPdf(fanOut: 1);
        try
        {
            PdfLimits.MaxFormContentBytesPerPage = 64 * 1024;

            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                PdfOperations.RenderPageToPng(ms, 0);
                using var ms2 = new MemoryStream(pdf);
                PdfOperations.RenderPageToSvg(ms2, 0);
            });

            Assert.Contains(reported, d => d.Detail != null && d.Detail.IndexOf("nesting exceeds", StringComparison.Ordinal) >= 0);
            Assert.DoesNotContain(reported, d => d.Detail != null
                && (d.Detail.IndexOf("bytes of decoded content", StringComparison.Ordinal) >= 0
                    || d.Detail.IndexOf("form-XObject invocations", StringComparison.Ordinal) >= 0));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    // ==================================================================
    //  The offscreen pixel ceiling — a cell's price before its content runs (GAPS.md §7c)
    // ==================================================================

    /// <summary>
    /// The coverage-guided fuzzer's pattern: a tiling cell a thousand points square at a one-point
    /// step whose content fills with itself twice. The lattice drops its million copies, the depth
    /// cap stops each branch at 24, the content ceiling is far off (a few dozen bytes a run), and the
    /// doubling runs to the invocation count — a hundred thousand sixteen-megapixel bitmaps, minutes
    /// from two kilobytes. Under an offscreen ceiling of 64 Mi pixels the raster renderer refuses
    /// after four cells, by that ceiling and naming it; neither count is reached.
    /// </summary>
    [Fact]
    public void SelfFillingPatternCell_IsRefusedByTheOffscreenPixelCeiling_NotTheCounts()
    {
        byte[] pdf = SelfFillingPatternPdf(fills: 2, xStep: 1);
        try
        {
            PdfLimits.MaxOffscreenPixelsPerPage = 64L * 1024 * 1024;

            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                PdfOperations.RenderPageToPng(ms, 0);
            });

            string ceiling = (64L * 1024 * 1024).ToString("N0", CultureInfo.InvariantCulture);
            Assert.Contains(reported, d => d.Detail != null
                && d.Detail.IndexOf("offscreen renders", StringComparison.Ordinal) >= 0
                && d.Detail.IndexOf(ceiling, StringComparison.Ordinal) >= 0);
            Assert.DoesNotContain(reported, d => d.Detail != null
                && (d.Detail.IndexOf("form-XObject invocations", StringComparison.Ordinal) >= 0
                    || d.Detail.IndexOf("bytes of decoded content", StringComparison.Ordinal) >= 0));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The floor: the same cell filling with itself <b>once</b> is a chain the depth cap stops at 24
    /// — twenty-four bitmaps, four hundred megapixels, inside the default ceiling — and the pixel
    /// ceiling does not refuse it; the blue square each cell paints reaches the page.
    /// </summary>
    [Fact]
    public void SelfFillingPatternCell_Once_IsNotRefusedByThePixelCeiling_AndPaints()
    {
        byte[] pdf = SelfFillingPatternPdf(fills: 1, xStep: 1);
        Tetitulz.Pdf.Base.Rendering.RenderedPage page = null;

        var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
        {
            using var ms = new MemoryStream(pdf);
            page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(ms, 0);
        });

        Assert.Contains(reported, d => d.Detail != null && d.Detail.IndexOf("nesting exceeds", StringComparison.Ordinal) >= 0);
        Assert.DoesNotContain(reported, d => d.Detail != null && d.Detail.IndexOf("offscreen renders", StringComparison.Ordinal) >= 0);
        Assert.True(PaintsASaturatedPrimary(page), "the cell's blue square did not reach the page");
    }

    /// <summary>
    /// A tile composed of several copies of its cell is charged their area too: a cell a thousand
    /// points square at a five-hundred-point step is four copies (§8.7.3.1's overlap), each a blit of
    /// the tile. At 150 dpi the cell is 4.1 Mi pixels and the four copies another 4.1 Mi, so a ceiling
    /// of 6 Mi admits the cell and refuses the composition, and one of 12 Mi admits both and paints.
    /// </summary>
    [Fact]
    public void ATileOfSeveralCopies_IsChargedTheirArea()
    {
        byte[] pdf = SelfFillingPatternPdf(fills: 0, xStep: 500);
        try
        {
            PdfLimits.MaxOffscreenPixelsPerPage = 6L * 1024 * 1024;
            var refused = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                PdfOperations.RenderPageToPng(ms, 0);
            });
            Assert.Contains(refused, d => d.Detail != null && d.Detail.IndexOf("offscreen renders", StringComparison.Ordinal) >= 0);

            PdfLimits.MaxOffscreenPixelsPerPage = 12L * 1024 * 1024;
            Tetitulz.Pdf.Base.Rendering.RenderedPage page = null;
            var admitted = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(ms, 0);
            });
            Assert.DoesNotContain(admitted, d => d.Detail != null && d.Detail.IndexOf("offscreen renders", StringComparison.Ordinal) >= 0);
            Assert.True(PaintsASaturatedPrimary(page), "the admitted tile did not paint");
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    // ==================================================================
    //  The page's image-decoding budget — the stream budgets' parent (GAPS.md §7c)
    // ==================================================================

    /// <summary>
    /// A JBIG2 stream of a few dozen bytes spends its whole composition budget, and that budget is per
    /// stream, so a page of a hundred such images cost a hundred budgets inside every ceiling. Under a
    /// stream budget of one million units and a page budget of two and a half million, a page of four
    /// such images decodes two (each falling back to its bytes, as before), and the page budget refuses
    /// the third — reported once, naming its ceiling, with the fourth not decoded at all. In both
    /// renderers, through the one image-decode path they share.
    /// </summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void APageOfJbig2StreamsEachSpendingTheirBudget_IsBoundedAsAPage_AndReportedOnce(string renderer)
    {
        byte[] pdf = Jbig2PagePdf(images: 4);
        try
        {
            PdfLimits.MaxJbig2CompositionPixels = 1_000_000;
            PdfLimits.MaxImageDecodeWorkPerPage = 2_500_000;

            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() => Render(renderer, pdf));

            Assert.Equal(1, reported.Count(d => d.Detail != null
                && d.Detail.IndexOf("PdfLimits.MaxImageDecodeWorkPerPage", StringComparison.Ordinal) >= 0));
            // The page's refusal is reported as the page's, never as the third stream failing to
            // decode on its own: the two renderers' decode-failure messages do not appear.
            Assert.DoesNotContain(reported, d => d.Detail != null
                && (d.Detail.IndexOf("GetDecodedData failed", StringComparison.Ordinal) >= 0
                    || d.Detail.IndexOf("XObject decode failed", StringComparison.Ordinal) >= 0));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The floor: two such images spend two million of the page's two and a half, and the page refuses nothing.</summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void TwoJbig2StreamsSpendingTheirBudgets_StayInsideThePages(string renderer)
    {
        byte[] pdf = Jbig2PagePdf(images: 2);
        try
        {
            PdfLimits.MaxJbig2CompositionPixels = 1_000_000;
            PdfLimits.MaxImageDecodeWorkPerPage = 2_500_000;

            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() => Render(renderer, pdf));

            Assert.DoesNotContain(reported, d => d.Detail != null
                && d.Detail.IndexOf("PdfLimits.MaxImageDecodeWorkPerPage", StringComparison.Ordinal) >= 0);
            // Potency: the streams did reach the decoder and were refused by their own budgets.
            Assert.Contains(reported, d => d.Kind == RenderDiagnosticKind.ImageDecodeFailed);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A JPEG 2000 codestream of 82 bytes declares as many pixels as it likes — a blank tile with no
    /// decomposition levels decodes to its full area, and a bitmap past the render context's cache is
    /// decoded again at every <c>Do</c> — so each decode charges the page its pixels before it starts.
    /// Under a page budget of two and a half million pixels, a page of four 1024² images decodes two and
    /// the page refuses the third, reported once; in both renderers.
    /// </summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void APageOfJpxImages_IsBoundedByThePagesImageBudget_AndReportedOnce(string renderer)
    {
        byte[] pdf = JpxPagePdf(images: 4, side: 1024);
        try
        {
            PdfLimits.MaxImageDecodeWorkPerPage = 2_500_000;

            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() => Render(renderer, pdf));

            Assert.Equal(1, reported.Count(d => d.Detail != null
                && d.Detail.IndexOf("PdfLimits.MaxImageDecodeWorkPerPage", StringComparison.Ordinal) >= 0));
            Assert.DoesNotContain(reported, d => d.Detail != null
                && d.Detail.IndexOf("the decoder declined", StringComparison.Ordinal) >= 0);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The floor: two such images spend two million of the page's two and a half and are drawn — the blank tile's mid grey.</summary>
    [Theory]
    [InlineData("raster")]
    [InlineData("SVG")]
    public void TwoJpxImagesInsideThePagesImageBudget_AreDrawn(string renderer)
    {
        byte[] pdf = JpxPagePdf(images: 2, side: 1024);
        try
        {
            PdfLimits.MaxImageDecodeWorkPerPage = 2_500_000;

            string svg = null;
            Tetitulz.Pdf.Base.Rendering.RenderedPage page = null;
            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                if (renderer == "raster") page = PdfOperations.RenderPageToPixels(ms, 0, 72);
                else svg = PdfOperations.RenderPageToSvg(ms, 0);
            });

            Assert.DoesNotContain(reported, d => d.Detail != null
                && d.Detail.IndexOf("PdfLimits.MaxImageDecodeWorkPerPage", StringComparison.Ordinal) >= 0);
            if (renderer == "raster")
            {
                // The first image covers (10, 10)-(50, 50) in a 200 x 100 page; its centre, rows from the top.
                int at = (100 - 30) * page.Stride + 30 * 4;
                Assert.InRange(page.Pixels[at], 100, 156);
            }
            else
            {
                Assert.Equal(2, CountOccurrences(svg, "data:image/png"));
            }
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    private static int CountOccurrences(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// A page drawing <paramref name="images"/> JPXDecode image XObjects, each its own object holding
    /// the same raw codestream: one blank <paramref name="side"/>² tile, one 8-bit component, no
    /// decomposition levels, 64 × 64 code-blocks, one empty packet.
    /// </summary>
    private static byte[] JpxPagePdf(int images, int side)
    {
        var cs = new List<byte>();
        void U16(int v) { cs.Add((byte)(v >> 8)); cs.Add((byte)v); }
        void U32(long v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        U16(0xFF4F);
        U16(0xFF51); U16(41); U16(0);
        U32(side); U32(side); U32(0); U32(0);
        U32(side); U32(side); U32(0); U32(0);
        U16(1); cs.Add(7); cs.Add(1); cs.Add(1);
        U16(0xFF52); U16(12); cs.Add(0x00); cs.Add(0); U16(1); cs.Add(0); cs.Add(0); cs.Add(4); cs.Add(4); cs.Add(0); cs.Add(1);
        U16(0xFF5C); U16(4); cs.Add(0x40); cs.Add(8 << 3);
        U16(0xFF90); U16(10); U16(0); U32(12 + 2 + 1); cs.Add(0); cs.Add(1);
        U16(0xFF93); cs.Add(0x00);
        U16(0xFFD9);
        string streamText = Encoding.Latin1.GetString(cs.ToArray());

        var bodies = new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };
        var xobjects = new StringBuilder();
        var content = new StringBuilder();
        for (int i = 0; i < images; i++)
        {
            xobjects.Append("/Im").Append(i).Append(' ').Append(5 + i).Append(" 0 R ");
            content.Append("q 40 0 0 40 ").Append(10 + 45 * i).Append(" 10 cm /Im").Append(i).Append(" Do Q\n");
        }
        bodies.Add(RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R " +
                           "/Resources << /XObject << " + xobjects + ">> >> >>"));
        bodies.Add(RenderFormXObjectBudgetTests.StreamObj(4, "", content.ToString()));
        for (int i = 0; i < images; i++)
            bodies.Add(RenderFormXObjectBudgetTests.StreamObj(5 + i,
                " /Type /XObject /Subtype /Image /Width " + side + " /Height " + side + " /ColorSpace /DeviceGray " +
                "/BitsPerComponent 8 /Filter /JPXDecode", streamText));
        return RenderFormXObjectBudgetTests.Assemble(bodies);
    }

    private static void Render(string renderer, byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        if (renderer == "raster") PdfOperations.RenderPageToPng(ms, 0);
        else PdfOperations.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// A page drawing <paramref name="images"/> JBIG2 image XObjects, each a 31-byte stream whose single
    /// generic region declares 8192 pixels square — 64 Mi pixels charged before a bit is decoded.
    /// </summary>
    private static byte[] Jbig2PagePdf(int images)
    {
        // Page info (16 × 16), an immediate generic region 8192 × 8192 (arithmetic, template 0, the
        // nominal AT bytes, two bytes of data), end of file.
        var jbig2 = new List<byte>();
        void Header(uint number, int type, uint length)
        {
            jbig2.AddRange(new byte[] { (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number,
                (byte)type, 0x00, 0x01, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length });
        }
        void U32(uint v) => jbig2.AddRange(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        Header(0, 48, 19); U32(16); U32(16); U32(0); U32(0); jbig2.AddRange(new byte[3]);
        Header(1, 38, 28); U32(8192); U32(8192); U32(0); U32(0); jbig2.Add(0); jbig2.Add(0);
        jbig2.AddRange(new byte[] { 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE, 0xFF, 0xAC });
        Header(2, 51, 0);
        string streamText = Encoding.Latin1.GetString(jbig2.ToArray());

        var bodies = new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        };
        var xobjects = new StringBuilder();
        var content = new StringBuilder();
        for (int i = 0; i < images; i++)
        {
            xobjects.Append("/Im").Append(i).Append(' ').Append(5 + i).Append(" 0 R ");
            content.Append("q 40 0 0 40 ").Append(10 + 45 * i).Append(" 10 cm /Im").Append(i).Append(" Do Q\n");
        }
        bodies.Add(RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R " +
                           "/Resources << /XObject << " + xobjects + ">> >> >>"));
        bodies.Add(RenderFormXObjectBudgetTests.StreamObj(4, "", content.ToString()));
        for (int i = 0; i < images; i++)
            bodies.Add(RenderFormXObjectBudgetTests.StreamObj(5 + i,
                " /Type /XObject /Subtype /Image /Width 8192 /Height 8192 /ColorSpace /DeviceGray " +
                "/BitsPerComponent 1 /Filter /JBIG2Decode", streamText));
        return RenderFormXObjectBudgetTests.Assemble(bodies);
    }

    /// <summary>Whether any pixel is a saturated primary — the blue square, whichever channel order the buffer has.</summary>
    private static bool PaintsASaturatedPrimary(Tetitulz.Pdf.Base.Rendering.RenderedPage page)
    {
        if (page == null) return false;
        byte[] px = page.Pixels;
        for (int y = 0; y < page.Height; y++)
        {
            int row = y * page.Stride;
            for (int x = 0; x < page.Width; x++)
            {
                int i = row + x * 4;
                int max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                int min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                if (max >= 200 && min <= 60) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A page filled with tiling pattern P1: a cell a thousand points square at <paramref name="xStep"/>,
    /// painting a blue square and then itself <paramref name="fills"/> times.
    /// </summary>
    private static byte[] SelfFillingPatternPdf(int fills, int xStep)
    {
        var cell = new StringBuilder("0 0 1 rg 0 0 500 500 re f ");
        for (int i = 0; i < fills; i++) cell.Append("/Pattern cs /P1 scn 0 0 10 10 re f ");
        cell.Append('\n');
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                                                "/Resources << /Pattern << /P1 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "/Pattern cs /P1 scn 0 0 200 200 re f\n"),
            RenderFormXObjectBudgetTests.StreamObj(5, " /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1000 1000] " +
                                                      "/XStep " + xStep + " /YStep " + xStep + " /Resources << /Pattern << /P1 5 0 R >> >>",
                                                   cell.ToString()),
        });
    }

    /// <summary>One page drawing one form whose content draws the form itself <paramref name="fanOut"/> times, then a square.</summary>
    private static byte[] SelfDrawingFormPdf(int fanOut)
    {
        var body = new StringBuilder();
        for (int i = 0; i < fanOut; i++) body.Append("/Fx Do ");
        body.Append("0 0 1 rg 0 0 10 10 re f\n");
        const string res = "<< /XObject << /Fx 5 0 R >> >>";
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources " + res + " >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", "/Fx Do\n"),
            RenderFormXObjectBudgetTests.StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources " + res, body.ToString()),
        });
    }

    // ==================================================================
    //  The operation ceiling — what a run executes, which its bytes do not price
    // ==================================================================

    /// <summary>
    /// A form of four thousand <c>q Q</c> pairs — eight thousand operations in sixteen kilobytes —
    /// drawn <paramref name="draws"/> times from the page. Under an operation ceiling of 100 000,
    /// twenty draws are refused by that ceiling and name it, in both renderers; ten are not refused
    /// at all. The invocation count and the content bytes are far off at either size. The fuzzer's
    /// cell of 4 556 <c>q</c> operators ran 39 million state clones inside every other ceiling.
    /// </summary>
    [Theory]
    [InlineData("raster", 20, true)]
    [InlineData("SVG", 20, true)]
    [InlineData("raster", 10, false)]
    [InlineData("SVG", 10, false)]
    public void AdversarialFormOfCheapOperations_IsRefusedByTheOperationCeiling(string renderer, int draws, bool refused)
    {
        byte[] pdf = RepeatedFormPdf(string.Concat(Enumerable.Repeat("q Q ", 4000)), draws);
        try
        {
            PdfLimits.MaxFormOperationsPerPage = 100_000;

            var reported = RenderFormXObjectBudgetTests.CaptureDiagnostics(() =>
            {
                using var ms = new MemoryStream(pdf);
                if (renderer == "raster") PdfOperations.RenderPageToPng(ms, 0);
                else PdfOperations.RenderPageToSvg(ms, 0);
            });

            string ceiling = 100_000.ToString("N0", CultureInfo.InvariantCulture);
            Assert.Equal(refused, reported.Any(d => d.Detail != null
                && d.Detail.IndexOf("MaxFormOperationsPerPage", StringComparison.Ordinal) >= 0
                && d.Detail.IndexOf(ceiling, StringComparison.Ordinal) >= 0));
            Assert.DoesNotContain(reported, d => d.Detail != null
                && (d.Detail.IndexOf("form-XObject invocations", StringComparison.Ordinal) >= 0
                    || d.Detail.IndexOf("bytes of decoded content", StringComparison.Ordinal) >= 0));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The refused draws paint nothing and the ones before still paint: a form ending in a blue
    /// square, drawn twenty times under a ceiling the first ten fit, leaves the square on the page.
    /// </summary>
    [Fact]
    public void FormRunsBeforeTheOperationCeiling_StillPaint()
    {
        byte[] pdf = RepeatedFormPdf(string.Concat(Enumerable.Repeat("q Q ", 4000)) + "0 0 1 rg 0 0 200 200 re f\n", 20);
        try
        {
            PdfLimits.MaxFormOperationsPerPage = 100_000;
            using var ms = new MemoryStream(pdf);
            var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(ms, 0, 36);
            Assert.True(PaintsASaturatedPrimary(page));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The page's own stream is not nested content and is not counted: 120 000 operations of it,
    /// past a ceiling of 100 000, still reach the blue fill at its end, in both renderers.
    /// </summary>
    [Fact]
    public void ThePagesOwnOperations_AreNotChargedToTheOperationCeiling()
    {
        byte[] pdf = RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", string.Concat(Enumerable.Repeat("n ", 120_000)) + "0 0 1 rg 0 0 200 200 re f\n"),
        });
        try
        {
            PdfLimits.MaxFormOperationsPerPage = 100_000;
            using (var ms = new MemoryStream(pdf))
                Assert.True(PaintsASaturatedPrimary(Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(ms, 0, 36)));
            using (var ms = new MemoryStream(pdf))
                Assert.Contains(System.Xml.Linq.XDocument.Parse(PdfOperations.RenderPageToSvg(ms, 0)).Descendants(),
                    e => string.Equals((string)e.Attribute("fill"), "rgb(0,0,255)", StringComparison.Ordinal));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>One page drawing form Fx <paramref name="draws"/> times; Fx's content is <paramref name="formContent"/>.</summary>
    private static byte[] RepeatedFormPdf(string formContent, int draws)
    {
        const string res = "<< /XObject << /Fx 5 0 R >> >>";
        return RenderFormXObjectBudgetTests.Assemble(new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources " + res + " >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", string.Concat(Enumerable.Repeat("/Fx Do ", draws)) + "\n"),
            RenderFormXObjectBudgetTests.StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200]", formContent),
        });
    }
}
