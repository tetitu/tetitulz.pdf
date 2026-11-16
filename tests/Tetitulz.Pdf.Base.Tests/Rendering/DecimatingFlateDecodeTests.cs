// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="DecimatingFlateDecode"/>: with <c>DownsampleImagesToTarget</c> on, a FlateDecode image
/// drawn far smaller than it is stored is decimated as it is inflated — the same samples the full
/// decode and the raster renderer's decimation give, without the whole buffer.
/// </summary>
/// <remarks>
/// <para><b>The control is the full path itself.</b> The same compressed bytes behind
/// <c>[/RunLengthDecode /FlateDecode]</c> are a chain of filters, which the decimating decode never
/// takes: the filter pipeline decodes them whole and the renderer decimates the buffer, as every
/// FlateDecode image was drawn before. Each case renders both and compares the pages byte for
/// byte. (The control was <c>[/ASCIIHexDecode /FlateDecode]</c> until 2026-10-19, when the
/// decimating decode came to take a chain of ASCII stages before its filter.)</para>
/// <para><b>Liveness is the allocation</b>, which is the claim: an optimisation whose fast and slow
/// paths agree is otherwise invisible to a test about their output.</para>
/// </remarks>
public sealed class DecimatingFlateDecodeTests
{
    private const int Dpi = 96;
    private static readonly PdfRenderOptions On = new PdfRenderOptions { DownsampleImagesToTarget = true };

    // ---------------------------------------------------------------- fixtures

