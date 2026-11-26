// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// An export keeps one program per host font file when <see cref="BuilderFontResolver"/>'s file
/// cache is full. Past the ceiling every load reads the file again as a new program, and the
/// exporter embeds one font per program (<c>PdfExporter</c>'s <c>EmbeddedByProgram</c>), so two
/// families falling back to one face would embed it twice.
/// </summary>
/// <remarks>
/// In the non-parallel collection: the file cache and its charge are process-wide, and
/// <see cref="BuilderFontResolver.FullFileCacheForTest"/> fills it for every test running at the
/// same moment. Host-independent: the one candidate is a synthetic font named through
/// <c>HostFontFilesForTest</c>.
/// </remarks>
[Collection(PdfLimitsCollection.Name)]
public sealed class FontCacheIdentityTests
{
    [Fact]
    public void AFullFileCache_StillGivesAnExportOneProgramPerFile()
    {
        const string word = "שלום";
        int[] hebrew = { 0x05E9, 0x05DC, 0x05D5, 0x05DD };
        string path = Path.Combine(Path.GetTempPath(),
            "tetitulz-builder-fullcache-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, MinimalTrueType.Build(word));
        try
        {
            using (BuilderFontResolver.FullFileCacheForTest())
            {
                var resolver = new BuilderFontResolver(new PdfExportOptions { AllowSystemFontSubstitution = true })
                {
                    HostFontFilesForTest = new[] { path },
                };

                var first = resolver.Resolve("First Missing Family", false, false, hebrew);
                var second = resolver.Resolve("Second Missing Family", false, false, hebrew);
                long charged = BuilderFontResolver.ChargedBytes;

                Assert.NotNull(first);
                Assert.Same(first, second);
                // The cache refused the program, so the second family's resolution read the file anew.
                Assert.Equal(BuilderFontResolver.MaxCachedFontBytes, charged);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
