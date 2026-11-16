// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A <c>/Decode</c> bound written as an indirect reference (§7.3.10) is resolved, in both renderers.
/// </summary>
/// <remarks>
/// <para><b>Both renderers read the array elements with a plain <c>as PdfNumber</c> cast, which
/// yields <c>null</c> for <c>6 0 R</c> — and their fallbacks differed, so the same file drew two
/// different pictures and only one of them was wrong.</b> The raster path fell through to
/// <c>0.0</c>, making the decode maximum zero, so every sample of an <c>/Indexed</c> image mapped
/// to <c>palette[0]</c> and the picture came out a flat colour. The SVG path fell through to
/// <c>maxVal</c>, which for an 8-bit image <i>is</i> the correct maximum — so it produced the right
/// export from the wrong reasoning, and would have started producing the wrong one the moment the
/// bit depth changed.</para>
///
/// <para><b>Why the assertion is on pixels.</b> A flat-colour image is a perfectly valid PNG of the
/// right dimensions; nothing structural separates it from the correct render. The fixture paints a
/// two-entry palette across a two-pixel image so that "the decode bound was read" and "it was not"
/// differ in colour at a named point.</para>
/// </remarks>
public sealed class IndirectDecodeBoundTests
{
    /// <summary>
    /// The raster path. Red before the fix: both pixels came back as palette entry 0.
    /// </summary>
    [Fact]
    public void TheRasterRendererResolvesAnIndirectDecodeBound()
    {
        using var bitmap = RenderFirstPage(PdfWithIndirectDecodeBound());

        SKColor left = SampleNear(bitmap, 0.25f);
        SKColor right = SampleNear(bitmap, 0.75f);

        // Palette entry 0 is red, entry 255 is green. Identity decode ⇒ the two halves differ.
        Assert.True(left.Red > 150 && left.Green < 100,
            $"left half should be palette entry 0 (red), was {left}");
        Assert.True(right.Green > 150 && right.Red < 100,
            $"right half should be palette entry 255 (green), was {right} — a flat image means the " +
            "decode maximum read as zero");
    }

    /// <summary>
    /// The premise, and the control that stops the case above from passing on a fixture whose
    /// image never reached the palette at all: written DIRECTLY, the same document renders the
    /// same two colours.
    /// </summary>
    [Fact]
    public void ADirectDecodeBoundRendersTheSameAsAnIndirectOne()
    {
        using var indirect = RenderFirstPage(PdfWithIndirectDecodeBound());
        using var direct = RenderFirstPage(PdfWithDirectDecodeBound());

        Assert.Equal(ToRgb(SampleNear(direct, 0.25f)), ToRgb(SampleNear(indirect, 0.25f)));
        Assert.Equal(ToRgb(SampleNear(direct, 0.75f)), ToRgb(SampleNear(indirect, 0.75f)));

        // …and the control really does show two different colours, or the equality above is vacuous.
        Assert.NotEqual(ToRgb(SampleNear(direct, 0.25f)), ToRgb(SampleNear(direct, 0.75f)));
    }

    /// <summary>
    /// <b>The parity assertion.</b> The SVG exporter must reach the same palette entries. It read
    /// the same array through the same non-resolving cast; its fallback merely happened to be
    /// right, which is exactly the state this repo treats as a defect rather than a coincidence.
    /// </summary>
    [Fact]
    public void TheSvgExporterAgreesWithTheRasterRendererOnTheResolvedBound()
    {
        string svg;
        using (var input = new MemoryStream(PdfWithIndirectDecodeBound()))
            svg = PdfSvgRenderer.RenderPageToSvg(input, 0);

        Assert.False(string.IsNullOrEmpty(svg));

        // The image is exported as an embedded raster; decoding it is what makes this comparable
        // to the raster render rather than a substring check on markup.
        SKBitmap exported = DecodeEmbeddedImage(svg);
        Assert.NotNull(exported);
        using (exported)
        {
            SKColor left = SampleNear(exported, 0.25f);
            SKColor right = SampleNear(exported, 0.75f);
            Assert.True(left.Red > 150 && left.Green < 100, $"SVG left half was {left}");
            Assert.True(right.Green > 150 && right.Red < 100, $"SVG right half was {right}");
        }
    }

