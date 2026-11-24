// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Decoding a QR module grid, with no rendering involved.
/// </summary>
/// <remarks>
/// <para><b>The encoder and the reader share <c>QrLayout</c>, so a round trip cannot see a wrong
/// layout rule</b> — the same blind spot the barcode symbol tables have. What it <i>can</i> see, and
/// what it caught immediately, is a rule applied on one side and not the other: the mask.</para>
///
/// <para><b>The independent leg is Reed–Solomon.</b> A QR symbol's error correction is checked in
/// <see cref="ReedSolomonDecoderTests"/> against a decoder that shares only a field with the
/// encoder, and a grid whose codewords are wrong will not satisfy it. So "this grid decodes" is a
/// much stronger statement than "the reader agrees with the writer": the codewords have to be a
/// valid RS codeword, which a mislaid or mis-masked grid is not.</para>
/// </remarks>
public class QrGridReaderTests
{
    /// <summary>What the encoder writes, the reader reads.</summary>
    /// <remarks>
    /// The payloads span the structural cases: version 1 with no alignment pattern, a version with
    /// one, and a payload long enough to need two error-correction blocks — where the interleaving
    /// the reader has to undo is not the identity.
    /// </remarks>
    [Theory]
    [InlineData("HELLO")]
    [InlineData("https://example.com/order/4711")]
    [InlineData("0123456789")]
    [InlineData("Tetitulz.Pdf — round trip with non-ASCII: naïve, ₪42, שלום")]
    public void AnEncodedSymbolReadsBackAsWhatWentIn(string payload)
    {
        bool[,] grid = QrCodeEncoder.Encode(payload);

        string decoded = QrGridReader.Decode(grid, out string refusal);

        Assert.Null(refusal);
        Assert.Equal(payload, decoded);
    }

    /// <summary>
    /// A grid whose data modules were never masked does not decode — which is what the encoder used
    /// to produce.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the regression test for a symbol nothing could read.</b> The encoder marked
    /// each data module as <i>reserved</i> while placing it, and masking skips reserved modules — so
    /// by the time the mask ran there was nothing left for it to apply to. The grid went out
    /// unmasked with a format field announcing a mask, and any conforming reader would unmask a
    /// symbol that had never been masked and get noise.</para>
    ///
    /// <para><b>The evidence is Reed–Solomon, not this library's opinion.</b> Undoing the mask on
    /// an unmasked grid gives codewords that are not a valid RS codeword and cannot be repaired into
    /// one, so the refusal below comes from the error correction rather than from any layout rule
    /// the two sides share.</para>
    /// </remarks>
    [Fact]
    public void AnUnmaskedGridDoesNotDecode()
    {
        bool[,] correct = QrCodeEncoder.Encode("HELLO");
        bool[,] unmasked = WithoutMask(correct);

        // Sanity: the two really do differ, or this test would prove nothing.
        Assert.NotEqual(Flatten(correct), Flatten(unmasked), StringComparer.Ordinal);

        Assert.Equal("HELLO", QrGridReader.Decode(correct, out _));
        Assert.Null(QrGridReader.Decode(unmasked, out _));
    }

    /// <summary>Damage within the error correction's reach is repaired.</summary>
    /// <remarks>
    /// <b>The reason a QR code works on a creased label.</b> Level M repairs about 15% of codewords,
    /// and this flips whole modules rather than tweaking them — the same thing a scratch does.
    /// </remarks>
    [Fact]
    public void ADamagedSymbolIsStillRead()
    {
        const string payload = "https://example.com/order/4711";
        bool[,] grid = QrCodeEncoder.Encode(payload);

        // A small square of destroyed modules, away from the finders and the format field.
        int size = grid.GetLength(0);
        for (int y = size / 2; y < (size / 2) + 3; y++)
            for (int x = size / 2; x < (size / 2) + 3; x++)
                grid[y, x] = !grid[y, x];

        Assert.Equal(payload, QrGridReader.Decode(grid, out string refusal));
        Assert.Null(refusal);
    }

