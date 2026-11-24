// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>
    /// Decodes a GIF's first frame to 8-bit RGB, expanding the palette and de-interlacing.
    ///
    /// <para><b>The LZW here is not the one in <c>Core.IO.Filters.LzwDecodeFilter</c>, and must
    /// not be replaced by it.</b> They share a name and nothing else: GIF packs codes
    /// <b>LSB-first</b> with a per-image initial code size read from the stream, so its clear and
    /// end codes move with that size; PDF's <c>LZWDecode</c> packs <b>MSB-first</b> with a fixed
    /// clear code of 256, end of 257, and an <c>EarlyChange</c> quirk GIF has no equivalent of.
    /// Unifying them would be centralising two different algorithms because they have the same
    /// three letters in front of them.</para>
    ///
    /// <para>Only the first frame is decoded — an animation has no meaning in a page — and the
    /// caller is told so through <c>hasMoreFrames</c> rather than being left to wonder.</para>
    /// </summary>
    public static class GifImageDecoder
    {
        /// <summary>Largest image this decoder will materialise, matching the PNG path's guard.</summary>
        private const long MaxPixels = 40_000_000L;

        /// <summary>
        /// Decodes the first frame. <paramref name="rgb"/> is <c>width*height*3</c> bytes;
        /// <paramref name="alpha"/> is <c>width*height</c> bytes when the frame declares a
        /// transparent index, else <c>null</c>.
        /// </summary>
        public static bool TryDecode(byte[] bytes, out byte[] rgb, out byte[] alpha,
            out int width, out int height, out bool hasMoreFrames)
        {
            rgb = null; alpha = null; width = 0; height = 0; hasMoreFrames = false;
            _lastError = null;
            if (bytes == null || bytes.Length < 13) return Fail("the data is too short to be a GIF");
            if (bytes[0] != 'G' || bytes[1] != 'I' || bytes[2] != 'F') return Fail("the data does not begin with the GIF signature");

            int pos = 6;
            int screenW = Read16(bytes, pos); pos += 2;
            int screenH = Read16(bytes, pos); pos += 2;
            byte packed = bytes[pos++];
            pos++;   // background colour index
            pos++;   // pixel aspect ratio

            byte[] globalTable = null;
            if ((packed & 0x80) != 0)
            {
                int entries = 1 << ((packed & 0x07) + 1);
                if (pos + entries * 3 > bytes.Length) return Fail("the global colour table is truncated");
                globalTable = new byte[entries * 3];
                Array.Copy(bytes, pos, globalTable, 0, globalTable.Length);
                pos += globalTable.Length;
            }

            int transparentIndex = -1;
            bool frameDecoded = false;

            while (pos < bytes.Length)
            {
                byte block = bytes[pos++];

                if (block == 0x3B) break;                      // trailer

                if (block == 0x21)                             // extension
                {
                    if (pos >= bytes.Length) break;
                    byte label = bytes[pos++];
                    if (label == 0xF9 && pos < bytes.Length && bytes[pos] == 4)
                    {
                        // Graphic Control Extension: the transparent-index flag lives here.
                        if (pos + 5 > bytes.Length) break;
                        byte gcePacked = bytes[pos + 1];
                        if ((gcePacked & 0x01) != 0) transparentIndex = bytes[pos + 4];
                    }
                    if (!SkipSubBlocks(bytes, ref pos)) break;
                    continue;
                }

                if (block != 0x2C) break;                      // not an image descriptor — malformed

                if (frameDecoded) { hasMoreFrames = true; break; }

                if (pos + 9 > bytes.Length) return Fail("an image descriptor is truncated");
                pos += 4;                                      // left, top — the frame is composited at 0,0
                int frameW = Read16(bytes, pos); pos += 2;
                int frameH = Read16(bytes, pos); pos += 2;
                byte imgPacked = bytes[pos++];

                byte[] localTable = null;
                if ((imgPacked & 0x80) != 0)
                {
                    int entries = 1 << ((imgPacked & 0x07) + 1);
                    if (pos + entries * 3 > bytes.Length) return Fail("a local colour table is truncated");
                    localTable = new byte[entries * 3];
                    Array.Copy(bytes, pos, localTable, 0, localTable.Length);
                    pos += localTable.Length;
                }
                bool interlaced = (imgPacked & 0x40) != 0;

                byte[] table = localTable ?? globalTable;
                if (table == null) return Fail("the frame has no colour table, global or local");

                if (frameW <= 0 || frameH <= 0) return Fail("the frame declares a zero width or height");
                if ((long)frameW * frameH > MaxPixels) return Fail($"the frame is {frameW}x{frameH}, past the {MaxPixels}-pixel ceiling");

                if (pos >= bytes.Length) return Fail("the frame ends before its LZW code size");
                int minCodeSize = bytes[pos++];
                if (minCodeSize < 2 || minCodeSize > 8) return Fail($"the LZW minimum code size {minCodeSize} is outside 2 to 8");

                byte[] data = ReadSubBlocks(bytes, ref pos);
                if (data == null) return Fail("the frame's data sub-blocks run past the end of the file");

                // A frame its data could not fill is a header choosing the cost, not a short stream drawn as
                // far as it goes: refused before the frame's buffers are made, as PNG and BMP refuse theirs.
                long most = MostSamples(data.Length, minCodeSize);
                if ((long)frameW * frameH > most)
                    return Fail($"the frame's {data.Length} bytes of LZW data hold at most {most} of its {frameW}x{frameH} pixels");

                byte[] indices = LzwDecode(data, minCodeSize, frameW * frameH, out int decodedPixels);
                if (indices == null) return Fail("the frame's LZW data names a code its table does not hold");
                // A short stream is decoded as far as it goes, the rest left at palette entry 0 as
                // browsers draw it; said here, because it is otherwise a confident partial image.
                if (decodedPixels < frameW * frameH)
                    _lastError = $"the frame's pixel data ended after {decodedPixels} of {frameW * frameH} pixels; the rest are palette entry 0";

                width = frameW;
                height = frameH;
                rgb = new byte[frameW * frameH * 3];
                if (transparentIndex >= 0) alpha = new byte[frameW * frameH];

                int paletteEntries = table.Length / 3;
                for (int row = 0; row < frameH; row++)
                {
                    int srcRow = interlaced ? InterlacedRow(row, frameH) : row;
                    if (srcRow < 0 || srcRow >= frameH) continue;
                    for (int x = 0; x < frameW; x++)
                    {
                        int idx = indices[row * frameW + x];
                        int dst = (srcRow * frameW + x) * 3;
                        if (idx < paletteEntries)
                        {
                            rgb[dst] = table[idx * 3];
                            rgb[dst + 1] = table[idx * 3 + 1];
                            rgb[dst + 2] = table[idx * 3 + 2];
                        }
                        if (alpha != null) alpha[srcRow * frameW + x] = idx == transparentIndex ? (byte)0 : (byte)255;
                    }
                }
                frameDecoded = true;
                // Keep walking: a second image descriptor means this is an animation, which the
                // caller is entitled to know about rather than silently receiving frame one.
            }

            if (!frameDecoded && _lastError == null) _lastError = "the file holds no image";
            return frameDecoded;
        }

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the last <see cref="TryDecode"/> on this thread returned <c>false</c>, or what it
        /// had to make up when it returned <c>true</c> (a pixel stream that ended early), or
        /// <c>null</c>. Reset at the start of every call.
        /// </summary>
        /// <remarks>
        /// It had no slot at all: twelve refusals returned a bare <c>false</c>, and a truncated
        /// frame came back <c>true</c> with its tail silently at palette entry 0 — the
        /// indistinguishable-from-success shape the Diagnostics Pattern forbids.
        /// </remarks>
        public static string LastError => _lastError;

        private static bool Fail(string reason)
        {
            _lastError = reason;
            return false;
        }

        /// <summary>
        /// The destination row for interlaced pass ordering: rows arrive in four passes
        /// (every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1).
        /// </summary>
        private static int InterlacedRow(int sequential, int height)
        {
            int pass1 = (height + 7) / 8;
            int pass2 = (height + 3) / 8;
            int pass3 = (height + 1) / 4;

            if (sequential < pass1) return sequential * 8;
            sequential -= pass1;
            if (sequential < pass2) return sequential * 8 + 4;
            sequential -= pass2;
            if (sequential < pass3) return sequential * 4 + 2;
            sequential -= pass3;
            return sequential * 2 + 1;
        }

        private static bool SkipSubBlocks(byte[] b, ref int pos)
        {
            while (pos < b.Length)
            {
                int len = b[pos++];
                if (len == 0) return true;
                if (pos + len > b.Length) return false;
                pos += len;
            }
            return false;
        }

        private static byte[] ReadSubBlocks(byte[] b, ref int pos)
        {
            var chunks = new List<byte[]>();
            int total = 0;
            while (pos < b.Length)
            {
                int len = b[pos++];
                if (len == 0) break;
                if (pos + len > b.Length) return null;
                var chunk = new byte[len];
                Array.Copy(b, pos, chunk, 0, len);
                chunks.Add(chunk);
                total += len;
                pos += len;
            }
            var all = new byte[total];
            int o = 0;
            foreach (var c in chunks) { Array.Copy(c, 0, all, o, c.Length); o += c.Length; }
            return all;
        }

        /// <summary>
        /// The most samples <paramref name="dataBytes"/> of LZW data can stand for: the codes are at least
        /// <paramref name="minCodeSize"/> + 1 bits each, and the j-th code after a clear names a string of at most
        /// j samples (each table entry is its predecessor and one more), never more than the 4 096 a full table
        /// holds — so k codes stand for at most k(k + 1) ÷ 2.
        /// </summary>
        internal static long MostSamples(int dataBytes, int minCodeSize)
        {
            const long MaxString = 4096;
            long codes = (long)dataBytes * 8 / (minCodeSize + 1);
            return codes <= MaxString
                ? codes * (codes + 1) / 2
                : MaxString * (MaxString + 1) / 2 + (codes - MaxString) * MaxString;
        }

        /// <summary>
        /// GIF's LZW: LSB-first bit packing, code size starting at <c>minCodeSize + 1</c> and
        /// growing as the dictionary fills, with clear and end codes derived from
        /// <paramref name="minCodeSize"/>.
        /// </summary>
        private static byte[] LzwDecode(byte[] data, int minCodeSize, int expectedPixels, out int decoded)
        {
            decoded = 0;
            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            const int MaxCodes = 4096;

            var prefix = new int[MaxCodes];
            var suffix = new byte[MaxCodes];
            var stack = new byte[MaxCodes];

            var output = new byte[expectedPixels];
            int outPos = 0;

            int codeSize = minCodeSize + 1;
            int next = endCode + 1;
            int previous = -1;

            int bitPos = 0;
            int totalBits = data.Length * 8;

            for (int i = 0; i < clearCode; i++) { prefix[i] = -1; suffix[i] = (byte)i; }

            while (bitPos + codeSize <= totalBits && outPos < expectedPixels)
            {
                int code = ReadBitsLsb(data, bitPos, codeSize);
                bitPos += codeSize;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    next = endCode + 1;
                    previous = -1;
                    continue;
                }
                if (code == endCode) break;
                if (code > next) return null;                 // a code the dictionary cannot hold yet

                int stackTop = 0;
                int current;

                if (code == next)
                {
                    // KwKwK: the encoder used a code it defined on this very step, so the entry is
                    // "previous entry, then the FIRST character of the previous entry". The first
                    // character is the root of its prefix chain — NOT suffix[previous], which is
                    // its last. Getting that wrong silently corrupts every image whose pixel runs
                    // repeat, which is most of them.
                    if (previous < 0) return null;
                    stack[stackTop++] = FirstChar(prefix, suffix, previous, clearCode);
                    current = previous;
                }
                else current = code;

                int guard = 0;
                while (current >= clearCode)
                {
                    if (current < 0 || current >= MaxCodes || ++guard > MaxCodes) return null;
                    stack[stackTop++] = suffix[current];
                    current = prefix[current];
                }
                if (current < 0 || current >= clearCode) return null;
                byte firstChar = (byte)current;
                stack[stackTop++] = firstChar;

                while (stackTop > 0 && outPos < expectedPixels) output[outPos++] = stack[--stackTop];

                if (previous >= 0 && next < MaxCodes)
                {
                    prefix[next] = previous;
                    suffix[next] = firstChar;
                    next++;
                    if (next < MaxCodes && (next & (next - 1)) == 0 && codeSize < 12) codeSize++;
                }
                previous = code;
            }

            decoded = outPos;
            return output;
        }

        /// <summary>The first character of a dictionary entry: the root of its prefix chain.</summary>
        private static byte FirstChar(int[] prefix, byte[] suffix, int code, int clearCode)
        {
            int guard = 0;
            while (code >= clearCode && code >= 0 && ++guard <= 4096) code = prefix[code];
            return code >= 0 && code < clearCode ? (byte)code : (byte)0;
        }

        private static int ReadBitsLsb(byte[] data, int bitPos, int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int b = bitPos + i;
                int bit = (data[b >> 3] >> (b & 7)) & 1;
                value |= bit << i;
            }
            return value;
        }

        private static int Read16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
    }
}