    /// <summary>
    /// <b>The case that separates the SVG fix from its own fallback.</b> With a decode maximum that
    /// is <i>not</i> the bit-depth maximum, <c>?? maxVal</c> stops being accidentally right.
    /// </summary>
    /// <remarks>
    /// Reverting the SVG resolution survives every fixture above, because at 8 bpc the fallback
    /// <c>maxVal</c> is 255 — the very number the indirect object holds. Here the bound is
    /// <b>3</b>, so the three possible states are three different colours: resolved gives
    /// <c>palette[3]</c> (blue), the SVG fallback gives <c>palette[255]</c> (green), and the raster
    /// fallback of <c>0.0</c> gives <c>palette[0]</c> (red). A surviving mutant was a finding about
    /// the fixture, not about the fix.
    /// </remarks>
    [Fact]
    public void BothRenderersResolveABoundThatIsNotTheBitDepthMaximum()
    {
        byte[] pdf = BuildPdf("[0 8 0 R]", indirectBound: true, boundValue: 3);

        using (var bitmap = RenderFirstPage(pdf))
        {
            SKColor right = SampleNear(bitmap, 0.75f);
            Assert.True(right.Blue > 150 && right.Green < 100 && right.Red < 100,
                $"raster right half should be palette entry 3 (blue), was {right}");
        }

        string svg;
        using (var input = new MemoryStream(pdf))
            svg = PdfSvgRenderer.RenderPageToSvg(input, 0);

        SKBitmap exported = DecodeEmbeddedImage(svg);
        Assert.NotNull(exported);
        using (exported)
        {
            SKColor right = SampleNear(exported, 0.75f);
            Assert.True(right.Blue > 150 && right.Green < 100 && right.Red < 100,
                $"SVG right half should be palette entry 3 (blue), was {right} — green means the " +
                "bound fell through to the bit-depth maximum");
        }
    }

    // ------------------------------------------------------------------

    private static SKBitmap RenderFirstPage(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToImage(input, 0, PdfImageFormat.Png, 72);
        Assert.NotNull(png);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>Samples the middle row at a fractional width, avoiding the very edges.</summary>
    private static SKColor SampleNear(SKBitmap bmp, float fractionAcross)
    {
        int x = Math.Clamp((int)(bmp.Width * fractionAcross), 0, bmp.Width - 1);
        int y = bmp.Height / 2;
        return bmp.GetPixel(x, y);
    }

    private static (byte, byte, byte) ToRgb(SKColor c) => (c.Red, c.Green, c.Blue);

    private static SKBitmap DecodeEmbeddedImage(string svg)
    {
        const string marker = "base64,";
        int start = svg.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;

        int end = svg.IndexOfAny(new[] { '"', '\'' }, start);
        if (end < 0) return null;

        byte[] bytes = Convert.FromBase64String(svg.Substring(start, end - start));
        return SKBitmap.Decode(bytes);
    }

    private static byte[] PdfWithIndirectDecodeBound() => BuildPdf("[0 8 0 R]", indirectBound: true);
    private static byte[] PdfWithDirectDecodeBound() => BuildPdf("[0 255]", indirectBound: false);

    /// <summary>
    /// A one-page document drawing a 2×1 <c>/Indexed</c> image whose two samples are 0 and 255,
    /// against a palette that is <b>red at 0, blue at 3 and green at 255</b> — three entries so
    /// that the correct answer, the SVG fallback and the raster fallback are three distinct
    /// colours rather than two.
    /// </summary>
    private static byte[] BuildPdf(string decodeArray, bool indirectBound, int boundValue = 255)
    {
        var lookup = new byte[256 * 3];
        lookup[0] = 0xFF;                     // entry 0   → red
        lookup[3 * 3 + 2] = 0xFF;             // entry 3   → blue
        lookup[255 * 3 + 1] = 0xFF;           // entry 255 → green

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 50] /Contents 4 0 R "
          + "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");

        const string content = "q 100 0 0 50 0 0 cm /Im0 Do Q\n";
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");

        var img = new StringBuilder();
        img.Append("5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 1 ");
        img.Append("/BitsPerComponent 8 /ColorSpace [/Indexed /DeviceRGB 255 6 0 R] ");
        img.Append($"/Decode {decodeArray} /Length 2 >>\nstream\n");
        Obj(img.ToString());
        int imgStart = offsets[offsets.Count - 1];
        sb.Append((char)0x00).Append((char)0xFF);      // the two samples: index 0 and index 255
        sb.Append("\nendstream\nendobj\n");
        _ = imgStart;

        var lut = new StringBuilder();
        lut.Append($"6 0 obj\n<< /Length {lookup.Length} >>\nstream\n");
        Obj(lut.ToString());
        foreach (byte b in lookup) sb.Append((char)b);
        sb.Append("\nendstream\nendobj\n");

        Obj("7 0 obj\n<< /Placeholder true >>\nendobj\n");
        Obj(indirectBound ? $"8 0 obj\n{boundValue}\nendobj\n" : "8 0 obj\n0\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        // Latin-1: every char written above is a single byte by construction.
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
