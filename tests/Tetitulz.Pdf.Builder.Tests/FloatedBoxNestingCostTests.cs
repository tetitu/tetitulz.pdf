// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A floated box nested inside floated boxes is laid out a number of times linear in its depth, not
/// 2^depth (Builder-1:0).
/// </summary>
/// <remarks>
/// <para><c>TryPlaceFloatedBox</c> ran a measure pass over the box's whole subtree and then the real
/// pass. The measure pass's height is read only to decide pagination, and a nested float in a cell is
/// placed with <c>paginate:false</c>, so the measure was pure waste there — and because each pass
/// recursed into the nested floats, the two passes compounded to 2^depth layouts. The measure now
/// runs only when its height is used.</para>
/// <para>Asserted on the work probe <see cref="PdfExporter.FloatedBoxLayoutPasses"/> rather than a
/// clock. The lower bound confirms the boxes really floated and nested (a wide page and widths that
/// shrink by more than <c>MinTextWidthBesideFloatPt</c> each level, so every level fits beside its
/// band) — without it the upper bound would pass vacuously on boxes that fell back to blocks.</para>
/// </remarks>
public class FloatedBoxNestingCostTests
{
    [Fact]
    public void NestedFloatedBoxes_AreLaidOutLinearly_NotExponentially()
    {
        const int Depth = 15;   // 2^15 = 32768 passes before the fix; ~2*Depth after
        var doc = new Document();
        var section = new Section { PageWidthPt = 3000 };   // wide, so 15 shrinking floats all fit beside their band
        for (int i = 0; i < Depth; i++)
        {
            section.Children.Add(new BoxStart { Float = FloatSide.Left, FloatWidth = 2500 - 100 * i });
            section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "x" } } });
        }
        for (int i = 0; i < Depth; i++) section.Children.Add(new BoxEnd());
        doc.Sections.Add(section);

        doc.ToPdf();

        Assert.InRange(PdfExporter.FloatedBoxLayoutPasses, Depth, 10 * Depth);
    }
}
