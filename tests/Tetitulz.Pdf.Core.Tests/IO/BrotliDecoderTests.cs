// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.IO.Brotli;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;
using Ours = Tetitulz.Pdf.Core.IO.Brotli.BrotliDecoder;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// The library's own Brotli decoder (RFC 7932). Every stream but the hand-built ones is made by .NET's
/// <see cref="BrotliEncoder"/>
/// — Google's C encoder inside the runtime — so the decoder is checked against an implementation it
/// shares nothing with; the hand-built ones pin what an encoder may never emit for a short input, the
/// static dictionary's transforms one by one, against the RFC's own definitions.
/// </summary>
public class BrotliDecoderTests
{
    private static byte[] Compress(byte[] data, int quality, int window)
    {
        var destination = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        Assert.True(BrotliEncoder.TryCompress(data, destination, out int written, quality, window));
        return destination.AsSpan(0, written).ToArray();
    }

    private static readonly string[] Words =
    {
        "the", "of", "and", "information", "available", "between", "government", "international",
        "development", "national", "following", "different", "community", "experience", "University",
        "important", "including", "software", "children", "together", "something", "December", "However,",
        "sometimes", "été", "для", "के", "()", "<div>", "http://www.", ".com/",
    };

    private static byte[] Text(int bytes, int seed)
    {
        var random = new Random(seed);
        var sb = new StringBuilder();
        while (sb.Length < bytes)
        {
            sb.Append(Words[random.Next(Words.Length)]);
            sb.Append(random.Next(9) == 0 ? ". " : random.Next(7) == 0 ? ",\n" : " ");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] Random(int bytes, int seed)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Repetitive(int bytes)
    {
        var data = new byte[bytes];
        for (int i = 0; i < bytes; i++) data[i] = (byte)("abcabcabd"[i % 9] + (i / 5000 % 3));
        return data;
    }

    public static IEnumerable<object[]> Streams()
    {
        foreach (int quality in new[] { 0, 1, 5, 9, 11 })
            foreach (int window in new[] { 10, 16, 22, 24 })
                foreach (string kind in new[] { "text", "random", "repetitive", "empty", "one" })
                    yield return new object[] { quality, window, kind };
    }

    /// <summary>What .NET's encoder makes of text, noise, repetition, nothing and one byte, at every quality class and window, comes back byte for byte.</summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public void WhatTheRuntimesEncoderMakes_DecodesToTheOriginal(int quality, int window, string kind)
    {
        byte[] plain = kind switch
        {
            "text" => Text(70_000, quality * 31 + window),
            "random" => Random(70_000, quality + window),
            "repetitive" => Repetitive(150_000),
            "one" => new byte[] { 0x42 },
            _ => Array.Empty<byte>(),
        };

        byte[] decoded = Ours.Decode(Compress(plain, quality, window), long.MaxValue);

        Assert.Equal(plain, decoded);
    }

    /// <summary>
    /// A stream far longer than its window — 3 MB of text through a 1 KiB window at quality 11 — so
    /// distances beyond the window are dictionary words and meta-blocks follow one another.
    /// </summary>
    [Fact]
    public void ALongStreamThroughASmallWindow_DecodesToTheOriginal()
    {
        byte[] plain = Text(3_000_000, 7);
        byte[] compressed = Compress(plain, 11, 10);
        Assert.True(compressed.Length < plain.Length / 2, "the fixture did not compress");

        Assert.Equal(plain, Ours.Decode(compressed, long.MaxValue));
    }

    /// <summary>The output limit is enforced as the output passes it: a megabyte of zeros refused at a thousand bytes, accepted at its own size.</summary>
    [Fact]
    public void AnOutputPastTheLimit_IsRefusedAsABomb()
    {
        byte[] zeros = new byte[1 << 20];
        byte[] compressed = Compress(zeros, 5, 22);
        Assert.True(compressed.Length < 100, "the bomb is " + compressed.Length + " bytes");

        var ex = Assert.Throws<PdfFilterException>(() => Ours.Decode(compressed, 1000));
        Assert.Contains("byte limit", ex.Message, StringComparison.Ordinal);
        Assert.Equal(zeros.Length, Ours.Decode(compressed, zeros.Length).Length);
        Assert.Throws<PdfFilterException>(() => Ours.Decode(compressed, zeros.Length - 1));
    }

    /// <summary>Every truncation of a stream is refused or — where the cut fell in padding — decodes to the original; never a different answer.</summary>
    [Fact]
    public void ATruncatedStream_IsRefused()
    {
        byte[] plain = Text(5000, 3);
        byte[] compressed = Compress(plain, 9, 18);
        int refused = 0;
        for (int cut = 0; cut < compressed.Length; cut++)
        {
            byte[] prefix = compressed.AsSpan(0, cut).ToArray();
            try
            {
                Assert.Equal(plain, Ours.Decode(prefix, long.MaxValue));
            }
            catch (PdfFilterException)
            {
                refused++;
            }
        }
        Assert.True(refused >= compressed.Length - 1, refused + " of " + compressed.Length + " truncations refused");
    }

    /// <summary>A corrupted stream — single bytes flipped throughout — either decodes or is refused with the typed exception; no other exception escapes and nothing runs away.</summary>
    [Fact]
    public void ACorruptStream_IsOnlyEverRefusedWithTheTypedException()
    {
        byte[] compressed = Compress(Text(20_000, 11), 11, 16);
        var random = new Random(5);
        int refused = 0;
        for (int trial = 0; trial < 400; trial++)
        {
            byte[] bad = (byte[])compressed.Clone();
            bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            try { Ours.Decode(bad, 1 << 22); }
            catch (PdfFilterException) { refused++; }
        }
        Assert.True(refused > 100, "only " + refused + " of 400 corruptions were refused");
    }

    /// <summary>Appendix A's dictionary: its length and §8's offsets agree, and its first word is "time".</summary>
    [Fact]
    public void TheStaticDictionary_IsAppendixAs()
    {
        byte[] dictionary = BrotliTables.Dictionary;
        Assert.Equal(BrotliTables.DictionarySize, dictionary.Length);
        Assert.Equal(BrotliTables.DictionarySize, BrotliTables.DOffset[24] + (24 << BrotliTables.NdBits[24]));
        Assert.Equal("time", Encoding.ASCII.GetString(dictionary, 0, 4));
    }

    // ------------------------------------------------------------------ hand-built streams

    /// <summary>Bits written LSB first, as RFC 7932 reads them.</summary>
    private sealed class Bits
    {
        private readonly List<byte> _bytes = new();
        private int _bit;
        internal void Write(int value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_bit == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
                _bit = (_bit + 1) & 7;
            }
        }
        internal byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>
    /// One last meta-block whose one command copies a static dictionary word: length
    /// <paramref name="length"/>, word <paramref name="index"/>, transform <paramref name="transform"/>,
    /// with <paramref name="outputLength"/> bytes declared — the shape of §9.2 and §9.3 written out field
    /// by field, every prefix code a one-symbol simple code (§3.4), so nothing but the fields is read.
    /// </summary>
    private static byte[] DictionaryWordStream(int length, int index, int transform, int outputLength)
    {
        var w = new Bits();
        w.Write(0, 1);                                  // WBITS 16
        w.Write(1, 1); w.Write(0, 1);                   // ISLAST, not empty
        w.Write(0, 2);                                  // MNIBBLES 4
        w.Write(outputLength - 1, 16);                  // MLEN - 1
        w.Write(0, 1); w.Write(0, 1); w.Write(0, 1);    // NBLTYPESL, I, D = 1
        w.Write(0, 2); w.Write(0, 4);                   // NPOSTFIX 0, NDIRECT 0
        w.Write(0, 2);                                  // the literal context mode
        w.Write(0, 1); w.Write(0, 1);                   // NTREESL, NTREESD = 1
        w.Write(1, 2); w.Write(0, 2); w.Write(0, 8);    // literal code: one symbol
        int command = 128 + (length - 2);               // insert code 0, copy code length - 2, explicit distance
        w.Write(1, 2); w.Write(0, 2); w.Write(command, 10);

        // The distance: past the window (nothing is written yet, so the limit is 0) by the word's id + 1.
        int wordId = (transform << BrotliTables.NdBits[length]) | index;
        int distance = wordId + 1;
        for (int dcode = 16; dcode < 64; dcode++)
        {
            int hcode = dcode - 16, ndistbits = 1 + (hcode >> 1);
            int offset = ((2 + (hcode & 1)) << ndistbits) - 4;
            if (distance - 1 < offset || distance - 1 >= offset + (1 << ndistbits)) continue;
            w.Write(1, 2); w.Write(0, 2); w.Write(dcode, 6);     // distance code: one symbol (alphabet 64)
            w.Write(distance - 1 - offset, ndistbits);          // its extra bits — the command itself reads none for its codes
            return w.ToArray();
        }
        throw new InvalidOperationException("no distance code reaches " + distance);
    }

    /// <summary>
    /// A meta-block declaring 16 MiB and holding nothing — uncompressed, or compressed with a first
    /// command that is malformed — is refused, and the refusal allocates a few kilobytes: the room is
    /// made as bytes are written, not from the declared MLEN (which, made up front, was 16 MiB for a
    /// stream of a dozen bytes). The ceiling is 64 MiB, so the length passes it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADeclaredMetaBlockLength_AllocatesNothingBeforeItsBytes(bool uncompressed)
    {
        var w = new Bits();
        w.Write(0, 1);                                  // WBITS 16
        w.Write(0, 1);                                  // ISLAST 0
        w.Write(2, 2);                                  // MNIBBLES 6
        w.Write(0xFFFFFF, 24);                          // MLEN - 1: 16 MiB
        if (uncompressed)
        {
            w.Write(1, 1);                              // ISUNCOMPRESSED, and no byte of it
        }
        else
        {
            w.Write(0, 1);
            w.Write(0, 1); w.Write(0, 1); w.Write(0, 1);    // NBLTYPESL, I, D = 1
            w.Write(0, 2); w.Write(0, 4);                   // NPOSTFIX 0, NDIRECT 0
            w.Write(0, 2);                                  // the literal context mode
            w.Write(0, 1); w.Write(0, 1);                   // NTREESL, NTREESD = 1
            w.Write(1, 2); w.Write(0, 2); w.Write(65, 8);   // literal code: one symbol
            w.Write(1, 2); w.Write(0, 2); w.Write(0, 10);   // command 0: insert 0, copy 2, the last distance
            w.Write(1, 2); w.Write(0, 2); w.Write(0, 6);    // distance code: one symbol
        }
        byte[] stream = w.ToArray();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PdfFilterException>(() => Ours.Decode(stream, 64L << 20));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 256 * 1024);
    }

