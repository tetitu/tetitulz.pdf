// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="UnicodeBidi.Reorder(string, BaseDirection)"/> — the overload that lets a caller supply
/// the paragraph direction instead of having it detected from the first strong character.
///
/// <para><b>Why the overload exists.</b> P2/P3 take the paragraph level from the first strong
/// character, which is right for a paragraph and wrong for a <i>field</i>. A Hebrew form whose value
/// starts with a Latin word or a digit resolves LTR, so the value reads inside-out. UAX #9 §3.3.1
/// explicitly permits a higher-level protocol to supply the level instead.</para>
///
/// <para><b>Two things these pin that are easy to break.</b> First, that <see cref="BaseDirection.Auto"/>
/// is byte-for-byte the old behaviour — every read-path caller in the tree (text extraction, the
/// Text plugin) goes through the single-argument overload, so a change to the default silently
/// rewrites every extraction result. Second, the pure-LTR fast path: <c>Reorder</c> returns early
/// when the string contains no RTL character, and that early-out is only valid under
/// <c>Auto</c> — under a forced RTL paragraph a pure-Latin string still reorders.</para>
/// </summary>
public class UnicodeBidiBaseDirectionTests
{
    private const string Shalom = "שלום";

    /// <summary>
    /// The compatibility pin. If this fails, every existing caller's output has moved.
    /// </summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("(abc)")]
    [InlineData("שלום")]
    [InlineData("(שלום)")]
    [InlineData("שלום 12 (abc) שלום")]
    [InlineData("שלום (a [12] b) שלום")]
    [InlineData("Tel Aviv 12")]
    [InlineData("")]
    public void AutoIsIdenticalToTheSingleArgumentOverload(string text)
    {
        Assert.Equal(UnicodeBidi.Reorder(text), UnicodeBidi.Reorder(text, BaseDirection.Auto));
    }

    /// <summary>
    /// The fast-path trap, named so it cannot be re-broken silently. <c>Reorder</c> short-circuits
    /// on a string containing no RTL character at all; leaving that check ahead of the direction
    /// test makes a forced RTL paragraph a silent no-op for exactly the values a Hebrew form is
    /// most likely to hold — a Latin place name, an account number, a date.
    ///
    /// <para><c>"abc."</c> is the fixture because it contains <b>no</b> RTL character (so the
    /// early-out would fire) and still reorders: the trailing period is a neutral with no strong
    /// type after it, so N2 gives it the paragraph direction and L2 moves it to the visual left.
    /// A string of Latin letters alone would <i>not</i> work here — one LTR run inside an RTL
    /// paragraph legitimately keeps its internal order, so it cannot distinguish the two cases.</para>
    /// </summary>
    [Fact]
    public void PureLatinTextWithATrailingNeutral_UnderForcedRtl_IsStillReordered()
    {
        Assert.Equal("abc.", UnicodeBidi.Reorder("abc.", BaseDirection.Auto));
        Assert.Equal(".abc", UnicodeBidi.Reorder("abc.", BaseDirection.RightToLeft));
    }

    /// <summary>
    /// The invoice bug the overload exists for: auto-detection sees the leading digit, calls the
    /// paragraph LTR, and puts the currency sign at the visual right. A Hebrew form wants it left.
    /// </summary>
    [Fact]
    public void LeadingDigit_ResolvesDifferentlyUnderAutoAndForcedRtl()
    {
        const string amount = "1,234 ₪";

        string auto = UnicodeBidi.Reorder(amount, BaseDirection.Auto);
        string rtl = UnicodeBidi.Reorder(amount, BaseDirection.RightToLeft);

        Assert.NotEqual(auto, rtl);
        Assert.EndsWith("₪", auto, System.StringComparison.Ordinal);
        Assert.StartsWith("₪", rtl, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The converse direction: forcing LTR on text whose first strong character is Hebrew must
    /// differ from letting it auto-detect, or the parameter is not reaching P2/P3 at all.
    /// </summary>
    [Fact]
    public void HebrewWithTrailingLatin_ResolvesDifferentlyUnderAutoAndForcedLtr()
    {
        string source = $"{Shalom} abc";

        Assert.NotEqual(UnicodeBidi.Reorder(source, BaseDirection.Auto),
                        UnicodeBidi.Reorder(source, BaseDirection.LeftToRight));
    }

    /// <summary>
    /// An all-RTL string under forced RTL is the same as under auto — forcing the direction that
    /// detection would have chosen anyway must change nothing. Without this, a test suite could
    /// pass with the forced values wired to something arbitrary.
    /// </summary>
    [Fact]
    public void AllRtlText_ForcedRtl_MatchesAuto()
    {
        Assert.Equal(UnicodeBidi.Reorder(Shalom, BaseDirection.Auto),
                     UnicodeBidi.Reorder(Shalom, BaseDirection.RightToLeft));
    }

    [Theory]
    [InlineData("abc", BaseDirection.Auto, false)]
    [InlineData("abc", BaseDirection.RightToLeft, true)]
    [InlineData("שלום", BaseDirection.Auto, true)]
    [InlineData("שלום", BaseDirection.LeftToRight, false)]
    [InlineData("12 שלום", BaseDirection.Auto, true)]   // digits are not strong; first strong is R
    [InlineData("", BaseDirection.Auto, false)]
    // P2 skips an isolate's content to its PDI: the first strong character outside one decides.
    [InlineData("⁦abc⁩ שלום", BaseDirection.Auto, true)]
    [InlineData("⁧שלום⁩ abc", BaseDirection.Auto, false)]
    // An isolate never closed runs to the paragraph's end, so nothing strong is found (P3: level 0).
    [InlineData("⁦abc שלום", BaseDirection.Auto, false)]
    public void IsRightToLeftParagraph_AgreesWithTheDirectionReorderUses(
        string text, BaseDirection direction, bool expected)
    {
        Assert.Equal(expected, UnicodeBidi.IsRightToLeftParagraph(text, direction));
        if (direction == BaseDirection.Auto)
            Assert.Equal(UnicodeBidi.ResolveParagraph(text, direction).IsRightToLeft, expected);
    }
}
