// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;
using F = Tetitulz.Pdf.Base.Tests.Operations.BarcodeReaderTests;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The barcode function characters — Code 128's FNC1–FNC4 and Data Matrix's FNC1, structured append,
/// reader programming and macros — reported rather than refused (ISO/IEC 15417 and 16022, as an independent reader
/// and an independent writer read them). Until 2026-10-17 every one of them refused the symbol by name; GS1-128 labels,
/// the commonest Code 128 on a shipping document, did not read at all.
/// </summary>
public class BarcodeFunctionCharacterTests
{
    private const int Fnc1 = 102, Fnc2 = 97, Fnc3 = 96;

    // ---------------------------------------------------------------- Code 128, direct

    private static (string Text, string Refusal, BarcodeSymbolInfo Info) Code128(int start, params int[] values)
    {
        string modules = F.SymbolModules(start, values);
        string row = new string('0', 10) + modules + new string('0', 10);
        var runs = new List<BarcodeScanline.Run>();
        int begin = 0;
        for (int i = 1; i <= row.Length; i++)
        {
            if (i < row.Length && row[i] == row[begin]) continue;
            runs.Add(new BarcodeScanline.Run(row[begin] == '1', (i - begin) * 3, begin * 3));
            begin = i;
        }
        string text = Code128Reader.Decode(runs, 1, out _, out string refusal, out BarcodeSymbolInfo info);
        return (text, refusal, info);
    }

    /// <summary>
    /// GS1-128: FNC1 first is the identifier, not a character; a later FNC1 is the GS separator that
    /// ends the variable-length element before it. (01) GTIN, then (10) batch.
    /// </summary>
    [Fact]
    public void Gs1_128_LeadingFnc1IsTheIdentifier_ALaterOneTheGroupSeparator()
    {
        var (text, refusal, info) = Code128(F.StartC, Fnc1, 1, 9, 50, 12, 34, 56, 78, 90, 12, 10, 12, 34, Fnc1, 21, 56);

        Assert.Null(refusal);
        Assert.Equal("0109501234567890121012" + "34\u001D2156", text);
        Assert.True(info.IsGs1);
        Assert.Equal(1, info.Modifier);
    }

    /// <summary>The control: the same digits with no FNC1 are plain Code 128, modifier 0.</summary>
    [Fact]
    public void WithoutFnc1_TheSymbolIsPlainCode128()
    {
        var (text, _, info) = Code128(F.StartC, 1, 9, 50);
        Assert.Equal("010950", text);
        Assert.False(info.IsGs1);
        Assert.Equal(0, info.Modifier);
    }

    /// <summary>FNC1 after one character (A or B) or one digit pair (C) marks an AIM application, and is not transmitted.</summary>
    [Theory]
    [InlineData(Code128Symbols.StartB, new[] { 'Z' - 32, Fnc1, '1' - 32 }, "Z1")]
    [InlineData(F.StartC, new[] { 42, Fnc1, 17 }, "4217")]
    public void Fnc1InTheSecondPosition_MarksAnAimApplication(int start, int[] values, string expected)
    {
        var (text, _, info) = Code128(start, values);
        Assert.Equal(expected, text);
        Assert.Equal(2, info.Modifier);
        Assert.False(info.IsGs1);
    }

    /// <summary>FNC1 after two characters is neither identifier nor AIM: it is a GS separator in the text.</summary>
    [Fact]
    public void Fnc1AfterTwoCharacters_IsAGroupSeparator()
    {
        var (text, _, info) = Code128(Code128Symbols.StartB, 'A' - 32, 'B' - 32, Fnc1, 'C' - 32);
        Assert.Equal("AB\u001DC", text);
        Assert.Equal(0, info.Modifier);
    }

    /// <summary>A single FNC4 adds 128 to the next character only: 'A' + 128 is U+00C1 in Latin-1, then a plain 'B'.</summary>
    [Fact]
    public void ASingleFnc4_ShiftsTheNextCharacterIntoTheUpperHalf()
    {
        var (text, _, _) = Code128(Code128Symbols.StartB, F.CodeBValue, 'A' - 32, 'B' - 32);
        Assert.Equal("\u00C1B", text);
    }