    /// <summary>
    /// A dictionary word written when the output has filled its room exactly — literals up to the
    /// first buffer's capacity (four bytes an input byte), then a four-byte word — is given room of
    /// its own, as literals and copies are: the word lands after them, whole.
    /// </summary>
    [Fact]
    public void ADictionaryWordAfterAFullBuffer_IsGivenRoom()
    {
        for (int literals = 16; literals <= 96; literals++)
        {
            byte[] stream = LiteralsThenWord(literals);
            if (Math.Max(16, 4 * stream.Length) != literals) continue;   // the first buffer is exactly the literals

            byte[] decoded = Ours.Decode(stream, 1 << 20);

            byte[] word = new byte[4];
            Array.Copy(BrotliTables.Dictionary, BrotliTables.DOffset[4], word, 0, 4);
            Assert.Equal(Enumerable.Repeat((byte)'A', literals).Concat(word).ToArray(), decoded);
            return;
        }
        throw new InvalidOperationException("no literal count fills the first buffer exactly");
    }

    /// <summary>One last meta-block: <paramref name="literals"/> 'A's, then dictionary word 0 of length 4, untransformed.</summary>
    private static byte[] LiteralsThenWord(int literals)
    {
        int insertCode = -1;
        for (int c = 0; c < BrotliTables.InsertBase.Length; c++)
            if (BrotliTables.InsertBase[c] <= literals && literals < BrotliTables.InsertBase[c] + (1 << BrotliTables.InsertExtra[c])) { insertCode = c; break; }
        int copyCode = Array.FindIndex(BrotliTables.CopyBase, b => b == 4);
        int command = -1;
        for (int cell = 2; cell < BrotliTables.CellInsert.Length && command < 0; cell++)   // cells 0, 1 take the last distance
        {
            int i = insertCode - BrotliTables.CellInsert[cell], k = copyCode - BrotliTables.CellCopy[cell];
            if (i >= 0 && i < 8 && k >= 0 && k < 8) command = (cell << 6) | (i << 3) | k;
        }

        var w = new Bits();
        w.Write(0, 1);                                  // WBITS 16
        w.Write(1, 1); w.Write(0, 1);                   // ISLAST, not empty
        w.Write(0, 2);                                  // MNIBBLES 4
        w.Write(literals + 4 - 1, 16);                  // MLEN - 1
        w.Write(0, 1); w.Write(0, 1); w.Write(0, 1);    // NBLTYPESL, I, D = 1
        w.Write(0, 2); w.Write(0, 4);                   // NPOSTFIX 0, NDIRECT 0
        w.Write(0, 2);                                  // the literal context mode
        w.Write(0, 1); w.Write(0, 1);                   // NTREESL, NTREESD = 1
        w.Write(1, 2); w.Write(0, 2); w.Write('A', 8);  // literal code: one symbol
        w.Write(1, 2); w.Write(0, 2); w.Write(command, 10);

        // The distance: one past the literals written (the window's limit then), word id 0.
        int distance = literals + 1;
        for (int dcode = 16; dcode < 64; dcode++)
        {
            int hcode = dcode - 16, ndistbits = 1 + (hcode >> 1);
            int offset = ((2 + (hcode & 1)) << ndistbits) - 4;
            if (distance - 1 < offset || distance - 1 >= offset + (1 << ndistbits)) continue;
            w.Write(1, 2); w.Write(0, 2); w.Write(dcode, 6);     // distance code: one symbol
            w.Write(literals - BrotliTables.InsertBase[insertCode], BrotliTables.InsertExtra[insertCode]);
            w.Write(distance - 1 - offset, ndistbits);
            return w.ToArray();
        }
        throw new InvalidOperationException("no distance code reaches " + distance);
    }

