// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A CSS length longer than <see cref="HtmlToBuilderMapper.MaxLengthChars"/> is not a length and
/// falls back before the pattern runs.
/// </summary>
/// <remarks>
/// <para>A number read as <c>\d*\.?\d+</c> would split a long digit string every way there is
/// once the unit fails to match — with the dot optional, the two digit runs overlap — which is
/// quadratic in the string: a style attribute of a megabyte of digits and a stray letter would
/// cost a million-squared steps. The pattern is one way only, and a value past the cap is
/// refused by its length alone — the cap is what this test can see, so it is what it asserts,
/// against the same value one character shorter, which is read.</para>
/// </remarks>
public class CssLengthCapTests
{
    [Fact]
    public void ALengthLongerThanAnyLength_FallsBackByItsLengthAlone()
    {
        const string word = "CAPPED";
        double plain  = XOf(DecodeStreams(ToPdf("<p>" + word + "</p>")), word);
        double inch   = XOf(DecodeStreams(ToPdf("<p style='margin-left:1in'>" + word + "</p>")), word);
        Assert.Equal(72.0, inch - plain, 1);   // the control: a 1 in margin moves the word an inch

        string under = new string('0', HtmlToBuilderMapper.MaxLengthChars - 3) + "1in";
        Assert.Equal(HtmlToBuilderMapper.MaxLengthChars, under.Length);
        double read = XOf(DecodeStreams(ToPdf("<p style='margin-left:" + under + "'>" + word + "</p>")), word);
        Assert.Equal(inch, read, 1);           // at the cap: still an inch

        string over = new string('0', HtmlToBuilderMapper.MaxLengthChars - 2) + "1in";
        Assert.Equal(HtmlToBuilderMapper.MaxLengthChars + 1, over.Length);
        double refused = XOf(DecodeStreams(ToPdf("<p style='margin-left:" + over + "'>" + word + "</p>")), word);
        Assert.Equal(plain, refused, 1);       // one over: the fallback, no margin
    }
}
