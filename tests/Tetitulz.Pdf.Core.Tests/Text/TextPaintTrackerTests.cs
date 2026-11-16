// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="TextPaintTracker"/>: for each text-showing operator, the font it uses and whether it
/// paints — the ISO 14289-1 7.21.4.1 question both PDF/UA checkers ask.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public class TextPaintTrackerTests
{
    [Fact]
    public void InvisibleAndClipModesDoNotPaint_EveryOtherModeDoes()
    {
        var shows = Shows("BT /F1 12 Tf 3 Tr (a) Tj 7 Tr (b) Tj 0 Tr (c) Tj 2 Tr (d) Tj ET");

        Assert.Equal(new[] { ("F1", false), ("F1", false), ("F1", true), ("F1", true) }, shows);
    }

    /// <summary>
    /// The font and the mode are graphics state, so <c>Q</c> restores them — the shape of an OCR
    /// layer, <c>q … 3 Tr … Q</c>, followed by body text. Without the restore the body text read as
    /// invisible and its font was exempted from embedding.
    /// </summary>
    [Fact]
    public void RestoringTheGraphicsStateRestoresTheFontAndTheMode()
    {
        var shows = Shows("/F1 10 Tf q BT /Ocr 10 Tf 3 Tr (layer) Tj ET Q BT (body) Tj ET");

        Assert.Equal(new[] { ("Ocr", false), ("F1", true) }, shows);
    }

    /// <summary>§9.3.6 defines modes 0 to 7; anything else — too large, or not an integer — leaves the mode alone.</summary>
    [Theory]
    [InlineData("4294967299")]   // low 32 bits are 3
    [InlineData("8")]
    [InlineData("-1")]
    [InlineData("3.5")]
    public void AnOperandThatIsNotARenderModeLeavesTheModeAlone(string operand)
    {
        var shows = Shows("BT /F1 12 Tf 0 Tr " + operand + " Tr (a) Tj ET");

        Assert.Equal(new[] { ("F1", true) }, shows);
    }

    /// <summary>
    /// Saves past the stack's ceiling are counted rather than stored, so the restores that match them
    /// do not unwind the saves beneath. Set low here so the fixture stays small.
    /// </summary>
    [Fact]
    public void SavesPastTheCeilingStillPairWithTheirRestores()
    {
        try
        {
            PdfLimits.MaxGraphicsStateStackDepth = 2;
            // Two visible saves are stored, the mode turns invisible, and the third save is only
            // counted. Its restore must leave the invisible mode in place — a tracker that dropped
            // the save and popped anyway would restore a visible one here — and the next restore
            // pops back to visible.
            var shows = Shows("/F1 1 Tf 0 Tr q q 3 Tr q Q (a) Tj Q (b) Tj");

            Assert.Equal(new[] { ("F1", false), ("F1", true) }, shows);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static List<(string Font, bool Paints)> Shows(string content)
    {
        var tracker = new TextPaintTracker();
        var shows = new List<(string, bool)>();
        foreach (var op in ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content)))
            if (tracker.Observe(op.Operator, op.Operands))
                shows.Add((tracker.CurrentFont, tracker.Paints));
        return shows;
    }
}
