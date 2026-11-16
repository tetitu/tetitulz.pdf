// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A structure element written <b>directly</b> into its parent's <c>/K</c> array — rather than as an
/// indirect reference — has no object number of its own, so an incremental update cannot revise it.
/// Both <c>/ActualText</c> routes skip it and report; neither may let it disturb the running index
/// the caller's provider is handed.
/// </summary>
/// <remarks>
/// <para><b>Found by mutation while consolidating the two routes.</b> Replacing the
/// <c>node.ObjectNumber &lt;= 0</c> guard with <c>if (false)</c> left all 994 Accessibility tests
/// green — no fixture in the suite contained an inline structure element, even though
/// <c>StructTreeWalker</c> documents <c>ObjectNumber = -1</c> for exactly that case and yields it.
/// So the branch was reachable and simply unexercised, which is the distinction worth resolving
/// before writing a test <i>or</i> deleting a line.</para>
///
/// <para><b>The index is the part that matters.</b> The provider is called as
/// <c>(index, existing)</c> with a running count, so a route that skipped a different set of
/// elements would not merely lose one value — it would <b>renumber every element after it</b>, and
/// the provider would answer for the wrong one while every call still looked successful. That is
/// the same failure the figure-alt-text pair's comments warned about, and it is why the two routes
/// share one pass.</para>
/// </remarks>
public class InlineStructureElementSkipTests
{
    /// <summary>
    /// Two indirect <c>&lt;Span&gt;</c>s with a <b>direct</b> one between them. The middle element
    /// is a dictionary written inline in the parent's <c>/K</c> array, so it has no object number.
    /// </summary>
    private static byte[] TaggedPdfWithInlineSpanBetweenTwoIndirectOnes() =>
        FigureAltTextFabricationTests.BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            FigureAltTextFabricationTests.StreamObject(
                "/P <</MCID 0>> BDC\nBT /F1 10 Tf 72 700 Td (The cat sat.) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
            // The middle child is a DIRECT dictionary: no object number, so nothing to revise.
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K [9 0 R "
                + "<< /Type /StructElem /S /Span /P 8 0 R /Pg 3 0 R /K 0 >> 11 0 R] >>",
            "<< /Type /StructElem /S /Span /P 8 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [8 0 R 9 0 R]] >>",
            "<< /Type /StructElem /S /Span /P 8 0 R /Pg 3 0 R /K 0 >>",
        });

    /// <summary>
    /// The fixture is only worth anything if the walker really yields the inline element — otherwise
    /// this file would be testing that a case which cannot arise is handled.
    /// </summary>
    [Fact]
    public void TheWalkerYieldsTheInlineSpanWithNoObjectNumber()
    {
        using var ms = new MemoryStream(TaggedPdfWithInlineSpanBetweenTwoIndirectOnes(), writable: false);
        using var doc = PdfDocument.Open(ms);

        var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) as PdfDictionary;
        Assert.NotNull(root);

        int spans = 0, withoutNumber = 0;
        foreach (var node in Internal.StructTreeWalker.Collect(root, doc, null))
        {
            if (node.Role != "Span") continue;
            spans++;
            if (node.ObjectNumber <= 0) withoutNumber++;
        }

        Assert.Equal(3, spans);
        Assert.Equal(1, withoutNumber);
    }

    /// <summary>
    /// The clear-text route revises the two spans that have object numbers, and the index it hands
    /// the provider counts <b>values applied</b>, not elements visited — so a skipped element does
    /// not advance it.
    /// </summary>
    /// <remarks>
    /// <para><b>The provider is therefore called three times and sees index 1 twice</b>, which looks
    /// wrong and is the deliberate trade. The provider runs <i>before</i> the revisability check on
    /// purpose: it may answer <c>null</c> for "leave this element alone", and an element the caller
    /// never wanted changed must not be reported as one whose <c>/ActualText</c> could not be set.
    /// Checking revisability first would make that diagnostic fire on perfectly intended input —
    /// the "a channel that fires on conformant files is one callers learn to ignore" problem.</para>
    ///
    /// <para>The cost is that a provider with side effects sees a call whose answer is discarded.
    /// That is pinned here rather than left to be rediscovered as a bug, because both orderings are
    /// defensible and only one of them is what the code does.</para>
    /// </remarks>
    [Fact]
    public void SetActualTextSkipsTheInlineSpanWithoutAdvancingTheIndex()
    {
        var seen = new List<int>();

        using var input = new MemoryStream(TaggedPdfWithInlineSpanBetweenTwoIndirectOnes());
        using var output = new MemoryStream();
        int count = PdfStructureTreeEditor.SetActualText(
            input, output, "Span", (i, _) => { seen.Add(i); return "T" + i; });

        // Two revisable spans -> two values applied.
        Assert.Equal(2, count);

        // Three calls, and the index does not advance across the skipped one: without that, the
        // third span would be handed index 2 and every provider keyed on position would answer for
        // the wrong element.
        Assert.Equal(new[] { 0, 1, 1 }, seen);
    }

    /// <summary>
    /// The encrypted-input route must reach the same verdict — same count, same indices — because a
    /// document remediated either way has to end up describing the same elements.
    /// </summary>
    [Fact]
    public void PlanActualTextAgreesWithTheClearTextRouteOnWhatIsSkipped()
    {
        var planSeen = new List<int>();
        using var ms = new MemoryStream(TaggedPdfWithInlineSpanBetweenTwoIndirectOnes(), writable: false);
        using var doc = PdfDocument.Open(ms);

        StructureTreePlan plan = PdfStructureTreeEditor.PlanActualText(
            doc, 100, "Span", (i, _) => { planSeen.Add(i); return "T" + i; });

        var writeSeen = new List<int>();
        using var input = new MemoryStream(TaggedPdfWithInlineSpanBetweenTwoIndirectOnes());
        using var output = new MemoryStream();
        int written = PdfStructureTreeEditor.SetActualText(
            input, output, "Span", (i, _) => { writeSeen.Add(i); return "T" + i; });

        Assert.Equal(written, plan.ItemsChanged);
        Assert.Equal(writeSeen, planSeen);
    }

    /// <summary>
    /// Skipping is <b>reported</b>, not silent: a caller that gets 2 back for a document containing
    /// 3 spans has to be able to find out why, which is the whole contract of the diagnostics sink.
    /// </summary>
    [Fact]
    public void TheSkippedInlineSpanIsReported()
    {
        // Content-filtered rather than counting: the sink is process-wide, so a concurrent test's
        // message could only add noise here, never remove the one this operation must produce.
        string captured = null;
        Action<string> handler =
            message => { if (message != null && message.Contains("direct (inline)")) captured = message; };

        Internal.AccessibilityDiagnostics.OnEvent += handler;
        try
        {
            using var input = new MemoryStream(TaggedPdfWithInlineSpanBetweenTwoIndirectOnes());
            using var output = new MemoryStream();
            PdfStructureTreeEditor.SetActualText(input, output, "Span", (i, _) => "T" + i);
        }
        finally
        {
            Internal.AccessibilityDiagnostics.OnEvent -= handler;
        }

        Assert.NotNull(captured);
        Assert.Contains("/ActualText was not set", captured);
    }
}
