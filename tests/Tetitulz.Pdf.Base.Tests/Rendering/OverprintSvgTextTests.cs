// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG exporter applies its overprint approximation to <b>text</b> on the same terms as to a
/// path: <c>mix-blend-mode:multiply</c> on the painting element, only when the caller opts in,
/// under <c>/OPM 1</c>, for a DeviceCMYK source.
/// </summary>
/// <remarks>
/// <para><b>Why this was a divergence and not a shared gap.</b> The raster path composited
/// overprinting text (mode 0) per colorant while this exporter reported every overprinting run as
/// "a renderer that composites no text per colorant" and painted it opaque — so the same prepress
/// file proofed one way as a raster and another as an SVG. Text and paths are the same painting
/// operation to §11.7.4.2, and they now take the attribute from one decision.</para>
///
/// <para><b>Two emitters, two tests.</b> A non-embedded font goes out as <c>&lt;text&gt;</c>; an
/// embedded program goes out as a <c>&lt;g&gt;</c> of glyph outlines. They are separate code, and
/// a fix landing on one and not the other is the divergence this pair keeps producing — so both
/// are pinned. Assertions parse the SVG as an <see cref="XDocument"/>, never as a substring.</para>
/// </remarks>
public class OverprintSvgTextTests
{
    private const string OverprintingRun =
        "q /GS0 gs 0 0 0 1 k BT /F1 24 Tf 10 10 Td (Hi) Tj ET Q";

    private const string OverprintingStrokedRun =
        "q /GS0 gs 0 0 0 1 K BT /F1 24 Tf 1 Tr 10 10 Td (Hi) Tj ET Q";

    // ====================================================================
    //  <text> — the non-embedded font route
    // ====================================================================

    [Fact]
    public void AnOverprintingTextElement_CarriesMultiply_WhenSimulating()
    {
        XElement text = TextElement(Svg(OverprintingRun, simulate: true));

        Assert.Contains("mix-blend-mode:multiply", text.Attribute("style")?.Value ?? string.Empty,
                        StringComparison.Ordinal);
    }

    /// <summary>The stroking half takes the same decision from the same place.</summary>
    [Fact]
    public void AnOverprintingStrokedTextElement_CarriesMultiply_WhenSimulating()
    {
        XElement text = TextElement(Svg(OverprintingStrokedRun, simulate: true));

        Assert.NotEqual("none", text.Attribute("stroke")?.Value);
        Assert.Contains("mix-blend-mode:multiply", text.Attribute("style")?.Value ?? string.Empty,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The opt-in is honoured for text exactly as for paths: off by default, because overprint is
    /// a press behaviour viewers expose as a preview toggle.
    /// </summary>
    [Fact]
    public void AnOverprintingTextElement_DoesNotCarryMultiply_WhenNotSimulating()
    {
        XElement text = TextElement(Svg(OverprintingRun, simulate: false));

        Assert.DoesNotContain("multiply", text.Attribute("style")?.Value ?? string.Empty,
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance control for the ladder: a DeviceRGB source has no colorants to preserve,
    /// so the element is painted opaque and the exporter says why — the same reason a path gets.
    /// </summary>
    [Fact]
    public void AnOverprintingRgbTextElement_IsNotMultiplied_AndTheReasonIsTheColourSpace()
    {
        const string rgbRun = "q /GS0 gs 1 0 0 rg BT /F1 24 Tf 10 10 Td (Hi) Tj ET Q";

        var reasons = new System.Collections.Generic.List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId == ownThread
                && k == RenderDiagnosticKind.OverprintNotSimulated) reasons.Add(d ?? string.Empty);
        }

        string svg;
        RenderDiagnostics.OnEvent += Sink;
        try { svg = Svg(rgbRun, simulate: true); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.DoesNotContain("multiply", TextElement(svg).Attribute("style")?.Value ?? string.Empty,
                              StringComparison.Ordinal);
        Assert.Contains(reasons, d => d.Contains("not set in DeviceCMYK"));
    }

    // ====================================================================
    //  <g> of glyph outlines — the embedded font route
    // ====================================================================

    /// <summary>
    /// The glyph-outline emitter carries the same attribute on its group. The fixture's font is
    /// an embedded bare CFF, which the exporter outlines rather than naming.
    /// </summary>
    [Fact]
    public void AnOverprintingGlyphOutlineGroup_CarriesMultiply_WhenSimulating()
    {
        string content = $"q /GS0 gs 0 0 0 1 k BT /F1 48 Tf 100 400 Td ({(char)EmbeddedCffFixture.GlyphCharCode}) Tj ET Q";
        string svg = EmbeddedSvg(content, simulate: true);

        XElement group = OutlineGroup(svg);
        Assert.Contains("mix-blend-mode:multiply", group.Attribute("style")?.Value ?? string.Empty,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverprintingGlyphOutlineGroup_DoesNotCarryMultiply_WhenNotSimulating()
    {
        string content = $"q /GS0 gs 0 0 0 1 k BT /F1 48 Tf 100 400 Td ({(char)EmbeddedCffFixture.GlyphCharCode}) Tj ET Q";
        string svg = EmbeddedSvg(content, simulate: false);

        XElement group = OutlineGroup(svg);
        Assert.DoesNotContain("multiply", group.Attribute("style")?.Value ?? string.Empty,
                              StringComparison.Ordinal);
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private static string Svg(string content, bool simulate)
    {
        using var ms = new MemoryStream(OverprintReportingTests.BuildPdf(content));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = simulate });
    }

    private static string EmbeddedSvg(string content, bool simulate)
    {
        byte[] pdf = EmbeddedCffFixture.Page(content,
            extraPageResources: "/ExtGState << /GS0 8 0 R >>",
            extraObjects: "8 0 obj\n<< /Type /ExtGState /OP true /op true /OPM 1 >>\nendobj\n");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = simulate });
    }

    /// <summary>The visible <c>&lt;text&gt;</c> element — not the zero-opacity selectable overlay.</summary>
    private static XElement TextElement(string svg)
    {
        XElement[] visible = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "text")
            .Where(e => e.Attribute("fill-opacity")?.Value != "0")
            .ToArray();
        Assert.True(visible.Length > 0, "the export drew no visible <text> element:" + Environment.NewLine + svg);
        return visible[0];
    }

    /// <summary>The glyph-outline group: a <c>&lt;g&gt;</c> that paints and holds <c>&lt;use&gt;</c> children.</summary>
    private static XElement OutlineGroup(string svg)
    {
        XElement[] groups = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "g")
            .Where(e => e.Attribute("fill") != null)
            .Where(e => e.Elements().Any(c => c.Name.LocalName == "use"))
            .ToArray();
        Assert.True(groups.Length > 0, "the export drew no glyph-outline group:" + Environment.NewLine + svg);
        return groups[0];
    }
}