    /// <summary>§8's Ferment, written from the RFC, for the expected values below.</summary>
    private static int Ferment(byte[] word, int pos)
    {
        if (word[pos] < 192) { if (word[pos] >= 97 && word[pos] <= 122) word[pos] ^= 32; return 1; }
        if (word[pos] < 224) { if (pos + 1 < word.Length) word[pos + 1] ^= 32; return 2; }
        if (pos + 2 < word.Length) word[pos + 2] ^= 5;
        return 3;
    }

    /// <summary>
    /// A dictionary word under transforms of every kind comes out as Appendix B defines it: identity,
    /// FermentFirst, FermentAll, OmitFirst, OmitLast, and a prefix and suffix — on "time" (word 0 of
    /// length 4) and on the first word of length 6 whose first byte is a two-byte UTF-8 lead.
    /// </summary>
    [Theory]
    [InlineData(4, 0, 0, "time")]
    [InlineData(4, 0, 9, "Time")]
    [InlineData(4, 0, 44, "TIME")]
    [InlineData(4, 0, 3, "ime")]
    [InlineData(4, 0, 12, "tim")]
    [InlineData(4, 0, 5, "time the ")]
    [InlineData(4, 0, 73, " the time of the ")]
    public void ADictionaryWordUnderATransform_IsAppendixBs(int length, int index, int transform, string expected)
    {
        byte[] stream = DictionaryWordStream(length, index, transform, expected.Length);
        Assert.Equal(expected, Encoding.UTF8.GetString(Ours.Decode(stream, long.MaxValue)));
    }

