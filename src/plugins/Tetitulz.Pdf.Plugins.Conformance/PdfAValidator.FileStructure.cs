// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>How much of the file's end the trailer and end-of-file checks read.</summary>
        private const int FileTailBytesRead = 64 * 1024;

        /// <summary>How much around an object's header, or before the next object, the spacing check reads.</summary>
        private const int ObjectWindowBytes = 256;

        /// <summary>
        /// The file structure rules a parser reads past, from the file's own bytes (ISO 19005-1 6.1.3,
        /// 6.1.4, 6.1.8; ISO 19005-2/3 6.1.3, 6.1.4, 6.1.9; ISO 19005-4 6.1.3, 6.1.4, 6.1.8; the
        /// rules of those numbers). A42 — the trailer (the last, or a linearized file's first-page one)
        /// has a non-empty <c>/ID</c>. A43 — nothing but one EOL marker follows the last <c>%%EOF</c>.
        /// A44 — the <c>xref</c> keyword is followed by one EOL marker, and in PDF/A-1 a subsection
        /// header's two numbers by one space. A45 — an indirect object's header is its number, one
        /// white-space byte, its generation, one white-space byte and <c>obj</c>, preceded and followed
        /// by an EOL marker, and its <c>endobj</c> preceded and followed by one. A46 — PDF/A-4: no
        /// <c>/Info</c> unless the catalog has <c>/PieceInfo</c>, and an information dictionary holds
        /// only <c>/ModDate</c>.
        /// </summary>
        private static void CheckFileStructure(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            var stream = doc.BaseStream;
            if (stream == null || !stream.CanSeek || !stream.CanRead)
            {
                ReportPartial("The A42-A45 file-structure checks did not run: the document's stream cannot be read.",
                    "A42", "A43", "A44", "A45");
                return;
            }
            string iso = part == PdfAConformancePart.Part1 ? "ISO 19005-1" : $"ISO 19005-{(int)part}";
            long length = stream.Length;
            byte[] tail = ReadAt(stream, System.Math.Max(0, length - FileTailBytesRead), (int)System.Math.Min(length, FileTailBytesRead));
            byte[] head = ReadAt(stream, 0, (int)System.Math.Min(length, FileTailBytesRead));

            // A43 — after the last %%EOF, at most one EOL marker.
            int eof = LastIndexOf(tail, "%%EOF", tail.Length);
            if (eof >= 0)
            {
                int after = eof + 5, rest = tail.Length - after;
                bool oneEol = rest == 0 || (rest == 1 && (tail[after] == '\r' || tail[after] == '\n'))
                              || (rest == 2 && tail[after] == '\r' && tail[after + 1] == '\n');
                if (!oneEol)
                    v.Add(new PdfAViolation("A43",
                        $"{rest.ToString(CultureInfo.InvariantCulture)} bytes follow the last %%EOF, more than one EOL marker " +
                        $"({iso} 6.1.3; rule 6.1.3-3)."));
            }

            // A42 — the trailer's /ID: a linearized file's first-page trailer, else the last.
            bool linearized = IndexOf(head, "/Linearized", 0, System.Math.Min(head.Length, 1024)) >= 0;
            byte[] trailerText = linearized ? FirstTrailer(head) : LastTrailer(stream, tail, doc.Parser?.StartXrefOffset ?? -1);
            if (trailerText != null && !HasNonEmptyId(trailerText))
                v.Add(new PdfAViolation("A42",
                    $"The {(linearized ? "first-page" : "last")} trailer has no /ID, or an empty one " +
                    $"({iso} 6.1.3; rule 6.1.3-1)."));

            // A44 — every classic cross-reference section's keyword and subsection headers.
            var xrefProblems = new Occurrences();
            var sections = new List<long>();
            CheckXrefSections(stream, length, doc.Parser?.StartXrefOffset ?? -1, part == PdfAConformancePart.Part1, xrefProblems, sections);
            if (xrefProblems.Count > 0)
                v.Add(new PdfAViolation("A44",
                    $"A cross-reference section is not formatted as the part requires: {xrefProblems} ({iso} 6.1.4; rule 6.1.4-" +
                    (part == PdfAConformancePart.Part1 ? "1/-2" : part == PdfAConformancePart.Part4 ? "1" : "2") + ")."));

            // A45 — each indirect object the table locates in the file.
            var spacing = new Occurrences();
            CheckObjectSpacing(stream, length, doc.Parser?.CrossReferenceTable, sections, spacing);
            if (spacing.Count > 0)
            {
                string clause = part == PdfAConformancePart.Part1 || part == PdfAConformancePart.Part4 ? "6.1.8" : "6.1.9";
                v.Add(new PdfAViolation("A45",
                    $"An indirect object's header or endobj is not spaced as the part requires: {spacing} " +
                    $"({iso} {clause}; rule {clause}-1)."));
            }

            // A46 — PDF/A-4's information dictionary.
            if (part == PdfAConformancePart.Part4 && doc.Trailer?[PdfName.Info] != null)
            {
                bool pieceInfo = doc.Catalog?.Dictionary?[PdfName.PieceInfo] != null;
                if (!pieceInfo)
                    v.Add(new PdfAViolation("A46",
                        "The trailer has /Info, and the catalog no /PieceInfo (ISO 19005-4 6.1.3; rule 6.1.3-4)."));
                if (doc.ResolveObject(doc.Trailer[PdfName.Info]) is PdfDictionary info
                    && (info.Count != 1 || info[PdfName.ModDate] == null))
                    v.Add(new PdfAViolation("A46",
                        "The information dictionary holds entries other than /ModDate (ISO 19005-4 6.1.3; rule 6.1.3-5)."));
            }
        }

        /// <summary>The last trailer's text: from the last <c>trailer</c> keyword, or the cross-reference stream's dictionary.</summary>
        private static byte[] LastTrailer(Stream stream, byte[] tail, long startXref)
        {
            int at = LastIndexOf(tail, "trailer", tail.Length);
            if (at >= 0) return Slice(tail, at, DictionaryEnd(tail, at) - at);
            if (startXref < 0) return null;
            byte[] section = ReadAt(stream, startXref, ObjectWindowBytes * 16);
            int streamAt = IndexOf(section, "stream", 0, section.Length);
            return streamAt < 0 ? null : Slice(section, 0, streamAt);
        }

        /// <summary>A linearized file's first-page trailer: the first <c>trailer</c>, or the first cross-reference stream's dictionary.</summary>
        private static byte[] FirstTrailer(byte[] head)
        {
            int at = IndexOf(head, "trailer", 0, head.Length);
            if (at >= 0) return Slice(head, at, DictionaryEnd(head, at) - at);
            int xrefStream = IndexOf(head, "/XRef", 0, head.Length);
            if (xrefStream < 0) return null;
            int start = LastIndexOf(head, "<<", xrefStream);
            int streamAt = IndexOf(head, "stream", xrefStream, head.Length);
            return start < 0 || streamAt < 0 ? null : Slice(head, start, streamAt - start);
        }

        /// <summary>
        /// Where the dictionary that begins at the first <c>&lt;&lt;</c> after <paramref name="from"/>
        /// ends — its <c>&gt;&gt;</c> at depth zero, strings skipped — or the buffer's end.
        /// </summary>
        private static int DictionaryEnd(byte[] text, int from)
        {
            int i = IndexOf(text, "<<", from, text.Length);
            if (i < 0) return text.Length;
            int depth = 0;
            while (i + 1 < text.Length)
            {
                if (text[i] == '(')
                {
                    int nesting = 0;
                    for (; i < text.Length; i++)
                    {
                        if (text[i] == '\\') { i++; continue; }
                        if (text[i] == '(') nesting++;
                        else if (text[i] == ')' && --nesting == 0) break;
                    }
                    i++;
                    continue;
                }
                if (text[i] == '<' && text[i + 1] == '<') { depth++; i += 2; continue; }
                if (text[i] == '>' && text[i + 1] == '>')
                {
                    i += 2;
                    if (--depth == 0) return i;
                    continue;
                }
                i++;
            }
            return text.Length;
        }

        /// <summary>
        /// Whether a trailer's text has an <c>/ID</c> holding a non-empty string — an indirect
        /// reference is taken as present, since its target is not read here.
        /// </summary>
        private static bool HasNonEmptyId(byte[] text)
        {
            int at = 0;
            while ((at = IndexOf(text, "/ID", at, text.Length)) >= 0)
            {
                int i = at + 3;
                at = i;
                if (i < text.Length && IsRegular(text[i])) continue;   // /IDTree, not /ID
                while (i < text.Length && IsWhite(text[i])) i++;
                if (i >= text.Length) return false;
                if (text[i] != '[') return IsAsciiDigit(text[i]);      // "12 0 R"
                i++;
                for (int k = 0; k < 2 && i < text.Length; k++)
                {
                    while (i < text.Length && IsWhite(text[i])) i++;
                    if (i >= text.Length) break;
                    byte open = text[i];
                    byte close = open == '<' ? (byte)'>' : open == '(' ? (byte)')' : (byte)0;
                    if (close == 0) break;
                    int content = i + 1, j = content;
                    while (j < text.Length && text[j] != close) j++;
                    for (int c = content; c < j; c++)
                        if (open == '(' || !IsWhite(text[c])) return true;
                    i = j + 1;
                }
                return false;
            }
            return false;
        }

        /// <summary>The cross-reference sections a file's chain can hold before the walk stops.</summary>
        private const int MaxXrefSections = 1024;

        /// <summary>
        /// Each section of the chain from <c>startxref</c> through every trailer's <c>/Prev</c>: a
        /// classic one's <c>xref</c> keyword followed by exactly one EOL marker and a subsection
        /// header, and in PDF/A-1 each header's two numbers separated by one space; a
        /// cross-reference stream is followed for its <c>/Prev</c> only.
        /// </summary>
        private static void CheckXrefSections(Stream stream, long length, long startXref, bool part1, Occurrences problems,
            List<long> sections)
        {
            var visited = new HashSet<long>();
            long at = startXref;
            while (at >= 0 && at < length && visited.Add(at))
            {
                sections.Add(at);
                if (visited.Count > MaxXrefSections)
                {
                    ReportPartial($"The A44 cross-reference check stopped after {MaxXrefSections} sections.", "A44");
                    return;
                }
                byte[] w = ReadAt(stream, at, 4);
                long dictionaryAt;
                if (w.Length == 4 && w[0] == 'x' && w[1] == 'r' && w[2] == 'e' && w[3] == 'f')
                {
                    CheckOneXref(stream, at, part1, problems);
                    dictionaryAt = FindForward(stream, at, length, "trailer");
                }
                else
                {
                    dictionaryAt = at;   // a cross-reference stream: its dictionary
                }
                if (dictionaryAt < 0) return;
                at = PrevOf(ReadAt(stream, dictionaryAt, ObjectWindowBytes * 16));
            }
        }

        /// <summary>A trailer or stream dictionary's <c>/Prev</c> offset, or -1.</summary>
        private static long PrevOf(byte[] text)
        {
            int limit = IndexOf(text, "stream", 0, text.Length);
            int stop = limit < 0 ? text.Length : limit;
            int at = IndexOf(text, "/Prev", 0, stop);
            if (at < 0 || (at + 5 < stop && IsRegular(text[at + 5]))) return -1;
            int i = at + 5;
            while (i < stop && IsWhite(text[i])) i++;
            long value = 0;
            int digits = i;
            while (i < stop && IsAsciiDigit(text[i]) && i - digits < 18) value = value * 10 + (text[i++] - '0');
            return i == digits ? -1 : value;
        }

        /// <summary>The offset of <paramref name="word"/> at or after <paramref name="from"/>, or -1.</summary>
        private static long FindForward(Stream stream, long from, long length, string word)
        {
            const int chunk = 1 << 16;
            for (long position = from; position < length; position += chunk)
            {
                byte[] w = ReadAt(stream, position, (int)System.Math.Min(chunk + word.Length, length - position));
                int found = IndexOf(w, word, 0, w.Length);
                if (found >= 0) return position + found;
            }
            return -1;
        }

        private static void CheckOneXref(Stream stream, long at, bool part1, Occurrences problems)
        {
            byte[] w = ReadAt(stream, at, ObjectWindowBytes);
            string where = "xref at byte " + at.ToString(CultureInfo.InvariantCulture);
            int i = 4;
            int eolStart = i;
            if (i < w.Length && w[i] == '\r') i++;
            if (i < w.Length && w[i] == '\n') i++;
            if (i == eolStart || i >= w.Length || !IsAsciiDigit(w[i]))
            {
                problems.Add(where + ": the keyword and the subsection header are not separated by one EOL marker");
                return;
            }
            if (!part1) return;

            long headerAt = at + i;
            for (int guard = 0; guard < MaxXrefSections; guard++)
            {
                byte[] line = ReadAt(stream, headerAt, 64);
                int p = 0;
                while (p < line.Length && IsAsciiDigit(line[p])) p++;
                if (p == 0) return;                                   // "trailer": the section ended
                int spaces = 0;
                while (p + spaces < line.Length && line[p + spaces] == ' ') spaces++;
                int countStart = p + spaces, q = countStart;
                long count = 0;
                while (q < line.Length && IsAsciiDigit(line[q]) && q - countStart < 10) { count = count * 10 + (line[q] - '0'); q++; }
                if (spaces != 1 || q == countStart)
                {
                    problems.Add(where + ": a subsection header's numbers are not separated by one space");
                    return;
                }
                while (q < line.Length && (line[q] == ' ' || line[q] == '\r' || line[q] == '\n')) q++;
                headerAt += q + count * 20;
            }
        }

        /// <summary>
        /// Each in-use object the table places in the file, in offset order: its header read at its
        /// offset, its <c>endobj</c> the last one before the next object's offset.
        /// </summary>
        private static void CheckObjectSpacing(Stream stream, long length, CrossReferenceTable table, List<long> sections,
            Occurrences problems)
        {
            if (table == null) return;
            var offsets = new List<(long Offset, int Number)>();
            foreach (var entry in table.AllEntries)
                if (entry.Type == CrossReferenceEntry.EntryType.Normal && entry.Offset > 0 && entry.Offset < length)
                    offsets.Add((entry.Offset, entry.ObjectNumber));
            offsets.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var boundaries = new List<long>(sections);
            boundaries.Sort();

            for (int k = 0; k < offsets.Count; k++)
            {
                long offset = offsets[k].Offset;
                string where = "object " + offsets[k].Number.ToString(CultureInfo.InvariantCulture);
                byte[] w = ReadAt(stream, offset - 1, ObjectWindowBytes);
                if (w.Length < 8) continue;
                // A table offset a byte or two early, at the EOL before the header, is read past as a
                // parser reads past it: the header is judged where it is.
                int i = 1;
                while (i < 4 && i < w.Length && IsWhite(w[i])) i++;
                if (w[i - 1] != '\r' && w[i - 1] != '\n') { problems.Add(where + ": no EOL marker before its number"); continue; }
                int digits = i;
                while (i < w.Length && IsAsciiDigit(w[i])) i++;
                if (i == digits) continue;                             // not a header: the table is the parser's to judge
                int white = i;
                while (i < w.Length && IsWhite(w[i])) i++;
                if (i - white != 1) { problems.Add(where + ": not one white-space byte after its number"); continue; }
                digits = i;
                while (i < w.Length && IsAsciiDigit(w[i])) i++;
                if (i == digits) continue;
                white = i;
                while (i < w.Length && IsWhite(w[i])) i++;
                if (i - white != 1) { problems.Add(where + ": not one white-space byte before obj"); continue; }
                if (i + 3 > w.Length || w[i] != 'o' || w[i + 1] != 'b' || w[i + 2] != 'j') continue;
                i += 3;
                if (i >= w.Length || (w[i] != '\r' && w[i] != '\n')) { problems.Add(where + ": no EOL marker after obj"); continue; }

                // The region up to the next object or cross-reference section (or the file's end) ends
                // with this object's endobj.
                long regionEnd = k + 1 < offsets.Count ? offsets[k + 1].Offset : length;
                foreach (long section in boundaries)
                    if (section > offset) { regionEnd = System.Math.Min(regionEnd, section); break; }
                long windowStart = System.Math.Max(offset, regionEnd - ObjectWindowBytes);
                byte[] tail = ReadAt(stream, windowStart, (int)(regionEnd - windowStart));
                int end = LastIndexOf(tail, "endobj", tail.Length);
                if (end < 0) continue;                                  // beyond the window: not judged
                if (end == 0 || (tail[end - 1] != '\r' && tail[end - 1] != '\n'))
                {
                    problems.Add(where + ": no EOL marker before endobj");
                    continue;
                }
                int afterEnd = end + 6;
                if (afterEnd < tail.Length && tail[afterEnd] != '\r' && tail[afterEnd] != '\n')
                    problems.Add(where + ": no EOL marker after endobj");
            }
        }

        private static bool IsWhite(byte b) => b == 0x20 || b == 0x0A || b == 0x0D || b == 0x09 || b == 0x0C || b == 0x00;

        private static bool IsRegular(byte b)
            => !IsWhite(b) && b != '(' && b != ')' && b != '<' && b != '>' && b != '[' && b != ']'
               && b != '{' && b != '}' && b != '/' && b != '%';

        private static byte[] ReadAt(Stream stream, long offset, int count)
        {
            if (offset < 0) { count += (int)offset; offset = 0; }
            if (count <= 0) return new byte[0];
            var buffer = new byte[count];
            int read = ReadInto(stream, offset, buffer, count);
            return read == count ? buffer : Slice(buffer, 0, read);
        }

        private static int ReadInto(Stream stream, long offset, byte[] buffer, int count)
        {
            long saved = stream.Position;
            try
            {
                stream.Position = offset;
                return Core.IO.StreamBytes.Fill(stream, buffer, 0, count);
            }
            finally
            {
                stream.Position = saved;
            }
        }

        private static byte[] Slice(byte[] bytes, int start, int count)
        {
            var result = new byte[System.Math.Max(0, count)];
            System.Array.Copy(bytes, start, result, 0, result.Length);
            return result;
        }

        private static int IndexOf(byte[] bytes, string text, int from, int to)
        {
            for (int i = System.Math.Max(0, from); i + text.Length <= to; i++)
            {
                int k = 0;
                while (k < text.Length && bytes[i + k] == text[k]) k++;
                if (k == text.Length) return i;
            }
            return -1;
        }

        private static int LastIndexOf(byte[] bytes, string text, int before)
        {
            for (int i = System.Math.Min(before, bytes.Length) - text.Length; i >= 0; i--)
            {
                int k = 0;
                while (k < text.Length && bytes[i + k] == text[k]) k++;
                if (k == text.Length) return i;
            }
            return -1;
        }
    }
}
