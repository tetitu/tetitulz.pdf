// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.ContextLookupFont;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The positioning reader's context rules (<c>GPOS</c> lookup types 7 and 8, formats 1–3) and its
/// mark-skipping flags (a mark-attachment type, a mark filtering set). Each form of the fixture's one
/// rule raises the second input glyph by 10 where it matches — see <see cref="F"/>.
/// </summary>
public class ContextLookupTests
{
    public static readonly TheoryData<F.Form> ContextForms = new() { F.Form.Context1, F.Form.Context2, F.Form.Context3 };
    public static readonly TheoryData<F.Form> ChainForms = new() { F.Form.Chain1, F.Form.Chain2, F.Form.Chain3 };

    /// <summary>B then A matches every context form, and the nested lookup raises A; A then A does not.</summary>
    [Theory]
    [MemberData(nameof(ContextForms))]
    public void AContextRule_RaisesItsSecondGlyph(F.Form form)
    {
        Assert.Equal(new[] { 0, 10 }, Raised(form, 0, F.B, F.A));
        Assert.Equal(new[] { 0, 0 }, Raised(form, 0, F.A, F.A));
        Assert.Equal(new[] { 0, 0 }, Raised(form, 0, F.B, F.C));
    }

    /// <summary>
    /// C is covered by formats 1 and 2, but its rule set is null (format 1) and its class has no rules
    /// (format 2); in format 3 it is not where a rule can start. None raises A after C.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContextForms))]
    public void AFirstGlyphWithNoRules_MatchesNothing(F.Form form)
        => Assert.Equal(new[] { 0, 0 }, Raised(form, 0, F.C, F.A));

    /// <summary>
    /// D is in A's class (format 2) and A's coverage (format 3), so B then D matches there; format 1
    /// names glyphs, and D is not A.
    /// </summary>
    [Theory]
    [InlineData(F.Form.Context1, 0)]
    [InlineData(F.Form.Context2, 10)]
    [InlineData(F.Form.Context3, 10)]
    [InlineData(F.Form.Chain1, 0)]
    [InlineData(F.Form.Chain2, 10)]
    [InlineData(F.Form.Chain3, 10)]
    public void AClassOrCoverage_MatchesEveryGlyphItHolds(F.Form form, int raised)
    {
        bool chained = form >= F.Form.Chain1;
        int[] y = chained ? Raised(form, 0, F.C, F.B, F.D, F.C) : Raised(form, 0, F.B, F.D);
        Assert.Equal(raised, y[chained ? 2 : 1]);
    }

    /// <summary>
    /// A chained rule matches only between its backtrack and its lookahead: C B A C raises A; with
    /// either C missing, or another glyph in its place — the first input glyph too — nothing moves.
    /// </summary>
    [Theory]
    [MemberData(nameof(ChainForms))]
    public void AChainedRule_NeedsItsBacktrackAndLookahead(F.Form form)
    {
        Assert.Equal(new[] { 0, 0, 10, 0 }, Raised(form, 0, F.C, F.B, F.A, F.C));
        Assert.Equal(new[] { 0, 0, 0 }, Raised(form, 0, F.B, F.A, F.C));
        Assert.Equal(new[] { 0, 0, 0 }, Raised(form, 0, F.C, F.B, F.A));
        Assert.Equal(new[] { 0, 0, 0, 0 }, Raised(form, 0, F.A, F.B, F.A, F.C));
        Assert.Equal(new[] { 0, 0, 0, 0 }, Raised(form, 0, F.C, F.B, F.A, F.A));
        Assert.Equal(new[] { 0, 0, 0, 0 }, Raised(form, 0, F.C, F.A, F.A, F.C));
    }

