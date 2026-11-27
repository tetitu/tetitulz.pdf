// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A host font file is one program for a whole conversion, so the document embeds it once
/// (<c>DocxRenderContext.ResolveFont</c>, <see cref="FontResolver.Resolve"/>'s
/// <c>conversionPrograms</c>).
/// </summary>
/// <remarks>
/// <para>The context embeds a font once per program <b>instance</b>, and the process-wide file
/// cache does not keep instances stable: a face larger than <see cref="FontResolver.MaxCachedFontBytes"/>,
/// or any face once the cache is full, is read again at every resolution, and a clear (a host-font
/// scope, a test) drops what it held. Embedded by instance, each re-read would be another copy of the
/// face in the same PDF.</para>
/// <para>In the non-parallel collection: the host-font scope clears the resolver's static caches.</para>
/// </remarks>
[Collection(DiagnosticsCollection.Name)]
public sealed class HostFontOneProgramTests : IDisposable
{
    private const string Hebrew = "שלום עולם";
    private readonly List<string> _written = new List<string>();

    public void Dispose()
    {
        foreach (string path in _written)
            try { File.Delete(path); } catch (IOException) { /* a leftover temp file is not a failure */ }
    }

    private string HostFont(byte[] program)
    {
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-oneprogram-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, program);
        _written.Add(path);
        return path;
    }

    [Fact]
    public void AFaceTheFileCacheCannotHold_IsEmbeddedOnceAcrossThreePages()
    {
        // A padding table takes the face past the cache's ceiling, so the cache never holds it.
        string path = HostFont(MinimalTrueType.BuildWithTables("שלוםע",
            new Dictionary<string, byte[]> { ["zpad"] = new byte[FontResolver.MaxCachedFontBytes + 1] }));

        using (FontResolver.UseHostFontFilesForTest(new[] { path }))
        {
            // Potency: every read of this file is a new program.
            Assert.NotSame(FontResolver.LoadFontFileForTest(path), FontResolver.LoadFontFileForTest(path));

            string pdf = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                HebrewOnThreePages(), new DocxToPdfOptions { CompressStreams = false }));

            Assert.Equal(3, Regex.Matches(pdf, @"/Type\s*/Page\b").Count);
            Assert.Equal(1, Regex.Matches(pdf, "/FontFile2").Count);
        }
    }

    [Fact]
    public void WithinAConversion_AFileIsOneProgram_WhateverTheFileCacheDrops()
    {
        string path = HostFont(MinimalTrueType.Build("אב"));
        var conversion = new Dictionary<string, TrueTypeFontProgram>(StringComparer.OrdinalIgnoreCase);

        using (FontResolver.UseHostFontFilesForTest(new[] { path }))
        {
            var first = FontResolver.Resolve("Zzz No Such Family", false, false, new[] { 0x05D0 }, false, conversion);
            Assert.NotNull(first);

            // A request the resolution cache has not seen, answered by the same file after the file
            // cache dropped it: read again, and answered with the conversion's program.
            FontResolver.ClearFileCacheForTest();
            Assert.Same(first, FontResolver.Resolve("Another Missing Family", true, false, new[] { 0x05D1 }, false, conversion));

            // The control: outside a conversion the same clear gives a new program.
            FontResolver.ClearFileCacheForTest();
            Assert.NotSame(first, FontResolver.Resolve("Zzz No Such Family", false, false, new[] { 0x05D0 }));

            // A request the resolution cache remembers is answered without reading the file again.
            FontResolver.ClearFileCacheForTest();
            File.Delete(path);
            Assert.Same(first, FontResolver.Resolve("Zzz No Such Family", false, false, new[] { 0x05D0 }, false, conversion));
        }
    }

    private static byte[] HebrewOnThreePages()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                body.Append(new Paragraph(new ParagraphProperties(new BiDi()),
                    new Run(new RunProperties(new RightToLeftText()),
                        new Text(Hebrew) { Space = SpaceProcessingModeValues.Preserve })));
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }
}
