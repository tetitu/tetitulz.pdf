// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Full-text search across all pages of a PDF document.
    ///
    /// <para>
    /// Each hit reports the zero-based page number, the matched text, the
    /// surrounding context snippet, and the character offset of the match within
    /// the page's extracted text. Hits do not carry a bounding rectangle.
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   var hits = PdfTextSearch.Search(stream, "invoice", ignoreCase: true);
    ///   foreach (var h in hits)
    ///       Console.WriteLine($"Page {h.PageNumber + 1}: ...{h.ContextSnippet}...");
    /// </code>
    /// </summary>
    public static class PdfTextSearch
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Searches all pages for the given query string and returns every match.
        /// </summary>
        /// <param name="inputPath">Path to the PDF file.</param>
        /// <param name="query">Text to search for.</param>
        /// <param name="ignoreCase">Case-insensitive search when true (default).</param>
        /// <param name="contextChars">
        /// Number of characters of surrounding text to include in the snippet (default 40).
        /// Must be zero or greater; a negative value throws
        /// <see cref="ArgumentOutOfRangeException"/> rather than failing inside the snippet
        /// builder with a message naming a <c>StringBuilder</c> parameter.
        /// </param>
        public static IReadOnlyList<TextSearchHit> Search(
            string inputPath, string query,
            bool ignoreCase = true, int contextChars = 40)
        {
            _lastTruncation = null;
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (contextChars < 0)
                throw new ArgumentOutOfRangeException(nameof(contextChars),
                    "contextChars must be zero or greater.");
            using var fs = File.OpenRead(inputPath);
            return Search(fs, query, ignoreCase, contextChars);
        }

        /// <summary>
        /// Searches all pages for the given query string.
        /// <paramref name="contextChars"/> must be zero or greater.
        /// </summary>
        public static IReadOnlyList<TextSearchHit> Search(
            Stream input, string query,
            bool ignoreCase = true, int contextChars = 40)
        {
            _lastTruncation = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (contextChars < 0)
                throw new ArgumentOutOfRangeException(nameof(contextChars),
                    "contextChars must be zero or greater.");
            if (query.Length == 0) return Array.Empty<TextSearchHit>();

            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the text", "PdfOperations.SearchText(Stream, string, string password, ...)");
            return SearchDocument(doc, query, ignoreCase, contextChars);
        }

        /// <summary>
        /// Searches all pages, with control over how the text is normalised before matching.
        /// </summary>
        /// <param name="input">The PDF to search. Not disposed.</param>
        /// <param name="query">Text to search for.</param>
        /// <param name="options">How to match. Null means the defaults.</param>
        /// <remarks>
        /// <para><b>A separate overload rather than an extra optional parameter on the shipped
        /// signature</b>, because C# bakes optional-argument defaults in at the call site, so
        /// widening a shipped signature is a binary break for an already-compiled consumer — the
        /// rule <c>PdfOperations.FullTextSearch</c> states verbatim.</para>
        ///
        /// <para><b>Hits are reported in the ORIGINAL text's coordinates</b> even when folding
        /// changed the string that was matched: <see cref="TextSearchHit.CharOffset"/> still indexes
        /// the extracted text and <see cref="TextSearchHit.MatchText"/> is still the substring as it
        /// appears there — pointed, with its final forms, exactly as the document spells it. That is
        /// what the normaliser's index map is for, and it is why folding could not be bolted on as a
        /// string transform.</para>
        /// </remarks>
        public static IReadOnlyList<TextSearchHit> Search(
            Stream input, string query, TextSearchOptions options)
        {
            _lastTruncation = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (query == null) throw new ArgumentNullException(nameof(query));

            options = options ?? new TextSearchOptions();
            if (options.ContextChars < 0)
                throw new ArgumentOutOfRangeException(nameof(options),
                    "TextSearchOptions.ContextChars must be zero or greater.");
            if (query.Length == 0) return Array.Empty<TextSearchHit>();

            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the text", "PdfOperations.SearchText(Stream, string, string password, ...)");
            return SearchDocument(doc, query, options.IgnoreCase, options.ContextChars,
                                  options.HebrewFolding);
        }

        /// <summary>
        /// Returns true when the document contains at least one occurrence of the query, with
        /// control over normalisation. See the <see cref="Search(Stream,string,TextSearchOptions)"/>
        /// overload for why this is a separate method.
        /// </summary>
        public static bool Contains(Stream input, string query, TextSearchOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (query.Length == 0) return false;

            options = options ?? new TextSearchOptions();
            var cmp = options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string foldedQuery = HebrewSearchNormalizer.Fold(query, options.HebrewFolding);

            // A query can survive the empty check above and still fold to nothing — one written
            // entirely of niqqud or other marks the folding removes. IndexOf("") is 0, so without
            // this every document with any text at all answered TRUE while Search, which does make
            // this check, returned no hits: two methods documented as answering one question,
            // answering it oppositely.
            if (foldedQuery.Length == 0) return false;

            using var doc = PdfDocument.Open(input);
            // An encrypted document reads as no pages, and "false" from one would be a confident
            // wrong answer about text that is there: refused, naming the door that takes a password.
            EncryptedRead.Refuse(doc, "the text", "PdfOperations.ContainsText(Stream, string, string password, ...)");
            foreach (var page in doc.Pages)
            {
                string pageText = PdfTextExtractor.ExtractText(page);
                if (pageText == null) continue;

                string haystack = HebrewSearchNormalizer.Fold(pageText, options.HebrewFolding);
                if (haystack.IndexOf(foldedQuery, cmp) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when the document contains at least one occurrence of the query.
        /// Faster than <see cref="Search(Stream,string,bool,int)"/> for existence checks.
        /// </summary>
        public static bool Contains(Stream input, string query, bool ignoreCase = true)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (query.Length == 0) return false;

            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the text", "PdfOperations.ContainsText(Stream, string, string password, ...)");

            var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var page in doc.Pages)
            {
                string pageText = PdfTextExtractor.ExtractText(page);
                if (pageText != null && pageText.IndexOf(query, cmp) >= 0)
                    return true;
            }
            return false;
        }

        // ----------------------------------------------------------------
        //  Core
        // ----------------------------------------------------------------

        [ThreadStatic] private static string _lastTruncation;

        /// <summary>
        /// Why the last <c>Search</c> on this thread returned less than every match, or <c>null</c> when it
        /// returned them all: past <see cref="Core.Constants.PdfLimits.MaxTextSearchHits"/> it stops, and past
        /// <see cref="Core.Constants.PdfLimits.MaxTextSearchContextChars"/> a hit's snippet is its match alone.
        /// </summary>
        /// <remarks>
        /// The hits are the page's to choose — matches overlap, and pages may share one content stream — so a
        /// search of a small file kept tens of millions of them, each with its snippet and its match copied
        /// (07's DoS hunt, F9). Per-thread, and reset at the start of every search.
        /// </remarks>
        public static string LastTruncation => _lastTruncation;

        private static IReadOnlyList<TextSearchHit> SearchDocument(
            PdfDocument doc, string query, bool ignoreCase, int contextChars,
            HebrewFolding folding = HebrewFolding.None)
        {
            _lastTruncation = null;
            var hits = new List<TextSearchHit>();
            var cmp  = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int maxHits = Core.Constants.PdfLimits.MaxTextSearchHits;
            long contextLeft = Core.Constants.PdfLimits.MaxTextSearchContextChars;

            string foldedQuery = HebrewSearchNormalizer.Fold(query, folding);
            if (foldedQuery.Length == 0) return hits;

            for (int pi = 0; pi < doc.Pages.Count; pi++)
            {
                var page     = doc.Pages[pi];
                string text  = PdfTextExtractor.ExtractText(page);
                // A shortcut, not a guard: ExtractText(PdfPage) never answers null, and a non-empty
                // foldedQuery is never found in "" — deleting this changes no hit (mutation-verified
                // equivalent). It only skips folding a page with nothing to search.
                if (string.IsNullOrEmpty(text)) continue;

                // Matching happens in folded space; everything REPORTED is translated back through
                // the index map into the caller's own coordinates. Reporting folded offsets would
                // break TextSearchHit's two documented contracts and silently move every rectangle
                // a caller derives from CharOffset.
                string haystack = HebrewSearchNormalizer.Fold(text, folding, out int[] source);

                int start = 0;
                while (true)
                {
                    int idx = haystack.IndexOf(foldedQuery, start, cmp);
                    if (idx < 0) break;
                    if (hits.Count >= maxHits)
                    {
                        _lastTruncation = $"the search stopped at {maxHits} hits (PdfLimits.MaxTextSearchHits), on page {pi + 1}; "
                            + "the document has more.";
                        return hits;
                    }

                    // The end comes from the MAP, never from idx + foldedQuery.Length: anything
                    // stripped inside the match — which, for pointed Hebrew, is most of it — makes
                    // the folded length shorter than the span it covers.
                    int origStart = source[idx];
                    int origEnd   = source[idx + foldedQuery.Length - 1] + 1;
                    string matchText = text.Substring(origStart, origEnd - origStart);

                    // Build context snippet.
                    // In long: a context of int.MaxValue overflowed the end past the text, and Append threw.
                    int ctxStart = (int)Math.Max(0L, (long)origStart - contextChars);
                    int ctxEnd   = (int)Math.Min(text.Length, (long)origEnd + contextChars);
                    string context;
                    if (ctxEnd - ctxStart <= contextLeft)
                    {
                        contextLeft -= ctxEnd - ctxStart;
                        var snippet  = new StringBuilder();
                        if (ctxStart > 0) snippet.Append("…");
                        snippet.Append(text, ctxStart, ctxEnd - ctxStart);
                        if (ctxEnd < text.Length) snippet.Append("…");
                        context = snippet.ToString();
                    }
                    else
                    {
                        // The context budget is spent: the match stands as its own snippet.
                        _lastTruncation ??= $"snippets past {Core.Constants.PdfLimits.MaxTextSearchContextChars} characters of context "
                            + "(PdfLimits.MaxTextSearchContextChars) are the match alone, from page " + (pi + 1) + ".";
                        contextLeft = 0;
                        context = matchText;
                    }

                    hits.Add(new TextSearchHit(
                        pageNumber:  pi,
                        matchText:   matchText,
                        context:     context,
                        charOffset:  origStart));

                    start = idx + 1;
                }
            }

            return hits;
        }
    }

    /// <summary>How <see cref="PdfTextSearch"/> should match.</summary>
    /// <remarks>
    /// Exists so normalisation could be added without widening a shipped signature — see the
    /// overload that takes it. Its defaults reproduce the older overloads exactly, so the two
    /// routes cannot disagree about a document.
    /// </remarks>
    public sealed class TextSearchOptions
    {
        /// <summary>Case-insensitive matching. Defaults to true, as the older overloads do.</summary>
        public bool IgnoreCase { get; set; } = true;

        /// <summary>Characters of surrounding text in the snippet. Must be zero or greater.</summary>
        public int ContextChars { get; set; } = 40;

        /// <summary>
        /// Which Hebrew spelling differences to fold away before matching.
        /// <see cref="HebrewFolding.None"/> by default, so this type changes nothing until asked.
        /// </summary>
        /// <remarks>
        /// <para><b>Set it to <see cref="HebrewFolding.Default"/> to search pointed Hebrew.</b> A
        /// pointed document extracts with its niqqud present, so an unpointed query — which is what
        /// a person types — matches nothing at all without folding.</para>
        ///
        /// <para><b>The WinForms viewer's find box defaults to
        /// <see cref="HebrewFolding.Default"/> instead, and the difference is deliberate rather than
        /// drift.</b> A find box is driven by a human who just typed the word and can see the page,
        /// so a miss is the visible failure and folding is the safe default. A library search feeds
        /// code that cannot tell a fold from a match, and its result may already be depended on — so
        /// here the safe default is to change nothing. Same fold, opposite risk.</para>
        /// </remarks>
        public HebrewFolding HebrewFolding { get; set; } = HebrewFolding.None;
    }

    /// <summary>A single text-search match.</summary>
    public sealed class TextSearchHit
    {
        /// <summary>Zero-based page index.</summary>
        public int PageNumber { get; }

        /// <summary>The exact matched text as it appears in the extracted string.</summary>
        public string MatchText { get; }

        /// <summary>Surrounding context including the match.</summary>
        public string ContextSnippet { get; }

        /// <summary>Character offset of the match within the page's extracted text.</summary>
        public int CharOffset { get; }

        public TextSearchHit(int pageNumber, string matchText, string context, int charOffset)
        {
            PageNumber     = pageNumber;
            MatchText      = matchText;
            ContextSnippet = context;
            CharOffset     = charOffset;
        }

        public override string ToString()
            => $"Page {PageNumber + 1}, offset {CharOffset}: \"{MatchText}\"";
    }
}
