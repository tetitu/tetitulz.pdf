// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// PDF §9.3.8 / Table 52 — text knockout (<c>/TK</c>).
///
/// <para><b>The default is the case that was wrong.</b> <c>/TK</c>'s initial value is
/// <b>true</b>: the glyphs of one text object composite as a knockout group, so where two glyphs
/// overlap the later one <i>replaces</i> the earlier rather than painting over it. Both renderers
/// painted glyph-by-glyph with SrcOver, which is the <c>/TK false</c> model — so the deviation
/// applied to every file, including the overwhelming majority that never mention the key, and the
/// only diagnostic fired on an explicit <c>/TK true</c>, i.e. exactly on the files that were
/// redundantly restating the default.</para>
///
/// <para><b>Why the fixture draws the same glyph twice at the same place.</b> The test needs
/// overlapping ink and must not depend on where a standard-14 glyph puts it. Two identical shows
/// at one <c>Tm</c> overlap <i>perfectly</i>, so the claim becomes a comparison between two
/// renders rather than a measurement of glyph geometry: the doubled show must not get darker than
/// the single one under knockout, and must get markedly darker under <c>/TK false</c>. Both
/// directions are asserted, so neither passes with the layer removed and neither passes with the
/// layer opened unconditionally.</para>
///
/// <para><b>The assertion is on the darkest pixel, not on every pixel, and that is a statement
/// about antialiasing rather than a weakened claim.</b> The PDF imaging model is analytic — a
/// point is inside a glyph or outside it — while a rasteriser paints partial coverage at the
/// edges. In <see cref="SKBlendMode.Src"/> the coverage still interpolates towards the
/// destination, so a doubly-drawn <i>edge</i> pixel lands at c(2−c)·α rather than c·α: darker
/// than one draw, lighter than SrcOver's, and confined to the one-pixel rim. Interior coverage is
/// 1 and is therefore exact, which is what the darkest-luminance comparison reads. Asserting
/// pixel-for-pixel equality instead would be asserting that Skia does not antialias.</para>
/// </summary>
public class TextKnockoutTests
{
    /// <summary>
    /// The knockout claim itself: with <c>/TK</c> at its default, a glyph drawn twice in one text
    /// object at half opacity is indistinguishable from the same glyph drawn once. Mutation: drop
    /// the layer in <c>ContentStreamInterpreter.BeginTextKnockout</c> and the doubled render gains
    /// a large block of darkened pixels.
    /// </summary>
    [Fact]
    public void OverlappingTranslucentGlyphs_KnockOutRatherThanDarkeningEachOther()
    {
        using var once  = PixelAssert.Render(TextPdf(shows: 1, extGs: "/ca 0.5"));
        using var twice = PixelAssert.Render(TextPdf(shows: 2, extGs: "/ca 0.5"));

        // Fixture check: without this the comparison below is satisfied by two blank pages, and
        // the second half of it is what says the ink really is translucent — a fixture whose /ca
        // never reached the paint would sit at 0 and could not tell the two models apart.
        int single = PixelAssert.DarkestLuminance(once);
        Assert.True(PixelAssert.CountInk(once) > 200,
            $"fixture drew no measurable ink ({PixelAssert.CountInk(once)} px)");
        Assert.InRange(single, 100, 160);

        Assert.Equal(single, PixelAssert.DarkestLuminance(twice));
    }

    /// <summary>
    /// The same knockout under a soft mask: the glyphs take the mask through their paint's shader,
    /// painted Src in the text object's knockout layer, so the doubled show is no darker than the
    /// single one. Through the masked layer, merged SrcOver, the second glyph darkened the first.
    /// </summary>
    [Fact]
    public void OverlappingSoftMaskedGlyphs_KnockOutRatherThanDarkeningEachOther()
    {
        using var once  = PixelAssert.Render(MaskedTextPdf(shows: 1));
        using var twice = PixelAssert.Render(MaskedTextPdf(shows: 2));

        int single = PixelAssert.DarkestLuminance(once);
        Assert.True(PixelAssert.CountInk(once) > 200,
            $"fixture drew no measurable ink ({PixelAssert.CountInk(once)} px)");
        Assert.InRange(single, 100, 160);   // the mask's 0.5 reached the paint

        Assert.Equal(single, PixelAssert.DarkestLuminance(twice));
    }

