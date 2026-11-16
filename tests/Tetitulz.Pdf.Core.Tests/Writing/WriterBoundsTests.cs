// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// Locks in the writer-side bounds checks added in §40.W2 / §40.W5 /
/// §40.W6.  These guard against silent PDF corruption: an xref offset
/// truncated to its 10-digit format, a /Size strictly less than the
/// highest object number written, or a UTF-16 name truncated to a
/// single byte.  Without explicit tests these regressions can sneak
/// back during refactors.
/// </summary>
public class WriterBoundsTests
{
    // ==================================================================
    //  §40.W2 — IncrementalUpdateWriter /Size consistency.
    // ==================================================================

    [Fact]
    public void IncrementalUpdate_SizeNotGreaterThanMaxObj_Throws()
    {
        // We're appending one object with number 5; /Size must be > 5.
        // Pass /Size = 5 to trigger the §40.W2 check.
        var modified = new List<IndirectObject>
        {
            new IndirectObject(5, 0, new PdfNumber(42)),
        };
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(5));

        using var input  = BuildMinimalPdfInput();
        using var output = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IncrementalUpdateWriter.WriteUpdate(
                input, output, modified, trailer,
                previousXrefOffset: 0, size: 5));

        Assert.Contains("must be strictly greater than the highest", ex.Message);
    }

    // ==================================================================
    //  §40.W6 — Name escape via UTF-8 + #XX.
    // ==================================================================

    [Fact]
    public void PdfName_NonAsciiCharacters_HexEscapedViaUtf8()
    {
        // The Euro sign (U+20AC) UTF-8-encodes as 0xE2 0x82 0xAC.  All three
        // bytes are > 0x7E so each must be hex-escaped via #XX.  Without
        // §40.W6 the previous (byte)c truncation would emit a single 0xAC
        // garbage byte instead of the three-byte sequence.
        var name = new PdfName("Eur€");
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteObject(name);
        ms.Flush();

        string emitted = Encoding.ASCII.GetString(ms.ToArray());
        // /Eur followed by exactly three #XX escapes covering bytes 0xE2 0x82 0xAC.
        Assert.StartsWith("/Eur", emitted);
        Assert.Contains("#E2", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#82", emitted);
        Assert.Contains("#AC", emitted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfName_DelimiterCharacters_AlwaysEscaped()
    {
        // Every PDF syntax delimiter must be hex-escaped no matter the
        // surrounding context.
        var name = new PdfName("a/b(c)<d>[e]{f}#g%");
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteObject(name);
        ms.Flush();

        string emitted = Encoding.ASCII.GetString(ms.ToArray());
        // None of the raw delimiters should survive (only the leading /).
        Assert.DoesNotContain("/b", emitted); // would mean '/' didn't escape
        Assert.DoesNotContain("(c)", emitted);
        Assert.DoesNotContain("[e]", emitted);
        Assert.DoesNotContain("{f}", emitted);
    }

    // ==================================================================
    //  §40.W5 — Xref offset overflow guard.
    // ==================================================================

    [Fact]
    public void XrefTable_OffsetOver10Billion_Throws()
    {
        // Build a fake offset map with one entry whose offset exceeds the
        // 10-digit fixed-width limit (9 999 999 999).
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var entries = new List<(int objNum, long offset)>
        {
            (1, 10_000_000_000L), // one byte over the cap
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            writer.WriteXrefTable(entries));
        Assert.Contains("10-digit fixed-width xref format limit", ex.Message);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>
    /// Returns a minimal valid PDF stream (header + minimal trailer)
    /// suitable as the "previous revision" for IncrementalUpdateWriter
    /// path tests that don't actually need to parse it.
    /// </summary>
    private static MemoryStream BuildMinimalPdfInput()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        sb.Append("xref\n0 2\n0000000000 65535 f \n0000000010 00000 n \n");
        sb.Append("trailer\n<< /Size 2 /Root 1 0 R >>\nstartxref\n50\n%%EOF\n");
        return new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()));
    }
}
