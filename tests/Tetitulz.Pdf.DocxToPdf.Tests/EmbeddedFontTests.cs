// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// End-to-end loading of fonts embedded inside a DOCX: word/fontTable.xml declares a font
    /// whose <c>embedRegular</c> points at an obfuscated font part; the renderer de-obfuscates,
    /// parses, and resolves it by family so a run can use the author's embedded font.
    /// </summary>
    public class EmbeddedFontTests
    {
        private const string Key = "{52F7CD7B-1A3C-4E2F-9B8A-0123456789AB}";

        [Fact]
        public void EmbeddedObfuscatedFont_IsLoadedAndResolvedByFamily()
        {
            byte[] fontBytes = FindSystemTtf();
            if (fontBytes == null) return; // needs a system TTF for the fixture (as the RTL tests do)

            // XOR is involutive — Deobfuscate is also the obfuscator for building the fixture.
            byte[] obfuscated = ObfuscatedFontReader.Deobfuscate(fontBytes, Key);

            byte[] docx = BuildDocxWithEmbeddedFont("EmbeddedTestFamily", Key, obfuscated);

            using var ms = new MemoryStream(docx);
            using var wd = WordprocessingDocument.Open(ms, false);
            var ctx = new DocxRenderContext(wd, new DocxToPdfOptions());

            // Resolved + de-obfuscated + parsed back into a usable program.
            Assert.NotNull(ctx.ResolveEmbeddedFont("EmbeddedTestFamily", false, false, Array.Empty<int>()));
            // A family the document doesn't embed yields nothing (falls back to OS/standard-14).
            Assert.Null(ctx.ResolveEmbeddedFont("FamilyNotEmbedded", false, false, Array.Empty<int>()));
        }

        [Fact]
        public void NoFontTable_ResolveEmbeddedFont_ReturnsNull()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("hi")))));
                main.Document.Save();
            }
            ms.Position = 0;

            using var wd = WordprocessingDocument.Open(ms, false);
            var ctx = new DocxRenderContext(wd, new DocxToPdfOptions());
            Assert.Null(ctx.ResolveEmbeddedFont("Anything", false, false, Array.Empty<int>()));
        }

        private static byte[] BuildDocxWithEmbeddedFont(string family, string fontKey, byte[] fontData)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("hi")))));
                main.Document.Save();

                var ftPart = main.AddNewPart<FontTablePart>();
                var fontPart = ftPart.AddNewPart<FontPart>(
                    "application/vnd.openxmlformats-officedocument.obfuscatedFont", "rIdEmb1");
                using (var fs = new MemoryStream(fontData)) fontPart.FeedData(fs);
                string rId = ftPart.GetIdOfPart(fontPart);

                ftPart.Fonts = new Fonts(
                    new Font(new EmbedRegularFont { Id = rId, FontKey = fontKey }) { Name = family });
                ftPart.Fonts.Save();
            }
            return ms.ToArray();
        }

        // Finds a standard (sfnt 1.0) TrueType file in the OS font directory for use as a fixture.
        private static byte[] FindSystemTtf()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            foreach (var path in Directory.EnumerateFiles(dir, "*.ttf"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length > 4 && bytes[0] == 0x00 && bytes[1] == 0x01
                        && bytes[2] == 0x00 && bytes[3] == 0x00)
                        return bytes;
                }
                catch { /* unreadable font — try the next */ }
            }
            return null;
        }
    }
}
