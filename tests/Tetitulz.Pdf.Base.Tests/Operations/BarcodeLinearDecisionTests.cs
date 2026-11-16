// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;
using F = Tetitulz.Pdf.Base.Tests.Operations.BarcodeReaderTests;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The decisions in the linear readers and the scanline that the <c>Barcodes</c> sweep, batch 1,
/// found nothing asserting — each a mutation that survived. The readers are called on hand-built runs,
/// three pixels a module, so each test reaches exactly the decision it names.
/// </summary>
public class BarcodeLinearDecisionTests
{
    private const int PixelsPerModule = 3, QuietModules = 10;

    /// <summary>
    /// A module string as runs, with a quiet zone of the opposite tone each side; the symbol's first
    /// run is at index 1. <paramref name="inverted"/> paints the bars light and the spaces dark.
    /// </summary>
    private static List<BarcodeScanline.Run> Runs(string modules, bool inverted = false)
    {
        string row = new string('0', QuietModules) + modules + new string('0', QuietModules);
        var runs = new List<BarcodeScanline.Run>();
        int start = 0;
        for (int i = 1; i <= row.Length; i++)
        {
            if (i < row.Length && row[i] == row[start]) continue;
            bool bar = row[start] == '1';
            runs.Add(new BarcodeScanline.Run(bar != inverted, (i - start) * PixelsPerModule, start * PixelsPerModule));
            start = i;
        }
        return runs;
    }

    private static (string Text, string Refusal) Code128(string modules, bool inverted = false)
    {
        string text = Code128Reader.Decode(Runs(modules, inverted), 1, out _, out string refusal);
        return (text, refusal);
    }

    private static string Stop => F.Bits(Code128Symbols.Pattern(Code128Symbols.Stop), Code128Symbols.StopModules);

    // ---------------------------------------------------------------- Code 128

    /// <summary>
    /// A character in no row of the table means "not a Code 128 here" — silence, not a check-character
    /// refusal. (<c>10101000000</c> has an odd number of bar modules, which no character does.)
    /// </summary>
    [Fact]
    public void AnUnknownCharacter_IsSilence()
    {
        var (text, refusal) = Code128(F.Modules(Code128Symbols.StartB) + "10101000000" + F.Modules(0) + Stop);
        Assert.Null(text);
        Assert.Null(refusal);
        Assert.Equal("Hi", Code128(F.SymbolModules(Code128Symbols.StartB, 'H' - 32, 'i' - 32)).Text);
    }

    /// <summary>A Start followed directly by Stop holds not even a check character: not a symbol, and no exception.</summary>
    [Fact]
    public void AStartFollowedByStop_IsNotASymbol()
    {
        var (text, refusal) = Code128(F.Modules(Code128Symbols.StartB) + Stop);
        Assert.Null(text);
        Assert.Null(refusal);
    }

    /// <summary>Code B from code set C reads what follows in B: 65 is 'a' there, a control character in A.</summary>
    [Fact]
    public void CodeBFromSetC_ReadsTheNextValueInB()
        => Assert.Equal("12a", Code128(F.SymbolModules(F.StartC, 12, F.CodeBValue, 65)).Text);

    /// <summary>Shift followed by a switch rather than a character is refused, and says why.</summary>
    [Fact]
    public void AShiftOntoASwitch_IsRefused()
    {
        var (text, refusal) = Code128(F.SymbolModules(Code128Symbols.StartB, F.ShiftValue, F.CodeCValue));
        Assert.Null(text);
        Assert.Contains("Shift", refusal);
    }

    /// <summary>Value 64 in code set A is NUL, the first control character — not a backquote.</summary>
    [Fact]
    public void TheFirstControlValueInSetA_IsNul()
        => Assert.Equal("\0", Code128(F.SymbolModules(F.StartA, 64)).Text);

    /// <summary>A Start value among the data is refused as a Start or Stop, not as a function character.</summary>
    [Fact]
    public void AStartValueAmongTheData_IsNamedAsSuch()
    {
        var (text, refusal) = Code128(F.SymbolModules(F.StartC, F.StartA));
        Assert.Null(text);
        Assert.Contains("Start or Stop", refusal);
    }

    /// <summary>
    /// A symbol painted in negative — light bars on dark — is not read from its first light run; the
    /// same symbol the right way round is.
    /// </summary>
    [Fact]
    public void ANegativeCode128_IsNotRead()
    {
        string modules = F.SymbolModules(Code128Symbols.StartB, 'H' - 32);
        Assert.Null(Code128(modules, inverted: true).Text);
        Assert.Equal("H", Code128(modules).Text);
    }

