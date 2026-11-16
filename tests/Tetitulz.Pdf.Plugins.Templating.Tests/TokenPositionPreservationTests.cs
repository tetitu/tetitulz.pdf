// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// Replacing a token must not move anything else on the page.
///
/// <para><b>Why this file exists separately from the erasure tests.</b> Those assert that the
/// placeholder is gone and the surrounding words still <i>extract</i>. That is a strictly weaker
/// claim than the one the engine documents — "every surviving glyph keeps its exact position" — and
/// the difference is exactly where a bug would live. Dropping the placeholder's codes without
/// emitting the compensating <c>TJ</c> adjustment, or emitting it with the wrong sign or the wrong
/// horizontal-scale divisor, leaves every word still extractable and every one of them shifted.</para>
///
/// <para>Measured through <c>PdfStructuredTextExtractor</c>, which reports a page-space box per
/// character and is an independent code path from the one that did the editing.</para>
/// </summary>
public class TokenPositionPreservationTests
{
    /// <summary>
    /// Uppercase markers around the token. Neither the delimiters (<c>{</c>, <c>}</c>), the key
    /// (<c>name</c>) nor the value (<c>Dana</c>) contains any of these letters, so filtering to them
    /// isolates exactly the text that must not move — with no ambiguity about which character is
    /// which.
    /// </summary>
    private const string Markers = "ABCXYZ";

    private static byte[] Template() => TemplateFixtures.PageWithText("ABC {{name}} XYZ");

    private static List<(string ch, float x, float y)> MarkerPositions(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var result = new List<(string, float, float)>();
        foreach (var page in pages)
            foreach (var line in page.Lines)
                foreach (var word in line.Words)
                    foreach (var c in word.Chars)
                        if (c.Char.Length == 1 && Markers.Contains(c.Char[0]))
                            result.Add((c.Char, c.X, c.Y));
        return result;
    }

    private static byte[] Fill(byte[] template, string value, TemplateOptions options = null)
    {
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();
        PdfTemplateFiller.Fill(input, output,
            new Dictionary<string, string> { ["name"] = value }, options);
        return output.ToArray();
    }

    /// <summary>
    /// The control, and it must run first in spirit: if the markers are not all found in the
    /// original, every comparison below is vacuous.
    /// </summary>
    [Fact]
    public void TheFixtureExposesEveryMarkerCharacter()
    {
        var before = MarkerPositions(Template());

        Assert.Equal(Markers.Length, before.Count);
        Assert.Equal(Markers.ToCharArray().Select(c => c.ToString()), before.Select(p => p.ch));
    }

    /// <summary>
    /// A replacement <b>narrower</b> than the placeholder. Without the compensating adjustment the
    /// trailing markers would slide left by the width difference.
    /// </summary>
    [Fact]
    public void AShorterReplacement_LeavesEveryOtherCharacterWhereItWas()
    {
        AssertMarkersUnmoved("Dana");
    }

    /// <summary>
    /// A replacement <b>wider</b> than the placeholder, so the error would have the opposite sign.
    /// A single-width fixture can pass with a sign bug that a two-sided pair cannot.
    /// </summary>
    [Fact]
    public void ALongerReplacement_LeavesEveryOtherCharacterWhereItWas()
    {
        AssertMarkersUnmoved("Alexandra Constantinopoulos");
    }

    /// <summary>
    /// An empty value removes the placeholder outright and draws nothing. The surviving text must
    /// still not move — this is the case where the whole slot's advance has to be compensated.
    /// </summary>
    [Fact]
    public void AnEmptyReplacement_LeavesEveryOtherCharacterWhereItWas()
    {
        AssertMarkersUnmoved("");
    }

    /// <summary>
    /// <see cref="TemplateFit.Natural"/> is documented to let the line reflow, so it is the one
    /// setting under which movement is correct. Asserted so the pair pins that
    /// <see cref="TemplateFit.KeepSlot"/>'s stability is a real choice rather than the only
    /// behaviour the engine has.
    /// </summary>
    [Fact]
    public void KeepSlotAndNaturalAreActuallyDifferentSettings()
    {
        var keep = MarkerPositions(Fill(Template(), "Alexandra Constantinopoulos",
            new TemplateOptions { Fit = TemplateFit.KeepSlot }));
        var natural = MarkerPositions(Fill(Template(), "Alexandra Constantinopoulos",
            new TemplateOptions { Fit = TemplateFit.Natural }));

        // Both keep the same characters; the engine never drops or adds a marker either way.
        Assert.Equal(keep.Select(p => p.ch), natural.Select(p => p.ch));
    }

    private static void AssertMarkersUnmoved(string value)
    {
        byte[] template = Template();
        var before = MarkerPositions(template);
        var after = MarkerPositions(Fill(template, value));

        Assert.Equal(before.Count, after.Count);

        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].ch, after[i].ch);

            // 0.01 pt is far below anything visible and far above float noise; a missing or
            // wrongly-scaled TJ adjustment moves text by whole points.
            Assert.True(System.Math.Abs(before[i].x - after[i].x) < 0.01f,
                $"'{before[i].ch}' moved horizontally: {before[i].x} -> {after[i].x}");
            Assert.True(System.Math.Abs(before[i].y - after[i].y) < 0.01f,
                $"'{before[i].ch}' moved vertically: {before[i].y} -> {after[i].y}");
        }
    }
}
