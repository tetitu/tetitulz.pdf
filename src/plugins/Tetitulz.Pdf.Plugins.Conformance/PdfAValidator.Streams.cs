// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>How much of a stream object, from its header, is read to find its <c>stream</c> keyword.</summary>
        private const int StreamDictionaryWindowBytes = 16 * 1024;

        /// <summary>
        /// The stream rules, every stream object the cross-reference table places in the file. A76 — a
        /// stream's <c>/Length</c> is the number of bytes between the EOL after <c>stream</c> and the EOL
        /// before <c>endstream</c>; no <c>/F</c>, <c>/FFilter</c> or <c>/FDecodeParms</c>; and before
        /// PDF/A-4 <c>stream</c> is followed by CRLF or LF and <c>endstream</c> preceded by an EOL
        /// (ISO 19005-1 6.1.7, 19005-2/3 6.1.7.1, 19005-4 6.1.6.1; the validation rules of those numbers).
        /// </summary>
        /// <remarks>
        /// A stream whose dictionary runs past <see cref="StreamDictionaryWindowBytes"/> is not judged on
        /// its bytes, and one inside an object stream cannot be (§7.5.7 puts no stream there).
        /// </remarks>
        private static void CheckStreams(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            var file = doc?.BaseStream;
            var table = doc?.Parser?.CrossReferenceTable;
            if (table == null || file == null || !file.CanSeek || !file.CanRead) return;
            bool part4 = part == PdfAConformancePart.Part4;
            long length = file.Length;
            var problems = new Occurrences();

            foreach (var entry in table.AllEntries)
            {
                if (entry.Type != CrossReferenceEntry.EntryType.Normal || entry.Offset <= 0 || entry.Offset >= length) continue;
                if (!(doc.Parser.GetObject(entry.ObjectNumber) is PdfStream stream)) continue;
                string where = "object " + entry.ObjectNumber.ToString(CultureInfo.InvariantCulture);

                foreach (var key in new[] { PdfName.F, PdfName.FFilter, PdfName.FDecodeParms })
                    if (stream[key] != null) problems.Add($"{where} /{key.Value}");

                byte[] w = ReadAt(file, entry.Offset, (int)System.Math.Min(StreamDictionaryWindowBytes, length - entry.Offset));
                int i = DictionaryEnd(w, 0);
                while (i < w.Length && IsWhite(w[i])) i++;
                if (i + 6 > w.Length || IndexOf(w, "stream", i, i + 6) != i) continue;   // not found in the window: not judged
                i += 6;
                long dataStart;
                if (i + 1 < w.Length && w[i] == '\r' && w[i + 1] == '\n') dataStart = entry.Offset + i + 2;
                else if (i < w.Length && w[i] == '\n') dataStart = entry.Offset + i + 1;
                else
                {
                    if (!part4) problems.Add(where + ": stream not followed by CRLF or LF");
                    dataStart = entry.Offset + i + (i < w.Length && w[i] == '\r' ? 1 : 0);
                }

                if (!(doc.ResolveObject(stream[PdfName.Length]) is PdfNumber declared) || !declared.IsInteger) continue;
                long end = dataStart + declared.LongValue;
                byte[] after = ReadAt(file, end - 1, 12);   // the byte before the declared end, then what follows
                if (after.Length < 10) { problems.Add($"{where}: /Length {declared.LongValue.ToString(CultureInfo.InvariantCulture)} runs past the file"); continue; }
                int k = 1;
                if (after[k] == '\r' && k + 1 < after.Length && after[k + 1] == '\n') k += 2;
                else if (after[k] == '\r' || after[k] == '\n') k += 1;
                bool endstreamFollows = IndexOf(after, "endstream", k, after.Length) == k;
                if (endstreamFollows && k > 1) continue;                                  // data, one EOL, endstream
                if (endstreamFollows && after[0] != '\r' && after[0] != '\n')
                {
                    if (!part4) problems.Add(where + ": no EOL before endstream");     // the data ends at /Length
                    continue;
                }
                problems.Add($"{where}: /Length {declared.LongValue.ToString(CultureInfo.InvariantCulture)} does not end where the data does");
            }

            if (problems.Count > 0)
            {
                string clause = part == PdfAConformancePart.Part1 ? "ISO 19005-1 6.1.7; rules 6.1.7-1 to -3"
                    : part4 ? "ISO 19005-4 6.1.6.1; rules 6.1.6.1-1/-2" : $"ISO 19005-{(int)part} 6.1.7.1; rules 6.1.7.1-1 to -3";
                v.Add(new PdfAViolation("A76", $"A stream object is not written as the part requires: {problems} ({clause})."));
            }
        }
    }
}
