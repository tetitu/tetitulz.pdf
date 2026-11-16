// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Parsing
{
    /// <summary>
    /// One indirect-object header — <c>N G obj</c> — located by <see cref="ObjectHeaderScanner"/>.
    /// </summary>
    /// <remarks>
    /// The generation digits travel as a <b>span</b> rather than a parsed value because the two
    /// callers disagree about what an unparseable generation means, and both are right for what
    /// they do: <see cref="PdfParser"/>'s recovery scan skips the header (a cross-reference entry
    /// with a wrong generation is worse than a missing one, since a strict reader treats the
    /// mismatch as a stale entry), while <c>Base.Operations.Documents.RawObjectScanner</c> — the scan
    /// behind <c>PdfRepair</c> and FDF import — defaults it to 0 and keeps the object (repair copies
    /// the header verbatim, so the object is still there to be read). Token recognition is the part
    /// that must not differ; that policy is not.
    /// </remarks>
    internal readonly struct ObjectHeaderMatch
    {
        internal ObjectHeaderMatch(
            int objectNumber, long offset, int generationStart, int generationLength, int keywordEnd)
        {
            ObjectNumber = objectNumber;
            Offset = offset;
            GenerationStart = generationStart;
            GenerationLength = generationLength;
            KeywordEnd = keywordEnd;
        }

        /// <summary>The object number the header declares.</summary>
        internal int ObjectNumber { get; }

        /// <summary>
        /// Byte offset of the header's <b>first digit</b> — which is the offset a cross-reference
        /// entry for this object has to carry, not the offset of the <c>obj</c> keyword.
        /// </summary>
        internal long Offset { get; }

        /// <summary>Index of the first generation digit.</summary>
        internal int GenerationStart { get; }

        /// <summary>How many generation digits there are; always at least 1 on a match.</summary>
        internal int GenerationLength { get; }

        /// <summary>
        /// Index one past the <c>j</c> of the keyword. A caller resumes its scan here, which is
        /// what stops one header being re-examined.
        /// </summary>
        internal int KeywordEnd { get; }
    }

    /// <summary>
    /// Recognises the <c>N G obj</c> indirect-object header in a raw byte buffer (§7.3.10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a type of its own.</b> The rule existed twice — in
    /// <see cref="PdfParser"/>'s <c>RecoverXrefByScanning</c> and in
    /// the repair tool's own scan (now <c>Base.Operations.Documents.RawObjectScanner</c>, which FDF
    /// import shares, having carried a third copy) — and the two disagreed on three
    /// inputs that are all legal PDF, in the direction that matters least defensibly: <b>the repair
    /// tool was the stricter of the two</b>, so a file <c>PdfDocument.Open</c> recovered cleanly
    /// came out of the repair tool with zero objects, an xref holding only the free-list head, a
    /// <c>/Root</c> naming an object that is not in the file, and an <b>empty</b> error list. The
    /// separating inputs were <c>12 0\nobj</c> (any §7.2.2 white space may precede the keyword, not
    /// only <c>0x20</c>), <c>12 0 obj&lt;&lt;/Type/Page&gt;&gt;</c> (a delimiter may follow it) and
    /// <c>12 0  obj</c> (the white space between the generation number and the keyword is a
    /// <i>run</i>).
    /// </para>
    /// <para>
    /// <b>It is byte-oriented on purpose.</b> The parser and the repair tool both run it over the
    /// raw bytes. Bytes are the
    /// representation both can share, and it removes a transcode from the parser's path.
    /// </para>
    /// <para>
    /// <b>It is a leaf with no static fields</b>, in the style of <c>Type3Encoding</c> and
    /// <c>SvgSyntax</c>: hanging it off <see cref="PdfParser"/> — which has a type initializer —
    /// would create exactly the type-initialization edge that once crashed a test host part-way
    /// through a parallel run (GAPS.md §2b).
    /// </para>
    /// </remarks>
    internal static class ObjectHeaderScanner
    {
        /// <summary>
        /// Finds the next <c>N G obj</c> header at or after <paramref name="start"/>.
        /// </summary>
        /// <param name="buffer">Raw file bytes.</param>
        /// <param name="count">How many bytes of <paramref name="buffer"/> are populated.</param>
        /// <param name="start">Index to begin searching at.</param>
        /// <param name="match">The header found, when this returns <c>true</c>.</param>
        /// <returns><c>false</c> once no further header exists in the buffer.</returns>
        /// <remarks>
        /// The rule, which is <see cref="PdfParser"/>'s recovery scan verbatim: <c>obj</c> must be a
        /// token of its own — preceded by §7.2.2 white space (which is also what keeps
        /// <c>endobj</c> from matching) and followed by white space, a delimiter or end of buffer —
        /// and behind it must lie, in order, a white-space run, generation digits, a white-space
        /// run and object-number digits, with the object number itself starting a token. That last
        /// clause is what stops <c>x12 0 obj</c> being recovered as object 12 at an offset one byte
        /// into whatever <c>x12</c> was the tail of: a plausible-looking wrong entry rather than a
        /// missing one.
        /// </remarks>
        internal static bool TryFindNext(byte[] buffer, int count, int start, out ObjectHeaderMatch match)
        {
            match = default;
            if (buffer == null) return false;
            if (count > buffer.Length) count = buffer.Length;
            if (start < 0) start = 0;

            int pos = start;
            while (pos < count)
            {
                int objIdx = IndexOfKeyword(buffer, count, pos);
                if (objIdx < 0) return false;

                // Resume past the keyword whether or not this candidate survives, so a rejected
                // header is never re-examined and the caller cannot loop on it.
                pos = objIdx + 3;

                if (objIdx == 0 || !PdfConstants.IsWhitespace(buffer[objIdx - 1])) continue;
                if (pos < count &&
                    !PdfConstants.IsWhitespace(buffer[pos]) && !PdfConstants.IsDelimiter(buffer[pos]))
                    continue;

                // Walk backwards over: white space, generation digits, white space, object digits.
                int walk = objIdx - 1;
                while (walk >= 0 && PdfConstants.IsWhitespace(buffer[walk])) walk--;
                int genEnd = walk + 1;
                while (walk >= 0 && PdfConstants.IsDigit(buffer[walk])) walk--;
                int genStart = walk + 1;
                if (genStart == genEnd) continue;                    // no generation number

                if (walk < 0 || !PdfConstants.IsWhitespace(buffer[walk])) continue;
                while (walk >= 0 && PdfConstants.IsWhitespace(buffer[walk])) walk--;
                int numEnd = walk + 1;
                while (walk >= 0 && PdfConstants.IsDigit(buffer[walk])) walk--;
                int numStart = walk + 1;
                if (numStart == numEnd) continue;                    // no object number

                if (numStart > 0 && !PdfConstants.IsWhitespace(buffer[numStart - 1])) continue;

                if (!TryParseDigits(buffer, numStart, numEnd - numStart, out int objNum)) continue;

                match = new ObjectHeaderMatch(objNum, numStart, genStart, genEnd - genStart, pos);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Parses a run of ASCII digits, refusing anything that would overflow an
        /// <see cref="int"/>. Allocation-free, so a scan over a large file does not mint a string
        /// per candidate header.
        /// </summary>
        internal static bool TryParseDigits(byte[] buffer, int start, int length, out int value)
        {
            value = 0;
            if (buffer == null || length <= 0 || start < 0 || start > buffer.Length - length) return false;

            long accumulated = 0;
            for (int i = start; i < start + length; i++)
            {
                byte b = buffer[i];
                if (!PdfConstants.IsDigit(b)) return false;
                accumulated = (accumulated * 10) + (b - (byte)'0');
                if (accumulated > int.MaxValue) return false;
            }

            value = (int)accumulated;
            return true;
        }

        /// <summary>
        /// Where a scan resumes after the object whose header ends at <paramref name="keywordEnd"/>:
        /// past its stream data when it is a stream, else right after the keyword. The data is stepped
        /// over by a direct <c>/Length</c> when an <c>endstream</c> stands where it says, otherwise to
        /// the first <c>endstream</c> after it — <c>RawObjectScanner</c>'s rule, which reads an indirect
        /// <c>/Length</c> too; here an indirect one takes the second path.
        /// </summary>
        /// <remarks>
        /// A header inside a stream's data — an uncompressed embedded PDF's <c>4 0 obj</c> — is not an
        /// object of this file, and a scan that took it replaced the real object 4 with the attachment's.
        /// An object with no <c>stream</c> before its
        /// <c>endobj</c>, or one whose stream has no <c>endstream</c> at all, resumes after the keyword.
        /// </remarks>
        internal static int ResumeAfter(byte[] buffer, int count, int keywordEnd, ResumeCursor cursor = null)
        {
            if (buffer == null || keywordEnd < 0) return keywordEnd;
            if (count > buffer.Length) count = buffer.Length;
            cursor ??= new ResumeCursor();

            // One forward pass to the first of `endobj`, the next header's `obj`, or `stream`: the
            // bytes it passes are the ones the caller's scan passes next, so a scan over the file
            // stays linear. Two searches to the end of the buffer per header made a million bare
            // headers (RepairBoundsTests) a scan of eight terabytes.
            int streamKeyword = FirstBoundary(buffer, count, keywordEnd, cursor);
            if (streamKeyword < 0) return keywordEnd;

            int dataStart = streamKeyword + Stream.Length;
            if (dataStart < count && buffer[dataStart] == (byte)'\r') dataStart++;
            if (dataStart < count && buffer[dataStart] == (byte)'\n') dataStart++;

            long declared = DirectLength(buffer, keywordEnd, streamKeyword);
            if (declared >= 0 && declared <= count - dataStart)
            {
                int at = (int)(dataStart + declared);
                while (at < count && PdfConstants.IsWhitespace(buffer[at])) at++;
                if (MatchesAt(buffer, count, at, Endstream)) return at + Endstream.Length;
            }

            // No `endstream` after one position means none after any later one: remembered, so a
            // file of streams that never end is searched to its end once, not once per stream.
            if (dataStart >= cursor.NoEndstreamFrom) return keywordEnd;
            int endstream = IndexOfToken(buffer, count, dataStart, Endstream);
            cursor.BytesExamined += (endstream < 0 ? count : endstream) - dataStart;
            if (endstream < 0) { cursor.NoEndstreamFrom = dataStart; return keywordEnd; }
            return endstream + Endstream.Length;
        }

        /// <summary>
        /// What one scan over a buffer remembers between its <see cref="ResumeAfter"/> calls, and
        /// the count of bytes they examined — the evidence that the scan is linear in the file.
        /// </summary>
        internal sealed class ResumeCursor
        {
            /// <summary>The earliest stream-data start after which the buffer holds no <c>endstream</c>.</summary>
            internal int NoEndstreamFrom = int.MaxValue;

            /// <summary>Bytes the resume searches have read; bounded by a small multiple of the buffer.</summary>
            internal long BytesExamined;
        }

        /// <summary>
        /// Index of the first <c>stream</c> token after <paramref name="start"/> that comes before any
        /// <c>endobj</c> or <c>obj</c> token, else -1 — the object has no stream data to step over.
        /// </summary>
        private static int FirstBoundary(byte[] buffer, int count, int start, ResumeCursor cursor)
        {
            int i = start < 0 ? 0 : start;
            int result = -1;
            for (; i + Obj.Length <= count; i++)
            {
                if (!IsTokenAt(buffer, count, i, Stream) && !IsTokenAt(buffer, count, i, Endobj) && !IsTokenAt(buffer, count, i, Obj)) continue;
                if (IsTokenAt(buffer, count, i, Stream)) result = i;
                break;
            }
            cursor.BytesExamined += i - (start < 0 ? 0 : start);
            return result;
        }

        /// <summary>True when <paramref name="token"/> stands at <paramref name="at"/> as a token of its own (white space or a delimiter on both sides).</summary>
        private static bool IsTokenAt(byte[] buffer, int count, int at, byte[] token)
        {
            if (!MatchesAt(buffer, count, at, token)) return false;
            if (at > 0 && !PdfConstants.IsWhitespace(buffer[at - 1]) && !PdfConstants.IsDelimiter(buffer[at - 1])) return false;
            int after = at + token.Length;
            return after >= count || PdfConstants.IsWhitespace(buffer[after]) || PdfConstants.IsDelimiter(buffer[after]);
        }

        private static readonly byte[] Obj = { (byte)'o', (byte)'b', (byte)'j' };
        private static readonly byte[] Endobj = { (byte)'e', (byte)'n', (byte)'d', (byte)'o', (byte)'b', (byte)'j' };
        private static readonly byte[] Stream = { (byte)'s', (byte)'t', (byte)'r', (byte)'e', (byte)'a', (byte)'m' };
        private static readonly byte[] Endstream = { (byte)'e', (byte)'n', (byte)'d', (byte)'s', (byte)'t', (byte)'r', (byte)'e', (byte)'a', (byte)'m' };
        private static readonly byte[] LengthKey = { (byte)'/', (byte)'L', (byte)'e', (byte)'n', (byte)'g', (byte)'t', (byte)'h' };

        /// <summary>A direct <c>/Length N</c> in the dictionary bytes <c>[from, to)</c>, or -1 (absent, indirect, or not digits).</summary>
        private static long DirectLength(byte[] buffer, int from, int to)
        {
            int key = IndexOfToken(buffer, to, from, LengthKey);
            if (key < 0) return -1;
            int at = key + LengthKey.Length;
            while (at < to && PdfConstants.IsWhitespace(buffer[at])) at++;
            long value = 0;
            int digits = 0;
            while (at < to && PdfConstants.IsDigit(buffer[at]) && digits < 12) { value = value * 10 + (buffer[at] - (byte)'0'); at++; digits++; }
            if (digits == 0) return -1;
            // `/Length 6 0 R` — the digits are an object number, not the length.
            int after = at;
            while (after < to && PdfConstants.IsWhitespace(buffer[after])) after++;
            if (after < to && PdfConstants.IsDigit(buffer[after])) return -1;
            return value;
        }

        /// <summary>The first <paramref name="token"/> at or after <paramref name="start"/> standing as a token of its own (white space or a delimiter on both sides), or -1.</summary>
        private static int IndexOfToken(byte[] buffer, int count, int start, byte[] token)
        {
            for (int i = start < 0 ? 0 : start; i + token.Length <= count; i++)
                if (IsTokenAt(buffer, count, i, token)) return i;
            return -1;
        }

        private static bool MatchesAt(byte[] buffer, int count, int at, byte[] token)
        {
            if (at < 0 || at + token.Length > count) return false;
            for (int i = 0; i < token.Length; i++)
                if (buffer[at + i] != token[i]) return false;
            return true;
        }

        /// <summary>Index of the next literal <c>obj</c> in <c>[start, count)</c>, or -1.</summary>
        private static int IndexOfKeyword(byte[] buffer, int count, int start)
        {
            for (int i = start; i + 2 < count; i++)
            {
                if (buffer[i] == (byte)'o' && buffer[i + 1] == (byte)'b' && buffer[i + 2] == (byte)'j')
                    return i;
            }
            return -1;
        }
    }
}
