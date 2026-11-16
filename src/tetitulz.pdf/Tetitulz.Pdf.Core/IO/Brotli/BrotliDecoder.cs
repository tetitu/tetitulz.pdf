// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.IO.Brotli
{
    /// <summary>
    /// A Brotli decoder (RFC 7932), written from the RFC: the WOFF 2.0 reader's and
    /// <c>/BrotliDecode</c>'s one decompressor, on both target frameworks, for input that may be hostile.
    /// </summary>
    /// <remarks>
    /// <para><b>The output is the window.</b> The whole output is kept, so a backward reference reads
    /// it directly; the window size only decides which distances are references and which are static
    /// dictionary words (§8). Nothing is allocated from the stream's <c>WBITS</c>.</para>
    /// <para><b>The output is bounded by the caller</b> (the <c>maxOutputBytes</c> of
    /// <see cref="Decode"/>): a stream past it is refused when it gets there, not after. <b>The rest
    /// of the work is linear in the input or the output</b>: a command writes at least two bytes,
    /// ends its meta-block, or reads at least one bit (a dictionary word a transform empties is named
    /// by distance bits), the output's room is made as bytes are written, never from a declared
    /// length, and every header structure is paid for in input bits — a context map's 64
    /// entries per literal block type by that type's two-bit mode, a prefix code's table (at most 704
    /// symbols) by the twelve bits a code takes at least — so a header costs a few hundred units of
    /// work per input byte at most, and needs no ceiling of its own.</para>
    /// <para>Every malformation the RFC says to reject is a <see cref="PdfFilterException"/>.</para>
    /// </remarks>
    internal sealed class BrotliDecoder
    {
        private readonly byte[] _in;
        private int _pos;            // next input byte to load into the bit buffer
        private ulong _bits;         // buffered bits, LSB first
        private int _bitCount;

        private byte[] _out;
        private int _outLen;
        private readonly long _maxOutput;

        // §4: the last four distances, the last at index 3.
        private readonly int[] _distRing = { 16, 15, 11, 4 };

        private BrotliDecoder(byte[] input, long maxOutputBytes)
        {
            _in = input;
            _maxOutput = maxOutputBytes;
            _out = new byte[(int)Math.Min(Math.Max(16, Math.Min(maxOutputBytes, (long)input.Length * 4)), 1 << 20)];
        }

        /// <summary>
        /// Decodes a whole Brotli stream. Refused with a <see cref="PdfFilterException"/> when the stream
        /// is malformed or truncated, or when its output would pass <paramref name="maxOutputBytes"/>.
        /// </summary>
        internal static byte[] Decode(byte[] input, long maxOutputBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxOutputBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
            var decoder = new BrotliDecoder(input, Math.Min(maxOutputBytes, 0x7FFFFFC7L));
            decoder.Run();
            var result = new byte[decoder._outLen];
            Buffer.BlockCopy(decoder._out, 0, result, 0, decoder._outLen);
            return result;
        }

        private static PdfFilterException Invalid(string why) => new PdfFilterException("Brotli: " + why);

        // ------------------------------------------------------------------ bits

        private void Fill(int need)
        {
            while (_bitCount < need)
            {
                if (_pos >= _in.Length) throw Invalid("the stream ends before its last meta-block does.");
                _bits |= (ulong)_in[_pos++] << _bitCount;
                _bitCount += 8;
            }
        }

        private int ReadBits(int n)
        {
            if (n == 0) return 0;
            Fill(n);
            int value = (int)(_bits & ((1UL << n) - 1));
            _bits >>= n;
            _bitCount -= n;
            return value;
        }

        private int ReadBit() => ReadBits(1);

        /// <summary>Drops the bits up to the next byte boundary, which must be zero.</summary>
        private void AlignToByte()
        {
            int pad = _bitCount & 7;
            if (pad != 0 && ReadBits(pad) != 0) throw Invalid("the padding before a byte boundary is not zero.");
        }

        /// <summary>The next byte-aligned input byte, after <see cref="AlignToByte"/>.</summary>
        private int ReadAlignedByte()
        {
            if (_bitCount >= 8) return ReadBits(8);
            if (_pos >= _in.Length) throw Invalid("the stream ends inside an uncompressed or metadata block.");
            return _in[_pos++];
        }

        // ------------------------------------------------------------------ prefix codes

        /// <summary>A canonical prefix code (§3.2), decoded a bit at a time: the count of codes of each length and the symbols in code order.</summary>
        private sealed class PrefixCode
        {
            internal readonly int[] Counts = new int[16];
            internal int[] Symbols;
            internal int Single = -1;   // a one-symbol code reads no bits
        }

        private static PrefixCode Build(int[] lengths, int alphabetSize)
        {
            var code = new PrefixCode();
            int used = 0, last = -1;
            for (int s = 0; s < alphabetSize; s++)
                if (lengths[s] != 0) { code.Counts[lengths[s]]++; used++; last = s; }
            if (used == 1) { code.Single = last; return code; }
            int[] next = new int[16];
            int start = 0;
            for (int len = 1; len < 16; len++) { next[len] = start; start += code.Counts[len]; }
            code.Symbols = new int[used];
            for (int s = 0; s < alphabetSize; s++)
                if (lengths[s] != 0) code.Symbols[next[lengths[s]]++] = s;
            return code;
        }

        private int ReadSymbol(PrefixCode code)
        {
            if (code.Single >= 0) return code.Single;
            int c = 0, first = 0, index = 0;
            for (int len = 1; len < 16; len++)
            {
                c |= ReadBit();
                int count = code.Counts[len];
                if (c - first < count) return code.Symbols[index + c - first];
                index += count;
                first = (first + count) << 1;
                c <<= 1;
            }
            throw Invalid("a prefix code was read past its longest code.");
        }

        private static readonly PrefixCode CodeLengthCode = BuildFixed(BrotliTables.CodeLengthCodeLengths);

        private static PrefixCode BuildFixed(int[] lengths) => Build(lengths, lengths.Length);

        /// <summary>Reads one prefix code over an alphabet of <paramref name="alphabetSize"/> symbols (§3.4, §3.5).</summary>
        private PrefixCode ReadPrefixCode(int alphabetSize)
        {
            var lengths = new int[alphabetSize];
            int hskip = ReadBits(2);
            if (hskip == 1)
            {
                int alphabetBits = 0;
                while ((1 << alphabetBits) < alphabetSize) alphabetBits++;
                int nsym = ReadBits(2) + 1;
                var symbols = new int[nsym];
                for (int i = 0; i < nsym; i++)
                {
                    int s = ReadBits(alphabetBits);
                    if (s >= alphabetSize) throw Invalid("a simple prefix code names a symbol outside its alphabet.");
                    for (int j = 0; j < i; j++)
                        if (symbols[j] == s) throw Invalid("a simple prefix code names a symbol twice.");
                    symbols[i] = s;
                }
                switch (nsym)
                {
                    case 1: lengths[symbols[0]] = 1; return new PrefixCode { Single = symbols[0] };
                    case 2: lengths[symbols[0]] = 1; lengths[symbols[1]] = 1; break;
                    case 3: lengths[symbols[0]] = 1; lengths[symbols[1]] = 2; lengths[symbols[2]] = 2; break;
                    default:
                        if (ReadBit() == 0) { foreach (int s in symbols) lengths[s] = 2; }
                        else { lengths[symbols[0]] = 1; lengths[symbols[1]] = 2; lengths[symbols[2]] = 3; lengths[symbols[3]] = 3; }
                        break;
                }
                return Build(lengths, alphabetSize);
            }

            // Complex (§3.5): the code length code lengths, then the code lengths.
            var clLengths = new int[18];
            int space = 32, nonZero = 0;
            for (int i = hskip; i < 18 && space > 0; i++)
            {
                int len = ReadSymbol(CodeLengthCode);
                clLengths[BrotliTables.CodeLengthOrder[i]] = len;
                if (len != 0) { space -= 32 >> len; nonZero++; }
            }
            if (!(nonZero == 1 || space == 0)) throw Invalid("the code length code lengths do not form a complete code.");
            PrefixCode clCode = Build(clLengths, 18);

            int symbol = 0, previous = 8, repeat = 0, repeatLength = 0;
            int space15 = 32768;
            while (symbol < alphabetSize && space15 > 0)
            {
                int len = ReadSymbol(clCode);
                if (len < 16)
                {
                    repeat = 0;
                    lengths[symbol++] = len;
                    if (len != 0) { previous = len; space15 -= 32768 >> len; }
                    continue;
                }
                int extraBits = len == 16 ? 2 : 3;
                int newLength = len == 16 ? previous : 0;
                if (repeatLength != newLength) { repeat = 0; repeatLength = newLength; }
                int oldRepeat = repeat;
                if (repeat > 0) repeat = (repeat - 2) << extraBits;
                repeat += ReadBits(extraBits) + 3;
                int delta = repeat - oldRepeat;
                if (symbol + delta > alphabetSize) throw Invalid("a code length repeat runs past the alphabet.");
                for (int k = 0; k < delta; k++) lengths[symbol++] = repeatLength;
                if (repeatLength != 0) space15 -= delta << (15 - repeatLength);
            }
            if (space15 != 0) throw Invalid("the code lengths do not form a complete prefix code.");
            int used = 0;
            foreach (int l in lengths) if (l != 0) used++;
            if (used < 2) throw Invalid("a complex prefix code has fewer than two symbols.");
            return Build(lengths, alphabetSize);
        }

        // ------------------------------------------------------------------ header fields

        /// <summary>§9.2's 1..11-bit code for 1..256 (NBLTYPES, NTREES).</summary>
        private int ReadCount256()
        {
            if (ReadBit() == 0) return 1;
            int n = ReadBits(3);
            return n == 0 ? 2 : (1 << n) + ReadBits(n) + 1;
        }

        private int ReadBlockCount(PrefixCode code)
        {
            int c = ReadSymbol(code);
            return BrotliTables.BlockCountBase[c] + ReadBits(BrotliTables.BlockCountExtra[c]);
        }

        /// <summary>A block category's types, their codes, the current block and its remaining count (§6).</summary>
        private sealed class BlockCategory
        {
            internal int Types = 1;
            internal PrefixCode TypeCode, CountCode;
            internal int Current, Previous = 1, Remaining = 1 << 24;
        }

        private BlockCategory ReadBlockCategory()
        {
            var category = new BlockCategory { Types = ReadCount256() };
            if (category.Types >= 2)
            {
                category.TypeCode = ReadPrefixCode(category.Types + 2);
                category.CountCode = ReadPrefixCode(26);
                category.Remaining = ReadBlockCount(category.CountCode);
            }
            return category;
        }

        private void Switch(BlockCategory category)
        {
            int s = ReadSymbol(category.TypeCode);
            int type = s == 0 ? category.Previous : s == 1 ? (category.Current + 1) % category.Types : s - 2;
            if (type >= category.Types) throw Invalid("a block type is outside its category.");
            category.Previous = category.Current;
            category.Current = type;
            category.Remaining = ReadBlockCount(category.CountCode);
        }

        /// <summary>§7.3: a context map of <paramref name="size"/> entries over <paramref name="trees"/> codes.</summary>
        private byte[] ReadContextMap(int size, int trees)
        {
            var map = new byte[size];
            if (trees < 2) return map;
            int rleMax = ReadBit() == 0 ? 0 : ReadBits(4) + 1;
            PrefixCode code = ReadPrefixCode(trees + rleMax);
            for (int i = 0; i < size; )
            {
                int s = ReadSymbol(code);
                if (s == 0) { map[i++] = 0; continue; }
                if (s <= rleMax)
                {
                    int run = (1 << s) + ReadBits(s);
                    if (i + run > size) throw Invalid("a context map's zero run passes its end.");
                    i += run;   // already zero
                    continue;
                }
                map[i++] = (byte)(s - rleMax);
            }
            if (ReadBit() == 1)
            {
                // The inverse move-to-front transform of §7.3.
                var mtf = new byte[256];
                for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
                for (int i = 0; i < size; i++)
                {
                    int index = map[i];
                    byte value = mtf[index];
                    map[i] = value;
                    for (; index > 0; index--) mtf[index] = mtf[index - 1];
                    mtf[0] = value;
                }
            }
            for (int i = 0; i < size; i++)
                if (map[i] >= trees) throw Invalid("a context map names a prefix code that does not exist.");
            return map;
        }

        // ------------------------------------------------------------------ output

        private void Ensure(long extra)
        {
            long need = _outLen + extra;
            if (need > _maxOutput) throw Invalid($"the output passes its {_maxOutput}-byte limit.");
            if (need <= _out.Length) return;
            long size = Math.Max(need, Math.Min((long)_out.Length * 2, _maxOutput));
            Array.Resize(ref _out, (int)Math.Min(size, 0x7FFFFFC7L));
        }

        private void Put(byte b)
        {
            if (_outLen == _out.Length) Ensure(1);
            else if (_outLen >= _maxOutput) throw Invalid($"the output passes its {_maxOutput}-byte limit.");
            _out[_outLen++] = b;
        }

        // ------------------------------------------------------------------ the stream

        private void Run()
        {
            int wbits = ReadWindowBits();
            long windowSize = (1L << wbits) - 16;

            while (true)
            {
                bool isLast = ReadBit() == 1;
                if (isLast && ReadBit() == 1) return;                     // ISLASTEMPTY

                int nibblesCode = ReadBits(2);
                if (nibblesCode == 3)
                {
                    // Metadata (MNIBBLES 0): skipped, not output.
                    if (ReadBit() != 0) throw Invalid("a metadata block's reserved bit is set.");
                    int skipBytes = ReadBits(2);
                    int skipLength = 0;
                    for (int i = 0; i < skipBytes; i++)
                    {
                        int b = ReadBits(8);
                        if (i == skipBytes - 1 && skipBytes > 1 && b == 0) throw Invalid("a metadata length has a needless zero byte.");
                        skipLength |= b << (8 * i);
                    }
                    if (skipBytes > 0) skipLength++;
                    AlignToByte();
                    for (int i = 0; i < skipLength; i++) ReadAlignedByte();
                    if (isLast) return;
                    continue;
                }

                int nibbles = nibblesCode + 4;
                int mlen = 0;
                for (int i = 0; i < nibbles; i++)
                {
                    int nibble = ReadBits(4);
                    if (i == nibbles - 1 && nibbles > 4 && nibble == 0) throw Invalid("a meta-block length has a needless zero nibble.");
                    mlen |= nibble << (4 * i);
                }
                mlen++;

                if (!isLast && ReadBit() == 1)
                {
                    // Uncompressed: MLEN bytes copied as they stand — refused before the room is made
                    // when the input does not hold them, so a declared length allocates nothing.
                    AlignToByte();
                    if (mlen > (long)(_in.Length - _pos) + (_bitCount >> 3))
                        throw Invalid("the stream ends inside an uncompressed or metadata block.");
                    Ensure(mlen);
                    for (int i = 0; i < mlen; i++) _out[_outLen++] = (byte)ReadAlignedByte();
                    continue;
                }

                DecodeMetaBlock(mlen, windowSize);
                if (isLast) return;
            }
        }

        private int ReadWindowBits()
        {
            if (ReadBit() == 0) return 16;
            int n = ReadBits(3);
            if (n != 0) return 17 + n;
            int m = ReadBits(3);
            if (m == 1) throw Invalid("the window size uses the reserved code.");
            return m == 0 ? 17 : 8 + m;
        }

        private void DecodeMetaBlock(int mlen, long windowSize)
        {
            BlockCategory literals = ReadBlockCategory();
            BlockCategory commands = ReadBlockCategory();
            BlockCategory distances = ReadBlockCategory();

            int npostfix = ReadBits(2);
            int ndirect = ReadBits(4) << npostfix;
            int postfixMask = (1 << npostfix) - 1;

            var modes = new int[literals.Types];
            for (int i = 0; i < literals.Types; i++) modes[i] = ReadBits(2);

            int ntreesL = ReadCount256();
            byte[] cmapL = ReadContextMap(64 * literals.Types, ntreesL);
            int ntreesD = ReadCount256();
            byte[] cmapD = ReadContextMap(4 * distances.Types, ntreesD);

            var literalCodes = new PrefixCode[ntreesL];
            for (int i = 0; i < ntreesL; i++) literalCodes[i] = ReadPrefixCode(256);
            var commandCodes = new PrefixCode[commands.Types];
            for (int i = 0; i < commands.Types; i++) commandCodes[i] = ReadPrefixCode(704);
            int distanceAlphabet = 16 + ndirect + (48 << npostfix);
            var distanceCodes = new PrefixCode[ntreesD];
            for (int i = 0; i < ntreesD; i++) distanceCodes[i] = ReadPrefixCode(distanceAlphabet);

            // The declared length is judged against the ceiling here, and the room made as commands
            // write: a meta-block's MLEN is the stream's word (up to 16 MiB), and making it all up
            // front let a few bytes of input allocate that much before one byte was decoded.
            long end = (long)_outLen + mlen;
            if (end > _maxOutput) throw Invalid($"the output passes its {_maxOutput}-byte limit.");
            byte[] dictionary = null;

            while (_outLen < end)
            {
                if (commands.Types >= 2 && commands.Remaining == 0) Switch(commands);
                commands.Remaining--;
                int command = ReadSymbol(commandCodes[commands.Current]);
                int cell = command >> 6;
                int insertCode = BrotliTables.CellInsert[cell] + ((command >> 3) & 7);
                int copyCode = BrotliTables.CellCopy[cell] + (command & 7);
                int insertLength = BrotliTables.InsertBase[insertCode] + ReadBits(BrotliTables.InsertExtra[insertCode]);
                int copyLength = BrotliTables.CopyBase[copyCode] + ReadBits(BrotliTables.CopyExtra[copyCode]);
                bool implicitZero = cell < 2;

                if (_outLen + (long)insertLength > end) throw Invalid("a command inserts past its meta-block's length.");
                Ensure(insertLength);
                for (int i = 0; i < insertLength; i++)
                {
                    if (literals.Types >= 2 && literals.Remaining == 0) Switch(literals);
                    literals.Remaining--;
                    int p1 = _outLen > 0 ? _out[_outLen - 1] : 0;
                    int p2 = _outLen > 1 ? _out[_outLen - 2] : 0;
                    int context;
                    switch (modes[literals.Current])
                    {
                        case 0: context = p1 & 0x3F; break;
                        case 1: context = p1 >> 2; break;
                        case 2: context = BrotliTables.Lut0[p1] | BrotliTables.Lut1[p2]; break;
                        default: context = (BrotliTables.Lut2[p1] << 3) | BrotliTables.Lut2[p2]; break;
                    }
                    _out[_outLen++] = (byte)ReadSymbol(literalCodes[cmapL[64 * literals.Current + context]]);
                }
                if (_outLen == end) break;   // the last command's copy length is ignored

                int distance;
                bool push;
                if (implicitZero)
                {
                    distance = _distRing[3];
                    push = false;
                }
                else
                {
                    if (distances.Types >= 2 && distances.Remaining == 0) Switch(distances);
                    distances.Remaining--;
                    int distanceContext = copyLength > 4 ? 3 : copyLength - 2;
                    int dcode = ReadSymbol(distanceCodes[cmapD[4 * distances.Current + distanceContext]]);
                    push = dcode != 0;
                    if (dcode < 16)
                    {
                        distance = ShortDistance(dcode);
                        if (distance <= 0) throw Invalid("a short distance code resolves to no distance.");
                    }
                    else if (dcode < 16 + ndirect)
                    {
                        distance = dcode - 15;
                    }
                    else
                    {
                        int d = dcode - ndirect - 16;
                        int ndistbits = 1 + (d >> (npostfix + 1));
                        int hcode = d >> npostfix;
                        int lcode = d & postfixMask;
                        long offset = ((2L + (hcode & 1)) << ndistbits) - 4;
                        long value = ((offset + ReadBits(ndistbits)) << npostfix) + lcode + ndirect + 1;
                        if (value > int.MaxValue) throw Invalid("a distance is out of range.");
                        distance = (int)value;
                    }
                }

                long maxDistance = Math.Min(windowSize, _outLen);
                if (distance <= maxDistance)
                {
                    if (push) Push(distance);
                    if (_outLen + (long)copyLength > end) throw Invalid("a copy runs past its meta-block's length.");
                    Ensure(copyLength);
                    int from = _outLen - distance;
                    for (int i = 0; i < copyLength; i++) _out[_outLen++] = _out[from + i];
                }
                else
                {
                    // §8: a static dictionary word, not pushed to the ring of distances.
                    if (copyLength < 4 || copyLength > 24) throw Invalid("a dictionary reference has a length outside 4..24.");
                    long wordId = distance - maxDistance - 1;
                    int bits = BrotliTables.NdBits[copyLength];
                    long words = 1L << bits;
                    int index = (int)(wordId % words);
                    long transform = wordId >> bits;
                    if (transform > 120) throw Invalid("a dictionary reference names a transform past the 121st.");
                    dictionary ??= BrotliTables.Dictionary;
                    int written = WriteTransformedWord(dictionary, BrotliTables.DOffset[copyLength] + index * copyLength,
                                                       copyLength, (int)transform, end);
                    if (written < 0) throw Invalid("a dictionary word runs past its meta-block's length.");
                }
            }
        }

        private int ShortDistance(int code)
        {
            switch (code)
            {
                case 0: return _distRing[3];
                case 1: return _distRing[2];
                case 2: return _distRing[1];
                case 3: return _distRing[0];
                default:
                {
                    // 4..9 from the last distance, 10..15 from the second-to-last: -1, +1, -2, +2, -3, +3.
                    int base_ = code < 10 ? _distRing[3] : _distRing[2];
                    int k = (code - 4) % 6;
                    int delta = (k >> 1) + 1;
                    return (k & 1) == 0 ? base_ - delta : base_ + delta;
                }
            }
        }

        private void Push(int distance)
        {
            _distRing[0] = _distRing[1];
            _distRing[1] = _distRing[2];
            _distRing[2] = _distRing[3];
            _distRing[3] = distance;
        }

        /// <summary>Writes the transformed word (Appendix B, §8) and returns its length, or -1 when it would pass <paramref name="end"/>.</summary>
        private int WriteTransformedWord(byte[] dictionary, int offset, int length, int transform, long end)
        {
            byte[] prefix = BrotliTables.TransformPrefix[transform];
            byte[] suffix = BrotliTables.TransformSuffix[transform];
            int type = BrotliTables.TransformType[transform];

            int skipFirst = type >= 3 && type <= 11 ? type - 2 : 0;
            int skipLast = type >= 12 ? type - 11 : 0;
            int wordLength = Math.Max(0, length - skipFirst - skipLast);
            int total = prefix.Length + wordLength + suffix.Length;
            if (_outLen + (long)total > end) return -1;
            Ensure(total);

            foreach (byte b in prefix) _out[_outLen++] = b;
            int wordStart = _outLen;
            Buffer.BlockCopy(dictionary, offset + Math.Min(skipFirst, length), _out, _outLen, wordLength);
            _outLen += wordLength;
            if (type == 1 && wordLength > 0) Ferment(wordStart, wordLength);
            else if (type == 2)
                for (int i = 0; i < wordLength; ) i += Ferment(wordStart + i, wordLength - i);
            foreach (byte b in suffix) _out[_outLen++] = b;
            return total;
        }

        /// <summary>§8's <c>Ferment</c> on the word in the output at <paramref name="at"/>, <paramref name="remaining"/> bytes long.</summary>
        private int Ferment(int at, int remaining)
        {
            byte c = _out[at];
            if (c < 192)
            {
                if (c >= 97 && c <= 122) _out[at] = (byte)(c ^ 32);
                return 1;
            }
            if (c < 224)
            {
                if (remaining > 1) _out[at + 1] ^= 32;
                return 2;
            }
            if (remaining > 2) _out[at + 2] ^= 5;
            return 3;
        }
    }
}
