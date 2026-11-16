// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Decodes a QR module grid — the encoder run backwards, through <see cref="QrLayout"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Grid in, text out: nothing here knows about pixels.</b> Locating a symbol on a page
    /// and sampling it into a grid is a different problem with different failure modes, so it lives
    /// elsewhere and this can be tested against grids built by the encoder, with no rendering at
    /// all.</para>
    ///
    /// <para><b>Every version (1–40) and every error-correction level</b>, the block layout derived
    /// from the table two independent implementations agree on (<see cref="QrLayout"/>), and the
    /// modes a text reader meets: numeric, alphanumeric, byte, ECI (the sets
    /// <see cref="EciCharacterSets"/> decodes), FNC1 in the first position (GS1, the alphanumeric
    /// <c>%</c> read as the GS separator and <c>%%</c> as a percent sign), and structured append.
    /// Byte data with no ECI is read as UTF-8 where it
    /// is valid UTF-8 (what this library writes, and what most producers mean) and as ISO-8859-1
    /// otherwise.</para>
    ///
    /// <para>Kanji mode (Shift JIS) and Hanzi mode (GB 2312, GB/T 18284's extension) are read through the code-page provider (<see cref="EciCharacterSets.Strict"/>).</para>
    ///
    /// <para><b>Refused by name:</b> a Hanzi subset other than GB 2312, and FNC1 in the second position,
    /// whose application indicator one independent reader does not read and another reads as a byte — two
    /// implementations disagreeing on the grammar, which only ISO/IEC 18004 would settle.</para>
    /// </remarks>
    internal static class QrGridReader
    {
        /// <summary>Bits in the format field.</summary>
        private const int FormatBits = 15;

        /// <summary>
        /// Decodes a grid, or returns <see langword="null"/> with a reason.
        /// </summary>
        /// <param name="grid">Square module grid; true is dark.</param>
        /// <param name="refusal">
        /// Why a grid that really was a QR symbol could not be read, or null when it was not one.
        /// </param>
        internal static string Decode(bool[,] grid, out string refusal) => Decode(grid, out refusal, out _);

        /// <summary>As <see cref="Decode(bool[,], out string)"/>, with what the symbol's modes said.</summary>
        internal static string Decode(bool[,] grid, out string refusal, out BarcodeSymbolInfo info)
        {
            refusal = null;
            info = null;
            if (grid == null) return null;

            int size = grid.GetLength(0);
            if (size != grid.GetLength(1)) return null;

            int version = QrLayout.VersionOf(size);
            if (version < 0) return null;

            // A square of ink the right size is not yet a symbol: the format field now forgives
            // three wrong bits, which a random grid can come within, so the three finder patterns
            // are asked first — a grid without them is passed over in silence, not refused.
            if (!HasFinderPatterns(grid, size)) return null;

            if (!TryReadFormat(grid, size, out int eccLevel, out int mask)) return null;

            QrLayout.BuildFunctionPatterns(version, out _, out bool[,] reserved);

            int[] interleaved = ReadCodewords(grid, size, reserved, mask);
            int[] dataCodewords = Deinterleave(interleaved, version, eccLevel, out refusal);
            if (dataCodewords == null) return null;

            return ReadSegments(dataCodewords, version, out refusal, out info);
        }

        /// <summary>The most format bits a read may differ from a valid code in and still be that code (BCH(15,5) corrects three).</summary>
        internal const int MaxFormatErrors = 3;

        /// <summary>Wrong modules a finder pattern (49 of them) may have and still be one.</summary>
        internal const int MaxFinderErrors = 7;

        /// <summary>Whether the three corners hold finder patterns, each within <see cref="MaxFinderErrors"/> modules.</summary>
        private static bool HasFinderPatterns(bool[,] grid, int size)
        {
            foreach ((int top, int left) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
            {
                int wrong = 0;
                for (int y = 0; y < 7; y++)
                    for (int x = 0; x < 7; x++)
                    {
                        bool dark = y == 0 || y == 6 || x == 0 || x == 6 || (y >= 2 && y <= 4 && x >= 2 && x <= 4);
                        if (grid[top + y, left + x] != dark) wrong++;
                    }
                if (wrong > MaxFinderErrors) return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the format field from either of its two copies, checking the BCH code.
        /// </summary>
        /// <remarks>
        /// <b>The check is done by re-deriving, not by decoding the BCH.</b> There are only 32 legal
        /// format values — four levels times eight masks — so computing all 32 and looking for an
        /// exact match is both simpler and stricter than running the syndrome. Anything that is not
        /// one of the 32 is not a format field, which is what makes this a reliable "is this even a
        /// QR symbol" test rather than a value that always parses into something.
        /// </remarks>
        private static bool TryReadFormat(bool[,] grid, int size, out int eccLevel, out int mask)
        {
            eccLevel = 0;
            mask = 0;

            int copyA = 0, copyB = 0;
            for (int bit = 0; bit < FormatBits; bit++)
            {
                (int ay, int ax) = QrLayout.FormatPositionA(bit);
                (int by, int bx) = QrLayout.FormatPositionB(bit, size);

                if (grid[ay, ax]) copyA |= 1 << bit;
                if (grid[by, bx]) copyB |= 1 << bit;
            }

            // The nearest of the 32 codes to either copy, within what BCH(15,5) corrects: a smudged
            // format field read as no symbol at all, though the code is built to survive three errors.
            int best = MaxFormatErrors + 1;
            for (int level = 0; level < 4; level++)
                for (int m = 0; m < 8; m++)
                {
                    int code = QrLayout.FormatCode(level, m);
                    int distance = Math.Min(BitCount(code ^ copyA), BitCount(code ^ copyB));
                    if (distance >= best) continue;

                    best = distance;
                    eccLevel = level;
                    mask = m;
                }

            return best <= MaxFormatErrors;
        }

        private static int BitCount(int value)
        {
            int count = 0;
            for (; value != 0; value &= value - 1) count++;
            return count;
        }

        /// <summary>Unmasks the data modules and reads them back in the order they were written.</summary>
        private static int[] ReadCodewords(bool[,] grid, int size, bool[,] reserved, int mask)
        {
            var bits = new List<bool>();

            foreach ((int y, int x) in QrLayout.DataModuleOrder(size, reserved))
            {
                bool module = grid[y, x];
                if (QrLayout.MaskBit(mask, y, x)) module = !module;
                bits.Add(module);
            }

            var codewords = new int[bits.Count / 8];
            for (int i = 0; i < codewords.Length; i++)
            {
                int value = 0;
                for (int b = 0; b < 8; b++) value = (value << 1) | (bits[(i * 8) + b] ? 1 : 0);
                codewords[i] = value;
            }

            return codewords;
        }

        /// <summary>
        /// Undoes the block interleaving and corrects each block, returning the data codewords.
        /// </summary>
        /// <remarks>
        /// <para><b>Interleaving is why a QR code survives a smudge.</b> Codewords are written
        /// round-robin across blocks, so damage that would exhaust one block's correction capacity
        /// is instead spread thinly over all of them. Undoing it is therefore not an optional
        /// tidying step — read the codewords in symbol order and every block gets a contiguous slice
        /// of the wrong data.</para>
        ///
        /// <para>Blocks may differ in length by one codeword, and the short ones sit first. That is
        /// what the column-major walk below is being careful about.</para>
        /// </remarks>
        private static int[] Deinterleave(int[] interleaved, int version, int eccLevel, out string refusal)
        {
            refusal = null;

            int[] blockSizes = QrLayout.BlockSizes(version, eccLevel);
            int ecPerBlock = QrLayout.EcPerBlock(version, eccLevel);
            int blocks = blockSizes.Length;

            int expected = 0;
            foreach (int size in blockSizes) expected += size;
            if (interleaved.Length < expected + (blocks * ecPerBlock)) return null;

            var data = new int[blocks][];
            for (int i = 0; i < blocks; i++) data[i] = new int[blockSizes[i]];

            int longest = 0;
            foreach (int size in blockSizes) if (size > longest) longest = size;

            int at = 0;
            for (int column = 0; column < longest; column++)
                for (int block = 0; block < blocks; block++)
                    if (column < blockSizes[block]) data[block][column] = interleaved[at++];

            var ec = new int[blocks][];
            for (int i = 0; i < blocks; i++) ec[i] = new int[ecPerBlock];

            for (int column = 0; column < ecPerBlock; column++)
                for (int block = 0; block < blocks; block++)
                    ec[block][column] = interleaved[at++];

            var field = new GaloisField256(GaloisField256.QrPrimitive, ecPerBlock);
            var result = new List<int>(expected);

            for (int block = 0; block < blocks; block++)
            {
                var codeword = new int[data[block].Length + ecPerBlock];
                Array.Copy(data[block], codeword, data[block].Length);
                Array.Copy(ec[block], 0, codeword, data[block].Length, ecPerBlock);

                if (!ReedSolomonDecoder.TryCorrect(field, codeword, ecPerBlock, out _))
                {
                    refusal = $"QR symbol's error correction could not repair block {block + 1} of "
                            + $"{blocks}; the symbol is damaged beyond what level {LevelName(eccLevel)} can recover";
                    return null;
                }

                for (int i = 0; i < data[block].Length; i++) result.Add(codeword[i]);
            }

            return result.ToArray();
        }

        /// <summary>QR's mode indicators (ISO/IEC 18004; the same four bits in both implementations).</summary>
        private const int ModeTerminator = 0b0000, ModeNumeric = 0b0001, ModeAlphanumeric = 0b0010,
                          ModeStructuredAppend = 0b0011, ModeByte = 0b0100, ModeFnc1First = 0b0101,
                          ModeEci = 0b0111, ModeKanji = 0b1000, ModeFnc1Second = 0b1001, ModeHanzi = 0b1101;

        private const int HanziGb2312 = 1, ShiftJisCodePage = 932, Gb2312CodePage = 936;

        /// <summary>
        /// How a 13-bit kanji or Hanzi value becomes its two bytes — the row width, and the offsets
        /// below and from the split — as independent decoders read them (ISO/IEC 18004
        /// §7.4.6 for kanji, GB/T 18284 for Hanzi).
        /// </summary>
        private readonly struct DoubleByteStep
        {
            internal DoubleByteStep(int row, int split, int below, int above)
            {
                Row = row; Split = split; Below = below; Above = above;
            }

            internal int Row { get; }
            internal int Split { get; }
            internal int Below { get; }
            internal int Above { get; }
        }

        /// <summary>Kanji: (v / 0xC0) &lt;&lt; 8 | v % 0xC0, then 0x8140 below 0x1F00, 0xC140 from it — Shift JIS bytes.</summary>
        private static readonly DoubleByteStep KanjiStep = new DoubleByteStep(0xC0, 0x1F00, 0x8140, 0xC140);

        /// <summary>Hanzi: (v / 0x60) &lt;&lt; 8 | v % 0x60, then 0xA1A1 below 0x0A00, 0xA6A1 from it — GB 2312 bytes.</summary>
        private static readonly DoubleByteStep HanziStep = new DoubleByteStep(0x60, 0x0A00, 0xA1A1, 0xA6A1);

        /// <summary>
        /// A kanji or Hanzi segment: <paramref name="count"/> characters of 13 bits each, every one two
        /// bytes of the mode's set, decoded strictly (a pair the set does not hold is refused).
        /// </summary>
        private static string ReadDoubleByte(BitReader reader, int count, DoubleByteStep step, int codePage, out string refusal)
        {
            refusal = "QR kanji or Hanzi segment is cut short";
            var bytes = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryRead(13, out int value)) return null;
                int assembled = ((value / step.Row) << 8) | (value % step.Row);
                assembled += assembled < step.Split ? step.Below : step.Above;
                bytes[2 * i] = (byte)(assembled >> 8);
                bytes[2 * i + 1] = (byte)assembled;
            }
            try
            {
                string text = EciCharacterSets.Strict(codePage).GetString(bytes);
                refusal = null;
                return text;
            }
            catch (ArgumentException ex)
            {
                refusal = $"QR {(codePage == ShiftJisCodePage ? "kanji" : "Hanzi")} segment holds a pair its set does not ({ex.Message})";
                return null;
            }
        }

        /// <summary>The alphanumeric mode's 45 characters, by value.</summary>
        private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

        /// <summary>
        /// Reads the corrected data codewords' segments: each a mode indicator and, for the data
        /// modes, a character count whose width the mode and the version decide.
        /// </summary>
        /// <remarks>
        /// A byte segment is decoded in the set the last ECI named; with no ECI, as UTF-8 where it is
        /// valid UTF-8 and as ISO-8859-1 otherwise. The AIM modifier follows the established table: 1, 2 with
        /// an ECI, 3 for GS1 (FNC1 first), 4 for GS1 with an ECI.
        /// </remarks>
        private static string ReadSegments(int[] codewords, int version, out string refusal, out BarcodeSymbolInfo info)
        {
            refusal = null;
            info = null;
            var reader = new BitReader(codewords);
            var text = new StringBuilder();
            var symbol = new BarcodeSymbolInfo { Modifier = 1 };
            int eci = -1;
            bool fnc1 = false;

            while (reader.Remaining >= 4)
            {
                reader.TryRead(4, out int mode);
                if (mode == ModeTerminator) break;

                switch (mode)
                {
                    case ModeFnc1First:
                        fnc1 = true;
                        symbol.IsGs1 = true;
                        continue;

                    case ModeStructuredAppend:
                        if (!reader.TryRead(16, out int header)) { refusal = "QR structured append header is cut short"; return null; }
                        symbol.SequencePosition = (header >> 12) + 1;
                        symbol.SequenceCount = ((header >> 8) & 0x0F) + 1;
                        continue;

                    case ModeEci:
                        if (!TryReadEci(reader, out eci)) { refusal = "QR ECI designator is cut short or malformed"; return null; }
                        if (!EciCharacterSets.IsDecoded(eci)) { refusal = EciCharacterSets.NotDecoded("QR", eci); return null; }
                        continue;

                    case ModeNumeric:
                    case ModeAlphanumeric:
                    case ModeByte:
                        break;

                    case ModeKanji:
                        break;

                    case ModeHanzi:
                        // GB/T 18284's extension: a 4-bit subset, of which only GB 2312 (1) is assigned.
                        if (!reader.TryRead(4, out int subset)) { refusal = "QR Hanzi subset is cut short"; return null; }
                        if (subset != HanziGb2312)
                        {
                            refusal = $"QR Hanzi segment names subset {subset}; only GB 2312 (1) is assigned";
                            return null;
                        }
                        break;

                    case ModeFnc1Second:
                        refusal = "QR symbol uses FNC1 in the second position, whose application indicator two implementations "
                                + "read differently (one reader reads none, another one byte); it is not decoded";
                        return null;

                    default:
                        refusal = $"QR symbol uses mode {mode:X1}, which ISO/IEC 18004 does not assign";
                        return null;
                }

                if (!reader.TryRead(QrLayout.CharacterCountBits(mode, version), out int count)) { refusal = "QR segment's character count is cut short"; return null; }

                string segment = mode == ModeNumeric ? ReadNumeric(reader, count, out refusal)
                    : mode == ModeAlphanumeric ? ReadAlphanumeric(reader, count, fnc1, out refusal)
                    : mode == ModeKanji ? ReadDoubleByte(reader, count, KanjiStep, ShiftJisCodePage, out refusal)
                    : mode == ModeHanzi ? ReadDoubleByte(reader, count, HanziStep, Gb2312CodePage, out refusal)
                    : ReadBytes(reader, count, eci, out refusal);
                if (segment == null) return null;
                text.Append(segment);
            }

            // A symbol holding no characters has nothing to return: not an empty string.
            if (text.Length == 0) return null;
            if (eci >= 0) symbol.Modifier = fnc1 ? 4 : 2;
            else if (fnc1) symbol.Modifier = 3;
            info = symbol;
            return text.ToString();
        }

        /// <summary>
        /// An ECI designator's number: one byte 0xxxxxxx (0–127), two 10xxxxxx… (to 16 383), or
        /// three 110xxxxx… (to 999 999) — read so by independent readers.
        /// </summary>
        private static bool TryReadEci(BitReader reader, out int eci)
        {
            eci = -1;
            if (!reader.TryRead(8, out int first)) return false;
            if ((first & 0x80) == 0) { eci = first; return true; }
            if ((first & 0xC0) == 0x80)
            {
                if (!reader.TryRead(8, out int second)) return false;
                eci = ((first & 0x3F) << 8) | second;
                return true;
            }
            if ((first & 0xE0) == 0xC0)
            {
                if (!reader.TryRead(16, out int rest)) return false;
                eci = ((first & 0x1F) << 16) | rest;
                return eci < 1000000;
            }
            return false;
        }

        /// <summary>Numeric mode: three digits in 10 bits, two in 7, one in 4; a group past its range is refused.</summary>
        private static string ReadNumeric(BitReader reader, int count, out string refusal)
        {
            refusal = "QR numeric segment is cut short or holds a value no digits make";
            var digits = new StringBuilder(count);
            while (count > 0)
            {
                int take = Math.Min(count, 3), bits = take == 3 ? 10 : take == 2 ? 7 : 4;
                if (!reader.TryRead(bits, out int value) || value >= (take == 3 ? 1000 : take == 2 ? 100 : 10)) return null;
                digits.Append(value.ToString(take == 3 ? "D3" : take == 2 ? "D2" : "D1", System.Globalization.CultureInfo.InvariantCulture));
                count -= take;
            }
            refusal = null;
            return digits.ToString();
        }

        /// <summary>
        /// Alphanumeric mode: two characters in 11 bits (45 × first + second), one in 6. Under FNC1 a
        /// <c>%</c> is the GS separator and <c>%%</c> a percent sign, as both implementations read it.
        /// </summary>
        private static string ReadAlphanumeric(BitReader reader, int count, bool fnc1, out string refusal)
        {
            refusal = "QR alphanumeric segment is cut short or holds a value past its 45 characters";
            var chars = new StringBuilder(count);
            while (count > 0)
            {
                if (count >= 2)
                {
                    if (!reader.TryRead(11, out int pair) || pair >= 45 * 45) return null;
                    chars.Append(Alphanumeric[pair / 45]).Append(Alphanumeric[pair % 45]);
                    count -= 2;
                }
                else
                {
                    if (!reader.TryRead(6, out int single) || single >= 45) return null;
                    chars.Append(Alphanumeric[single]);
                    count--;
                }
            }
            refusal = null;
            if (!fnc1) return chars.ToString();

            var gs1 = new StringBuilder(chars.Length);
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '%') { gs1.Append(chars[i]); continue; }
                if (i + 1 < chars.Length && chars[i + 1] == '%') { gs1.Append('%'); i++; }
                else gs1.Append('\u001D');
            }
            return gs1.ToString();
        }

        /// <summary>Byte mode: <paramref name="count"/> bytes, in the set <paramref name="eci"/> names, or UTF-8 / ISO-8859-1 when none does.</summary>
        private static string ReadBytes(BitReader reader, int count, int eci, out string refusal)
        {
            refusal = null;
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryRead(8, out int value)) { refusal = "QR byte segment is cut short"; return null; }
                bytes[i] = (byte)value;
            }

            if (eci >= 0) return EciCharacterSets.Decode("QR", bytes, eci, out refusal);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (ArgumentException) { return Core.Text.Latin1Text.Decode(bytes); }
        }
        private static string LevelName(int eccLevel) => eccLevel switch
        {
            0b01 => "L",
            0b00 => "M",
            0b11 => "Q",
            0b10 => "H",
            _ => eccLevel.ToString(),
        };

        /// <summary>Reads big-endian bit fields across a codeword array.</summary>
        private sealed class BitReader
        {
            private readonly int[] _codewords;
            private int _bit;

            internal BitReader(int[] codewords) => _codewords = codewords;

            /// <summary>Bits not yet read.</summary>
            internal int Remaining => _codewords.Length * 8 - _bit;

            internal bool TryRead(int count, out int value)
            {
                value = 0;
                if (_bit + count > _codewords.Length * 8) return false;

                for (int i = 0; i < count; i++)
                {
                    int codeword = _codewords[_bit / 8];
                    int bit = (codeword >> (7 - (_bit % 8))) & 1;
                    value = (value << 1) | bit;
                    _bit++;
                }

                return true;
            }
        }
    }
}
