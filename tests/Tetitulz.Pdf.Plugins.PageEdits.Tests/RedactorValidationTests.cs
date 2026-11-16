// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Locks in the input-validation contract added in §40.W4: PdfRedactor
/// rejects negative page indices and null regions instead of silently
/// dropping them on the floor.  Without these guards a caller's typo
/// (e.g. "page 0" interpreted as "page -1") produces a redacted-looking
/// document that wasn't actually redacted.
/// </summary>
public class RedactorValidationTests
{
    [Fact]
    public void Redact_NegativePageIndex_Throws()
    {
        var regions = new List<RedactionRegion>
        {
            new RedactionRegion { PageIndex = -1, X = 0, Y = 0, Width = 50, Height = 50 },
        };

        using var input  = new MemoryStream(MinimalPdfBytes());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfRedactor.Redact(input, output, regions));
        Assert.Equal("redactions", ex.ParamName);
    }

    [Fact]
    public void Redact_NullRegion_Throws()
    {
        var regions = new List<RedactionRegion> { null };

        using var input  = new MemoryStream(MinimalPdfBytes());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() =>
            PdfRedactor.Redact(input, output, regions));
        Assert.Equal("redactions", ex.ParamName);
    }

    [Fact]
    public void Redact_EmptyRegionList_PassesThroughUnchanged()
    {
        // Edge case: empty list should NOT throw; PDF passes through.
        var regions = new List<RedactionRegion>();

        byte[] source = MinimalPdfBytes();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();

        PdfRedactor.Redact(input, output, regions);

        // "Passes through unchanged" is the name's promise: byte for byte. A length check would
        // accept a re-serialised document that dropped or rewrote content it had no reason to
        // touch — and redaction rewriting a file it was told to leave alone is worth catching.
        Assert.Equal(source, output.ToArray());
    }

    private static byte[] MinimalPdfBytes()
    {
        return System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n" +
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n" +
            "xref\n0 5\n0000000000 65535 f \n0000000009 00000 n \n0000000060 00000 n \n0000000113 00000 n \n0000000209 00000 n \n" +
            "trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n259\n%%EOF\n");
    }
}
