// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Finds indirect objects in raw bytes without a cross-reference table: every <c>N G obj</c>
    /// header, and where each object's bytes end.
    /// </summary>
    /// <remarks>
    /// <para><b>Two readers need this, and each had its own copy.</b> <see cref="PdfRepair"/> exists
    /// for files whose cross-reference data is wrong, and the FDF reader does not trust an FDF file's,
    /// which hand-built and server-generated files routinely get wrong. The FDF copy spelled the
    /// header rule a third way — it took <c>x12 0 obj</c> for object 12 — and both ended an object at
    /// the first <c>endobj</c> in the bytes.</para>
    /// <para><b>Header recognition is <see cref="ObjectHeaderScanner"/>'s</b>, the parser's own
    /// recovery rule. <b>An object's end is read from its tokens</b>: see
    /// <see cref="LocateObjects"/>.</para>
    /// </remarks>
    internal static class RawObjectScanner
    {
        /// <summary>The highest object number an object may have; a caller building a table has a row for each below it.</summary>
        internal const int MaxObjectNumber = 1_000_000;

        /// <summary>
        /// How many object headers the scan records. The object-number range already bounds a table
        /// built from them; this bounds the list the headers are collected into, and a file that
        /// reaches it is reported rather than silently cut short.
        /// </summary>
        private const int MaxRecordedHeaders = 1_000_000;

        /// <summary>
        /// How many header-shaped runs of text inside one object's strings and comments are stepped
        /// over before its search for <c>endobj</c> gives up. Each object's tokens are read no
        /// further than the header after that many, which is what keeps the work linear in the
        /// file: a file of unclosed strings would otherwise be read to its end once per header.
        /// </summary>
        private const int MaxHeadersInsideAnObject = 4;

        private static readonly byte[] Endobj = Encoding.ASCII.GetBytes("endobj");
        private static readonly byte[] Endstream = Encoding.ASCII.GetBytes("endstream");

        /// <summary>
        /// Every object the file's headers begin, in file order, with what was skipped reported to
        /// <paramref name="errors"/>.
        /// </summary>
        /// <param name="raw">The file.</param>
        /// <param name="parser">A parser over the same bytes, to read an indirect <c>/Length</c>; may be null.</param>
        /// <param name="errors">Where skipped and repaired objects are reported.</param>
        /// <param name="headersFound">How many headers the scan found, objects or not.</param>
        internal static List<RawObject> Scan(byte[] raw, PdfParser parser, List<string> errors, out int headersFound)
        {
            List<ObjectHeaderMatch> headers = ScanForObjects(raw, errors);
            headersFound = headers.Count;
            return LocateObjects(raw, headers, parser, errors);
        }

        /// <summary>
        /// Locates every <c>N G obj</c> header, through the one shared recogniser, in file order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The repair tool shares the parser's header recognition</b>, never a stricter copy of
        /// its own — a literal <c>(byte)' '</c> before the keyword, white space (never a delimiter)
        /// after it, a generation digit immediately at <c>i - 1</c>. A stricter copy would drop three legal spellings
        /// that <c>PdfDocument.Open</c> recovers — <c>12 0\nobj</c>,
        /// <c>12 0 obj&lt;&lt;/Type/Page&gt;&gt;</c> and <c>12 0  obj</c>.
        /// </para>
        /// <para>
        /// <b>Every header is kept, not only the last per number</b> — including one whose number is
        /// out of range, which is still where another object begins. Whether a header is an object
        /// at all depends on the objects around it: kept per number, a line of stream data reading
        /// <c>3 0 obj</c> would replace the real object 3.
        /// </para>
        /// <para>
        /// <b>Repair does not stop at a fixed object count without saying so</b>, which a large
        /// document passes: whatever came after would be missing from the "repaired" file.
        /// </para>
        /// </remarks>
        private static List<ObjectHeaderMatch> ScanForObjects(byte[] raw, List<string> errors)
        {
            var headers = new List<ObjectHeaderMatch>();
            int scanPos = 0;
            while (ObjectHeaderScanner.TryFindNext(raw, raw.Length, scanPos, out var header))
            {
                scanPos = header.KeywordEnd;

                if (headers.Count >= MaxRecordedHeaders)
                {
                    errors.Add($"Recovery stopped after {MaxRecordedHeaders} object headers; objects " +
                               $"from byte {header.Offset} on were not recovered.");
                    break;
                }

                headers.Add(header);
            }
            return headers;
        }

        /// <summary>
        /// The objects the headers begin, in file order, each with the bytes it occupies.
        /// </summary>
        /// <remarks>
        /// <para><b>An object's end is not the first <c>endobj</c> after its header</b> by byte
        /// search: stream data is arbitrary bytes and a string is any text, so an object holding the
        /// word would be cut there — a content stream keeping only what came before it, with a
        /// <c>/Length</c> that then ran on into the next object — and a <c>3 0 obj</c> in either
        /// would become an object 3 that could replace the real one. The end is found by reading the
        /// object's tokens, and a stream's data is stepped over by its <c>/Length</c>; headers
        /// inside an accepted object are not objects.</para>
        /// <para><b>A byte search would also be quadratic.</b> Headers that shared one distant
        /// <c>endobj</c> would each copy everything up to it, so two thousand of them before 100 KB
        /// of padding would copy the padding two thousand times. An object ends by the next header
        /// at the latest.</para>
        /// </remarks>
        private static List<RawObject> LocateObjects(
            byte[] raw, List<ObjectHeaderMatch> headers, PdfParser parser, List<string> errors)
        {
            var objects = new List<RawObject>(headers.Count);
            List<long> endstreams = FindAll(raw, Endstream);
            long coveredUntil = 0;
            var lost = new HeaderTally();
            var closed = new HeaderTally();
            var outOfRange = new HeaderTally();

            for (int h = 0; h < headers.Count; h++)
            {
                ObjectHeaderMatch header = headers[h];
                if (header.Offset < coveredUntil) continue;

                if (header.ObjectNumber < 1 || header.ObjectNumber > MaxObjectNumber)
                {
                    outOfRange.Add(header);
                    continue;
                }

                if (!TryLocate(raw, headers, h, endstreams, parser, out RawObject obj))
                {
                    lost.Add(header);
                    continue;
                }

                if (obj.NeedsEndobj) closed.Add(header);
                objects.Add(obj);
                coveredUntil = obj.End;
            }

            if (lost.Count > 0)
                errors.Add($"{lost.Count} object(s) were not recovered: no endobj, no endstream for a stream, " +
                           $"and no whole value before what follows ({lost.Describe()}).");
            if (closed.Count > 0)
                errors.Add($"{closed.Count} object(s) had no endobj and were closed where their value " +
                           $"ends ({closed.Describe()}).");
            if (outOfRange.Count > 0)
                errors.Add($"{outOfRange.Count} object header(s) numbered outside 1..{MaxObjectNumber} " +
                           $"were skipped ({outOfRange.Describe()}).");

            return objects;
        }

        /// <summary>
        /// Reads the tokens of the object <paramref name="index"/> begins until its <c>endobj</c>,
        /// its <c>stream</c> keyword, or where something else begins.
        /// </summary>
        /// <remarks>
        /// <para>A header the tokens step over lies inside a string or a comment. A header a token
        /// starts at is another object, and <c>xref</c>, <c>trailer</c> or <c>startxref</c> is the end
        /// of the objects, so reaching either — or the end of the file — means this object has no
        /// <c>endobj</c>. It is then closed after its last token, but only when that token completes a
        /// value made of nothing but objects. Reaching the edge of the search closes nothing: the
        /// header there may be inside a string.</para>
        /// </remarks>
        private static bool TryLocate(
            byte[] raw, List<ObjectHeaderMatch> headers, int index, List<long> endstreams, PdfParser parser,
            out RawObject located)
        {
            located = null;
            ObjectHeaderMatch header = headers[index];
            int generation = GenerationOf(raw, header);

            int searchEndIndex = index + 1 + MaxHeadersInsideAnObject;
            long searchEnd = searchEndIndex < headers.Count ? headers[searchEndIndex].Offset : raw.Length;
            var tokens = new PdfTokenizer(new MemoryStream(raw, 0, (int)searchEnd, writable: false))
            {
                Position = header.KeywordEnd,
            };

            int next = index + 1;       // the first header not yet stepped over
            int depth = 0;              // open arrays and dictionaries
            long lastTokenEnd = -1;
            bool onlyObjects = true;    // no keyword but R, and nothing closed that was not open

            while (true)
            {
                PdfToken token;
                try
                {
                    token = tokens.NextToken();
                }
                catch (PdfException)
                {
                    // A string escape cut off by the end of the search: not an object this search can end.
                    return false;
                }

                bool atAnotherObject = false;
                while (next < headers.Count && headers[next].Offset < tokens.Position)
                {
                    if (headers[next].Offset == token.Position) { atAnotherObject = true; break; }
                    next++;
                }

                bool objectsEnd = atAnotherObject ||
                                  (token.Type == PdfTokenType.Eof && searchEnd == raw.Length) ||
                                  (token.Type == PdfTokenType.Keyword && IsFileStructureKeyword(token.Value));
                if (objectsEnd)
                {
                    if (lastTokenEnd < 0 || depth != 0 || !onlyObjects) return false;

                    located = new RawObject(header, generation, dictionaryEnd: lastTokenEnd, end: lastTokenEnd,
                                            isStream: false, needsEndobj: true, streamDictionary: null);
                    return true;
                }

                switch (token.Type)
                {
                    case PdfTokenType.Eof:
                        return false;

                    case PdfTokenType.Keyword when token.Value == "endobj":
                        located = new RawObject(header, generation, dictionaryEnd: token.Position,
                                                end: PastEndOfLine(raw, tokens.Position),
                                                isStream: false, needsEndobj: false, streamDictionary: null);
                        return true;

                    case PdfTokenType.Keyword when token.Value == "stream":
                        return TryLocateStream(raw, header, generation, token.Position, tokens.Position,
                                               endstreams, parser, out located);

                    case PdfTokenType.Keyword:
                        if (token.Value != "R") onlyObjects = false;
                        break;

                    case PdfTokenType.DictionaryStart:
                    case PdfTokenType.ArrayStart:
                        depth++;
                        break;

                    case PdfTokenType.DictionaryEnd:
                    case PdfTokenType.ArrayEnd:
                        if (depth > 0) depth--;
                        else onlyObjects = false;
                        break;
                }

                lastTokenEnd = tokens.Position;
            }
        }

        /// <summary>The keywords that follow the last object of a file section (§7.5.4, §7.5.5).</summary>
        private static bool IsFileStructureKeyword(string keyword)
            => keyword == "xref" || keyword == "trailer" || keyword == "startxref";

        /// <summary>
        /// Finds where a stream object ends, from its <c>stream</c> keyword: past its data, its
        /// <c>endstream</c> and, when present, its <c>endobj</c>.
        /// </summary>
        /// <remarks>
        /// The data is stepped over by <c>/Length</c> when an <c>endstream</c> stands where it says —
        /// an uncompressed embedded PDF holds <c>endstream</c> keywords of its own — and otherwise ends
        /// at the first <c>endstream</c> after it, which is how the parser reads a stream whose length
        /// is wrong. An indirect <c>/Length</c> is read through the parser.
        /// </remarks>
        private static bool TryLocateStream(
            byte[] raw, ObjectHeaderMatch header, int generation, long streamKeyword, long afterKeyword,
            List<long> endstreams, PdfParser parser, out RawObject located)
        {
            located = null;
            PdfDictionary dictionary = ReadDictionary(raw, header.KeywordEnd, streamKeyword);

            // §7.3.8.1: the keyword is followed by CRLF or LF; a lone CR is tolerated.
            long dataStart = afterKeyword;
            if (dataStart < raw.Length && raw[dataStart] == (byte)'\r') dataStart++;
            if (dataStart < raw.Length && raw[dataStart] == (byte)'\n') dataStart++;

            // A length that is negative, or that runs past the end of the file, says nothing — and
            // added to the data's offset, a large enough one wraps around to a negative index.
            long endstream = -1;
            long length = DeclaredLength(dictionary, parser);
            if (length >= 0 && length <= raw.Length - dataStart)
            {
                long at = dataStart + length;
                while (at < raw.Length && PdfConstants.IsWhitespace(raw[at])) at++;
                if (MatchesAt(raw, at, Endstream)) endstream = at;
            }
            if (endstream < 0) endstream = FirstAtOrAfter(endstreams, dataStart);
            if (endstream < 0) return false;

            long afterEndstream = endstream + Endstream.Length;
            var tail = new PdfTokenizer(new MemoryStream(raw, writable: false)) { Position = afterEndstream };
            tail.SkipWhitespaceAndComments();
            bool terminated = MatchesAt(raw, tail.Position, Endobj);

            located = new RawObject(header, generation, dictionaryEnd: streamKeyword,
                                    end: terminated ? PastEndOfLine(raw, tail.Position + Endobj.Length) : afterEndstream,
                                    isStream: true, needsEndobj: !terminated, streamDictionary: dictionary);
            return true;
        }

        /// <summary>A stream's <c>/Length</c>, following an indirect one through the parser; -1 when unknown.</summary>
        private static long DeclaredLength(PdfDictionary dictionary, PdfParser parser)
        {
            PdfObject length = dictionary?[PdfName.Length];
            if (length is PdfReference reference && parser != null)
                length = parser.GetObject(reference.ReferenceObjectNumber);
            return length is PdfNumber number && number.IsInteger ? number.LongValue : -1;
        }

        /// <summary>
        /// The generation a header declares. Unparseable falls back to 0 rather than refusing the
        /// object — repair is best-effort, and since the header is copied verbatim a recovered
        /// object with a defaulted generation still beats a dropped one.
        /// </summary>
        private static int GenerationOf(byte[] raw, ObjectHeaderMatch header)
            => header.GenerationLength <= 5
               && ObjectHeaderScanner.TryParseDigits(
                   raw, header.GenerationStart, header.GenerationLength, out int gen)
               && gen >= 0 && gen <= PdfXrefFormat.MaxGenerationNumber
                ? gen
                : 0;

        /// <summary>The dictionary written in <c>[start, end)</c>, or <see langword="null"/> when that is not one.</summary>
        internal static PdfDictionary ReadDictionary(byte[] raw, long start, long end)
        {
            if (end <= start) return null;
            try
            {
                var parser = new PdfParser(new MemoryStream(raw, (int)start, (int)(end - start), writable: false));
                return parser.ParseDirectObject() as PdfDictionary;
            }
            catch (PdfException)
            {
                return null;
            }
        }

        // ----------------------------------------------------------------
        //  Bytes
        // ----------------------------------------------------------------

        /// <summary>Past the end-of-line byte that follows <paramref name="position"/>, if one does.</summary>
        private static long PastEndOfLine(byte[] raw, long position)
            => position < raw.Length && (raw[position] == (byte)'\n' || raw[position] == (byte)'\r')
                ? position + 1
                : position;

        /// <summary>Whether the bytes at <paramref name="at"/> are <paramref name="pattern"/>.</summary>
        internal static bool MatchesAt(byte[] raw, long at, byte[] pattern)
        {
            if (at + pattern.Length > raw.Length) return false;
            for (int j = 0; j < pattern.Length; j++)
                if (raw[at + j] != pattern[j]) return false;
            return true;
        }

        /// <summary>The first position of <paramref name="pattern"/> in <c>[start, end)</c>, or -1.</summary>
        internal static long IndexOf(byte[] raw, byte[] pattern, long start, long end)
        {
            for (long i = start; i + pattern.Length <= end; i++)
            {
                int j = 0;
                while (j < pattern.Length && raw[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        /// <summary>
        /// Every position of <paramref name="pattern"/>, in one pass, so that finding the next one
        /// from any offset costs a binary search rather than a scan.
        /// </summary>
        private static List<long> FindAll(byte[] raw, byte[] pattern)
        {
            var positions = new List<long>();
            for (long i = IndexOf(raw, pattern, 0, raw.Length); i >= 0; i = IndexOf(raw, pattern, i + 1, raw.Length))
                positions.Add(i);
            return positions;
        }

        private static long FirstAtOrAfter(List<long> positions, long position)
        {
            int lo = 0, hi = positions.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (positions[mid] < position) lo = mid + 1;
                else hi = mid;
            }
            return lo < positions.Count ? positions[lo] : -1;
        }

        /// <summary>How many headers one outcome applied to, and the first of them, for one error line.</summary>
        private sealed class HeaderTally
        {
            private ObjectHeaderMatch _first;

            internal int Count { get; private set; }

            internal void Add(ObjectHeaderMatch header)
            {
                if (Count++ == 0) _first = header;
            }

            internal string Describe() => $"the first: object {_first.ObjectNumber} at byte {_first.Offset}";
        }
    }

    /// <summary>One object <see cref="RawObjectScanner"/> located: its header's numbers and the bytes it occupies.</summary>
    internal sealed class RawObject
    {
        internal RawObject(ObjectHeaderMatch header, int generation, long dictionaryEnd, long end,
                           bool isStream, bool needsEndobj, PdfDictionary streamDictionary)
        {
            ObjectNumber = header.ObjectNumber;
            Generation = generation;
            Start = header.Offset;
            BodyStart = header.KeywordEnd;
            DictionaryEnd = dictionaryEnd;
            End = end;
            IsStream = isStream;
            NeedsEndobj = needsEndobj;
            StreamDictionary = streamDictionary;
        }

        internal int ObjectNumber { get; }
        internal int Generation { get; }

        /// <summary>Offset of the header's first digit.</summary>
        internal long Start { get; }

        /// <summary>Just past the <c>obj</c> keyword.</summary>
        internal long BodyStart { get; }

        /// <summary>Where the object's value ends: at <c>stream</c>, at <c>endobj</c>, or at its last token.</summary>
        internal long DictionaryEnd { get; }

        /// <summary>One past the object's last byte.</summary>
        internal long End { get; }

        internal bool IsStream { get; }

        /// <summary>The object has no <c>endobj</c>: a copy has to be closed with one.</summary>
        internal bool NeedsEndobj { get; }

        /// <summary>A stream's dictionary; <see langword="null"/> for any other object.</summary>
        internal PdfDictionary StreamDictionary { get; }

        /// <summary>Whether this is a stream whose dictionary declares <c>/Type</c> <paramref name="type"/>.</summary>
        internal bool Declares(PdfName type) => StreamDictionary?.GetName(PdfName.Type) == type;
    }
}