    /// <summary>
    /// The rule's glyphs are matched over the glyphs its lookup does not skip: with IgnoreMarks, a mark
    /// between B and A is passed over, and between the backtrack and B, and A and the lookahead.
    /// Without the flag the mark interrupts the rule.
    /// </summary>
    [Theory]
    [InlineData(F.Form.Context1)]
    [InlineData(F.Form.Context2)]
    [InlineData(F.Form.Context3)]
    [InlineData(F.Form.Chain1)]
    [InlineData(F.Form.Chain2)]
    [InlineData(F.Form.Chain3)]
    public void SkippedMarks_DoNotInterruptARule(F.Form form)
    {
        bool chained = form >= F.Form.Chain1;
        ushort[] glyphs = chained
            ? new[] { F.C, F.M1, F.B, F.M1, F.A, F.M1, F.C }
            : new[] { F.B, F.M1, F.A };
        int a = chained ? 4 : 2;
        Assert.Equal(10, Raised(form, 0x8, glyphs)[a]);
        Assert.Equal(0, Raised(form, 0, glyphs)[a]);
    }

    /// <summary>
    /// A mark-attachment type skips the marks of every other attachment class: with type 1, M2 (class
    /// 2) is passed over and M1 (class 1) is matched — so it interrupts the rule.
    /// </summary>
    [Fact]
    public void AMarkAttachmentType_SkipsTheOtherClasses()
    {
        Assert.Equal(new[] { 0, 0, 10 }, Raised(F.Form.Context3, 0x0100, F.B, F.M2, F.A));
        Assert.Equal(new[] { 0, 0, 0 }, Raised(F.Form.Context3, 0x0100, F.B, F.M1, F.A));
    }

    /// <summary>
    /// A mark filtering set skips the marks outside it: set 0 holds M1, so M2 is passed over and M1
    /// interrupts the rule. The set decides where the flag also names an attachment type that would
    /// say the opposite (type 2 skips M1).
    /// </summary>
    [Fact]
    public void AMarkFilteringSet_SkipsTheMarksOutsideIt_AndOverridesTheAttachmentType()
    {
        Assert.Equal(new[] { 0, 0, 10 }, Raised(F.Form.Context3, 0x0010, F.B, F.M2, F.A));
        Assert.Equal(new[] { 0, 0, 0 }, Raised(F.Form.Context3, 0x0010, F.B, F.M1, F.A));
        Assert.Equal(new[] { 0, 0, 10 }, Raised(F.Form.Context3, 0x0210, F.B, F.M2, F.A));
        Assert.Equal(new[] { 0, 0, 0 }, Raised(F.Form.Context3, 0x0210, F.B, F.M1, F.A));
    }

    /// <summary>Without either flag, both marks are matched: neither is skipped, and each interrupts the rule.</summary>
    [Fact]
    public void WithoutAFlag_NoMarkIsSkipped()
    {
        Assert.Equal(new[] { 0, 0, 0 }, Raised(F.Form.Context3, 0, F.B, F.M1, F.A));
        Assert.Equal(new[] { 0, 0, 0 }, Raised(F.Form.Context3, 0, F.B, F.M2, F.A));
    }

    /// <summary>
    /// A rule whose record applies its own lookup at its own first glyph matches again there, without
    /// end but for the nesting bound: the application returns, and the nested raise — never called —
    /// leaves every glyph where it was. The control is the same rule calling the raise.
    /// </summary>
    [Theory]
    [InlineData(F.Form.Context1)]
    [InlineData(F.Form.Context2)]
    [InlineData(F.Form.Context3)]
    [InlineData(F.Form.Chain1)]
    [InlineData(F.Form.Chain2)]
    [InlineData(F.Form.Chain3)]
    public void ARuleThatCallsItself_IsBounded(F.Form form)
    {
        ushort[] glyphs = form >= F.Form.Chain1 ? new[] { F.C, F.B, F.A, F.C } : new[] { F.B, F.A };
        OpenTypeMarkPositioning recursive = OpenTypeMarkPositioning.TryLoad(F.Build(form, selfRecursive: true), 1000);
        Assert.All(recursive.Apply("latn", glyphs).YPlacement, y => Assert.Equal(0, y));
        Assert.Contains(10, Raised(form, 0, glyphs));
    }

    private static int[] Raised(F.Form form, int flag, params ushort[] glyphs)
    {
        OpenTypeMarkPositioning gpos = OpenTypeMarkPositioning.TryLoad(F.Build(form, flag), 1000);
        Assert.NotNull(gpos);
        return gpos.Apply("latn", glyphs).YPlacement.ToArray();
    }
}
