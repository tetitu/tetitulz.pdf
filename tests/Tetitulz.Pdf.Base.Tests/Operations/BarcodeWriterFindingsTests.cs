// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What reading <c>Base/Operations/Barcodes/</c>' writer side found: an EAN-13 check digit encoded
/// without being checked, Unicode digits taken for ASCII ones, a quiet zone the 2-D symbols ignored,
/// and a capacity table transcribed beside the table it follows from.
/// </summary>
public class BarcodeWriterFindingsTests
{
    /// <summary>
    /// A supplied EAN-13 check digit is checked. Encoded as given, a wrong one made a symbol every
    /// scanner refuses, from a call that returned normally. The right one is the control.
    /// </summary>
    [Fact]
    public void AnEan13WithAWrongCheckDigitIsRefused_TheRightOneIsEncoded()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            PdfBarcodeWriter.GenerateContentStream("5901234123458", BarcodeType.Ean13, 0, 0));
        Assert.Contains("should be 7", ex.Message, StringComparison.Ordinal);

        string cs = PdfBarcodeWriter.GenerateContentStream("5901234123457", BarcodeType.Ean13, 0, 0);
        Assert.Contains("(5901234123457) Tj", cs, StringComparison.Ordinal);
    }

    /// <summary>
    /// Arabic-Indic digits are not ASCII digits. <c>char.IsDigit</c> took them, and the writer
    /// indexed its tables with <c>c - '0'</c> — a value in the thousands — and threw
    /// <see cref="IndexOutOfRangeException"/>. They are now simply not digits, so the input is
    /// refused for the reason it is wrong.
    /// </summary>
    [Fact]
    public void UnicodeDigitsAreNotEan13Digits()
    {
        var ex = Assert.Throws<ArgumentException>(() => PdfBarcodeWriter.GenerateContentStream(
            "\u0665\u0669\u0660\u0661\u0662\u0663\u0664\u0661\u0662\u0663\u0664\u0665", BarcodeType.Ean13, 0, 0));
        Assert.Contains("12 or 13 digits", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pair of Unicode digits is not a Data Matrix digit pair. It was encoded as one, as a codeword
    /// far outside 0–255 that failed inside Reed–Solomon; now each character is refused by name, as
    /// any other code point above U+00FF is.
    /// </summary>
    [Fact]
    public void UnicodeDigitsAreNotADataMatrixDigitPair()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            PdfBarcodeWriter.GenerateContentStream("\u0663\u0663", BarcodeType.DataMatrix, 0, 0));
        Assert.Contains("U+0663", ex.Message, StringComparison.Ordinal);

        // The control: ASCII digit pairs still encode, one codeword a pair.
        Assert.Contains("DataMatrix", PdfBarcodeWriter.GenerateContentStream("3333", BarcodeType.DataMatrix, 0, 0),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A 2-D symbol leaves the same quiet zone before it that a linear one does. It was drawn at
    /// <c>x</c> itself, so under <c>GeneratePdf</c> a QR met the page edge two modules out, where
    /// ISO/IEC 18004 asks for four.
    /// </summary>
    [Theory]
    [InlineData(BarcodeType.QrCode)]
    [InlineData(BarcodeType.DataMatrix)]
    public void AMatrixSymbolStartsAfterTheQuietZone(BarcodeType type)
    {
        float Leftmost(float quietZone)
        {
            string cs = PdfBarcodeWriter.GenerateContentStream("QUIET", type, 0, 0,
                new BarcodeOptions { QuietZone = quietZone });
            return Regex.Matches(cs, @"^(-?[\d.]+) -?[\d.]+ [\d.]+ [\d.]+ re\r?$", RegexOptions.Multiline)
                .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Min();
        }

        Assert.Equal(12f, Leftmost(12f), 3);
        Assert.Equal(0f, Leftmost(0f), 3);
    }

    /// <summary>
    /// The byte capacities come from the codeword table: ISO/IEC 18004 Table 11's ECC-M byte-mode
    /// column, pinned. And the boundary holds either side of version 10's.
    /// </summary>
    [Fact]
    public void QrByteCapacityIsTheStandardsColumn_AndTheLastByteFits()
    {
        Assert.Equal(new[] { 14, 26, 42, 62, 84, 106, 122, 152, 180, 213 },
            Enumerable.Range(1, 10).Select(QrLayout.ByteCapacity).ToArray());

        Assert.Equal(57, QrCodeEncoder.Encode(new string('a', 213)).GetLength(0));
        Assert.Throws<ArgumentException>(() => QrCodeEncoder.Encode(new string('a', 214)));
    }
}
