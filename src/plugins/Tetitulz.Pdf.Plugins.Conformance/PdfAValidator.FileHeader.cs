// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// The file header (ISO 19005-1 6.1.2, 19005-2/3 6.1.2, 19005-4 6.1.2), read from the file's
        /// own first bytes — the parsed version cannot see it: <c>%PDF-2.01</c> parses as 2.1, and a
        /// header the parser found after leading junk parses like one at byte 0.
        /// </summary>
        /// <remarks>
        /// <para><b>The rules are the published validation profiles'</b> (the PDF/A-1b, -2b and -4 profiles' 6.1.2-1
        /// and -2), each part's corpus agreeing: the header begins at byte 0;
        /// its line is <c>/%PDF-\d\.\d/</c> for PDF/A-1 (unanchored at the end),
        /// <c>/^%PDF-1\.[0-7]$/</c> for PDF/A-2 and -3 and <c>/^%PDF-2\.[0-9]$/</c> for PDF/A-4
        /// (a line, so a trailing space fails); then one EOL marker (CR, LF or CR LF) and at once a
        /// comment, <c>%</c> and four bytes each above 127.</para>
        /// <para>A4-01 stays PDF/A-4's header code; A32 is the other parts'; A33 is the comment,
        /// every part.</para>
        /// </remarks>
        private static void CheckFileHeader(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            byte[] head = ReadFileStart(doc.BaseStream, HeaderBytesRead);
            if (head == null)
            {
                ReportPartial("The A32/A33 file-header check did not run: the document's stream " +
                              "cannot be read from its start.", "A32", "A33", "A4-01");
                return;
            }

            string code = part == PdfAConformancePart.Part4 ? "A4-01" : "A32";
            string iso = part == PdfAConformancePart.Part1 ? "ISO 19005-1"
                : part == PdfAConformancePart.Part2 ? "ISO 19005-2"
                : part == PdfAConformancePart.Part3 ? "ISO 19005-3"
                : "ISO 19005-4";
            string shape = part == PdfAConformancePart.Part1 ? "%PDF-d.d"
                : part == PdfAConformancePart.Part4 ? "%PDF-2.n, n from 0 to 9, alone on its line"
                : "%PDF-1.n, n from 0 to 7, alone on its line";

            int lineEnd = 0;
            while (lineEnd < head.Length && head[lineEnd] != '\r' && head[lineEnd] != '\n') lineEnd++;

            if (!StartsWithMarker(head))
            {
                v.Add(new PdfAViolation(code,
                    $"The file header does not begin at byte 0 with %PDF- ({iso} 6.1.2; rule 6.1.2-1)."));
            }
            else if (!HeaderLineConforms(head, lineEnd, part))
            {
                v.Add(new PdfAViolation(code,
                    $"The file header is \"{HeaderText(head, lineEnd)}\", not {shape} ({iso} 6.1.2; " +
                    "rule 6.1.2-1)."));
            }

            // One EOL marker, then the comment at once.
            // head[lineEnd] is the line's CR or LF, never '%', so the comment test fails when no EOL
            // was consumed.
            int at = lineEnd;
            if (at < head.Length && head[at] == '\r') at++;
            if (at < head.Length && head[at] == '\n') at++;
            bool comment = at + 4 < head.Length && head[at] == '%'
                           && head[at + 1] > 127 && head[at + 2] > 127 && head[at + 3] > 127 && head[at + 4] > 127;
            if (!comment)
                v.Add(new PdfAViolation("A33",
                    "The file header is not followed by one EOL marker and a comment of % and four bytes " +
                    $"each above 127 ({iso} 6.1.2; rule 6.1.2-2)."));
        }

        /// <summary>How much of the file's start the header check reads: the header line and the comment.</summary>
        private const int HeaderBytesRead = 64;

        private static bool StartsWithMarker(byte[] head)
            => head.Length >= 5 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F' && head[4] == '-';

        private static bool HeaderLineConforms(byte[] head, int lineEnd, PdfAConformancePart part)
        {
            if (lineEnd < 8 || head[6] != '.') return false;
            bool digits = IsAsciiDigit(head[5]) && IsAsciiDigit(head[7]);
            switch (part)
            {
                case PdfAConformancePart.Part1:
                    return digits;
                case PdfAConformancePart.Part4:
                    return digits && lineEnd == 8 && head[5] == '2';
                default:
                    return digits && lineEnd == 8 && head[5] == '1' && head[7] <= '7';
            }
        }

        private static bool IsAsciiDigit(byte b) => b >= '0' && b <= '9';

        private static string HeaderText(byte[] head, int lineEnd)
        {
            var text = new char[System.Math.Min(lineEnd, 16)];
            for (int i = 0; i < text.Length; i++)
                text[i] = head[i] >= 0x20 && head[i] < 0x7F ? (char)head[i] : '?';
            return new string(text);
        }

        /// <summary>
        /// Up to <paramref name="count"/> bytes from the stream's start, its position put back; null
        /// when the stream cannot seek or read.
        /// </summary>
        private static byte[] ReadFileStart(Stream stream, int count)
        {
            if (stream == null || !stream.CanSeek || !stream.CanRead) return null;
            long position = stream.Position;
            try
            {
                stream.Position = 0;
                var buffer = new byte[count];
                int read = Core.IO.StreamBytes.Fill(stream, buffer, 0, count);
                if (read == count) return buffer;
                var shorter = new byte[read];
                System.Array.Copy(buffer, shorter, read);
                return shorter;
            }
            finally
            {
                stream.Position = position;
            }
        }
    }
}
