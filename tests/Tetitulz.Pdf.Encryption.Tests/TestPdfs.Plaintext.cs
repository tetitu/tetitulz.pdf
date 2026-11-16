// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Tetitulz.Pdf.Encryption.Tests;

internal static partial class TestPdfs
{
    /// <summary>
    /// Unencrypted one-page documents for the writers that take plaintext — <c>PdfEncryptor</c> and
    /// <c>PdfPublicKeyEncryptor</c> — each carrying <see cref="Marker"/> in its content stream.
    /// </summary>
    public static class Plaintext
    {
        public const string Marker = "plaintext-fixture-marker";

        /// <summary>The string inside the indirect <c>/DecodeParms</c> dictionary of <see cref="Build"/>'s image.</summary>
        public const string DecodeParmsMarker = "marker-inside-the-decode-parms-dictionary";

        /// <summary>The data of the <c>/JBIG2Globals</c> stream that dictionary names.</summary>
        public const string GlobalsData = "jbig2-globals-stream-data";

        /// <summary>
        /// A classic-table document. <paramref name="contentGeneration"/> is written both in object 4's header and in its
        /// cross-reference entry, and the page refers to it as <c>4 G R</c> — the shape of a reused object number
        /// (ISO 32000-2 §7.5.4). With <paramref name="indirectDecodeParms"/> the page also draws image 6, whose
        /// <c>/DecodeParms</c> is the indirect dictionary 7, which in turn names stream 8 as <c>/JBIG2Globals</c> — every
        /// value <c>PdfParser</c> resolves into place while reading.
        /// </summary>
        public static byte[] Build(string extraTrailerEntries = "", int contentGeneration = 0, bool indirectDecodeParms = false)
        {
            string content = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET";
            var sb = new StringBuilder();
            var entries = new List<(int Offset, int Generation)>();
            void Obj(int generation, string body) { entries.Add((sb.Length, generation)); sb.Append(body); }

            string resources = indirectDecodeParms
                ? " /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 6 0 R >> >>"
                : " /Resources << /Font << /F1 5 0 R >> >>";

            sb.Append("%PDF-1.7\n");
            Obj(0, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Obj(0, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            Obj(0, $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 {contentGeneration} R{resources} >>\nendobj\n");
            Obj(contentGeneration, $"4 {contentGeneration} obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            Obj(0, "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
            if (indirectDecodeParms)
            {
                // No /Filter, so the parameters are never applied — only resolved, which is what is under test.
                Obj(0, "6 0 obj\n<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
                       "/BitsPerComponent 8 /DecodeParms 7 0 R /Length 1 >>\nstream\n\0\nendstream\nendobj\n");
                Obj(0, $"7 0 obj\n<< /JBIG2Globals 8 0 R /T ({DecodeParmsMarker}) >>\nendobj\n");
                Obj(0, $"8 0 obj\n<< /Length {GlobalsData.Length} >>\nstream\n{GlobalsData}\nendstream\nendobj\n");
            }

            int xref = sb.Length;
            sb.Append("xref\n0 ").Append(entries.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (var (offset, generation) in entries) sb.AppendFormat("{0:D10} {1:D5} n \n", offset, generation);
            sb.Append("trailer\n<< /Size ").Append(entries.Count + 1).Append(" /Root 1 0 R");
            if (extraTrailerEntries.Length > 0) sb.Append(' ').Append(extraTrailerEntries);
            sb.Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            return Encoding.Latin1.GetBytes(sb.ToString());
        }

        /// <summary>
        /// The same one-page document closed by a Flate-compressed <b>cross-reference stream</b> (object 5), so the
        /// trailer dictionary a reader sees is the stream's own — <c>/Type /XRef</c>, <c>/W</c>, <c>/Index</c>,
        /// <c>/Filter</c>, <c>/DecodeParms</c>, <c>/Length</c> and all.
        /// </summary>
        public static byte[] WithCrossReferenceStream()
        {
            string content = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET";
            var bytes = new List<byte>();
            var offsets = new List<int>();
            void Ascii(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
            void Obj(string body) { offsets.Add(bytes.Count); Ascii(body); }

            Ascii("%PDF-1.5\n");
            Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
            Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

            int xrefOffset = bytes.Count;
            var rows = new List<byte>();
            void Row(byte type, int field2, int field3)
            {
                rows.Add(type);
                for (int shift = 24; shift >= 0; shift -= 8) rows.Add((byte)(field2 >> shift));
                rows.Add((byte)(field3 >> 8));
                rows.Add((byte)field3);
            }
            Row(0, 0, 0xFFFF);
            foreach (int offset in offsets) Row(1, offset, 0);
            Row(1, xrefOffset, 0);

            byte[] deflated;
            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                    zlib.Write(rows.ToArray(), 0, rows.Count);
                deflated = compressed.ToArray();
            }

            Ascii($"5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 2] /Index [0 6] /Root 1 0 R /Filter /FlateDecode " +
                  $"/DecodeParms << /Columns 7 >> /Length {deflated.Length} >>\nstream\n");
            bytes.AddRange(deflated);
            Ascii($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
            return bytes.ToArray();
        }
    }
}
