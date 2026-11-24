// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// <see cref="FontResolver"/>'s file cache is live and charges what it holds, exactly. Removing the
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
}
