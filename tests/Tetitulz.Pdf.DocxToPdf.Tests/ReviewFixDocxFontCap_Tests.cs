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
    /// Review finding #5: <c>DocxRenderContext.TryLoadEmbeddedFont</c> used an uncapped
    /// <c>Stream.CopyTo</c> to read an embedded-font part, unlike the image paths which cap
    /// the decompressed size. A DOCX is a ZIP, so a tiny deflated font part can expand to
    /// gigabytes (zip bomb) and exhaust memory. The font part is now read with the same
    /// streaming-with-cap loop (64 MB) and skipped when it exceeds the cap.
    /// </summary>
    public class ReviewFixDocxFontCap_Tests
    {
        private const string Key = "{52F7CD7B-1A3C-4E2F-9B8A-0123456789AB}";

        [Fact]
        public void OversizedEmbeddedFont_IsSkipped_NoOutOfMemory()
        {
            // 96 MB of zeros: trivially compressible inside the DOCX ZIP, but expands well past
            // the 64 MB font cap on read. Pre-fix this CopyTo'd the whole blob into memory.
            const int decompressedSize = 96 * 1024 * 1024;
            byte[] docx = BuildDocxWithEmbeddedFont("BombFamily", Key, decompressedSize);

            using var ms = new MemoryStream(docx);
            using var wd = WordprocessingDocument.Open(ms, false);

            // Constructing the context loads embedded fonts; the cap must make this a no-op
            // (skip) for the oversized part rather than buffering ~96 MB / throwing OOM.
            var ctx = new DocxRenderContext(wd, new DocxToPdfOptions());

            // The oversized font was skipped, so it does not resolve.
            Assert.Null(ctx.ResolveEmbeddedFont("BombFamily", false, false, Array.Empty<int>()));
        }

        [Fact]
        public void SmallButGarbageEmbeddedFont_IsSkippedGracefully()
        {
            // A small part under the cap that is not a valid font: read fully, parse fails,
            // skipped silently (cap path is not the only guard).
            byte[] docx = BuildDocxWithEmbeddedFont("JunkFamily", Key, 1024);

            using var ms = new MemoryStream(docx);
            using var wd = WordprocessingDocument.Open(ms, false);
            var ctx = new DocxRenderContext(wd, new DocxToPdfOptions());

            Assert.Null(ctx.ResolveEmbeddedFont("JunkFamily", false, false, Array.Empty<int>()));
        }

        // Builds a DOCX whose font table embeds a single regular font part of the requested
        // DECOMPRESSED size (filled with zeros so it compresses to almost nothing in the ZIP).
        private static byte[] BuildDocxWithEmbeddedFont(string family, string fontKey, int decompressedSize)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("hi")))));
                main.Document.Save();

                var ftPart = main.AddNewPart<FontTablePart>();
                var fontPart = ftPart.AddNewPart<FontPart>(
                    "application/vnd.openxmlformats-officedocument.obfuscatedFont", "rIdBomb1");

                // Stream zeros into the part without allocating the whole blob up front.
                using (var partStream = fontPart.GetStream(FileMode.Create, FileAccess.Write))
                {
                    var chunk = new byte[1024 * 1024];
                    int remaining = decompressedSize;
                    while (remaining > 0)
                    {
                        int n = Math.Min(chunk.Length, remaining);
                        partStream.Write(chunk, 0, n);
                        remaining -= n;
                    }
                }
                string rId = ftPart.GetIdOfPart(fontPart);

                ftPart.Fonts = new Fonts(
                    new Font(new EmbedRegularFont { Id = rId, FontKey = fontKey }) { Name = family });
                ftPart.Fonts.Save();
            }
            return ms.ToArray();
        }
    }
}