    // ---------------------------------------------------------------- EAN-13

    /// <summary>An EAN-13 module string whose six left digits use the tables <paramref name="parity"/> names (bit 5 the first).</summary>
    private static string EanModules(string twelveDrawn, int parity)
    {
        var sb = new System.Text.StringBuilder(F.Bits(EanSymbols.EdgeGuard, EanSymbols.EdgeGuardModules));
        for (int i = 0; i < 6; i++)
        {
            int digit = twelveDrawn[i] - '0';
            bool even = ((parity >> (5 - i)) & 1) != 0;
            sb.Append(F.Bits(even ? EanSymbols.LeftEven(digit) : EanSymbols.LeftOdd(digit), EanSymbols.DigitModules));
        }
        sb.Append(F.Bits(EanSymbols.CentreGuard, EanSymbols.CentreGuardModules));
        for (int i = 6; i < 12; i++)
            sb.Append(F.Bits(EanSymbols.Right(twelveDrawn[i] - '0'), EanSymbols.DigitModules));
        sb.Append(F.Bits(EanSymbols.EdgeGuard, EanSymbols.EdgeGuardModules));
        return sb.ToString();
    }

    private static (string Text, string Refusal) Ean(string modules, bool inverted = false)
    {
        string text = Ean13Reader.Decode(Runs(modules, inverted), 1, out _, out string refusal);
        return (text, refusal);
    }

    /// <summary>
    /// 5901234123457: the drawn digits 901234 123457 under first digit 5's parity read back whole; a
    /// parity pattern no first digit uses (all six even) is "not an EAN-13" — silence, not a check
    /// failure; and the same symbol in negative is not read.
    /// </summary>
    [Fact]
    public void AnEan13_NeedsADefinedParity_AndDarkBars()
    {
        const string drawn = "901234123457";
        string modules = EanModules(drawn, EanSymbols.FirstDigitParity(5));
        Assert.Equal("5901234123457", Ean(modules).Text);

        var (text, refusal) = Ean(EanModules(drawn, 0b111111));
        Assert.Null(text);
        Assert.Null(refusal);

        Assert.Null(Ean(modules, inverted: true).Text);
    }

    /// <summary>A sum that is already a multiple of ten has check digit 0, not 10.</summary>
    [Fact]
    public void ACheckDigitOfZero_IsZero()
    {
        Assert.Equal(0, EanSymbols.CheckDigit("000000000000"));
        Assert.Equal(7, EanSymbols.CheckDigit("590123412345"));
    }

    // ---------------------------------------------------------------- the scanline

    /// <summary>One opaque row of grey levels as premultiplied RGBA.</summary>
    private static byte[] Row(params byte[] levels)
    {
        var pixels = new byte[levels.Length * 4];
        for (int i = 0; i < levels.Length; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = levels[i];
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }

    /// <summary>A row becomes its runs, the last one included; a row of one tone is no runs at all.</summary>
    [Fact]
    public void Runs_KeepTheLastRun_AndAUniformRowIsNone()
    {
        var runs = BarcodeScanline.Runs(Row(0, 0, 255, 255, 255), 5, 20, 0, 128);
        Assert.Equal(2, runs.Count);
        Assert.Equal((false, 3, 2), (runs[1].IsDark, runs[1].Length, runs[1].Start));

        Assert.Null(BarcodeScanline.Runs(Row(255, 255, 255), 3, 12, 0, 128));
    }

    /// <summary>A run narrower than half a module rounds to nothing, which is a misread even when the sum comes out right.</summary>
    [Fact]
    public void Quantise_RefusesARunNarrowerThanHalfAModule()
    {
        var into = new int[3];
        var narrow = new List<BarcodeScanline.Run> { new(true, 1, 0), new(false, 10, 1), new(true, 10, 11) };
        Assert.False(BarcodeScanline.Quantise(narrow, 0, 3, 2, into));

        var even = new List<BarcodeScanline.Run> { new(true, 10, 0), new(false, 10, 10), new(true, 10, 20) };
        Assert.True(BarcodeScanline.Quantise(even, 0, 3, 3, into));
        Assert.Equal(new[] { 1, 1, 1 }, into);
    }
}