    /// <summary>One text object showing the glyph <paramref name="shows"/> times under a uniform 0.5 luminosity mask.</summary>
    private static byte[] MaskedTextPdf(int shows)
    {
        var content = new StringBuilder("q /GS1 gs BT /F1 72 Tf ");
        for (int i = 0; i < shows; i++) content.Append("1 0 0 1 10 20 Tm (H) Tj ");
        content.Append("ET Q");
        const string maskBody = "0.5 g 0 0 100 100 re f";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources <<" +
            " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>" +
            " /ExtGState << /GS1 << /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >> >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceGray >>" +
            $" /Length {maskBody.Length} >>\nstream\n{maskBody}\nendstream");
    }

    /// <summary>
    /// The polarity half, and the reason it is a separate case: an implementation that opened the
    /// layer unconditionally would pass the test above while getting <c>/TK false</c> — the one
    /// value glyph-by-glyph SrcOver already had right — wrong instead.
    /// </summary>
    [Fact]
    public void TkFalse_LetsOverlappingGlyphsCompositeWithEachOther()
    {
        using var once  = PixelAssert.Render(TextPdf(shows: 1, extGs: "/ca 0.5 /TK false"));
        using var twice = PixelAssert.Render(TextPdf(shows: 2, extGs: "/ca 0.5 /TK false"));

        // 0.5 over 0.5 is 0.75, so the doubled show is half again as dark: ~128 against ~64.
        Assert.True(PixelAssert.DarkestLuminance(twice) < PixelAssert.DarkestLuminance(once) - 30,
            "/TK false asks for glyph-by-glyph compositing, so the doubled show must darken where "
            + $"the glyphs overlap (once={PixelAssert.DarkestLuminance(once)}, "
            + $"twice={PixelAssert.DarkestLuminance(twice)})");
    }

    /// <summary>
    /// Opaque text is pixel-identical under both models, so no layer is opened and nothing is
    /// reported. This is the "presence is not deviation" rule: a diagnostic that fires on every
    /// text-bearing page is one a caller learns to ignore.
    /// </summary>
    [Fact]
    public void OpaqueText_IsUnaffectedAndReportsNothing()
    {
        using var once  = PixelAssert.Render(TextPdf(shows: 1, extGs: null));
        using var twice = PixelAssert.Render(TextPdf(shows: 2, extGs: null));

        Assert.Equal(PixelAssert.DarkestLuminance(once), PixelAssert.DarkestLuminance(twice));
        Assert.DoesNotContain(SvgReports(TextPdf(shows: 2, extGs: null)), d => d.Contains("/TK"));
    }

    /// <summary>
    /// SVG has no knockout compositing model, but a text object whose glyphs share one alpha needs
    /// none: knocked out, it is the union of its glyphs at that alpha, latest on top — the glyphs
    /// painted opaque inside <c>&lt;g opacity&gt;</c>. The writer emits exactly that, on the state
    /// the text object <i>opens</i> with, so it acts on a file that never writes <c>/TK</c> (this
    /// fixture does not). It used to report the knockout as ignored, and was renamed when it
    /// stopped.
    /// </summary>
    [Fact]
    public void SvgExport_EmulatesTheKnockout_EvenWithNoTkInTheFile()
    {
        byte[] pdf = TextPdf(shows: 2, extGs: "/ca 0.5");
        XElement group = Assert.Single(Groups(Svg(pdf)));

        Assert.Equal("0.5", (string)group.Attribute("opacity"));
        Assert.Equal(2, group.Elements(SvgNs + "text").Count());
        Assert.All(group.Descendants(SvgNs + "text"), t => Assert.Null(t.Attribute("fill-opacity")));
        Assert.DoesNotContain(SvgReports(pdf), d => d.Contains("/TK"));
    }

    /// <summary>The polarity control: <c>/TK false</c> opens no group and keeps each glyph's alpha.</summary>
    [Fact]
    public void SvgExport_TkFalse_OpensNoGroup()
    {
        XDocument svg = Svg(TextPdf(shows: 2, extGs: "/ca 0.5 /TK false"));

        Assert.Empty(Groups(svg));
        Assert.All(svg.Descendants(SvgNs + "text"), t => Assert.Equal("0.5", (string)t.Attribute("fill-opacity")));
        Assert.Equal(2, svg.Descendants(SvgNs + "text").Count());
    }

    /// <summary>
    /// Stroked text takes its one alpha from <c>/CA</c>, whatever <c>/ca</c> is; and the state's
    /// alphas come back at <c>ET</c>, so a path after the text paints at its own.
    /// </summary>
    [Fact]
    public void SvgExport_StrokedText_TakesTheStrokeAlpha_AndTheAlphasComeBackAtET()
    {
        XDocument svg = Svg(Pdf("q /GS1 gs BT /F1 72 Tf 1 Tr 1 0 0 1 10 20 Tm (H) Tj (H) Tj ET 0 0 5 5 re f Q",
                                "/CA 0.4 /ca 0.9"));

        Assert.Equal("0.4", (string)Assert.Single(Groups(svg)).Attribute("opacity"));
        XElement path = svg.Descendants(SvgNs + "path").Last();
        Assert.Equal("0.9", (string)path.Attribute("fill-opacity"));
    }

    /// <summary>Filled and stroked text at two alphas has no one alpha: reported, no group.</summary>
    [Fact]
    public void SvgExport_FillAndStrokeAtDifferentAlphas_IsReported()
    {
        byte[] pdf = Pdf("q /GS1 gs BT /F1 72 Tf 2 Tr 1 0 0 1 10 20 Tm (H) Tj ET Q", "/CA 0.4 /ca 0.9");

        Assert.Empty(Groups(Svg(pdf)));
        Assert.Contains(SvgReports(pdf), d => d.Contains("/TK true"));
    }

    /// <summary>
    /// A <c>gs</c> inside the text object that sets an alpha ends the group's one alpha: the group
    /// closes there, the glyph after it paints at its own alpha outside it, and it is reported.
    /// </summary>
    [Fact]
    public void SvgExport_AnAlphaChangeInsideTheTextObject_ClosesTheGroupAndIsReported()
    {
        byte[] pdf = Pdf("q /GS1 gs BT /F1 72 Tf 1 0 0 1 10 20 Tm (H) Tj /GS2 gs (H) Tj ET Q",
                         "/ca 0.5", "/ca 0.3");
        XDocument svg = Svg(pdf);

        XElement group = Assert.Single(Groups(svg));
        Assert.Single(group.Elements(SvgNs + "text"));
        XElement after = svg.Descendants(SvgNs + "text").Last();
        Assert.NotEqual(group, after.Parent);
        Assert.Equal("0.3", (string)after.Attribute("fill-opacity"));
        Assert.Contains(SvgReports(pdf), d => d.Contains("/TK true"));
    }

    /// <summary>
    /// A <c>Tr</c> inside the text object keeps the group while its paints share the group's alpha
    /// and closes it when they do not.
    /// </summary>
    [Fact]
    public void SvgExport_ARenderModeChange_KeepsTheGroupOnlyWhileTheAlphaIsShared()
    {
        const string Shows = "q /GS1 gs BT /F1 72 Tf 1 0 0 1 10 20 Tm (H) Tj 1 Tr (H) Tj ET Q";

        XElement kept = Assert.Single(Groups(Svg(Pdf(Shows, "/ca 0.5 /CA 0.5"))));
        Assert.Equal(2, kept.Elements(SvgNs + "text").Count());

        byte[] split = Pdf(Shows, "/ca 0.5 /CA 0.8");
        Assert.Single(Assert.Single(Groups(Svg(split))).Elements(SvgNs + "text"));
        Assert.Contains(SvgReports(split), d => d.Contains("/TK true"));
    }

    /// <summary>
    /// Invisible glyphs (an OCR layer's <c>3 Tr</c>) paint nothing, so they open no group: the
    /// group waits for the first glyph that paints, and takes that glyph's alpha — here the fill's,
    /// though the stroke alpha (1) differs. Opened on the invisible glyph instead, it would carry
    /// the stroke alpha and the filled glyph would close it with a false report.
    /// </summary>
    [Fact]
    public void SvgExport_InvisibleGlyphsFirst_TheGroupWaitsForThePaintingOne()
    {
        byte[] pdf = Pdf("q /GS1 gs BT /F1 72 Tf 3 Tr 1 0 0 1 10 20 Tm (H) Tj 0 Tr (H) Tj ET Q", "/ca 0.5");

        Assert.Equal("0.5", (string)Assert.Single(Groups(Svg(pdf))).Attribute("opacity"));
        Assert.DoesNotContain(SvgReports(pdf), d => d.Contains("/TK"));
    }

    /// <summary>Under a soft mask or a blend mode one group cannot stand for the glyphs: reported, no group.</summary>
    [Fact]
    public void SvgExport_UnderABlendMode_ReportsAndOpensNoGroup()
    {
        byte[] pdf = TextPdf(shows: 2, extGs: "/ca 0.5 /BM /Multiply");

        Assert.Empty(Groups(Svg(pdf)));
        Assert.Contains(SvgReports(pdf), d => d.Contains("/TK true"));
    }

    /// <summary>
    /// The raster path must NOT report it — it honours it. Reporting an ignored parameter that is
    /// no longer ignored is the same class of false report as reporting <c>/HT /Default</c>.
    /// </summary>
    [Fact]
    public void RasterExport_DoesNotReportTextKnockout()
    {
        var reports = Collect(() =>
        {
            using var ms = new MemoryStream(TextPdf(shows: 2, extGs: "/ca 0.5 /TK true"));
            PdfOperations.RenderPageToPng(ms, 0, dpi: PixelAssert.Dpi);
        });

        Assert.DoesNotContain(reports, d => d.Contains("/TK"));
    }

    /// <summary>
    /// Adversarial: a text object that is opened and never closed. The knockout layer is a canvas
    /// save, so leaving it open would put every later operation — and, for a form XObject or a
    /// Type 3 glyph, the caller's whole stream — one layer deep. The content after the unbalanced
    /// <c>BT</c> must still reach the page.
    /// </summary>
    [Fact]
    public void UnbalancedBeginText_DoesNotLeaveTheKnockoutLayerOpen()
    {
        byte[] pdf = Pdf("q /GS1 gs BT /F1 60 Tf 1 0 0 1 5 20 Tm (H) Tj Q 0 0 1 rg 60 60 30 30 re f",
                         "/ca 0.5");
        using var bmp = PixelAssert.Render(pdf);

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 75, 75)),
            "content after an unbalanced BT was lost — the knockout layer was never closed");
    }

    /// <summary>
    /// The knockout layer must not swallow the text clip. <c>EndTextClip</c> applies the
    /// accumulated glyph outline with <c>SKCanvas.ClipPath</c>, so it has to run <b>after</b> the
    /// layer is closed — a <c>Restore</c> that pops the layer pops any clip set inside it too.
    /// With the two in the wrong order the clip vanished for exactly the text objects that open a
    /// layer, and the content after <c>ET</c> covered the whole page.
    ///
    /// <para>Render mode 7 (clip, no paint) at half opacity, then a full-page red fill: red may
    /// appear only where the glyph is. The corner is the assertion that fails when the clip is
    /// lost; the glyph-box sample is what stops the test passing on a page where nothing was
    /// drawn at all.</para>
    /// </summary>
    [Fact]
    public void TextClipMode_SurvivesTheKnockoutLayer()
    {
        byte[] pdf = Pdf(
            "q /GS1 gs BT /F1 72 Tf 7 Tr 1 0 0 1 10 20 Tm (H) Tj ET " +
            "1 0 0 rg 0 0 100 100 re f Q",
            "/ca 0.5");
        using var bmp = PixelAssert.Render(pdf);

        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 95, 95)),
            "the page corner is outside every glyph, so the fill after ET must have been clipped " +
            "out — it was not, which means the text clip was lost with the knockout layer");
        Assert.True(PixelAssert.CountInk(bmp) > 100,
            "nothing was painted at all, so the assertion above is vacuous");
    }

    // ----------------------------------------------------------------

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    private static XDocument Svg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return XDocument.Parse(PdfOperations.RenderPageToSvg(ms, 0));
    }

    /// <summary>The <c>&lt;g opacity&gt;</c> groups the export holds — only the knockout emulation writes one here.</summary>
    private static List<XElement> Groups(XDocument svg)
        => svg.Descendants(SvgNs + "g").Where(g => g.Attribute("opacity") != null).ToList();

    private static List<string> SvgReports(byte[] pdf) => Collect(() =>
    {
        using var ms = new MemoryStream(pdf);
        PdfOperations.RenderPageToSvg(ms, 0);
    });

    /// <summary>
    /// Collects <c>RenderParameterIgnored</c> details raised on the calling thread only —
    /// <c>RenderDiagnostics.OnEvent</c> is process-wide, so an unfiltered handler would make this
    /// a claim about whatever else the test host is rendering.
    /// </summary>
    private static List<string> Collect(Action render)
    {
        var reports = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != own) return;
            if (kind == RenderDiagnosticKind.RenderParameterIgnored)
                lock (reports) reports.Add(detail ?? "");
        }

        RenderDiagnostics.OnEvent += Sink;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        lock (reports) return new List<string>(reports);
    }

    /// <summary>
    /// One text object showing the same glyph <paramref name="shows"/> times at one text matrix,
    /// under an optional ExtGState.
    /// </summary>
    private static byte[] TextPdf(int shows, string extGs)
    {
        var content = new StringBuilder();
        content.Append("q ");
        if (extGs != null) content.Append("/GS1 gs ");
        content.Append("BT /F1 72 Tf ");
        for (int i = 0; i < shows; i++) content.Append("1 0 0 1 10 20 Tm (H) Tj ");
        content.Append("ET Q");
        return Pdf(content.ToString(), extGs);
    }

    private static byte[] Pdf(string content, string extGs, string extGs2 = null)
    {
        string resources =
            "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>" +
            (extGs != null
                ? $" /ExtGState << /GS1 << /Type /ExtGState {extGs} >>" +
                  (extGs2 != null ? $" /GS2 << /Type /ExtGState {extGs2} >>" : "") + " >>"
                : "");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            $" /Resources << {resources} >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
