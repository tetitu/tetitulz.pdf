// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// The box a glyph is tested against — its height below and above the baseline, the half-em width
/// it falls back to when the font cannot say, and the margin every box is inflated by.
/// </summary>
/// <remarks>
/// <para>All three came out of a mutation run as decisions that <b>no</b> redaction test in either
/// <c>Base.Tests</c> or this suite reached. They share a failure mode: every existing fixture puts
/// the region squarely over the middle of the text, where the glyph box is hit however it is
/// computed. Only a region that grazes an <i>edge</i> of the box can tell these apart, and each one
/// below targets a different edge.</para>
///
/// <para>The arithmetic is deliberately exact rather than approximate. With
/// <c>Tf 12</c> at <c>Td 100 700</c>, the glyph box runs from <c>700 + 12 × -0.25 = 697</c> to
/// <c>700 + 12 × 0.95 = 711.4</c>, and every box is then inflated by one point. Each region below
/// is placed so the correct box reaches it and the mutated box does not — which is what makes these
/// refusals refuse for the reason in their names.</para>
/// </remarks>
public class GlyphBoxRedactionTests
{
    private const string Secret = "SECRET";

    /// <summary>
    /// <b>The descender.</b> A region lying entirely <i>below</i> the baseline still covers the
    /// part of the glyph that hangs there. Box bottom is 697; inflated, 696. The region 695..698
    /// is reached by that and not by a box that starts at the baseline (700, inflated 699).
    /// </summary>
    [Fact]
    public void AReadactionBelowTheBaselineStillCoversTheGlyphsDescender()
    {
        byte[] pdf = TextPdf(width: 500);

        string content = RedactAndReadContent(pdf, x: 100, y: 695, w: 60, h: 3);

        Assert.DoesNotContain(Secret, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The ascender edge, and the one-point margin past it.</b> Box top is 711.4; the margin
    /// carries it to 712.4, which reaches a region starting at 712. Without the margin the box
    /// stops short and the glyph leaks.
    /// </summary>
    /// <remarks>
    /// The margin exists so advance-width drift never causes a missed glyph, and "missed" here
    /// means the secret stays in a file the caller was told had been redacted.
    /// </remarks>
    [Fact]
    public void AReadactionJustBeyondTheGlyphBoxIsStillCoveredByTheMargin()
    {
        byte[] pdf = TextPdf(width: 500);

        string content = RedactAndReadContent(pdf, x: 100, y: 712, w: 60, h: 2);

        Assert.DoesNotContain(Secret, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The half-em fallback.</b> This font declares a width of <b>zero</b> for every glyph, so
    /// the engine cannot measure the run and falls back to half an em — 6 points at
    /// <c>Tf 12</c>. The region 102..105 sits inside that fallback box and outside a zero-width
    /// one (which the margin alone carries only to 101).
    /// </summary>
    [Fact]
    public void AGlyphWhoseFontDeclaresNoWidthIsStillCovered()
    {
        byte[] pdf = TextPdf(width: 0);

        string content = RedactAndReadContent(pdf, x: 102, y: 700, w: 3, h: 6);

        Assert.DoesNotContain(Secret, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance control. A region nowhere near the text leaves it alone — without this, every
    /// refusal above would also pass on an engine that removed all text unconditionally.
    /// </summary>
    [Fact]
    public void AReadactionFarFromTheTextLeavesItAlone()
    {
        byte[] pdf = TextPdf(width: 500);

        string content = RedactAndReadContent(pdf, x: 100, y: 200, w: 60, h: 20);

        Assert.Contains(Secret, content, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static string RedactAndReadContent(byte[] pdf, double x, double y, double w, double h)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = x, Y = y, Width = w, Height = h },
        });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData() ?? Array.Empty<byte>());
    }

    /// <summary>
    /// One page showing <see cref="Secret"/> at 100,700 in a 12-point simple font whose every glyph
    /// is declared <paramref name="width"/> glyph-space units wide. Zero means the font cannot
    /// measure the run, which is the case the half-em fallback exists for.
    /// </summary>
    private static byte[] TextPdf(int width)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string content = $"BT /F1 12 Tf 100 700 Td ({Secret}) Tj ET";
        var widths = new List<string>();
        for (int c = 32; c <= 126; c++) widths.Add(width.ToString(System.Globalization.CultureInfo.InvariantCulture));

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 " +
            "/LastChar 126 /Widths [" + string.Join(" ", widths) + "] >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
