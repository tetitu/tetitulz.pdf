// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Reverses the WOFF 2.0 §5.1 <c>glyf</c> transform, rebuilding <c>glyf</c> <b>and</b>
    /// <c>loca</c> together.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a re-encoding, not a decompression.</b> The transformed table is seven
    /// parallel substreams — contour counts, point counts, flags, coordinates, composite data,
    /// bounding boxes and instructions — grouped that way so Brotli sees like data together. Putting
    /// a glyph back together means interleaving all seven in the TrueType layout, and the
    /// coordinates have to be re-encoded from the §5.2 triplet form into TrueType's own
    /// flag/short/same encoding. So there is no shortcut through the Brotli step: this half has to
    /// be written whatever the decompressor is.</para>
    ///
    /// <para><b>The two tables are one job.</b> §5.3 makes the transformed <c>loca</c> empty by
    /// definition — its <c>transformLength</c> MUST be zero — because the offsets are whatever this
    /// reconstruction produces. A decoder that rebuilt <c>glyf</c> and then looked for a
    /// <c>loca</c> would find nothing and would have to invent it; here the offsets fall out of the
    /// same loop that writes the glyphs.</para>
    ///
    /// <para><b>Every count in the container is a claim.</b> <c>numGlyphs</c>, each contour count,
    /// each point count and each instruction length arrive from the font and are used to size loops
    /// and buffers. They are checked against what the substreams actually contain rather than
    /// trusted — a substream that runs out is a refusal, not a short glyph.</para>
    ///
    /// <para><b>Refuses by returning <see langword="null"/>.</b> The caller's contract, all the way
    /// up to <c>TrueTypeFontProgram.Parse</c>, is that a font that will not unwrap is
    /// indistinguishable from one that will not parse.</para>
    /// </remarks>
    internal static class Woff2GlyfTransform
    {
        /// <summary>Flag bit 5 of a composite component: another component follows.</summary>
        private const int MoreComponents = 0x0020;

        /// <summary>Flag bit 0: the component's arguments are 16-bit rather than 8-bit.</summary>
        private const int Arg1And2AreWords = 0x0001;

        /// <summary>Flag bit 3: one F2Dot14 scale follows.</summary>
        private const int WeHaveAScale = 0x0008;

        /// <summary>Flag bit 6: separate x and y F2Dot14 scales follow.</summary>
        private const int WeHaveAnXAndYScale = 0x0040;

        /// <summary>Flag bit 7: a 2×2 F2Dot14 matrix follows.</summary>
        private const int WeHaveATwoByTwo = 0x0080;

        /// <summary>Flag bit 8: the composite carries its own instructions.</summary>
        private const int WeHaveInstructions = 0x0100;

        /// <summary>TrueType simple-glyph flag: this point is on the curve.</summary>
        private const int OnCurve = 0x01;

        /// <summary>TrueType simple-glyph flag: the x delta is one byte.</summary>
        private const int XShortVector = 0x02;

        /// <summary>TrueType simple-glyph flag: the y delta is one byte.</summary>
        private const int YShortVector = 0x04;

        /// <summary>
        /// TrueType simple-glyph flag: with <see cref="XShortVector"/> the byte is positive;
        /// without it, x is unchanged from the previous point.
        /// </summary>
        private const int XSameOrPositive = 0x10;

        /// <summary>The y counterpart of <see cref="XSameOrPositive"/>.</summary>
        private const int YSameOrPositive = 0x20;

        /// <summary>TrueType simple-glyph flag bit 6: contours may overlap.</summary>
        private const int OverlapSimple = 0x40;

        /// <summary>The fixed part of the transformed table, before the substreams.</summary>
        private const int HeaderSize = 36;

        /// <summary>
        /// The rebuilt <c>glyf</c> and <c>loca</c>, or <c>(null, null)</c> when the transformed
        /// table is malformed.
        /// </summary>
        /// <param name="transformed">The transformed <c>glyf</c> table as it left Brotli.</param>
        /// <param name="maxGlyfBytes">
        /// Ceiling on the reconstructed <c>glyf</c>. The reconstruction is driven by counts the file
        /// chose, so without a ceiling a small container can ask for an arbitrarily large one.
        /// </param>
        internal static (byte[] Glyf, byte[] Loca) Rebuild(byte[] transformed, long maxGlyfBytes)
        {
            try
            {
                return RebuildCore(transformed, maxGlyfBytes);
            }
            catch (Exception)
            {
                // Includes the IndexOutOfRange an inconsistent substream length could still reach
                // past the explicit checks. The contract is a refusal, not an escape.
                return (null, null);
            }
        }

        private static (byte[] Glyf, byte[] Loca) RebuildCore(byte[] transformed, long maxGlyfBytes)
        {
            var head = new Woff2Reader(transformed);

            if (!head.TryReadU16(out int reserved) ||
                !head.TryReadU16(out int optionFlags) ||
                !head.TryReadU16(out int numGlyphs) ||
                !head.TryReadU16(out int indexFormat)) return (null, null);

            // §5.1: reserved is 0x0000, and the option flags carry one defined bit.
            if (reserved != 0 || (optionFlags & ~0x0001) != 0) return (null, null);

            var sizes = new uint[7];
            for (int i = 0; i < 7; i++)
                if (!head.TryReadU32(out sizes[i])) return (null, null);

            // The substreams follow the header in the order the size fields are declared in.
            var streams = new Woff2Reader[7];
            long offset = HeaderSize;
            for (int i = 0; i < 7; i++)
            {
                if (sizes[i] > int.MaxValue) return (null, null);
                if (offset + sizes[i] > transformed.Length) return (null, null);
                streams[i] = new Woff2Reader(transformed, (int)offset);
                offset += sizes[i];
            }

            Woff2Reader nContours = streams[0];
            Woff2Reader nPoints = streams[1];
            Woff2Reader flags = streams[2];
            Woff2Reader glyphs = streams[3];
            Woff2Reader composites = streams[4];
            Woff2Reader bboxes = streams[5];
            Woff2Reader instructions = streams[6];

            // §5.1: the bbox bitmap is 4 * floor((numGlyphs + 31) / 32) bytes and precedes the bbox
            // values inside the SAME substream, so the value reader starts after it.
            int bitmapBytes = 4 * ((numGlyphs + 31) / 32);
            if (bitmapBytes > sizes[5]) return (null, null);
            if (!bboxes.TryReadBytes(bitmapBytes, out byte[] bboxBitmap)) return (null, null);

            // The overlap bitmap, when present, is the last thing in the table — after every
            // substream. Its absence means every OVERLAP_SIMPLE flag is zero.
            byte[] overlapBitmap = null;
            if ((optionFlags & 0x0001) != 0)
            {
                int overlapBytes = (numGlyphs + 7) / 8;
                if (offset + overlapBytes > transformed.Length) return (null, null);
                overlapBitmap = new byte[overlapBytes];
                Buffer.BlockCopy(transformed, (int)offset, overlapBitmap, 0, overlapBytes);
            }

            var glyf = new MemoryStream();
            var offsets = new List<long>(numGlyphs + 1) { 0 };

            for (int g = 0; g < numGlyphs; g++)
            {
                if (!nContours.TryReadS16(out int contourCount)) return (null, null);

                bool hasExplicitBbox = BitSet(bboxBitmap, g);
                byte[] glyph;

                if (contourCount == 0)
                {
                    // §5.1: an empty glyph occupies no bytes, and an explicit bbox for one is
                    // invalid rather than merely redundant.
                    if (hasExplicitBbox) return (null, null);
                    glyph = Array.Empty<byte>();
                }
                else if (contourCount == -1)
                {
                    // §5.1: a composite MUST carry an explicit bounding box, because computing one
                    // would mean resolving its components.
                    if (!hasExplicitBbox) return (null, null);
                    glyph = BuildComposite(composites, glyphs, instructions, bboxes);
                }
                else
                {
                    glyph = BuildSimple(contourCount, nPoints, flags, glyphs, instructions,
                                        bboxes, hasExplicitBbox,
                                        overlapBitmap != null && BitSet(overlapBitmap, g));
                }

                if (glyph == null) return (null, null);

                glyf.Write(glyph, 0, glyph.Length);

                // §5.3 requires the reconstructed offsets, and the sfnt requires them padded to a
                // two-byte boundary in the short format — which is also where a glyph's own length
                // has to be even. Padding here keeps the two consistent.
                while (glyf.Length % 4 != 0) glyf.WriteByte(0);

                if (glyf.Length > maxGlyfBytes) return (null, null);
                offsets.Add(glyf.Length);
            }

            byte[] loca = BuildLoca(offsets, indexFormat);
            return loca == null ? (null, null) : (glyf.ToArray(), loca);
        }

        // ------------------------------------------------------------------

        /// <summary>A simple glyph, rebuilt into the §5.3.3 TrueType layout.</summary>
        private static byte[] BuildSimple(
            int contourCount, Woff2Reader nPoints, Woff2Reader flagStream, Woff2Reader glyphStream,
            Woff2Reader instructionStream, Woff2Reader bboxStream, bool hasExplicitBbox,
            bool overlaps)
        {
            // Point counts per contour → endPtsOfContours, which is the cumulative sum minus one.
            var endPoints = new int[contourCount];
            int total = 0;
            for (int c = 0; c < contourCount; c++)
            {
                if (!nPoints.TryRead255UInt16(out int count)) return null;
                total += count;
                if (total > 0xFFFF) return null;         // endPtsOfContours entries are UInt16
                endPoints[c] = total - 1;
            }

            if (total == 0) return null;                 // a contour with no points is not a contour

            var xs = new int[total];
            var ys = new int[total];
            var onCurve = new bool[total];

            int x = 0, y = 0;
            for (int p = 0; p < total; p++)
            {
                if (!flagStream.TryReadU8(out int flag)) return null;

                onCurve[p] = (flag & 0x80) == 0;         // §5.2: MSB clear means on-curve
                if (!ReadTriplet(glyphStream, flag, out int dx, out int dy)) return null;

                x += dx;
                y += dy;
                xs[p] = x;
                ys[p] = y;
            }

            if (!glyphStream.TryRead255UInt16(out int instructionLength)) return null;
            if (!instructionStream.TryReadBytes(instructionLength, out byte[] instrs)) return null;

            var body = new MemoryStream();
            WriteS16(body, contourCount);

            if (!WriteBoundingBox(body, bboxStream, hasExplicitBbox, xs, ys)) return null;

            foreach (int end in endPoints) WriteU16(body, end);

            WriteU16(body, instructionLength);
            body.Write(instrs, 0, instrs.Length);

            WriteSimpleOutline(body, xs, ys, onCurve, overlaps);
            return body.ToArray();
        }

        /// <summary>
        /// Re-encodes the absolute points into TrueType's flag / short / same-value form.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately not run-length compressed.</b> TrueType allows a repeat flag
        /// (<c>0x08</c>) collapsing identical consecutive flag bytes, and every real font uses it —
        /// but the output here is consumed by this library's own parser, not shipped, and the
        /// §5.1 note is explicit that a decoder must not be rejected for producing a <c>glyf</c>
        /// whose length differs from <c>origLength</c>. Emitting one flag per point is larger and
        /// exactly correct; adding the repeat encoding is an optimisation with a new way to be
        /// wrong.
        /// </remarks>
        private static void WriteSimpleOutline(MemoryStream body, int[] xs, int[] ys,
                                               bool[] onCurve, bool overlaps)
        {
            int count = xs.Length;
            var flags = new byte[count];
            var xBytes = new MemoryStream();
            var yBytes = new MemoryStream();

            int prevX = 0, prevY = 0;
            for (int i = 0; i < count; i++)
            {
                int flag = onCurve[i] ? OnCurve : 0;

                int dx = xs[i] - prevX;
                if (dx == 0)
                {
                    flag |= XSameOrPositive;             // "same as previous", no bytes emitted
                }
                else if (dx >= -255 && dx <= 255)
                {
                    flag |= XShortVector;
                    if (dx > 0) flag |= XSameOrPositive; // with SHORT set, this bit is the sign
                    xBytes.WriteByte((byte)Math.Abs(dx));
                }
                else
                {
                    WriteS16(xBytes, dx);
                }

                int dy = ys[i] - prevY;
                if (dy == 0)
                {
                    flag |= YSameOrPositive;
                }
                else if (dy >= -255 && dy <= 255)
                {
                    flag |= YShortVector;
                    if (dy > 0) flag |= YSameOrPositive;
                    yBytes.WriteByte((byte)Math.Abs(dy));
                }
                else
                {
                    WriteS16(yBytes, dy);
                }

                // §5.1: OVERLAP_SIMPLE is carried on the FIRST flag byte of the glyph only.
                if (i == 0 && overlaps) flag |= OverlapSimple;

                flags[i] = (byte)flag;
                prevX = xs[i];
                prevY = ys[i];
            }

            body.Write(flags, 0, flags.Length);
            byte[] xb = xBytes.ToArray();
            byte[] yb = yBytes.ToArray();
            body.Write(xb, 0, xb.Length);
            body.Write(yb, 0, yb.Length);
        }

        /// <summary>A composite glyph: its components come through verbatim.</summary>
        /// <remarks>
        /// The component records are already in TrueType form in the composite substream — the
        /// transform groups them, it does not re-encode them. What has to be worked out here is how
        /// many bytes each component occupies, which is a function of its flag word, and where the
        /// instructions live (the glyph and instruction streams, not the composite one).
        /// </remarks>
        private static byte[] BuildComposite(Woff2Reader composites, Woff2Reader glyphStream,
                                             Woff2Reader instructionStream, Woff2Reader bboxStream)
        {
            var components = new MemoryStream();
            bool wantInstructions = false;
            int start = composites.Position;

            while (true)
            {
                if (!composites.TryReadU16(out int flag)) return null;

                if ((flag & WeHaveInstructions) != 0) wantInstructions = true;

                int argBytes = (flag & Arg1And2AreWords) != 0 ? 4 : 2;
                int transformBytes =
                    (flag & WeHaveATwoByTwo) != 0 ? 8 :
                    (flag & WeHaveAnXAndYScale) != 0 ? 4 :
                    (flag & WeHaveAScale) != 0 ? 2 : 0;

                // The glyph index is a UInt16 that follows the flag word.
                if (!composites.TrySkip(2 + argBytes + transformBytes)) return null;

                if ((flag & MoreComponents) == 0) break;
            }

            int length = composites.Position - start;
            composites.Position = start;
            if (!composites.TryReadBytes(length, out byte[] componentBytes)) return null;
            components.Write(componentBytes, 0, componentBytes.Length);

            byte[] instrs = Array.Empty<byte>();
            int instructionLength = 0;
            if (wantInstructions)
            {
                if (!glyphStream.TryRead255UInt16(out instructionLength)) return null;
                if (!instructionStream.TryReadBytes(instructionLength, out instrs)) return null;
            }

            var body = new MemoryStream();
            WriteS16(body, -1);
            if (!WriteBoundingBox(body, bboxStream, true, null, null)) return null;

            byte[] comp = components.ToArray();
            body.Write(comp, 0, comp.Length);

            if (wantInstructions)
            {
                WriteU16(body, instructionLength);
                body.Write(instrs, 0, instrs.Length);
            }

            return body.ToArray();
        }

        /// <summary>
        /// Writes the glyph's four bounding-box values, read from the stream or computed.
        /// </summary>
        /// <remarks>
        /// §5.1: an unset bitmap bit means the box is the extent of <b>all</b> outline points, the
        /// off-curve ones included. Using only the on-curve points gives a box that is right for most
        /// glyphs and slightly small for curved ones — the kind of near-miss that shows as clipped
        /// hinting rather than as a failure.
        /// </remarks>
        private static bool WriteBoundingBox(MemoryStream body, Woff2Reader bboxStream,
                                             bool explicitBox, int[] xs, int[] ys)
        {
            if (explicitBox)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!bboxStream.TryReadS16(out int v)) return false;
                    WriteS16(body, v);
                }
                return true;
            }

            if (xs == null || ys == null || xs.Length == 0) return false;

            int xMin = xs[0], xMax = xs[0], yMin = ys[0], yMax = ys[0];
            for (int i = 1; i < xs.Length; i++)
            {
                if (xs[i] < xMin) xMin = xs[i];
                if (xs[i] > xMax) xMax = xs[i];
                if (ys[i] < yMin) yMin = ys[i];
                if (ys[i] > yMax) yMax = ys[i];
            }

            WriteS16(body, xMin);
            WriteS16(body, yMin);
            WriteS16(body, xMax);
            WriteS16(body, yMax);
            return true;
        }

        /// <summary>
        /// One point's (dx, dy) from the §5.2 triplet encoding.
        /// </summary>
        /// <remarks>
        /// <b>The nibble case is where this goes wrong quietly.</b> When a coordinate uses 4 or 12
        /// bits the two coordinates share bytes, X first and most-significant-bit first. Reading
        /// them in the other order produces plausible small numbers rather than an error, so the
        /// glyph comes out subtly deformed with nothing to report.
        /// </remarks>
        private static bool ReadTriplet(Woff2Reader glyphStream, int flag, out int dx, out int dy)
        {
            dx = dy = 0;
            Woff2Tables.TripletRow row = Woff2Tables.TripletFor(flag);

            int dataBytes = row.ByteCount - 1;           // ByteCount includes the flag byte
            if (!glyphStream.TryReadBytes(dataBytes, out byte[] data)) return false;

            int xValue, yValue;

            if (row.XBits == 0)
            {
                xValue = 0;
                yValue = Big(data, 0, row.YBits);
            }
            else if (row.YBits == 0)
            {
                xValue = Big(data, 0, row.XBits);
                yValue = 0;
            }
            else if (row.XBits == 4)
            {
                // One byte, X in the high nibble.
                xValue = data[0] >> 4;
                yValue = data[0] & 0x0F;
            }
            else if (row.XBits == 12)
            {
                // Three bytes: X is the first twelve bits, Y the last twelve.
                xValue = (data[0] << 4) | (data[1] >> 4);
                yValue = ((data[1] & 0x0F) << 8) | data[2];
            }
            else
            {
                int xBytes = row.XBits / 8;
                xValue = Big(data, 0, row.XBits);
                yValue = Big(data, xBytes, row.YBits);
            }

            dx = row.XSign * (xValue + row.DeltaX);
            dy = row.YSign * (yValue + row.DeltaY);
            return true;
        }

        /// <summary>A big-endian value of 8 or 16 bits at a byte offset.</summary>
        private static int Big(byte[] data, int offset, int bits)
            => bits == 8 ? data[offset] : (data[offset] << 8) | data[offset + 1];

        /// <summary>The §5.3 <c>loca</c>, in the format <c>indexFormat</c> declares.</summary>
        /// <remarks>
        /// The short format stores <b>half</b> the offset, so every offset has to be even — which is
        /// what the four-byte padding after each glyph guarantees. An odd offset here would be
        /// silently halved and every later glyph would start in the middle of the previous one.
        /// </remarks>
        private static byte[] BuildLoca(List<long> offsets, int indexFormat)
        {
            var ms = new MemoryStream();

            foreach (long offset in offsets)
            {
                if (indexFormat == 0)
                {
                    if (offset % 2 != 0 || offset / 2 > 0xFFFF) return null;
                    WriteU16(ms, (int)(offset / 2));
                }
                else
                {
                    if (offset > uint.MaxValue) return null;
                    WriteU32(ms, (uint)offset);
                }
            }

            return ms.ToArray();
        }

        private static bool BitSet(byte[] bitmap, int index)
        {
            int b = index >> 3;
            return b < bitmap.Length && (bitmap[b] & (0x80 >> (index & 7))) != 0;
        }

        private static void WriteU16(Stream s, int value) => IO.BigEndian.Write16(s, value);

        private static void WriteS16(Stream s, int value) => WriteU16(s, value & 0xFFFF);

        private static void WriteU32(Stream s, uint value) => IO.BigEndian.Write32(s, value);
    }
}
