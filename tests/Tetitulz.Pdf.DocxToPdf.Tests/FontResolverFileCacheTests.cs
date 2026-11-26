// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// <see cref="FontResolver"/>'s file cache is live and charges what it holds, exactly, and a full
/// one still leaves a document one program, and one embedded font, per file. Removing the
/// cache write or the charge, or adding a byte to the charge, is invisible to an answer-only test: a
/// cache whose hits and misses return equal programs is invisible to every
/// assertion about the answer (CLAUDE.md's second blind spot), so the evidence asserted here is
/// identity and the byte count.
/// </summary>
/// <remarks>
/// In the non-parallel collection: the cache and its charge are process-wide, and another test
/// loading fonts at the same moment would move the count under this one.
/// </remarks>
[Collection(DiagnosticsCollection.Name)]
public class FontResolverFileCacheTests
{
    [Fact]
    public void ASecondLoadOfAFileIsTheCachedProgramAndTheFirstIsChargedItsSize()
    {
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-cachetest-" + Guid.NewGuid().ToString("N") + ".otf");
        File.WriteAllBytes(path, MinimalOpenTypeCff.Build("AB"));
        try
        {
            long before = FontResolver.ChargedBytes;
            var first = FontResolver.LoadFontFileForTest(path);
            long added = FontResolver.ChargedBytes - before;

            Assert.NotNull(first);
            Assert.Same(first, FontResolver.LoadFontFileForTest(path));

            // A cache already at its ceiling stops accepting entries; the charge is then 0 and that
            // is the correct answer, not a failure.
            long size = first!.FontData.LongLength;
            long expected = before + size <= FontResolver.MaxCachedFontBytes ? size : 0;
            Assert.Equal(expected, added);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A document converted while the file cache is full embeds its host face once. Past the
    /// ceiling every load reads the file again as a new program, and the document's embedded fonts
    /// are deduplicated by program, so each of the twelve runs would embed its own copy; the
    /// document keeps the program it took for the file (<c>DocxRenderContext.ResolveHostFont</c>).
    /// </summary>
    [Fact]
    public void AFullFileCache_StillEmbedsTheHostFaceOnce()
    {
        const string word = "שלום";
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-fullcache-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, MinimalTrueType.Build(word));
        try
        {
            string pdf;
            long charged;
            using (FontResolver.UseHostFontFilesForTest(new[] { path }))
            using (FontResolver.FullFileCacheForTest())
            {
                pdf = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                    Paragraphs(word, 12), new DocxToPdfOptions { CompressStreams = false }));
                charged = FontResolver.ChargedBytes;
            }

            Assert.Single(Regex.Matches(pdf, "/FontFile2"));
            // The cache refused every program throughout, so each run's resolution read the file anew.
            Assert.Equal(FontResolver.MaxCachedFontBytes, charged);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static byte[] Paragraphs(string text, int count)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            for (int i = 0; i < count; i++)
                body.Append(new Paragraph(new Run(new Text(text))));
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }
}
