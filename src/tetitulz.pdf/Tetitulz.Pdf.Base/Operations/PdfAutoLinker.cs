// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>A URL found in a page's text, with the box it occupies.</summary>
    public sealed class AutoLinkCandidate
    {
        internal AutoLinkCandidate(int pageIndex, string text, string uri,
                                   double x, double y, double width, double height)
        {
            PageIndex = pageIndex;
            Text = text;
            Uri = uri;
            X = x; Y = y; Width = width; Height = height;
        }

        /// <summary>Zero-based page index.</summary>
        public int PageIndex { get; }

        /// <summary>The matched text exactly as it appears in the page.</summary>
        public string Text { get; }

        /// <summary>
        /// The URI the link would target — <see cref="Text"/> with a scheme supplied where the page
        /// wrote none (<c>www.x.com</c> → <c>https://www.x.com</c>, an address → <c>mailto:</c>).
        /// </summary>
        public string Uri { get; }

        /// <summary>Left edge, in points from the page origin.</summary>
        public double X { get; }
        /// <summary>Bottom edge — the text <b>baseline</b>, not the descender.</summary>
        public double Y { get; }
        /// <summary>Width in points.</summary>
        public double Width { get; }
        /// <summary>Height in points, baseline to ascender.</summary>
        public double Height { get; }
    }

    /// <summary>Options for <see cref="PdfAutoLinker"/>.</summary>
    public sealed class AutoLinkOptions
    {
        /// <summary>Turn bare e-mail addresses into <c>mailto:</c> links. Default true.</summary>
        public bool DetectEmailAddresses { get; set; } = true;

        /// <summary>
        /// Turn a bare host beginning <c>www.</c> into an <c>https://</c> link. Default true.
        /// </summary>
        /// <remarks>
        /// Limited to <c>www.</c> on purpose. "Any dotted token is a host" matches
        /// <c>e.g.</c>, <c>Fig.3</c>, version numbers and the end of a sentence, and a link a reader
        /// did not ask for is worse than a missing one.
        /// </remarks>
        public bool DetectBareHosts { get; set; } = true;

        /// <summary>
        /// Skip a candidate whose box overlaps a <c>/Link</c> the page already has. Default true.
        /// </summary>
        /// <remarks>
        /// Without it, running the autolinker twice doubles every annotation — and a document that
        /// already links its URLs is the common case for anything produced by a word processor.
        /// </remarks>
        public bool SkipAlreadyLinked { get; set; } = true;

        /// <summary>Padding added around the matched glyphs, in points. Default 1.</summary>
        public double Padding { get; set; } = 1;
    }

    /// <summary>
    /// Synthesises <c>/Link</c> annotations from URLs written as plain text.
    /// </summary>
    /// <remarks>
    /// <para><b>Three components shipped and never met</b> — the same shape
    /// <c>PdfTextRedactor</c> records. <c>PdfLinkExtractor</c> reads links,
    /// <c>PdfAnnotationWriter.AddUriLink</c> writes one, and
    /// <c>PdfStructuredTextExtractor</c> has per-character geometry; nothing turned a URL in the
    /// text into an annotation over it.</para>
    ///
    /// <para><b>Find and apply are separate entry points</b>, because adding annotations to a
    /// document is not trivially reversible and a URL detector's false positives are the whole risk.
    /// <see cref="FindLinks"/> is what a caller reviews.</para>
    ///
    /// <para><b>Two limits inherited from extraction, stated rather than discovered:</b> the text
    /// comes from <c>PdfStructuredTextExtractor</c>, which does not retain space glyphs and rebuilds
    /// words from horizontal gaps — so a URL can arrive glued to neighbouring text, and trailing
    /// punctuation is trimmed from the <i>match</i> rather than trusted from the string. And a match
    /// must lie within one extracted line: <b>a URL broken by a line wrap is skipped</b>, not linked
    /// with one rectangle spanning both lines, which would put a clickable region over unrelated
    /// text.</para>
    /// </remarks>
    public static class PdfAutoLinker
    {
        /// <summary>
        /// Matches an explicit scheme, a <c>www.</c> host, or an e-mail address. Deliberately
        /// conservative: everything it does not match is text a reader did not ask to be clickable.
        /// </summary>
        /// <remarks>
        /// <para><b>The address alternative is bounded, and that is a DoS guard.</b> It was
        /// <c>[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}</c>: an attempt at every position of
        /// a run, each scanning to the end of the run, is quadratic in the run's length. A run of
        /// letters ending in <c>@</c> took 30 seconds of one core at a million characters on .NET 10,
        /// and about eight minutes at four million, which is a few kilobytes of compressed content
        /// stream. The bounded pattern takes 86 ms at four million.</para>
        /// <para>Now the local part is at most 64 characters and may not begin inside a longer run
        /// of local-part characters, the domain before its top-level label at most 253 and the
        /// top-level label 2 to 63
        /// (RFC 5321 §4.5.3.1.1–2, RFC 1035 §2.3.4). An attempt begins only where a local part can,
        /// and each is bounded. A 65-character local part is not an address, rather than having its
        /// last 64 characters linked as one, and a top-level label may not be followed by another
        /// letter, so a 64-letter one is not an address rather than a 63-letter prefix of one. The
        /// old unbounded label could not be cut short, and a bound without that boundary would.</para>
        /// <para><c>internal</c> so the guard's test can run the pattern on a four-million-character
        /// run without building a page that holds one.</para>
        /// <para>It carries a match timeout as every pattern over a file's text does; a run it is
        /// exceeded on is searched no further and said (<see cref="LastUnsearchedTextError"/>).</para>
        /// </remarks>
        internal static readonly Regex UrlPattern = new Regex(
            @"(?:(?:https?|ftp)://[^\s<>""]+)" +
            @"|(?:www\.[^\s<>""]+)" +
            @"|(?:(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9.\-]{1,253}\.[A-Za-z]{2,63}(?![A-Za-z]))",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        [ThreadStatic] private static string _lastUnsearchedTextError;

        /// <summary>
        /// Which text the last <see cref="FindLinks"/> or <see cref="AddLinks"/> on this thread did
        /// not finish searching because matching took longer than the pattern's time limit, or
        /// <c>null</c> when it searched everything.
        /// </summary>
        /// <remarks><c>[ThreadStatic]</c>, cleared at the start of both entry points.</remarks>
        public static string LastUnsearchedTextError => _lastUnsearchedTextError;

        [ThreadStatic] private static string _lastExistingLinksError;

        /// <summary>
        /// Why the last <see cref="FindLinks"/> or <see cref="AddLinks"/> on this thread could not
        /// check every link the document already has, or <c>null</c> when it checked them all.
        /// </summary>
        /// <remarks>
        /// <para><see cref="AutoLinkOptions.SkipAlreadyLinked"/> compares each candidate with the
        /// page's existing links. When those could not all be read, a URL that is already linked can
        /// be returned, and linked a second time by <see cref="AddLinks"/>. That is the documented
        /// best-effort answer; this says it happened.</para>
        /// <para>Two causes write it: the link extractor stopping at
        /// <see cref="Core.Constants.PdfLimits.MaxAnnotationsPerPage"/> on a page (its
        /// <c>LastError</c> is carried over), and the read throwing.</para>
        /// <para><c>[ThreadStatic]</c>, cleared at the start of both entry points.</para>
        /// </remarks>
        public static string LastExistingLinksError => _lastExistingLinksError;

        /// <summary>
        /// Characters trimmed from the end of a match: a URL at the end of a sentence picks up the
        /// punctuation, and a parenthesised one picks up the bracket.
        /// </summary>
        private const string TrailingPunctuation = ".,;:!?)]}'\"";

        /// <summary>
        /// Finds every URL in the document's text, without changing anything.
        /// </summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <param name="options">Detection options, or null for the defaults.</param>
        /// <remarks>
        /// A first-class entry point rather than a convenience: annotations are appended in a
        /// revision that a caller cannot selectively undo, and "show me what would be linked" should
        /// not be something they have to reconstruct.
        /// </remarks>
        public static IReadOnlyList<AutoLinkCandidate> FindLinks(Stream input, AutoLinkOptions options = null)
        {
            _lastExistingLinksError = null;
            _lastUnsearchedTextError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            options ??= new AutoLinkOptions();

            var ms = PdfMetadataWriter.EnsureSeekable(input);

            // Existing links are read through PdfLinkExtractor rather than by walking /Annots here:
            // it already resolves an indirect /Rect per element and an indirect /URI, both of which
            // a raw read loses. Grouped by page once: each candidate scanned every link of the
            // document, candidates × links.
            var existing = new Dictionary<int, List<Extraction.PdfLink>>();
            if (options.SkipAlreadyLinked)
                foreach (var link in ReadExistingLinks(ms))
                {
                    if (!existing.TryGetValue(link.PageIndex, out var onPage)) existing[link.PageIndex] = onPage = new List<Extraction.PdfLink>();
                    onPage.Add(link);
                }

            ms.Position = 0;
            var pages = PdfStructuredTextExtractor.Extract(ms);
            var found = new List<AutoLinkCandidate>();
            var unsearched = new List<string>();

            foreach (var page in pages)
            {
                foreach (var line in page.Lines)
                {
                    var (text, map) = TextLineGlyphMap.Build(line);
                    if (text.Length == 0) continue;

                    // Matched WITHIN each unbroken glyph run rather than over the whole line, and
                    // that is not a refinement — it is what makes the result correct at all. See
                    // UnbrokenRuns.
                    foreach (var (runStart, runLength) in UnbrokenRuns(map))
                    {
                        string run = text.Substring(runStart, runLength);

                        foreach (Match match in MatchesWithin(UrlPattern, run, page.PageNumber, unsearched))
                        {
                            string matched = TrimTrailing(match.Value);
                            if (matched.Length == 0) continue;

                            string uri = ToUri(matched, options);
                            if (uri == null) continue;

                            if (!TextLineGlyphMap.TryUnionBox(map, runStart + match.Index, matched.Length,
                                    out float x0, out float y0, out float x1, out float y1))
                                continue;

                            double pad = Math.Max(options.Padding, 0);
                            // PdfPageTextLayout.PageNumber is ZERO-BASED despite the name — its own
                            // doc comment says so, and PdfTextRedactor passes it straight into a
                            // PdfRedactionArea.PageIndex. Reading it as one-based would put every
                            // link one page late and link nothing on the last page.
                            var candidate = new AutoLinkCandidate(
                                page.PageNumber, matched, uri,
                                x0 - pad, y0 - pad, (x1 - x0) + pad * 2, (y1 - y0) + pad * 2);

                            if (existing.TryGetValue(candidate.PageIndex, out var linksOnPage)
                                && OverlapsExistingLink(candidate, linksOnPage))
                                continue;

                            found.Add(candidate);
                        }
                    }
                }
            }

            if (unsearched.Count > 0)
                _lastUnsearchedTextError = $"{unsearched.Count} run(s) of text were not searched to their end: matching " +
                                           $"took longer than the pattern's {UrlPattern.MatchTimeout.TotalSeconds:0.###} s limit (the first: {unsearched[0]}).";
            return found;
        }

        /// <summary>
        /// The matches of <paramref name="pattern"/> in <paramref name="run"/>, ending where its match
        /// timeout is exceeded — the matches before it kept, and the run recorded in
        /// <paramref name="unsearched"/>.
        /// </summary>
        internal static IEnumerable<Match> MatchesWithin(Regex pattern, string run, int pageIndex, List<string> unsearched)
        {
            Match match;
            try { match = pattern.Match(run); }
            catch (RegexMatchTimeoutException) { match = null; }
            while (match != null && match.Success)
            {
                yield return match;
                int next = match.Index + Math.Max(match.Length, 1);
                if (next > run.Length) yield break;
                try { match = pattern.Match(run, next); }
                catch (RegexMatchTimeoutException) { match = null; }
            }
            if (match == null)
                unsearched.Add($"a run of {run.Length} characters on page {pageIndex + 1}");
        }

        /// <summary>
        /// Finds every URL in the document's text and writes a <c>/Link</c> over each, as
        /// <b>one</b> incremental revision.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="options">Detection options, or null for the defaults.</param>
        /// <returns>The candidates that were written.</returns>
        public static IReadOnlyList<AutoLinkCandidate> AddLinks(
            Stream input, Stream output, AutoLinkOptions options = null)
        {
            _lastExistingLinksError = null;
            _lastUnsearchedTextError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            var found = FindLinks(ms, options);

            var requests = new List<PdfAnnotationWriter.UriLinkRequest>(found.Count);
            foreach (var c in found)
                requests.Add(new PdfAnnotationWriter.UriLinkRequest
                {
                    PageIndex = c.PageIndex,
                    X1 = c.X, Y1 = c.Y,
                    X2 = c.X + c.Width, Y2 = c.Y + c.Height,
                    Uri = c.Uri,
                });

            ms.Position = 0;
            PdfAnnotationWriter.AddUriLinks(ms, output, requests);
            return found;
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// The links the document already has, for <see cref="AutoLinkOptions.SkipAlreadyLinked"/>,
        /// with any reason the list is incomplete on <see cref="LastExistingLinksError"/>.
        /// </summary>
        /// <remarks>
        /// A document whose links cannot be read is one this pass cannot deduplicate against, and
        /// not a reason to refuse to link it. The empty list makes every candidate look new, which is
        /// the conservative direction only if the caller knows, so the caller is told.
        /// </remarks>
        private static IReadOnlyList<Extraction.PdfLink> ReadExistingLinks(Stream ms)
        {
            ms.Position = 0;
            try
            {
                var links = PdfLinkExtractor.ExtractLinks(ms);
                if (PdfLinkExtractor.LastError != null)
                    _lastExistingLinksError = "Existing links were read in part: " + PdfLinkExtractor.LastError;
                return links;
            }
            catch (Exception ex)
            {
                _lastExistingLinksError = "Existing links could not be read (" + ex.GetType().Name + ": " +
                                          ex.Message + "); every candidate was treated as unlinked.";
                return System.Array.Empty<Extraction.PdfLink>();
            }
        }

        /// <summary>
        /// A gap wider than this fraction of the glyph height is a word break the extractor did not
        /// record. A space in the same font is about 0.28 of its height; intra-word letter spacing
        /// is near zero, so the threshold sits well clear of both.
        /// </summary>
        private const double WordGapFraction = 0.15;

        /// <summary>
        /// The line's character ranges that are one unbroken run of glyphs — split at the synthetic
        /// word separator and at any real horizontal gap.
        /// </summary>
        /// <remarks>
        /// <para><b>This exists because the extractor's word model loses spaces, and a regex can
        /// only see the string.</b> <c>PdfStructuredTextExtractor</c> does not retain space glyphs
        /// and rebuilds words from horizontal gaps against its own threshold; where that threshold
        /// misses, <c>"Visit https://example.com today"</c> arrives as
        /// <c>"Visit https://example.comtoday"</c>. Measured on a Helvetica 12 pt fixture: the space
        /// before <c>today</c> is a <b>3.34 pt</b> gap that the glyphs record and the string does
        /// not.</para>
        ///
        /// <para><b>The runs are computed before matching, not after</b>, and that ordering is the
        /// whole correctness of it. Truncating a match afterwards fixes only the right-hand end: on
        /// <c>"Write to tamir@example.com now"</c> the address pattern's local part starts at
        /// <c>Write</c>, so the match <i>begins</i> in the wrong place, and on a line with several
        /// URLs one greedy match swallows the rest and the later ones are never seen at all.</para>
        ///
        /// <para><b>Deliberately not in <see cref="TextLineGlyphMap"/>.</b> It is a rule about URLs —
        /// a URL cannot contain a space — not about the text-to-glyph join. Redaction matches
        /// <i>across</i> words on purpose, and a gap rule there would silently narrow every
        /// multi-word redaction.</para>
        /// </remarks>
        private static List<(int Start, int Length)> UnbrokenRuns(PdfTextChar[] map)
        {
            var runs = new List<(int, int)>();
            int start = -1;

            for (int i = 0; i < map.Length; i++)
            {
                PdfTextChar current = map[i];
                if (current == null)                       // the synthetic word separator
                {
                    if (start >= 0) { runs.Add((start, i - start)); start = -1; }
                    continue;
                }

                if (start < 0) { start = i; continue; }

                PdfTextChar previous = map[i - 1];
                double gap = previous == null ? 0 : current.X - (previous.X + previous.Width);
                if (gap > current.Height * WordGapFraction)
                {
                    runs.Add((start, i - start));
                    start = i;
                }
            }

            if (start >= 0) runs.Add((start, map.Length - start));
            return runs;
        }

        /// <summary>
        /// Trims trailing punctuation a URL picked up from the sentence around it, keeping a
        /// balanced closing parenthesis that is part of the URL itself.
        /// </summary>
        private static string TrimTrailing(string value)
        {
            int end = value.Length;
            while (end > 0 && TrailingPunctuation.IndexOf(value[end - 1]) >= 0)
            {
                // A closing bracket is kept when the match opened one — Wikipedia-style URLs end in
                // "(disambiguation)" and trimming that leaves a link to the wrong page.
                char c = value[end - 1];
                if ((c == ')' || c == ']' || c == '}') && IsBalanced(value, end)) break;
                end--;
            }
            return value.Substring(0, end);
        }

        private static bool IsBalanced(string value, int end)
        {
            int open = 0, close = 0;
            for (int i = 0; i < end; i++)
            {
                char c = value[i];
                if (c == '(' || c == '[' || c == '{') open++;
                else if (c == ')' || c == ']' || c == '}') close++;
            }
            return close <= open;
        }

        /// <summary>The URI a matched string names, or null when the options exclude its kind.</summary>
        private static string ToUri(string matched, AutoLinkOptions options)
        {
            if (matched.IndexOf("://", StringComparison.Ordinal) > 0) return matched;

            if (matched.IndexOf('@') > 0)
                return options.DetectEmailAddresses ? "mailto:" + matched : null;

            if (matched.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                return options.DetectBareHosts ? "https://" + matched : null;

            return null;
        }

        /// <summary>
        /// Whether the candidate's box overlaps a link the page already carries, on the same page.
        /// </summary>
        /// <remarks>
        /// Through <see cref="PdfSpatialQuery.RectIntersects"/>, the one box-intersection predicate:
        /// this wrote the same strict-edged test out again.
        /// </remarks>
        private static bool OverlapsExistingLink(
            AutoLinkCandidate candidate, List<Extraction.PdfLink> linksOnPage)
        {
            for (int i = 0; i < linksOnPage.Count; i++)
            {
                var link = linksOnPage[i];
                if (PdfSpatialQuery.RectIntersects(
                        (float)candidate.X, (float)candidate.Y, (float)candidate.Width, (float)candidate.Height,
                        link.X, link.Y, link.Width, link.Height))
                    return true;
            }
            return false;
        }
    }
}
