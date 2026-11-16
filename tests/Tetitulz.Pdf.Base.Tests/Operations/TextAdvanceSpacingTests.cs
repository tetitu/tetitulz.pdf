// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// ISO 32000-1 §9.4.4's glyph advance, in the two terms nothing measured:
/// <c>tx = ((w₀ / 1000) · Tfs + Tc + Tw) · Th</c>.
/// </summary>
/// <remarks>
/// <para><b>Two mutation rows survived the whole Base suite here</b>, and neither was equivalent —
/// no extraction fixture in the tree set word spacing at all, and none combined a horizontal scale
/// with a non-zero spacing term. Applying <c>Tw</c> to every glyph rather than to the single-byte
/// space, and dropping <c>Th</c> from the spacing terms, were both invisible.</para>
///
/// <para><b>A wrong advance is invisible to anything that asserts text.</b> The characters extracted
/// are identical; only their positions move. It is invisible to a rendering parity suite too, since
/// each renderer stays internally self-consistent. The only thing that can see it is the
/// <b>position of a later glyph</b>, which is what this asserts.</para>
///
/// <para><b>The font carries explicit <c>/Widths</c> on purpose.</b> A non-embedded Helvetica with
/// no widths array falls through to the extractor's 0.6-em approximation, and an assertion about an
/// approximation pins the fallback rather than the formula.</para>
/// </remarks>
public class TextAdvanceSpacingTests
{
    // 10 Tf, 200 Tz, 2 Tc, 7 Tw, and a font where 'A' is 500/1000 em and the space is 250/1000.
    //
    //   glyph 1 'A'   at x = 0
    //     charW = 500/1000 · 10 · 2 = 10      advance = 10 + (2 + 0) · 2 = 14
    //   glyph 2 ' '   at x = 14               <- Tw does NOT apply to 'A'
    //     charW = 250/1000 · 10 · 2 =  5      advance =  5 + (2 + 7) · 2 = 23
    //   glyph 3 'A'   at x = 37               <- Tw applies here, and Th scales both terms
    private const float SpaceX  = 14f;
    private const float SecondA = 37f;

    /// <summary>
    /// <b>Word spacing applies to the single-byte space and to nothing else</b> (§9.4.4), and both
    /// spacing terms are scaled by the horizontal scale.
    /// </summary>
    [Fact]
    public void TheSpacingTermsFollowTheAdvanceFormula()
    {
        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(SpacedRunPdf()), 0);

        var chars = layout.Chars;
        Assert.Equal(new[] { "A", " ", "A" }, chars.Select(c => c.Char));

        // The first advance: Tc is scaled by Th, and Tw must NOT be added — the glyph is not a space.
        Assert.Equal(SpaceX, chars[1].X, 3);

        // The second: Tw applies, and (Tc + Tw) is scaled by Th rather than added unscaled.
        Assert.Equal(SecondA, chars[2].X, 3);
    }

    /// <summary>
    /// The control. With <c>Tc</c> and <c>Tw</c> at their defaults the same run must collapse to the
    /// widths alone, so the test above is pinning the spacing terms rather than the width lookup.
    /// </summary>
    [Fact]
    public void WithNoSpacingSetTheAdvanceIsTheWidthAlone()
    {
        var layout = PdfStructuredTextExtractor.Extract(
            new MemoryStream(SpacedRunPdf(spacing: "")), 0);

        var chars = layout.Chars;
        Assert.Equal(10f, chars[1].X, 3);   // 500/1000 · 10 · 2
        Assert.Equal(15f, chars[2].X, 3);   // + 250/1000 · 10 · 2
    }

    // ------------------------------------------------------------------

    private static byte[] SpacedRunPdf(string spacing = "2 Tc 7 Tw ")
    {
        string content = $"BT /F1 10 Tf 200 Tz {spacing}0 100 Td (A A) Tj ET";

        // /FirstChar 32 .. /LastChar 65: the space and 'A' are the only codes the run uses.
        var widths = new int[65 - 32 + 1];
        widths[0]  = 250;                 // code 32, space
        widths[65 - 32] = 500;            // code 65, 'A'
        string w = string.Join(" ", widths);

        return Build(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                + $"/FirstChar 32 /LastChar 65 /Widths [{w}] >>\nendobj\n",
        });
    }

    private static byte[] Build(string[] objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
