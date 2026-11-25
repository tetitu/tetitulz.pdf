// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// What PdfRepair's header scan is bounded by, and that reaching a bound is said.
/// </summary>
/// <remarks>
/// A scan that stopped accumulating once 10 000 distinct objects had been recorded, saying nothing,
/// would bring a document with more objects out of repair missing the rest, reported clean. The
/// table is bounded by the object-number range instead, and the list of headers by a count that is
/// reported when a file reaches it.
/// </remarks>
public class RepairBoundsTests
{
    [Fact]
    public void Repair_MoreObjectsThanTheOldSilentCap_AreAllRecovered()
    {
        byte[] pdf = BuildObjects(20_000);
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var result = PdfRepair.Repair(input, output);

        Assert.Equal(20_001, result.RecoveredObjectCount);
        Assert.True(result.IsClean, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Repair_OversizedHeaderCount_ReportsWhereRecoveryStopped()
    {
        // A million and one bare headers: eight bytes each, so the list they would fill is the
        // largest thing in the process long before the file is.
        var sb = new StringBuilder("%PDF-1.4\n");
        for (int i = 0; i <= 1_000_000; i++) sb.Append("1 0 obj ");
        using var input  = new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()));
        using var output = new MemoryStream();

        var result = PdfRepair.Repair(input, output);

        Assert.Contains(result.Errors, e => e.StartsWith("Recovery stopped after 1000000 object headers", StringComparison.Ordinal));
    }

    private static byte[] BuildObjects(int count)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        // One legitimate catalog so the repair has something to anchor on.
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        // Each "N 0 obj\n<< >>\nendobj\n" is ~25 bytes; 20k → ~500 KB.
        for (int n = 100; n < 100 + count; n++)
        {
            sb.Append(n);
            sb.Append(" 0 obj\n<< >>\nendobj\n");
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
