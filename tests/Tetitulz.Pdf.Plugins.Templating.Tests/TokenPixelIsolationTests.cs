// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// Replacing a token must not change a single <b>pixel</b> outside the line it edits.
///
/// <para><b>Why this exists alongside the geometric check.</b> The engine verifies its own output by
/// re-walking the rewritten stream and confirming every non-token glyph is at the same position.
/// That proves geometry and nothing else: identical glyph positions are not identical pixels. A text
/// render mode, a clip path or a pattern fill can leave every position intact and still repaint the
/// page. This renders both documents and compares the actual bytes.</para>
///
/// <para><b>Honest limit on that claim.</b> No mutation was found where these tests fail and the
/// geometric self-check passes. The obvious candidate — dropping the <c>Q</c> that closes the
/// inserted run, so its fill colour leaks — turns out to be <i>equivalent by construction</i>: the
/// run adopts the colour already in force at the placeholder, so leaking it changes nothing. So this
/// file is a stronger statement of the same guarantee, verified end to end through the renderer,
/// rather than a demonstrated superset. Do not delete it on the strength of the geometric check
/// alone; do not claim it catches strictly more either.</para>
///
/// <para>The fixture puts an untouched second line well below the edited one, so the comparison
/// region is unambiguous and does not depend on knowing the token's exact glyph extents.</para>
/// </summary>
public class TokenPixelIsolationTests
{
    /// <summary>72 dpi keeps 1 pt = 1 px, so the excluded band can be reasoned about directly.</summary>
    private const int Dpi = 72;

    /// <summary>Device rows covering the edited line's baseline at y=700 (device y = 792-700 = 92).</summary>
    private const int EditedBandTop = 68;
    private const int EditedBandBottom = 108;

    private static byte[] TwoLineTemplate() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        // The edited line is red and the line below explicitly black: a coloured template is
        // realistic, and it exercises the path where the replacement run inherits the token's own
        // fill colour rather than defaulting to black.
        TemplateFixtures.StreamBody(string.Empty,
            "BT 1 0 0 rg /F1 24 Tf 72 700 Td (ABC {{name}} XYZ) Tj ET\n" +
            "BT 0 g /F1 24 Tf 72 650 Td (UNTOUCHED LINE BELOW) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    private static RenderedPage Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfPageRenderer.RenderPageToPixels(ms, 0, Dpi);
    }

    private static byte[] Fill(byte[] template, string value, TemplateOptions options = null)
    {
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();
        PdfTemplateFiller.Fill(input, output,
            new Dictionary<string, string> { ["name"] = value }, options);
        return output.ToArray();
    }

    /// <summary>Non-white pixels in a row band — the ink the fixture actually put on the page.</summary>
    private static int InkInBand(RenderedPage page, int top, int bottom)
    {
        int ink = 0;
        for (int y = System.Math.Max(0, top); y < System.Math.Min(page.Height, bottom); y++)
        {
            for (int x = 0; x < page.Width; x++)
            {
                int o = y * page.Stride + x * 4;
                if (page.Pixels[o] < 200 || page.Pixels[o + 1] < 200 || page.Pixels[o + 2] < 200) ink++;
            }
        }
        return ink;
    }

    /// <summary>
    /// The fixture-validity control, and it runs first for a reason: if the page renders blank —
    /// no font on this host, a broken fixture — then every pixel comparison below is vacuously true
    /// and the suite would report success while measuring nothing.
    /// </summary>
    [Fact]
    public void TheFixtureActuallyDrawsInkOnBothLines()
    {
        var page = Render(TwoLineTemplate());

        Assert.True(InkInBand(page, EditedBandTop, EditedBandBottom) > 0,
            "the edited line rendered blank; the pixel comparison would be vacuous");
        Assert.True(InkInBand(page, EditedBandBottom, page.Height) > 0,
            "the untouched line rendered blank; the pixel comparison would be vacuous");
    }

    /// <summary>
    /// The headline property: every row outside the edited line is byte-identical.
    /// </summary>
    [Fact]
    public void EveryPixelOutsideTheEditedLineIsUnchanged()
    {
        byte[] template = TwoLineTemplate();
        var before = Render(template);
        var after = Render(Fill(template, "Dana"));

        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.Stride, after.Stride);

        for (int y = 0; y < before.Height; y++)
        {
            if (y >= EditedBandTop && y < EditedBandBottom) continue;   // the line being edited

            for (int x = 0; x < before.Width; x++)
            {
                int o = y * before.Stride + x * 4;
                for (int c = 0; c < 4; c++)
                {
                    Assert.True(before.Pixels[o + c] == after.Pixels[o + c],
                        $"pixel ({x},{y}) channel {c} changed: " +
                        $"{before.Pixels[o + c]} -> {after.Pixels[o + c]}");
                }
            }
        }
    }

    /// <summary>
    /// The counterpart, without which the test above passes for an engine that does nothing at all:
    /// the edited line <b>must</b> have changed.
    /// </summary>
    [Fact]
    public void TheEditedLineDidChange()
    {
        byte[] template = TwoLineTemplate();
        var before = Render(template);
        var after = Render(Fill(template, "Dana"));

        int differing = 0;
        for (int y = EditedBandTop; y < EditedBandBottom; y++)
        {
            for (int x = 0; x < before.Width; x++)
            {
                int o = y * before.Stride + x * 4;
                // All four channels, not just the first. The edited line is red, so its glyphs and
                // the white background share R=255 and a red-channel-only comparison sees nothing
                // change however much text moves.
                if (before.Pixels[o] != after.Pixels[o] ||
                    before.Pixels[o + 1] != after.Pixels[o + 1] ||
                    before.Pixels[o + 2] != after.Pixels[o + 2] ||
                    before.Pixels[o + 3] != after.Pixels[o + 3]) differing++;
            }
        }

        Assert.True(differing > 0, "the edited line is pixel-identical; nothing was replaced");
    }

    /// <summary>
    /// A longer replacement stresses the compensating <c>TJ</c> adjustment hardest — the trailing
    /// marker has furthest to drift if the adjustment is wrong — and still must not touch the line
    /// below.
    /// </summary>
    [Fact]
    public void ALongerReplacementStillLeavesTheOtherLineUntouched()
    {
        byte[] template = TwoLineTemplate();
        var before = Render(template);
        var after = Render(Fill(template, "Alexandra Constantinopoulos"));

        for (int y = EditedBandBottom; y < before.Height; y++)
        {
            for (int x = 0; x < before.Width; x++)
            {
                int o = y * before.Stride + x * 4;
                for (int c = 0; c < 4; c++)
                    Assert.True(before.Pixels[o + c] == after.Pixels[o + c],
                        $"pixel ({x},{y}) channel {c} below the edited line changed: " +
                        $"{before.Pixels[o + c]} -> {after.Pixels[o + c]}");
            }
        }
    }
}
