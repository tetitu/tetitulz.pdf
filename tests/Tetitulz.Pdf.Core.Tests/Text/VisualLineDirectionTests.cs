// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// The direction a line in visual order is read back in, when nothing says which paragraph it came
/// from: its two ends when they agree, its strong characters by number when they do not.
/// </summary>
/// <remarks>Strings are as drawn, left to right: a Hebrew word appears with its letters reversed.</remarks>
public sealed class VisualLineDirectionTests
{
    // "שלום" as drawn, left to right.
    private const string ShalomDrawn = "\u05DD\u05D5\u05DC\u05E9";

    /// <summary>A mostly Hebrew line whose left end is a Latin code reads right to left, where P2/P3 read it left to right.</summary>
    [Fact]
    public void AHebrewLineEndingInALatinCode_ReadsRightToLeft()
    {
        string drawn = "AB " + ShalomDrawn + " " + ShalomDrawn;
        Assert.Equal(BaseDirection.RightToLeft, UnicodeBidi.VisualLineDirection(drawn));
        Assert.False(UnicodeBidi.IsRightToLeftParagraph(drawn, BaseDirection.Auto));   // what P2/P3 says
    }

    /// <summary>A mostly Latin line whose right end is a Hebrew word reads left to right.</summary>
    [Fact]
    public void ALatinLineEndingInAHebrewWord_ReadsLeftToRight()
        => Assert.Equal(BaseDirection.LeftToRight, UnicodeBidi.VisualLineDirection("Hello World " + ShalomDrawn));

    /// <summary>Ends that agree decide whatever lies between: a Hebrew line holding a long Latin phrase stays right to left.</summary>
    [Fact]
    public void EndsThatAgree_Decide()
    {
        Assert.Equal(BaseDirection.RightToLeft,
            UnicodeBidi.VisualLineDirection(ShalomDrawn + " Hello Wonderful World " + ShalomDrawn));
        Assert.Equal(BaseDirection.LeftToRight,
            UnicodeBidi.VisualLineDirection("Go " + ShalomDrawn + " " + ShalomDrawn + " " + ShalomDrawn + " now"));
    }

    /// <summary>A tie keeps P2/P3's answer, the leftmost strong character's; no strong character reads left to right.</summary>
    [Fact]
    public void ATieOrNothingStrong_KeepsTheLeftmost()
    {
        Assert.Equal(BaseDirection.LeftToRight, UnicodeBidi.VisualLineDirection("ABCD " + ShalomDrawn));
        Assert.Equal(BaseDirection.RightToLeft, UnicodeBidi.VisualLineDirection(ShalomDrawn + " ABCD"));
        Assert.Equal(BaseDirection.LeftToRight, UnicodeBidi.VisualLineDirection("12 34"));
    }
}
