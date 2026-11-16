// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Tests for the pure-managed HTML→PDF engine (RenderEngine = Managed): HTML is parsed
    /// and mapped to the Builder model and rendered without a browser. These run anywhere
    /// (no Playwright install needed).
    /// </summary>
    public class HtmlManagedEngineTests
    {
        private static HtmlToPdfOptions Managed() => new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed };

        private static int CountPages(byte[] pdf)
        {
            string raw = Encoding.Latin1.GetString(pdf);
            int n = 0, i = 0;
            while ((i = raw.IndexOf("/MediaBox", i, StringComparison.Ordinal)) >= 0) { n++; i += 9; }
            return n;
        }

        /// <summary>
        /// Arabic must survive HTML→PDF as <b>base letters</b>, shaped on the page but recorded as
        /// the original code points in <c>/ToUnicode</c>.
        /// </summary>
        /// <remarks>
        /// The managed engine maps HTML onto the Builder model and calls <c>ToPdf()</c>, so it
        /// inherits <c>Builder.Export.RtlText</c>'s shaping — the fix for recording presentation
        /// forms instead of base letters landed there. This test exists because "it routes through
        /// Builder, so it is covered" is a claim about code structure, and the mapper sits between:
        /// it decodes entities, collapses whitespace and splits runs, any of which could reach the
        /// exporter with different text. Asserting the extracted string checks the claim end to end
        /// rather than trusting the wiring.
        /// </remarks>
        [Fact]
        public void Managed_ArabicText_ExtractsAsBaseLetters()
        {
            const string arabic = "بيت";
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                $"<html><body><p>{arabic}</p></body></html>", Managed());

            using var ms = new MemoryStream(pdf);
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
            string text = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);

            Assert.Contains(arabic, text);
            // No U+FE70–FEFF presentation forms: those are what was drawn, not what was written.
            Assert.DoesNotContain(text, c => c >= 'ﹰ' && c <= '﻿');
        }

        [Fact]
        public void Managed_SimpleHtml_ProducesValidPdf()
        {
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<html><body><h1>Title</h1><p>Hello <b>world</b>.</p></body></html>", Managed());
            Assert.True(pdf.Length > 200);
            Assert.Equal((byte)'%', pdf[0]);
            Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(pdf, 0, 8));
        }

        [Fact]
        public void Managed_Table_Renders()
        {
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<table><tr><th>Item</th><th>Price</th></tr><tr><td>Widget</td><td>$10</td></tr></table>",
                Managed());
            Assert.True(pdf.Length > 200);
            Assert.Equal((byte)'%', pdf[0]);
        }

        [Fact]
        public void Managed_List_Renders()
        {
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<ul><li>First</li><li>Second</li></ul><ol><li>One</li><li>Two</li></ol>", Managed());
            Assert.True(pdf.Length > 200);
        }

        [Fact]
        public void Managed_LongHtml_Paginates()
        {
            var sb = new StringBuilder("<body>");
            for (int i = 0; i < 150; i++) sb.Append($"<p>Paragraph {i} with content.</p>");
            sb.Append("</body>");
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(sb.ToString(), Managed());
            Assert.True(CountPages(pdf) >= 2, $"expected pagination, got {CountPages(pdf)}");
        }

        /// <summary>
        /// Hebrew is drawn with an embedded composite font and its glyphs really are the Hebrew
        /// ones.
        ///
        /// <para><b>This used to open with <c>if (!HasHebrewFont()) return;</c></b> — banned
        /// pattern 2. It reported <i>Passed</i> while executing nothing on any host without one
        /// of four hard-coded font files, and it slipped past the build guard only because the
        /// <c>File.Exists</c> was one level down inside the helper rather than in the
        /// <c>if</c> itself. The font is now injected, so the test always runs and asserts the
        /// same thing everywhere.</para>
        ///
        /// <para>The assertion also moved past "the string Type0 appears in the bytes", which a
        /// document containing any composite font satisfies whether or not the Hebrew reached it.
        /// The glyph ids drawn are compared against the injected font's own cmap.</para>
        /// </summary>
        [Fact]
        public void Managed_Rtl_Hebrew_EmbedsFontAndDrawsHebrewGlyphs()
        {
            const string hebrew = "שלום עולם";
            byte[] program = Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build(hebrew);

            var options = Managed();
            options.AllowSystemFontSubstitution = false;
            options.FontProvider = _ => program;

            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes($"<p dir=\"rtl\">{hebrew}</p>", options);

            string raw = Encoding.Latin1.GetString(pdf);
            Assert.Contains("/Type0", raw);
            Assert.Contains("/Identity-H", raw);

            // Every glyph drawn maps back to a Hebrew code point in the injected font.
            var cmap = Tetitulz.Pdf.TestHelpers.MinimalTrueType.ReadCmap(program);
            var gidToCp = cmap.ToDictionary(kv => kv.Value, kv => kv.Key);
            var drawn = ManagedTestSupport.DrawnGlyphRuns(ManagedTestSupport.DecodeStreams(pdf));

            Assert.NotEmpty(drawn);
            foreach (var run in drawn)
                foreach (ushort gid in run.Gids)
                {
                    Assert.True(gidToCp.TryGetValue(gid, out int cp), $"glyph {gid} is not from the injected font");
                    Assert.InRange(cp, 0x0590, 0x05FF);
                }
        }

        [Fact]
        public void Managed_DataUriImage_EmbedsImage()
        {
            string dataUri = "data:image/png;base64," + System.Convert.ToBase64String(MakeRgbPng(4, 4));
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                $"<p>Logo:</p><img src=\"{dataUri}\" width=\"40\" height=\"40\">", Managed());
            Assert.Contains("/Image", Encoding.Latin1.GetString(pdf));
        }

        [Fact]
        public void Managed_RemoteImage_IsSkippedNoNetwork()
        {
            // The managed engine does no I/O; a remote <img> is silently skipped.
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
                "<p>Text</p><img src=\"https://example.com/a.png\">", Managed());
            Assert.Equal((byte)'%', pdf[0]);
            Assert.DoesNotContain("/Image", Encoding.Latin1.GetString(pdf));
        }

        [Fact]
        public void Managed_NullHtml_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                HtmlToPdfConverter.ConvertHtmlToBytes(null!, Managed()));
        }

        // ---- minimal RGB PNG synthesis (for data-URI image test) ----

        private static byte[] MakeRgbPng(int w, int h)
        {
            byte[] raw = new byte[h * (1 + w * 3)];
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < w; x++) { raw[p++] = 200; raw[p++] = 50; raw[p++] = 50; }
            }
            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                    z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }
            using var outMs = new MemoryStream();
            outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 2;
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
            foreach (byte b in data) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1); }
            return c ^ 0xFFFFFFFF;
        }
    }
}
