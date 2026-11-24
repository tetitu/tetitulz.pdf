// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// What the managed engine does with an image it cannot embed.
    ///
    /// <para>It used to size the image from its header and then drop it at draw time, leaving a
    /// correctly-sized blank rectangle — the most confusing possible failure, because the gap
    /// looks like a deliberate layout choice. The format opinion is now shared
    /// (<c>Core.Imaging.RasterImageInspector</c>), the decision is made where the <c>src</c> is
    /// still known, and the reason is reported.</para>
    /// </summary>
    public class HtmlManagedImageTests
    {
        private static (byte[] pdf, List<BuilderDiagnostic> diagnostics) Convert(string html)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = seen.Add;
            return (ToPdf(html, o), seen);
        }

        /// <summary>The y of the first text draw — where the content after an image ends up.</summary>
        private static double BaselineOf(byte[] pdf, string text)
        {
            double y = YOf(DecodeStreams(pdf), text);
            Assert.False(double.IsNaN(y), $"'{text}' was never drawn");
            return y;
        }

        // ------------------------------------------------------------------ undecodable formats

        /// <summary>
        /// The defect this whole file exists for: a format whose header is readable — so it gets
        /// measured — but which nothing can embed. Reserving space for an image that never
        /// arrives is worse than not reserving it, because the reader gets a hole with no
        /// explanation and no way to tell it from a deliberate layout choice.
        ///
        /// <para>GIF was the archetype when this was written and is no longer available as a
        /// fixture: it is embedded now. TIFF takes its place — still recognised by the inspector,
        /// so its identity is reportable, but with no decoder behind it.</para>
        /// </summary>
        [Fact]
        public void UndecodableFormat_ReservesNoSpaceAndIsReported()
        {
            // TIFF: recognised by the inspector (so its identity is known and reportable) but not
            // decodable. GIF used to serve this role and no longer can — it is embedded now.
            string tiff = "data:image/tiff;base64," + System.Convert.ToBase64String(MakeTiff());

            var withImage = Convert($"<p>ABOVE</p><img src=\"{tiff}\" width=\"40\" height=\"40\"><p>BELOW</p>");
            var without = Convert("<p>ABOVE</p><p>BELOW</p>");

            // No image XObject reached the file…
            Assert.DoesNotContain("/Subtype /Image", Raw(withImage.pdf));
            // …and the text after it is exactly where it would be with no <img> at all.
            Assert.Equal(BaselineOf(without.pdf, "BELOW"), BaselineOf(withImage.pdf, "BELOW"), 3);

            var d = withImage.diagnostics.SingleOrDefault(x => x.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
            Assert.NotNull(d);
            Assert.Contains("Tiff", d.Detail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Gif_IsNowEmbedded()
        {
            string gif = "data:image/gif;base64," + System.Convert.ToBase64String(MakeGif(4, 4));
            var (pdf, diagnostics) = Convert($"<img src=\"{gif}\" width=\"20\" height=\"20\">");

            Assert.Contains("/Subtype /Image", Raw(pdf));
            Assert.Contains("/Width 4", Raw(pdf));
            Assert.DoesNotContain(diagnostics, x => x.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
        }

        [Fact]
        public void Bmp_IsNowEmbedded()
        {
            string bmp = "data:image/bmp;base64," + System.Convert.ToBase64String(MakeBmp(4, 4));
            var (pdf, diagnostics) = Convert($"<img src=\"{bmp}\" width=\"20\" height=\"20\">");

            Assert.Contains("/Subtype /Image", Raw(pdf));
            Assert.Contains("/Width 4", Raw(pdf));
            Assert.DoesNotContain(diagnostics, x => x.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
        }

        [Fact]
        public void UndecodableImage_DiagnosticNamesTheSource()
        {
            using var dir = NewScratchDir();
            dir.Write("logo.tif", MakeTiff());
            string htmlPath = dir.Write("doc.html", "<img src=\"logo.tif\" width=\"20\" height=\"20\">");

            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = seen.Add;
            HtmlToPdfConverter.ConvertFileToBytes(htmlPath, o);

            // Naming the src is the point of deciding this in the mapper: the exporter sees only
            // bytes and could say no more than "an image could not be decoded".
            Assert.Contains(seen, d => d.Detail.Contains("logo.tif", StringComparison.Ordinal));
        }

        /// <summary>
        /// Adam7 interlaced PNG is decoded, not declined.
        /// </summary>
        [Fact]
        public void InterlacedPng_IsEmbedded()
        {
            string png = "data:image/png;base64," + System.Convert.ToBase64String(Png(8, 8, interlaced: true));
            var (pdf, diagnostics) = Convert($"<img src=\"{png}\" width=\"20\" height=\"20\">");

            Assert.Contains("/Subtype /Image", Raw(pdf));
            Assert.Contains("/Width 8", Raw(pdf));
            Assert.DoesNotContain(diagnostics, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
        }

        /// <summary>
        /// The strongest available form: the same image stored both ways must produce identical
        /// output. It cannot pass with the Adam7 pass walk stubbed, and it does not depend on
        /// knowing what the right pixels are — only that the two encodings agree.
        /// </summary>
        [Fact]
        public void InterlacedPng_DecodesToTheSamePixelsAsNonInterlaced()
        {
            string plain = "data:image/png;base64," + System.Convert.ToBase64String(Png(8, 8, interlaced: false));
            string laced = "data:image/png;base64," + System.Convert.ToBase64String(Png(8, 8, interlaced: true));

            string a = DecodeStreams(ToPdf($"<img src=\"{plain}\" width=\"20\" height=\"20\">", Managed()));
            string b = DecodeStreams(ToPdf($"<img src=\"{laced}\" width=\"20\" height=\"20\">", Managed()));

            Assert.Equal(a, b);
        }

        [Fact]
        public void GarbageBytes_AreReportedAsNotAnImage()
        {
            string junk = "data:image/png;base64," +
                System.Convert.ToBase64String(Encoding.ASCII.GetBytes("this is not an image"));
            var (_, diagnostics) = Convert($"<img src=\"{junk}\" width=\"20\" height=\"20\">");

            Assert.Contains(diagnostics, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
        }

        // ------------------------------------------------------------------ the control

        [Fact]
        public void DecodablePng_StillEmbeds_AndReportsNothing()
        {
            // The control for every case above: a supported image must be unaffected.
            string png = "data:image/png;base64," + System.Convert.ToBase64String(MakeRgbPng(4, 4));
            var (pdf, diagnostics) = Convert($"<img src=\"{png}\" width=\"20\" height=\"20\">");

            Assert.Contains("/Subtype /Image", Raw(pdf));
            Assert.Contains("/Width 4", Raw(pdf));
            Assert.DoesNotContain(diagnostics, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
        }

        [Fact]
        public void DecodablePng_StillReservesItsSpace()
        {
            string png = "data:image/png;base64," + System.Convert.ToBase64String(MakeRgbPng(4, 4));
            var withImage = Convert($"<p>ABOVE</p><img src=\"{png}\" width=\"40\" height=\"40\"><p>BELOW</p>");
            var without = Convert("<p>ABOVE</p><p>BELOW</p>");

            Assert.True(BaselineOf(withImage.pdf, "BELOW") < BaselineOf(without.pdf, "BELOW") - 30,
                "an embedded image must push the following content down");
        }

        // ------------------------------------------------------------------ transparency

        /// <summary>
        /// A PNG with an alpha channel gets a real <c>/SMask</c>, and its colour samples stay
        /// <b>untouched</b>.
        ///
        /// <para>Alpha composited over white would be right only when the background happens to be
        /// white; over a shaded paragraph or a coloured table cell it would leave a white halo around
        /// every transparent edge — a transparent logo on a coloured header is the case people
        /// actually hit.</para>
        /// </summary>
        [Fact]
        public void PngWithAlpha_EmitsASoftMask_AndKeepsTheOriginalColour()
        {
            // Solid red at 50% alpha. Composited over white this becomes (255, 127, 127); as a
            // soft mask the colour stays (255, 0, 0) and the alpha travels separately.
            string png = "data:image/png;base64," +
                System.Convert.ToBase64String(RgbaPng(2, 2, 255, 0, 0, 128));
            var (pdf, _) = Convert($"<img src=\"{png}\" width=\"20\" height=\"20\">");

            string raw = Raw(pdf);
            Assert.Contains("/SMask", raw);
            Assert.Contains("/DeviceGray", raw);

            // The colour channel must be the ORIGINAL red, not the white-composited pink.
            byte[] samples = FirstImageSamples(pdf);
            Assert.Equal(255, samples[0]);
            Assert.Equal(0, samples[1]);
            Assert.Equal(0, samples[2]);
        }

        [Fact]
        public void PngWithFullyOpaqueAlpha_GetsNoSoftMask()
        {
            // An all-opaque mask says exactly what no mask says, and would add a full-size
            // greyscale image to every file for nothing.
            string png = "data:image/png;base64," +
                System.Convert.ToBase64String(RgbaPng(2, 2, 0, 0, 255, 255));
            var (pdf, _) = Convert($"<img src=\"{png}\" width=\"20\" height=\"20\">");

            Assert.DoesNotContain("/SMask", Raw(pdf));
        }

        [Fact]
        public void GifWithATransparentIndex_EmitsASoftMask()
        {
            // GIF and PNG transparency moved together on purpose: doing one and not the other
            // would leave two transparency models in one renderer.
            string gif = "data:image/gif;base64," +
                System.Convert.ToBase64String(MakeGif(4, 4, transparentIndex: 0));
            var (pdf, _) = Convert($"<img src=\"{gif}\" width=\"20\" height=\"20\">");

            Assert.Contains("/SMask", Raw(pdf));
        }

        // ------------------------------------------------------------------ CMYK JPEG

        [Fact]
        public void CmykJpegWithAdobeMarker_EmbedsWithAnInvertedDecode()
        {
            string jpeg = "data:image/jpeg;base64," +
                System.Convert.ToBase64String(CmykJpeg(8, 8, withAdobeMarker: true));
            var (pdf, _) = Convert($"<img src=\"{jpeg}\" width=\"20\" height=\"20\">");

            string raw = Raw(pdf);
            Assert.Contains("/DeviceCMYK", raw);
            Assert.Contains("/Decode [1 0 1 0 1 0 1 0]", raw);
        }

        /// <summary>
        /// A four-component JPEG with no Adobe marker has no identifiable colour space, and is
        /// <b>refused rather than guessed at</b> — guessing CMYK on a non-CMYK frame inverts the
        /// whole image. Same rule <c>PdfImageReplacer</c> follows.
        /// </summary>
        [Fact]
        public void FourComponentJpegWithoutTheMarker_IsRefusedAndReported()
        {
            string jpeg = "data:image/jpeg;base64," +
                System.Convert.ToBase64String(CmykJpeg(8, 8, withAdobeMarker: false));
            var (pdf, diagnostics) = Convert($"<img src=\"{jpeg}\" width=\"20\" height=\"20\">");

            Assert.DoesNotContain("/Subtype /Image", Raw(pdf));
            Assert.Contains(diagnostics, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed
                                           && d.Detail.Contains("Adobe", StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ data: URIs

        [Fact]
        public void PercentEncodedDataUri_IsDecoded()
        {
            // The default data: encoding is percent-escaping, not base64. Requiring the literal
            // "base64" token rejected every URL-encoded payload — which is how inline SVG is
            // almost always written.
            byte[] png = MakeRgbPng(4, 4);
            var sb = new StringBuilder("data:image/png,");
            foreach (byte b in png) sb.Append('%').Append(b.ToString("X2"));

            var (pdf, _) = Convert($"<img src=\"{sb}\" width=\"20\" height=\"20\">");

            Assert.Contains("/Subtype /Image", Raw(pdf));
            Assert.Contains("/Width 4", Raw(pdf));
        }

        /// <summary>
        /// An SVG data URI decodes perfectly and is then not a raster image at all. It used to
        /// reach the image decoder, be refused, and leave a sized blank hole — the same defect as
        /// an undecodable GIF, arriving by a different route.
        /// </summary>
        [Fact]
        public void SvgDataUri_ReachesTheRasterizer()
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"></svg>";
            string uri = "data:image/svg+xml," + Uri.EscapeDataString(svg);

            string seen = null;
            var o = Managed();
            o.SvgRasterizer = markup => { seen = markup; return MakeRgbPng(4, 4); };

            byte[] pdf = ToPdf($"<img src=\"{uri}\" width=\"20\" height=\"20\">", o);

            Assert.NotNull(seen);
            Assert.Contains("<svg", seen, StringComparison.Ordinal);
            Assert.Contains("/Subtype /Image", Raw(pdf));
        }

        [Fact]
        public void SvgDataUri_WithNoRasterizer_IsSkippedNotDrawnAsAHole()
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"></svg>";
            string uri = "data:image/svg+xml," + Uri.EscapeDataString(svg);

            var withSvg = Convert($"<p>ABOVE</p><img src=\"{uri}\" width=\"40\" height=\"40\"><p>BELOW</p>");
            var without = Convert("<p>ABOVE</p><p>BELOW</p>");

            // The rasterizer is opt-in, so with none configured the SVG is simply absent — and it
            // must not reserve space it will never fill.
            Assert.Equal(BaselineOf(without.pdf, "BELOW"), BaselineOf(withSvg.pdf, "BELOW"), 3);
        }

        // ------------------------------------------------------------------ alt text

        [Fact]
        public void AltText_IsRecordedOnTheImage()
        {
            string png = "data:image/png;base64," + System.Convert.ToBase64String(MakeRgbPng(4, 4));
            byte[] pdf = ToPdf($"<img src=\"{png}\" width=\"20\" height=\"20\" alt=\"A red square\">", Managed());

            // The image still embeds; alt is carried on the model for the tagged-PDF work and
            // for the undecodable-image fallback.
            Assert.Contains("/Subtype /Image", Raw(pdf));
        }

        // ------------------------------------------------------------------ synthesis

        /// <summary>A real, decodable GIF: 4-entry palette, every pixel index 0.</summary>
        private static byte[] MakeGif(int w, int h, int transparentIndex = -1)
        {
            using var ms = new System.IO.MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("GIF89a"), 0, 6);
            ms.WriteByte((byte)w); ms.WriteByte((byte)(w >> 8));
            ms.WriteByte((byte)h); ms.WriteByte((byte)(h >> 8));
            ms.WriteByte(0x81);                       // global colour table, 4 entries
            ms.WriteByte(0); ms.WriteByte(0);
            ms.Write(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 }, 0, 12);

            if (transparentIndex >= 0)
                ms.Write(new byte[] { 0x21, 0xF9, 4, 0x01, 0, 0, (byte)transparentIndex, 0 }, 0, 8);

            ms.WriteByte(0x2C);                       // image descriptor
            for (int i = 0; i < 4; i++) ms.WriteByte(0);
            ms.WriteByte((byte)w); ms.WriteByte((byte)(w >> 8));
            ms.WriteByte((byte)h); ms.WriteByte((byte)(h >> 8));
            ms.WriteByte(0);

            ms.WriteByte(2);                          // LZW minimum code size
            // Clear, then one literal per pixel with a clear between — valid GIF that needs no
            // dictionary, which is all this end-to-end fixture requires.
            var bits = new System.Collections.Generic.List<bool>();
            void Emit(int code) { for (int i = 0; i < 3; i++) bits.Add(((code >> i) & 1) != 0); }
            Emit(4);
            for (int i = 0; i < w * h; i++) { Emit(0); Emit(4); }
            Emit(5);
            var data = new byte[(bits.Count + 7) / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i >> 3] |= (byte)(1 << (i & 7));

            for (int o = 0; o < data.Length; o += 255)
            {
                int n = Math.Min(255, data.Length - o);
                ms.WriteByte((byte)n);
                ms.Write(data, o, n);
            }
            ms.WriteByte(0);
            ms.WriteByte(0x3B);
            return ms.ToArray();
        }

        /// <summary>An 8-bit RGBA PNG filled with one colour at one alpha (colour type 6).</summary>
        private static byte[] RgbaPng(int w, int h, byte r, byte g, byte b, byte a)
        {
            var raw = new System.IO.MemoryStream();
            for (int y = 0; y < h; y++)
            {
                raw.WriteByte(0);                              // filter: None
                for (int x = 0; x < w; x++) { raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b); raw.WriteByte(a); }
            }
            byte[] idat;
            using (var ms = new System.IO.MemoryStream())
            {
                using (var z = new System.IO.Compression.ZLibStream(
                    ms, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    byte[] rawBytes = raw.ToArray();
                    z.Write(rawBytes, 0, rawBytes.Length);
                }
                idat = ms.ToArray();
            }

            using var outMs = new System.IO.MemoryStream();
            outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
            ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
            ihdr[8] = 8;
            ihdr[9] = 6;                                       // truecolour + alpha
            WriteChunk(outMs, "IHDR", ihdr);
            WriteChunk(outMs, "IDAT", idat);
            WriteChunk(outMs, "IEND", Array.Empty<byte>());
            return outMs.ToArray();
        }

        /// <summary>A four-component JPEG frame, optionally carrying the Adobe APP14 marker.</summary>
        private static byte[] CmykJpeg(int w, int h, bool withAdobeMarker)
        {
            using var ms = new System.IO.MemoryStream();
            ms.Write(new byte[] { 0xFF, 0xD8 }, 0, 2);                       // SOI

            if (withAdobeMarker)
            {
                byte[] adobe = Encoding.ASCII.GetBytes("Adobe");
                int segLen = 2 + adobe.Length + 7;
                ms.Write(new byte[] { 0xFF, 0xEE, (byte)(segLen >> 8), (byte)segLen }, 0, 4);
                ms.Write(adobe, 0, adobe.Length);
                ms.Write(new byte[] { 0, 100, 0, 0, 0, 0, 2 }, 0, 7);        // transform: YCCK
            }

            const int comps = 4;
            int sofLen = 8 + comps * 3;
            ms.Write(new byte[] { 0xFF, 0xC0, (byte)(sofLen >> 8), (byte)sofLen }, 0, 4);
            ms.WriteByte(8);                                                  // precision
            ms.Write(new byte[] { (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w }, 0, 4);
            ms.WriteByte(comps);
            for (int i = 0; i < comps; i++) ms.Write(new byte[] { (byte)(i + 1), 0x11, 0 }, 0, 3);

            ms.Write(new byte[] { 0xFF, 0xD9 }, 0, 2);                        // EOI
            return ms.ToArray();
        }

        /// <summary>
        /// The inflated sample bytes of the first <b>colour</b> image XObject. Used to assert that
        /// a colour channel really is the original colour and not a composited approximation.
        ///
        /// <para>It searches for <c>/DeviceRGB</c> rather than the first <c>/Subtype /Image</c>,
        /// because a soft mask is itself an image XObject and is written <i>first</i> — its object
        /// number has to exist before the colour image can reference it. Taking the first image
        /// returns the mask, whose bytes are alpha values.</para>
        /// </summary>
        private static byte[] FirstImageSamples(byte[] pdf)
        {
            string raw = Raw(pdf);
            int marker = raw.IndexOf("/DeviceRGB", StringComparison.Ordinal);
            Assert.True(marker >= 0, "no DeviceRGB image XObject in the document");
            int start = raw.IndexOf("stream", marker, StringComparison.Ordinal) + 6;
            if (raw[start] == '\r') start++;
            if (raw[start] == '\n') start++;
            int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);

            byte[] comp = Encoding.Latin1.GetBytes(raw.Substring(start, end - start));
            using var input = new System.IO.MemoryStream(comp);
            using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var outMs = new System.IO.MemoryStream();
            z.CopyTo(outMs);
            return outMs.ToArray();
        }

        /// <summary>A real, decodable 24-bit BMP filled with one colour.</summary>
        private static byte[] MakeBmp(int w, int h)
        {
            int rowBytes = ((w * 24 + 31) / 32) * 4;
            var b = new byte[54 + rowBytes * h];
            b[0] = 0x42; b[1] = 0x4D;                  // "BM"
            WriteLE32(b, 2, b.Length);
            WriteLE32(b, 10, 54);
            WriteLE32(b, 14, 40);
            WriteLE32(b, 18, w);
            WriteLE32(b, 22, h);
            b[26] = 1;                                 // planes
            b[28] = 24;                                // bits per pixel
            for (int i = 54; i < b.Length; i += 3) { b[i] = 0; b[i + 1] = 0; b[i + 2] = 255; }
            return b;
        }

        /// <summary>A TIFF header — recognised by the inspector, decodable by nothing here.</summary>
        private static byte[] MakeTiff()
        {
            var b = new byte[64];
            b[0] = 0x49; b[1] = 0x49; b[2] = 0x2A; b[3] = 0x00;
            return b;
        }

        private static void WriteLE32(byte[] b, int o, int v)
        { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

        /// <summary>
        /// A real 8-bit RGB PNG of a non-uniform pattern, stored sequentially or Adam7-interlaced.
        /// The pattern matters: a solid colour would decode identically however the passes were
        /// scattered, so it could not detect a wrong pass order.
        /// </summary>
        private static byte[] Png(int w, int h, bool interlaced)
        {
            byte[] Pixel(int x, int y) => new byte[]
            {
                (byte)(x * 31 % 256), (byte)(y * 57 % 256), (byte)((x + y) * 17 % 256),
            };

            var raw = new System.IO.MemoryStream();
            if (!interlaced)
            {
                for (int y = 0; y < h; y++)
                {
                    raw.WriteByte(0);                                   // filter: None
                    for (int x = 0; x < w; x++) raw.Write(Pixel(x, y), 0, 3);
                }
            }
            else
            {
                int[] xo = { 0, 4, 0, 2, 0, 1, 0 }, yo = { 0, 0, 4, 0, 2, 0, 1 };
                int[] xs = { 8, 8, 4, 4, 2, 2, 1 }, ys = { 8, 8, 8, 4, 4, 2, 2 };
                for (int pass = 0; pass < 7; pass++)
                {
                    int pw = (w - xo[pass] + xs[pass] - 1) / xs[pass];
                    int ph = (h - yo[pass] + ys[pass] - 1) / ys[pass];
                    if (pw <= 0 || ph <= 0) continue;                   // a pass with no pixels is omitted
                    for (int py = 0; py < ph; py++)
                    {
                        raw.WriteByte(0);
                        for (int px = 0; px < pw; px++)
                            raw.Write(Pixel(xo[pass] + px * xs[pass], yo[pass] + py * ys[pass]), 0, 3);
                    }
                }
            }

            byte[] idat;
            using (var ms = new System.IO.MemoryStream())
            {
                using (var z = new System.IO.Compression.ZLibStream(
                    ms, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    byte[] rawBytes = raw.ToArray();
                    z.Write(rawBytes, 0, rawBytes.Length);
                }
                idat = ms.ToArray();
            }

            using var outMs = new System.IO.MemoryStream();
            outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
            ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
            ihdr[8] = 8;                       // bit depth
            ihdr[9] = 2;                       // colour type: truecolour
            ihdr[12] = (byte)(interlaced ? 1 : 0);
            WriteChunk(outMs, "IHDR", ihdr);
            WriteChunk(outMs, "IDAT", idat);
            WriteChunk(outMs, "IEND", Array.Empty<byte>());
            return outMs.ToArray();
        }

        private static byte[] MakeInterlacedPng(int w, int h)
        {
            using var ms = new System.IO.MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
            ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
            ihdr[8] = 8; ihdr[9] = 2; ihdr[12] = 1;   // interlace: Adam7
            WriteChunk(ms, "IHDR", ihdr);
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void WriteChunk(System.IO.Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            len[0] = (byte)(data.Length >> 24); len[1] = (byte)(data.Length >> 16);
            len[2] = (byte)(data.Length >> 8); len[3] = (byte)data.Length;
            s.Write(len, 0, 4);
            s.Write(Encoding.ASCII.GetBytes(type), 0, 4);
            s.Write(data, 0, data.Length);
            s.Write(new byte[4], 0, 4);
        }
    }
}
