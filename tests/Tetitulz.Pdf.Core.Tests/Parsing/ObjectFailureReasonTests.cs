// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Every <c>PdfNull</c> that does not mean "this file has no such object" says why.
/// </summary>
/// <remarks>
/// <para><b>The distinction has one carrier and it is <see cref="PdfParser.LastObjectParseError"/>.</b>
/// <c>GetObject</c> and <c>ResolveObject</c> are called millions of times during a render and cannot
/// throw on a file a reader is expected to tolerate, so a malformed object and an absent one are the
/// same return value. The slot is what separates them, and a path that returns <c>PdfNull</c>
/// without setting it hands a caller a corrupt file dressed as an ordinary incomplete one.</para>
///
/// <para><b>Four paths were silent</b>: an object-stream index outside that stream's entry table, an
/// entry whose offset lies outside the decoded stream, a reference chain longer than
/// <see cref="PdfLimits.MaxIndirectReferenceChain"/>, and a reference chain that loops. Each is
/// covered below with a fixture malformed in exactly that one way, and each is paired with the
/// acceptance control that shows the same shape parses when it is well-formed — a refusal test whose
/// fixture is broken in two ways proves only that something was rejected.</para>
/// </remarks>
public class ObjectFailureReasonTests
{
    // ==================================================================
    //  Genuine absence — the two paths that are SUPPOSED to stay silent
    // ==================================================================

    /// <summary>
    /// <b>An object number the cross-reference table does not name is absent, not broken</b>, and
    /// must report nothing — otherwise the slot fires on ordinary input and a caller learns to
    /// ignore it.
    /// </summary>
    [Fact]
    public void AnObjectTheCrossReferenceTableDoesNotNameReportsNoReason()
    {
        using var doc = Open(MinimalPdf());

        PdfObject missing = doc.ResolveObject(new PdfReference(9999, 0));

        Assert.IsType<PdfNull>(missing);
        Assert.Null(PdfParser.LastObjectParseError);
    }

    /// <summary>The control: an object that <i>is</i> named resolves, and still reports nothing.</summary>
    [Fact]
    public void AnObjectThatResolvesReportsNoReason()
    {
        using var doc = Open(MinimalPdf());

        PdfObject catalog = doc.ResolveObject(new PdfReference(1, 0));

        Assert.IsType<PdfDictionary>(catalog);
        Assert.Null(PdfParser.LastObjectParseError);
    }

    // ==================================================================
    //  Reference chains
    // ==================================================================

    /// <summary>
    /// <b>A reference chain that loops says so.</b> Two objects pointing at each other is a file a
    /// reader must survive, and the guard that makes it survivable must not leave the caller unable
    /// to tell the loop from an object that was never there.
    /// </summary>
    [Fact]
    public void AReferenceChainThatLoopsReportsThatItReturnedToAVisitedObject()
    {
        // 4 -> 5 -> 4 …
        using var doc = Open(MinimalPdf(Obj(4, "5 0 R"), Obj(5, "4 0 R")));

        PdfObject resolved = doc.ResolveObject(new PdfReference(4, 0));

        Assert.IsType<PdfNull>(resolved);
        Assert.NotNull(PdfParser.LastObjectParseError);
        Assert.Contains("already visited", PdfParser.LastObjectParseError, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A reference chain longer than the hop ceiling says so</b>, rather than reporting the
    /// object at the end of it as absent.
    /// </summary>
    /// <remarks>
    /// The chain is one longer than <see cref="PdfLimits.MaxIndirectReferenceChain"/> so that the
    /// ceiling is the only thing that can refuse it; a chain that also looped would prove which of
    /// the two guards fired only by accident.
    /// </remarks>
    [Fact]
    public void AReferenceChainPastTheHopCeilingReportsItsLength()
    {
        int links = PdfLimits.MaxIndirectReferenceChain + 2;
        var objects = new List<string>();
        for (int i = 0; i < links; i++)
            objects.Add(Obj(4 + i, $"{4 + i + 1} 0 R"));
        objects.Add(Obj(4 + links, "42"));

        using var doc = Open(MinimalPdf(objects.ToArray()));

        PdfObject resolved = doc.ResolveObject(new PdfReference(4, 0));

        Assert.IsType<PdfNull>(resolved);
        Assert.NotNull(PdfParser.LastObjectParseError);
        Assert.Contains("hops", PdfParser.LastObjectParseError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance control for both chain tests: a chain comfortably inside the ceiling resolves
    /// to its final value and reports nothing.
    /// </summary>
    [Fact]
    public void AShortReferenceChainResolvesAndReportsNoReason()
    {
        using var doc = Open(MinimalPdf(
            Obj(4, "5 0 R"), Obj(5, "6 0 R"), Obj(6, "42")));

        PdfObject resolved = doc.ResolveObject(new PdfReference(4, 0));

        Assert.Equal(42, Assert.IsType<PdfNumber>(resolved).IntValue);
        Assert.Null(PdfParser.LastObjectParseError);
    }

    // ==================================================================
    //  Resetting
    // ==================================================================

    /// <summary>
    /// <b>The slot belongs to the most recent call and to nothing earlier.</b> Resolving a
    /// <i>direct</i> object never reaches <c>GetObject</c>, which is the path on which a stale
    /// reason could survive and be attributed to an object that parsed perfectly.
    /// </summary>
    [Fact]
    public void ResolvingADirectObjectClearsAnEarlierFailuresReason()
    {
        using var doc = Open(MinimalPdf(Obj(4, "5 0 R"), Obj(5, "4 0 R")));

        doc.ResolveObject(new PdfReference(4, 0));
        Assert.NotNull(PdfParser.LastObjectParseError);   // the fixture really does set it

        PdfObject direct = doc.ResolveObject(new PdfNumber(7));

        Assert.Equal(7, Assert.IsType<PdfNumber>(direct).IntValue);
        Assert.Null(PdfParser.LastObjectParseError);
    }

    // ==================================================================
    //  Fixture assembly
    // ==================================================================

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    /// <summary>A minimal one-page document, plus any extra objects the caller supplies.</summary>
    private static byte[] MinimalPdf(params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
        };
        bodies.AddRange(extraObjects);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string b in bodies) { offsets.Add(sb.Length); sb.Append(b); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
