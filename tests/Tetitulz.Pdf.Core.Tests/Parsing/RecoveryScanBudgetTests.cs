// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The header scan that rebuilds a cross-reference table is charged against the parser's entry
/// budget like every parsed section and every recovered object-stream member (the review of
/// 2026-10-16, R1.10). Uncharged, a file of bare headers rebuilt a table of one entry per eight
/// bytes over the whole file — the one path the budget did not bound.
/// </summary>
public sealed class RecoveryScanBudgetTests
{
    [Fact]
    public void Adversarial_bare_headers_past_the_entry_budget_stop_the_scan_and_say_so()
    {
        var parser = new PdfParser(new MemoryStream(BareHeaders(100))) { MaxCrossReferenceEntries = 50 };
        parser.ParseHeader();
        parser.ParseCrossReferences();

        Assert.Equal(50, parser.CrossReferenceTable.Count);
        Assert.NotNull(parser.CrossReferenceTable.GetEntry(50));
        Assert.Null(parser.CrossReferenceTable.GetEntry(51));
        Assert.Contains("recovery stopped at the 50-entry cross-reference limit", PdfParser.LastXrefSectionError);
    }

    [Fact]
    public void Bare_headers_within_the_entry_budget_are_all_recovered()
    {
        var parser = new PdfParser(new MemoryStream(BareHeaders(40))) { MaxCrossReferenceEntries = 50 };
        parser.ParseHeader();
        parser.ParseCrossReferences();

        Assert.Equal(40, parser.CrossReferenceTable.Count);
        Assert.NotNull(parser.CrossReferenceTable.GetEntry(40));
        Assert.Null(PdfParser.LastXrefSectionError);
    }

    /// <summary>
    /// A file of <paramref name="count"/> empty objects and no cross-reference section at all, so the
    /// parser rebuilds its table by scanning.
    /// </summary>
    private static byte[] BareHeaders(int count)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        for (int i = 1; i <= count; i++) sb.Append(i).Append(" 0 obj\n<< >>\nendobj\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