    /// <summary>
    /// Two FNC4s latch extended ASCII: every following character is in the upper half, a single FNC4
    /// inside the latch takes one back down, and two more unlatch.
    /// </summary>
    [Fact]
    public void TwoFnc4sLatchExtendedAscii_ASingleOneInsideTakesOneCharacterBackDown()
    {
        var (text, _, _) = Code128(Code128Symbols.StartB,
            F.CodeBValue, F.CodeBValue, 'A' - 32, 'B' - 32, F.CodeBValue, 'C' - 32, 'D' - 32,
            F.CodeBValue, F.CodeBValue, 'E' - 32);
        Assert.Equal("\u00C1\u00C2C\u00C4E", text);
    }

    /// <summary>In code set A, FNC4 is value 101; it shifts a control character too (HT + 128).</summary>
    [Fact]
    public void Fnc4InCodeSetA_IsValue101()
    {
        var (text, _, _) = Code128(F.StartA, F.CodeAValue, 73);
        Assert.Equal("\u0089", text);
    }

    /// <summary>An FNC4 with nothing after it has nothing to shift, and says so.</summary>
    [Fact]
    public void AnFnc4AtTheEnd_IsRefused()
    {
        var (text, refusal, _) = Code128(Code128Symbols.StartB, 'A' - 32, F.CodeBValue);
        Assert.Null(text);
        Assert.Contains("FNC4", refusal, StringComparison.Ordinal);
    }

    /// <summary>FNC2 and FNC3 are flags on the result; the text is the data around them.</summary>
    [Fact]
    public void Fnc2AndFnc3_AreFlags()
    {
        var (append, _, appendInfo) = Code128(Code128Symbols.StartB, Fnc2, 'A' - 32);
        Assert.Equal("A", append);
        Assert.True(appendInfo.AppendsToNext);
        Assert.False(appendInfo.ReaderProgramming);

        var (program, _, programInfo) = Code128(Code128Symbols.StartB, Fnc3, 'A' - 32);
        Assert.Equal("A", program);
        Assert.True(programInfo.ReaderProgramming);
        Assert.False(programInfo.AppendsToNext);
    }

    /// <summary>In code set C, 96 and 97 are the digit pairs they are, not FNC3 and FNC2.</summary>
    [Fact]
    public void InCodeSetC_96And97AreDigitPairs()
    {
        var (text, _, info) = Code128(F.StartC, 96, 97);
        Assert.Equal("9697", text);
        Assert.False(info.AppendsToNext);
        Assert.False(info.ReaderProgramming);
    }

    // ---------------------------------------------------------------- Code 128, through the page

    /// <summary>A painted GS1-128 symbol read off a rendered page reports <c>]C1</c> and GS1, the GS in its text.</summary>
    [Fact]
    public void AGs1_128SymbolOnAPage_ReportsItsIdentifier()
    {
        BarcodeReadResult result = Assert.Single(ReadPage(F.SymbolModules(F.StartC, Fnc1, 10, 12, 34, Fnc1, 21, 56)));
        Assert.Equal("101234\u001D2156", result.Text);
        Assert.Equal("]C1", result.SymbologyIdentifier);
        Assert.True(result.IsGs1);

        BarcodeReadResult plain = Assert.Single(ReadPage(F.SymbolModules(Code128Symbols.StartB, 'H' - 32, 'i' - 32)));
        Assert.Equal("]C0", plain.SymbologyIdentifier);
        Assert.False(plain.IsGs1);
    }

    // ---------------------------------------------------------------- Data Matrix

    private static (string Text, string Refusal, BarcodeSymbolInfo Info) DataMatrix(params int[] codewords)
    {
        bool[,] grid = DataMatrixEncoder.EncodeCodewords(new List<int>(codewords));
        string text = DataMatrixGridReader.Decode(grid, out string refusal, out BarcodeSymbolInfo info);
        return (text, refusal, info);
    }

