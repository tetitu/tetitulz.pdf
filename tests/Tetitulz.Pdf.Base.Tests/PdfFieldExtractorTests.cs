// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>P3a — data-driven label-to-value field extraction (Hebrew + Latin, same-line and below).</summary>
public class PdfFieldExtractorTests
{
    private const int Alef = 0x05D0, Bet = 0x05D1;

    private static byte[] InvoicePdf()
        => new InvoicePdfBuilder()
            // LTR field: "ID" then value "777" to the right.
            .AsciiRun("ID", x: 50, y: 700)
            .AsciiRun("777", x: 90, y: 700)
            // RTL field: Hebrew label on the right, value "12345" to the left.
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet), xRight: 250, y: 650)
            .AsciiRun("12345", x: 150, y: 650)
            // Label-above / value-below field.
            .AsciiRun("TOTAL", x: 50, y: 600)
            .AsciiRun("999", x: 50, y: 585)
            .Build();

    private static IReadOnlyList<FieldResult> Run(IReadOnlyList<FieldQuery> queries)
    {
        using var ms = new MemoryStream(InvoicePdf());
        return PdfFieldExtractor.Extract(ms, queries);
    }

    [Fact]
    public void Extract_LtrField_SameLineValue()
    {
        var r = Run(new[] { new FieldQuery("id", "ID") }).Single();
        Assert.Equal("777", r.Value);
        Assert.Equal(1f, r.Confidence);
    }

    [Fact]
    public void Extract_HebrewRtlField_SameLineValue()
    {
        var r = Run(new[] { new FieldQuery("num", InvoicePdfBuilder.Hb(Alef, Bet)) }).Single();
        Assert.Equal("12345", r.Value);
    }

    [Fact]
    public void Extract_LabelAbove_ValueBelow()
    {
        var r = Run(new[] { new FieldQuery("total", "TOTAL") { Side = FieldValueSide.Below } }).Single();
        Assert.Equal("999", r.Value);
        Assert.True(r.Confidence > 0f && r.Confidence < 1f);
    }

    [Fact]
    public void Extract_MissingLabel_EmptyValueZeroConfidence()
    {
        var r = Run(new[] { new FieldQuery("missing", "NOPE") }).Single();
        Assert.Equal("", r.Value);
        Assert.Equal(0f, r.Confidence);
        Assert.Equal(-1, r.PageIndex);
    }

    [Fact]
    public void Extract_AllQueries_ReturnsOnePerQuery()
    {
        var results = Run(new[]
        {
            new FieldQuery("id", "ID"),
            new FieldQuery("num", InvoicePdfBuilder.Hb(Alef, Bet)),
            new FieldQuery("missing", "NOPE"),
        });
        Assert.Equal(3, results.Count);
    }
}