    /// <summary>An image's samples: a pattern of every byte value across rows and columns, so a wrong row or column is visible.</summary>
    internal static byte[] Samples(int width, int height, int bytesPerPixel)
    {
        var s = new byte[width * height * bytesPerPixel];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int b = 0; b < bytesPerPixel; b++)
                    s[(y * width + x) * bytesPerPixel + b] = (byte)(x * 7 + y * 13 + b * 61 + ((x ^ y) & 31));
        return s;
    }

    /// <summary>The samples PNG-predicted row by row, the filter type cycling None, Sub, Up, Average, Paeth.</summary>
    private static byte[] PngPredicted(byte[] samples, int width, int height, int bytesPerPixel)
    {
        int row = width * bytesPerPixel;
        var o = new byte[height * (row + 1)];
        var prev = new byte[row];
        for (int y = 0; y < height; y++)
        {
            int type = y % 5;
            int src = y * row, dst = y * (row + 1);
            o[dst] = (byte)type;
            for (int i = 0; i < row; i++)
            {
                int raw = samples[src + i];
                int a = i >= bytesPerPixel ? samples[src + i - bytesPerPixel] : 0;
                int b = prev[i];
                int c = i >= bytesPerPixel ? prev[i - bytesPerPixel] : 0;
                int pred = type switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                o[dst + 1 + i] = (byte)(raw - pred);
            }
            Buffer.BlockCopy(samples, src, prev, 0, row);
        }
        return o;
    }

    /// <summary>The samples TIFF-predicted (2): each sample less the same component of the pixel before it in its row, 8- or 16-bit big-endian.</summary>
    private static byte[] TiffPredicted(byte[] samples, int width, int height, int colors, int bpc)
    {
        int bytesPerSample = bpc / 8, row = width * colors * bytesPerSample;
        var o = new byte[samples.Length];
        for (int y = 0; y < height; y++)
            for (int s = 0; s < width * colors; s++)
            {
                int at = y * row + s * bytesPerSample;
                int cur = bpc == 16 ? (samples[at] << 8) | samples[at + 1] : samples[at];
                int left = s >= colors ? (bpc == 16 ? (samples[at - colors * 2] << 8) | samples[at - colors * 2 + 1] : samples[at - colors]) : 0;
                int diff = (cur - left) & (bpc == 16 ? 0xFFFF : 0xFF);
                if (bpc == 16) { o[at] = (byte)(diff >> 8); o[at + 1] = (byte)diff; } else o[at] = (byte)diff;
            }
        return o;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    internal static byte[] Hex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2 + 1);
        foreach (byte b in data) sb.Append(b.ToString("X2"));
        sb.Append('>');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>ASCII base-85 (§7.4.3): four bytes to five characters from <c>!</c>, an all-zero group as <c>z</c>, a final group of n bytes as n + 1 characters, then <c>~&gt;</c>.</summary>
    internal static byte[] Ascii85(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 5 / 4 + 8);
        var digits = new char[5];
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (k < n ? data[i + k] : 0u);
            if (n == 4 && v == 0) { sb.Append('z'); continue; }
            for (int k = 4; k >= 0; k--) { digits[k] = (char)('!' + v % 85); v /= 85; }
            sb.Append(digits, 0, n + 1);
        }
        sb.Append("~>");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>The bytes run-length encoded (§7.4.5) — the full path's chain, which the decimating decode does not take.</summary>
    internal static byte[] RunLength(byte[] data) => Tetitulz.Pdf.Core.IO.Filters.RunLengthEncodeFilter.Encode(data);

    /// <summary>
    /// A page drawing one image 72 pt square: its compressed bytes as FlateDecode alone, or
    /// (<paramref name="chain"/>) the same bytes run-length encoded behind <c>[/RunLengthDecode /FlateDecode]</c>.
    /// </summary>
    internal static byte[] ImagePdf(byte[] compressed, int width, int height, string colourAndDepth, string predictorParms, bool chain)
    {
        string filter = chain ? "/Filter [/RunLengthDecode /FlateDecode]" : "/Filter /FlateDecode";
        string parms = predictorParms == null ? "" : chain ? $" /DecodeParms [null {predictorParms}]" : $" /DecodeParms {predictorParms}";
        byte[] data = chain ? RunLength(compressed) : compressed;
        return ImagePdfWith(data, width, height, colourAndDepth + " " + filter + parms);
    }

    /// <summary>A page drawing one image 72 pt square, its stream <paramref name="data"/> under <paramref name="dictionaryTail"/>.</summary>
    internal static byte[] ImagePdfWith(byte[] data, int width, int height, string dictionaryTail)
    {
        const string content = "q 72 0 0 72 40 600 cm /Im0 Do Q\n";

        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }
        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3); Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4); Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5); Emit($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} {dictionaryTail} /Length {data.Length} >>\nstream\n");
        acc.AddRange(data);
        Emit("\nendstream\nendobj\n");
        int xref = acc.Count;
        Emit("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }

    internal static byte[] Render(byte[] pdf, PdfRenderOptions options)
    {
        using var ms = new MemoryStream(pdf);
        return PdfPageRenderer.RenderPage(ms, 0, Dpi, options);
    }

    /// <summary>The page with the image as FlateDecode alone, and the page with it behind the chain, both downsampled.</summary>
    private static (byte[] Streamed, byte[] Full) BothWays(byte[] compressed, int width, int height, string colourAndDepth, string predictorParms = null)
        => (Render(ImagePdf(compressed, width, height, colourAndDepth, predictorParms, chain: false), On),
            Render(ImagePdf(compressed, width, height, colourAndDepth, predictorParms, chain: true), On));

    // ---------------------------------------------------------------- the same samples as the full path

    /// <summary>RGB, grey and 16-bit RGB, unpredicted: the decimated decode draws the page the full decode and decimation draw.</summary>
    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8", 3)]
    [InlineData("/ColorSpace /DeviceGray /BitsPerComponent 8", 1)]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 16", 6)]
    [InlineData("/ColorSpace /DeviceCMYK /BitsPerComponent 8", 4)]
    public void TheSamplesAreTheFullDecodes(string colourAndDepth, int bytesPerPixel)
    {
        const int size = 1000;
        var (streamed, full) = BothWays(Zlib(Samples(size, size, bytesPerPixel)), size, size, colourAndDepth);
        Assert.Equal(full, streamed);
    }

    /// <summary>
    /// A PNG-predicted image, every filter type among its rows: each row undone against the one
    /// before by the predictor's own row undo, the page the full decode's.
    /// </summary>
    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8", 3, 3, 8)]
    [InlineData("/ColorSpace /DeviceGray /BitsPerComponent 16", 2, 1, 16)]
    public void APngPredictedImage_IsTheFullDecodes(string colourAndDepth, int bytesPerPixel, int colors, int bpc)
    {
        const int width = 999, height = 777;
        byte[] predicted = PngPredicted(Samples(width, height, bytesPerPixel), width, height, bytesPerPixel);
        string parms = $"<< /Predictor 15 /Colors {colors} /BitsPerComponent {bpc} /Columns {width} >>";
        var (streamed, full) = BothWays(Zlib(predicted), width, height, colourAndDepth, parms);
        Assert.Equal(full, streamed);
    }

    /// <summary>
    /// A TIFF-predicted image (2026-10-19): each row undone by the predictor's own row undo
    /// (<c>PredictorHelper.UndoTiffRow</c>), 8-bit RGB and 16-bit grey — the page the full decode's,
    /// and the decimated decode taken, not declined.
    /// </summary>
    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8", 3, 8)]
    [InlineData("/ColorSpace /DeviceGray /BitsPerComponent 16", 1, 16)]
    public void ATiffPredictedImage_IsTheFullDecodes(string colourAndDepth, int colors, int bpc)
    {
        const int width = 999, height = 777;
        byte[] predicted = TiffPredicted(Samples(width, height, colors * bpc / 8), width, height, colors, bpc);
        string parms = $"<< /Predictor 2 /Colors {colors} /BitsPerComponent {bpc} /Columns {width} >>";
        var (streamed, full) = BothWays(Zlib(predicted), width, height, colourAndDepth, parms);
        Assert.Equal(full, streamed);

        var dict = new PdfDictionary
        {
            [PdfName.Predictor] = new PdfNumber(2), [PdfName.Colors] = new PdfNumber(colors),
            [PdfName.Columns] = new PdfNumber(width), [PdfName.BitsPerComponent] = new PdfNumber(bpc),
        };
        Assert.True(ImageDownsampling.TryRawDecimation(width, height, 96, 96, out int step, out int nw, out int nh));
        byte[]? taken = DecimatingFlateDecode.TryDecode(Zlib(predicted), dict, width, height, colors, bpc, step, nw, nh);
        Assert.NotNull(taken);
        // The samples are the image's, undone: the second kept pixel of the second kept row is the source's.
        int bytesPerPixel = colors * bpc / 8;
        byte[] source = Samples(width, height, bytesPerPixel);
        Assert.Equal(source[(step * width + step) * bytesPerPixel], taken![(nw + 1) * bytesPerPixel]);
    }

    /// <summary>
    /// An LZWDecode image (2026-10-19): the codes decoded as they are read, the rows assembled and
    /// decimated — unpredicted, PNG- and TIFF-predicted, and under <c>/EarlyChange 0</c> — the page the full
    /// path draws behind <c>[/RunLengthDecode /LZWDecode]</c>, and the decimated decode taken.
    /// </summary>
    [Theory]
    [InlineData(null, 1)]
    [InlineData("/Predictor 15", 1)]
    [InlineData("/Predictor 2", 1)]
    [InlineData(null, 0)]
    public void AnLzwImage_IsTheFullDecodes(string? predictor, int earlyChange)
    {
        const int width = 999, height = 777;
        byte[] samples = Samples(width, height, 3);
        byte[] rows = predictor == "/Predictor 15" ? PngPredicted(samples, width, height, 3)
                    : predictor == "/Predictor 2" ? TiffPredicted(samples, width, height, 3, 8) : samples;
        byte[] lzw = Tetitulz.Pdf.Core.IO.Filters.LzwEncodeFilter.Encode(rows, earlyChange);
        string inner = $"<< {predictor} /Colors 3 /BitsPerComponent 8 /Columns {width} /EarlyChange {earlyChange} >>";
        string rgb = "/ColorSpace /DeviceRGB /BitsPerComponent 8";

        byte[] streamed = Render(ImagePdfWith(lzw, width, height, $"{rgb} /Filter /LZWDecode /DecodeParms {inner}"), On);
        byte[] full = Render(ImagePdfWith(RunLength(lzw), width, height, $"{rgb} /Filter [/RunLengthDecode /LZWDecode] /DecodeParms [null {inner}]"), On);
        Assert.Equal(full, streamed);

        var parms = new PdfDictionary
        {
            [PdfName.Colors] = new PdfNumber(3), [PdfName.Columns] = new PdfNumber(width),
            [PdfName.BitsPerComponent] = new PdfNumber(8), [PdfName.EarlyChange] = new PdfNumber(earlyChange),
        };
        if (predictor != null) parms[PdfName.Predictor] = new PdfNumber(predictor.EndsWith("15", StringComparison.Ordinal) ? 15 : 2);
        Assert.True(ImageDownsampling.TryRawDecimation(width, height, 96, 96, out int step, out int nw, out int nh));
        byte[]? taken = DecimatingFlateDecode.TryDecodeLzw(lzw, parms, width, height, 3, 8, step, nw, nh);
        Assert.NotNull(taken);
        Assert.Equal(samples[(step * width + step) * 3], taken![(nw + 1) * 3]);
    }

    /// <summary>An LZW stream short of its rows, or stopped by a corrupt code, is the full path's: the decimating decode declines it.</summary>
    [Fact]
    public void AnLzwStreamShortOfItsRows_IsDeclined()
    {
        const int w = 512, h = 512;
        byte[] lzw = Tetitulz.Pdf.Core.IO.Filters.LzwEncodeFilter.Encode(Samples(w, h, 3));
        Assert.NotNull(DecimatingFlateDecode.TryDecodeLzw(lzw, null, w, h, 3, 8, 2, w / 2, h / 2));   // the control

        byte[] cut = new byte[lzw.Length / 2];
        Buffer.BlockCopy(lzw, 0, cut, 0, cut.Length);
        Assert.Null(DecimatingFlateDecode.TryDecodeLzw(cut, null, w, h, 3, 8, 2, w / 2, h / 2));

        // 0xFF bytes: nine-bit code 511 at once, which no table holds — the decoder stops there.
        var corrupt = (byte[])lzw.Clone();
        for (int i = lzw.Length / 4; i < lzw.Length / 4 + 8; i++) corrupt[i] = 0xFF;
        Assert.Null(DecimatingFlateDecode.TryDecodeLzw(corrupt, null, w, h, 3, 8, 2, w / 2, h / 2));
    }

    /// <summary>A raw deflate stream, with no zlib header — which the filter reads too — is inflated from its first byte.</summary>
    [Fact]
    public void ARawDeflateStream_IsTheFullDecodes()
    {
        const int size = 600;
        byte[] samples = Samples(size, size, 3);
        byte[] raw;
        using (var ms = new MemoryStream())
        {
            using (var d = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) d.Write(samples, 0, samples.Length);
            raw = ms.ToArray();
        }
        Assert.NotEqual(0x08, raw[0] & 0x0F);   // no zlib header, or the case is not this one
        var (streamed, full) = BothWays(raw, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8");
        Assert.Equal(full, streamed);
        Assert.NotNull(DecimatingFlateDecode.TryDecode(raw, null, size, size, 3, 8, 3, 200, 200));
    }

    /// <summary>
    /// The decimated output's room is made as rows arrive: a stream of a few rows declaring a
    /// 10 000 × 4 000 16-bit RGB image drawn at half size (240 MB decoded, under the filter's ceiling)
    /// declines — it is short of its rows — having allocated what those rows needed, where the whole
    /// decimated image, 60 MB, was allocated before a byte was inflated. The potency: a whole image
    /// through the same path is the full decode's.
    /// </summary>
    [Fact]
    public void AShortStream_AllocatesItsRows_NotTheDeclaredImage()
    {
        byte[] fewRows;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(new byte[10_000 * 6 * 4], 0, 10_000 * 6 * 4);
            fewRows = ms.ToArray();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(DecimatingFlateDecode.TryDecode(fewRows, null, 10_000, 4_000, 3, 16, 2, 5_000, 2_000));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 4L << 20);

        const int size = 600;
        byte[] samples = Samples(size, size, 3);
        var (streamed, full) = BothWays(Zlib(samples), size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8");
        Assert.Equal(full, streamed);
    }

    /// <summary>
    /// A chain of filters is the pipeline's, even when the last is FlateDecode: the same samples
    /// deflated twice, <c>[/FlateDecode /FlateDecode]</c>, draw the page the run-length chain draws — the
    /// decimating decode would have read the inner layer's compressed bytes as samples. The samples
    /// are noise, which does not compress, so the inner layer is as long as the rows it would be
    /// misread as.
    /// </summary>
    [Fact]
    public void AChainEndingInFlate_IsThePipelines()
    {
        const int size = 600;
        var noise = new byte[size * size * 3];
        new Random(19).NextBytes(noise);
        byte[] once = Zlib(noise);
        Assert.True(once.Length >= noise.Length, "the inner layer compressed, so the case is not this one");
        byte[] twice = Zlib(once);
        byte[] doubled = Render(ImagePdfWith(twice, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter [/FlateDecode /FlateDecode]"), On);
        byte[] full = Render(ImagePdf(once, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: true), On);
        Assert.Equal(full, doubled);
    }

    /// <summary>
    /// ASCII stages before the filter (2026-10-19): <c>[/ASCIIHexDecode /FlateDecode]</c> PNG-predicted,
    /// <c>[/A85 /LZWDecode]</c> TIFF-predicted, and two ASCII layers before <c>/Fl</c> — the ASCII stages
    /// run whole by the pipeline, the last decimated, the page the full path draws behind the run-length chain.
    /// </summary>
    [Theory]
    [InlineData("hex-flate")]
    [InlineData("a85-lzw")]
    [InlineData("a85-hex-fl")]
    public void AnAsciiWrappedImage_IsTheFullDecodes(string chain)
    {
        const int width = 999, height = 777;
        byte[] samples = Samples(width, height, 3);
        string rgb = "/ColorSpace /DeviceRGB /BitsPerComponent 8";
        string png = $"<< /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns {width} >>";
        string tiff = $"<< /Predictor 2 /Colors 3 /BitsPerComponent 8 /Columns {width} >>";
        var (data, tail, encoded, last) = chain switch
        {
            "hex-flate" => (Hex(Zlib(PngPredicted(samples, width, height, 3))), $"/Filter [/ASCIIHexDecode /FlateDecode] /DecodeParms [null {png}]",
                Zlib(PngPredicted(samples, width, height, 3)), $"/FlateDecode] /DecodeParms [null {png}]"),
            "a85-lzw" => (Ascii85(Tetitulz.Pdf.Core.IO.Filters.LzwEncodeFilter.Encode(TiffPredicted(samples, width, height, 3, 8))),
                $"/Filter [/A85 /LZWDecode] /DecodeParms [null {tiff}]",
                Tetitulz.Pdf.Core.IO.Filters.LzwEncodeFilter.Encode(TiffPredicted(samples, width, height, 3, 8)), $"/LZWDecode] /DecodeParms [null {tiff}]"),
            _ => (Ascii85(Hex(Zlib(samples))), "/Filter [/ASCII85Decode /AHx /Fl]", Zlib(samples), "/Fl]"),
        };

        byte[] streamed = Render(ImagePdfWith(data, width, height, $"{rgb} {tail}"), On);
        byte[] full = Render(ImagePdfWith(RunLength(encoded), width, height, $"{rgb} /Filter [/RunLengthDecode {last}"), On);
        Assert.Equal(full, streamed);
    }

    /// <summary>
    /// The stages are the pipeline's, paired as it pairs them: a lone parameter dictionary beside a
    /// chain names no stage, so the full path drops it and reports it — and the decimating decode leaves
    /// that image to it; a parameter array beside one filter name is not that filter's parameters
    /// either, there or here. The potency control: the dictionary where it does apply draws another page.
    /// </summary>
    [Fact]
    public void TheParametersAreThoseThePipelinePairs()
    {
        const int width = 999, height = 777;
        byte[] predicted = Zlib(PngPredicted(Samples(width, height, 3), width, height, 3));
        string rgb = "/ColorSpace /DeviceRGB /BitsPerComponent 8";
        string png = $"<< /Predictor 15 /Colors 3 /BitsPerComponent 8 /Columns {width} >>";

        byte[] applied = Render(ImagePdfWith(predicted, width, height, $"{rgb} /Filter /FlateDecode /DecodeParms {png}"), On);
        byte[] unpredicted = Render(ImagePdfWith(predicted, width, height, $"{rgb} /Filter /FlateDecode"), On);
        Assert.NotEqual(applied, unpredicted);

        // A dictionary beside two filters: dropped, the image drawn unpredicted, the drop reported.
        byte[] stray = Render(ImagePdfWith(Hex(predicted), width, height, $"{rgb} /Filter [/AHx /Fl] /DecodeParms {png}"), On);
        Assert.Equal(unpredicted, stray);
        Assert.Contains("/DecodeParms is a dictionary", Tetitulz.Pdf.Core.IO.Filters.FilterPipeline.LastUnknownFilterName ?? "", StringComparison.Ordinal);

        // An array beside one name: not the filter's parameters.
        byte[] arrayBesideName = Render(ImagePdfWith(predicted, width, height, $"{rgb} /Filter /FlateDecode /DecodeParms [{png}]"), On);
        Assert.Equal(unpredicted, arrayBesideName);
    }

    /// <summary>
    /// The one step rule, pinned: a 96 px target wants 256 px (its 192 of headroom, raised to the
    /// floor), so a 1 000² source is stepped by 3 to 334²; a source too short for the target's height
    /// is not decimated, and neither is one already within what the target wants.
    /// </summary>
    [Fact]
    public void TheStepRule_IsPinned()
    {
        Assert.True(ImageDownsampling.TryRawDecimation(1000, 1000, 96, 96, out int step, out int w, out int h));
        Assert.Equal((3, 334, 334), (step, w, h));
        Assert.True(ImageDownsampling.TryRawDecimation(999, 777, 96, 96, out step, out w, out h));
        Assert.Equal((3, 333, 259), (step, w, h));

        // 4 000 wide at a step of 15, but 300 tall: 20 rows for a 96 px target is too coarse.
        Assert.False(ImageDownsampling.TryRawDecimation(4000, 300, 96, 96, out _, out _, out _));
        Assert.True(ImageDownsampling.TryRawDecimation(4000, 3000, 96, 96, out step, out _, out h));
        Assert.Equal((15, 200), (step, h));
        Assert.False(ImageDownsampling.TryRawDecimation(300, 300, 200, 200, out _, out _, out _));
        Assert.False(ImageDownsampling.TryRawDecimation(1000, 1000, 0, 96, out _, out _, out _));
    }

    /// <summary>An indexed image is decimated one index a sample, as the full path strides it.</summary>
    [Fact]
    public void AnIndexedImage_IsTheFullDecodes()
    {
        const int size = 800;
        var samples = Samples(size, size, 1);
        for (int i = 0; i < samples.Length; i++) samples[i] %= 4;
        var (streamed, full) = BothWays(Zlib(samples), size, size,
            "/ColorSpace [/Indexed /DeviceRGB 3 <FF0000 00FF00 0000FF FFFF00>] /BitsPerComponent 8");
        Assert.Equal(full, streamed);
    }

    // ---------------------------------------------------------------- liveness

    /// <summary>
    /// The decimated decode is taken: a 2 000² RGB image drawn small allocates a fraction of what the
    /// full path does for the same bytes — 12 MB of samples and the inflater's segments, there.
    /// </summary>
    [Fact]
    public void TheDecimatedDecode_IsTaken_AndAllocatesAFraction()
    {
        const int size = 2000;
        byte[] compressed = Zlib(Samples(size, size, 3));
        byte[] single = ImagePdf(compressed, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: false);
        byte[] chain = ImagePdf(compressed, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: true);
        Render(single, On); Render(chain, On);   // warm

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        Render(single, On);
        long streamed = GC.GetAllocatedBytesForCurrentThread() - a0;
        a0 = GC.GetAllocatedBytesForCurrentThread();
        Render(chain, On);
        long full = GC.GetAllocatedBytesForCurrentThread() - a0;

        Assert.True(full > 20_000_000, $"the control allocated {full:N0} bytes; the comparison means nothing");
        Assert.True(streamed < 6_000_000, $"the decimated decode allocated {streamed:N0} bytes against the full path's {full:N0}");
    }

    /// <summary>
    /// The decimated decode is taken for an LZWDecode image too: a 1 500² RGB image drawn small allocates a
    /// fraction of what the full path behind <c>[/RunLengthDecode /LZWDecode]</c> does — 6.75 MB of samples
    /// and the decoder's output buffer there.
    /// </summary>
    [Fact]
    public void TheDecimatedLzwDecode_IsTaken_AndAllocatesAFraction()
    {
        const int size = 1500;
        byte[] lzw = Tetitulz.Pdf.Core.IO.Filters.LzwEncodeFilter.Encode(Samples(size, size, 3));
        string rgb = "/ColorSpace /DeviceRGB /BitsPerComponent 8";
        byte[] single = ImagePdfWith(lzw, size, size, $"{rgb} /Filter /LZWDecode");
        byte[] chain = ImagePdfWith(RunLength(lzw), size, size, $"{rgb} /Filter [/RunLengthDecode /LZWDecode]");
        Render(single, On); Render(chain, On);   // warm

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        Render(single, On);
        long streamed = GC.GetAllocatedBytesForCurrentThread() - a0;
        a0 = GC.GetAllocatedBytesForCurrentThread();
        Render(chain, On);
        long full = GC.GetAllocatedBytesForCurrentThread() - a0;

        Assert.True(full > 15_000_000, $"the control allocated {full:N0} bytes; the comparison means nothing");
        Assert.True(streamed < full / 3, $"the decimated decode allocated {streamed:N0} bytes against the full path's {full:N0}");
    }

    /// <summary>
    /// The decimated decode is taken behind ASCII stages too: a 2 000² RGB image behind
    /// <c>[/ASCII85Decode /FlateDecode]</c> allocates a fraction of what the full path behind the
    /// run-length chain does — the ASCII stage's output, the compressed stream, is all it adds.
    /// </summary>
    [Fact]
    public void TheDecimatedDecodeBehindAsciiStages_IsTaken_AndAllocatesAFraction()
    {
        const int size = 2000;
        byte[] compressed = Zlib(Samples(size, size, 3));
        string rgb = "/ColorSpace /DeviceRGB /BitsPerComponent 8";
        byte[] wrapped = ImagePdfWith(Ascii85(compressed), size, size, $"{rgb} /Filter [/ASCII85Decode /FlateDecode]");
        byte[] chain = ImagePdf(compressed, size, size, rgb, null, chain: true);
        Render(wrapped, On); Render(chain, On);   // warm

        long a0 = GC.GetAllocatedBytesForCurrentThread();
        Render(wrapped, On);
        long streamed = GC.GetAllocatedBytesForCurrentThread() - a0;
        a0 = GC.GetAllocatedBytesForCurrentThread();
        Render(chain, On);
        long full = GC.GetAllocatedBytesForCurrentThread() - a0;

        Assert.True(full > 20_000_000, $"the control allocated {full:N0} bytes; the comparison means nothing");
        Assert.True(streamed < full / 3, $"the decimated decode allocated {streamed:N0} bytes against the full path's {full:N0}");
    }

    /// <summary>The control for liveness: with the option off nothing is decimated, so the single filter and the chain draw the same full image.</summary>
    [Fact]
    public void WithTheOptionOff_NothingIsDecimated()
    {
        const int size = 600;
        byte[] compressed = Zlib(Samples(size, size, 3));
        var off = new PdfRenderOptions { DownsampleImagesToTarget = false };
        Assert.Equal(
            Render(ImagePdf(compressed, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: true), off),
            Render(ImagePdf(compressed, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: false), off));
    }

    // ---------------------------------------------------------------- where it declines

    /// <summary>
    /// Where the decimated decode declines and the full path draws: a stream short of its rows, PNG
    /// parameters that are not the image's rows — each page the chain's; and a TIFF predictor over
    /// unpredicted samples, which it takes since 2026-10-19 and undoes as the full path does.
    /// </summary>
    [Fact]
    public void WhereItDeclines_ThePageIsTheFullPaths()
    {
        const int size = 600;
        byte[] samples = Samples(size, size, 3);
        string rgb = "/ColorSpace /DeviceRGB /BitsPerComponent 8";

        byte[] whole = Zlib(samples);
        byte[] cut = new byte[whole.Length / 2];
        Buffer.BlockCopy(whole, 0, cut, 0, cut.Length);
        var (s1, f1) = BothWays(cut, size, size, rgb);
        Assert.Equal(f1, s1);

        var (s2, f2) = BothWays(whole, size, size, rgb, $"<< /Predictor 2 /Colors 3 /Columns {size} >>");
        Assert.Equal(f2, s2);

        byte[] predicted = PngPredicted(samples, size, size, 3);
        var (s3, f3) = BothWays(Zlib(predicted), size, size, rgb, $"<< /Predictor 15 /Colors 3 /Columns {size - 1} >>");
        Assert.Equal(f3, s3);
    }

    /// <summary>The decoder's own refusals: a predictor other than PNG's or TIFF's, predictor rows of other columns, colours or depth, a stream short of its rows, a codec's signature in the first row.</summary>
    [Fact]
    public void TheDecoderDeclines_WhatItCannotBeSureOf()
    {
        const int w = 512, h = 512, step = 2;
        byte[] samples = Samples(w, h, 3);
        byte[] z = Zlib(samples);
        PdfDictionary Parms(int predictor, int colors = 3, int columns = w, int bpc = 8) => new PdfDictionary
        {
            [PdfName.Predictor] = new PdfNumber(predictor), [PdfName.Colors] = new PdfNumber(colors),
            [PdfName.Columns] = new PdfNumber(columns), [PdfName.BitsPerComponent] = new PdfNumber(bpc),
        };

        // The control: unpredicted, the decimated samples are every second of every second row.
        byte[] got = DecimatingFlateDecode.TryDecode(z, null, w, h, 3, 8, step, w / 2, h / 2);
        Assert.NotNull(got);
        Assert.Equal(samples[(2 * w + 2) * 3], got![(1 * (w / 2) + 1) * 3]);

        Assert.NotNull(DecimatingFlateDecode.TryDecode(z, Parms(2), w, h, 3, 8, step, w / 2, h / 2));
        Assert.Null(DecimatingFlateDecode.TryDecode(z, Parms(2, columns: w - 1), w, h, 3, 8, step, w / 2, h / 2));
        Assert.Null(DecimatingFlateDecode.TryDecode(z, Parms(3), w, h, 3, 8, step, w / 2, h / 2));
        byte[] pz = Zlib(PngPredicted(samples, w, h, 3));
        Assert.NotNull(DecimatingFlateDecode.TryDecode(pz, Parms(12), w, h, 3, 8, step, w / 2, h / 2));
        Assert.Null(DecimatingFlateDecode.TryDecode(pz, Parms(12, columns: w + 1), w, h, 3, 8, step, w / 2, h / 2));
        Assert.Null(DecimatingFlateDecode.TryDecode(pz, Parms(12, colors: 1), w, h, 3, 8, step, w / 2, h / 2));
        Assert.Null(DecimatingFlateDecode.TryDecode(pz, Parms(12, bpc: 16), w, h, 3, 8, step, w / 2, h / 2));

        byte[] cut = new byte[z.Length / 3];
        Buffer.BlockCopy(z, 0, cut, 0, cut.Length);
        Assert.Null(DecimatingFlateDecode.TryDecode(cut, null, w, h, 3, 8, step, w / 2, h / 2));

        byte[] jpegish = (byte[])samples.Clone();
        jpegish[0] = 0xFF; jpegish[1] = 0xD8; jpegish[2] = 0xFF;
        Assert.Null(DecimatingFlateDecode.TryDecode(Zlib(jpegish), null, w, h, 3, 8, step, w / 2, h / 2));

        // A size that disagrees with the step is refused rather than written past.
        Assert.Null(DecimatingFlateDecode.TryDecode(z, null, w, h, 3, 8, step, w / 2 + 1, h / 2));
    }
}
