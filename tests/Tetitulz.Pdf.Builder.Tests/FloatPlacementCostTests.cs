// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Placing a float no longer scans every exclusion on the page once per float that reaches it, so a
/// page of many floats is laid out in work bounded by <see cref="PdfLimits.MaxBuilderFloatsPerPage"/>
/// rather than growing with the square (or, when they step past one another, the cube) of the floats
/// on the page (Builder-2:0).
/// </summary>
/// <remarks>
/// A float does not advance the flow cursor, so near-zero-height floats pile onto one page without
/// paginating; each placement used to scan every exclusion already registered, which is quadratic in
/// the floats on a page before counting the step-downs. Past the cap the page stops registering new
/// exclusions and placing a float stops stepping, so the work grows linearly. A page with no more
/// floats than the cap is laid out exactly as before — asserted here by byte-identical output.
/// </remarks>
public class FloatPlacementCostTests
{
    // Empty floated paragraphs: no line, so they do not advance the cursor or paginate — hundreds land
    // on one page, which is the shape that made float placement superlinear.
    private static byte[] Render(int floats)
    {
        var doc = new Document();
        var sec = new Section();
        for (int i = 0; i < floats; i++)
            sec.Children.Add(new Paragraph { Float = FloatSide.Left, FloatWidth = 250, SpacingBefore = 0, SpacingAfter = 0 });
        doc.Sections.Add(sec);
        return doc.ToPdf();
    }

    private static long WorkFor(int floats)
    {
        Render(floats);
        return PdfExporter.FloatExclusionsExamined;
    }

    [Fact]
    public void FloatPlacementWork_GrowsLinearly_NotWithTheSquareOfTheFloats()
    {
        int saved = PdfLimits.MaxBuilderFloatsPerPage;
        try
        {
            PdfLimits.MaxBuilderFloatsPerPage = 100;
            long small = WorkFor(250);
            long big = WorkFor(1000);   // 4× the floats

            // Linear: past the cap each float adds a bounded, constant amount of scanning, so 4× the
            // floats is about 4× the work. Before the fix each float scanned every exclusion on the
            // page, so 4× the floats was ~16× the work — which this ×8 ceiling refuses.
            Assert.True(big < small * 8, $"float placement work grew faster than linearly: {small} → {big}");
            // And the work is genuinely spent (not a dead counter the cap zeroed): the control render
            // below has floats, so some exclusions are examined.
            Assert.True(small > 0, "no float exclusions were examined at all");
        }
        finally { PdfLimits.MaxBuilderFloatsPerPage = saved; }
    }

    // Six floats that each draw a word and stack down one page — few enough that the whole page fits
    // without paginating, so the cap's effect on their placement shows in the bytes.
    private static byte[] RenderVisible()
    {
        var doc = new Document();
        var sec = new Section();
        for (int i = 0; i < 6; i++)
            sec.Children.Add(new Paragraph { Float = FloatSide.Left, FloatWidth = 250, Runs = { new TextRun { Text = $"float{i}" } } });
        doc.Sections.Add(sec);
        return doc.ToPdf();
    }

    [Fact]
    public void APageWithinTheCap_IsLaidOutIdentically_WhateverTheCap()
    {
        int saved = PdfLimits.MaxBuilderFloatsPerPage;
        try
        {
            // Six floats, laid out under a cap well above them and under a cap exactly at them: the cap
            // never engages either way, so the bytes match — the bound does not touch a page within it.
            PdfLimits.MaxBuilderFloatsPerPage = 10_000;
            string high = PdfContentProbe.NormalizedRaw(RenderVisible());
            PdfLimits.MaxBuilderFloatsPerPage = 6;
            string atCap = PdfContentProbe.NormalizedRaw(RenderVisible());
            Assert.Equal(high, atCap);

            // Below the float count the cap must actually change the output — otherwise the equality
            // above would pass with the whole mechanism removed.
            PdfLimits.MaxBuilderFloatsPerPage = 3;
            string belowCap = PdfContentProbe.NormalizedRaw(RenderVisible());
            Assert.NotEqual(high, belowCap);
        }
        finally { PdfLimits.MaxBuilderFloatsPerPage = saved; }
    }
}
