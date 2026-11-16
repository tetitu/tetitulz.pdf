// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <c>PdfExporter.AdvanceWidth</c> — the Standard-14 string measurement every layout decision in
/// the PDF exporter is built on.
/// </summary>
/// <remarks>
/// <para><b>This existed with no coverage at all, and a mutation is how it surfaced.</b> The width
/// table was being re-selected per character (an if-chain of up to eleven string comparisons, on a
/// base font the caller had already resolved four lines above); hoisting it out of the loop is a
/// pure win, but disabling the table lookup entirely — so every character fell back to the unknown
/// glyph width — left all 309 Builder tests green. The exporter measured every string wrong and
/// nothing noticed.</para>
///
/// <para><b>What makes these falsifiable is the comparison, not the number.</b> Asserting a
/// specific width in glyph-space units would pin the AFM table, not the selection, and would have
/// to be rewritten whenever the fixture changed. Asserting that a proportional font gives
/// <c>W</c> and <c>i</c> <i>different</i> widths while a monospaced one gives them the
/// <i>same</i> width fails for any implementation that has lost the per-font table — including the
/// mutation above, which makes every character equal in both fonts.</para>
/// </remarks>
public class Standard14AdvanceWidthTests
{
    private const double Size = 12.0;

    /// <summary>
    /// Helvetica is proportional: four <c>W</c>s must be wider than four <c>i</c>s. With the width
    /// table unreachable both collapse to the same unknown-glyph width and this fails.
    /// </summary>
    [Fact]
    public void ProportionalFont_GivesWideAndNarrowGlyphsDifferentWidths()
    {
        double wide   = PdfExporter.AdvanceWidth("WWWW", bold: false, italic: false, Size);
        double narrow = PdfExporter.AdvanceWidth("iiii", bold: false, italic: false, Size);

        Assert.True(wide > narrow,
            $"Helvetica is proportional, so 'WWWW' ({wide}) must measure wider than 'iiii' ({narrow}). " +
            "Equal widths mean the per-font width table was not reached.");
    }

    /// <summary>
    /// Courier is monospaced: the same two strings must measure <b>equal</b>. This is the control
    /// that stops the test above being satisfied by any implementation that merely returns
    /// something varying — and it pins the Courier branch, which is the one with its own
    /// missing-glyph width.
    /// </summary>
    [Fact]
    public void MonospacedFont_GivesEveryGlyphTheSameWidth()
    {
        double wide   = PdfExporter.AdvanceWidth("WWWW", bold: false, italic: false, Size, PdfExporter.StdFamily.Mono);
        double narrow = PdfExporter.AdvanceWidth("iiii", bold: false, italic: false, Size, PdfExporter.StdFamily.Mono);

        Assert.Equal(wide, narrow, 6);
    }

    /// <summary>
    /// Bold Helvetica is wider than regular for the same text — so the <c>bold</c> flag really does
    /// select a different table, rather than every family collapsing onto one.
    /// </summary>
    [Fact]
    public void BoldSelectsADifferentTableFromRegular()
    {
        double regular = PdfExporter.AdvanceWidth("Handgloves", bold: false, italic: false, Size);
        double bold    = PdfExporter.AdvanceWidth("Handgloves", bold: true,  italic: false, Size);

        Assert.True(bold > regular,
            $"Helvetica-Bold ({bold}) must measure wider than Helvetica ({regular}) for the same text.");
    }

    /// <summary>Width scales linearly with point size, and an empty string measures zero.</summary>
    [Fact]
    public void WidthScalesWithSizeAndEmptyTextIsZero()
    {
        double at12 = PdfExporter.AdvanceWidth("Handgloves", bold: false, italic: false, 12.0);
        double at24 = PdfExporter.AdvanceWidth("Handgloves", bold: false, italic: false, 24.0);

        Assert.Equal(at12 * 2.0, at24, 6);
        Assert.Equal(0.0, PdfExporter.AdvanceWidth("", bold: false, italic: false, Size));
    }
}
