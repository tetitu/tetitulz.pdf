// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Lossless transcoding between JPEG 2000's two block coders: the Part 1 coder (ITU-T T.800) and the
    /// high-throughput one (HTJ2K, ITU-T T.814). Every code-block is re-coded so it decodes to the same
    /// values; tiles, components, precincts, quality layers and progression stay as they were, the layers'
    /// boundaries kept as T.814's placeholder passes. A raw codestream comes back raw; a JP2 file becomes a
    /// JPH file and a JPH a JP2 (T.814 Annex D), the other boxes kept.
    /// </summary>
    /// <remarks>
    /// The use in a PDF is the way back: ISO 32000-2 §7.4.9 limits <c>JPXDecode</c> to the JPX baseline,
    /// which HT code-blocks are not, and few viewers decode them, so an HTJ2K image transcoded to Part 1 is
    /// one every reader shows, at no cost in quality.
    /// </remarks>
    public static class JpxTranscoding
    {
        /// <summary>
        /// Re-codes every code-block of a Part 1 codestream as an HT code-block. False, with
        /// <paramref name="reason"/>, for input that is not a whole JPEG 2000 codestream, JP2 or JPH, a
        /// codestream that is already HTJ2K, or one whose blocks HT cannot carry.
        /// </summary>
        public static bool TryToHighThroughput(byte[] jpeg2000, out byte[] result, out string reason) =>
            TryToHighThroughput(jpeg2000, keepQualityLayers: true, out result, out reason);

        /// <summary>
        /// As <see cref="TryToHighThroughput(byte[], out byte[], out string)"/>. With <paramref name="keepQualityLayers"/>
        /// a code-block whose passes came in more than one layer before its last cleanup keeps those boundaries
        /// as T.814's placeholder passes, so the way back to Part 1 restores the layers; without it every block's
        /// passes arrive with its last cleanup — the form decoders without placeholder passes read (common decoders
        /// refuses an HT block of more than three passes). The picture is the same either way.
        /// </summary>
        public static bool TryToHighThroughput(byte[] jpeg2000, bool keepQualityLayers, out byte[] result, out string reason) =>
            TryTranscode(jpeg2000, toHighThroughput: true, keepQualityLayers, out result, out reason);

        /// <summary>
        /// Re-codes every HT code-block of an HTJ2K codestream by the Part 1 block coder — the form
        /// ISO 32000's <c>JPXDecode</c> asks for. False, with <paramref name="reason"/>, for input that is
        /// not a whole JPEG 2000 codestream, JP2 or JPH, or holds no HT code-block.
        /// </summary>
        public static bool TryToPart1(byte[] jpeg2000, out byte[] result, out string reason) =>
            TryTranscode(jpeg2000, toHighThroughput: false, keepQualityLayers: true, out result, out reason);

        /// <summary>
        /// As <see cref="TryToPart1(byte[], out byte[], out string)"/>, every image's work also spending
        /// <paramref name="documentWork"/> — a document's, so a document of many images is bounded as a
        /// whole, not only image by image.
        /// </summary>
        internal static bool TryToPart1(byte[] jpeg2000, Util.WorkBudget documentWork, out byte[] result, out string reason) =>
            TryTranscode(jpeg2000, toHighThroughput: false, keepQualityLayers: true, out result, out reason, documentWork);

        private static bool TryTranscode(byte[] data, bool toHighThroughput, bool keepQualityLayers, out byte[] result, out string reason,
            Util.WorkBudget documentWork = null)
        {
            result = null;
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (JpxHeader.IsRawCodestream(data))
                return JpxTranscoder.TryTranscode(data, toHighThroughput, null, out result, out reason, keepQualityLayers, documentWork);
            return TryTranscodeFile(data, toHighThroughput, keepQualityLayers, out result, out reason, documentWork);
        }

        private const uint Jp2Brand = 0x6A703220, JphBrand = 0x6A706820;   // 'jp2 ', 'jph '
        private const uint Ftyp = 0x66747970, Jp2c = 0x6A703263, Rreq = 0x72726571, Ftbl = 0x6674626C;

        /// <summary>The most top-level boxes a file is walked for.</summary>
        private const int MaxBoxes = 4096;

        /// <summary>
        /// A JP2 or JPH file: its one codestream box transcoded, its File Type box rebranded — <c>jp2 </c> and
        /// <c>jph </c> swapped as brand and among the compatible brands (T.814 Annex D.3: a JPH is not
        /// <c>jp2 </c>-compatible) — every other box kept byte for byte. A JPX file (Reader Requirements, a
        /// fragment table, brand <c>jpx </c>) is refused, as is CMYK on the way to JPH, which enumerates none.
        /// </summary>
        private static bool TryTranscodeFile(byte[] data, bool toHt, bool keepQualityLayers, out byte[] result, out string reason,
            Util.WorkBudget documentWork)
        {
            result = null;
            if (!JpxHeader.IsJp2Container(data))
            {
                reason = "neither a JPEG 2000 codestream nor a JP2 or JPH file";
                return false;
            }
            var boxes = new List<(uint Type, long Start, long ContentStart, long End)>();
            for (long pos = 0; pos < data.Length;)
            {
                if (boxes.Count >= MaxBoxes || !JpxHeader.TryReadBoxHeader(data, pos, out uint type, out long contentStart, out long end))
                {
                    reason = $"the box at offset {pos} is malformed or past {MaxBoxes} boxes";
                    return false;
                }
                boxes.Add((type, pos, contentStart, end));
                pos = end;
            }

            uint from = toHt ? Jp2Brand : JphBrand, to = toHt ? JphBrand : Jp2Brand;
            int codestreams = 0;
            (uint Type, long Start, long ContentStart, long End) codestream = default;
            foreach (var box in boxes)
            {
                if (box.Type == Rreq || box.Type == Ftbl)
                {
                    reason = "a JPX file (Reader Requirements or a fragment table): only JP2 and JPH files are transcoded";
                    return false;
                }
                if (box.Type == Ftyp && (box.End - box.ContentStart < 8 || (uint)JpxHeader.ReadU32(data, box.ContentStart) != from))
                {
                    reason = $"the file's brand is not {(toHt ? "'jp2 '" : "'jph '")}, the one a {(toHt ? "Part 1" : "HTJ2K")} codestream's file has";
                    return false;
                }
                if (box.Type == Jp2c)
                {
                    codestreams++;
                    codestream = box;
                }
            }
            if (codestreams != 1)
            {
                reason = $"the file holds {codestreams} contiguous codestream boxes; one is transcoded";
                return false;
            }
            if (toHt && JpxHeader.TryReadColourSpecifications(data, out JpxColourSpecificationSummary[] colours, out _))
                foreach (JpxColourSpecificationSummary colour in colours)
                    if (colour.EnumeratedSpace == 12)
                    {
                        reason = "the file's colour is CMYK, which a JPH file does not enumerate (ITU-T T.814 Annex D.4)";
                        return false;
                    }

            var source = new byte[codestream.End - codestream.ContentStart];
            Array.Copy(data, codestream.ContentStart, source, 0, source.Length);
            if (!JpxTranscoder.TryTranscode(source, toHt, null, out byte[] transcoded, out reason, keepQualityLayers, documentWork)) return false;

            var output = new List<byte>(data.Length + transcoded.Length - source.Length);
            foreach (var box in boxes)
            {
                if (box.Type == Ftyp) output.AddRange(Rebranded(data, box.Start, box.ContentStart, box.End, from, to));
                else if (box.Type == Jp2c) output.AddRange(Box(Jp2c, transcoded));
                else for (long b = box.Start; b < box.End; b++) output.Add(data[b]);
            }
            result = output.ToArray();
            return true;
        }

        /// <summary>The File Type box with <paramref name="from"/> made <paramref name="to"/> as brand and as each compatible brand, <paramref name="to"/> added if none was.</summary>
        private static byte[] Rebranded(byte[] data, long start, long contentStart, long end, uint from, uint to)
        {
            var content = new List<byte>();
            AddU32(content, to);
            AddU32(content, 0);   // MinV
            bool listed = false;
            for (long at = contentStart + 8; at + 4 <= end; at += 4)
            {
                uint brand = (uint)JpxHeader.ReadU32(data, at);
                if (brand == from) brand = to;
                if (brand == to)
                {
                    if (listed) continue;
                    listed = true;
                }
                AddU32(content, brand);
            }
            if (!listed) AddU32(content, to);
            return Box(Ftyp, content.ToArray());
        }

        private static byte[] Box(uint type, IReadOnlyList<byte> content)
        {
            var box = new List<byte>(content.Count + 16);
            long length = content.Count + 8L;
            if (length <= uint.MaxValue)
            {
                AddU32(box, (uint)length);
                AddU32(box, type);
            }
            else
            {
                AddU32(box, 1);
                AddU32(box, type);
                AddU32(box, (uint)((length + 8) >> 32));
                AddU32(box, (uint)(length + 8));
            }
            box.AddRange(content);
            return box.ToArray();
        }

        private static void AddU32(List<byte> to, uint v) => IO.BigEndian.Add32(to, v);
    }
}
