// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// QR codes another tool made: every version and error-correction level, and the modes a text
/// reader meets. The symbols below were made by an independent encoder (Python, an encoder this repository did
/// not write) and are checked in as their module rows, one hex string a row, no quiet zone — so the
/// reader is held to an independent encoder's output, not to round trips through its own, and
/// reads more than this library writes (version 1–10, level M, byte mode).
/// </summary>
public class QrThirdPartySymbolTests
{
    [Theory]
    [InlineData("NumericL", "01234567")]
    [InlineData("AlnumQ", "HELLO WORLD 123")]
    [InlineData("ByteH", "Hello, World")]
    public void EachLevel_AndTheTextModes_AreRead(string fixture, string expected)
    {
        Assert.Equal(expected, QrGridReader.Decode(Grid(Fixture(fixture)), out string refusal, out BarcodeSymbolInfo info));
        Assert.Null(refusal);
        Assert.Equal(1, info.Modifier);
    }

    /// <summary>
    /// Past version 10 the blocks split unevenly and the byte count takes 16 bits (version 15, 77
    /// modules); past 26 the numeric count takes 14 (version 27, 125 modules, 1500 digits).
    /// </summary>
    [Fact]
    public void LargeVersions_AreRead_WithEachCountWidth()
    {
        Assert.Equal(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 4)),
                     QrGridReader.Decode(Grid(V15), out _));
        Assert.Equal(string.Concat(Enumerable.Repeat("31415926535897932384626433832795028841971693993751", 30)),
                     QrGridReader.Decode(Grid(V27num), out _));
    }

    /// <summary>An ECI names the byte data's set — UTF-8 (26) for Latin and Hebrew, ISO-8859-1 (3) — and the modifier is 2.</summary>
    [Fact]
    public void AnEci_DecodesTheBytesInItsSet()
    {
        Assert.Equal("Gr" + (char)0xFC + (char)0xDF + "e " + new string(new[] { (char)0x05E9, (char)0x05DC, (char)0x05D5, (char)0x05DD }),
                     QrGridReader.Decode(Grid(EciUtf8), out _, out BarcodeSymbolInfo info));
        Assert.Equal(2, info.Modifier);
        Assert.Equal("Gr" + (char)0xFC + (char)0xDF + "e", QrGridReader.Decode(Grid(EciLatin1), out _));
    }

    /// <summary>A structured-append pair names each symbol's place in the message; the two texts make the whole.</summary>
    [Fact]
    public void AStructuredAppendPair_NamesEachSymbolsPlace()
    {
        string first = QrGridReader.Decode(Grid(Sa1), out _, out BarcodeSymbolInfo one);
        string second = QrGridReader.Decode(Grid(Sa2), out _, out BarcodeSymbolInfo two);

        Assert.Equal("Structured append splits this text across two symbols.", first + second);
        Assert.Equal((1, 2), (one.SequencePosition, one.SequenceCount));
        Assert.Equal((2, 2), (two.SequencePosition, two.SequenceCount));
    }

    /// <summary>
    /// Kanji mode (Shift JIS, ISO/IEC 18004 §7.4.6) and Hanzi mode (GB 2312, GB/T 18284) are read —
    /// an independent encoder's symbols, whose text an independent reader reads the same.
    /// </summary>
    [Fact]
    public void KanjiAndHanziModes_AreRead()
    {
        Assert.Equal("\u65E5\u672C\u8A9E\u306E\u6587\u5B57", QrGridReader.Decode(Grid(Kanji2), out string refusal));
        Assert.Null(refusal);
        Assert.Equal("\u6C49\u5B57\u7F16\u7801", QrGridReader.Decode(Grid(Hanzi), out refusal));
        Assert.Null(refusal);
    }

    /// <summary>
    /// An ECI names a code page — Shift JIS (20), ISO-8859-8 Hebrew (10), Windows-1256 Arabic (24),
    /// Big5 (28), EUC-KR (30), GB18030 (32), ISO-8859-5 (7) — read through the code-page provider, the
    /// modifier 2. An independent encoder wrote the designators; an independent reader reads the same texts.
    /// </summary>
    [Theory]
    [InlineData("EciShiftJis")]
    [InlineData("EciHebrew")]
    [InlineData("EciArabic")]
    [InlineData("EciBig5")]
    [InlineData("EciEucKr")]
    [InlineData("EciGb18030")]
    [InlineData("EciCyrillic")]
    public void AnEciCodePage_IsReadThroughTheProvider(string fixture)
    {
        Assert.Equal(EciTexts[fixture], QrGridReader.Decode(Grid(Fixture(fixture)), out string refusal, out BarcodeSymbolInfo info));
        Assert.Null(refusal);
        Assert.Equal(2, info.Modifier);
    }

    /// <summary>
    /// ISO-8859-10 (ECI 12), which no target framework carries a code page for, read through the generated tables:
    /// an independent encoder's symbol, the text an independent reader reads from it, U+0100
    /// U+0112 U+0122 — bytes C0, A2, A3 in ISO-8859-10.
    /// </summary>
    [Fact]
    public void AnIso8859_10Symbol_IsReadAsAnIndependentReaderReadsIt()
    {
        Assert.Equal("\u0100\u0112\u0122", QrGridReader.Decode(Grid(EciLatin6), out string refusal));
        Assert.Null(refusal);
    }

    private static readonly Dictionary<string, string> EciTexts = new()
    {
        ["EciShiftJis"] = "\u30C6\u30B9\u30C8\u3067\u3059",
        ["EciHebrew"] = "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD",
        ["EciArabic"] = "\u0645\u0631\u062D\u0628\u0627",
        ["EciBig5"] = "\u7E41\u9AD4\u4E2D\u6587",
        ["EciEucKr"] = "\uD55C\uAD6D\uC5B4",
        ["EciGb18030"] = "\u4E2D\u6587\u6587\u672C",
        ["EciCyrillic"] = "\u041F\u0440\u0438\u0432\u0435\u0442",
    };

    /// <summary>
    /// The format field is read to its nearest code within three bits (BCH(15,5)'s reach): two
    /// errors in each copy still read; four in each read as no symbol.
    /// </summary>
    [Fact]
    public void TheFormatField_IsCorrectedWithinThreeBits()
    {
        bool[,] two = Grid(NumericL);
        FlipFormatBits(two, 0, 9);
        Assert.Equal("01234567", QrGridReader.Decode(two, out _));

        bool[,] four = Grid(NumericL);
        FlipFormatBits(four, 0, 3, 9, 13);
        Assert.Null(QrGridReader.Decode(four, out _));
    }

    /// <summary>
    /// The ceiling itself: four flips chosen so the read is exactly four bits from every one of the
    /// 32 codes (a code's nearest neighbour is seven away, so some four-flip reads land within three
    /// of another code and say nothing about the ceiling) — no symbol, and no refusal.
    /// </summary>
    [Fact]
    public void AFormatReadFourFromEveryCode_IsNoSymbol()
    {
        bool[,] grid = Grid(NumericL);
        int size = grid.GetLength(0), read = 0;
        for (int bit = 0; bit < 15; bit++) { var (y, x) = QrLayout.FormatPositionA(bit); if (grid[y, x]) read |= 1 << bit; }

        int[] flips = null;
        for (int a = 0; a < 15 && flips == null; a++)
            for (int b = a + 1; b < 15 && flips == null; b++)
                for (int c = b + 1; c < 15 && flips == null; c++)
                    for (int d = c + 1; d < 15 && flips == null; d++)
                    {
                        int corrupted = read ^ (1 << a) ^ (1 << b) ^ (1 << c) ^ (1 << d), nearest = 15;
                        for (int level = 0; level < 4; level++)
                            for (int m = 0; m < 8; m++)
                                nearest = Math.Min(nearest, Popcount(QrLayout.FormatCode(level, m) ^ corrupted));
                        if (nearest == 4) flips = new[] { a, b, c, d };
                    }
        Assert.NotNull(flips);

        FlipFormatBits(grid, flips);
        Assert.Null(QrGridReader.Decode(grid, out string refusal));
        Assert.Null(refusal);
    }

    private static int Popcount(int v) { int n = 0; for (; v != 0; v &= v - 1) n++; return n; }

    /// <summary>
    /// GS1 (FNC1 first, ISO/IEC 18004): an alphanumeric "01%10%%" reads "01", GS, "10%", and the
    /// modifier is 3. Built here — an independent encoder writes no FNC1 — as the bit stream two independent readers read.
    /// </summary>
    [Fact]
    public void Gs1_ReadsPercentAsTheSeparator()
    {
        var bits = new List<int>();
        void Put(int value, int width) { for (int b = width - 1; b >= 0; b--) bits.Add((value >> b) & 1); }
        const string Alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
        string data = "01%10%%";
        Put(0b0101, 4);                       // FNC1 in the first position
        Put(0b0010, 4); Put(data.Length, 9);  // alphanumeric, its count
        for (int i = 0; i + 1 < data.Length; i += 2) Put(45 * Alnum.IndexOf(data[i]) + Alnum.IndexOf(data[i + 1]), 11);
        if (data.Length % 2 == 1) Put(Alnum.IndexOf(data[data.Length - 1]), 6);
        Put(0, 4);                            // terminator

        int capacity = QrLayout.DataCodewords(1);
        var codewords = new byte[capacity];
        int n = 0;
        for (; n * 8 < bits.Count; n++)
            for (int b = 0; b < 8; b++) codewords[n] |= (byte)((n * 8 + b < bits.Count ? bits[n * 8 + b] : 0) << (7 - b));
        for (int pad = 0; n < capacity; n++, pad++) codewords[n] = (byte)(pad % 2 == 0 ? 0xEC : 0x11);

        string text = QrGridReader.Decode(QrCodeEncoder.EncodeCodewords(codewords, 1), out string refusal, out BarcodeSymbolInfo info);
        Assert.Null(refusal);
        Assert.Equal("01\u001D10%", text);
        Assert.True(info.IsGs1);
        Assert.Equal(3, info.Modifier);
    }

    /// <summary>
    /// The derived layout against the two implementations' table, at points each kind of row passes
    /// through: version 1-L's one block of 19, 5-Q's two of 15 and two of 16, 40-H's 20 of 15 and
    /// 61 of 16; and alignment centres for versions 2, 7 and 32 (the row whose step is 26).
    /// </summary>
    [Fact]
    public void TheDerivedLayout_MatchesTheTable()
    {
        Assert.Equal(new[] { 19 }, QrLayout.BlockSizes(1, 0b01));
        Assert.Equal(new[] { 15, 15, 16, 16 }, QrLayout.BlockSizes(5, 0b11));
        int[] h40 = QrLayout.BlockSizes(40, 0b10);
        Assert.Equal(20, h40.Count(s => s == 15));
        Assert.Equal(61, h40.Count(s => s == 16));
        Assert.Equal(30, QrLayout.EcPerBlock(40, 0b10));

        Assert.Equal(new[] { 6, 18 }, QrLayout.AlignmentPositions(2));
        Assert.Equal(new[] { 6, 22, 38 }, QrLayout.AlignmentPositions(7));
        Assert.Equal(new[] { 6, 34, 60, 86, 112, 138 }, QrLayout.AlignmentPositions(32));
    }

    // ---------------------------------------------------------------- helpers

    private static void FlipFormatBits(bool[,] grid, params int[] bits)
    {
        int size = grid.GetLength(0);
        foreach (int bit in bits)
        {
            var (ay, ax) = QrLayout.FormatPositionA(bit);
            var (by, bx) = QrLayout.FormatPositionB(bit, size);
            grid[ay, ax] = !grid[ay, ax];
            grid[by, bx] = !grid[by, bx];
        }
    }

    private static string[] Fixture(string name) => name switch
    {
        "EciShiftJis" => EciShiftJis,
        "EciHebrew" => EciHebrew,
        "EciArabic" => EciArabic,
        "EciBig5" => EciBig5,
        "EciEucKr" => EciEucKr,
        "EciGb18030" => EciGb18030,
        "EciCyrillic" => EciCyrillic,
        "NumericL" => NumericL,
        "AlnumQ" => AlnumQ,
        "ByteH" => ByteH,
        _ => throw new ArgumentException(name),
    };

    /// <summary>A grid from hex rows: each row's last <c>size</c> bits, the first the leftmost module; true is dark.</summary>
    internal static bool[,] Grid(string[] rows)
    {
        int size = rows.Length;
        var grid = new bool[size, size];
        for (int y = 0; y < size; y++)
        {
            string bits = string.Concat(rows[y].Select(h => Convert.ToString(Convert.ToInt32(h.ToString(), 16), 2).PadLeft(4, '0')));
            bits = bits.Substring(bits.Length - size);
            for (int x = 0; x < size; x++) grid[y, x] = bits[x] == '1';
        }
        return grid;
    }

    // ---------------------------------------------------------------- an independent encoder's symbols
    private static readonly string[] NumericL =
    {
        "1fd97f",
        "104941",
        "17555d",
        "17525d",
        "175c5d",
        "104041",
        "1fd57f",
        "000c00",
        "1e549d",
        "1e312c",
        "01f9f2",
        "19358a",
        "126690",
        "0018a1",
        "1fccd6",
        "1043c5",
        "174a41",
        "175096",
        "175294",
        "105b5b",
        "1fdb22",
    };

    private static readonly string[] AlnumQ =
    {
        "1fc47f",
        "104841",
        "174a5d",
        "17515d",
        "17445d",
        "105e41",
        "1fd57f",
        "001500",
        "0c4e68",
        "1d84cb",
        "0cf352",
        "09b144",
        "0efe9f",
        "00192f",
        "1fc1c6",
        "104e43",
        "174515",
        "1748c8",
        "175bc3",
        "105c21",
        "1fcb0b",
    };

    private static readonly string[] ByteH =
    {
        "1fc0c7f",
        "105f141",
        "1754d5d",
        "175345d",
        "175085d",
        "1053241",
        "1fd557f",
        "0008c00",
        "04f79be",
        "0e004ed",
        "01dfccd",
        "033127a",
        "10f0569",
        "0506d6d",
        "1872d0d",
        "033af28",
        "1876dfa",
        "001b11c",
        "1fd5351",
        "1056910",
        "174aff8",
        "17464ac",
        "1759b9f",
        "1041080",
        "1fca7f9",
    };

    internal static readonly string[] V15 =
    {
        "1fccc1c16b0e060f587f",
        "1044017dc1ab7352cd41",
        "175ea6764b03260a915d",
        "175c8c9bf126e398a95d",
        "175cb07f5b87fe4e3f5d",
        "105c8d11f1e34796f441",
        "1fd5555555555555557f",
        "001a6c515bca46cdde00",
        "17c0079fe1e07f9cf97c",
        "04347d494b861609ae1e",
        "13440a25c1e0c29463b5",
        "139eee4759c617818e1f",
        "0ff4449fcaac40d4fab1",
        "04bcf6595086250d8d1e",
        "12c00807ceecd0d451b5",
        "149eae45584a3d41ad1f",
        "0ef5c4bdcb205b54fbb1",
        "01bef64a630a3001ac1f",
        "164e0835e764de8c72a4",
        "141e2e56b42aa501a63f",
        "09fbc4bd2790be44f310",
        "05b6b66a913ab041a11b",
        "1041082755945e5c7b80",
        "13912e3784dad9f1aefa",
        "19fde4bf3140fe84f7f8",
        "051886b190dac6c1a71b",
        "0b5a08357514d60d7950",
        "1f143e7188f8c5f2251a",
        "0ff4e47f31437e9539f5",
        "10b5b62d9bc9eee02253",
        "1d7a10ca7307a60cffd8",
        "100737a785f98ff02e8a",
        "1fd4cd273e420c8576de",
        "09b7966d94ebe9e160b5",
        "0779270a7a26940cfe83",
        "12a619ef85ffcbfa940a",
        "0757cb273e62108536d6",
        "19a5996194ffc1cd2468",
        "1748bb4e7a235405eaba",
        "0ab4346305e5c3da1408",
        "1f4722affe565ca8e6fe",
        "19954c2514fe8dfb7010",
        "07683b46fa1a9825b68a",
        "12b4a96f25a507dab010",
        "0f47ccef8e1698ad26ea",
        "1985882464bc0d3f6040",
        "17f8fd7fca50fca5b7fa",
        "131cca5115a4c7101110",
        "075709f50e57d4ab255a",
        "19194131b4bd453a6110",
        "17f8fa5f4a53fda727fa",
        "130c057d77289a5850e0",
        "06fb0ffae913c767355a",
        "190dcb37a3b99a7e40a0",
        "13dc3a642dd7cea3254a",
        "1314074a9224995870e0",
        "00da0fcdeb1fc72b045a",
        "1b048b3380b59a3e53a1",
        "13f43a769c5fddab0448",
        "1704c773f0c489ec78e1",
        "0669cfe36d6ff68b0759",
        "1ea08b21e2c5a8fe53a9",
        "11687aee8d2ffe2b0940",
        "120dd763ea34a86c74ea",
        "10e6eff34b8fd65b0056",
        "15aa9ba0e335111e53af",
        "09e84a9e878fd44b0c4c",
        "0103c7c3e416243e7dea",
        "0f61cf7f4f8cfe5989f6",
        "0011bb11e536460d1717",
        "1fcb621588bcd579cd56",
        "1053f291eb37463ff718",
        "1753e6df41aeff4905ff",
        "17509d91eb170e2e544a",
        "175b4de081af6b4bc89a",
        "1040e09deb1d0e271f82",
        "1fd3c250418ee7446294",
    };

    private static readonly string[] V27num =
    {
        "1fdfda340abf4c36b0642fbf6401e37f",
        "10539e5c9405bd7f29df7d25febf2441",
        "175ed5d883008a7e08d92a30ebb0525d",
        "17549e8eae2a33dcca339bdacd5c605d",
        "17406087ff7f7089ff646b9f9809065d",
        "1054b4ee71c6f99115dfaf5112800e41",
        "1fd5555555555555555555555555557f",
        "000ea2f73182d5c710d922f153b4a100",
        "19c9149ebf14d815f90daf5f4e92472f",
        "0390620c3bbf7f3e93a4393fe43827f5",
        "1bc3d99f6df9118ff6d35628615dfeaf",
        "12b581f08403cb71384ba1421b978204",
        "1acad155d2dbcea929dcb0a6c2529b1a",
        "05ab0c433b716486bb660a8de8f93d10",
        "066149f36f2713e7c20144582cbf5854",
        "059aa4f485a8d84d38e8beb15317f4ff",
        "0b4bddd5d237fd592110a3aa8ba028ac",
        "13b927737a4e773abfab0902e82a8e51",
        "11e19d572e5b21afffdd6e97e75cf317",
        "17990064c6b2db09167085fa1386063d",
        "0bc6513560e80e9dd6dd9025c55f1baf",
        "13b98763da31a43e73a53a0faaf5bd93",
        "0965d0072cf6c36711d14edb3bb2d815",
        "062e38045698688db7fbb5b0c30d3c3d",
        "134f7eddbffcfd972472602baae024af",
        "0a1dac47f527573badd8ca819a2a8e5f",
        "0cd1dfa2d22e2368fd1faed7e30cdb1b",
        "0fb536f80d9cd88305400538ccb630b1",
        "01d670af60690191d845902a0e6fe4f4",
        "1709ed87aa1fab3977533aadb7854e50",
        "0a64a2537cbccf4c1e2d6ec2f0221b14",
        "098c052b26f760a4b657853457cdb0be",
        "1dfd705dbf9efda3f9ce5c3f0b9825ff",
        "111e0ce711cc97091228f691aa368d1e",
        "0f50a0e25536c36f540ba1d5c5ced95b",
        "1114343bb1c46887186c0e114ffd3311",
        "11fd5c1c9fd93151f9b1913f2260d9f4",
        "02b2fa840f73dbdb211f7bb5988e7178",
        "00f692334a108faff6ebadf0cfee25e4",
        "1dbe0a09a2b7a10478d406205041bb52",
        "0a445633a2eef5b2390d9cd42118d7eb",
        "0f0522aa08441e2ad3ab774ebbb25f32",
        "13f1b71f51344c5d44cb1027ed2a5a47",
        "07a529008a83a7941b644d8653cd6321",
        "0c416f4ea6da3f4d4a39c11fc250e984",
        "0fb90b04387011f7609b159568f66026",
        "1766f1b34114baa0b6cbb4c40fa4c6ff",
        "02bebb089abb950f1860a200e004cb6d",
        "13f164f156ea2df3c531e0fc315a450c",
        "133c222bc040093aaf9b804a9bf1cc86",
        "14f4d41db914d12db8ce760fefa311c7",
        "020f39830ebb6d069360e0b4500a537d",
        "0ff83442a22a091c4a333c90cd5de43c",
        "1f29000834809235e09af48a67f706b4",
        "10c3d98f45d5fd02f4cf763f03a1a5e7",
        "1c3f3310953944ff5b6361a4e82d975e",
        "15e200dd57eb282386f168e4b16ffd0f",
        "161d9e27ca439a8eec5afefa1bf5dda5",
        "146b1801a015f3a9b83c8ccfcf835f03",
        "029fb19b14ba5c4a03c10ab4201fb2ab",
        "0ff4c892df2711d5fa00775f3dbf23f8",
        "1f1e98e8b18cbb33109bd5b19715c11c",
        "115c1d4d35c9dd0f50fdaa3540713959",
        "1d1272c3b11163b71b4305f12a8f0d10",
        "15f543ae5f1812e9fadc5c5f7f4cdbf6",
        "0fbdd704713388cb5c67f6c450f470fc",
        "085fdd61b1c6cf8e1a1080058a8219e8",
        "0d023ebbda4860f4b7ca3eefea0db3e1",
        "08c98f628f65f16dfb406ffbb1a0eda4",
        "07af5b78322c5bc76c9bc5445b1a4b0d",
        "19e064e9b1882d862a7fa0867e4c365a",
        "04265b4f9ae2d33cbb500eefe5868800",
        "196187a68f60026df22d9ff8779fdb55",
        "07a140f0a0fda8cb5af7354758d570f3",
        "02576ae95060cd8e16b1400bdb6235e5",
        "1f3e50e6c96f6330a3bbbea2218d8bcc",
        "1ae90742de0efe6dc0ae6ff5cdd019de",
        "1f2fc23f836854c562d4c54c42fab3be",
        "0452b8b420422182c40780072a8cf66b",
        "1c35f9997ad8db2c40ac3eafc4b608c1",
        "036114b65d79824e1531a3f8c1e3d914",
        "06a121bce26fe8c6cc5f09457b097370",
        "1b4618fabfd86d921b654d2b27ac0664",
        "059db83535f3130c901fb4a1f48a983e",
        "04e11edc43264e9ee5c2aed5cd57259f",
        "0e99d0a6f98824154c2c447e47b5bf26",
        "1cec805b80fee040d84140111188ca0f",
        "0930f08c1a0c1aaf73ab3f95cb72302d",
        "1bf02ef55f19836ff6f2e01ffee3ebf4",
        "011620a2d1b72bd71c5c489154493318",
        "095c1c7795d228b15a29bfd503c8575c",
        "111097bc313c9f0f10dfa611e8baeb16",
        "19f17ddd7f250581f5c652df3da387f3",
        "04a2c17efd8fed280f68074797090ca8",
        "0d64f25d8dfa7e5f0821352fe140986c",
        "10bba2470f00b8a7a3d378ad5bb15126",
        "12f02e3eaa15276dbace5420ceaaf4cb",
        "052fb0ec13bf5965606466816400409c",
        "11f1dcff76fac8c2362115c0014fa1c8",
        "030cc7b4b4002bfd98db7c2eebb9b09e",
        "0f4ff1d571d5d96145cd855b32a1cf2f",
        "1881e173eb7f5848cf65152f980be437",
        "0d51da798a3a3aae4a230cf7ed6cc60d",
        "0305527b0c82f506e0d9467553a4ced9",
        "15f05a26a916db54390da3980e9063ec",
        "02b9d4d01bbd50bf43a51169a439495d",
        "10ef8cfb72fa31c106d272c0615e7701",
        "033d1bbcb003cff96849a5c2dba780d3",
        "0fd0f52579dbde2575cdb80b82411cfa",
        "189505936771644483540655e8ebba40",
        "0cdfdf2b462733aeca224cb42ebdd524",
        "033b54389c8af80220e9b2fa521633ee",
        "1de3dbb5fd46eddcbd10a3df028221fe",
        "1aa692f3af2c57ff4fbb0965e0398b50",
        "09df070b864b21a9cacc6e806c5cfd04",
        "15ba18ccdc73db00e851850a5a9604ee",
        "10577375ffc90e91f35d905fc56f13fe",
        "0018861fb191a4371ca43a1161c5b910",
        "1fc3677bd507c36d53814ef52292dd54",
        "105bf53cf1196887111bb591af2d3d1c",
        "17574ae1ffb5fd9bf702601f82c027fe",
        "1746b51bbe68573ab848ca8f743a8f1e",
        "1743628bee96236eef4faeda6c0cdb5b",
        "105af6c3c470d88d0d100530e3b631f1",
        "1fd78809527501903d299025352fe4a4",
    };

    private static readonly string[] EciUtf8 =
    {
        "1fd5a7f",
        "1057f41",
        "1751b5d",
        "174f65d",
        "1755d5d",
        "104ec41",
        "1fd557f",
        "0009500",
        "13f5c97",
        "09b345e",
        "1a72705",
        "0b39547",
        "0f62c59",
        "169d39d",
        "1ce863d",
        "17adc37",
        "13e71f7",
        "001e113",
        "1fd1f51",
        "105eb1c",
        "17529fc",
        "175db2b",
        "174ab6b",
        "104ba24",
        "1fdb961",
    };

    private static readonly string[] EciLatin1 =
    {
        "1fcf7f",
        "105241",
        "17485d",
        "174f5d",
        "17575d",
        "104241",
        "1fd57f",
        "000000",
        "154512",
        "02aea9",
        "1362e3",
        "149fb5",
        "08caeb",
        "001c66",
        "1fcd17",
        "104064",
        "17595f",
        "174ea6",
        "175ad9",
        "104782",
        "1fd6f3",
    };

    private static readonly string[] Sa1 =
    {
        "1fd5077f",
        "10461a41",
        "1744b35d",
        "17517d5d",
        "1753d55d",
        "105cea41",
        "1fd5557f",
        "001c4300",
        "1174f7f9",
        "0b0e0337",
        "0ee9e4ff",
        "179abe89",
        "1870833e",
        "113ad637",
        "0367147e",
        "05894f81",
        "14c2ff08",
        "180e0094",
        "06f0e25d",
        "068ebb09",
        "1e7887f7",
        "001cd515",
        "1fd31557",
        "10444f11",
        "175ee3f0",
        "17408634",
        "174ab6cf",
        "1048f058",
        "1fd347fc",
    };

    private static readonly string[] Sa2 =
    {
        "1fd2017f",
        "104e1a41",
        "1740b75d",
        "175f785d",
        "175fd35d",
        "1050eb41",
        "1fd5557f",
        "00184200",
        "1174f2f9",
        "101500d6",
        "0d75e67e",
        "00aebb02",
        "1d788e32",
        "133cd45f",
        "1d641576",
        "14a54381",
        "1549fbdc",
        "183802d4",
        "00cce57d",
        "0335b329",
        "1bd183ff",
        "0013d715",
        "1fd91557",
        "104f4d19",
        "175deff4",
        "174b8216",
        "1741b00f",
        "1044b354",
        "1fde8bfc",
    };

    // ---------------------------------------------------------------- an independent encoder, kanji, Hanzi and code-page ECIs
    private static readonly string[] Kanji2 =
    {
        "1fc87f",
        "105941",
        "175f5d",
        "17445d",
        "17425d",
        "104941",
        "1fd57f",
        "000500",
        "0ec306",
        "12bc56",
        "15fa96",
        "11aaac",
        "19ea48",
        "001906",
        "1fc8a4",
        "105ea9",
        "174d23",
        "1758f2",
        "17562c",
        "105b45",
        "1fc7bb",
    };    private static readonly string[] Hanzi =
    {
        "1fd27f",
        "105441",
        "17415d",
        "175a5d",
        "175b5d",
        "105c41",
        "1fd57f",
        "000b00",
        "02423b",
        "1c14e6",
        "1ee8c4",
        "1bab3c",
        "1177c5",
        "001ebc",
        "1fc0aa",
        "104b32",
        "17465f",
        "175179",
        "1746a5",
        "104f8e",
        "1fcd4f",
    };    private static readonly string[] EciShiftJis =
    {
        "1fc37f",
        "104941",
        "17465d",
        "175e5d",
        "174a5d",
        "105e41",
        "1fd57f",
        "001b00",
        "0c4468",
        "171275",
        "1ac5de",
        "082a10",
        "15d693",
        "0017f8",
        "1fc8f9",
        "104288",
        "174065",
        "174b46",
        "1755ef",
        "105b23",
        "1fc291",
    };    private static readonly string[] EciHebrew =
    {
        "1fdd7f",
        "104a41",
        "175b5d",
        "175a5d",
        "17435d",
        "105441",
        "1fd57f",
        "001500",
        "0af5ed",
        "1a8474",
        "1ecd8a",
        "0fb957",
        "0ce7ed",
        "001ff4",
        "1fd95a",
        "105e77",
        "174fc6",
        "175c0f",
        "174d21",
        "105ae3",
        "1fc3a6",
    };    private static readonly string[] EciArabic =
    {
        "1fce7f",
        "104a41",
        "17525d",
        "17545d",
        "17485d",
        "104b41",
        "1fd57f",
        "000b00",
        "03690c",
        "14245b",
        "0474ab",
        "1e3b1c",
        "06c9f3",
        "001c7b",
        "1fdf1c",
        "1048d5",
        "175f14",
        "175412",
        "174aef",
        "104088",
        "1fc6cc",
    };    private static readonly string[] EciBig5 =
    {
        "1fd97f",
        "105041",
        "175a5d",
        "17525d",
        "17555d",
        "104f41",
        "1fd57f",
        "001100",
        "0d6a5f",
        "1e8de6",
        "11cf34",
        "1c2e3a",
        "10fa4d",
        "001662",
        "1fd533",
        "10493a",
        "175fca",
        "174a84",
        "175e45",
        "1053b1",
        "1fc26f",
    };    private static readonly string[] EciEucKr =
    {
        "1fd17f",
        "105a41",
        "17465d",
        "17505d",
        "175e5d",
        "105241",
        "1fd57f",
        "000a00",
        "024a3b",
        "08b977",
        "0be9f5",
        "1bab7b",
        "06dea9",
        "0016e4",
        "1fcfa6",
        "10431b",
        "17441c",
        "1754c5",
        "174509",
        "10418b",
        "1fcb86",
    };    private static readonly string[] EciGb18030 =
    {
        "1fdc7f",
        "104541",
        "175a5d",
        "17595d",
        "17455d",
        "105b41",
        "1fd57f",
        "001900",
        "0afeed",
        "112ce6",
        "03402e",
        "17bbeb",
        "0be4e1",
        "001264",
        "1fd2e6",
        "105f68",
        "1748b4",
        "175ced",
        "174e75",
        "105737",
        "1fc42a",
    };    private static readonly string[] EciLatin6 =
    {
        "1fda7f",
        "104541",
        "17425d",
        "175f5d",
        "17405d",
        "104b41",
        "1fd57f",
        "000d00",
        "05d389",
        "0e9285",
        "1ee255",
        "032eca",
        "0c4455",
        "00192e",
        "1fc323",
        "105d7a",
        "1757e2",
        "174a6e",
        "1752d1",
        "104a69",
        "1fccdb",
    };    private static readonly string[] EciCyrillic =
    {
        "1fc07f",
        "104641",
        "174b5d",
        "174b5d",
        "17595d",
        "104541",
        "1fd57f",
        "001f00",
        "0676d0",
        "17aef1",
        "164787",
        "0b808e",
        "10ced8",
        "001925",
        "1fdfb4",
        "1042c7",
        "17417b",
        "175d8e",
        "175fec",
        "104dae",
        "1fcf20",
    };
}
