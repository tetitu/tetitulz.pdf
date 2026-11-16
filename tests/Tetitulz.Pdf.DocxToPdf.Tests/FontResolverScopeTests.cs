// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A host-font scope clears the file cache and its charge together, under the cache's own lock. It cleared
/// the cache under another lock than the loader's, and left the charge behind, so a few scopes spent the
/// 32 MiB budget on entries no longer held and nothing more was cached. In the no-parallel collection: the
/// charge is process-wide.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class FontResolverScopeTests
{
    [Fact]
    public void AScope_ClearsTheFileCachesCharge_OnTheWayInAndOut()
    {
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-scope-" + Guid.NewGuid().ToString("N") + ".ttf");
        try
        {
            File.WriteAllBytes(path, Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("AB"));
            FontResolver.LoadFontFileForTest(path);
            Assert.True(FontResolver.ChargedBytes > 0, "the fixture's font was not charged, so this proves nothing");

            using (FontResolver.UseHostFontFilesForTest(Array.Empty<string>()))
            {
                Assert.Equal(0, FontResolver.ChargedBytes);
                var program = FontResolver.LoadFontFileForTest(path);
                Assert.Equal(program!.FontData.LongLength, FontResolver.ChargedBytes);
            }

            Assert.Equal(0, FontResolver.ChargedBytes);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
