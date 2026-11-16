// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Glyph advances for a font the PDF gives no <c>/Widths</c> for — §9.6.2.2's "the widths come
/// from the font program" case, which a standard-14 font is entitled to leave out entirely.
/// </summary>
/// <remarks>
/// <para><b>This path had no test.</b> Deleting the Skia measurement outright — so every glyph
/// falls back to the <c>0.5 × size</c> guess beside it — left all 3 110 Base tests green. A
/// standard-14 font with no <c>/Widths</c> is not an edge case; it is what a minimal generator
/// emits, and every glyph on such a page would have been laid out at a uniform half-em pitch
/// while the suite stayed silent.</para>
///
/// <para><b>Why the assertion is a comparison and not a position.</b> Absolute advances depend on
/// the host's font substitution, which differs between Windows and a bare Linux container — the
/// reason <c>SystemFontFact</c> exists. What does not differ is that a real metric set is
/// <i>proportional</i>: a run of <c>i</c> is far narrower than a run of <c>W</c> at the same size.
/// Under the half-em fallback the two are exactly equal, because the fallback ignores the glyph.
/// So the comparison discriminates on every host while the number would not.</para>
/// </remarks>
public class MeasuredGlyphWidthTests
{
    /// <summary>
    /// A run of narrow glyphs must occupy visibly less width than the same number of wide ones.
    /// </summary>
    [Fact]
    public void GlyphsWithoutPdfWidthsAreMeasured_NotGivenAUniformHalfEm()
    {
        int narrow = InkWidth(TextPage(new string('i', 20)));
        int wide = InkWidth(TextPage(new string('W', 20)));

        Assert.True(narrow > 0, "the narrow run drew no ink");
        Assert.True(wide > 0, "the wide run drew no ink");

        // Helvetica: 'i' is 222/1000 em and 'W' is 944/1000, a ratio over four. Even a substitute
        // face keeps them far apart. The half-em fallback makes the two runs identical, so any
        // clear separation is enough to pin the behaviour without pinning the host's font.
        Assert.True(wide > narrow * 2,
            $"20 W is {wide}px and 20 i is {narrow}px — under 2x apart, so the advances are not "
            + "coming from the font's own metrics");
    }

    /// <summary>Width in pixels between the first and last columns carrying ink.</summary>
    private static int InkWidth(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf, false), 0, 150);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int first = -1, last = -1;
        for (int x = 0; x < bmp.Width; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha != 0 && (c.Red < 128 || c.Green < 128 || c.Blue < 128))
                {
                    if (first < 0) first = x;
                    last = x;
                    break;
                }
            }
        return first < 0 ? 0 : last - first + 1;
    }

    /// <summary>
    /// One line of <paramref name="text"/> in Helvetica, declared <b>without</b> <c>/Widths</c>,
    /// <c>/FirstChar</c> or a <c>/FontDescriptor</c> — the minimal §9.6.2.2 standard-14 font
    /// dictionary, which is exactly the shape that has to be measured rather than read.
    /// </summary>
    private static byte[] TextPage(string text)
    {
        string content = $"BT /F1 24 Tf 40 700 Td ({text}) Tj ET\n";

        var b = new StringBuilder();
        b.Append("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }
}
