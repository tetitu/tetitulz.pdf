// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// <see cref="PdfParser.LastObjectParseError"/> documents itself as
/// "<c>null</c> when the last <see cref="PdfParser.GetObject"/> call on this thread succeeded".
/// </summary>
/// <remarks>
/// <para>
/// That promise is the whole value of the slot: a caller that gets <see cref="PdfNull"/> back reads
/// it to tell "this object is genuinely absent" from "this object failed to parse, and here is why".
/// If a successful call leaves a previous failure's message in place, the second reading is a
/// phantom — the caller blames the wrong object.
/// </para>
/// <para>
/// CLAUDE.md states the rule these pin: <i>"A slot that a caller reads after an operation must be
/// reset at the start of that operation, on every path, not only where it is written."</i> It was
/// written after <c>LastPermsValidation</c> was found to be <c>[ThreadStatic]</c> and still wrong
/// for exactly this reason. Nothing guarded the rest of the slots against the same shape.
/// </para>
/// </remarks>
public class LastObjectParseErrorStalenessTests
{
    /// <summary>
    /// A failing fetch followed by a succeeding one. The success must clear the slot.
    /// </summary>
    [Fact]
    public void GetObject_AfterAFailedParse_ASuccessfulParseClearsTheError()
    {
        byte[] pdf = BuildPdfWithOneCorruptObject();
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        // Object 4's xref offset points at garbage, so this records an error.
        Assert.IsType<PdfNull>(parser.GetObject(4));
        Assert.NotNull(PdfParser.LastObjectParseError);

        // Object 1 (the catalog) is well formed. Per the documented contract the slot is now null.
        Assert.IsType<PdfDictionary>(parser.GetObject(1));
        Assert.Null(PdfParser.LastObjectParseError);
    }

    /// <summary>
    /// A cache hit is a successful call too, so it must not resurrect a stale message either.
    /// </summary>
    [Fact]
    public void GetObject_CachedHitAfterAFailedParse_AlsoClearsTheError()
    {
        byte[] pdf = BuildPdfWithOneCorruptObject();
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        Assert.IsType<PdfDictionary>(parser.GetObject(1));   // populate the cache
        Assert.IsType<PdfNull>(parser.GetObject(4));         // fail, recording an error
        Assert.NotNull(PdfParser.LastObjectParseError);

        Assert.IsType<PdfDictionary>(parser.GetObject(1));   // served from the cache
        Assert.Null(PdfParser.LastObjectParseError);
    }

    /// <summary>
    /// Objects 1-3 are ordinary and parse cleanly. Object 4's <b>xref offset is deliberately
    /// wrong</b> — it points at the <c>%PDF-1.4</c> header, where there is no <c>4 0 obj</c> —
    /// so resolving it fails and the parser records why.
    /// </summary>
    /// <remarks>
    /// A merely malformed <i>body</i> is not enough: the parser is tolerant and recovers
    /// <c>&lt;&lt; /Broken</c> into a dictionary rather than throwing, so a fixture built that way
    /// silently tests nothing. Corrupting the offset is what actually reaches the failure path.
    /// </remarks>
    private static byte[] BuildPdfWithOneCorruptObject()
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        offsets.Add(2);   // object 4: points inside the header, not at an indirect object

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
