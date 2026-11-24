// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The assembly-scan findings against <c>Operations/Images</c>: the exporter's
/// colour-space handling and its unbounded decode, the thumbnail writer's assumptions about the
/// caller's output stream and the count it reports, and the SVG rasterizer's silent failure.
///
/// <para>Every assertion here is on a <b>decoded pixel</b>, on the re-parsed PDF object, or on the
/// guard's own reported evidence — an exported PNG is a valid non-empty PNG whatever colour it
/// contains, so "bytes came back" would pass with each of these fixes deleted.</para>
/// </summary>
public sealed class ImageExportAndThumbnailGapTests
{
    // ================================================================
    //  Helpers
    // ================================================================

    private static SKColor ExportedPixel(byte[] pdf, int x = 0, int y = 0)
    {
        using var input = new MemoryStream(pdf);
        var img = PdfImageExporter.ExtractImage(input, 0, 0);
        Assert.Equal("image/png", img.MimeType);
        using var bmp = SKBitmap.Decode(img.Data);
        Assert.NotNull(bmp);
        return bmp.GetPixel(x, y);
    }

    private static void AssertColor(SKColor actual, byte r, byte g, byte b, string what)
    {
        Assert.True(
            Math.Abs(actual.Red - r) <= 2 && Math.Abs(actual.Green - g) <= 2 &&
            Math.Abs(actual.Blue - b) <= 2,
            $"{what}: expected ({r},{g},{b}) but got ({actual.Red},{actual.Green},{actual.Blue})");
    }

    /// <summary>Collects <see cref="RenderDiagnostics"/> events raised on the calling thread only.</summary>
    private sealed class ThreadLocalDiagnostics : IDisposable
    {
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly Action<RenderDiagnosticKind, string, Exception> _handler;
        private readonly List<(RenderDiagnosticKind Kind, string Detail)> _events = new();

        internal ThreadLocalDiagnostics()
        {
            // Filtered to this thread, not serialised: the sink is process-wide, so a neighbour's
            // legitimate report would otherwise satisfy — or break — an assertion about ours.
            _handler = (k, d, _) =>
            {
                if (Thread.CurrentThread.ManagedThreadId != _thread) return;
                lock (_events) _events.Add((k, d));
            };
            RenderDiagnostics.OnEvent += _handler;
        }

        internal IReadOnlyList<(RenderDiagnosticKind Kind, string Detail)> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Dispose() => RenderDiagnostics.OnEvent -= _handler;
    }

    // ================================================================
    //  PdfImageExporter — /ColorSpace is read, not guessed from the byte count
    // ================================================================

    /// <summary>
    /// A DeviceCMYK image exported as PNG must go through the subtractive conversion. The exporter
    /// derived its component count as <c>decoded.Length / (w*h)</c>, never read
    /// <c>/ColorSpace</c>, and wrote bytes 0,1,2 of each group as R,G,B — so pure cyan
    /// (C=255, M=Y=K=0) came out as pure RED, a colour negative returned successfully with
    /// MimeType "image/png" and no diagnostic.
    /// </summary>
    [Fact]
    public void ExtractImage_DeviceCmyk_ConvertsSubtractivelyInsteadOfCopyingCmyAsRgb()
    {
        byte[] cyan = new byte[2 * 2 * 4];
        for (int i = 0; i < 4; i++) cyan[i * 4] = 255;   // C = 1, M = Y = K = 0

        byte[] pdf = ImageFixtures.OneImagePdf(cyan, 2, 2, "/DeviceCMYK");

        // (1-1)*(1-0)=0 red, (1-0)*(1-0)=1 green and blue -> cyan. The predecessor produced
        // (255, 0, 0): C into Red, M into Green, Y into Blue, K discarded.
        AssertColor(ExportedPixel(pdf), 0, 255, 255, "a pure-cyan CMYK sample");
    }

