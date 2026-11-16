// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// W1–W6 and N0 per isolating run sequence (UAX #9 X10), where they ran over the flat paragraph: a
/// matched isolate is one neutral of the sequence round it, its interior a sequence of its own
/// from its own sos. Asserted on the resolved levels.
/// </summary>
public class UnicodeBidiIsolatingSequenceTests
{
    private const char Alef = 'א', Bet = 'ב';
    private const char Lri = '⁦', Rli = '⁧', Pdi = '⁩';

    private static int Level(string text, BaseDirection direction, int index)
        => UnicodeBidi.ResolveParagraph(text, direction).LevelAt(index);

    /// <summary>
    /// W5: a run of terminators before a number becomes numbers, all of it — <c>$$5</c> after
    /// Hebrew puts both dollar signs at the number's level 2. Only the one next to the digit did;
    /// the first resolved right-to-left with the Hebrew, level 1.
    /// </summary>
    [Fact]
    public void ARunOfTerminatorsBeforeANumberBecomesNumbers()
    {
        string text = Alef + " $$5";
        Assert.Equal(2, Level(text, BaseDirection.RightToLeft, 2));   // the first $
        Assert.Equal(2, Level(text, BaseDirection.RightToLeft, 3));   // the second
        Assert.Equal(2, Level(text, BaseDirection.RightToLeft, 4));   // the 5
    }

    /// <summary>
    /// N0: brackets whose only content is an isolate enclose nothing strong in their own sequence,
    /// so they resolve by N1/N2 — between Hebrew and a Latin word in a left-to-right paragraph,
    /// the paragraph's direction, level 0. Flat, the isolated Hebrew inside decided them
    /// right-to-left, level 1.
    /// </summary>
    [Fact]
    public void BracketsRoundAnIsolateDoNotSeeInsideIt()
    {
        string text = Alef + " (" + Rli + Bet + Pdi + ") b";
        Assert.Equal(0, Level(text, BaseDirection.LeftToRight, 2));   // (
        Assert.Equal(0, Level(text, BaseDirection.LeftToRight, 6));   // )
    }

    /// <summary>
    /// N0 c: with nothing strong before the opening bracket in its sequence, the context is the
    /// sequence's sos — a right-to-left isolate's, R — so brackets opening it round Latin text
    /// resolve right-to-left, level 1. The paragraph's direction (L) made them level 2.
    /// </summary>
    [Fact]
    public void BracketsAtAnIsolatesStartTakeItsSos()
    {
        string text = Rli + "(abc)" + Pdi;
        Assert.Equal(1, Level(text, BaseDirection.LeftToRight, 1));   // (
        Assert.Equal(1, Level(text, BaseDirection.LeftToRight, 5));   // )
        Assert.Equal(2, Level(text, BaseDirection.LeftToRight, 2));   // a: L inside the RTL isolate
    }

    /// <summary>
    /// W4: a separator between two digits is part of the number — the comma of <c>1,000</c> and
    /// the plus of <c>5+3</c> after Hebrew sit at the digits' level 2, so the number is not split
    /// round a right-to-left separator.
    /// </summary>
    [Fact]
    public void ASeparatorBetweenDigitsIsPartOfTheNumber()
    {
        Assert.Equal(2, Level(Alef + " 1,000", BaseDirection.RightToLeft, 3));
        Assert.Equal(2, Level(Alef + " 5+3", BaseDirection.RightToLeft, 3));
    }

    /// <summary>
    /// The control: without isolates the per-sequence rules are the flat ones — brackets round
    /// Hebrew after Hebrew in a left-to-right paragraph resolve with it, level 1.
    /// </summary>
    [Fact]
    public void WithoutIsolatesNothingChanges()
    {
        string text = Alef + " (" + Bet + ") b";
        Assert.Equal(1, Level(text, BaseDirection.LeftToRight, 2));
        Assert.Equal(1, Level(text, BaseDirection.LeftToRight, 4));
    }

    /// <summary>
    /// N1 inside an isolate a neutral runs into: in <c>x RLI one two, PDI y</c> the space between
    /// <c>one</c> and <c>two</c> lies between two L's, so it is L — level 2 at the isolate's level 1 —
    /// and the comma, between <c>two</c> and the isolate's end (eos R), takes the isolate's direction,
    /// level 1. The neutral walk extended the space before the isolate over the whole span and
    /// jumped past it, so the interior was never resolved and its space fell back to R, level 1:
    /// the Latin run inside was reversed word by word. The control is the same isolate opening the
    /// paragraph, which the walk visited at its initiator and always resolved.
    /// </summary>
    [Fact]
    public void AnIsolateAfterANeutralHasItsInteriorResolved()
    {
        string text = "x " + Rli + "one two," + Pdi + " y";
        int space = text.IndexOf("one two", System.StringComparison.Ordinal) + 3;
        Assert.Equal(2, Level(text, BaseDirection.LeftToRight, space));
        Assert.Equal(1, Level(text, BaseDirection.LeftToRight, text.IndexOf(',')));
        Assert.Equal(2, Level(text, BaseDirection.LeftToRight, space - 1));   // the e of one

        string atStart = Rli + "one two," + Pdi + " y";
        Assert.Equal(2, Level(atStart, BaseDirection.LeftToRight, atStart.IndexOf("one two", System.StringComparison.Ordinal) + 3));
    }
}
