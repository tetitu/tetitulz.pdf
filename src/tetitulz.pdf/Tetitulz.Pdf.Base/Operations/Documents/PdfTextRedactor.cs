// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>Policy for <see cref="PdfTextRedactor"/>.</summary>
    public sealed class TextRedactionOptions
    {
        /// <summary>Whether the search is case-sensitive. Off by default.</summary>
        public bool MatchCase { get; set; }

        /// <summary>
        /// Whether each pattern is a regular expression rather than a literal. Off by default.
        /// </summary>
        /// <remarks>
        /// A caller-supplied regular expression runs against every line of the document, so it is
        /// bounded by <see cref="RegexTimeout"/> — an expression with catastrophic backtracking is
        /// otherwise a denial of service driven by the caller's own input.
        /// </remarks>
        public bool UseRegex { get; set; }

        /// <summary>How long any one match attempt may take. Defaults to two seconds.</summary>
        public TimeSpan RegexTimeout { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Points added around each match's box before it becomes a redaction area. Defaults to 1.
        /// </summary>
        /// <remarks>
        /// Glyph boxes come from advance widths, which drift by a fraction of a point against the
        /// ink actually painted; a little padding keeps a descender or an italic overhang from
        /// falling outside the area and surviving. The engine also inflates by a margin of its own
        /// when deciding which glyphs to delete, so this is about the visible cover box.
        /// </remarks>
        public float Padding { get; set; } = 1f;

        /// <summary>
        /// Which Hebrew differences to ignore while matching. <b>Off by default</b>, and
        /// deliberately not <see cref="HebrewFolding.Default"/> the way a find box is.
        /// </summary>
        /// <remarks>
        /// <para><b>The same fold, the opposite risk.</b> In a search a miss is the visible failure
        /// and a spurious hit costs a keystroke. Here a spurious hit <b>destroys content
        /// irreversibly</b>, so the default has to be the setting that cannot invent a match. A
        /// caller who knows their document is pointed Hebrew turns it on, and
        /// <see cref="PdfTextRedactor.FindMatches(Stream, System.Collections.Generic.IEnumerable{string}, TextRedactionOptions)"/>
        /// exists so they can see what it found before anything is removed.</para>
        ///
        /// <para><b>It cannot be combined with <see cref="UseRegex"/>, and the refusal is not
        /// caution.</b> Folding removes characters, so a quantifier counts <i>folded</i> ones:
        /// <c>.{3}</c> over pointed Hebrew spans three base letters and every mark between them,
        /// which is a different region of the page than the caller wrote. A literal has no such
        /// gap - its folded form is exactly as long as what it must cover.</para>
        /// </remarks>
        public HebrewFolding HebrewFolding { get; set; } = HebrewFolding.None;

        /// <summary>
        /// Whether the text the document holds outside its pages is cleaned too: each string of the
        /// information dictionary, each outline item's title, and the text of the catalog's XMP
        /// packet lose every match. On by default; the places are named in
        /// <see cref="PdfTextRedactor.LastDocumentTextRemoved"/>.
        /// </summary>
        /// <remarks>
        /// A match there is removed, not covered — there is nothing to draw a box on — so a title
        /// reads around the gap. A packet that cannot be read or parsed within its bounds is removed
        /// whole, since nothing can say what it holds.
        /// </remarks>
        public bool IncludeDocumentText { get; set; } = true;

        /// <summary>Cover-box red channel, 0..1. Defaults to black.</summary>
        public float FillR { get; set; }

        /// <summary>Cover-box green channel, 0..1. Defaults to black.</summary>
        public float FillG { get; set; }

        /// <summary>Cover-box blue channel, 0..1. Defaults to black.</summary>
        public float FillB { get; set; }
    }

    /// <summary>One text match, and the area that would cover it.</summary>
    public sealed class TextRedactionMatch
    {
        internal TextRedactionMatch(int pageIndex, string text, PdfRedactionArea area)
        {
            PageIndex = pageIndex;
            Text = text;
            Area = area;
        }

        /// <summary>Zero-based page the match is on.</summary>
        public int PageIndex { get; }

        /// <summary>The matched text, as extraction produced it.</summary>
        public string Text { get; }

        /// <summary>The area that covers it, ready for the redaction engine.</summary>
        public PdfRedactionArea Area { get; }

        /// <inheritdoc/>
        public override string ToString() =>
            $"page {PageIndex}: '{Text}' at ({Area.X},{Area.Y}) {Area.Width}x{Area.Height}";
    }

    /// <summary>
    /// Finds text by pattern and redacts it — "remove every occurrence of this" rather than
    /// "remove this rectangle".
    /// </summary>
    /// <remarks>
    /// <para><b>A join of three components that all shipped and never met.</b>
    /// <see cref="PdfTextSearch"/> returns a page number and a string but <b>no rectangle</b>;
    /// <see cref="PdfRedactionArea"/> needs one; and <see cref="PdfStructuredTextExtractor"/> has
    /// had per-character geometry all along. This class does the join and hands the result to
    /// <see cref="PdfRedactionEngine"/>.</para>
    /// <para><b>It is a front end, not a second engine, and that is the security point.</b> Every
    /// refusal the engine makes still applies — an encrypted document is still refused, a form
    /// shared with a page the caller did not redact still keeps its original object — because the
    /// removal is still done by the one implementation that knows about them. A text-driven entry
    /// point that reimplemented the removal would be a way around all of it.</para>
    /// <para><b>The search runs over EXTRACTED text and inherits its word splitting.</b> The
    /// extractor ends a word at a horizontal gap or at a space glyph the content stream drew, so
    /// <c>"Top Secret"</c> written with a space is found as written. Two words set close together
    /// with no space glyph at all still come back joined, because that is what the page holds.
    /// Search for what <c>PdfOperations.ExtractText</c> returns, and use
    /// <see cref="FindMatches(Stream, IEnumerable{string}, TextRedactionOptions)"/> to check before
    /// destroying anything. Matching a normalised form instead would invent matches the document
    /// does not contain, which for a redaction is the worse error.</para>
    /// <para><b>A match must lie within one extracted line.</b> A phrase broken across a line wrap
    /// is two runs of glyphs with two boxes and, as far as the page is concerned, two separate
    /// pieces of text; matching across the break would need the line-joining rules extraction
    /// deliberately does not apply. Stated because a caller redacting a long phrase needs to know,
    /// and <see cref="FindMatches(Stream, IEnumerable{string}, TextRedactionOptions)"/> is there so
    /// they can see what was found before anything is destroyed.</para>
    /// <para><b>A result is read back before it is handed over.</b> The search can only find what the
    /// layout can place: text drawn rotated or mirrored, or two runs overlapping, come apart into glyphs
    /// no line holds, so nothing matches and the page keeps them. The plain extractor reads text in the
    /// order it is drawn, whatever its direction, so it reads each page of the result, and a match
    /// within one of its lines refuses the call with nothing written: a caller told "0 matches" of a
    /// word still on the page would believe it. Such text is removed by region
    /// (<see cref="PdfRedactionEngine.Redact(Stream, Stream, IReadOnlyList{PdfRedactionArea})"/>).</para>
    /// <para><b>The search reads the annotation appearances with the pages</b> — a filled field's
    /// value, a free-text note, a stamp's caption, each where a reader shows it — so a match in
    /// one makes an area over it, and the engine drops every annotation an area meets and scrubs
    /// its words; the read-back reads the appearances the result keeps.</para>
    /// <para><b>Beside the pages it cleans the document's own text</b> — the information dictionary,
    /// the outline's titles and the catalog's XMP packet (<see cref="TextRedactionOptions.IncludeDocumentText"/>,
    /// <see cref="LastDocumentTextRemoved"/>) — and nothing else: named destinations, attachments, a
    /// field's value that no page draws, a document's scripts, and an image's or a form's own
    /// metadata keep every occurrence, and the read-back reads the pages. The sanitizer
    /// (<c>Core.Document.PdfSanitizer</c>) takes the attachments and scripts.</para>
    /// </remarks>
    public static class PdfTextRedactor
    {
        [ThreadStatic]
        private static string _lastDocumentTextRemoved;

        /// <summary>
        /// The search reads what a reader sees: the pages' content and the text their annotation
        /// appearances draw — a filled field's value, a free-text note, a stamp's caption — each
        /// where it is shown. A match inside an appearance makes an area over it, and the engine
        /// drops every annotation an area meets.
        /// </summary>
        private static readonly WordAssemblyOptions WithAnnotations = new WordAssemblyOptions { IncludeAnnotationAppearances = true };

        /// <summary>
        /// Where the last <see cref="Redact(Stream, Stream, IEnumerable{string}, TextRedactionOptions)"/>
        /// or <see cref="RedactIsraeliIdentifiers"/> on this thread removed text outside the pages —
        /// <c>/Info /Title</c>, an outline item's title, the XMP packet — or <see langword="null"/> when
        /// it removed none there. It names the places, never the text. Reset at the start of every call.
        /// </summary>
        public static string LastDocumentTextRemoved => _lastDocumentTextRemoved;

        /// <summary>
        /// How many times one document string is searched again after its matches are removed — a
        /// removal can join what stood either side into another match. A string still matching after
        /// as many is emptied: a file can nest matches so that each pass removes one.
        /// </summary>
        internal const int MaxCleaningPasses = 64;

        /// <summary>
        /// Finds every occurrence of the patterns and reports the areas that would cover them —
        /// <b>without changing the document</b>.
        /// </summary>
        /// <param name="input">The PDF to search. Not disposed.</param>
        /// <param name="patterns">Literals, or regular expressions when the options say so.</param>
        /// <param name="options">Search policy; null uses the defaults.</param>
        /// <remarks>
        /// Redaction destroys content irreversibly and a pattern is easy to get wrong, so the
        /// review step is a first-class entry point rather than something a caller has to
        /// reconstruct.
        /// </remarks>
        public static IReadOnlyList<TextRedactionMatch> FindMatches(
            Stream input, IEnumerable<string> patterns, TextRedactionOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (patterns == null) throw new ArgumentNullException(nameof(patterns));
            options ??= new TextRedactionOptions();

            var regexes = Compile(patterns, options);
            var matches = new List<TextRedactionMatch>();
            if (regexes.Count == 0) return matches;

            var layouts = PdfStructuredTextExtractor.Extract(input, WithAnnotations);
            foreach (var page in layouts)
                foreach (var line in page.Lines)
                    CollectLine(page.PageNumber, line, regexes, options, matches);

            return matches;
        }

        /// <summary>
        /// Finds every occurrence of <paramref name="pattern"/> and reports the covering areas.
        /// </summary>
        /// <param name="input">The PDF to search. Not disposed.</param>
        /// <param name="pattern">A literal, or a regular expression when the options say so.</param>
        /// <param name="options">Search policy; null uses the defaults.</param>
        public static IReadOnlyList<TextRedactionMatch> FindMatches(
            Stream input, string pattern, TextRedactionOptions options = null)
            => FindMatches(input, new[] { pattern }, options);

        /// <summary>
        /// Removes every occurrence of the patterns, writing a full single-revision PDF.
        /// </summary>
        /// <param name="input">The PDF to redact. Not disposed.</param>
        /// <param name="output">Where the redacted PDF is written. Not disposed.</param>
        /// <param name="patterns">Literals, or regular expressions when the options say so.</param>
        /// <param name="options">Search policy; null uses the defaults.</param>
        /// <returns>What was found and removed, so the caller can record it.</returns>
        /// <exception cref="PdfRedactionException">
        /// The engine refused — an encrypted document, or content it cannot safely remove.
        /// </exception>
        /// <remarks>
        /// <b>Nothing matched means nothing is written but a copy</b> — the engine's own behaviour
        /// for an empty area list, and byte-identical to the input. It matters because rewriting a
        /// document as a single revision discards its incremental history, and any signature over
        /// it, to accomplish nothing.
        /// </remarks>
        public static IReadOnlyList<TextRedactionMatch> Redact(
            Stream input, Stream output, IEnumerable<string> patterns,
            TextRedactionOptions options = null)
        {
            _lastDocumentTextRemoved = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (patterns == null) throw new ArgumentNullException(nameof(patterns));

            // Read twice, by the search and by the read-back, and a caller's sequence may run once.
            patterns = new List<string>(patterns);

            var source = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool owned);
            try
            {
                // Refused before the search rather than by reaching the engine, so a large
                // encrypted document is not extracted first.
                //
                // There is no "nothing matched, copy the input" branch before this refusal: with one,
                // an encrypted input would return successfully having done nothing — a text-driven
                // entry point quietly ignoring a document the rectangle-driven one
                // refuses. PdfRedactionEngine.Redact tests IsEncrypted BEFORE it looks at the area
                // list, so this call decides only WHEN the refusal happens: kept for the ordering,
                // not mistaken for the guarantee.
                RefuseEncrypted(source);

                var matches = FindMatches(source, patterns, options);

                var areas = new List<PdfRedactionArea>(matches.Count);
                foreach (var m in matches) areas.Add(m.Area);

                // The one engine, with every refusal it makes still in force — and with its own
                // empty-list behaviour, which is to copy the input unchanged.
                //
                // There was a "nothing matched, copy the input" branch here until a mutation
                // showed deleting it changed nothing: the engine already does exactly that, byte
                // for byte. That is the "surviving mutation means the line does nothing" case, not
                // a coverage gap — and a redundant guard against a real hazard is the hardest kind
                // to see through, because the hazard is genuine and the guard is inert. The test
                // that covered it now pins the engine's behaviour through this entry point, which
                // is the claim that actually matters to a caller.
                options ??= new TextRedactionOptions();
                List<Regex> regexes = Compile(patterns, options);
                Func<string, string> documentText = null;
                if (options.IncludeDocumentText && regexes.Count > 0)
                {
                    TimeSpan timeout = options.RegexTimeout;
                    documentText = text => RemoveEveryMatch(text, s => PatternSpans(s, regexes, options), timeout);
                }
                RedactAndReadBack(source, output, areas, line => MatchesAny(line, regexes, options), documentText);
                return matches;
            }
            finally
            {
                if (owned) source.Dispose();
            }
        }

        /// <summary>
        /// Removes every occurrence of <paramref name="pattern"/>.
        /// </summary>
        /// <param name="input">The PDF to redact. Not disposed.</param>
        /// <param name="output">Where the redacted PDF is written. Not disposed.</param>
        /// <param name="pattern">A literal, or a regular expression when the options say so.</param>
        /// <param name="options">Search policy; null uses the defaults.</param>
        public static IReadOnlyList<TextRedactionMatch> Redact(
            Stream input, Stream output, string pattern, TextRedactionOptions options = null)
            => Redact(input, output, new[] { pattern }, options);

        // ----------------------------------------------------------------
        //  Israeli identifiers
        // ----------------------------------------------------------------

        /// <summary>
        /// Finds every nine-digit Israeli identifier on the page and reports the areas that would
        /// cover them - <b>without changing the document</b>.
        /// </summary>
        /// <param name="input">The PDF to search. Not disposed.</param>
        /// <param name="options">
        /// Box appearance (<see cref="TextRedactionOptions.Padding"/> and the fill channels). The
        /// three matching members do not apply to a scan for digits and are refused rather than
        /// ignored - see the exception below.
        /// </param>
        /// <returns>
        /// One match per identifier, with the digits as they appear on the page in
        /// <see cref="TextRedactionMatch.Text"/>.
        /// </returns>
        /// <exception cref="PdfRedactionException">
        /// <see cref="TextRedactionOptions.MatchCase"/>, <see cref="TextRedactionOptions.UseRegex"/>
        /// or <see cref="TextRedactionOptions.HebrewFolding"/> is set.
        /// </exception>
        /// <remarks>
        /// <para><b>Review these before redacting them, and the reason is arithmetic.</b>
        /// <see cref="Core.Text.IsraeliIdentifiers"/>' check digit is one decimal digit, so about
        /// <b>one in ten</b> arbitrary nine-digit numbers passes it. An invoice number, an order
        /// reference or a phone number written without separators will sometimes be reported here,
        /// and no amount of care in this method changes that - the information needed to tell them
        /// apart is not in the digits. That is why this is a <c>Find</c> and
        /// <see cref="RedactIsraeliIdentifiers"/> is separate.</para>
        ///
        /// <para><b>What is deliberately not searched for.</b> Bank account and vehicle numbers are
        /// named in the same register row and are absent because they have no national check digit
        /// and no single format - a locator for them would be a regular expression over digit runs,
        /// which reports every number on the page. Naming the limit is more useful than shipping a
        /// filter that does not filter.</para>
        /// </remarks>
        public static IReadOnlyList<TextRedactionMatch> FindIsraeliIdentifiers(
            Stream input, TextRedactionOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            options ??= new TextRedactionOptions();
            RefuseMatchingOptions(options);

            var matches = new List<TextRedactionMatch>();
            foreach (var page in PdfStructuredTextExtractor.Extract(input, WithAnnotations))
                foreach (var line in page.Lines)
                    CollectIdentifiers(page.PageNumber, line, options, matches);

            return matches;
        }

        /// <summary>
        /// Removes every nine-digit Israeli identifier found by
        /// <see cref="FindIsraeliIdentifiers"/>, writing a full single-revision PDF.
        /// </summary>
        /// <param name="input">The PDF to redact. Not disposed.</param>
        /// <param name="output">Where the redacted PDF is written. Not disposed.</param>
        /// <param name="options">Box appearance; null uses the defaults.</param>
        /// <returns>What was found and removed, so the caller can record it.</returns>
        /// <remarks>
        /// <b>Read <see cref="FindIsraeliIdentifiers"/>' remarks before calling this.</b> Roughly
        /// one nine-digit number in ten passes the check digit by chance, and this destroys what it
        /// finds.
        /// </remarks>
        public static IReadOnlyList<TextRedactionMatch> RedactIsraeliIdentifiers(
            Stream input, Stream output, TextRedactionOptions options = null)
        {
            _lastDocumentTextRemoved = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var source = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool owned);
            try
            {
                // Early, so a large encrypted document is refused before it is extracted rather
                // than after. It is NOT what makes the refusal happen: PdfRedactionEngine.Redact
                // tests IsEncrypted before it looks at the area list, so an empty list reaches the
                // same exception with the same wording. Mutation says so - deleting either copy
                // leaves both redaction suites green - and the comment on the pattern path above
                // once claimed otherwise.
                RefuseEncrypted(source);

                var matches = FindIsraeliIdentifiers(source, options);

                var areas = new List<PdfRedactionArea>(matches.Count);
                foreach (var m in matches) areas.Add(m.Area);

                Func<string, string> documentText = (options ?? new TextRedactionOptions()).IncludeDocumentText
                    ? text => RemoveEveryMatch(text, IdentifierSpans, Regex.InfiniteMatchTimeout)
                    : (Func<string, string>)null;
                RedactAndReadBack(source, output, areas, HoldsValidIdentifier, documentText);
                return matches;
            }
            finally
            {
                if (owned) source.Dispose();
            }
        }

        /// <summary>
        /// The engine's result, written to <paramref name="output"/> only once the plain extractor finds
        /// nothing in it that <paramref name="stillThere"/> answers for. See the class's remarks.
        /// </summary>
        private static void RedactAndReadBack(
            Stream source, Stream output, IReadOnlyList<PdfRedactionArea> areas, Func<string, bool> stillThere,
            Func<string, string> documentText)
        {
            using var result = new MemoryStream();
            source.Position = 0;
            var removedFrom = new Core.Util.DistinctStrings();
            PdfRedactionEngine.Redact(source, result, areas, documentText, removedFrom);

            int foundOnPage = -1;
            try
            {
                using var doc = Core.Document.PdfDocument.Open(new MemoryStream(result.GetBuffer(), 0, (int)result.Length, writable: false));
                Core.Text.PdfTextExtractor.ExtractText(doc, (pageIndex, text) =>
                {
                    if (foundOnPage >= 0 || string.IsNullOrEmpty(text)) return;
                    foreach (string line in text.Split('\n'))
                        if (stillThere(line.TrimEnd('\r'))) { foundOnPage = pageIndex; return; }
                });
                // The annotation appearances too: the plain extractor reads the pages' content, and
                // a match the search found in an appearance is still there unless the engine dropped
                // the annotation.
                for (int pageIndex = 0; foundOnPage < 0 && pageIndex < doc.Pages.Count; pageIndex++)
                    foreach (PdfTextLine line in PdfStructuredTextExtractor.AnnotationLines(doc, pageIndex))
                        if (stillThere(line.Text ?? string.Empty)) { foundOnPage = pageIndex; break; }
            }
            catch (RegexMatchTimeoutException ex)
            {
                throw new PdfRedactionException(
                    "The result could not be read back for what was to be removed, so it was not written: the pattern " +
                    "ran past TextRedactionOptions.RegexTimeout (" + ex.MatchTimeout + ").");
            }

            if (foundOnPage >= 0)
                throw new PdfRedactionException(
                    "Text this was asked to remove is still on page " + (foundOnPage + 1) + " of the result, so nothing was " +
                    "written. It is drawn where the search could not place it — rotated, mirrored or overlapping — or by an " +
                    "annotation the regions did not meet. Remove it by region through PdfRedactionEngine.Redact.");

            result.Position = 0;
            result.CopyTo(output);
            if (removedFrom.Count > 0) _lastDocumentTextRemoved = removedFrom.Joined("; ");
        }

        /// <summary>
        /// <paramref name="text"/> without what <paramref name="find"/> reports, searched again until it
        /// reports nothing (<see cref="MaxCleaningPasses"/>); a pattern that runs past its timeout
        /// refuses the call, as the read-back does.
        /// </summary>
        private static string RemoveEveryMatch(string text, Func<string, List<(int Start, int Length)>> find, TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string current = text;
            try
            {
                for (int pass = 0; pass < MaxCleaningPasses; pass++)
                {
                    List<(int Start, int Length)> spans = find(current);
                    spans.RemoveAll(s => s.Length <= 0);
                    if (spans.Count == 0) return current;
                    spans.Sort((a, b) => a.Start.CompareTo(b.Start));

                    var kept = new StringBuilder(current.Length);
                    int at = 0;
                    foreach (var (start, length) in spans)
                    {
                        if (start > at) kept.Append(current, at, start - at);
                        at = Math.Max(at, start + length);
                    }
                    if (at < current.Length) kept.Append(current, at, current.Length - at);
                    current = kept.ToString();
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new PdfRedactionException(
                    "The document's own text could not be searched for what was to be removed, so nothing was written: the " +
                    "pattern ran past TextRedactionOptions.RegexTimeout (" + timeout + ").");
            }
            return find(current).Exists(s => s.Length > 0) ? "" : current;
        }

        /// <summary>Where the patterns match <paramref name="text"/>, in its own positions however it folds.</summary>
        private static List<(int Start, int Length)> PatternSpans(string text, List<Regex> regexes, TextRedactionOptions options)
        {
            var spans = new List<(int Start, int Length)>();
            string haystack = text;
            int[] sourceIndex = null;
            if (options.HebrewFolding != HebrewFolding.None)
            {
                haystack = HebrewSearchNormalizer.Fold(text, options.HebrewFolding, out sourceIndex);
                if (haystack.Length == 0) return spans;
            }
            foreach (Regex regex in regexes)
                foreach (Match match in regex.Matches(haystack))
                    spans.Add(InSourceCoordinates(match, sourceIndex));
            return spans;
        }

        /// <summary>Where a valid Israeli identifier stands in <paramref name="text"/>.</summary>
        private static List<(int Start, int Length)> IdentifierSpans(string text)
        {
            var spans = new List<(int Start, int Length)>();
            foreach (var (index, length) in Core.Text.IsraeliIdentifiers.Candidates(text, spaceJoins: false))
                if (Core.Text.IsraeliIdentifiers.IsValid(text.Substring(index, length))) spans.Add((index, length));
            return spans;
        }

        private static bool MatchesAny(string line, List<Regex> regexes, TextRedactionOptions options)
        {
            string haystack = options.HebrewFolding == HebrewFolding.None
                ? line
                : HebrewSearchNormalizer.Fold(line, options.HebrewFolding, out _);
            foreach (Regex regex in regexes)
                if (regex.IsMatch(haystack)) return true;
            return false;
        }

        private static bool HoldsValidIdentifier(string line)
        {
            foreach (var (index, length) in Core.Text.IsraeliIdentifiers.Candidates(line, spaceJoins: false))
                if (Core.Text.IsraeliIdentifiers.IsValid(line.Substring(index, length))) return true;
            return false;
        }

        /// <summary>
        /// Refuses the members of <see cref="TextRedactionOptions"/> that a digits-only scan cannot
        /// act on.
        /// </summary>
        /// <remarks>
        /// All three are inert here - there are no letters to case-fold, no pattern to compile, and
        /// nothing Hebrew in a run of digits - and an option that is silently inert is worse than
        /// one that is refused: the caller believes it applied. The options type is shared rather
        /// than duplicated because the half that <i>does</i> apply, the cover box, is the half that
        /// would drift if there were two of it.
        /// </remarks>
        private static void RefuseMatchingOptions(TextRedactionOptions options)
        {
            if (!options.MatchCase && !options.UseRegex &&
                options.HebrewFolding == HebrewFolding.None)
                return;

            throw new PdfRedactionException(
                "MatchCase, UseRegex and HebrewFolding do not apply to an identifier scan: it " +
                "matches runs of digits validated by a check digit, not text. Only Padding and " +
                "the fill channels are read. They are refused rather than ignored so a caller " +
                "cannot believe a setting took effect.");
        }

        /// <summary>
        /// Runs the identifier scan over one line and turns each validated hit into an area.
        /// </summary>
        /// <remarks>
        /// The candidates are <see cref="Core.Text.IsraeliIdentifiers.Candidates"/>' — the scanner
        /// the invoice extractor reads supplier numbers with — with groups joined by hyphens only:
        /// a redaction is destructive, and two numbers a space apart are two numbers here.
        /// </remarks>
        private static void CollectIdentifiers(
            int pageIndex, PdfTextLine line, TextRedactionOptions options,
            List<TextRedactionMatch> into)
        {
            (string text, PdfTextChar[] map) = LineTextWithMap(line);
            if (text.Length == 0) return;

            foreach (var (index, length) in Core.Text.IsraeliIdentifiers.Candidates(text, spaceJoins: false))
            {
                string value = text.Substring(index, length);
                if (!Core.Text.IsraeliIdentifiers.IsValid(value)) continue;

                PdfRedactionArea area = AreaFor(pageIndex, map, index, length, options);
                if (area != null) into.Add(new TextRedactionMatch(pageIndex, value, area));
            }
        }

        /// <summary>
        /// Refuses an encrypted document with the engine's own reason, before any work is done.
        /// </summary>
        /// <remarks>
        /// The same exception type and the same wording as
        /// <see cref="PdfRedactionEngine.Redact(Stream, Stream, IReadOnlyList{PdfRedactionArea})"/>, so a caller cannot tell which entry point
        /// refused — because for this purpose there is no difference and they must not diverge.
        /// </remarks>
        private static void RefuseEncrypted(Stream source)
        {
            source.Position = 0;
            using var doc = Core.Document.PdfDocument.Open(source);
            if (doc.IsEncrypted)
                throw new PdfRedactionException(
                    "Encrypted PDFs are not supported by true redaction; decrypt the document first.");
        }

        // ----------------------------------------------------------------
        //  Matching
        // ----------------------------------------------------------------

        private static List<Regex> Compile(IEnumerable<string> patterns, TextRedactionOptions options)
        {
            if (options.HebrewFolding != HebrewFolding.None && options.UseRegex)
                throw new PdfRedactionException(
                    "HebrewFolding cannot be combined with UseRegex: folding removes characters, " +
                    "so a quantifier would count folded ones and cover a different region of the " +
                    "page than the expression describes. Fold the expression yourself, or search " +
                    "for literals.");

            var regexes = new List<Regex>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string pattern in patterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;

                // The pattern is folded too, or it would only match text that already agreed with
                // the caller's spelling - which is the thing folding exists to stop mattering.
                // De-duplication is on the FOLDED form: two spellings of one name are one pattern
                // once folded, and compiling both would report every hit twice.
                string expression = options.HebrewFolding == HebrewFolding.None
                    ? pattern
                    : HebrewSearchNormalizer.Fold(pattern, options.HebrewFolding);

                if (expression.Length == 0)
                    throw new PdfRedactionException(
                        "The pattern '" + pattern + "' folds away to nothing under HebrewFolding " +
                        options.HebrewFolding + ", so it names no text at all. Redaction is " +
                        "irreversible, so a pattern the options erased is refused rather than " +
                        "dropped quietly.");

                if (!seen.Add(expression)) continue;

                var flags = RegexOptions.CultureInvariant;
                if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;

                if (!options.UseRegex) expression = Regex.Escape(expression);

                // A caller-supplied expression runs over every line of the document, so a
                // catastrophic-backtracking pattern is bounded rather than allowed to hang the
                // call. An escaped literal cannot backtrack, but the timeout costs it nothing.
                regexes.Add(new Regex(expression, flags, options.RegexTimeout));
            }

            return regexes;
        }

        /// <summary>
        /// Runs the patterns over one line's text and turns each match into a covering area.
        /// </summary>
        private static void CollectLine(
            int pageIndex, PdfTextLine line, List<Regex> regexes,
            TextRedactionOptions options, List<TextRedactionMatch> into)
        {
            (string text, PdfTextChar[] map) = LineTextWithMap(line);
            if (text.Length == 0) return;

            // The regexes run over the FOLDED line, and every position they report is translated
            // back through sourceIndex before it reaches a glyph box. Folding the line without the
            // map would cover the wrong glyphs - by exactly the number of marks stripped before the
            // hit, which is a whole word or more on a fully pointed line.
            string haystack = text;
            int[] sourceIndex = null;
            if (options.HebrewFolding != HebrewFolding.None)
            {
                haystack = HebrewSearchNormalizer.Fold(text, options.HebrewFolding, out sourceIndex);
                if (haystack.Length == 0) return;
            }

            foreach (Regex regex in regexes)
            {
                Match match;
                try
                {
                    match = regex.Match(haystack);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Reported by omission rather than by throwing: one pathological line must not
                    // lose the matches found everywhere else, and FindMatches is the review step a
                    // caller checks before destroying anything.
                    continue;
                }

                while (match.Success)
                {
                    (int start, int length) = InSourceCoordinates(match, sourceIndex);

                    PdfRedactionArea area = AreaFor(pageIndex, map, start, length, options);
                    // The reported text is the ORIGINAL substring, not match.Value: what sits on
                    // the page is the pointed spelling, and a caller reviewing FindMatches before
                    // destroying anything needs to read what will actually be removed.
                    if (area != null)
                        into.Add(new TextRedactionMatch(
                            pageIndex, text.Substring(start, length), area));

                    if (match.Length == 0) break;      // a zero-width match would not advance
                    try { match = regex.Match(haystack, match.Index + match.Length); }
                    catch (RegexMatchTimeoutException) { break; }
                }
            }
        }

        /// <summary>
        /// Where a match found in the folded line sits in the original one.
        /// </summary>
        /// <remarks>
        /// <c>start + foldedLength</c> is wrong whenever anything was stripped <i>inside</i> the
        /// match, which for pointed Hebrew is most of the time, so the end comes from the map too.
        /// A zero-width match has no last character to look up and stays where it began.
        /// </remarks>
        private static (int start, int length) InSourceCoordinates(Match match, int[] sourceIndex)
        {
            if (sourceIndex == null) return (match.Index, match.Length);
            if (match.Length == 0) return (sourceIndex[match.Index], 0);

            int start = sourceIndex[match.Index];
            int end = sourceIndex[match.Index + match.Length - 1] + 1;
            return (start, end - start);
        }

        /// <summary>
        /// A line's text together with, for each character position, the glyph that produced it -
        /// <c>null</c> for the space this inserts between words.
        /// </summary>
        /// <remarks>
        /// <b>Built here rather than read from <see cref="PdfTextLine.Text"/>, because the map is
        /// the whole point.</b> A match's position in the string has to name the glyphs it covers,
        /// and a string alone cannot say which. The separator matches what the extractor puts in
        /// <c>Text</c>, so a caller searching the extracted text finds the same matches here.
        /// </remarks>
        /// <remarks>
        /// <b>The join is <see cref="Extraction.TextLineGlyphMap"/>'s</b>, shared with the
        /// autolinker, which needs the same text→box rule; a second copy is this repository's named recurring defect, and this map is subtle exactly
        /// where a re-implementation goes wrong — multi-character clusters, and the synthetic
        /// separator that must be skipped rather than unioned.
        /// </remarks>
        private static (string text, PdfTextChar[] map) LineTextWithMap(PdfTextLine line)
            => Extraction.TextLineGlyphMap.Build(line);

        /// <summary>
        /// The union of the boxes of the glyphs a match covers, padded, as a redaction area.
        /// </summary>
        /// <remarks>
        /// Positions that map to no glyph — the synthetic word separator — are skipped rather than
        /// treated as zero-width boxes at the origin, which would stretch every multi-word match
        /// back to the bottom-left corner of the page.
        /// </remarks>
        private static PdfRedactionArea AreaFor(
            int pageIndex, PdfTextChar[] map, int start, int length, TextRedactionOptions options)
        {
            // Only the padding and the PdfRedactionArea construction are local; the union —
            // including skipping positions that map to no glyph — is TextLineGlyphMap.TryUnionBox,
            // shared with the autolinker so the two cannot disagree about where a match sits.
            if (!Extraction.TextLineGlyphMap.TryUnionBox(map, start, length,
                    out float x0, out float y0, out float x1, out float y1))
                return null;

            float pad = Math.Max(options.Padding, 0);
            return new PdfRedactionArea
            {
                PageIndex = pageIndex,
                X = x0 - pad,
                Y = y0 - pad,
                Width = (x1 - x0) + pad * 2,
                Height = (y1 - y0) + pad * 2,
                FillR = options.FillR,
                FillG = options.FillG,
                FillB = options.FillB,
            };
        }
    }
}