    /// <summary>
    /// An <c>/Indexed</c> image carries one palette INDEX per pixel. Byte-count guessing returned
    /// 1 component, treated the indices as grey levels and exported a 16-colour logo as a
    /// near-black smear.
    /// </summary>
    [Fact]
    public void ExtractImage_IndexedColorSpace_ResolvesThePaletteInsteadOfShowingIndicesAsGrey()
    {
        // Palette: entry 0 = red, entry 1 = green. Samples: index 1 everywhere.
        byte[] indices = { 1, 1, 1, 1 };
        byte[] pdf = ImageFixtures.OneImagePdf(
            indices, 2, 2, "[/Indexed /DeviceRGB 1 <FF000000FF00>]");

        AssertColor(ExportedPixel(pdf), 0, 255, 0, "palette entry 1");
    }

    /// <summary>Grey and RGB, the two the predecessor happened to get right, must not regress.</summary>
    [Fact]
    public void ExtractImage_DeviceGrayAndRgb_AreUnchanged()
    {
        byte[] grey = { 0x40, 0x40, 0x40, 0x40 };
        AssertColor(ExportedPixel(ImageFixtures.OneImagePdf(grey, 2, 2, "/DeviceGray")),
            0x40, 0x40, 0x40, "a DeviceGray sample");

        AssertColor(ExportedPixel(ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(2, 2, 10, 200, 30), 2, 2)),
            10, 200, 30, "a DeviceRGB sample");
    }

    /// <summary>
    /// A <c>/Lab</c> image is converted as the renderer draws it. The exporter refused it while it
    /// had a decoder of its own for five colour spaces; it decodes through the renderer now, so
    /// L = 100, a = b = 0 is white. Black is the control: L = 0.
    /// </summary>
    [Fact]
    public void ExtractImage_Lab_IsConvertedAsTheRendererDrawsIt()
    {
        const string lab = "[/Lab << /WhitePoint [0.9505 1.0 1.089] >>]";
        byte[] white = { 255, 128, 128, 255, 128, 128, 255, 128, 128, 255, 128, 128 };
        byte[] black = { 0, 128, 128, 0, 128, 128, 0, 128, 128, 0, 128, 128 };

        AssertColor(ExportedPixel(ImageFixtures.OneImagePdf(white, 2, 2, lab)), 255, 255, 255, "L = 100");
        AssertColor(ExportedPixel(ImageFixtures.OneImagePdf(black, 2, 2, lab)), 0, 0, 0, "L = 0");
    }

    [Theory]
    [InlineData(false, false)]   // [/ICCBased 6 0 R], profile /N 3
    [InlineData(true, false)]    // [/ICCBased 6 0 R], profile /N 7 0 R
    [InlineData(false, true)]    // [/Indexed [/ICCBased 6 0 R] 1 …], /N 3
    [InlineData(true, true)]     // [/Indexed [/ICCBased 6 0 R] 1 …], /N 7 0 R
    /// <summary>
    /// An <c>/ICCBased</c> image, directly and as an <c>/Indexed</c> base, is exported through the
    /// alternate its profile's <c>/N</c> names, whichever way <c>/N</c> is spelled.
    /// </summary>
    /// <remarks>
    /// Both reads were <c>GetInteger</c>, which does not follow a reference, so <c>/N 7 0 R</c>
    /// read 0 and a perfectly ordinary RGB image was refused as "unsupported /N". They were
    /// resolved with the rest of the exporter's geometry reads and went unpinned until this test;
    /// the palette's entry 1 is the same colour as the direct image, so every case expects one
    /// answer.
    /// </remarks>
    public void ExtractImage_IccBasedProfileN_IsReadWhicheverWayItIsSpelled(bool indirectN, bool asIndexedBase)
    {
        byte[] samples = asIndexedBase ? new byte[] { 1, 1, 1, 1 } : ImageFixtures.SolidRgb(2, 2, 10, 200, 30);
        string colorSpace = asIndexedBase ? "[/Indexed [/ICCBased 6 0 R] 1 <FF00000AC81E>]" : "[/ICCBased 6 0 R]";

        byte[] pdf = ImageFixtures.OneImagePdf(samples, 2, 2, colorSpace, extraObjects: b => b
            .StreamObj(6, "<< /N " + (indirectN ? "7 0 R" : "3"), string.Empty)
            .Obj(7, "3"));

        AssertColor(ExportedPixel(pdf), 10, 200, 30, "an sRGB-alternate /ICCBased sample");
    }

    /// <summary>
    /// A raw sample buffer shorter than <c>/Width</c> × <c>/Height</c> is refused, as the renderer
    /// refuses to draw it, and the complete buffer is the control.
    /// </summary>
    /// <remarks>
    /// The exporter's own decoder exported the covered pixels and left the rest black. It decodes
    /// through the renderer now, and an image the renderer will not draw is not one to export as if
    /// it had been: a partial image with black where the data ran out is the plausible-looking wrong
    /// PNG this class's remarks call worse than a refusal.
    /// </remarks>
    [Fact]
    public void ExtractImage_ATruncatedSampleBufferIsRefusedAsTheRendererRefusesIt()
    {
        // 2×2 declared, three pixels of RGB supplied.
        byte[] truncated = { 255, 0, 0, 0, 255, 0, 0, 0, 255 };
        byte[] complete  = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9 };

        using var diagnostics = new ThreadLocalDiagnostics();
        using (var input = new MemoryStream(ImageFixtures.OneImagePdf(truncated, 2, 2, "/DeviceRGB")))
            Assert.Throws<NotSupportedException>(() => PdfImageExporter.ExtractImage(input, 0, 0));
        Assert.Contains(diagnostics.Events, e => e.Kind == RenderDiagnosticKind.ImageDecodeFailed);

        AssertColor(ExportedPixel(ImageFixtures.OneImagePdf(complete, 2, 2, "/DeviceRGB"), 1, 1),
            9, 9, 9, "the fourth pixel of the complete buffer");
    }

    /// <summary>
    /// The raw-sample export writes through the bitmap's own buffer, not through the allocating
    /// <c>SKBitmap.Pixels</c> setter.
    /// </summary>
    /// <remarks>
    /// The last pure <c>SKColor[]</c>-build-and-assign in the tree outside the sanctioned fallbacks.
    /// Both routes produce the same pixels, so no assertion about the PNG can say which ran — which
    /// is what <see cref="Tetitulz.Pdf.Base.Images.BitmapPixels.BufferWalks"/> is for. The counters are
    /// <c>[ThreadStatic]</c> and the export runs on the calling thread.
    /// </remarks>
    [Fact]
    public void ExtractImage_RawSamplesAreWrittenThroughTheBitmapBuffer()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(4, 4, 10, 200, 30), 4, 4);

        Tetitulz.Pdf.Base.Images.BitmapPixels.ResetCounters();
        using var input = new MemoryStream(pdf);
        var img = PdfImageExporter.ExtractImage(input, 0, 0);

        Assert.Equal("image/png", img.MimeType);
        Assert.True(Tetitulz.Pdf.Base.Images.BitmapPixels.BufferWalks > 0,
            "the raw-sample export never reached the buffer walk");
        Assert.Equal(0, Tetitulz.Pdf.Base.Images.BitmapPixels.PropertyFallbacks);
    }

    // ================================================================
    //  PdfImageExporter — the decode is bounded before it allocates
    // ================================================================

    /// <summary>
    /// An adversarial image declares <c>/Width 1 /Height 1</c> — passing every cap in this method —
    /// while its Flate payload inflates to a PNG whose OWN header says 16384x12000. Raw
    /// <c>SKBitmap.Decode</c> allocates the codec's real frame (~786 MB) before the <c>w*h</c>
    /// check in the raw-pixel fallback below is ever reached.
    ///
    /// <para>The assertion is the guard's own evidence — the refusal it reports — never a wall
    /// clock. A guard test that merely finishes fast has quite possibly not run.</para>
    /// </summary>
    [Fact]
    public void ExtractImage_OversizedEmbeddedCodec_IsRefusedBeforeAllocating()
    {
        byte[] bomb = PngClaimingSize(16384, 12000);

        // Control: the fixture really does reach the codec and really does claim the huge size —
        // without this the guard assertion below would also pass for bytes no codec recognises.
        using (var probe = SKCodec.Create(new MemoryStream(bomb)))
        {
            Assert.NotNull(probe);
            Assert.Equal(16384, probe.Info.Width);
            Assert.Equal(12000, probe.Info.Height);
        }

        byte[] pdf = ImageFixtures.OneImagePdf(
            Deflate(bomb), 1, 1, "/DeviceGray", extraImageKeys: "/Filter /FlateDecode ");

        using var diagnostics = new ThreadLocalDiagnostics();
        using var input = new MemoryStream(pdf);
        // The export is refused as a whole: the renderer's decode, which the exporter shares, will
        // not draw the stream either. (It returned one pixel read from the PNG's first byte before.)
        Assert.Throws<NotSupportedException>(() => PdfImageExporter.ExtractImage(input, 0, 0));

        Assert.Contains(diagnostics.Events,
            e => e.Kind == RenderDiagnosticKind.ImageDecodeFailed &&
                 e.Detail != null && e.Detail.Contains("16384x12000", StringComparison.Ordinal));
    }

    /// <summary>
    /// A real, complete PNG whose IHDR has been rewritten to claim <paramref name="width"/> x
    /// <paramref name="height"/>, with the chunk CRC recomputed so the codec accepts the header.
    ///
    /// <para>Built by re-heading an encoder-produced file rather than hand-rolling one: a decoder
    /// needs the chunks it expects up to the first IDAT before it will report a size at all, so a
    /// signature-plus-IHDR stub is silently rejected and the fixture never reaches the code under
    /// test — banned test pattern 3 arriving through the fixture instead of the assertion.</para>
    /// </summary>
    private static byte[] PngClaimingSize(int width, int height)
    {
        using var tiny = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Opaque);
        tiny.SetPixel(0, 0, new SKColor(1, 2, 3));
        using var img = SKImage.FromBitmap(tiny);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        byte[] png = data.ToArray();

        // Signature is 8 bytes; the IHDR chunk is length(4) + "IHDR"(4) + data(13) + crc(4), so its
        // width and height sit at 16 and 20 and its CRC covers bytes 12..28.
        WriteBe(png, 16, width);
        WriteBe(png, 20, height);
        var crcInput = new byte[4 + 13];
        Buffer.BlockCopy(png, 12, crcInput, 0, 4 + 13);
        WriteBe(png, 29, unchecked((int)Crc32(crcInput)));
        return png;
    }

    private static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>zlib-wrapped deflate — what <c>/FlateDecode</c> means (§7.4.4).</summary>
    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    // ================================================================
    //  PdfThumbnailManager — the caller's output stream, and an honest count
    // ================================================================

    /// <summary>A write-only, non-seekable destination — a network response body.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => _inner.Write(b, o, c);
        internal byte[] ToArray() => _inner.ToArray();
    }

    private static byte[] TwoPagePdf()
    {
        const string content = "0 0 1 rg 10 10 50 50 re f";
        return new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] "
                  + "/Resources << >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", content)
            .Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] "
                  + "/Resources << >> /Contents 4 0 R >>")
            .Finish();
    }

    private static int PagesWithThumb(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        int n = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
            if (doc.ResolveObject(doc.Pages[i].Dictionary[PdfName.Thumb]) is PdfStream) n++;
        return n;
    }

    /// <summary>
    /// The reported count is the number of pages that actually carry a <c>/Thumb</c>, not the number
    /// of images written. Asserting the two against each other is the point: the predecessor emitted
    /// the image object and only then discovered it could not patch the page, so the two could
    /// disagree with nothing recording it.
    /// </summary>
    [Fact]
    public void GenerateThumbnails_ReportedCountMatchesThePagesThatCarryAThumb()
    {
        using var input = new MemoryStream(TwoPagePdf());
        using var output = new MemoryStream();

        int reported = PdfThumbnailManager.GenerateThumbnails(input, output);
        byte[] result = output.ToArray();

        Assert.Equal(2, reported);
        Assert.Equal(reported, PagesWithThumb(result));
        Assert.Null(PdfThumbnailManager.LastError);
    }

    /// <summary>
    /// The embedded thumbnail is a real DeviceRGB image of the page, not a placeholder: the fixture
    /// page is white with a blue square, so the thumbnail's samples must contain a blue pixel.
    /// </summary>
    [Fact]
    public void GenerateThumbnails_EmbedsThePageContentAsDeviceRgbSamples()
    {
        using var input = new MemoryStream(TwoPagePdf());
        using var output = new MemoryStream();
        PdfThumbnailManager.GenerateThumbnails(input, output, dpi: 72);

        using var result = new MemoryStream(output.ToArray());
        var info = PdfThumbnailManager.ReadThumbnail(result, 0);
        Assert.NotNull(info);
        Assert.Equal("DeviceRGB", info.ColorSpace);
        Assert.Equal(info.Width * info.Height * 3, info.PixelData.Length);

        bool sawBlue = false;
        for (int i = 0; i + 2 < info.PixelData.Length; i += 3)
            if (info.PixelData[i] < 64 && info.PixelData[i + 1] < 64 && info.PixelData[i + 2] > 192)
                sawBlue = true;
        Assert.True(sawBlue, "the thumbnail carries none of the page's blue square");
    }

    /// <summary>
    /// A thumbnail whose <c>/Width</c>, <c>/Height</c> and <c>/ColorSpace</c> are each their own
    /// object reports them, as the inline spelling does.
    /// </summary>
    /// <remarks>
    /// §12.3.4 makes a thumbnail an image XObject, whose entries may be indirect (§7.3.10). Read raw,
    /// they came back 0×0 "Unknown" — a thumbnail a caller could not decode from its own samples.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadThumbnail_ReportsItsGeometryWhicheverWayItIsSpelled(bool indirect)
    {
        string thumb = indirect
            ? "<< /Width 6 0 R /Height 7 0 R /ColorSpace 8 0 R /BitsPerComponent 8 /Length 12 >>"
            : "<< /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 12 >>";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Thumb 5 0 R >>",
            "null",
            thumb + "\nstream\nABCDEFGHIJKL\nendstream",
            "2", "2", "/DeviceRGB",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");

        var info = PdfThumbnailManager.ReadThumbnail(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())), 0);

        Assert.NotNull(info);
        Assert.Equal(2, info.Width);
        Assert.Equal(2, info.Height);
        Assert.Equal("DeviceRGB", info.ColorSpace);
    }

    /// <summary>
    /// A page dictionary written <b>directly</b> into <c>/Kids</c> instead of as an indirect
    /// reference. §7.7.3.2 requires a reference, so this is malformed — but it parses, and the page
    /// then has no object number at all, which is the condition the writer must handle.
    /// </summary>
    private static byte[] PdfWithUnnumberedPage() =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [ << /Type /Page /MediaBox [0 0 72 72] "
                  + "/Resources << >> /Contents 4 0 R >> ] /Count 1 >>")
            .StreamObj(4, "<<", "0 0 1 rg 10 10 50 50 re f")
            .Finish();

    /// <summary>
    /// A page the writer cannot patch must contribute <b>nothing</b> — no count, and no image
    /// object either. The predecessor wrote the /Thumb image and only then discovered it could not
    /// locate the page, so the output grew an orphaned XObject that no page referenced while the
    /// return value still counted it: "12 thumbnails generated" for a file that displays none, with
    /// the only trace a <c>Debug.WriteLine</c> that does not exist in a Release build.
    /// </summary>
    [Fact]
    public void GenerateThumbnails_MalformedPageWithNoObjectNumber_CountsNothingAndWritesNoOrphan()
    {
        byte[] original = PdfWithUnnumberedPage();
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();

        int reported = PdfThumbnailManager.GenerateThumbnails(input, output);
        byte[] result = output.ToArray();

        Assert.Equal(0, reported);
        Assert.Equal(0, PagesWithThumb(result));

        // No orphan: nothing at all was appended, so the output is the input byte for byte.
        Assert.Equal(original, result);

        // ...and the reason is reported rather than left to a Debug.WriteLine.
        Assert.NotNull(PdfThumbnailManager.LastError);
        Assert.Contains("could not be located", PdfThumbnailManager.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-seekable destination — a server streaming the result straight to a response body — must
    /// work. The hand-rolled writer recorded every xref offset from <c>output.Position</c>, so it
    /// threw <see cref="NotSupportedException"/> from a method documenting only
    /// <see cref="ArgumentNullException"/>/<see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    [Fact]
    public void GenerateThumbnails_NonSeekableOutput_StillProducesAReadableFile()
    {
        using var input = new MemoryStream(TwoPagePdf());
        var output = new NonSeekableStream();

        int reported = PdfThumbnailManager.GenerateThumbnails(input, output);

        Assert.Equal(2, reported);
        Assert.Equal(2, PagesWithThumb(output.ToArray()));
    }

    /// <summary>
    /// A destination already holding bytes — <c>FileMode.Append</c>, or one part of a multipart
    /// body — must still emit a <c>startxref</c> that points at the cross-reference section it
    /// actually wrote. The hand-rolled writer numbered every offset from <c>output.Position</c>
    /// while computing <c>/Prev</c> from a search of the original bytes, so the two were on
    /// different bases and the emitted <c>startxref</c> pointed into the middle of an object body.
    ///
    /// <para><b>Asserting "the result still parses" is not enough and was the first version of this
    /// test:</b> the parser repairs a file whose xref will not resolve, so it succeeded with the
    /// offsets wrong. Reading the emitted <c>startxref</c> and checking what is AT that byte is
    /// what makes the claim in the name checkable.</para>
    /// </summary>
    [Fact]
    public void GenerateThumbnails_OutputPrePositioned_StartxrefCountsFromWhereTheOriginalLanded()
    {
        byte[] preamble = Encoding.ASCII.GetBytes("PREAMBLE-BYTES-NOT-PART-OF-THE-PDF\n");

        using var input = new MemoryStream(TwoPagePdf());
        using var output = new MemoryStream();
        output.Write(preamble, 0, preamble.Length);

        int reported = PdfThumbnailManager.GenerateThumbnails(input, output);
        byte[] all = output.ToArray();

        Assert.Equal(2, reported);

        // ISO 32000-2 §7.5.2: offsets count from the header's percent sign. The original's
        // sections already count from its header, so the appended revision's startxref counts from
        // where that header landed in the output — the preamble's length — and the whole output
        // reads both as it is and with the preamble stripped. An earlier reading wrote output
        // positions here, which made the file readable neither way.
        long startxref = LastStartXref(all);
        Assert.Equal("xref", Encoding.ASCII.GetString(all, (int)(startxref + preamble.Length), 4));

        using (var whole = PdfDocument.Open(new MemoryStream(all)))
            Assert.Equal(2, whole.Pages.Count);
        using (var stripped = PdfDocument.Open(new MemoryStream(all, preamble.Length, all.Length - preamble.Length)))
            Assert.Equal(2, stripped.Pages.Count);
    }

    /// <summary>The offset the file's final <c>startxref</c> declares.</summary>
    private static long LastStartXref(byte[] bytes)
    {
        string tail = Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 512),
                                               Math.Min(512, bytes.Length));
        int i = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(i >= 0, "the output carries no startxref");
        int p = i + "startxref".Length;
        while (p < tail.Length && (tail[p] == '\r' || tail[p] == '\n' || tail[p] == ' ')) p++;
        int e = p;
        while (e < tail.Length && tail[e] >= '0' && tail[e] <= '9') e++;
        Assert.True(e > p, "the output's startxref carries no number");
        return long.Parse(tail.Substring(p, e - p));
    }

    // ================================================================
    //  SvgRasterizer — a failure that says so
    // ================================================================

    /// <summary>
    /// <c>RasterizeToPng</c> wrapped the whole SVG → PDF → PNG chain in
    /// <c>catch { return null; }</c>, so a caller could not tell "no SVG supplied" from "the SVG was
    /// rejected" from "the converter is broken". HtmlToPdf's managed engine reaches this through
    /// <c>HtmlToPdfOptions.SvgRasterizer</c> and simply leaves a hole in the page.
    /// </summary>
    [Fact]
    public void RasterizeToPng_MalformedSvg_ReportsWhyItReturnedNull()
    {
        Assert.Null(SvgRasterizer.RasterizeToPng("<svg><rect", 96));
        Assert.NotNull(SvgRasterizer.LastError);
        Assert.Contains("rasterization failed", SvgRasterizer.LastError, StringComparison.Ordinal);
    }

    /// <summary>"No SVG was supplied" is a different answer from "the SVG was rejected".</summary>
    [Fact]
    public void RasterizeToPng_EmptyInput_SaysNothingWasSupplied()
    {
        Assert.Null(SvgRasterizer.RasterizeToPng("   ", 96));
        Assert.NotNull(SvgRasterizer.LastError);
        Assert.Contains("no SVG markup", SvgRasterizer.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A good SVG rasterizes to a PNG with the graphic's ink in it and reports nothing. Both halves
    /// matter: a blank page encodes to a perfectly valid non-empty PNG, so the pixel assertion is
    /// what makes the rasterizer's own behaviour checkable, and the null <c>LastError</c> is what
    /// stops the two tests above from passing against a rasterizer that reports unconditionally.
    /// </summary>
    [Fact]
    public void RasterizeToPng_ValidSvg_ProducesInkAndReportsNothing()
    {
        byte[] png = SvgRasterizer.RasterizeToPng(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""40"" height=""40"">
                <rect x=""0"" y=""0"" width=""40"" height=""40"" fill=""#FF0000""/></svg>", 96);

        Assert.NotNull(png);
        Assert.Null(SvgRasterizer.LastError);

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        AssertColor(bmp.GetPixel(bmp.Width / 2, bmp.Height / 2), 255, 0, 0, "the rasterized rectangle");
    }

    /// <summary>
    /// A rasterization that succeeded but dropped content is neither a clean success nor a null: the
    /// PNG comes back AND the converter's reason is relayed, so a partial result cannot be mistaken
    /// for a faithful one.
    /// </summary>
    [Fact]
    public void RasterizeToPng_SvgWithUnencodableText_RelaysTheConverterWarningWithThePng()
    {
        const string hebrew = "שלום";
        byte[] png = SvgRasterizer.RasterizeToPng(
            $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""60"" height=""30"">
                 <text x=""2"" y=""20"" font-size=""12"">{hebrew}</text></svg>", 96);

        Assert.NotNull(png);
        Assert.NotNull(SvgRasterizer.LastError);
        Assert.Contains("WinAnsiEncoding cannot", SvgRasterizer.LastError, StringComparison.Ordinal);
    }

    // ================================================================
    //  SvgRasterizer — the intrinsic-size fallback chain (width/height, else viewBox, else the
    //  SVG 300x150 default) was untested: every fixture above asserts a pixel colour or an error
    //  string, never the page size the SVG actually converted into.
    // ================================================================

    private static (int Width, int Height) RasterizedSize(string svgXml)
    {
        byte[] png = SvgRasterizer.RasterizeToPng(svgXml, 96);
        Assert.NotNull(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return (bmp.Width, bmp.Height);
    }

    /// <summary>
    /// A unit on <c>width</c>/<c>height</c> ("120px") is stripped the same as no unit at all
    /// ("120") — <c>ParseDim</c> reads the leading number and ignores what follows. A rasterizer
    /// that failed to strip the unit would fail <c>double.TryParse("120px")</c>, read 0, and fall
    /// through to the viewBox this fixture also carries — a different, distinguishable size.
    /// </summary>
    [Fact]
    public void RasterizeToPng_UnitOnWidthAndHeight_IsStrippedLikeNoUnit()
    {
        const string viewBox = "viewBox=\"0 0 999 999\""; // a decoy: only reached if width/height fail to parse
        var withUnit = RasterizedSize(
            $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""120px"" height=""60px"" {viewBox}>
                 <rect width=""120"" height=""60"" fill=""#FF0000""/></svg>");
        var noUnit = RasterizedSize(
            $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""120"" height=""60"" {viewBox}>
                 <rect width=""120"" height=""60"" fill=""#FF0000""/></svg>");

        Assert.Equal(noUnit, withUnit);
        // And it really did take the 120x60 declaration, not the 999x999 decoy.
        Assert.True(withUnit.Width < withUnit.Height * 3, "the decoy viewBox was used instead of width/height");
    }

    /// <summary>
    /// With no <c>width</c>/<c>height</c>, the <c>viewBox</c>'s size is used — a 3:2 box reads as
    /// 3:2, distinct from the SVG default's 2:1.
    /// </summary>
    [Fact]
    public void RasterizeToPng_NoWidthOrHeight_FallsBackToTheViewBoxSize()
    {
        var (w, h) = RasterizedSize(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 300 200"">
                <rect width=""300"" height=""200"" fill=""#FF0000""/></svg>");

        Assert.InRange((double)w / h, 1.4, 1.6); // 3:2, not the 2:1 default
    }

    /// <summary>
    /// With no <c>width</c>/<c>height</c> and no <c>viewBox</c>, the SVG 1.1 §7.2 replaced-element
    /// default (300x150, 2:1) applies.
    /// </summary>
    [Fact]
    public void RasterizeToPng_NoSizingInformationAtAll_UsesTheSvgDefault()
    {
        var (w, h) = RasterizedSize(
            @"<svg xmlns=""http://www.w3.org/2000/svg""><rect width=""10"" height=""10""/></svg>");

        Assert.InRange((double)w / h, 1.9, 2.1); // 300:150
    }

    /// <summary>
    /// A non-positive <c>width</c>/<c>height</c> ("-10", matching a percentage or any other value
    /// <c>ParseDim</c> cannot resolve to a positive number) is not used — it falls back to the
    /// <c>viewBox</c>, the same as if <c>width</c>/<c>height</c> were absent.
    /// </summary>
    [Fact]
    public void RasterizeToPng_NegativeWidthOrHeight_FallsBackToTheViewBoxSize()
    {
        var negative = RasterizedSize(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""-10"" height=""-10"" viewBox=""0 0 300 200"">
                <rect width=""300"" height=""200"" fill=""#FF0000""/></svg>");
        var absent = RasterizedSize(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 300 200"">
                <rect width=""300"" height=""200"" fill=""#FF0000""/></svg>");

        Assert.Equal(absent, negative);
    }

    /// <summary>
    /// A percentage <c>width</c> is not a length — it resolves against a containing block nothing
    /// here has — so the <c>viewBox</c> sizes the page: 400×200 at 96 dpi is 533×267 pixels. The
    /// rasterizer's own copy of the size reading took "100%" for 100 points (133 pixels wide), the
    /// defect the converter's copy had already been fixed of; it asks the converter now.
    /// </summary>
    [Fact]
    public void RasterizeToPng_PercentageWidth_FallsBackToTheViewBoxSize()
    {
        var (w, h) = RasterizedSize(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""100%"" height=""100%"" viewBox=""0 0 400 200"">
                <rect width=""400"" height=""200"" fill=""#FF0000""/></svg>");

        Assert.Equal((534, 267), (w, h));
    }
}
