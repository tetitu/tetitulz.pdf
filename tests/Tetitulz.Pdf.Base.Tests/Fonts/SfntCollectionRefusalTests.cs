// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// A TrueType Collection is refused by name at every public font entry point.
/// </summary>
/// <remarks>
/// <para>A <c>ttcf</c> header puts <c>majorVersion</c> (1) exactly where an sfnt keeps
/// <c>numTables</c>, and the first face's u32 offset where the first directory record's tag goes.
/// A reader with no signature test therefore does not fault — it finds one table with a nonsense
/// name at a nonsense offset, locates no <c>head</c>, <c>hhea</c>, <c>maxp</c> or <c>hmtx</c>, and
/// returns every metric at its default. Measured before the fix:
/// <c>upm=1000 asc=0 desc=0 bbox=0,0,0,0</c>, every advance 500, and <c>EmbedFont</c> writing
/// <c>/Ascent 0 /Descent 0 /FontBBox [0 0 0 0]</c> over a <c>/FontFile2</c> holding the whole
/// collection — which §9.9 defines as a single TrueType font program. Valid-looking PDF, unusable
/// font, no exception and no diagnostic.</para>
///
/// <para>It is reachable rather than theoretical: <c>Core.Fonts.FontFileLocator</c> — this
/// library's own font search — returns <c>*.ttc</c> alongside <c>*.ttf</c>, and a collection is the
/// normal shipping form for CJK faces on Windows and macOS.</para>
///
/// <para><b>Each case carries the single face as a control</b>, so a refusal cannot be passing
/// because the fixture is broken.</para>
/// </remarks>
public class SfntCollectionRefusalTests
{
    private static byte[] Face() => MinimalTrueType.Build("Hello");
    private static byte[] Collection() => MinimalTrueType.BuildCollection(Face());

    [Fact]
    public void MetricsOfACollectionAreRefusedRatherThanDefaulted()
    {
        var faceMetrics = PdfOperations.GetFontMetrics(Face());
        Assert.True(faceMetrics.UnitsPerEm > 0);
        Assert.True(faceMetrics.Ascent > 0);   // the value the collection used to return as 0

        var ex = Assert.Throws<NotSupportedException>(() => PdfOperations.GetFontMetrics(Collection()));
        Assert.Contains("ttcf", ex.Message);
    }

    [Fact]
    public void EmbeddingACollectionIsRefusedRatherThanWritingAZeroedDescriptor()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            PdfOperations.EmbedFont(input, output, Face(), "F9");   // a name the page does not already use
            string written = Encoding.Latin1.GetString(output.ToArray());
            Assert.Contains("/FontFile2", written);
            Assert.DoesNotContain("/FontBBox [0 0 0 0]", written);
        }

        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            var ex = Assert.Throws<NotSupportedException>(
                () => PdfOperations.EmbedFont(input, output, Collection(), "F9"));
            Assert.Contains("ttcf", ex.Message);
        }
    }

    /// <summary>
    /// The subsetter refused a collection already, but with the wrong reason: "Font does not
    /// contain a 'glyf' table", which a collection plainly does. That sent the caller to the CFF
    /// path for a TrueType font.
    /// </summary>
    [Fact]
    public void SubsettingACollectionNamesTheCollectionRatherThanABsentGlyfTable()
    {
        Assert.NotNull(PdfOperations.SubsetFont(Face(), "Hello"));

        var ex = Assert.Throws<NotSupportedException>(
            () => PdfOperations.SubsetFont(Collection(), "Hello"));
        Assert.Contains("ttcf", ex.Message);
        Assert.DoesNotContain("glyf", ex.Message);
    }

    /// <summary>Table compaction refuses one too, through the shared directory reader.</summary>
    [Fact]
    public void CompactingACollectionIsRefused()
    {
        Assert.True(PdfFontSubsetter.TryCompact(
            MinimalTrueType.BuildWithDroppableTables("Hello", 512), out byte[] compacted, out _));
        Assert.NotNull(compacted);

        Assert.False(PdfFontSubsetter.TryCompact(Collection(), out _, out string reason));
        Assert.Contains("ttcf", reason);
    }
}