    /// <summary>FermentFirst and FermentAll on a word that starts with a two-byte UTF-8 sequence flip its second byte, by §8's rule.</summary>
    [Fact]
    public void FermentOnAMultiByteWord_FlipsTheFollowingByte()
    {
        byte[] dictionary = BrotliTables.Dictionary;
        int length = 6, start = BrotliTables.DOffset[length];
        int index = Enumerable.Range(0, 1 << BrotliTables.NdBits[length])
            .First(i => dictionary[start + i * length] >= 192 && dictionary[start + i * length] < 224);
        byte[] word = dictionary.AsSpan(start + index * length, length).ToArray();

        foreach (int transform in new[] { 9, 44 })
        {
            byte[] expected = (byte[])word.Clone();
            if (transform == 9) Ferment(expected, 0);
            else for (int i = 0; i < expected.Length; ) i += Ferment(expected, i);
            Assert.NotEqual(word, expected);

            Assert.Equal(expected, Ours.Decode(DictionaryWordStream(length, index, transform, length), long.MaxValue));
        }
    }

    /// <summary>FermentFirst on a word starting with a three-byte UTF-8 sequence changes its third byte by 5, by §8's rule.</summary>
    [Fact]
    public void FermentOnAThreeByteLead_ChangesTheThirdByte()
    {
        byte[] dictionary = BrotliTables.Dictionary;
        int length = 6, start = BrotliTables.DOffset[length];
        int index = Enumerable.Range(0, 1 << BrotliTables.NdBits[length]).First(i => dictionary[start + i * length] >= 224);
        byte[] expected = dictionary.AsSpan(start + index * length, length).ToArray();
        expected[2] ^= 5;

        Assert.Equal(expected, Ours.Decode(DictionaryWordStream(length, index, 9, length), long.MaxValue));
    }

