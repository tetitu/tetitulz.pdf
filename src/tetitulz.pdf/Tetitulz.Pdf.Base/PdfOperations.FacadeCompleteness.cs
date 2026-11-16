// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>Enumerates fonts referenced by the document. Returns (resourceName,
        /// baseName, subtype) per font found across all page resource dictionaries.
        /// Never null; empty list when no fonts are used.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<PdfFontSummary> ListFonts(Stream input)
            => ListFonts(input, null);

        /// <summary>Enumerates fonts referenced by the document, decrypting first when it is
        /// encrypted and a decryptor is registered.</summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">
        /// The user password; ignored for an unencrypted document. Without it an encrypted document
        /// has an unreadable page tree, so the answer is an empty list — the same answer a document
        /// that uses no fonts gives.
        /// </param>
        public static IReadOnlyList<PdfFontSummary> ListFonts(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return WithDecrypted(input, password, ListFontsCore);
        }

        private static IReadOnlyList<PdfFontSummary> ListFontsCore(Stream input)
        {
            using var doc = PdfDocument.Open(input);
            var seen = new HashSet<int>();
            var result = new List<PdfFontSummary>();
            foreach (var page in doc.Pages)
            {
                var res = page.Resources;
                if (res == null) continue;
                var fontObj = res[PdfName.Font];
                var fontDict = doc.ResolveObject(fontObj) as PdfDictionary
                              ?? fontObj as PdfDictionary;
                if (fontDict == null) continue;
                foreach (var kvp in fontDict)
                {
                    var refNum = (kvp.Value as PdfReference)?.ReferenceObjectNumber ?? 0;
                    if (refNum != 0 && !seen.Add(refNum)) continue;
                    var fd = doc.ResolveObject(kvp.Value) as PdfDictionary;
                    if (fd == null) continue;
                    string subtype = PdfResolve.Name(doc, fd, PdfName.Subtype)?.Value ?? "Unknown";
                    string baseFont = PdfResolve.Name(doc, fd, PdfName.BaseFont)?.Value ?? "";
                    result.Add(new PdfFontSummary(kvp.Key.Value, baseFont, subtype));
                }
            }
            return result;
        }

        // GetPageLabels formats what Structure.PdfPageLabels returns, and that walker owns every
        // dictionary key the number tree needs.

        /// <summary>The logical page number a label range starts at when it omits <c>/St</c>.</summary>
        private const int DefaultPageLabelStart = 1;

        /// <summary>Reads /PageLabels from the catalog and returns a user-facing label
        /// (e.g. "i", "ii", "1", "A-1") for each page index, or null when the document
        /// has no page-label tree.</summary>
        /// <remarks>
        /// <para><b>The range reading comes from <see cref="Operations.Structure.PdfPageLabels"/>,
        /// which is the number-tree walker.</b> Reading <c>catalog /PageLabels /Nums</c> directly and
        /// returning <c>null</c> when that key is absent would be wrong: §12.4.2 makes <c>/PageLabels</c> a <i>number tree</i>, whose intermediate
        /// nodes carry <c>/Kids</c> and no <c>/Nums</c> at all. A document whose labels live below
        /// a root node therefore answered "this document has no page labels", which is
        /// indistinguishable from a document that genuinely has none. The real reader walks
        /// <c>/Kids</c> and bounds the recursion at <see cref="Core.Constants.PdfLimits.MaxTreeDepth"/>; this
        /// method now formats what that returns.</para>
        /// </remarks>
        public static IReadOnlyList<string> GetPageLabels(Stream input)
            => GetPageLabelsCore(input);

        /// <summary>
        /// Formatted page labels for an <b>encrypted</b> document.
        /// </summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password, never a <c>password = null</c>
        /// appended above — that is a binary break, and the shipped overload keeps its original
        /// call path so its behaviour is untouched. See the remarks on
        /// <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        public static IReadOnlyList<string> GetPageLabels(Stream input, string password)
            => WithDecrypted(input, password, GetPageLabelsCore);

        /// <summary>
        /// Writes the document's page labels, replacing any it has, as one appended revision; an
        /// empty list removes them. The ranges are checked and ordered as
        /// <see cref="Operations.Structure.PdfPageLabels.SetLabels"/> describes.
        /// </summary>
        public static void SetPageLabels(Stream input, Stream output,
                                         IReadOnlyList<Operations.Structure.PageLabelRange> ranges)
            => Operations.Structure.PdfPageLabels.SetLabels(input, output, ranges);

        /// <summary>The label walk itself, shared by both overloads so they cannot drift.</summary>
        private static IReadOnlyList<string> GetPageLabelsCore(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            Stream seekable = PdfHelpers.EnsureSeekable(input);
            var labelRanges = Operations.Structure.PdfPageLabels.GetLabels(seekable);
            if (labelRanges == null || labelRanges.Count == 0) return null;

            seekable.Position = 0;
            using var doc = PdfDocument.Open(seekable);

            var ranges = new List<(int firstPage, string prefix, string style, int start)>();
            foreach (var r in labelRanges)
                ranges.Add((r.StartPageIndex, r.Prefix ?? "", r.Style, r.StartValue));

            // The PageLabels number tree is required to be sorted by key, but a
            // malformed PDF may not be; sort so the "last matching range" scan
            // below selects the correct (highest firstPage <= p) range.
            ranges.Sort((x, y) => x.firstPage.CompareTo(y.firstPage));

            var list = new string[doc.Pages.Count];

            // Both sequences ascend — the ranges were just sorted, and p increases — so one cursor
            // that advances with p, not a full rescan of every range per page — which would be
            // O(pages x ranges) on a value the file controls on BOTH axes: a large /Nums array plus a
            // few thousand real pages would turn a metadata read into hundreds of millions of
            // comparisons.
            (int firstPage, string prefix, string style, int start) active =
                (0, "", null, DefaultPageLabelStart);
            int next = 0;
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                while (next < ranges.Count && ranges[next].firstPage <= p)
                    active = ranges[next++];

                // In long: /St may be int.MaxValue, and the page offset added to it overflowed.
                long num = (long)active.start + (p - active.firstPage);
                list[p] = active.prefix + FormatPageLabel(num, active.style);
            }
            return list;
        }

        // A /S value this library does not recognise falls back to decimal, which is what a
        // viewer does; a missing /S means the range contributes only its /P prefix. A roman or
        // alphabetic label longer than PdfLimits.MaxPageLabelLength falls back to decimal too: its
        // length is chosen by the file, and a hostile /St made it megabytes per page.
        private static string FormatPageLabel(long num, string style) => style switch
        {
            Core.Constants.PdfPageLabelStyles.Decimal    => FormatDecimal(num),
            Core.Constants.PdfPageLabelStyles.UpperRoman => ToRoman(num)?.ToUpperInvariant() ?? FormatDecimal(num),
            Core.Constants.PdfPageLabelStyles.LowerRoman => ToRoman(num) ?? FormatDecimal(num),
            Core.Constants.PdfPageLabelStyles.UpperAlpha => ToLetters(num)?.ToUpperInvariant() ?? FormatDecimal(num),
            Core.Constants.PdfPageLabelStyles.LowerAlpha => ToLetters(num) ?? FormatDecimal(num),
            null                                         => "",
            _                                            => FormatDecimal(num)
        };

        private static string FormatDecimal(long num) =>
            num.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static readonly int[] RomanValues =
            { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        private static readonly string[] RomanNumerals =
            { "m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i" };

        /// <summary>
        /// The lower-case roman numeral for <paramref name="n"/>, or null when its thousands alone
        /// would exceed <see cref="Core.Constants.PdfLimits.MaxPageLabelLength"/> characters.
        /// </summary>
        private static string ToRoman(long n)
        {
            if (n / RomanValues[0] > Core.Constants.PdfLimits.MaxPageLabelLength) return null;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < RomanValues.Length; i++)
                while (n >= RomanValues[i]) { sb.Append(RomanNumerals[i]); n -= RomanValues[i]; }
            return sb.ToString();
        }

        /// <summary>
        /// Alphabetic page labels: a…z, then aa…zz, then aaa…zzz (PDF 32000-1:2008, Table 159) — or
        /// null when the repeated letter would exceed
        /// <see cref="Core.Constants.PdfLimits.MaxPageLabelLength"/> characters.
        /// </summary>
        private static string ToLetters(long n)
        {
            const int LettersInAlphabet = 26;
            long cycles = (n - 1) / LettersInAlphabet + 1;
            if (cycles > Core.Constants.PdfLimits.MaxPageLabelLength) return null;

            char c = (char)('a' + (n - 1) % LettersInAlphabet);
            return new string(c, (int)cycles);
        }

        /// <summary>
        /// Rotates every page by <paramref name="degrees"/> (must be a multiple of 90), in one
        /// incremental update.
        /// </summary>
        /// <remarks>
        /// <para><b>One revision, not one per page.</b> Calling
        /// <see cref="PdfPageManipulator.RotatePage"/> once per page would re-serialise the whole — and
        /// monotonically growing — document each time, so a 500-page file would do 500 full
        /// re-serialisations and the output carry 500 revisions, every intermediate
        /// half-rotated state recoverable through <see cref="ExtractRevision"/>, which is a
        /// surprising thing for an operation the caller thinks of as one edit. Cloning every page
        /// dictionary and handing the whole list to a single
        /// <see cref="IncrementalUpdateWriter.WriteUpdate(byte[], Stream, System.Collections.Generic.IReadOnlyList{IndirectObject}, IncrementalUpdateOptions)"/>
        /// makes the result two revisions: the original plus this edit.</para>
        /// <para>A page whose dictionary has no object number cannot be superseded by an
        /// incremental update, so it is left unrotated rather than silently dropped — the same
        /// refusal <c>RotatePage</c> makes, but per page instead of for the whole call.</para>
        /// </remarks>
        public static void RotateAllPages(Stream input, Stream output, int degrees)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.", nameof(degrees));

            // Read twice — parsed, then copied through or used as the incremental writer's base —
            // so this one needs a seekable view, and `ownsBuffer` says whether that view is a
            // buffer to release or the caller's own stream.
            Stream ms = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsBuffer);
            try
            {
                RotateAllPagesIn(ms, output, degrees);
            }
            finally
            {
                if (ownsBuffer) ms.Dispose();
            }
        }

        private static void RotateAllPagesIn(Stream ms, Stream output, int degrees)
        {
            using var doc = PdfDocument.Open(ms);

            var objects = new List<IndirectObject>();
            foreach (var page in doc.Pages)
            {
                int objNum = page.Dictionary.ObjectNumber;
                if (objNum <= 0) continue;

                var updated = Core.Objects.PdfDictionary.CloneShallow(page.Dictionary);
                updated.Set(PdfName.Rotate, new PdfNumber(degrees));
                updated.ObjectNumber = objNum;
                updated.GenerationNumber = 0;
                objects.Add(new IndirectObject(objNum, 0, updated));
            }

            if (objects.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return;
            }

            int size = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;

            // The appended revision's trailer is Core.Writing.PdfTrailerBuilder's, reached through
            // WriteUpdate — which hands whatever trailer it is given to
            // PdfTrailerBuilder.ForAppendedRevision.
            //
            // Never built inline: copying every key of doc.Trailer forward is ForUpdate's body minus
            // its deny list, so it would carry /Type /XRef, /W, /Index, /Filter, /DecodeParms and a
            // stale /Length forward for any PDF 1.5+ document, whose trailer IS the cross-reference
            // stream's own dictionary as the parser presents it.
            //
            // It was harmless only because the writer strips those keys again a moment later; that
            // is a fact about the current call order, not a property of this code, so the copy is
            // deleted rather than corrected. doc.Trailer goes straight to the one implementation.
            ms.Position = 0;
            IncrementalUpdateWriter.WriteUpdate(ms, output, objects, doc.Trailer,
                doc.Parser.StartXrefOffset, size);
        }
    }
}
