// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;
using F = Tetitulz.Pdf.Base.Tests.Operations.BarcodeReaderTests;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The decisions in the QR reader, the QR locator and <see cref="PdfBarcodeReader"/> that the
/// <c>Barcodes</c> sweep, batch 3, found nothing asserting.
/// </summary>
public class BarcodeReaderDecisionTests
{
    // ---------------------------------------------------------------- QR grids

    /// <summary>Data codewords for a version: <paramref name="head"/>, then zeros to the length the version needs.</summary>
    private static bool[,] QrWith(int version, params byte[] head)
    {
        var data = new byte[QrLayout.DataCodewords(version)];
        Array.Copy(head, data, head.Length);
        return QrCodeEncoder.EncodeCodewords(data, version);
    }

    /// <summary>Every version this writes reads back filled to its capacity — v8 and up have blocks of two lengths, the short ones first.</summary>
    [Fact]
    public void EveryVersion_FilledToCapacity_ReadsBack()
    {
        for (int version = QrLayout.MinVersion; version <= QrLayout.MaxVersion; version++)
        {
            string payload = new string((char)('a' + version), QrLayout.ByteCapacity(version));
            bool[,] grid = QrCodeEncoder.Encode(payload);
            Assert.Equal(QrLayout.SizeOf(version), grid.GetLength(0));
            Assert.Equal(payload, QrGridReader.Decode(grid, out string refusal));
            Assert.Null(refusal);
        }
    }

    /// <summary>A grid that is not square is not a QR symbol, even with a whole symbol in its left-hand columns.</summary>
    [Fact]
    public void ANonSquareGrid_IsNotRead()
    {
        bool[,] symbol = QrCodeEncoder.Encode("square");
        int size = symbol.GetLength(0);
        var wide = new bool[size, size + 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) wide[y, x] = symbol[y, x];

        Assert.Null(QrGridReader.Decode(wide, out string refusal));
        Assert.Null(refusal);
        Assert.Equal("square", QrGridReader.Decode(symbol, out _));
    }