    /// <summary>A dictionary word longer than what is left of its meta-block is refused for that reason (§9.3).</summary>
    [Fact]
    public void ADictionaryWordPastItsMetaBlock_IsRefused()
    {
        var ex = Assert.Throws<PdfFilterException>(() => Ours.Decode(DictionaryWordStream(4, 0, 0, 3), long.MaxValue));
        Assert.Contains("runs past its meta-block", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A metadata block (MNIBBLES 0, §9.2) is skipped — its bytes are not output — and an uncompressed
    /// meta-block's bytes are copied as they stand; then a last, empty meta-block ends the stream.
    /// </summary>
    [Fact]
    public void AMetadataBlockIsSkipped_AndAnUncompressedOneCopied()
    {
        var w = new Bits();
        w.Write(0, 1);                                          // WBITS 16
        w.Write(0, 1); w.Write(3, 2); w.Write(0, 1);            // not last; MNIBBLES 0 (metadata); reserved
        w.Write(1, 2); w.Write(2, 8);                           // MSKIPBYTES 1; MSKIPLEN - 1 = 2
        w.Write(0, 1);                                          // to the byte boundary (15 bits so far)
        foreach (char c in "XYZ") w.Write(c, 8);                // three bytes of metadata
        w.Write(0, 1); w.Write(0, 2); w.Write(2, 16); w.Write(1, 1);   // not last; MNIBBLES 4; MLEN - 1 = 2; uncompressed
        w.Write(0, 4);                                          // to the byte boundary
        foreach (char c in "abc") w.Write(c, 8);
        w.Write(1, 1); w.Write(1, 1);                           // ISLAST, ISLASTEMPTY

        Assert.Equal("abc", Encoding.ASCII.GetString(Ours.Decode(w.ToArray(), long.MaxValue)));
    }

    /// <summary>A meta-block length written in five nibbles whose last is zero is refused, as §9.2 says.</summary>
    [Fact]
    public void ALengthWithANeedlessZeroNibble_IsRefused()
    {
        var w = new Bits();
        w.Write(0, 1);                                          // WBITS 16
        w.Write(0, 1); w.Write(1, 2); w.Write(2, 20); w.Write(1, 1);    // MNIBBLES 5, the fifth zero; uncompressed
        w.Write(0, 7);
        foreach (char c in "abc") w.Write(c, 8);
        w.Write(1, 1); w.Write(1, 1);

        var ex = Assert.Throws<PdfFilterException>(() => Ours.Decode(w.ToArray(), long.MaxValue));
        Assert.Contains("needless zero nibble", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Block switching (§6): two literal block types, each with its own prefix code (one draws
    /// <c>A</c>, the other <c>B</c>, through the context map), a one-literal block count, and every
    /// switch the "+1" symbol — so the second switch wraps from the last type back to type 0. Four
    /// literals read <c>ABAB</c>.
    /// </summary>
    [Fact]
    public void BlockSwitches_WrapPastTheLastType()
    {
        var w = new Bits();
        w.Write(0, 1);                                          // WBITS 16
        w.Write(1, 1); w.Write(0, 1);                           // ISLAST, not empty
        w.Write(0, 2); w.Write(3, 16);                          // MNIBBLES 4, MLEN - 1 = 3
        w.Write(1, 1); w.Write(0, 3);                           // NBLTYPESL = 2
        w.Write(1, 2); w.Write(0, 2); w.Write(1, 2);            //   block type code: the one symbol 1 ("+1")
        w.Write(1, 2); w.Write(0, 2); w.Write(0, 5);            //   block count code: the one symbol 0 (base 1, 2 extra bits)
        w.Write(0, 2);                                          //   the first block count: 1
        w.Write(0, 1); w.Write(0, 1);                           // NBLTYPESI, NBLTYPESD = 1
        w.Write(0, 2); w.Write(0, 4);                           // NPOSTFIX, NDIRECT
        w.Write(0, 2); w.Write(0, 2);                           // both literal types LSB6
        w.Write(1, 1); w.Write(0, 3);                           // NTREESL = 2
        w.Write(0, 1);                                          //   RLEMAX 0
        w.Write(1, 2); w.Write(1, 2); w.Write(0, 1); w.Write(1, 1);   //   its code: symbols 0 and 1, one bit each
        for (int i = 0; i < 64; i++) w.Write(0, 1);             //   type 0's 64 contexts -> code 0
        for (int i = 0; i < 64; i++) w.Write(1, 1);             //   type 1's -> code 1
        w.Write(0, 1);                                          //   no inverse move-to-front
        w.Write(0, 1);                                          // NTREESD = 1
        w.Write(1, 2); w.Write(0, 2); w.Write('A', 8);          // literal code 0: 'A'
        w.Write(1, 2); w.Write(0, 2); w.Write('B', 8);          // literal code 1: 'B'
        w.Write(1, 2); w.Write(0, 2); w.Write(32, 10);          // command code: insert 4, copy code 0, implicit distance
        w.Write(1, 2); w.Write(0, 2); w.Write(0, 6);            // distance code (never read)
        w.Write(0, 2);                                          // the second block's count: 1
        w.Write(0, 2);                                          // the third's (the wrap)
        w.Write(0, 2);                                          // the fourth's

        Assert.Equal("ABAB", Encoding.ASCII.GetString(Ours.Decode(w.ToArray(), long.MaxValue)));
    }

    /// <summary>
    /// One literal block type under context <paramref name="mode"/> (§7.1), two literal codes (code 0
    /// draws <c>a</c>, code 1 <c>b</c>) and a context map sending <paramref name="toCodeOne"/> to code 1 —
    /// so each literal is chosen by the context the byte before it gives. Four literals are inserted.
    /// </summary>
    private static byte[] ContextStream(int mode, params int[] toCodeOne)
    {
        var w = new Bits();
        w.Write(0, 1);                                          // WBITS 16
        w.Write(1, 1); w.Write(0, 1);                           // ISLAST, not empty
        w.Write(0, 2); w.Write(3, 16);                          // MNIBBLES 4, MLEN - 1 = 3
        w.Write(0, 1); w.Write(0, 1); w.Write(0, 1);            // NBLTYPESL, I, D = 1
        w.Write(0, 2); w.Write(0, 4);                           // NPOSTFIX, NDIRECT
        w.Write(mode, 2);                                       // the literal context mode
        w.Write(1, 1); w.Write(0, 3);                           // NTREESL = 2
        w.Write(0, 1);                                          //   RLEMAX 0
        w.Write(1, 2); w.Write(1, 2); w.Write(0, 1); w.Write(1, 1);   //   its code: symbols 0 and 1, one bit each
        for (int i = 0; i < 64; i++) w.Write(toCodeOne.Contains(i) ? 1 : 0, 1);
        w.Write(0, 1);                                          //   no inverse move-to-front
        w.Write(0, 1);                                          // NTREESD = 1
        w.Write(1, 2); w.Write(0, 2); w.Write('a', 8);          // literal code 0: 'a'
        w.Write(1, 2); w.Write(0, 2); w.Write('b', 8);          // literal code 1: 'b'
        w.Write(1, 2); w.Write(0, 2); w.Write(32, 10);          // command code: insert 4, copy code 0, implicit distance
        w.Write(1, 2); w.Write(0, 2); w.Write(0, 6);            // distance code (never read)
        return w.ToArray();
    }

    /// <summary>
    /// LSB6 takes the low six bits of the byte before (§7.1): after 'a' (0x61) the context is 33 and
    /// after 'b' 34 — 1 and 2 under five bits, which the map sends elsewhere. With 33 mapped to code 1:
    /// a, then b, then (34) a, then b.
    /// </summary>
    [Fact]
    public void TheLsb6ContextMode_ChoosesByTheLowSixBits()
        => Assert.Equal("abab", Encoding.ASCII.GetString(Ours.Decode(ContextStream(0, 33), long.MaxValue)));

    /// <summary>MSB6 takes the high six bits (§7.1): 'a' and 'b' both give 24 (12 under a shift of three), mapped to code 1: a, then b, b, b.</summary>
    [Fact]
    public void TheMsb6ContextMode_ChoosesByTheHighSixBits()
        => Assert.Equal("abbb", Encoding.ASCII.GetString(Ours.Decode(ContextStream(1, 24), long.MaxValue)));

    /// <summary>A dictionary reference naming a transform past the 121st is refused, as §8 says; the 121st itself — a space, FermentFirst, then =' — is read.</summary>
    [Fact]
    public void ATransformPastTheLast_IsRefused()
    {
        Assert.Throws<PdfFilterException>(() => Ours.Decode(DictionaryWordStream(4, 0, 121, 4), long.MaxValue));
        Assert.Equal(" Time='", Encoding.ASCII.GetString(Ours.Decode(DictionaryWordStream(4, 0, 120, 7), long.MaxValue)));
    }
}
