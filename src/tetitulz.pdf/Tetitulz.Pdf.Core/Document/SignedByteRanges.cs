// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// The §12.8.1 <c>/ByteRange</c> <b>read</b> side: the four integers a signature dictionary
    /// declares as the two file regions it covers.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation.</b> <c>Signing.SignatureByteRange</c> is the *write* side —
    /// the arithmetic that decides where the two ranges go around a <c>/Contents</c> placeholder.
    /// This is the other half: reading back what a file declares, for <c>PdfSignatureVerifier</c>
    /// (a signature and a document timestamp), <c>PdfCounterSigner</c>, <c>DocMdpAnalyzer</c>,
    /// <c>Base.Operations.Documents.PdfIncrementalSaveValidator</c> and
    /// <c>Base.Operations.Documents.PdfRevisions</c>. The two things that matter:</para>
    ///
    /// <list type="bullet">
    ///   <item><description><b>Resolving the array.</b> <c>PdfDictionary.GetArray</c> tests the
    ///   RAW value's type and does not follow an indirect reference, so <c>/ByteRange 9 0 R</c>
    ///   would read as <i>absent</i> — and absent is not "invalid" to any of these callers, it is
    ///   "there is no signature here".</description></item>
    ///   <item><description><b>Type-checking the elements.</b> <c>PdfArray.GetLong</c> <i>throws</i>
    ///   <see cref="System.InvalidOperationException"/> for anything that is not a
    ///   <see cref="PdfNumber"/> — including a <see cref="PdfReference"/>, which §7.3.10 permits
    ///   anywhere a value is permitted; a blanket <c>catch</c> around it would discard
    ///   <b>every remaining signature in the document</b>, not just the malformed one.</description></item>
    /// </list>
    ///
    /// <para><b>On well-formed input all six agreed</b> — a direct array of four direct integers
    /// is what every producer in this tree writes, and what <c>PdfSigner</c> and
    /// <c>PdfDocumentTimestamper</c> emit. The separating input is an indirect value where a
    /// direct one was assumed, so this is a correctness fix for legal-but-unusual files, not a
    /// change to anything a normal file does.</para>
    ///
    /// <para><b>Resolving cannot be used to forge coverage.</b> The four values only say which
    /// bytes to digest; the CMS digest still has to match over exactly those bytes, and every
    /// caller re-applies its own bounds rule (first range starts at 0, ranges ordered, inside the
    /// file, gap no larger than the <c>/Contents</c> hole) to the values this hands back. What
    /// resolving buys is that a cryptographically valid signature stops being reported invalid —
    /// and, in the verifier, that <c>ByteRangeCoverageEnd</c> stops staying <c>0</c>, which is
    /// what silently disables the shadow/append check for that signature.</para>
    ///
    /// <para><b>Bounds are deliberately NOT checked here.</b> The callers' rules genuinely differ:
    /// the verifier fails closed, <c>DocMdpAnalyzer</c> returns 0 so its caller records a
    /// violation, and <c>PdfIncrementalSaveValidator</c> keeps the report and describes the
    /// problem in <c>Issue</c>. Folding them together would turn a report into a refusal.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer.</para>
    /// </remarks>
    internal readonly struct SignedByteRanges
    {
        private SignedByteRanges(long offset1, long length1, long offset2, long length2, int elementCount)
        {
            Offset1 = offset1;
            Length1 = length1;
            Offset2 = offset2;
            Length2 = length2;
            ElementCount = elementCount;
        }

        /// <summary><c>/ByteRange</c> element 0 — start of the first signed range.</summary>
        internal long Offset1 { get; }

        /// <summary><c>/ByteRange</c> element 1 — length of the first signed range.</summary>
        internal long Length1 { get; }

        /// <summary><c>/ByteRange</c> element 2 — start of the second signed range.</summary>
        internal long Offset2 { get; }

        /// <summary><c>/ByteRange</c> element 3 — length of the second signed range.</summary>
        internal long Length2 { get; }

        /// <summary>
        /// How many elements the array actually held. A conforming <c>/ByteRange</c> has exactly
        /// four; callers that require exactly four (the verifier does, because a fifth pair would
        /// be a second signed region it does not digest) test this themselves rather than having
        /// the rule imposed on the callers that tolerate a longer array.
        /// </summary>
        internal int ElementCount { get; }

        /// <summary>The offset one past the last signed byte — <c>Offset2 + Length2</c>.</summary>
        internal long CoverageEnd => Offset2 + Length2;

        /// <summary>
        /// Whether a signature whose signed bytes end at <paramref name="coverageEnd"/> reaches the
        /// end of <paramref name="file"/>: at its last byte, or at a <c>%%EOF</c> that nothing but one
        /// end-of-line marker follows.
        /// </summary>
        /// <remarks>
        /// <para><b>ISO 32000-2 §12.8.1:</b> for multiple signatures the range runs "from the "%PDF-"
        /// comment at the beginning of the PDF document to the end of the "%%EOF" comment, possibly
        /// followed by an optional EOL marker". A signature whose range stops at <c>%%EOF</c> leaves
        /// that marker unsigned, and the marker is not a revision appended after it. Every check that
        /// compared the range's end with the file's length reported such a signature as not covering
        /// the document — the verifier as a possible shadow attack.</para>
        /// <para>One marker only: CR, LF or CR LF. Anything more is bytes the signature does not
        /// cover.</para>
        /// </remarks>
        /// <param name="file">The file the signature is in; read at the end and left where it was.</param>
        /// <param name="coverageEnd">One past the last signed byte.</param>
        /// <param name="fileLength">The file's length.</param>
        internal static bool ReachesEndOfFile(System.IO.Stream file, long coverageEnd, long fileLength)
        {
            if (coverageEnd == fileLength) return true;

            const int markerLength = 5;     // "%%EOF"
            long trailing = fileLength - coverageEnd;
            if (file == null || trailing < 1 || trailing > 2 || coverageEnd < markerLength) return false;

            var tail = new byte[markerLength + trailing];
            long saved = file.Position;
            try
            {
                file.Position = coverageEnd - markerLength;
                // A short read needs no test of its own: the bytes it leaves are zeros, which match
                // neither the marker nor an end-of-line.
                IO.StreamBytes.Fill(file, tail, 0, tail.Length);
            }
            finally
            {
                file.Position = saved;
            }

            bool marker = tail[0] == (byte)'%' && tail[1] == (byte)'%' && tail[2] == (byte)'E'
                       && tail[3] == (byte)'O' && tail[4] == (byte)'F';
            bool endOfLine = trailing == 1
                ? tail[markerLength] == (byte)'\n' || tail[markerLength] == (byte)'\r'
                : tail[markerLength] == (byte)'\r' && tail[markerLength + 1] == (byte)'\n';
            return marker && endOfLine;
        }

        /// <summary>
        /// The bytes a signature covers — both ranges, concatenated — or <c>null</c> when the file
        /// ends before they do.
        /// </summary>
        /// <remarks>
        /// <para><b>One reader</b>, for <c>PdfSigner</c> reading the ranges it has just written and
        /// <c>PdfSignatureVerifier</c> reading them for a signature and for a document timestamp. A
        /// short read answers <c>null</c>, never an exception of its own type that a caller's catch
        /// could miss, so it lands in the report.</para>
        ///
        /// <para>Bounds stay the callers' (see the type's remarks). This refuses only what it could
        /// not allocate or read: a negative value, or two lengths that do not fit one array.</para>
        /// </remarks>
        /// <param name="file">The file the ranges index. Its position is left after the last byte read.</param>
        /// <param name="offset1">Start of the first range.</param>
        /// <param name="length1">Length of the first range.</param>
        /// <param name="offset2">Start of the second range.</param>
        /// <param name="length2">Length of the second range.</param>
        internal static byte[] ReadSignedBytes(
            System.IO.Stream file, long offset1, long length1, long offset2, long length2)
        {
            if (file == null || offset1 < 0 || length1 < 0 || offset2 < 0 || length2 < 0) return null;
            if (length1 > int.MaxValue - length2) return null;   // also refuses a length2 past int.MaxValue

            var signed = new byte[length1 + length2];
            file.Position = offset1;
            if (IO.StreamBytes.Fill(file, signed, 0, (int)length1) < length1) return null;
            file.Position = offset2;
            if (IO.StreamBytes.Fill(file, signed, (int)length1, (int)length2) < length2) return null;
            return signed;
        }

        /// <summary>The most of a signature's object read ahead of its <c>/Contents</c>; every signer writes a few entries there.</summary>
        internal const int MaxBytesBeforeContents = 1 << 20;

        /// <summary>
        /// Whether the object starting at <paramref name="objectOffset"/> is the one the gap stands in:
        /// it starts ahead of the gap and has not ended by it. A dictionary anywhere else holds a copy of
        /// the <c>/Contents</c> and <c>/ByteRange</c>, whatever else it says, and is not among the bytes signed.
        /// </summary>
        /// <param name="file">The file; its position is put back.</param>
        /// <param name="objectOffset">Where the cross-reference table says the object starts.</param>
        /// <param name="gapStart">Where the excluded gap starts.</param>
        internal static bool GapIsInObjectAt(System.IO.Stream file, long objectOffset, long gapStart)
        {
            if (file == null || objectOffset < 0 || objectOffset >= gapStart || gapStart - objectOffset > MaxBytesBeforeContents)
                return false;

            var ahead = new byte[gapStart - objectOffset];
            long saved = file.Position;
            try
            {
                file.Position = objectOffset;
                if (IO.StreamBytes.Fill(file, ahead, 0, ahead.Length) != ahead.Length) return false;
            }
            finally
            {
                file.Position = saved;
            }

            for (int i = 0; i + EndOfObject.Length <= ahead.Length; i++)
            {
                int matched = 0;
                while (matched < EndOfObject.Length && ahead[i + matched] == EndOfObject[matched]) matched++;
                if (matched == EndOfObject.Length) return false;
            }
            return true;
        }

        private static readonly byte[] EndOfObject = { (byte)'e', (byte)'n', (byte)'d', (byte)'o', (byte)'b', (byte)'j' };

        /// <summary>
        /// Whether the excluded gap holds this signature's <c>/Contents</c> and nothing else: its hex
        /// digits, in order, white space among them, with or without the delimiters §12.8.1 puts inside
        /// the gap. A literal string is refused, since §12.8.1 requires the hex form and only that form
        /// fills the gap the size check allows.
        /// </summary>
        /// <param name="file">The file; its position is left after the gap.</param>
        /// <param name="start">Where the gap starts.</param>
        /// <param name="length">The gap's length, already bounded by the size of <paramref name="contents"/>.</param>
        /// <param name="contents">The decoded <c>/Contents</c> of the signature dictionary in force.</param>
        internal static bool GapHoldsOnlyContents(System.IO.Stream file, long start, long length, byte[] contents)
        {
            if (file == null || length < 0 || length > int.MaxValue) return false;

            var gap = new byte[length];
            file.Position = start;
            if (IO.StreamBytes.Fill(file, gap, 0, gap.Length) < gap.Length) return false;

            int at = 0;
            SkipWhiteSpace(gap, ref at);
            bool delimited = at < gap.Length && gap[at] == (byte)'<';
            if (delimited) at++;

            int matched = 0, high = -1;
            for (; at < gap.Length; at++)
            {
                byte b = gap[at];
                if (IsWhiteSpace(b)) continue;
                int digit = HexDigit(b);
                if (digit < 0) break;
                if (high < 0) { high = digit; continue; }
                if (matched >= contents.Length || contents[matched] != (byte)((high << 4) | digit)) return false;
                matched++;
                high = -1;
            }

            // §7.3.4.3: an odd final digit is followed by an assumed zero.
            if (high >= 0)
            {
                if (matched >= contents.Length || contents[matched] != (byte)(high << 4)) return false;
                matched++;
            }
            if (matched != contents.Length) return false;

            if (delimited)
            {
                if (at >= gap.Length || gap[at] != (byte)'>') return false;
                at++;
            }
            SkipWhiteSpace(gap, ref at);
            return at == gap.Length;
        }

        private static void SkipWhiteSpace(byte[] bytes, ref int at)
        {
            while (at < bytes.Length && IsWhiteSpace(bytes[at])) at++;
        }

        private static bool IsWhiteSpace(byte b)
            => b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;

        private static int HexDigit(byte b)
        {
            if (b >= (byte)'0' && b <= (byte)'9') return b - '0';
            if (b >= (byte)'a' && b <= (byte)'f') return b - 'a' + 10;
            if (b >= (byte)'A' && b <= (byte)'F') return b - 'A' + 10;
            return -1;
        }

        /// <summary>Chunk size for <see cref="DigestSignedBytes"/> — a throughput trade-off, not a bound.</summary>
        private const int DigestChunkBytes = 64 * 1024;

        /// <summary>
        /// Hands the bytes a signature covers — both ranges, in order — to <paramref name="append"/> in
        /// chunks, without holding the document in memory; <c>false</c> when the file ends before they
        /// do.
        /// </summary>
        /// <remarks>
        /// <para><b>One streamed reader</b>, for <c>Signing.Timestamping.PdfDocumentTimestamper</c>
        /// and <c>Writing.StandalonePdfMacSeal</c> alike. A file that ends early answers
        /// <c>false</c>: hashing what was read would send a TSA a digest of fewer bytes than the <c>/ByteRange</c> names,
        /// to certify.</para>
        /// <para><see cref="ReadSignedBytes"/> is the other form, for a caller that needs the bytes
        /// themselves: a CMS library verifying or generating over an array.</para>
        /// </remarks>
        /// <param name="file">The file the ranges index. Its position is left after the last byte read.</param>
        /// <param name="offset1">Start of the first range.</param>
        /// <param name="length1">Length of the first range.</param>
        /// <param name="offset2">Start of the second range.</param>
        /// <param name="length2">Length of the second range.</param>
        /// <param name="append">Receives each chunk as <c>(buffer, offset, count)</c> — a hash's append.</param>
        internal static bool DigestSignedBytes(
            System.IO.Stream file, long offset1, long length1, long offset2, long length2,
            System.Action<byte[], int, int> append)
        {
            if (file == null || append == null
                || offset1 < 0 || length1 < 0 || offset2 < 0 || length2 < 0) return false;

            var chunk = new byte[DigestChunkBytes];
            return Feed(file, offset1, length1, chunk, append)
                && Feed(file, offset2, length2, chunk, append);
        }

        private static bool Feed(
            System.IO.Stream file, long offset, long length, byte[] chunk, System.Action<byte[], int, int> append)
        {
            file.Position = offset;
            for (long remaining = length; remaining > 0;)
            {
                int want = (int)System.Math.Min(chunk.Length, remaining);
                if (IO.StreamBytes.Fill(file, chunk, 0, want) < want) return false;
                append(chunk, 0, want);
                remaining -= want;
            }
            return true;
        }

        /// <summary>Total number of signed bytes — the buffer size needed to concatenate both
        /// ranges before digesting them.</summary>
        internal long SignedLength => Length1 + Length2;

        /// <summary>Start of the excluded gap — <c>Offset1 + Length1</c>, i.e. the <c>&lt;</c> that
        /// opens <c>/Contents</c> in a file this library wrote.</summary>
        internal long HoleStart => Offset1 + Length1;

        /// <summary>
        /// Reads a signature dictionary's <c>/ByteRange</c>, following an indirect reference for
        /// the array itself and for each of its first four elements.
        /// </summary>
        /// <param name="sigDict">The signature value dictionary (the field's <c>/V</c>).</param>
        /// <param name="doc">
        /// The document the dictionary came from, used to resolve references. May be <c>null</c>,
        /// in which case only a directly-written array of directly-written numbers is read.
        /// </param>
        /// <param name="ranges">The four values, when this returns <c>true</c>.</param>
        /// <param name="error">
        /// Why the read failed, when this returns <c>false</c>; <c>null</c> on success. Worded so
        /// a caller can surface it unchanged.
        /// </param>
        internal static bool TryRead(
            PdfDictionary sigDict, PdfDocument doc, out SignedByteRanges ranges, out string error)
        {
            ranges = default;

            PdfObject rawArray = sigDict?[PdfName.ByteRange];
            if (rawArray is PdfReference && doc != null)
                rawArray = doc.ResolveObject(rawArray);

            if (!(rawArray is PdfArray array) || array.Count < 4)
            {
                error = "Invalid or missing /ByteRange array.";
                return false;
            }

            long o1 = 0, l1 = 0, o2 = 0, l2 = 0;
            for (int i = 0; i < 4; i++)
            {
                PdfObject element = array[i];
                if (element is PdfReference && doc != null)
                    element = doc.ResolveObject(element);

                // §12.8.1: integers. A real is not truncated into one: 100.5 read as 100 is a range this
                // reader and a stricter one disagree on.
                if (!(element is PdfNumber number) || !number.IsInteger)
                {
                    error = $"Invalid /ByteRange element at index {i}: expected integer, got " +
                            (element is PdfNumber real ? "the real " + real : array[i]?.GetType().Name ?? "null") + ".";
                    return false;
                }

                long value = number.LongValue;
                switch (i)
                {
                    case 0: o1 = value; break;
                    case 1: l1 = value; break;
                    case 2: o2 = value; break;
                    default: l2 = value; break;
                }
            }

            ranges = new SignedByteRanges(o1, l1, o2, l2, array.Count);
            error = null;
            return true;
        }
    }
}