    /// <summary>
    /// Numeric mode (indicator 0001) is read — "123": the count 3 in ten bits, the
    /// three digits in ten more — and a mode ISO/IEC 18004 does not assign (0110) is refused by name,
    /// not read as bytes.
    /// </summary>
    [Fact]
    public void ANumericModeSymbol_IsRead_AndAnUnassignedModeRefusedByName()
    {
        Assert.Equal("123", QrGridReader.Decode(QrWith(1, 0x10, 0x0C, 0x7B, 0x00), out string refusal));
        Assert.Null(refusal);

        Assert.Null(QrGridReader.Decode(QrWith(1, 0x60, 0x00), out string unassigned));
        Assert.Contains("mode 6", unassigned, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused by name, each: FNC1 in the second position (indicator 1001), whose application
    /// indicator two implementations read differently; an ECI no assignment names (19, the designator
    /// 0111 00010011 — 12, ISO-8859-10, is read).
    /// </summary>
    [Fact]
    public void Fnc1SecondAndAnUndecodedEci_AreRefusedByName()
    {
        Assert.Null(QrGridReader.Decode(QrWith(1, 0x90, 0x00), out string fnc1));
        Assert.Contains("second position", fnc1, StringComparison.Ordinal);

        Assert.Null(QrGridReader.Decode(QrWith(1, 0x71, 0x30, 0x00), out string eci));
        Assert.Contains("ECI 000019", eci, StringComparison.Ordinal);

        // A two-byte designator, 10000001 00101100: ECI 300, its high bits read.
        Assert.Null(QrGridReader.Decode(QrWith(1, 0x78, 0x12, 0xC0, 0x00), out string wide));
        Assert.Contains("ECI 000300", wide, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each half of the kanji and Hanzi value rule — the fixtures take one half each: a kanji value
    /// past 0x1F00 adds 0xC140 (0x1740 is Shift JIS E0 40, U+6F3E), a Hanzi value below 0x0A00 adds
    /// 0xA1A1 (0xCB is GB 2312 A3 AC, the full-width comma). Hanzi names its subset, and only GB 2312
    /// (1) is assigned: subset 2 is refused.
    /// </summary>
    [Fact]
    public void KanjiAndHanzi_BothHalvesOfTheValueRule_AndTheSubset()
    {
        Assert.Equal(((char)0x6F3E).ToString(), QrGridReader.Decode(QrWith(1, 0x80, 0x1B, 0xA0, 0x00), out string kanji));
        Assert.Null(kanji);
        Assert.Equal(((char)0xFF0C).ToString(), QrGridReader.Decode(QrWith(1, 0xD1, 0x01, 0x06, 0x58, 0x00), out string hanzi));
        Assert.Null(hanzi);

        Assert.Null(QrGridReader.Decode(QrWith(1, 0xD2, 0x01, 0x06, 0x58, 0x00), out string subset));
        Assert.Contains("subset 2", subset, StringComparison.Ordinal);
    }

    /// <summary>A byte segment of length zero holds nothing to return: not a symbol this reads, and not an empty string.</summary>
    [Fact]
    public void AnEmptyByteSegment_IsNotRead()
        => Assert.Null(QrGridReader.Decode(QrWith(1, 0x40, 0x00), out _));

    /// <summary>
    /// A length that runs past the data codewords — 16 bytes claimed in a version 1 symbol, whose 16
    /// data codewords also carry the mode and the count — is not read, and not read past the end.
    /// </summary>
    [Fact]
    public void ALengthRunningPastTheData_IsNotRead()
        => Assert.Null(QrGridReader.Decode(QrWith(1, 0x41, 0x00), out _));

    // ---------------------------------------------------------------- pages

    private static RenderedPage Render(string content)
    {
        using var input = new MemoryStream(F.OnePagePdf(content));
        return PdfOperations.RenderPageToPixels(input, 0, PdfBarcodeReader.DefaultDpi);
    }

    /// <summary>
    /// A sheet of twenty labels is read to the last label, for each 2-D symbology and for Data Matrix turned on
    /// the page. A locator bound counting every candidate it offered, read or not, with the reader decoding them
    /// afterwards, would stop early: the neighbouring labels' finders make squares of their own, and the sheet
    /// would read one QR symbol and eight Data Matrix ones.
    /// </summary>
    [Theory]
    [InlineData(BarcodeType.QrCode, 0)]
    [InlineData(BarcodeType.DataMatrix, 0)]
    [InlineData(BarcodeType.DataMatrix, 20)]
    public void ASheetOfLabels_IsReadToTheLastLabel(BarcodeType type, int degrees)
    {
        var (content, expected) = Sheet(type, degrees, 4, 5, 100f, 140, 150);
        using var input = new MemoryStream(F.OnePagePdf(content));
        var read = PdfBarcodeReader.Read(input).Where(r => r.Type == type).Select(r => r.Text).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(expected, read);
    }

    /// <summary>
    /// Each locator alone reads its sheet to the last label — the reader runs both Data Matrix locators, and the
    /// outline one reads a square symbol too, so only a locator by itself shows its own bound. QR on a sheet of
    /// 48 smaller labels on an equal pitch, whose neighbours' finders make squares as square as any label's own
    /// (56 candidates fail before the last label is read); square Data Matrix on twenty, whose data makes false Ls
    /// by the dozen; turned Data Matrix on 48. A QR locator stopping after sixteen candidates, or a Data Matrix one
    /// after eight, read or not, would fail them.
    /// </summary>
    [Theory]
    [InlineData(BarcodeType.QrCode, 0, 6, 8, 60f, 95)]
    [InlineData(BarcodeType.DataMatrix, 0, 4, 5, 100f, 145)]
    [InlineData(BarcodeType.DataMatrix, 20, 6, 8, 60f, 95)]
    public void EachLocatorAlone_ReadsASheetToTheLastLabel(BarcodeType type, int degrees, int columns, int rows, float size, int pitch)
    {
        var (content, expected) = Sheet(type, degrees, columns, rows, size, pitch, pitch, prefix: "LABEL-");
        RenderedPage page = Render(content);
        int threshold = BarcodeScanline.Threshold(page.Pixels, page.Width, page.Height, page.Stride);
        var read = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
        bool Accept((bool[,] Grid, int Left, int Top, int Right, int Bottom) candidate)
        {
            string text = type == BarcodeType.QrCode ? QrGridReader.Decode(candidate.Grid, out _) : DataMatrixGridReader.Decode(candidate.Grid, out _);
            if (text == null) return false;
            read.Add(text);
            return true;
        }
        if (type == BarcodeType.QrCode) QrLocator.Locate(page.Pixels, page.Width, page.Height, page.Stride, threshold, Accept);
        else if (degrees == 0) DataMatrixLocator.Locate(page.Pixels, page.Width, page.Height, page.Stride, threshold, Accept);
        else DataMatrixTurnedLocator.Locate(BarcodeScanline.DarkMap(page.Pixels, page.Width, page.Height, page.Stride, threshold), page.Width, page.Height, Accept);
        Assert.Equal(expected, read);
    }

    /// <summary>A page of <paramref name="columns"/> by <paramref name="rows"/> labels, each turned by <paramref name="degrees"/> about its corner, and their texts in order.</summary>
    private static (string Content, string[] Texts) Sheet(BarcodeType type, int degrees, int columns, int rows, float size, int dx, int dy,
        string prefix = null)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        var content = new StringBuilder();
        var texts = new System.Collections.Generic.List<string>();
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                string text = (prefix ?? $"LABEL-{type}-") + texts.Count.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
                texts.Add(text);
                content.Append(FormattableString.Invariant(
                    $"q {cos:0.####} {sin:0.####} {-sin:0.####} {cos:0.####} {40 + (column * dx)} {40 + (row * dy)} cm\n"));
                content.Append(PdfBarcodeWriter.GenerateContentStream(text, type, 0, 0,
                    new BarcodeOptions { Size = size, ShowText = false }));
                content.Append("Q\n");
            }
        texts.Sort(StringComparer.Ordinal);
        return (content.ToString(), texts.ToArray());
    }
    private static int FinderCandidates(RenderedPage page)
        => QrLocator.CountFinderCandidates(page.Pixels, page.Width, page.Height, page.Stride,
               BarcodeScanline.Threshold(page.Pixels, page.Width, page.Height, page.Stride));

    private static string Qr(string text, float x, float y)
        => PdfBarcodeWriter.GenerateContentStream(text, BarcodeType.QrCode, x, y,
               new BarcodeOptions { Size = 120f, ShowText = false });

    /// <summary>
    /// The finder search's own evidence (<see cref="QrLocator.CountFinderCandidates"/>): a page with
    /// one QR symbol yields its three finders, each once however many scan rows cross it; a page of
    /// text yields almost none, because a horizontal 1:1:3:1:1 must be confirmed vertically. The
    /// candidates are paired three at a time, so this count is what bounds the work, and no answer
    /// can show it — the decoder rejects false triples either way.
    /// </summary>
    [Fact]
    public void TheFinderSearch_FindsEachFinderOnce_AndAlmostNothingInText()
    {
        Assert.Equal(3, FinderCandidates(Render(Qr("one", 100, 400))));
        Assert.InRange(FinderCandidates(Render(DenseText())), 0, MaxTextCandidates);
    }

    private const int MaxTextCandidates = 2;

    private static string DenseText()
    {
        var content = new StringBuilder();
        for (int line = 0; line < 40; line++)
            content.AppendLine($"BT /F1 11 Tf 40 {740 - line * 18} Td (illillill milli lIlIlI 111 iii lll Hill Mill Bill Till illuminated fulfilling) Tj ET");
        return content.ToString();
    }

    /// <summary>Two identical symbols on a page are one result: results are keyed by type and payload — QR and Data Matrix alike.</summary>
    [Theory]
    [InlineData(BarcodeType.QrCode)]
    [InlineData(BarcodeType.DataMatrix)]
    public void TwoIdenticalSymbols_AreOneResult(BarcodeType type)
    {
        string Symbol(float x) => PdfBarcodeWriter.GenerateContentStream("twice", type, x, 400,
            new BarcodeOptions { Size = 120f, ShowText = false });

        var found = PdfBarcodeReader.ReadPixels(Render(Symbol(60) + Symbol(360)));
        var result = Assert.Single(found);
        Assert.Equal((type, "twice"), (result.Type, result.Text));
    }

    /// <summary>
    /// Of two refusals the first is kept — the symbol higher on the page, scanned first: two EAN-13
    /// symbols each failing its check digit, the upper one named.
    /// </summary>
    [Fact]
    public void OfTwoRefusals_TheFirstIsKept()
    {
        string Ean(string code, float bottom)
        {
            string modules = F.Ean13Modules(code);
            var sb = new StringBuilder();
            for (int i = 0; i < modules.Length; i++)
                if (modules[i] == '1')
                    sb.AppendLine(FormattableString.Invariant($"{100 + i * 2} {bottom} 2 50 re f"));
            return sb.ToString();
        }

        PdfBarcodeReader.ReadPixels(Render(Ean("5901234123450", 600) + Ean("4006381333930", 300)));
        Assert.Contains("5901234123450", PdfBarcodeReader.LastRefusal);
    }
}
