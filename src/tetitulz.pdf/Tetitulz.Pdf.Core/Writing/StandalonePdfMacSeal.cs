// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// ISO/TS 32004 §6.5.1 — the file-level half of a <b>standalone PDF MAC token</b>: the trailer's
    /// <c>/AuthCode</c> dictionary, a <c>/ByteRange</c> covering the whole file except the value of its
    /// <c>/MAC</c> entry, and the SHA-256 digest of exactly those bytes. The token itself — CMS
    /// <c>AuthenticatedData</c>, key derivation, key wrap, HMAC — is the subclass's, because it needs
    /// cryptography this assembly does not carry.
    /// </summary>
    /// <remarks>
    /// <para><b>One placement, two writers.</b> A whole-file encryption write and an appended revision
    /// both end in a trailer that must carry the token, and the appended case is the one §B.2.2.2 warns
    /// about: a revision written without a fresh token leaves the previous one covering only part of
    /// the file, which a validator reads as possible tampering. Both go through
    /// <see cref="CreatePlaceholder"/> and <see cref="Seal"/>, so the byte arithmetic exists once.</para>
    /// <para><b>No headroom.</b> Table 6 requires the <c>/MAC</c> space to be allocated before the token
    /// is computed and to hold exactly its hex digits, with no padding. §6.3.7 forbids unauthenticated
    /// attributes, so a token's DER length depends only on fixed-size fields and
    /// <see cref="TokenLength"/> is exact.</para>
    /// </remarks>
    public abstract class StandalonePdfMacSeal
    {
        /// <summary>Twelve digits per offset — files up to ~999 GB, the width the signature writers reserve.</summary>
        private const long ByteRangePlaceholderValue = 999999999999L;

        /// <summary>The DER length, in bytes, of every token <see cref="BuildToken"/> returns.</summary>
        public abstract int TokenLength { get; }

        /// <summary>
        /// The DER-encoded PDF MAC token for a document whose <c>/ByteRange</c> bytes digest, under
        /// SHA-256, to <paramref name="dataDigest"/>. Must be exactly <see cref="TokenLength"/> bytes.
        /// </summary>
        public abstract byte[] BuildToken(byte[] dataDigest);

        /// <summary>
        /// The <c>/AuthCode</c> dictionary to set on a trailer — or on a cross-reference stream's
        /// dictionary, which stands in for it — before that is written: <c>/MACLocation /Standalone</c>,
        /// a placeholder <c>/ByteRange</c> and a zero-filled hexadecimal <c>/MAC</c> of
        /// <see cref="TokenLength"/> bytes. It must be written unencrypted, as both of those are.
        /// </summary>
        public PdfDictionary CreatePlaceholder()
        {
            var byteRange = new PdfArray();
            byteRange.Add(new PdfNumber(0));
            for (int i = 0; i < 3; i++) byteRange.Add(new PdfNumber(ByteRangePlaceholderValue));

            var authCode = new PdfDictionary();
            authCode.Set(PdfName.MACLocation, PdfName.Standalone);
            authCode.Set(PdfName.ByteRange, byteRange);
            authCode.Set(PdfName.MAC, new PdfString(new byte[TokenLength], isHex: true));
            return authCode;
        }

        /// <summary>
        /// Seals a written file: finds the first <see cref="CreatePlaceholder"/> dictionary at or after
        /// <paramref name="searchFrom"/>, writes the real <c>/ByteRange</c> — the file from position 0 to
        /// the stream's current position, less the <c>/MAC</c> value — digests those bytes, and writes
        /// the token's hex digits into the <c>/MAC</c> string. The stream is left at its end.
        /// </summary>
        /// <param name="pdf">
        /// The file, starting at position 0 and ending at the current position; seekable, readable and
        /// writable.
        /// </param>
        /// <param name="searchFrom">
        /// Where the trailer or cross-reference stream carrying the placeholder begins — past every
        /// object's data, so nothing but that dictionary can match.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
        /// <exception cref="ArgumentException">The stream cannot be sought, read and written.</exception>
        /// <exception cref="PdfException">No well-formed placeholder follows <paramref name="searchFrom"/>, or the token is not <see cref="TokenLength"/> bytes.</exception>
        public void Seal(Stream pdf, long searchFrom)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            if (!pdf.CanSeek || !pdf.CanRead || !pdf.CanWrite)
                throw new ArgumentException("Sealing a PDF MAC token needs a seekable, readable and writable stream.", nameof(pdf));

            long end = pdf.Position;
            if (searchFrom < 0 || searchFrom > end) throw new ArgumentOutOfRangeException(nameof(searchFrom));

            byte[] tail = new byte[end - searchFrom];
            pdf.Position = searchFrom;
            if (IO.StreamBytes.Fill(pdf, tail, 0, tail.Length) < tail.Length)
                throw new PdfException("The file ended before the PDF MAC placeholder could be read.");

            int authCode = IndexOfName(tail, "/AuthCode", 0);
            int byteRangeKey = authCode < 0 ? -1 : IndexOfName(tail, "/ByteRange", authCode);
            int open = byteRangeKey < 0 ? -1 : NextNonWhitespace(tail, byteRangeKey + "/ByteRange".Length, '[');
            int close = open < 0 ? -1 : Array.IndexOf(tail, (byte)']', open);
            int macKey = authCode < 0 ? -1 : IndexOfName(tail, "/MAC", authCode);
            int lt = macKey < 0 ? -1 : NextNonWhitespace(tail, macKey + "/MAC".Length, '<');
            long gt = lt < 0 ? -1 : (long)lt + 1 + 2L * TokenLength;
            if (close < 0 || lt < 0 || gt >= tail.Length || tail[gt] != (byte)'>')
                throw new PdfException("No /AuthCode placeholder for a " + TokenLength.ToString(CultureInfo.InvariantCulture) +
                                       "-byte PDF MAC token follows offset " + searchFrom.ToString(CultureInfo.InvariantCulture) + ".");

            // §6.5.1 / Table 6: [0 L1 S L2] — everything but the MAC value, whose '<' ends the first
            // range and whose '>' is the last byte before the second.
            long macStart = searchFrom + lt;
            long afterStart = searchFrom + gt + 1;
            string range = "[0 " + macStart.ToString(CultureInfo.InvariantCulture) + " " +
                           afterStart.ToString(CultureInfo.InvariantCulture) + " " +
                           (end - afterStart).ToString(CultureInfo.InvariantCulture);
            int width = close - open + 1;
            if (range.Length + 1 > width)
                throw new PdfException("The /ByteRange placeholder is too short for this file's offsets.");
            byte[] rangeBytes = Encoding.ASCII.GetBytes(range.PadRight(width - 1) + "]");
            pdf.Position = searchFrom + open;
            pdf.Write(rangeBytes, 0, rangeBytes.Length);

            byte[] digest = DigestByteRange(pdf, 0, macStart, afterStart, end - afterStart)
                ?? throw new PdfException("The file ended before the PDF MAC byte range did.");
            byte[] token = BuildToken(digest);
            if (token == null || token.Length != TokenLength)
                throw new PdfException("The PDF MAC token is not the " + TokenLength.ToString(CultureInfo.InvariantCulture) +
                                       " bytes its placeholder was allocated for.");

            byte[] hex = Encoding.ASCII.GetBytes(ToUpperHex(token));
            pdf.Position = macStart + 1;
            pdf.Write(hex, 0, hex.Length);
            pdf.Position = end;
        }

        /// <summary>
        /// Whether a range ending at <paramref name="coverageEnd"/> reaches the end of
        /// <paramref name="pdf"/> — ISO/TS 32004 §6.5.1's requirement that a standalone token cover
        /// the entire file.
        /// </summary>
        /// <remarks>
        /// <para><b>A token that verifies is not necessarily a token that covers.</b> Appending a
        /// revision leaves the earlier token's own range intact — the bytes it names are still
        /// there, unchanged — so its digest still matches and only its <i>extent</i> is wrong.
        /// Annex B.2.2.2 names exactly this: failure to cover the entire file "can be an indication
        /// that the document was tampered with. It can also indicate that the PDF MAC token has
        /// become stale, e.g. because another PDF processor failed to apply a new MAC after
        /// incrementally updating the document."</para>
        ///
        /// <para><b>Forwarded to the signature rule rather than restated.</b> The end-of-file
        /// question is the one ISO 32000-2 §12.8.1 already answers for a signature, down to the
        /// optional EOL marker after <c>%%EOF</c> that a range is allowed to stop short of. A
        /// second reading of "does this reach the end" would differ from the signature verifier's
        /// on exactly that byte, and report conforming files as short.</para>
        /// </remarks>
        public static bool CoversToEndOfFile(Stream pdf, long coverageEnd) =>
            pdf != null && Document.SignedByteRanges.ReachesEndOfFile(pdf, coverageEnd, pdf.Length);

        /// <summary>
        /// Whether the bytes a standalone token's <c>/ByteRange</c> leaves out — <paramref name="gapStart"/>
        /// up to <paramref name="gapEnd"/> — are exactly ISO/TS 32004 Table 6's: '&lt;', the hex digits of
        /// <paramref name="token"/> and '&gt;', with no white space (its NOTE 2) and nothing else. What
        /// <see cref="Seal"/> writes there, and the only bytes a reader may find there: anything else is
        /// bytes no digest covers, standing where only the token may.
        /// </summary>
        /// <param name="pdf">The file, seekable; its position is restored.</param>
        /// <param name="gapStart">Where the first range ends.</param>
        /// <param name="gapEnd">Where the second range starts.</param>
        /// <param name="token">The token the <c>/MAC</c> string decodes to.</param>
        public static bool GapHoldsOnlyToken(Stream pdf, long gapStart, long gapEnd, byte[] token)
        {
            if (pdf == null || token == null || gapStart < 0 || gapEnd - gapStart != 2L + 2L * token.Length) return false;
            var gap = new byte[gapEnd - gapStart];
            long saved = pdf.Position;
            try
            {
                pdf.Position = gapStart;
                if (IO.StreamBytes.Fill(pdf, gap, 0, gap.Length) < gap.Length) return false;
            }
            finally
            {
                pdf.Position = saved;
            }
            if (gap[0] != (byte)'<' || gap[gap.Length - 1] != (byte)'>') return false;
            for (int i = 0; i < token.Length; i++)
            {
                int high = HexDigit(gap[1 + 2 * i]), low = HexDigit(gap[2 + 2 * i]);
                if (high < 0 || low < 0 || ((high << 4) | low) != token[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether the bytes a signature's <c>/ByteRange</c> leaves out hold its <c>/Contents</c> and nothing
        /// else — the rule the signature verifier holds them to (ISO 32000-2 §12.8.1), for a PDF MAC token
        /// attached to that signature (ISO/TS 32004 §6.5.2), whose verifier this assembly does not carry.
        /// </summary>
        /// <param name="pdf">The file, seekable; its position is left after the gap.</param>
        /// <param name="gapStart">Where the first range ends.</param>
        /// <param name="gapLength">The gap's length.</param>
        /// <param name="contents">The signature dictionary's decoded <c>/Contents</c>.</param>
        public static bool SignatureGapHoldsOnlyContents(Stream pdf, long gapStart, long gapLength, byte[] contents) =>
            pdf != null && contents != null && gapStart >= 0
            && Document.SignedByteRanges.GapHoldsOnlyContents(pdf, gapStart, gapLength, contents);

        private static int HexDigit(byte b) =>
            b >= (byte)'0' && b <= (byte)'9' ? b - '0'
            : b >= (byte)'A' && b <= (byte)'F' ? b - 'A' + 10
            : b >= (byte)'a' && b <= (byte)'f' ? b - 'a' + 10
            : -1;

        /// <summary>
        /// The SHA-256 digest a PDF MAC token is computed over: the two ranges a <c>/ByteRange</c>
        /// names, streamed rather than assembled in memory.
        /// </summary>
        /// <remarks>
        /// <para><b>Public, and taking the four values as the file spells them</b>, because a
        /// <i>reader</i> checking a token has exactly those four numbers and nothing else — where
        /// this class, writing one, derives them from where it put the placeholder. Writing a token
        /// whose digest one implementation computes and checking it with another is the divergence
        /// this repository keeps finding defects in, and here it would be undetectable from either
        /// side: both would be internally consistent and the two would simply disagree.</para>
        ///
        /// <para>Table 6 requires the first element to be zero and ISO 32000-2 Table 255 says the
        /// same of a signature's range, but that is the caller's rule to enforce or not — this
        /// digests what it is given.</para>
        /// </remarks>
        /// <returns>The digest, or null when the ranges reach past the end of the file.</returns>
        public static byte[] DigestByteRange(
            Stream pdf, long offset1, long length1, long offset2, long length2)
        {
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                // §6.5.1 / Table 6's ranges are a signature's shape, so they are read the way a
                // signature's are — through the one streamed reader of two signed ranges.
                return Document.SignedByteRanges.DigestSignedBytes(
                           pdf, offset1, length1, offset2, length2, sha256.AppendData)
                    ? sha256.GetHashAndReset()
                    : null;
            }
        }

        /// <summary>The first occurrence of the name token <paramref name="name"/> at or after <paramref name="from"/> — not a longer name it prefixes.</summary>
        /// <remarks>
        /// Internal, with <see cref="NextNonWhitespace"/>, because <c>Tetitulz.Pdf.Signing</c> locates a
        /// planned signature dictionary's <c>/ByteRange</c> and <c>/Contents</c> placeholders in a written
        /// file by the same two steps — one token scan, not a second copy of it.
        /// </remarks>
        internal static int IndexOfName(byte[] data, string name, int from)
        {
            for (int i = from; i + name.Length <= data.Length; i++)
            {
                int k = 0;
                while (k < name.Length && data[i + k] == name[k]) k++;
                if (k < name.Length) continue;
                int next = i + name.Length;
                if (next == data.Length || IsDelimiterOrWhitespace(data[next])) return i;
            }
            return -1;
        }

        /// <summary>The index of <paramref name="expected"/> when it is the first non-whitespace byte at or after <paramref name="from"/>, else -1.</summary>
        internal static int NextNonWhitespace(byte[] data, int from, char expected)
        {
            int i = from;
            while (i < data.Length && IsWhitespace(data[i])) i++;
            return i < data.Length && data[i] == expected ? i : -1;
        }

        // §7.2.2's character classes, PdfConstants'.
        private static bool IsWhitespace(byte b) => PdfConstants.IsWhitespace(b);

        private static bool IsDelimiterOrWhitespace(byte b) => PdfConstants.IsEndOfToken(b);

        private static string ToUpperHex(byte[] data)
        {
            const string digits = "0123456789ABCDEF";
            var chars = new char[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                chars[i * 2] = digits[data[i] >> 4];
                chars[i * 2 + 1] = digits[data[i] & 0xF];
            }
            return new string(chars);
        }
    }
}