    /// <summary>
    /// GS1 Data Matrix: FNC1 (232) first is the identifier <c>]d2</c>; a later one is GS. "01" and "12"
    /// as digit pairs (130 + n), the letters in ASCII (code + 1).
    /// </summary>
    [Fact]
    public void Gs1DataMatrix_LeadingFnc1IsTheIdentifier_ALaterOneTheGroupSeparator()
    {
        var (text, refusal, info) = DataMatrix(232, 131, 142, 'A' + 1, 232, 151);
        Assert.Null(refusal);
        Assert.Equal("0112A\u001D21", text);
        Assert.True(info.IsGs1);
        Assert.Equal(2, info.Modifier);
    }

    [Fact]
    public void DataMatrixFnc1AfterOneLetter_MarksAnAimApplication()
    {
        var (text, _, info) = DataMatrix('Z' + 1, 232, '1' + 1);
        Assert.Equal("Z1", text);
        Assert.Equal(3, info.Modifier);
    }

    /// <summary>FNC1 inside a C40 segment (Shift 2, value 27) is the same function: here a GS between letters.</summary>
    [Fact]
    public void Fnc1InsideAC40Segment_IsAGroupSeparator()
    {
        // C40 values: A (14), B (15), Shift 2 (1); then 27 (FNC1), C (16), D (17). Two letters first, so
        // the FNC1 is past the AIM position (after one letter it would be the AIM flag, not a GS).
        int Pair(int a, int b, int c) => a * 1600 + b * 40 + c + 1;
        int p1 = Pair(14, 15, 1), p2 = Pair(27, 16, 17);
        var (text, refusal, _) = DataMatrix(230, p1 >> 8, p1 & 0xFF, p2 >> 8, p2 & 0xFF, 254);
        Assert.Null(refusal);
        Assert.Equal("AB\u001DCD", text);
    }

    /// <summary>The control: plain ASCII data is <c>]d1</c>, no GS1, no sequence.</summary>
    [Fact]
    public void PlainDataMatrix_IsModifier1()
    {
        var (text, _, info) = DataMatrix('H' + 1, 'i' + 1);
        Assert.Equal("Hi", text);
        Assert.Equal(1, info.Modifier);
        Assert.False(info.IsGs1);
        Assert.Equal(0, info.SequenceCount);
    }

    /// <summary>
    /// Structured append: the indicator 0x2D is position 3 (2 + 1 in the high nibble) of 4 (17 − 13
    /// in the low), followed by two file identification codewords; the data after them is read.
    /// </summary>
    [Fact]
    public void StructuredAppend_ReportsPositionAndCount()
    {
        var (text, refusal, info) = DataMatrix(233, 0x2D, 17, 42, 'O' + 1, 'K' + 1);
        Assert.Null(refusal);
        Assert.Equal("OK", text);
        Assert.Equal(3, info.SequencePosition);
        Assert.Equal(4, info.SequenceCount);
    }

    /// <summary>A sequence indicator naming a position past the count is refused, and says why.</summary>
    [Fact]
    public void StructuredAppend_PastItsCount_IsRefused()
    {
        var (text, refusal, _) = DataMatrix(233, 0x5D, 17, 42, 'O' + 1);   // position 6 of 4
        Assert.Null(text);
        Assert.Contains("position 6 of 4", refusal, StringComparison.Ordinal);
    }

    /// <summary>A function codeword legal only first, met later, is refused by that reason.</summary>
    [Fact]
    public void ReaderProgrammingAfterData_IsRefused()
    {
        var (text, refusal, _) = DataMatrix('A' + 1, 234);
        Assert.Null(text);
        Assert.Contains("only as the first codeword", refusal, StringComparison.Ordinal);

        var (first, _, info) = DataMatrix(234, 'A' + 1);
        Assert.Equal("A", first);
        Assert.True(info.ReaderProgramming);
    }

    /// <summary>The 05 macro stands for its header and trailer around the data.</summary>
    [Fact]
    public void Macro05_StandsForItsHeaderAndTrailer()
    {
        var (text, _, _) = DataMatrix(236, 'A' + 1);
        Assert.Equal("[)>\u001E05\u001DA\u001E\u0004", text);
        var (six, _, _) = DataMatrix(237, 'B' + 1);
        Assert.Equal("[)>\u001E06\u001DB\u001E\u0004", six);
    }

