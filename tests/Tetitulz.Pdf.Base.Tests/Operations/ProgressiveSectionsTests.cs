// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The fetched ranges of a progressive page read, looked up by object number through an index made as
/// each range is added: an object the page's own run and a shared group both hold is the run's — the
/// first added, as the walk over the sections in order answered — and one neither holds is absent.
/// </summary>
public sealed class ProgressiveSectionsTests
{
    [Fact]
    public void AnObjectTwoSectionsHold_IsTheFirstAdded_AndOneNoneHolds_IsAbsent()
    {
        byte[] own = Encoding.ASCII.GetBytes("5 0 obj\n(own)\nendobj\n6 0 obj\n(six)\nendobj\n");
        byte[] shared = Encoding.ASCII.GetBytes("5 0 obj\n(shared)\nendobj\n7 0 obj\n(seven)\nendobj\n");
        var sections = new ProgressiveSections();
        sections.Add(own, RawObjectScanner.Scan(own, null, new List<string>(), out _));
        sections.Add(shared, RawObjectScanner.Scan(shared, null, new List<string>(), out _));

        Assert.Same(own, sections.Find(5)!.Value.Bytes);
        Assert.Equal("own", Text(sections.Parse(5)));
        Assert.Equal("six", Text(sections.Parse(6)));
        Assert.Equal("seven", Text(sections.Parse(7)));
        Assert.Null(sections.Find(8));
        Assert.Null(sections.Parse(8));
    }

    private static string Text(PdfObject value) => Encoding.ASCII.GetString(Assert.IsType<PdfString>(value).RawBytes);
}
