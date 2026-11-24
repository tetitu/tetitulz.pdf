// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>A single page-label range entry from the /PageLabels number tree.</summary>
    public sealed class PageLabelRange
    {
        /// <summary>Zero-based first page index this range applies to.</summary>
        public int StartPageIndex { get; init; }

        /// <summary>Numbering style: "D" (decimal), "r" (lower-roman), "R" (upper-roman),
        /// "a" (lower-alpha), "A" (upper-alpha), or null (no label).</summary>
        public string Style { get; init; }

        /// <summary>Label prefix string prepended to every page number in this range.</summary>
        public string Prefix { get; init; }

        /// <summary>Starting logical page number within this range (default 1).</summary>
        public int StartValue { get; init; } = 1;
    }

    /// <summary>
    /// Read and write /PageLabels number trees.
    /// </summary>
    public static class PdfPageLabels
    {
        // ----------------------------------------------------------------
        //  Read
        // ----------------------------------------------------------------

        /// <summary>
        /// Why the last <see cref="GetLabels(Stream)"/> on this thread returned fewer ranges than the
        /// document defines, or a range's prefix cut short; <c>null</c> when it read them all whole.
        /// </summary>
        /// <remarks>
        /// <para>The <c>/PageLabels</c> number tree's walk stops at
        /// <see cref="PdfLimits.MaxNameTreeEntries"/> entries or <see cref="PdfLimits.MaxTreeDepth"/>
        /// levels, and the ranges it did not reach are not an absence a caller can see: every page
        /// past the last range read takes that range's numbering instead of its own. A <c>/P</c>
        /// prefix longer than <see cref="PdfLimits.MaxPageLabelLength"/> is cut to it, since the
        /// prefix is repeated on every page of its range.</para>
        /// <para><c>[ThreadStatic]</c>: read it on the thread that made the call.</para>
        /// </remarks>
        public static string LastReadError
        {
            get => _lastReadError;
            private set => _lastReadError = value;
        }

        [ThreadStatic]
        private static string _lastReadError;

        /// <summary>Returns all page-label ranges defined in the PDF file.</summary>
        public static IReadOnlyList<PageLabelRange> GetLabels(string inputPath)
        {
            LastReadError = null;
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return GetLabels(fs);
        }

        /// <summary>Returns all page-label ranges defined in the PDF stream.</summary>
        /// <remarks>A read cut short by a tree limit is reported through <see cref="LastReadError"/>.</remarks>
        public static IReadOnlyList<PageLabelRange> GetLabels(Stream input)
        {
            LastReadError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the page labels", null);
            var ranges = ReadLabels(doc, out bool complete);
            if (!complete)
                LastReadError = (LastReadError == null ? "" : LastReadError + "; ") +
                                "the /PageLabels number tree exceeds the entry budget or the depth limit, " +
                                "so the page-label ranges are incomplete";
            return ranges;
        }

        // ----------------------------------------------------------------
        //  Write
        // ----------------------------------------------------------------

        /// <summary>
        /// Writes a new /PageLabels number tree to the output, replacing any existing one. An empty
        /// list removes the document's page labels.
        /// </summary>
        /// <remarks>
        /// <para><b>The ranges are checked, and written in ascending order whatever order they
        /// arrive in.</b> §7.9.7 requires a number tree's keys in ascending order — a reader finds a
        /// page's range by searching them — and §12.4.2 requires a range for page 0, a start of at
        /// least 1, and a style from Table 159. Written as given, ranges out of order would make a
        /// tree a reader searches wrongly, and a misspelled style labels a viewer silently ignores;
        /// each is refused before anything is written, naming the range.</para>
        ///
        /// <para>An empty list removes <c>/PageLabels</c> rather than writing a tree with no
        /// entries, which §12.4.2 does not allow — it must have one for page 0.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// A range is null; two ranges start on the same page; a start page is negative; no range
        /// starts on page 0; a <see cref="PageLabelRange.StartValue"/> is below 1; or a
        /// <see cref="PageLabelRange.Style"/> is not one of <see cref="PdfPageLabelStyles"/>.
        /// </exception>
        public static void SetLabels(Stream input, Stream output, IReadOnlyList<PageLabelRange> ranges)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));
            var ordered = InTreeOrder(ranges);

            // The stream is read twice — once to parse and once as the incremental writer's base —
            // so unlike the read-only paths this one really does need a seekable view. `ownsBuffer`
            // says whether that view is a buffer this method must release or the caller's own
            // stream, which it must not touch.
            Stream ms = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsBuffer);
            try
            {
                WriteLabels(ms, output, ordered);
            }
            finally
            {
                if (ownsBuffer) ms.Dispose();
            }
        }

        /// <summary>
        /// <paramref name="ranges"/> sorted by start page, or an <see cref="ArgumentException"/>
        /// naming the first range §12.4.2 does not allow.
        /// </summary>
        private static List<PageLabelRange> InTreeOrder(IReadOnlyList<PageLabelRange> ranges)
        {
            var ordered = new List<PageLabelRange>(ranges.Count);
            for (int i = 0; i < ranges.Count; i++)
            {
                PageLabelRange r = ranges[i];
                if (r == null)
                    throw new ArgumentException($"Page-label range {i} is null.", nameof(ranges));
                if (r.StartPageIndex < 0)
                    throw new ArgumentException(
                        $"Page-label range {i} starts on page {r.StartPageIndex}; a page index cannot be negative.",
                        nameof(ranges));
                if (r.StartValue < 1)
                    throw new ArgumentException(
                        $"Page-label range {i} numbers from {r.StartValue}; §12.4.2 requires /St of at least 1.",
                        nameof(ranges));
                if (!string.IsNullOrEmpty(r.Style) && !PdfPageLabelStyles.IsDefined(r.Style))
                    throw new ArgumentException(
                        $"Page-label range {i} has the style \"{r.Style}\"; §12.4.2 allows D, R, r, A and a " +
                        "(names are case-sensitive), or none.", nameof(ranges));
                ordered.Add(r);
            }

            // Equal keys end up adjacent, which is all the duplicate check below needs.
            ordered.Sort((a, b) => a.StartPageIndex.CompareTo(b.StartPageIndex));
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].StartPageIndex == ordered[i - 1].StartPageIndex)
                    throw new ArgumentException(
                        $"Two page-label ranges start on page {ordered[i].StartPageIndex}; a number tree " +
                        "holds one value per key.", nameof(ranges));

            if (ordered.Count > 0 && ordered[0].StartPageIndex != 0)
                throw new ArgumentException(
                    $"No page-label range starts on page 0 (the first starts on page {ordered[0].StartPageIndex}); " +
                    "§12.4.2 requires one. Add a range for page 0, with no style if those pages carry no label.",
                    nameof(ranges));

            return ordered;
        }

        private static void WriteLabels(Stream ms, Stream output, IReadOnlyList<PageLabelRange> ranges)
        {
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "set the page labels of", "the /PageLabels tree");
            // The catalog's number from the trailer: a /Root written directly has none, and the
            // catalog went out as object 0, which the writer skips — the tree was appended with
            // nothing pointing to it, and the call returned.
            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the /PageLabels tree");

            int nextObj = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var objects = new List<IndirectObject>();
            var newCatalog = CloneDictionary(doc.Catalog.Dictionary);

            if (ranges.Count == 0)
            {
                // No ranges, no tree: §12.4.2 does not allow one without an entry for page 0.
                newCatalog.Remove(PdfName.PageLabels);
            }
            else
            {
                // The /Nums array: pairs of (pageIndex labelDict), in the ascending key order the
                // caller's list was sorted into.
                var nums = new PdfArray();
                foreach (var r in ranges)
                {
                    nums.Add(new PdfNumber(r.StartPageIndex));

                    var labelDict = new PdfDictionary();
                    if (!string.IsNullOrEmpty(r.Style))
                        labelDict.Set(PdfName.S, new PdfName(r.Style));
                    if (!string.IsNullOrEmpty(r.Prefix))
                        labelDict.Set(PdfName.P, new PdfString(r.Prefix));
                    if (r.StartValue != 1)
                        labelDict.Set(PdfName.St, new PdfNumber(r.StartValue));
                    nums.Add(labelDict);
                }

                // Inline number tree (single leaf node)
                var pageLabelsDict = new PdfDictionary();
                pageLabelsDict.Set(PdfName.Nums, nums);
                pageLabelsDict.ObjectNumber = nextObj++;
                pageLabelsDict.GenerationNumber = 0;
                objects.Add(new IndirectObject(pageLabelsDict.ObjectNumber, 0, pageLabelsDict));

                newCatalog.Set(PdfName.PageLabels, new PdfReference(pageLabelsDict.ObjectNumber, 0));
            }

            newCatalog.ObjectNumber = catalogObjNum;
            newCatalog.GenerationNumber = 0;
            objects.Add(new IndirectObject(newCatalog.ObjectNumber, 0, newCatalog));

            int size = nextObj;
            var trailer = BuildTrailer(doc.Trailer, size);
            ms.Position = 0;
            IncrementalUpdateWriter.WriteUpdate(ms, output, objects, trailer,
                doc.Parser.StartXrefOffset, size);
        }

        // ----------------------------------------------------------------
        //  Implementation
        // ----------------------------------------------------------------

        private static IReadOnlyList<PageLabelRange> ReadLabels(PdfDocument doc, out bool complete)
        {
            complete = true;
            var result = new List<PageLabelRange>();
            if (doc.Catalog == null) return result;

            var plObj = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.PageLabels]);
            if (!(plObj is PdfDictionary plDict)) return result;

            // Collect all (number, labelDict) pairs from the number tree — through
            // Core.Document.PdfNumberTree, the §7.9.7 walk CLAUDE.md names as the one
            // implementation. See CollectNumberTreePairs' obituary below for what the copy cost.
            var pairs = new List<(int pageIdx, PdfDictionary labelDict)>();
            var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            complete = PdfNumberTree.TryFlatten(plDict, doc, entries, ref budget);

            foreach (var entry in entries)
            {
                // A key that is not a number is a malformed tree, and TryKey says so rather than
                // dropping it quietly. Substituting 0 would not mean "no entry" but page ONE, so a
                // damaged key would silently relabel the first page.
                if (!PdfNumberTree.TryKey(doc.ResolveObject(entry.Key), out long pageIdx)) continue;
                if (pageIdx < 0 || pageIdx > int.MaxValue) continue;

                if (doc.ResolveObject(entry.Value) is PdfDictionary labelDict)
                    pairs.Add(((int)pageIdx, labelDict));
            }

            foreach (var (pageIdx, labelDict) in pairs)
            {
                string style = null;
                string prefix = null;
                int startValue = 1;

                // All three resolved. The label dictionaries reached through the number tree are
                // resolved; their entries were not. Each null is a DIFFERENT wrong label rather
                // than a missing one: no /S means §12.4.2's "no numbering style", so front matter
                // is reported unlabelled instead of "i, ii, iii"; no /P loses the "A-" prefix; no
                // /St silently restarts at 1. A caller navigating or printing by the printed page
                // number then addresses the wrong page.
                var sObj = doc.ResolveObject(labelDict[PdfName.S]);
                if (sObj is PdfName sName) style = sName.Value;

                var pObj = doc.ResolveObject(labelDict[PdfName.P]);
                if (pObj is PdfString pStr)
                {
                    prefix = pStr.GetText();
                    // The prefix is written onto every page of its range, so its length times the
                    // page count is a product the file chooses on both sides; cut at the one label
                    // ceiling, and said.
                    int cap = PdfLimits.MaxPageLabelLength;
                    if (prefix.Length > cap)
                    {
                        LastReadError = $"a page-label range's /P prefix is {prefix.Length} characters; the first {cap} " +
                                        "(PdfLimits.MaxPageLabelLength) were kept, since the prefix is written on every page of the range";
                        prefix = prefix.Substring(0, cap);
                    }
                }

                // §12.4.2: /St is an integer of at least 1, and 1 when absent. A value that is not
                // one is read as that default rather than truncated or saturated through IntValue,
                // which made /St 2.5 into 2 and /St -100 into -100 — and a negative start is not a
                // label nobody reads: the facade's alphabetic formatter threw on it.
                var stObj = doc.ResolveObject(labelDict[PdfName.St]);
                if (stObj is PdfNumber stNum && stNum.IsInteger
                    && stNum.LongValue >= 1 && stNum.LongValue <= int.MaxValue)
                    startValue = (int)stNum.LongValue;

                result.Add(new PageLabelRange
                {
                    StartPageIndex = pageIdx,
                    Style          = style,
                    Prefix         = prefix,
                    StartValue     = startValue
                });
            }

            return result.AsReadOnly();
        }

        // The §7.9.7 walk is Core.Document.PdfNumberTree's, which ReadLabels calls directly. It
        // descends /Kids even on a node that also carries /Nums, and skips a key that is not a
        // number rather than reading it as 0, which would be not "no entry" but page ONE.

        // One implementation, in Core.Objects.PdfDictionary.CloneShallow.
        private static PdfDictionary CloneDictionary(PdfDictionary src) => PdfDictionary.CloneShallow(src);

        // One implementation, in Core.Writing.PdfTrailerBuilder. IncrementalUpdateWriter also
        // drops /Prev from whatever trailer it is handed, so no caller's trailer carries the
        // original's forward.
        private static PdfDictionary BuildTrailer(PdfDictionary existing, int size)
            => PdfTrailerBuilder.ForUpdate(existing, size);
    }
}

