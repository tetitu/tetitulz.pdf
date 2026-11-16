// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// The in-memory PDFs the JPEG 2000 consumer tests share — a page exactly an image's size, filled
/// by one <c>JPXDecode</c> image XObject, written binary-safe — and the readers of what comes back.
/// Shared rather than copied per file, so a fixture defect is fixed once.
/// </summary>
internal static class JpxTestPdf
{
    /// <summary>A page exactly the image's size, filled by one JPXDecode image XObject (object 5, /Im0).</summary>
    internal static byte[] ImagePdf(byte[] jpx, int width, int height, string extras) =>
        Pdf(width, height, $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} {extras} /Filter /JPXDecode", jpx));

    /// <summary>
    /// A one-page PDF: objects 1–4 are the catalog, pages, page and content; each image is object
    /// 5 + k, reachable as /Im&lt;k&gt;. <paramref name="pageExtras"/> goes into the page
    /// dictionary (a <c>/Thumb</c>, say). Binary-safe: the streams are written byte for byte.
    /// </summary>
    internal static byte[] Pdf(int pageWidth, int pageHeight, string content, params (string dict, byte[] stream)[] images) =>
        Pdf(pageWidth, pageHeight, content, "", images);

    internal static byte[] Pdf(int pageWidth, int pageHeight, string content, string pageExtras, params (string dict, byte[] stream)[] images)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        var xobjects = new StringBuilder();
        for (int k = 0; k < images.Length; k++) xobjects.Append($"/Im{k} {5 + k} 0 R ");

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Resources << /XObject << {xobjects}>> >> /Contents 4 0 R {pageExtras}>>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        for (int k = 0; k < images.Length; k++)
        {
            offsets.Add(acc.Count);
            Emit($"{5 + k} 0 obj\n{images[k].dict} /Length {images[k].stream.Length} >>\nstream\n");
            acc.AddRange(images[k].stream);
            Emit("\nendstream\nendobj\n");
        }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }

    /// <summary>A fixture's codestream in a JP2 file whose header holds an <c>ihdr</c> and the given boxes.</summary>
    internal static byte[] Jp2(JpxLosslessFixtures.Fixture f, params byte[][] headerBoxes)
    {
        var boxes = new List<byte[]> { H.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7) };
        boxes.AddRange(headerBoxes);
        return H.Jp2File(f.Codestream, boxes.ToArray());
    }

    /// <summary>The page rendered at 72 dpi: 1 pt = 1 px, so image sample (x, y) is page pixel (x, y).</summary>
    internal static SKBitmap Render(byte[] pdf) => Rendering.PixelAssert.Render(pdf);

    internal static string Svg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    internal static SKBitmap DecodeFirstEmbeddedPng(string svg)
    {
        const string marker = "data:image/png;base64,";
        int i = svg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, "the SVG contains no PNG data URI — the image was not decoded");
        int start = i + marker.Length;
        int end = svg.IndexOfAny(new[] { '"', '\'' }, start);
        Assert.True(end > start, "malformed data URI in the SVG");
        SKBitmap bmp = SKBitmap.Decode(Convert.FromBase64String(svg.Substring(start, end - start)));
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>Every pixel against an expectation, within a per-channel tolerance; the first eight differences are reported.</summary>
    internal static void AssertEveryPixel(SKBitmap page, Func<int, int, SKColor> expected, int tolerance = 0)
    {
        var failures = new List<string>();
        for (int y = 0; y < page.Height && failures.Count < 8; y++)
            for (int x = 0; x < page.Width && failures.Count < 8; x++)
            {
                SKColor want = expected(x, y), got = page.GetPixel(x, y);
                if (Math.Abs(want.Red - got.Red) > tolerance || Math.Abs(want.Green - got.Green) > tolerance || Math.Abs(want.Blue - got.Blue) > tolerance)
                    failures.Add($"({x},{y}): expected {want}, got {got}");
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>The pixel a three-plane fixture holds at (x, y).</summary>
    internal static SKColor Pixel(JpxLosslessFixtures.Fixture f, int x, int y)
    {
        int i = y * f.Width + x;
        return new SKColor(f.Planes[0][i], f.Planes[1][i], f.Planes[2][i]);
    }

    /// <summary>The fixture's planes interleaved in the given order — what a sample buffer must be.</summary>
    internal static byte[] Interleaved(JpxLosslessFixtures.Fixture f, params int[] planes)
    {
        var dst = new byte[f.Width * f.Height * planes.Length];
        for (int i = 0; i < f.Width * f.Height; i++)
            for (int k = 0; k < planes.Length; k++)
                dst[i * planes.Length + k] = f.Planes[planes[k]][i];
        return dst;
    }
}
