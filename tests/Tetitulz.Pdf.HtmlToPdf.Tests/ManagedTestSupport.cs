// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Managed-engine test support: convert, and reach the shared PDF probes.
    ///
    /// <para>The content-stream decoding and geometry readers live in
    /// <see cref="PdfContentProbe"/> (TestHelpers) because the Builder suite needs exactly the
    /// same ones; this type only adds what is specific to converting HTML. Two copies of a
    /// decoder are how two suites come to disagree about what the output contains.</para>
    /// </summary>
    internal static class ManagedTestSupport
    {
        internal static HtmlToPdfOptions Managed() =>
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed };

        // Named ToPdf rather than Pdf: inside namespace Tetitulz.Pdf.HtmlToPdf.Tests the
        // identifier `Pdf` binds to the Tetitulz.Pdf NAMESPACE, which wins over a
        // `using static` member and makes every call site a CS0118.
        internal static byte[] ToPdf(string html) =>
            HtmlToPdfConverter.ConvertHtmlToBytes(html, Managed());

        internal static byte[] ToPdf(string html, HtmlToPdfOptions options) =>
            HtmlToPdfConverter.ConvertHtmlToBytes(html, options);

        /// <summary>
        /// A table whose width nothing can know: one more table nested in each than
        /// <c>PdfLimits.MaxTableNestingDepth</c> lets the layout draw or measure, so the innermost —
        /// holding <paramref name="innermost"/> — is past the cap, and every column around it holds
        /// only a table of unknown width. (A nested table within the cap is measured, so it cannot be the
        /// fixture for "unknown".)
        /// </summary>
        internal static string TablesPastTheNestingCap(string innermost)
        {
            int n = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTableNestingDepth + 1;
            var html = new StringBuilder();
            for (int i = 0; i < n; i++) html.Append("<table><tr><td>");
            html.Append(innermost);
            for (int i = 0; i < n; i++) html.Append("</td></tr></table>");
            return html.ToString();
        }

        // ---- thin re-exports of the shared probes, so call sites stay readable ----

        internal static string Raw(byte[] pdf) => PdfContentProbe.Raw(pdf);
        internal static string NormalizedRaw(byte[] pdf) => PdfContentProbe.NormalizedRaw(pdf);
        // A kerned standard-14 word's TJ joined back into its Tj (HTML kerns by default, as a browser does).
    internal static string DecodeStreams(byte[] pdf) => PdfContentProbe.JoinKernedStrings(PdfContentProbe.DecodeStreams(pdf));
        internal static int CountPages(byte[] pdf) => PdfContentProbe.CountPages(pdf);

        internal static List<PdfContentProbe.DrawnText> DrawnTexts(string content) =>
            PdfContentProbe.DrawnTexts(content);
        internal static List<(double X, double Y, ushort[] Gids)> DrawnGlyphRuns(string content) =>
            PdfContentProbe.DrawnGlyphRuns(content);
        internal static List<PdfContentProbe.Segment> StrokedSegments(string content) =>
            PdfContentProbe.StrokedSegments(content);
        internal static double XOf(string content, string text) => PdfContentProbe.XOf(content, text);
        internal static double YOf(string content, string text) => PdfContentProbe.YOf(content, text);

        // ------------------------------------------------------------ tiny raster synthesis

        /// <summary>A w×h 8-bit RGB PNG filled with one colour, for image-path tests.</summary>
        internal static byte[] MakeRgbPng(int w, int h, byte r = 200, byte g = 50, byte b = 50)
        {
            byte[] raw = new byte[h * (1 + w * 3)];
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                raw[p++] = 0; // filter: None
                for (int x = 0; x < w; x++) { raw[p++] = r; raw[p++] = g; raw[p++] = b; }
            }
            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
                    z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }
            using var outMs = new MemoryStream();
            outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // colour type: truecolour
            WriteChunk(outMs, "IHDR", ihdr);
            WriteChunk(outMs, "IDAT", idat);
            WriteChunk(outMs, "IEND", Array.Empty<byte>());
            return outMs.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
            byte[] t = Encoding.ASCII.GetBytes(type);
            var crcIn = new byte[4 + data.Length];
            Array.Copy(t, 0, crcIn, 0, 4); Array.Copy(data, 0, crcIn, 4, data.Length);
            s.Write(t, 0, 4); s.Write(data, 0, data.Length);
            var crcb = new byte[4]; WriteBE(crcb, 0, (int)Crc32(crcIn)); s.Write(crcb, 0, 4);
        }

        private static void WriteBE(byte[] b, int o, int v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
            }
            return c ^ 0xFFFFFFFF;
        }

        // ------------------------------------------------------------ scratch directories

        /// <summary>A fresh empty directory under the system temp root; delete via <see cref="ScratchDir.Dispose"/>.</summary>
        internal static ScratchDir NewScratchDir() => new ScratchDir();

        internal sealed class ScratchDir : IDisposable
        {
            public ScratchDir()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "tetitulz-html-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public string Write(string name, byte[] bytes)
            {
                string p = System.IO.Path.Combine(Path, name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, bytes);
                return p;
            }

            public string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
