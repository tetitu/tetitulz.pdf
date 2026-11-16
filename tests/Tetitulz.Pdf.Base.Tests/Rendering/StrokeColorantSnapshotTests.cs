// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The DeviceCMYK colorant snapshot the overprint simulator reads must be cleared when a
/// <b>non-CMYK</b> device colour operator replaces the colour.
///
/// <para><b>The fill half had the rule and the stroke half did not.</b> <c>OpSetGrayFill</c> and
/// <c>OpSetRgbFill</c> both cleared <c>FillCmyk</c>; <c>OpSetGrayStroke</c> and
/// <c>OpSetRgbStroke</c> left <c>StrokeCmyk</c> holding the colorants of whatever <c>K</c> came
/// before. <c>PdfRenderContext.Transparency.cs:529</c> then composited a grey or RGB stroke against
/// colorants the file had already replaced.</para>
///
/// <para>This is the shape a duplication sweep nearly walks past: the rule is <i>missing</i> from
/// one of a pair rather than written twice, so there is no second body to find. It turned up only
/// because the pair was read side by side.</para>
/// </summary>
public sealed class StrokeColorantSnapshotTests
{
    /// <summary>
    /// <c>1 0 0 0 K</c> (cyan) then <c>0 G</c> (black) then a stroke, with overprint simulation on.
    /// The stroke is DeviceGray, so overprint must not composite it as if it were still cyan — and
    /// with a stale snapshot the cyan colorant survives and the line paints cyan-ish rather than
    /// black.
    /// </summary>
    [Fact]
    public void GrayStrokeAfterACmykStroke_DoesNotInheritTheCmykColorants()
    {
        using var bmp = Render("1 0 0 0 K 0 G 8 w 10 45 m 80 45 l S");

        SKColor c = PixelAssert.At(bmp, 45, 45);
        Assert.True(c.Red < 60 && c.Green < 60 && c.Blue < 60,
            $"a DeviceGray 0 stroke must paint black, not the previous CMYK colorants; got {c}");
    }

    /// <summary>The RGB counterpart — a red stroke after a cyan one must be red.</summary>
    [Fact]
    public void RgbStrokeAfterACmykStroke_DoesNotInheritTheCmykColorants()
    {
        using var bmp = Render("1 0 0 0 K 1 0 0 RG 8 w 10 45 m 80 45 l S");

        SKColor c = PixelAssert.At(bmp, 45, 45);
        Assert.True(c.Red > 180, $"the stroke should be red; got {c}");
        Assert.True(c.Green < 90, $"cyan's green colorant must not survive into it; got {c}");
    }

    /// <summary>
    /// The control: a CMYK stroke with overprint on still overprints. Without this, the two tests
    /// above would pass for a renderer that had simply stopped simulating overprint on strokes.
    /// </summary>
    [Fact]
    public void ACmykStroke_StillOverprints()
    {
        // Magenta over cyan: with overprint the crossing keeps cyan's channel.
        using var bmp = Render("1 0 0 0 K 8 w 10 45 m 80 45 l S " +
                               "0 1 0 0 K 8 w 45 10 m 45 80 l S");

        SKColor crossing = PixelAssert.At(bmp, 45, 45);
        Assert.True(crossing.Red < 120,
            $"cyan and magenta overprinted should both contribute; got {crossing}");
    }

    // ------------------------------------------------------------------

    private static SKBitmap Render(string content)
    {
        using var ms = new MemoryStream(Pdf(content));
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72,
            new PdfRenderOptions { SimulateOverprint = true });
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>
    /// A 90x90 page with <c>/OPM 1</c> and both overprint flags set, which is what
    /// §11.7.4.2's nonzero-overprint rule and the raster path's own opt-in both require.
    /// </summary>
    private static byte[] Pdf(string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << /ExtGState << /OP << /Type /ExtGState /OP true /op true /OPM 1 >> >> >> >>",
            Stream("", "/OP gs " + content));

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

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
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