    /// <summary>Damage beyond the error correction's reach is refused, not guessed at.</summary>
    /// <remarks>
    /// <para><b>A reader that always answers is worse than one that sometimes declines.</b> Past
    /// level M's capacity the codewords may be closer to a different valid symbol, and "correcting"
    /// them yields a well-formed URL nobody encoded. The refusal names the error correction, because
    /// that is what actually refused.</para>
    ///
    /// <para><b>The damage is counted in codewords, not smeared over an area.</b> A first attempt
    /// scribbled over a 9×9 block of the middle and the symbol <i>still decoded</i> — which is the
    /// right answer and made the test's premise wrong rather than the code. Flipping whole codewords
    /// through the layout's own walk makes "twenty damaged, thirteen repairable" exact instead of
    /// hopeful, and keeps every finder, timing and format module untouched so the refusal can only
    /// come from the error correction.</para>
    /// </remarks>
    [Fact]
    public void ASymbolDamagedBeyondRepairIsRefusedByName()
    {
        bool[,] grid = QrCodeEncoder.Encode("https://example.com/order/4711");

        int size = grid.GetLength(0);
        int version = QrLayout.VersionOf(size);
        QrLayout.BuildFunctionPatterns(version, out _, out bool[,] reserved);

        // Level M on this version repairs half of its 26 error-correction codewords, so thirteen.
        const int DamagedCodewords = 20;
        int flipped = 0;

        foreach ((int y, int x) in QrLayout.DataModuleOrder(size, reserved))
        {
            if (flipped >= DamagedCodewords * 8) break;
            grid[y, x] = !grid[y, x];
            flipped++;
        }

        Assert.Null(QrGridReader.Decode(grid, out string refusal));
        Assert.Contains("error correction", refusal, StringComparison.Ordinal);
    }

    /// <summary>An error-correction level this has no block table for is refused by name.</summary>
    /// <remarks>
    /// <para><b>The block layout differs per level, and guessing it is the dangerous failure.</b>
    /// A level-L symbol of the same version splits its codewords into different blocks with a
    /// different error-correction length; read with M's table, the pieces are individually
    /// plausible and collectively meaningless, and Reed–Solomon would be asked to repair damage
    /// that is really just a wrong split.</para>
    ///
    /// <para>The fixture rewrites a real symbol's format field to declare level L with the same
    /// mask, leaving everything else intact — so the only thing the reader can object to is the
    /// level, which is what its message has to say.</para>
    /// </remarks>
    [Fact]
    public void AFormatFieldDeclaringAnotherLevel_IsRefusedAtErrorCorrection_NamingThatLevel()
    {
        // Every level is read (QrThirdPartySymbolTests), so a symbol whose format
        // field says L over level-M data is split into L's blocks — and refused where Reed–Solomon
        // cannot repair that wrong split, naming the level it was read at. Version 5, where M has two
        // blocks and L one: at version 1 both are one block of 26 and L's generator divides M's, so
        // an M codeword is a valid L codeword and reads the same — no wrong split to find.
        const int LevelL = 0b01;
        bool[,] grid = QrCodeEncoder.Encode(new string('A', 70));
        Assert.Equal(QrLayout.SizeOf(5), grid.GetLength(0));
        int size = grid.GetLength(0);

        WriteFormat(grid, size, QrLayout.FormatCode(LevelL, MaskOf(grid, size)));

        Assert.Null(QrGridReader.Decode(grid, out string refusal));
        Assert.Contains("level L", refusal, StringComparison.Ordinal);
    }

    /// <summary>A symbol whose first format copy is destroyed still reads from the second.</summary>
    /// <remarks>
    /// <b>The format field is written twice because it is the one thing that cannot be
    /// recovered.</b> Everything else in a symbol is protected by Reed–Solomon; the field that says
    /// which mask to undo is not, so it is duplicated in two places and a reader that consults only
    /// one throws away the redundancy. On a clean symbol both copies agree, which is exactly why a
    /// reader with that defect passes every test but this one.
    /// </remarks>
    [Fact]
    public void ASymbolWithOneDestroyedFormatCopyStillReads()
    {
        bool[,] grid = QrCodeEncoder.Encode("HELLO");
        int size = grid.GetLength(0);

        // Scribble over copy A only; copy B, around the other two finders, is untouched.
        for (int bit = 0; bit < 15; bit++)
        {
            (int y, int x) = QrLayout.FormatPositionA(bit);
            grid[y, x] = bit % 2 == 0;
        }

        Assert.Equal("HELLO", QrGridReader.Decode(grid, out string refusal));
        Assert.Null(refusal);
    }

