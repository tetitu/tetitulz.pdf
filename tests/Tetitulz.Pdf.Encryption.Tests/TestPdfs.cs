// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// In-memory PDF fixtures for the Encryption suite.
///
/// <para><b><see cref="LegacyRc4"/> computes its own /O, /U and object keys.</b> The suite's other
/// fixtures come from <c>PdfEncryptor</c>, which means they can only ever confirm that this
/// library agrees with itself — the shape CLAUDE.md records as having certified a wrong key
/// derivation and an owner password that protected nothing. The implementation below is a
/// deliberate second transcription of ISO 32000-1 §7.6.2/§7.6.3 (Algorithms 1, 2, 3 and 4) with
/// its own RC4, so a document it produces is an independent vector: the decryptor either derives
/// the same key from the spec, or it does not. It is also the only way to hand the decryptor a
/// trailer this library would never write.</para>
/// </summary>
internal static partial class TestPdfs
{
    /// <summary>The marker the fixture paints, so a decrypted output can be checked for content.</summary>
    public const string ContentMarker = "confidential";

    public static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    /// <summary>
    /// Builds RC4-40 (/V 1 /R 2) encrypted PDFs with an independently computed key and a
    /// caller-chosen trailer.
    /// </summary>
    public static partial class LegacyRc4
    {
        /// <summary>ISO 32000-1 Table 20's 32-byte padding string, transcribed here independently.</summary>
        private static readonly byte[] PadString =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
            0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
            0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
        };

        /// <summary>40-bit key: five bytes of the MD5 digest (§7.6.3.3 Algorithm 2 step (f)).</summary>
        private const int KeyBytes = 5;

        private const int PValue = -3904;   // print and copy denied; the reserved bits set