    /// <summary>
    /// An ECI's bytes are decoded in its set (ISO/IEC 16022 §5.4.4): "a" in the default ISO-8859-1,
    /// then ECI 26 and the bytes C3 A9 (each by Upper Shift) — UTF-8 for "é", where read as bytes
    /// they were "Ã©". The AIM modifier is 4, a symbol carrying an ECI.
    /// </summary>
    [Fact]
    public void AnEci_DecodesWhatFollowsInItsCharacterSet()
    {
        var (text, refusal, info) = DataMatrix('a' + 1, 241, 27, 235, 0xC3 - 127, 235, 0xA9 - 127);
        Assert.Null(refusal);
        Assert.Equal("a" + (char)0xE9, text);
        Assert.Equal(4, info.Modifier);
    }

    /// <summary>
    /// The four ISO-8859 parts no target framework carries (2026-10-20), read through the generated tables —
    /// the Unicode Consortium's mapping files: byte A1 is U+0104 in 8859-10 (ECI 12) and 8859-16 (18), U+0E01
    /// in 8859-11 (13), U+1E02 in 8859-14 (16); a byte below A0 is itself, ASCII 'A' here.
    /// </summary>
    [Theory]
    [InlineData(12, 0x0104)]
    [InlineData(13, 0x0E01)]
    [InlineData(16, 0x1E02)]
    [InlineData(18, 0x0104)]
    public void AnIso8859PartNoFrameworkCarries_IsDecodedByItsTable(int eci, int expected)
    {
        var (text, refusal, _) = DataMatrix(241, eci + 1, 'A' + 1, 235, 0xA1 - 127);
        Assert.Null(refusal);
        Assert.Equal("A" + (char)expected, text);
    }

    /// <summary>
    /// Each ECI holds until the next: UTF-8's C3 A9 is "é", then ECI 3 (ISO-8859-1) reads C3 as
    /// "Ã"; US-ASCII (ECI 27) reads its bytes as they are.
    /// </summary>
    [Fact]
    public void EachEci_HoldsUntilTheNext()
    {
        var (text, _, _) = DataMatrix(241, 27, 235, 0xC3 - 127, 235, 0xA9 - 127, 241, 4, 235, 0xC3 - 127);
        Assert.Equal(new string(new[] { (char)0xE9, (char)0xC3 }), text);

        var (ascii, refusal, _) = DataMatrix(241, 28, 'Z' + 1);
        Assert.Null(refusal);
        Assert.Equal("Z", ascii);
    }

