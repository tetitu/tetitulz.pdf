// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="CssColor.TryParse"/>, the one CSS colour reading (HtmlToPdf and the SVG converter).
/// </summary>
public class CssColorTests
{
    [Theory]
    [InlineData("#f80", 1, 0x88 / 255.0, 0, 1)]
    [InlineData("#F808", 1, 0x88 / 255.0, 0, 0x88 / 255.0)]
    [InlineData("#20C080", 0x20 / 255.0, 0xC0 / 255.0, 0x80 / 255.0, 1)]
    [InlineData("#20C08040", 0x20 / 255.0, 0xC0 / 255.0, 0x80 / 255.0, 0x40 / 255.0)]
    [InlineData("rgb(255, 128, 0)", 1, 128 / 255.0, 0, 1)]
    [InlineData("rgb(100% 50% 0%)", 1, 0.5, 0, 1)]
    [InlineData("rgba(0, 0, 255, 0.25)", 0, 0, 1, 0.25)]
    [InlineData("hsl(240, 100%, 50%)", 0, 0, 1, 1)]
    [InlineData("  Navy  ", 0, 0, 128 / 255.0, 1)]
    [InlineData("transparent", 0, 0, 0, 0)]    // CSS Color 3 §4.2.3: rgba(0, 0, 0, 0)
    [InlineData("Transparent", 0, 0, 0, 0)]
    public void AColourIsReadInEveryForm(string value, double r, double g, double b, double a)
    {
        Assert.True(CssColor.TryParse(value, out double pr, out double pg, out double pb, out double pa));
        Assert.Equal(r, pr, 6);
        Assert.Equal(g, pg, 6);
        Assert.Equal(b, pb, 6);
        Assert.Equal(a, pa, 6);
    }

    /// <summary>
    /// A value longer than <see cref="CssColor.MaxLength"/> is refused before either functional
    /// pattern runs — the cap is the guard against the separators' quadratic backtracking on a long
    /// run of spaces — and the same colour one character shorter is read, so the cap refuses by
    /// length alone.
    /// </summary>
    [Fact]
    public void AValueLongerThanAnyColour_IsRefusedByItsLengthAlone()
    {
        string core = "rgb(1,2,3)";
        string over = "rgb(1," + new string(' ', CssColor.MaxLength + 1 - core.Length) + "2,3)";
        Assert.Equal(CssColor.MaxLength + 1, over.Length);
        Assert.False(CssColor.TryParse(over, out _, out _, out _, out _));

        string under = "rgb(1," + new string(' ', CssColor.MaxLength - core.Length) + "2,3)";
        Assert.Equal(CssColor.MaxLength, under.Length);
        Assert.True(CssColor.TryParse(under, out double r, out double g, out double b, out double a));
        Assert.Equal(1 / 255.0, r, 6);
        Assert.Equal(2 / 255.0, g, 6);
        Assert.Equal(3 / 255.0, b, 6);
        Assert.Equal(1, a, 6);

        // Surrounding whitespace is trimmed before the cap, so padding alone does not refuse.
        Assert.True(CssColor.TryParse(new string(' ', 200) + core + new string(' ', 200), out _, out _, out _, out _));
    }

    /// <summary>
    /// What is not a colour is not one: a trailing component, a bad digit, an unknown name, and
    /// digits outside ASCII.
    /// </summary>
    /// <remarks>
    /// The functional forms matched <c>\d</c>, which in .NET is any Unicode decimal digit, and the
    /// number parse behind it read those as 0. So <c>rgb(\u0661\u0660\u0660,0,0)</c> was black rather than not a
    /// colour.
    /// </remarks>
    [Theory]
    [InlineData("#eeeeee url(bg.png)")]
    [InlineData("#gggggg")]
    [InlineData("notacolour")]
    [InlineData("rgb(\u0661\u0660\u0660,0,0)")]
    [InlineData("hsl(\u0661\u0662\u0660, 100%, 50%)")]
    [InlineData("")]
    public void WhatIsNotAColourIsRefused(string value)
        => Assert.False(CssColor.TryParse(value, out _, out _, out _, out _));

    /// <summary>
    /// <see cref="CssColor.ToOpaqueHex"/>: a colour as <c>#RRGGBB</c>, a translucent one composited over
    /// white (half-transparent black is mid grey, 0.5 rounding up to 128), and nothing for what paints
    /// nothing or is not a colour.
    /// </summary>
    [Theory]
    [InlineData("red", "#FF0000")]
    [InlineData("#0a0B0c", "#0A0B0C")]
    [InlineData("rgba(0, 0, 0, 0.5)", "#808080")]
    [InlineData("hsl(120, 100%, 25%)", "#008000")]
    [InlineData("transparent", null)]
    [InlineData("rgba(10, 20, 30, 0)", null)]
    [InlineData("notacolour", null)]
    [InlineData(null, null)]
    public void AColourIsItsOpaqueHex(string? value, string? hex)
        => Assert.Equal(hex, CssColor.ToOpaqueHex(value));
}
