// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using System.Threading;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// CSS values a page controls, read in time linear in their length. Quadratic patterns on a failed match,
/// measured through the converter: <c>\d*\.?\d+</c> on a <c>line-height</c> of a hundred thousand digits, 22 s;
/// <c>/\*.*?\*/</c> on twenty thousand unclosed <c>/*</c>, 11 s; unanchored <c>url\(…</c> on a value of unclosed
/// openers. The hostile inputs here are ten times those sizes, so such patterns could not finish inside the
/// join; each converts and still draws the page's word.
/// </summary>
public class HostileCssValueTests
{
    [Theory]
    [InlineData("1", false, true)]
    [InlineData("1.5", false, true)]
    [InlineData(".5", false, true)]
    [InlineData("-1.5", true, true)]
    [InlineData("-1.5", false, false)]
    [InlineData("1.", false, false)]
    [InlineData(".", false, false)]
    [InlineData("-", true, false)]
    [InlineData("", false, false)]
    [InlineData("1x", false, false)]
    [InlineData("1.2.3", false, false)]
    [InlineData("--1", true, false)]
    public void ACssNumber_IsDigitsWithAnOptionalFraction(string value, bool signed, bool expected)
        => Assert.Equal(expected, HtmlToBuilderMapper.IsCssNumber(value, signed));

    [Fact]
    public void ANumberLongerThanALength_IsNotANumber()
    {
        Assert.True(HtmlToBuilderMapper.IsCssNumber(new string('1', HtmlToBuilderMapper.MaxLengthChars), signed: false));
        Assert.False(HtmlToBuilderMapper.IsCssNumber(new string('1', HtmlToBuilderMapper.MaxLengthChars + 1), signed: false));
    }

    [Theory]
    [InlineData("a/*x*/b", "ab")]
    [InlineData("a/*x*/b/*y*/c", "abc")]
    [InlineData("a/* /* */b", "ab")]          // comments do not nest
    [InlineData("a*/b", "a*/b")]              // a closer alone is text
    [InlineData("a/*x", "a")]                 // open to the end of the sheet (CSS Syntax §4.3.2)
    [InlineData("p{}", "p{}")]
    public void CssComments_AreStripped(string css, string expected)
        => Assert.Equal(expected, HtmlToBuilderMapper.StripCssComments(css));

    /// <summary>
    /// A media query's length and resolution are refused past the length a CSS length can have, before the
    /// pattern runs, and the feature is then one this engine cannot evaluate — which, like every unknown query,
    /// applies its block. So a <c>min-width</c> of 99999 that is false written short (the control: not red) is
    /// unknown written in seventy characters, zeros leading, and its block applies (red); read past the cap it
    /// would be false again.
    /// </summary>
    [Theory]
    [InlineData("min-width: {0}px")]
    [InlineData("min-resolution: {0}dppx")]
    public void AMediaValueLongerThanALength_IsNotRead(string feature)
    {
        string Page(string number) => "<style>@media (" + string.Format(System.Globalization.CultureInfo.InvariantCulture, feature, number)
                                    + ") { p { color: #ff0000 } }</style><p>Word</p>";
        Assert.DoesNotContain("1 0 0 rg", DecodeStreams(ToPdf(Page("99999"))), StringComparison.Ordinal);
        Assert.Contains("1 0 0 rg", DecodeStreams(ToPdf(Page(new string('0', 70) + "99999"))), StringComparison.Ordinal);
    }

    [Fact]
    public void AHostileLineHeightOfAMillionDigits_IsIgnoredAndThePageDraws()
        => AssertConvertsAndDraws($"<p style=\"line-height: {new string('1', 1_000_000)}x\">Word</p>");

    [Fact]
    public void AHostileSheetOfUnclosedComments_IsReadAndThePageDraws()
        => AssertConvertsAndDraws("<style>" + Repeat("/*a", 200_000) + "</style><p>Word</p>");

    [Fact]
    public void AHostileBackgroundOfUnclosedUrls_IsReadAndThePageDraws()
        => AssertConvertsAndDraws($"<p style=\"background: {Repeat("url(", 200_000)} left top\">Word</p>");

    [Fact]
    public void AHostileMediaResolutionOfAMillionDigits_IsUnmatchedAndThePageDraws()
        => AssertConvertsAndDraws($"<style>@media (resolution: {new string('1', 1_000_000)}q) {{ p {{ color: red }} }}</style><p>Word</p>");

    private static string Repeat(string s, int count)
    {
        var sb = new StringBuilder(s.Length * count);
        for (int i = 0; i < count; i++) sb.Append(s);
        return sb.ToString();
    }

    /// <summary>Converts on a worker thread with a generous join, and asserts the page's word was drawn.</summary>
    private static void AssertConvertsAndDraws(string html)
    {
        byte[]? pdf = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { pdf = ToPdf(html); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "the conversion did not finish");
        Assert.Null(failure);
        Assert.Contains("(Word) Tj", DecodeStreams(pdf!), StringComparison.Ordinal);
    }
}