    /// <summary>A grid that is not a QR symbol is passed over silently.</summary>
    /// <remarks>
    /// <b>The acceptance control for the refusal channel.</b> A page scan offers this every square
    /// of ink it finds, so "not a QR symbol" must be silent or the channel is noise. Both a wrong
    /// size and a right-sized square of nonsense are tried: the first is rejected by arithmetic —
    /// every QR grid is 17 + 4v modules — and the second by the format field, of which only 32
    /// values are legal.
    /// </remarks>
    [Fact]
    public void AGridThatIsNotAQrSymbolIsPassedOverWithoutARefusal()
    {
        Assert.Null(QrGridReader.Decode(new bool[20, 20], out string sizeRefusal));
        Assert.Null(sizeRefusal);

        var nonsense = new bool[21, 21];
        for (int y = 0; y < 21; y++)
            for (int x = 0; x < 21; x++)
                nonsense[y, x] = ((x * 3) + (y * 5)) % 4 == 0;

        Assert.Null(QrGridReader.Decode(nonsense, out string junkRefusal));
        Assert.Null(junkRefusal);
    }

    /// <summary>A non-square grid is refused rather than indexed out of bounds.</summary>
    [Fact]
    public void AMalformedGridIsRefusedRatherThanIndexed()
    {
        Assert.Null(QrGridReader.Decode(new bool[21, 25], out _));
        Assert.Null(QrGridReader.Decode(null, out _));
    }

    // ================================================================

    /// <summary>
    /// The grid with its mask undone — i.e. what the encoder produced before the mask was applied.
    /// </summary>
    private static bool[,] WithoutMask(bool[,] grid)
    {
        int size = grid.GetLength(0);
        int version = QrLayout.VersionOf(size);
        QrLayout.BuildFunctionPatterns(version, out _, out bool[,] reserved);

        // The mask the format field declares — the same one the reader will try to undo.
        int mask = -1;
        for (int level = 0; level < 4 && mask < 0; level++)
            for (int m = 0; m < 8; m++)
            {
                int code = QrLayout.FormatCode(level, m);
                int read = 0;
                for (int bit = 0; bit < 15; bit++)
                {
                    (int y, int x) = QrLayout.FormatPositionA(bit);
                    if (grid[y, x]) read |= 1 << bit;
                }
                if (code == read) { mask = m; break; }
            }

        Assert.True(mask >= 0, "the encoder wrote no readable format field");

        var without = (bool[,])grid.Clone();
        foreach ((int y, int x) in QrLayout.DataModuleOrder(size, reserved))
            if (QrLayout.MaskBit(mask, y, x)) without[y, x] = !without[y, x];

        return without;
    }

    /// <summary>Writes a 15-bit format code into both of its copies.</summary>
    private static void WriteFormat(bool[,] grid, int size, int code)
    {
        for (int bit = 0; bit < 15; bit++)
        {
            bool set = ((code >> bit) & 1) == 1;

            (int ay, int ax) = QrLayout.FormatPositionA(bit);
            (int by, int bx) = QrLayout.FormatPositionB(bit, size);
            grid[ay, ax] = set;
            grid[by, bx] = set;
        }
    }

    /// <summary>The mask a grid's format field declares.</summary>
    private static int MaskOf(bool[,] grid, int size)
    {
        int read = 0;
        for (int bit = 0; bit < 15; bit++)
        {
            (int y, int x) = QrLayout.FormatPositionA(bit);
            if (grid[y, x]) read |= 1 << bit;
        }

        for (int level = 0; level < 4; level++)
            for (int m = 0; m < 8; m++)
                if (QrLayout.FormatCode(level, m) == read) return m;

        Assert.Fail("the encoder wrote no readable format field");
        return -1;
    }

    private static string Flatten(bool[,] grid)
    {
        int size = grid.GetLength(0);
        var sb = new System.Text.StringBuilder(size * size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                sb.Append(grid[y, x] ? '1' : '0');
        return sb.ToString();
    }
}