        /// <summary>
        /// Writes an encrypted document whose user password is empty and whose owner password is
        /// "owner-secret".
        /// </summary>
        /// <param name="fileId">The /ID to write and to hash into the key.</param>
        /// <param name="extraTrailerEntries">Raw trailer syntax appended verbatim, e.g. "/XRefStm 123".</param>
        public static byte[] Build(byte[] fileId, string extraTrailerEntries = "")
        {
            byte[] o = OwnerValue(ownerPassword: "owner-secret", userPassword: "");
            byte[] key = FileKey(userPassword: "", oValue: o, pValue: PValue, fileId: fileId);
            byte[] u = UserValue(key);

            string content = $"BT /F1 12 Tf 72 700 Td ({ContentMarker}) Tj ET\n";
            byte[] encryptedContent = Rc4(ObjectKey(key, 4, 0), Encoding.ASCII.GetBytes(content));

            var bytes = new List<byte>();
            var offsets = new List<int>();
            void Ascii(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
            void Obj(string body) { offsets.Add(bytes.Count); Ascii(body); }

            Ascii("%PDF-1.4\n");
            Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");

            offsets.Add(bytes.Count);
            Ascii($"4 0 obj\n<< /Length {encryptedContent.Length} >>\nstream\n");
            bytes.AddRange(encryptedContent);
            Ascii("\nendstream\nendobj\n");

            Obj($"5 0 obj\n<< /Filter /Standard /V 1 /R 2 /O <{Hex(o)}> /U <{Hex(u)}> /P {PValue} >>\nendobj\n");

            int xref = bytes.Count;
            var trailer = new StringBuilder();
            trailer.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
            foreach (int off in offsets) trailer.AppendFormat("{0:D10} 00000 n \n", off);
            trailer.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R /Encrypt 5 0 R", offsets.Count + 1);
            trailer.AppendFormat(" /ID [<{0}> <{0}>]", Hex(fileId));
            if (extraTrailerEntries.Length > 0)
                trailer.Append(' ').Append(extraTrailerEntries);
            trailer.AppendFormat(" >>\nstartxref\n{0}\n%%EOF\n", xref);
            Ascii(trailer.ToString());

            return bytes.ToArray();
        }

        /// <summary>
        /// The same RC4-40 document, but written with a <b>cross-reference stream</b> so it can
        /// carry a type-2 (compressed) entry, and with an object stream whose <c>/Filter</c> chain
        /// is longer than <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxFilterChainLength"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Every part of that shape is load-bearing.</b> The decryptor expands an object
        /// stream only when some xref entry claims to live inside it, and only a cross-reference
        /// <i>stream</i> can express such an entry — a classic <c>xref</c> table has no type-2 form.
        /// The over-long filter chain is the one route that makes the expansion fail without
        /// needing the stream's decrypted bytes to be anything in particular:
        /// <c>FilterPipeline</c> refuses on the chain length <b>before decoding a byte</b>, so the
        /// contents can be arbitrary. The obvious alternative — a truncated <c>/ObjStm</c> index —
        /// does not get there, because <c>PdfTokenizer.ReadInteger</c> returns 0 rather than
        /// throwing.</para>
        /// <para>The object stream is object 6 and the compressed entry claims object 7 lives in
        /// it, so object 7 is what the failed expansion drops.</para>
        /// </remarks>
        public static byte[] BuildWithUnexpandableObjectStream(byte[] fileId)
        {
            byte[] o = OwnerValue(ownerPassword: "owner-secret", userPassword: "");
            byte[] key = FileKey(userPassword: "", oValue: o, pValue: PValue, fileId: fileId);
            byte[] u = UserValue(key);

            string content = $"BT /F1 12 Tf 72 700 Td ({ContentMarker}) Tj ET\n";
            byte[] encryptedContent = Rc4(ObjectKey(key, 4, 0), Encoding.ASCII.GetBytes(content));

            // 17 filters — one past the 16 the pipeline allows. Never decoded, so their identity
            // does not matter; the refusal happens on the count.
            var chain = new StringBuilder("[");
            for (int i = 0; i < 17; i++) chain.Append(" /FlateDecode");
            chain.Append(" ]");

            // Would-be ObjStm payload. Encrypted like every other stream in the document, so the
            // decryptor's own path is exercised right up to the point the filter chain is refused.
            byte[] objStmData = Rc4(ObjectKey(key, 6, 0), Encoding.ASCII.GetBytes("7 0 <</X 1>>"));

            var bytes = new List<byte>();
            var offsets = new List<int>();
            void Ascii(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
            void Obj(string body) { offsets.Add(bytes.Count); Ascii(body); }

            Ascii("%PDF-1.5\n");
            Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");

            offsets.Add(bytes.Count);
            Ascii($"4 0 obj\n<< /Length {encryptedContent.Length} >>\nstream\n");
            bytes.AddRange(encryptedContent);
            Ascii("\nendstream\nendobj\n");

            Obj($"5 0 obj\n<< /Filter /Standard /V 1 /R 2 /O <{Hex(o)}> /U <{Hex(u)}> /P {PValue} >>\nendobj\n");

            offsets.Add(bytes.Count);
            Ascii($"6 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter {chain} " +
                  $"/Length {objStmData.Length} >>\nstream\n");
            bytes.AddRange(objStmData);
            Ascii("\nendstream\nendobj\n");

            // Object 7 has no body of its own: the xref entry below says it lives inside object 6.

            int xrefOffset = bytes.Count;
            var entries = new List<byte>();
            void Entry(byte type, int field2, int field3)
            {
                entries.Add(type);
                entries.Add((byte)((field2 >> 24) & 0xFF));
                entries.Add((byte)((field2 >> 16) & 0xFF));
                entries.Add((byte)((field2 >> 8) & 0xFF));
                entries.Add((byte)(field2 & 0xFF));
                entries.Add((byte)((field3 >> 8) & 0xFF));
                entries.Add((byte)(field3 & 0xFF));
            }

            Entry(0, 0, 0xFFFF);                       // object 0 — the head of the free list
            foreach (int off in offsets) Entry(1, off, 0);   // objects 1-6, uncompressed
            Entry(2, 6, 0);                            // object 7 — inside object stream 6, index 0
            Entry(1, xrefOffset, 0);                   // object 8 — this cross-reference stream

            // The xref stream is never encrypted (§7.5.8.2) and is written unfiltered here, so the
            // parser reads the table above verbatim.
            Ascii($"8 0 obj\n<< /Type /XRef /Size 9 /W [1 4 2] /Root 1 0 R /Encrypt 5 0 R" +
                  $" /ID [<{Hex(fileId)}> <{Hex(fileId)}>] /Length {entries.Count} >>\nstream\n");
            bytes.AddRange(entries);
            Ascii($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");

            return bytes.ToArray();
        }

        /// <summary>
        /// The offset a <c>/XRefStm</c> entry must carry to name this file's own cross-reference
        /// table. Pointing it there keeps the fixture parseable — the parser has already visited
        /// that offset, so it skips it — while still handing the trailer key to the rewrite under
        /// test. The trailer is written after the table, so adding the entry does not move it.
        /// </summary>
        public static int XrefOffsetOf(byte[] pdf)
        {
            string text = Encoding.ASCII.GetString(pdf);
            int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
            string tail = text.Substring(marker + "startxref".Length).Trim();
            int end = tail.IndexOf('\n');
            return int.Parse(end < 0 ? tail : tail.Substring(0, end).Trim());
        }

        // ------------------------------------------------------------ ISO 32000-1 §7.6.3, by hand

        /// <summary>Algorithm 2 steps (a)-(f) for revision 2: one MD5 over the padded password, /O, /P and the file ID.</summary>
        private static byte[] FileKey(string userPassword, byte[] oValue, int pValue, byte[] fileId)
        {
            byte[] p =
            {
                (byte)(pValue         & 0xFF),   // low-order byte first (step (d))
                (byte)((pValue >>  8) & 0xFF),
                (byte)((pValue >> 16) & 0xFF),
                (byte)((pValue >> 24) & 0xFF),
            };
            return First(Digest(Padded(userPassword), oValue, p, fileId), KeyBytes);
        }

        /// <summary>Algorithm 3 for revision 2: RC4 the padded user password under MD5(padded owner password).</summary>
        private static byte[] OwnerValue(string ownerPassword, string userPassword)
            => Rc4(First(Digest(Padded(ownerPassword)), KeyBytes), Padded(userPassword));

        /// <summary>Algorithm 4 (revision 2): RC4 the padding string itself under the file key.</summary>
        private static byte[] UserValue(byte[] fileKey) => Rc4(fileKey, PadString);

        /// <summary>Algorithm 1: MD5(file key || low 3 bytes of the object number || low 2 of the generation).</summary>
        private static byte[] ObjectKey(byte[] fileKey, int objectNumber, int generation)
        {
            byte[] suffix =
            {
                (byte)(objectNumber         & 0xFF),
                (byte)((objectNumber >>  8) & 0xFF),
                (byte)((objectNumber >> 16) & 0xFF),
                (byte)(generation         & 0xFF),
                (byte)((generation >>  8) & 0xFF),
            };
            return First(Digest(fileKey, suffix), Math.Min(fileKey.Length + suffix.Length, 16));
        }

        private static byte[] Padded(string password)
        {
            byte[] raw = Encoding.GetEncoding("iso-8859-1").GetBytes(password ?? "");
            var padded = new byte[PadString.Length];
            int take = Math.Min(raw.Length, padded.Length);
            Array.Copy(raw, padded, take);
            Array.Copy(PadString, 0, padded, take, padded.Length - take);
            return padded;
        }

        /// <summary>RC4 (ARC4) written out here rather than taken from BouncyCastle, so the fixture shares no cipher code with the decryptor.</summary>
        private static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }

            var output = new byte[data.Length];
            for (int n = 0, x = 0, y = 0; n < data.Length; n++)
            {
                x = (x + 1) & 0xFF;
                y = (y + s[x]) & 0xFF;
                (s[x], s[y]) = (s[y], s[x]);
                output[n] = (byte)(data[n] ^ s[(s[x] + s[y]) & 0xFF]);
            }
            return output;
        }

        private static byte[] Digest(params byte[][] parts)
        {
            var all = new List<byte>();
            foreach (byte[] part in parts) all.AddRange(part);
            using var md5 = MD5.Create();
            return md5.ComputeHash(all.ToArray());
        }

        private static byte[] First(byte[] source, int count)
        {
            var result = new byte[count];
            Array.Copy(source, result, count);
            return result;
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }
}