    /// <summary>
    /// What cannot be decoded is refused with its reason: an ECI no assignment names (19 — Shift JIS
    /// until 2026-10-18 and ISO-8859-10 until 2026-10-20, the examples here before, are read now) by its
    /// number; bytes the set cannot hold (a lone C3 in UTF-8, a byte above 127 in US-ASCII, DB in
    /// ISO-8859-11, which defines no character there); and with the refusals, the acceptance above.
    /// </summary>
    [Fact]
    public void WhatAnEciCannotDecode_IsRefusedWithItsReason()
    {
        var (unassigned, refusal, _) = DataMatrix(241, 20, 'A' + 1);
        Assert.Null(unassigned);
        Assert.Contains("ECI 000019", refusal, StringComparison.Ordinal);

        var (thaiGap, thaiRefusal, _) = DataMatrix(241, 14, 235, 0xDB - 127);
        Assert.Null(thaiGap);
        Assert.Contains("names no character in ISO-8859-11", thaiRefusal, StringComparison.Ordinal);

        var (loneLead, utf8Refusal, _) = DataMatrix(241, 27, 235, 0xC3 - 127);
        Assert.Null(loneLead);
        Assert.Contains("not valid", utf8Refusal, StringComparison.Ordinal);

        var (high, asciiRefusal, _) = DataMatrix(241, 28, 235, 0xC3 - 127);
        Assert.Null(high);
        Assert.Contains("ECI 000027", asciiRefusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// A code-page ECI decodes strictly: Shift JIS (20) reads 93 FA as U+65E5, and refuses a lone lead
    /// byte 81 rather than replacing it.
    /// </summary>
    [Fact]
    public void ACodePageEci_DecodesStrictly()
    {
        var (sun, refusal, _) = DataMatrix(241, 21, 235, 0x93 - 127, 235, 0xFA - 127);
        Assert.Equal(((char)0x65E5).ToString(), sun);
        Assert.Null(refusal);

        var (lone, loneRefusal, _) = DataMatrix(241, 21, 235, 0x81 - 127);
        Assert.Null(lone);
        Assert.Contains("not valid", loneRefusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// GB18030 (ECI 32) is read as GB18030, not as GBK, its two-byte subset: 81 30 8B 38 is a
    /// four-byte sequence for U+0100, which GBK cannot hold.
    /// </summary>
    [Fact]
    public void Gb18030_ReadsItsFourByteSequences()
    {
        var (text, refusal, _) = DataMatrix(241, 33, 235, 0x81 - 127, 0x30 + 1, 235, 0x8B - 127, 0x38 + 1);
        Assert.Null(refusal);
        Assert.Equal(((char)0x0100).ToString(), text);
    }

    /// <summary>
    /// UTF-16BE (ECI 25) reads its bytes in pairs: 00 41 05 D0 is "A" and alef; an odd count is
    /// refused.
    /// </summary>
    [Fact]
    public void Utf16BigEndian_ReadsPairs()
    {
        // Byte 0 cannot be an ASCII codeword (0 ends the data here), so the pairs go through Base 256.
        var (text, refusal, _) = DataMatrix(241, 26, 231, Base256(4, 4), Base256(0x00, 5), Base256(0x41, 6), Base256(0x05, 7), Base256(0xD0, 8));
        Assert.Null(refusal);
        Assert.Equal("A" + (char)0x05D0, text);

        var (odd, oddRefusal, _) = DataMatrix(241, 26, 231, Base256(3, 4), Base256(0x00, 5), Base256(0x41, 6), Base256(0x05, 7));
        Assert.Null(odd);
        Assert.Contains("odd number of bytes", oddRefusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ECI number's three lengths (ISO/IEC 16022 §5.4.4.2) at their edges: one codeword up to
    /// 126, two from 127 to 16 382, three from 16 383.
    /// </summary>
    [Theory]
    [InlineData(new[] { 241, 127 }, 126)]
    [InlineData(new[] { 241, 128, 1 }, 127)]
    [InlineData(new[] { 241, 191, 254 }, 16382)]
    [InlineData(new[] { 241, 192, 1, 1 }, 16383)]
    public void TheEciNumber_IsReadInOneToThreeCodewords(int[] codewords, int eci)
    {
        int i = 0;
        Assert.True(DataMatrixGridReader.TryReadEciNumber(codewords, codewords.Length, ref i, out int read, out _));
        Assert.Equal(eci, read);
        Assert.Equal(codewords.Length - 1, i);
    }

    /// <summary>A GS1 symbol (FNC1 first) carrying an ECI is modifier 5.</summary>
    [Fact]
    public void AGs1SymbolWithAnEci_IsModifierFive()
    {
        var (text, _, info) = DataMatrix(232, 241, 27, '1' + 1);
        Assert.Equal("1", text);
        Assert.Equal(5, info.Modifier);
    }

    /// <summary>A Base 256 codeword as written: the value plus the 255-state pseudo-random step for its position (ISO/IEC 16022 Annex B.2).</summary>
    private static int Base256(int value, int position)
    {
        int pseudo = ((149 * position) % 255) + 1;
        int written = value + pseudo;
        return written <= 255 ? written : written - 256;
    }

    // ---------------------------------------------------------------- page fixture

    private static IReadOnlyList<BarcodeReadResult> ReadPage(string modules)
    {
        const float ModuleWidth = 2f, Height = 60f, Left = 40f, Bottom = 400f;
        var content = new StringBuilder("q 0 g\n");
        for (int i = 0; i < modules.Length; i++)
            if (modules[i] == '1')
                content.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2} {3} re f\n",
                    Left + i * ModuleWidth, Bottom, ModuleWidth, Height);
        content.Append("Q");

        string body = content.ToString();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            string.Format(CultureInfo.InvariantCulture,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 {0} 612 {1}] /Contents 4 0 R >>", Bottom - 40, Bottom + Height + 40),
            "<< /Length " + body.Length + " >>\nstream\n" + body + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString()), writable: false);
        return PdfBarcodeReader.Read(input);
    }
}
