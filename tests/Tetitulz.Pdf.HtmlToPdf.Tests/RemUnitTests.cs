// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>rem</c> resolves against the <b>document's</b> root font size, not a hardcoded 12pt.
/// </summary>
/// <remarks>
/// <para>Mapped to <c>n * DefaultFontSizePt</c> — a fixed 12pt root — <c>rem</c> would make
/// <c>html { font-size: 62.5% }</c>, the common "1rem = 10px" idiom, and every other custom root
/// size produce silently wrong lengths everywhere <c>rem</c> appears: font sizes, margins,
/// padding, indents, letter-spacing.</para>
/// <para><b>The assertion is on rendered glyph height</b>, measured from the produced PDF, not on
/// the presence of an element. A length bug changes sizes, and a structural assertion cannot see
/// that, however well it covers <c>rem</c>'s existence.</para>
/// <para><b><c>rem</c> is root-em, so <c>body { font-size }</c> must not move it.</b> That is the
/// one behaviour separating it from <c>em</c>, and the third test is the one that would fail if the
/// root size were captured after <c>&lt;body&gt;</c> was folded in — which is exactly where the
/// obvious implementation puts it.</para>
/// </remarks>
public class RemUnitTests
{
    [Fact]
    public void Rem_FollowsAnExplicitRootFontSize()
    {
        // 2rem against a 20pt root is 40pt; against the old hardcoded 12pt root it was 24pt.
        double big   = FirstGlyphHeight("<html style='font-size:20pt'><body><p style='font-size:2rem'>Xg</p></body></html>");
        double small = FirstGlyphHeight("<html style='font-size:10pt'><body><p style='font-size:2rem'>Xg</p></body></html>");

        Assert.True(big > small * 1.5,
            $"2rem measured {big:F1}pt against a 20pt root and {small:F1}pt against a 10pt root; " +
            "rem is not following the root font size");
    }

    [Fact]
    public void Rem_WithNoRootFontSize_KeepsTheTwelvePointDefault()
    {
        // The compatibility half: a document that sets no root size must render as it always did.
        double rem = FirstGlyphHeight("<html><body><p style='font-size:2rem'>Xg</p></body></html>");
        double pt  = FirstGlyphHeight("<html><body><p style='font-size:24pt'>Xg</p></body></html>");

        Assert.True(Math.Abs(rem - pt) < 0.5,
            $"2rem measured {rem:F1}pt but 24pt measured {pt:F1}pt; the default root is not 12pt");
    }

    [Fact]
    public void Rem_IsRootEm_SoABodyFontSizeDoesNotMoveIt()
    {
        // The distinction from `em`. Capturing the root size AFTER folding <body> in would make
        // these two differ, and that is the natural place to put the capture.
        double withBody = FirstGlyphHeight(
            "<html style='font-size:10pt'><body style='font-size:40pt'><p style='font-size:2rem'>Xg</p></body></html>");
        double without = FirstGlyphHeight(
            "<html style='font-size:10pt'><body><p style='font-size:2rem'>Xg</p></body></html>");

        Assert.True(Math.Abs(withBody - without) < 0.5,
            $"2rem measured {withBody:F1}pt under a 40pt body and {without:F1}pt without it; " +
            "rem is being resolved as em");
    }

    /// <summary>Height of the first laid-out glyph, in points, from the produced PDF.</summary>
    private static double FirstGlyphHeight(string html)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output,
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        var pages = PdfOperations.ExtractStructuredText(input);

        PdfTextChar glyph = pages
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .SelectMany(w => w.Chars)
            .FirstOrDefault();

        Assert.True(glyph != null, "no glyph was laid out, so nothing was measured");
        return glyph!.Height;
    }
}
