// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Emits a classic-xref PDF from a list of pre-rendered object bodies. Shared by the
/// image-replacement and revision tests rather than copied into each, so a fixture defect is
/// fixed once — the "special characters were actually balanced parens" lesson in CLAUDE.md came
/// from a fixture that did not exercise what its test claimed.
/// </summary>
internal sealed class PdfFixtureBuilder
{
    private readonly List<byte[]> _chunks = new();
    private readonly Dictionary<int, int> _offsets = new();
    private readonly Dictionary<int, int> _generations = new();
    private int _length;
    private int _maxObj;

    private void Emit(string s) => Emit(Encoding.Latin1.GetBytes(s));

    private void Emit(byte[] bytes)
    {
        _chunks.Add(bytes);
        _length += bytes.Length;
    }

    public PdfFixtureBuilder Header()
    {
        Emit("%PDF-1.7\n");
        return this;
    }

    public PdfFixtureBuilder Obj(int num, string dict, byte[]? streamData = null)
        => ObjAtGeneration(num, 0, dict, streamData);

    /// <summary>
    /// An object written at <paramref name="generation"/> — its header and its cross-reference
    /// entry both say so, as a file that reused an object number (§7.5.4) writes them.
    /// </summary>
    public PdfFixtureBuilder ObjAtGeneration(int num, int generation, string dict, byte[]? streamData = null)
    {
        _offsets[num] = _length;
        _generations[num] = generation;
        if (num > _maxObj) _maxObj = num;
        Emit($"{num} {generation} obj\n{dict}");
        if (streamData != null)
        {
            Emit("\nstream\n");
            Emit(streamData);
            Emit("\nendstream");
        }
        Emit("\nendobj\n");
        return this;
    }

    /// <summary>Emits a stream object whose /Length is computed from the payload.</summary>
    public PdfFixtureBuilder StreamObj(int num, string dictWithoutLength, string content)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(content);
        return Obj(num, $"{dictWithoutLength} /Length {bytes.Length} >>", bytes);
    }

    /// <param name="trailerExtras">Further trailer entries — <c>/Encrypt 9 0 R /ID […]</c>, say.</param>
    /// <param name="root">The trailer's <c>/Root</c> reference.</param>
    public byte[] Finish(string trailerExtras = "", string root = "1 0 R")
    {
        int xref = _length;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {_maxObj + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= _maxObj; i++)
            sb.Append(_offsets.TryGetValue(i, out int o)
                ? $"{o:D10} {_generations[i]:D5} n \n"
                : "0000000000 65535 f \n");
        sb.Append($"trailer\n<< /Size {_maxObj + 1} /Root {root} {trailerExtras}>>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        Emit(sb.ToString());

        var result = new byte[_length];
        int p = 0;
        foreach (var chunk in _chunks) { Buffer.BlockCopy(chunk, 0, result, p, chunk.Length); p += chunk.Length; }
        return result;
    }
}

/// <summary>In-memory PDFs for the image-replacement tests. Object 5 is always the image.</summary>
internal static class ImageFixtures
{
    internal static readonly byte[] Red   = { 0xFF, 0x00, 0x00 };
    internal static readonly byte[] Green = { 0x00, 0xFF, 0x00 };
    internal static readonly byte[] Blue  = { 0x00, 0x00, 0xFF };

    /// <summary>A 72x72 page filled edge-to-edge by one uncompressed image XObject (object 5, /Im0).</summary>
    internal static byte[] OneImagePdf(
        byte[] samples, int width, int height,
        string colorSpace = "/DeviceRGB",
        string extraImageKeys = "",
        string catalogExtras = "",
        Action<PdfFixtureBuilder>? extraObjects = null)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, $"<< /Type /Catalog /Pages 2 0 R {catalogExtras}>>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Im0 Do Q")
            .Obj(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                    $"/ColorSpace {colorSpace} /BitsPerComponent 8 {extraImageKeys}" +
                    $"/Length {samples.Length} >>", samples);
        extraObjects?.Invoke(b);
        return b.Finish();
    }

    /// <summary>A 72x72 page split into two half-width images: /Im0 on the left, /Im1 on the right.</summary>
    internal static byte[] TwoImagePdf(byte[] left, byte[] right) =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 36 0 0 72 0 0 cm /Im0 Do Q q 36 0 0 72 36 0 cm /Im1 Do Q")
            .Obj(5, OnePixelImageDict(left), left)
            .Obj(6, OnePixelImageDict(right), right)
            .Finish();

    /// <summary>Two pages that draw the same image object (5), so replacing it changes both.</summary>
    internal static byte[] SharedImagePdf(byte[] samples)
    {
        const string pageDict =
            "/MediaBox [0 0 72 72] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R";
        return new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>")
            .Obj(3, $"<< /Type /Page /Parent 2 0 R {pageDict} >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Im0 Do Q")
            .Obj(5, OnePixelImageDict(samples), samples)
            .Obj(6, $"<< /Type /Page /Parent 2 0 R {pageDict} >>")
            .Finish();
    }

    /// <summary>
    /// The image (object 5) is reachable only through a form XObject (object 6), which a
    /// page-level resource scan cannot see.
    /// </summary>
    internal static byte[] ImageInsideFormPdf(byte[] samples) =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Fx 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Fx Do Q")
            .Obj(5, OnePixelImageDict(samples), samples)
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                          "/Resources << /XObject << /Logo 5 0 R >> >>",
                       "q 1 0 0 1 0 0 cm /Logo Do Q")
            .Finish();

    /// <summary>
    /// A form XObject that lists itself in its own resources. A walker with no cycle guard
    /// recurses until its depth cap; one with no depth cap never returns.
    /// </summary>
    internal static byte[] SelfReferencingFormPdf(byte[] samples) =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Fx 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Fx Do Q")
            .Obj(5, OnePixelImageDict(samples), samples)
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                          "/Resources << /XObject << /Self 6 0 R /Logo 5 0 R >> >>",
                       "q 1 0 0 1 0 0 cm /Logo Do Q")
            .Finish();

    /// <summary>A document certified with a DocMDP signature at P=1 (no changes permitted).</summary>
    internal static byte[] CertifiedPdf() => OneImagePdf(
        Red, 1, 1,
        catalogExtras: "/Perms << /DocMDP 6 0 R >> ",
        extraObjects: b => b.Obj(6,
            "<< /Type /Sig /Reference [ << /Type /SigRef /TransformMethod /DocMDP " +
            "/TransformParams << /Type /TransformParams /P 1 /V /1.2 >> >> ] " +
            "/ByteRange [0 100 200 300] >>"));

    internal static string OnePixelImageDict(byte[] samples) =>
        "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB " +
        $"/BitsPerComponent 8 /Length {samples.Length} >>";

    /// <summary>A solid-colour RGB sample block, <paramref name="w"/> x <paramref name="h"/>.</summary>
    internal static byte[] SolidRgb(int w, int h, byte r, byte g, byte b)
    {
        var buf = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            buf[i * 3] = r; buf[i * 3 + 1] = g; buf[i * 3 + 2] = b;
        }
        return buf;
    }

    /// <summary>A solid-colour RGBA sample block with a uniform alpha.</summary>
    internal static byte[] SolidRgba(int w, int h, byte r, byte g, byte b, byte a)
    {
        var buf = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            buf[i * 4] = r; buf[i * 4 + 1] = g; buf[i * 4 + 2] = b; buf[i * 4 + 3] = a;
        }
        return buf;
    }
}
