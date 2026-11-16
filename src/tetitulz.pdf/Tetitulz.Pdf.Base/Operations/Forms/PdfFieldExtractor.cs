// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>Which side of a label its value sits on (Auto picks by the label's reading direction).</summary>
    public enum FieldValueSide
    {
        /// <summary>Left for an RTL label, right for an LTR label.</summary>
        Auto,
        /// <summary>The value is to the left of the label (typical for Hebrew/Arabic labels).</summary>
        Left,
        /// <summary>The value is to the right of the label (typical for Latin labels).</summary>
        Right,
        /// <summary>The value is on the line directly below the label.</summary>
        Below,
    }

    /// <summary>Which of several equally good matches a <see cref="FieldQuery"/> takes.</summary>
    public enum FieldOccurrence
    {
        /// <summary>The first in reading order: pages in order, lines top to bottom, and along a line as it reads.</summary>
        First,
        /// <summary>
        /// The last in reading order — a grand total printed after the subtotals carried from page
        /// to page.
        /// </summary>
        Last,
    }

    /// <summary>What a <see cref="FieldQuery"/>'s value is expected to be.</summary>
    public enum FieldValueKind
    {
        /// <summary>Any text: the value ends at the first column-wide gap.</summary>
        Text,

        /// <summary>
        /// An amount: the value reads on across column-wide gaps until it holds a number that is
        /// not a percentage — a VAT line prints its rate by the label and the amount a column
        /// away — and ends at the first gap after that. A run on the label's line that never
        /// reaches an amount is not the value while a neighbouring line holds one. A row of column
        /// totals whose last column is the sum of the others reads as that last column; a second
        /// amount with decimals beside the first is the next column however narrow the gap.
        /// </summary>
        Amount,
    }

    /// <summary>A single label to locate, with one or more synonym strings (Hebrew + English, …).</summary>
    public sealed class FieldQuery
    {
        /// <summary>Caller-defined key returned on the matching <see cref="FieldResult"/>.</summary>
        public string Key { get; set; }

        /// <summary>
        /// Label synonyms to search for. Matched as a substring of the line, ignoring spaces,
        /// colons, bidi controls, case, Hebrew points, and the quote/apostrophe look-alikes
        /// (<see cref="InvoiceValueParser.NormalizePunctuation"/>) — so <c>סה"כ</c> typed with an
        /// ASCII quote matches a page that sets it with the gershayim.
        /// </summary>
        public IReadOnlyList<string> Labels { get; set; }

        /// <summary>
        /// Phrases that disqualify a label match they contain: a query for <c>מע"מ</c> (VAT) with
        /// <c>לפני מע"מ</c> excluded does not take the "total before VAT" line. Matched the way
        /// <see cref="Labels"/> are. An occurrence of the label outside every excluded phrase still
        /// counts, so a line carrying both the subtotal and the VAT still yields the VAT. A
        /// bracketed qualifier after the label holding one — <c>סה"כ (לפני מע"מ):</c> — is no part
        /// of the label either.
        /// </summary>
        public IReadOnlyList<string> ExcludeLabels { get; set; }

        /// <summary>Where the value sits relative to the label. Default <see cref="FieldValueSide.Auto"/>.</summary>
        public FieldValueSide Side { get; set; } = FieldValueSide.Auto;

        /// <summary>
        /// Which match wins when several are equally good. Default <see cref="FieldOccurrence.First"/>.
        /// </summary>
        public FieldOccurrence Occurrence { get; set; } = FieldOccurrence.First;

        /// <summary>What the value is expected to be. Default <see cref="FieldValueKind.Text"/>.</summary>
        public FieldValueKind ValueKind { get; set; } = FieldValueKind.Text;

        /// <summary>
        /// Whether a label must match whole words: an occurrence that begins after a letter of the
        /// word it starts in, or ends before a letter of the word it ends in, does not count.
        /// Default <c>false</c>, under which such an occurrence still counts but loses to one that
        /// does not.
        /// </summary>
        /// <remarks>
        /// For a short label that ordinary words contain: <c>VAT</c> is inside "Private" and
        /// "Renovation", and <c>תאריך</c> (date) inside <c>בתאריך</c> ("on the date", in a payment
        /// note). Digits and punctuation are not letters, so <c>VAT17%</c> and <c>Total:118.00</c>
        /// still match.
        /// </remarks>
        public bool WholeWord { get; set; }

        public FieldQuery() { }

        public FieldQuery(string key, params string[] labels)
        {
            Key = key;
            Labels = labels;
        }
    }

    /// <summary>The value located for a <see cref="FieldQuery"/>.</summary>
    public sealed class FieldResult
    {
        public string Key { get; set; }
        /// <summary>The label synonym that matched, or <c>null</c> when nothing matched.</summary>
        public string Label { get; set; }
        /// <summary>The extracted value text, in reading order (empty when not found).</summary>
        public string Value { get; set; } = string.Empty;
        /// <summary>Zero-based page index of the match, or -1 when not found.</summary>
        public int PageIndex { get; set; } = -1;
        /// <summary>Bounding box of the value in PDF page coordinates.</summary>
        public (float X, float Y, float Width, float Height) ValueBox { get; set; }
        /// <summary>
        /// Heuristic confidence: 1 = on the label's line, on its value side; 0.9 = on the label's
        /// line, on the other side (only when the side is <see cref="FieldValueSide.Auto"/>); 0.6 =
        /// a spatial neighbour (the line below, or another line beside the label); 0.3 = an
        /// <see cref="FieldValueKind.Amount"/> query's run on the label's line that holds no amount,
        /// with none nearby either (a VAT rate whose amount is elsewhere); 0 = not found.
        /// </summary>
        public float Confidence { get; set; }
    }

    /// <summary>
    /// Data-driven label to value field extraction over a PDF's structured text.  The caller supplies
    /// the label strings to look for (e.g. Hebrew "מספר חשבונית" / English "Invoice No"); this class
    /// locates each label and reads the adjacent value using the RTL-aware <see cref="PdfSpatialQuery"/>.
    /// It ships <em>no</em> hard-coded invoice schema — what counts as "invoice number" or "total" is
    /// entirely the caller's choice (KISS). <see cref="IsraeliInvoiceExtractor"/> is one such choice.
    /// </summary>
    /// <remarks>
    /// <para><b>Which match wins.</b> A value on the label's own line beats a spatial neighbour.
    /// Among those, a match that begins and ends on word boundaries beats one inside a longer
    /// word (<c>Total</c> in "Subtotal"). Then the match whose label accounts for <b>more of its
    /// line's words</b> wins, the value's own words not counted: a query for <c>סה"כ</c> (total)
    /// prefers the line <c>סה"כ 1,180.00</c> to <c>סה"כ לפני מע"מ 1,000.00</c> (total before
    /// VAT), where the label is one word of three — and a currency word printed after an amount
    /// does not make its line less specific. What is left is decided by
    /// <see cref="FieldQuery.Occurrence"/>, in reading order — along a line as well as down the
    /// page.</para>
    /// <para><b>Where a value ends.</b> The value is the run of words on the value side of the
    /// label, nearest first, ending at a gap wider than <see cref="ValueGapEm"/> word heights or at
    /// the next label — a word with letters that starts or ends with a colon, or a word of any
    /// query's label in the same call — so <c>מס' חשבונית: 1001   תאריך: 10/06/2026</c> yields
    /// <c>1001</c> for the invoice number, not the rest of the line. An
    /// <see cref="FieldValueKind.Amount"/> reads on past gaps until it has its number. A label's
    /// own bracketed qualifier, closed by the label's colon — <c>מספר הקצאה (חשבוניות ישראל):</c> —
    /// is label text, not the next label; one holding a phrase the query excludes is not. So is a
    /// currency the label's colon closes (<c>סה"כ לתשלום בש"ח:</c>).</para>
    /// <para><b>A value set in the label's own word.</b> A label that ends inside a word whose rest
    /// has no letters — <c>Invoice #1001</c>, <c>Total:118.00</c> — leaves that rest as the first
    /// word of the value. A word whose rest has letters is label text: <c>Total</c> found in
    /// "SubTotal".</para>
    /// <para><b>Bounded.</b> Every label occurrence is a candidate, and one with no value on its
    /// line searches its page; the work is charged against
    /// <see cref="PdfLimits.MaxFieldExtractionWork"/>, and a call that runs out answers from what
    /// it read and says so in <see cref="LastError"/>.</para>
    /// </remarks>
    public static class PdfFieldExtractor
    {
        /// <summary>
        /// The widest gap, in word heights, between two words of one value. Wider is a column
        /// boundary: the next field, not more of this one.
        /// </summary>
        internal const float ValueGapEm = 2f;

        /// <summary>
        /// The confidence of an <see cref="FieldValueKind.Amount"/> query's run that holds no
        /// amount: below every neighbour that does, above nothing found.
        /// </summary>
        internal const float NoAmountConfidence = 0.3f;

        /// <summary>
        /// Last non-fatal issue (e.g. null arguments, or the work budget running out). <c>null</c>
        /// after a clean run.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent extraction on another thread
        /// cannot overwrite it — read it on the thread that ran the call (see the Diagnostics
        /// Pattern section of CLAUDE.md).
        /// </para>
        /// </summary>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        [System.ThreadStatic]
        private static string _lastError;

        /// <summary>
        /// Extracts fields from an already-extracted set of page layouts. For best results the layouts
        /// should be produced with <see cref="WordAssemblyOptions.ApplyBidi"/> enabled.
        /// </summary>
        public static IReadOnlyList<FieldResult> Extract(
            IReadOnlyList<PdfPageTextLayout> pages, IReadOnlyList<FieldQuery> queries)
        {
            LastError = null;
            var results = new List<FieldResult>();
            if (pages == null || queries == null) { LastError = "pages or queries is null"; return results; }

            // Every line is folded once; each query searches the folded lines.
            var prepared = Prepare(pages, queries);
            foreach (var q in queries)
                results.Add(prepared.Best(q));
            if (prepared.Incomplete) LastError = prepared.IncompleteReason;
            return results;
        }

        /// <summary>
        /// The pages' lines keyed once, for a caller that asks several questions of one document —
        /// <see cref="IsraeliInvoiceExtractor"/> among them, which also needs every match of a
        /// query rather than the best one. Every label of <paramref name="queries"/> becomes a
        /// place a value ends. The work of every question asked is charged to one budget of
        /// <see cref="PdfLimits.MaxFieldExtractionWork"/>.
        /// </summary>
        internal static Prepared Prepare(IReadOnlyList<PdfPageTextLayout> pages, IReadOnlyList<FieldQuery> queries)
            => Prepare(pages, queries, PdfLimits.MaxFieldExtractionWork);

        /// <summary>
        /// <see cref="Prepare(IReadOnlyList{PdfPageTextLayout}, IReadOnlyList{FieldQuery})"/> under
        /// a budget of <paramref name="workUnits"/> — a parameter, so a test reaches the ceiling with
        /// a page it can build.
        /// </summary>
        internal static Prepared Prepare(IReadOnlyList<PdfPageTextLayout> pages, IReadOnlyList<FieldQuery> queries,
                                         int workUnits)
        {
            var prepared = new Prepared(KeyLines(TextLayouts.WithoutNulls(pages)), new WalkBudget(workUnits), workUnits);
            foreach (var q in queries ?? Array.Empty<FieldQuery>())
            {
                if (q == null) continue;
                foreach (var (_, key) in LabelKeys(q.Labels))
                    foreach (var kl in prepared.Lines)
                        for (int at = kl.Key.IndexOf(key, StringComparison.Ordinal);
                             at >= 0;
                             at = kl.Key.IndexOf(key, at + 1, StringComparison.Ordinal))
                        {
                            if (!prepared.Budget.Take(key.Length)) return prepared;
                            if (q.WholeWord && kl.Boundaries(at, key.Length) < 2) continue;
                            foreach (int wi in kl.Shape(at, key.Length, label: 0).LabelWords)
                                prepared.KnownLabelWords.Add(kl.Line.Words[wi]);
                        }
            }
            return prepared;
        }

        // A query's labels with their keys; blank labels and labels that fold to nothing dropped.
        private static List<(string Label, string Key)> LabelKeys(IReadOnlyList<string> labels)
            => (labels ?? Array.Empty<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => (Label: l, Key: InvoiceValueParser.LabelKey(l)))
                .Where(l => l.Key.Length > 0)
                .ToList();

        /// <summary>A document's keyed lines, and the queries asked of them.</summary>
        internal sealed class Prepared
        {
            private readonly Dictionary<PdfTextWord, (KeyedLine Line, int Index)> _home =
                new Dictionary<PdfTextWord, (KeyedLine, int)>();
            private readonly Dictionary<PdfPageTextLayout, int> _pageWords = new Dictionary<PdfPageTextLayout, int>();
            private readonly Dictionary<(PdfTextWord, FieldValueSide), PdfTextWord> _neighbours =
                new Dictionary<(PdfTextWord, FieldValueSide), PdfTextWord>();
            private readonly int _workUnits;

            internal Prepared(List<KeyedLine> keyed, WalkBudget budget, int workUnits)
            {
                Budget = budget;
                _workUnits = workUnits;
                foreach (var kl in keyed)
                {
                    if (kl.Key.Length > 0) Lines.Add(kl);
                    _pageWords[kl.Page] = (_pageWords.TryGetValue(kl.Page, out int n) ? n : 0) + kl.Line.Words.Count;
                    for (int i = 0; i < kl.Line.Words.Count; i++)
                        if (!_home.ContainsKey(kl.Line.Words[i])) _home[kl.Line.Words[i]] = (kl, i);
                }
            }

            /// <summary>Every line with a label key, in reading order.</summary>
            internal List<KeyedLine> Lines { get; } = new List<KeyedLine>();

            /// <summary>
            /// Every word that is part of some query's label. A value ends before one: a label
            /// without a colon of its own still begins the next field — and on a right-to-left
            /// line the colon closes a two-word label at its far end, after the word a value run
            /// meets first. A word the label only ends or begins inside, whose rest has no letters,
            /// is not one: its rest is a value.
            /// </summary>
            internal HashSet<PdfTextWord> KnownLabelWords { get; } = new HashSet<PdfTextWord>();

            /// <summary>The work every question asked of these lines is charged to.</summary>
            internal WalkBudget Budget { get; }

            /// <summary>Whether the budget ran out, so some matches were never examined.</summary>
            internal bool Incomplete => Budget.Exhausted;

            /// <summary>What <see cref="LastError"/> says when <see cref="Incomplete"/>.</summary>
            internal string IncompleteReason =>
                $"field extraction stopped at its work budget ({_workUnits} units, PdfLimits.MaxFieldExtractionWork); " +
                "the values are from the matches examined before it";

            /// <summary>The best match, or a not-found result — what <see cref="Extract(IReadOnlyList{PdfPageTextLayout}, IReadOnlyList{FieldQuery})"/> returns per query.</summary>
            internal FieldResult Best(FieldQuery q)
            {
                Candidate best = null;
                foreach (var c in Candidates(this, q))
                    if (Better(c, best, q.Occurrence)) best = c;
                return best?.Result ?? new FieldResult { Key = q?.Key };
            }

            /// <summary>
            /// The best match whose result <paramref name="accept"/> takes, or a not-found result:
            /// for a caller that can tell a value it can use from one it cannot — an invoice
            /// number with no digit, a total that is not an amount — and would rather have the
            /// next match than none. What it takes is <see cref="Accepted"/>.
            /// </summary>
            internal FieldResult Best(FieldQuery q, Func<FieldResult, bool> accept) => Best(q, accept, null);

            /// <summary>
            /// The best match whose label occurrence <paramref name="where"/> takes (all, when it is
            /// null) and whose result <paramref name="accept"/> takes: for a caller that judges where
            /// a label stands on its line — a title, not a sentence's words — as well as what it reads.
            /// </summary>
            internal FieldResult Best(FieldQuery q, Func<FieldResult, bool> accept, Func<Candidate, bool> where)
            {
                Candidate best = null;
                foreach (var c in Accepted(q, accept, where))
                    if (Better(c, best, q.Occurrence)) best = c;
                return best?.Result ?? new FieldResult { Key = q?.Key };
            }

            /// <summary>
            /// Every match <paramref name="accept"/> takes (<see cref="Accepted"/>), best first — for
            /// a caller choosing among them by what it knows besides the page.
            /// </summary>
            internal List<FieldResult> Ranked(FieldQuery q, Func<FieldResult, bool> accept)
            {
                var accepted = Accepted(q, accept).ToList();
                accepted.Sort((a, b) => Better(a, b, q.Occurrence) ? -1 : Better(b, a, q.Occurrence) ? 1 : 0);
                return accepted.Select(c => c.Result).ToList();
            }

            /// <summary>
            /// The matches <paramref name="accept"/> takes, in reading order — of those whose label
            /// occurrence <paramref name="where"/> takes, when it is given — with what knowing the
            /// value's use tells about a row of labels and a table's column.
            /// </summary>
            /// <remarks>
            /// <para><b>A row of labels reads its values below.</b> A text value found a column away
            /// from its label, or on another line beside it, that <paramref name="accept"/> refuses
            /// is the next cell of a row of headings — <c>מספר מסמך | תאריך | לכבוד</c> over the
            /// row of their values — and the value under the label is read instead. A refused value
            /// beside its label, a word space away (<c>תאריך: ______</c>), is the label's own, and
            /// nothing below it is taken.</para>
            /// <para><b>A table's heading is no label.</b> A value read below its label is passed
            /// over when the line under it holds, in its column, another value
            /// <paramref name="accept"/> takes: a column of dates under <c>תאריך</c> is a table's.</para>
            /// </remarks>
            private IEnumerable<Candidate> Accepted(FieldQuery q, Func<FieldResult, bool> accept,
                                                   Func<Candidate, bool> where = null)
            {
                foreach (var c in Candidates(this, q))
                {
                    if (where != null && !where(c)) continue;
                    var taken = c;
                    if (!accept(c.Result))
                    {
                        if (!c.Rowed || q.ValueKind != FieldValueKind.Text) continue;
                        taken = ReadBelow(c);
                        if (taken == null || !accept(taken.Result)) continue;
                    }
                    if (taken.FoundBelow && HeadsAColumn(taken, accept)) continue;
                    yield return taken;
                }
            }

            // The value below the label of `c`, read as BuildCandidate reads one there: from the
            // label's word nearest its value side, at a neighbour's confidence.
            private Candidate ReadBelow(Candidate c)
            {
                var kl = c.Line;
                var anchor = PickAnchor(c.Own.Words.ToList(), c.Side) ?? kl.Line.Words[0];
                var found = NeighbourRun(this, kl, c.Own, anchor, FieldValueSide.Below, amount: false, out bool rtlHome);
                if (found == null) return null;
                var below = new Candidate
                {
                    Line = kl,
                    At = c.At,
                    Boundaries = c.Boundaries,
                    Span = c.Span,
                    Own = c.Own,
                    Side = c.Side,
                    FoundBelow = true,
                    Result = new FieldResult { Key = c.Result.Key, Label = c.Result.Label, PageIndex = c.Result.PageIndex },
                };
                return Found(below, kl, c.Span, found, rtlHome, 0.6f, 0);
            }

            // Whether a value read below its label heads a column: the nearest line under it holds,
            // among the words over or under the value's own box, a value `accept` takes too. Charged
            // a visit of every word on the page.
            private bool HeadsAColumn(Candidate c, Func<FieldResult, bool> accept)
            {
                var page = c.Line.Page;
                if (!Budget.Take(_pageWords.TryGetValue(page, out int n) ? n : 1)) return false;
                var (x, y, width, height) = c.Result.ValueBox;
                var box = new PdfTextWord(c.Result.Value, x, y, width, height, Array.Empty<PdfTextChar>());
                var seed = PdfSpatialQuery.WordBelow(page, box);
                if (seed == null || !TryGetHome(seed, out KeyedLine home, out _)) return false;
                var cell = home.Line.Words.Where(w => w.X < x + width && w.X + w.Width > x).ToList();
                if (cell.Count == 0) return false;
                return accept(new FieldResult
                {
                    Key = c.Result.Key,
                    Label = c.Result.Label,
                    PageIndex = c.Result.PageIndex,
                    Value = Reading(cell, home.Rtl),
                    ValueBox = BBox(cell),
                    Confidence = c.Result.Confidence,
                });
            }

            /// <summary>Every match, in reading order, each with the line it was found on.</summary>
            internal List<Candidate> All(FieldQuery q) => Candidates(this, q).ToList();

            /// <summary>
            /// Whether a value ends at <paramref name="w"/>: it is another label's word, or a word
            /// with letters that starts or ends with a colon.
            /// </summary>
            internal bool IsStop(PdfTextWord w, HashSet<PdfTextWord> ownLabel)
                => (KnownLabelWords.Contains(w) && !ownLabel.Contains(w)) || IsLabelStop(w);

            /// <summary>The keyed line <paramref name="w"/> was read from, and its index there.</summary>
            internal bool TryGetHome(PdfTextWord w, out KeyedLine line, out int index)
            {
                bool found = _home.TryGetValue(w, out var home);
                line = home.Line;
                index = home.Index;
                return found;
            }

            /// <summary>
            /// The page's nearest word on <paramref name="side"/> of <paramref name="anchor"/>,
            /// asked once per anchor and side — every occurrence of a label inside one word has the
            /// same anchor — and charged a visit of every word on the page. <c>null</c> when there
            /// is none, or when the budget cannot pay for the search.
            /// </summary>
            internal PdfTextWord Neighbour(PdfPageTextLayout page, PdfTextWord anchor, FieldValueSide side)
            {
                if (_neighbours.TryGetValue((anchor, side), out var known)) return known;
                if (!Budget.Take(_pageWords.TryGetValue(page, out int n) ? n : 1)) return null;
                var found = side switch
                {
                    FieldValueSide.Left  => PdfSpatialQuery.WordToTheLeftOf(page, anchor),
                    FieldValueSide.Right => PdfSpatialQuery.WordToTheRightOf(page, anchor),
                    _                    => PdfSpatialQuery.WordBelow(page, anchor),
                };
                _neighbours[(anchor, side)] = found;
                return found;
            }
        }

        /// <summary>
        /// Extracts fields directly from a PDF stream. Structured extraction is run with bidi enabled
        /// unless <paramref name="options"/> overrides it.
        /// </summary>
        public static IReadOnlyList<FieldResult> Extract(
            Stream input, IReadOnlyList<FieldQuery> queries, WordAssemblyOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var pages = PdfStructuredTextExtractor.Extract(
                input, options ?? new WordAssemblyOptions { ApplyBidi = true });
            return Extract(pages, queries);
        }

        // ----------------------------------------------------------------
        //  Lines in reading order, as label keys that remember their glyphs
        // ----------------------------------------------------------------

        /// <summary>
        /// One line's label key — the line in logical (reading) order, folded by
        /// <see cref="InvoiceValueParser.AppendLabelKey"/> — with the word and the glyph each key
        /// character came from.
        /// </summary>
        /// <remarks>
        /// <para><b>Logical order comes from the bidi algorithm, not from the order of the words.</b>
        /// An English label inside a Hebrew line is stored right-to-left word by word, so joining
        /// the words would read "No Invoice"; the line's glyphs in visual order go through
        /// <see cref="UnicodeBidi"/>, which hands back where each logical character came from.</para>
        /// <para><b>Under the line's own direction.</b> A right-to-left line's visual string begins
        /// with its logical end, so the first strong character the algorithm would otherwise read
        /// the paragraph's direction from is often the Latin or numeric value. Resolved left to
        /// right, the geresh of <c>חשבונית מס'</c> moved to the far side of its word and the label
        /// no longer matched.</para>
        /// <para><b>A glyph of several characters</b> — a ligature, a letter with its point — is
        /// appended through <see cref="UnicodeBidi.InVisualOrder"/>, so the line's reversal gives
        /// it back in logical order, as <see cref="TextDirection"/> does for the value.</para>
        /// </remarks>
        internal sealed class KeyedLine
        {
            internal PdfPageTextLayout Page;
            internal PdfTextLine Line;
            /// <summary>The line's place in reading order among the lines with a key; -1 for a line without one.</summary>
            internal int Order = -1;
            internal string Key;
            /// <summary>
            /// Whether the key was read right to left — the line's own direction when that is right
            /// to left, else its first strong character's — and so the side a label's value lies on
            /// and the order the value reads in. A Hebrew label beside a long number ("עוסק מורשה /
            /// ח.פ.: 512988486") is a line the extractor calls left to right, by its digits.
            /// </summary>
            internal bool Rtl;
            /// <summary>Per key character: the index of its word in <see cref="PdfTextLine.Words"/>.</summary>
            internal int[] WordOf;
            /// <summary>Per key character: the index of its glyph in its word's <see cref="Glyphs"/>.</summary>
            internal int[] GlyphOf;
            /// <summary>Per word: its glyphs left to right (<see cref="TextDirection.VisualGlyphs"/>).</summary>
            internal PdfTextChar[][] Glyphs;
            /// <summary>Per word and glyph: whether the glyph contributed a key character.</summary>
            internal bool[][] Keyed;
            /// <summary>Per word: how many key characters it contributed.</summary>
            internal int[] KeyCount;
            /// <summary>Per word: how many of its key characters are letters.</summary>
            internal int[] Letters;
            /// <summary>Per word: how many of its key characters are ASCII digits (the key folds the others to them).</summary>
            internal int[] Digits;
            /// <summary>Per key character: how many letters its word contributed before it.</summary>
            internal int[] LettersBefore;
            /// <summary>The key characters of the line's words that have a letter.</summary>
            internal int LetterChars;
            /// <summary>The words' indexes, left to right.</summary>
            internal int[] ByX;
            /// <summary>Per word: its place in <see cref="ByX"/>.</summary>
            internal int[] PositionOf;

            /// <summary>
            /// How many ends of the key span [<paramref name="at"/>, +<paramref name="length"/>)
            /// fall on a word boundary — no letter of the same word before its start, none after
            /// its end.
            /// </summary>
            internal int Boundaries(int at, int length)
            {
                int end = at + length - 1;
                int after = Letters[WordOf[end]] - LettersBefore[end] - (char.IsLetter(Key[end]) ? 1 : 0);
                return (LettersBefore[at] == 0 ? 1 : 0) + (after == 0 ? 1 : 0);
            }

            /// <summary>
            /// The words a label occurrence touches, sorted into the label's own words and the words
            /// it only ends or begins inside whose rest has no letters.
            /// </summary>
            internal LabelSpan Shape(int at, int length, int label)
            {
                var covered = new Dictionary<int, int>();
                var coveredLetters = new Dictionary<int, int>();
                var order = new List<int>();
                for (int i = at; i < at + length; i++)
                {
                    int w = WordOf[i];
                    if (!covered.TryGetValue(w, out int c)) { order.Add(w); c = 0; }
                    covered[w] = c + 1;
                    if (char.IsLetter(Key[i]))
                        coveredLetters[w] = (coveredLetters.TryGetValue(w, out int l) ? l : 0) + 1;
                }

                var span = new LabelSpan { Label = label, At = at, Length = length, Boundaries = Boundaries(at, length) };
                foreach (int w in order)
                {
                    int restKeys = KeyCount[w] - covered[w];
                    int restLetters = Letters[w] - (coveredLetters.TryGetValue(w, out int l) ? l : 0);
                    if (restKeys == 0 || restLetters > 0) span.LabelWords.Add(w);
                    else span.Covered[w] = covered[w];
                }
                return span;
            }
        }

        /// <summary>One label occurrence: where it is in its line's key, and the words it touches.</summary>
        internal sealed class LabelSpan
        {
            internal int Label;
            internal int At;
            internal int Length;
            /// <summary>0–2: how many of its ends fall on a word boundary.</summary>
            internal int Boundaries;
            /// <summary>Words that are wholly label text, by index, in key order.</summary>
            internal List<int> LabelWords = new List<int>();
            /// <summary>
            /// Words the label only ends or begins inside, whose rest has no letters: index to how
            /// many of the word's key characters the label covers.
            /// </summary>
            internal Dictionary<int, int> Covered = new Dictionary<int, int>();

            // Two occurrences of one label over the same whole words read the same value: the second
            // adds nothing but work ("TotalTotalTotal…" is one word and as many occurrences).
            internal bool SameAs(LabelSpan other)
                => other != null && other.Label == Label && other.Boundaries == Boundaries
                   && Covered.Count == 0 && other.Covered.Count == 0
                   && other.LabelWords.SequenceEqual(LabelWords);
        }

        private static List<KeyedLine> KeyLines(List<PdfPageTextLayout> pages)
        {
            var keyed = new List<KeyedLine>();
            int order = 0;
            foreach (var page in pages)
            {
                foreach (var line in page.Lines)
                {
                    if (line.Words.Count == 0) continue;
                    var k = KeyLine(line);
                    k.Page = page;
                    if (k.Key.Length > 0) k.Order = order++;
                    keyed.Add(k);
                }
            }
            return keyed;
        }

        private static KeyedLine KeyLine(PdfTextLine line)
        {
            int n = line.Words.Count;
            var byX = Enumerable.Range(0, n).OrderBy(i => line.Words[i].X).ToArray();
            var glyphs = new PdfTextChar[n][];

            // The visual string: words left to right, each word's glyphs left to right.
            var visual = new StringBuilder();
            var visualWord = new List<int>();
            var visualGlyph = new List<int>();
            foreach (int wi in byX)
            {
                glyphs[wi] = TextDirection.VisualGlyphs(line.Words[wi]).ToArray();
                if (glyphs[wi].Length == 0) continue;
                if (visual.Length > 0) { visual.Append(' '); visualWord.Add(-1); visualGlyph.Add(-1); }
                for (int g = 0; g < glyphs[wi].Length; g++)
                    foreach (char c in TextDirection.VisualText(glyphs[wi][g]))
                    {
                        visual.Append(c);
                        visualWord.Add(wi);
                        visualGlyph.Add(g);
                    }
            }

            var direction = line.IsRtl ? BaseDirection.RightToLeft : ByStrongCharacters(visual);
            string logical = UnicodeBidi.ReorderWithMap(visual.ToString(), direction, out int[] map) ?? string.Empty;

            var key = new StringBuilder(logical.Length);
            var wordOf = new List<int>(logical.Length);
            var glyphOf = new List<int>(logical.Length);
            for (int i = 0; i < logical.Length; )
            {
                int v = map == null ? i : map[i];
                int w = visualWord[v];
                if (w < 0) { i++; continue; }
                int g = visualGlyph[v];
                InvoiceValueParser.AppendLabelKey(key, logical, ref i);
                while (wordOf.Count < key.Length) { wordOf.Add(w); glyphOf.Add(g); }
            }

            var keyed = new bool[n][];
            for (int wi = 0; wi < n; wi++) keyed[wi] = new bool[glyphs[wi].Length];
            var keyCount = new int[n];
            var letters = new int[n];
            var digits = new int[n];
            var lettersBefore = new int[key.Length];
            for (int i = 0; i < key.Length; i++)
            {
                int w = wordOf[i];
                keyed[w][glyphOf[i]] = true;
                lettersBefore[i] = letters[w];
                keyCount[w]++;
                if (char.IsLetter(key[i])) letters[w]++;
                else if (key[i] >= '0' && key[i] <= '9') digits[w]++;
            }
            int letterChars = 0;
            for (int wi = 0; wi < n; wi++)
                if (letters[wi] > 0) letterChars += keyCount[wi];
            var positionOf = new int[n];
            for (int p = 0; p < n; p++) positionOf[byX[p]] = p;

            return new KeyedLine
            {
                Line = line,
                Key = key.ToString(),
                Rtl = UnicodeBidi.IsRightToLeftParagraph(visual.ToString(), direction),
                WordOf = wordOf.ToArray(),
                GlyphOf = glyphOf.ToArray(),
                Glyphs = glyphs,
                Keyed = keyed,
                KeyCount = keyCount,
                Letters = letters,
                Digits = digits,
                LettersBefore = lettersBefore,
                LetterChars = letterChars,
                ByX = byX,
                PositionOf = positionOf,
            };
        }

        /// <summary>
        /// The direction of a line the extractor does not call right to left, by its strong
        /// characters alone: right to left when most are, left to right when most are not, and
        /// the first one's (<see cref="BaseDirection.Auto"/>) on a tie or with none. The extractor
        /// counts digits and punctuation too, so a Hebrew label beside a long number — "עוסק מורשה /
        /// ח.פ.: 512988486" — is left to right to it; and the first strong character of a Hebrew
        /// line can be a Latin invoice number at its visual start ("LS550909").
        /// </summary>
        private static BaseDirection ByStrongCharacters(StringBuilder visual)
        {
            int rtl = 0, ltr = 0;
            for (int i = 0; i < visual.Length; i++)
            {
                char c = visual[i];
                if (TextDirection.IsRtlChar(c)) rtl++;
                else if (char.IsLetter(c)) ltr++;
            }
            return rtl > ltr ? BaseDirection.RightToLeft : ltr > rtl ? BaseDirection.LeftToRight : BaseDirection.Auto;
        }

        // ----------------------------------------------------------------
        //  One query
        // ----------------------------------------------------------------

        /// <summary>One label occurrence and the value read for it.</summary>
        internal sealed class Candidate
        {
            internal FieldResult Result;
            internal float Specificity;
            internal int Boundaries;
            internal int At;
            internal KeyedLine Line;
            internal LabelSpan Span;
            internal OwnWords Own;
            /// <summary>The value side the label's line was read on.</summary>
            internal FieldValueSide Side;
            /// <summary>
            /// The value was found beside the label a column away, or on another line beside it: the
            /// next cell of a row of labels, maybe, rather than the label's value.
            /// </summary>
            internal bool Rowed;
            /// <summary>The value was read below the label.</summary>
            internal bool FoundBelow;
        }

        /// <summary>
        /// Every occurrence of the query's labels, in reading order — line by line, and along each
        /// line by where the occurrence starts in its key (the longer label first where two start
        /// together) — each with its value.
        /// </summary>
        private static IEnumerable<Candidate> Candidates(Prepared p, FieldQuery q)
        {
            if (q == null) yield break;
            var labels = LabelKeys(q.Labels);
            if (labels.Count == 0) yield break;
            var excludes = LabelKeys(q.ExcludeLabels).Select(e => e.Key).ToList();

            var matches = new List<(int At, int Label)>();
            var excluded = new List<(int Start, int End)>();
            var previous = new LabelSpan[labels.Count];
            foreach (var kl in p.Lines)
            {
                matches.Clear();
                for (int l = 0; l < labels.Count; l++)
                {
                    string key = labels[l].Key;
                    for (int at = kl.Key.IndexOf(key, StringComparison.Ordinal);
                         at >= 0;
                         at = kl.Key.IndexOf(key, at + 1, StringComparison.Ordinal))
                    {
                        if (!p.Budget.Take(key.Length)) yield break;
                        matches.Add((at, l));
                    }
                }
                if (matches.Count == 0) continue;
                matches.Sort((a, b) => a.At != b.At
                    ? a.At.CompareTo(b.At)
                    : labels[b.Label].Key.Length.CompareTo(labels[a.Label].Key.Length));

                if (!ExcludedSpans(p, kl.Key, excludes, excluded)) yield break;
                int next = 0, reach = -1;
                Array.Clear(previous, 0, previous.Length);
                foreach (var (at, l) in matches)
                {
                    int length = labels[l].Key.Length;
                    // The excluded phrases starting at or before this occurrence, and how far the
                    // longest of them reaches: the occurrence is excluded when one covers all of it.
                    while (next < excluded.Count && excluded[next].Start <= at)
                        reach = Math.Max(reach, excluded[next++].End);
                    if (reach >= at + length) continue;
                    if (q.WholeWord && kl.Boundaries(at, length) < 2) continue;

                    var span = kl.Shape(at, length, l);
                    if (span.SameAs(previous[l])) continue;
                    previous[l] = span;
                    yield return BuildCandidate(p, kl, q, labels[l].Label, span, excludes);
                    if (p.Budget.Exhausted) yield break;
                }
            }
        }

        // Every occurrence of an excluded phrase in a key, as [Start, End) sorted by start; false
        // when the budget cannot pay for finding them.
        private static bool ExcludedSpans(Prepared p, string key, List<string> excludes, List<(int Start, int End)> spans)
        {
            spans.Clear();
            foreach (string ex in excludes)
                for (int e = key.IndexOf(ex, StringComparison.Ordinal);
                     e >= 0;
                     e = key.IndexOf(ex, e + 1, StringComparison.Ordinal))
                {
                    if (!p.Budget.Take(ex.Length)) return false;
                    spans.Add((e, e + ex.Length));
                }
            spans.Sort((a, b) => a.Start.CompareTo(b.Start));
            return true;
        }

        // Higher confidence; then a match on word boundaries; then higher specificity; and on a
        // tie the earlier match in reading order under First, the later under Last.
        private static bool Better(Candidate c, Candidate best, FieldOccurrence occurrence)
        {
            if (best == null) return true;
            if (c.Result.Confidence != best.Result.Confidence)
                return c.Result.Confidence > best.Result.Confidence;
            if (c.Boundaries != best.Boundaries)
                return c.Boundaries > best.Boundaries;
            if (c.Specificity != best.Specificity)
                return c.Specificity > best.Specificity;
            int position = c.Line.Order != best.Line.Order
                ? c.Line.Order.CompareTo(best.Line.Order)
                : c.At.CompareTo(best.At);
            if (position == 0) return false;
            return occurrence == FieldOccurrence.Last ? position > 0 : position < 0;
        }

        /// <summary>The label's words on its line, and the value-side rest of a word it ends inside.</summary>
        internal sealed class OwnWords
        {
            /// <summary>Every word the occurrence touches: never a value, never a neighbour.</summary>
            internal readonly HashSet<PdfTextWord> Words = new HashSet<PdfTextWord>();
            /// <summary>Word index to the word built from its glyphs the label does not cover.</summary>
            internal readonly Dictionary<int, PdfTextWord> Rests = new Dictionary<int, PdfTextWord>();
            /// <summary>Word index of a rest, by the rest.</summary>
            internal readonly Dictionary<PdfTextWord, int> RestOf = new Dictionary<PdfTextWord, int>();
            /// <summary>Word index to how many of the word's key characters its rest holds.</summary>
            internal readonly Dictionary<int, int> RestKeys = new Dictionary<int, int>();
            internal float Left = float.MaxValue, Right = float.MinValue;
            internal int MinPosition = int.MaxValue, MaxPosition = int.MinValue;
            /// <summary>
            /// The key characters of a currency the label's colon closes (<c>בש"ח:</c>): the
            /// value's unit, which no more makes the label's line less specific than a currency
            /// printed after the amount does.
            /// </summary>
            internal int CurrencyKeys;

            internal void Include(float x, float width)
            {
                if (x < Left) Left = x;
                if (x + width > Right) Right = x + width;
            }
        }

        private static Candidate BuildCandidate(Prepared p, KeyedLine kl, FieldQuery q, string label, LabelSpan span,
                                                List<string> excludes)
        {
            var line = kl.Line;
            var own = Own(p, kl, span);

            FieldValueSide side = ResolveSide(q.Side, kl.Rtl);
            var candidate = new Candidate
            {
                Line = kl,
                At = span.At,
                Boundaries = span.Boundaries,
                Span = span,
                Own = own,
                Side = side,
                Result = new FieldResult { Key = q.Key, Label = label, PageIndex = kl.Page.PageNumber },
            };
            var result = candidate.Result;
            bool amount = q.ValueKind == FieldValueKind.Amount;
            if (side != FieldValueSide.Below) TakeQualifier(p, kl, own, side, excludes);

            // The value on the label's own line: the run on its value side, else (Auto) the other.
            List<PdfTextWord> sideRun = null;
            int sideLetters = 0;
            if (side != FieldValueSide.Below)
            {
                sideRun = ValueRun(p, kl, own, side, q.ValueKind, out bool complete, out sideLetters);
                if (sideRun.Count > 0 && complete)
                {
                    candidate.Rowed = AColumnAway(own, side, sideRun);
                    var run = amount ? ColumnTotal(p, kl, own, side, sideRun) ?? sideRun : sideRun;
                    return Found(candidate, kl, span, run, kl.Rtl, 1f, sideLetters);
                }
                if (q.Side == FieldValueSide.Auto)
                {
                    var other = ValueRun(p, kl, own, Opposite(side), q.ValueKind, out complete, out int otherLetters);
                    if (other.Count > 0 && complete)
                    {
                        candidate.Rowed = AColumnAway(own, Opposite(side), other);
                        var run = amount ? ColumnTotal(p, kl, own, Opposite(side), other) ?? other : other;
                        return Found(candidate, kl, span, run, kl.Rtl, 0.9f, otherLetters);
                    }
                }
            }

            // Otherwise a spatial neighbour: beside the label in another line, or below it. For an
            // amount, only one whose run holds an amount — and the run the label's line did have on
            // its value side (a VAT rate) is kept in front of it.
            var anchorWords = own.Words.ToList();
            PdfTextWord anchor = PickAnchor(anchorWords, side) ?? line.Words[0];
            List<PdfTextWord> found = null;
            bool rtlHome = false, below = false;
            if (side != FieldValueSide.Below)
                found = NeighbourRun(p, kl, own, anchor, side, amount, out rtlHome);
            if (found == null && !p.Budget.Exhausted)
            {
                found = NeighbourRun(p, kl, own, anchor, FieldValueSide.Below, amount, out rtlHome);
                below = found != null;
            }

            if (found != null)
            {
                candidate.FoundBelow = below;
                candidate.Rowed = !below;
                bool prefix = amount && sideRun != null && sideRun.Count > 0
                              && !(p.TryGetHome(found[0], out var home, out _) && ReferenceEquals(home, kl));
                if (!prefix)
                    return Found(candidate, kl, span, found, rtlHome, 0.6f, 0);

                result.Value = Reading(sideRun, kl.Rtl) + " " + Reading(found, rtlHome);
                result.ValueBox = BBox(sideRun.Concat(found).ToList());
                result.Confidence = 0.6f;
                candidate.Specificity = Specificity(kl, span, sideLetters, own);
                return candidate;
            }

            // An amount query's run that holds no amount, with none nearby: what the line said.
            if (amount && sideRun != null && sideRun.Count > 0)
                return Found(candidate, kl, span, sideRun, kl.Rtl, NoAmountConfidence, sideLetters);

            candidate.Specificity = Specificity(kl, span, 0, own);
            return candidate;
        }

        // The words the occurrence touches, the rests of the words it ends inside, and the
        // horizontal extent of what it covers.
        private static OwnWords Own(Prepared p, KeyedLine kl, LabelSpan span)
        {
            var own = new OwnWords();
            foreach (int wi in span.LabelWords)
            {
                var w = kl.Line.Words[wi];
                own.Words.Add(w);
                own.Include(w.X, w.Width);
                Place(own, kl.PositionOf[wi]);
            }
            foreach (int wi in span.Covered.Keys)
            {
                var w = kl.Line.Words[wi];
                own.Words.Add(w);
                Place(own, kl.PositionOf[wi]);

                var glyphs = kl.Glyphs[wi];
                var coveredGlyph = new bool[glyphs.Length];
                for (int i = span.At; i < span.At + span.Length; i++)
                    if (kl.WordOf[i] == wi) coveredGlyph[kl.GlyphOf[i]] = true;
                for (int g = 0; g < glyphs.Length; g++)
                    if (coveredGlyph[g]) own.Include(glyphs[g].X, glyphs[g].Width);

                var rest = Rest(p, kl, wi, coveredGlyph);
                if (rest != null)
                {
                    own.Rests[wi] = rest;
                    own.RestOf[rest] = wi;
                    own.RestKeys[wi] = kl.KeyCount[wi] - span.Covered[wi];
                }
            }
            return own;
        }

        private static void Place(OwnWords own, int position)
        {
            if (position < own.MinPosition) own.MinPosition = position;
            if (position > own.MaxPosition) own.MaxPosition = position;
        }

        /// <summary>
        /// Takes a bracketed qualifier printed between the label and its colon —
        /// <c>מספר הקצאה (חשבוניות ישראל): 136429430</c> — into the label's own words, so the colon
        /// that closes it does not end the value before it began.
        /// </summary>
        /// <remarks>
        /// The qualifier is the value-side words from a first one that opens with a bracket to the
        /// first that closes one: taken when that word has letters and its colon follows the
        /// bracket; left alone when it closes with no colon (a bracketed value, a negative amount),
        /// or a label stop comes first. Either bracket counts at either end — which way round a
        /// right-to-left bracket reads depends on its producer. A qualifier holding one of the
        /// query's excluded phrases — <c>סה"כ (לפני מע"מ):</c> to a total that excludes "before
        /// VAT" — makes the label another field's, and is left to stop the run.
        /// </remarks>
        private static void TakeQualifier(Prepared p, KeyedLine kl, OwnWords own, FieldValueSide side,
                                          List<string> excludes)
        {
            bool right = side == FieldValueSide.Right;
            List<PdfTextWord> qualifier = null;
            var text = new StringBuilder();
            for (int pos = right ? own.MinPosition : own.MaxPosition;
                 right ? pos < kl.ByX.Length : pos >= 0;
                 pos += right ? 1 : -1)
            {
                if (!p.Budget.Take()) return;
                int wi = kl.ByX[pos];
                if (own.Rests.ContainsKey(wi)) return;
                // The label's own words are never on its value side.
                var w = kl.Line.Words[wi];
                bool onSide = right ? w.X >= own.Right - 0.5f : w.X + w.Width <= own.Left + 0.5f;
                if (!onSide) continue;

                // A word of nothing, or of a blank glyph, is passed over.
                string t = ReadingText(w);
                if (t.Length == 0) continue;
                if (qualifier == null)
                {
                    // A currency the label's colon closes — "סה"כ לתשלום בש"ח: 14,236.80", "in
                    // shekels" — qualifies the label as a bracketed one does.
                    if (t[t.Length - 1] == ':' && InvoiceValueParser.IsCurrencyWord(t))
                    {
                        own.Words.Add(w);
                        own.Include(w.X, w.Width);
                        own.CurrencyKeys = kl.KeyCount[wi];
                        return;
                    }
                    if (!UnicodeBidi.IsPairedBracket(t[0])) return;
                    qualifier = new List<PdfTextWord>();
                }
                qualifier.Add(w);
                text.Append(t);

                bool colonAfterBracket = t.Length >= 2 && t[t.Length - 1] == ':'
                                         && UnicodeBidi.IsPairedBracket(t[t.Length - 2]);
                bool closes = colonAfterBracket || (t.Length >= 2 && UnicodeBidi.IsPairedBracket(t[t.Length - 1]));
                bool stop = IsLabelStop(w);
                if (!closes && !stop) continue;
                if (!colonAfterBracket || !stop) return;

                string key = InvoiceValueParser.LabelKey(text.ToString());
                if (excludes.Any(e => key.IndexOf(e, StringComparison.Ordinal) >= 0)) return;
                foreach (var qw in qualifier)
                {
                    own.Words.Add(qw);
                    own.Include(qw.X, qw.Width);
                }
                return;
            }
        }

        /// <summary>
        /// The glyphs of word <paramref name="wi"/> the label does not cover, as a word of their
        /// own — without the colon or space that only separated them from the label — or
        /// <c>null</c> when none are left, or the word has no glyphs of its own to divide.
        /// </summary>
        private static PdfTextWord Rest(Prepared p, KeyedLine kl, int wi, bool[] coveredGlyph)
        {
            var word = kl.Line.Words[wi];
            var glyphs = kl.Glyphs[wi];
            if (!TextDirection.HasOwnGlyphs(word) || !p.Budget.Take(glyphs.Length)) return null;

            int first = 0, last = glyphs.Length - 1;
            while (first <= last && (coveredGlyph[first] || !kl.Keyed[wi][first])) first++;
            while (last >= first && (coveredGlyph[last] || !kl.Keyed[wi][last])) last--;
            var rest = new List<PdfTextChar>();
            for (int g = first; g <= last; g++)
                if (!coveredGlyph[g]) rest.Add(glyphs[g]);
            if (rest.Count == 0) return null;

            float left = rest.Min(c => c.X), right = rest.Max(c => c.X + c.Width);
            return new PdfTextWord(TextDirection.ToReadingOrder(rest, applyBidi: true),
                                   left, word.Y, right - left, word.Height, rest, word.IsRtl);
        }

        // Sets the result from a run, scores the candidate, and returns it.
        private static Candidate Found(Candidate candidate, KeyedLine kl, LabelSpan span, List<PdfTextWord> run,
                                       bool rtlLine, float confidence, int valueLetters)
        {
            var result = candidate.Result;
            result.Value = Reading(run, rtlLine);
            result.ValueBox = BBox(run);
            result.Confidence = confidence;
            candidate.Specificity = Specificity(kl, span, valueLetters, candidate.Own);
            return candidate;
        }

        /// <summary>
        /// The share of its line's lettered words the label is: its key length over the key
        /// characters of the line's words that have a letter — less <paramref name="valueLetters"/>,
        /// those of the value's own words on the line from its first number on, so that a currency
        /// or a month name in the value does not count against the label — and less a currency the
        /// label's colon closes (<see cref="OwnWords.CurrencyKeys"/>).
        /// </summary>
        private static float Specificity(KeyedLine kl, LabelSpan span, int valueLetters, OwnWords own)
        {
            int denominator = kl.LetterChars - valueLetters - own.CurrencyKeys;
            return denominator > 0 ? Math.Min(1f, (float)span.Length / denominator) : 1f;
        }

        // The key characters a value word on the label's line takes from the specificity's
        // denominator. The rest of a word the label ends inside (w, the rest of word wi) is value,
        // and gives back the key characters it holds. Any other word is value from the run's first
        // word with a digit on (started): a currency after an amount, a month name inside a date.
        // A lettered word before that qualifies the label — "לפני מע"מ" after "סה"כ" — and stays
        // counted, or "total" would be as specific on the subtotal's line as on the total's.
        private static int ValueLetters(KeyedLine kl, OwnWords own, PdfTextWord w, int wi, ref bool started)
        {
            if (own.RestOf.ContainsKey(w))
            {
                started = true;
                return kl.Letters[wi] > 0 ? own.RestKeys[wi] : 0;
            }
            if (kl.Digits[wi] > 0) started = true;
            return started && kl.Letters[wi] > 0 ? kl.KeyCount[wi] : 0;
        }

        private static string Reading(List<PdfTextWord> run, bool rtlLine)
            => TextDirection.ToReadingOrder(run, rightToLeft: rtlLine);

        private static FieldValueSide ResolveSide(FieldValueSide side, bool lineRtl)
            => side != FieldValueSide.Auto ? side
                                           : (lineRtl ? FieldValueSide.Left : FieldValueSide.Right);

        private static FieldValueSide Opposite(FieldValueSide side)
            => side == FieldValueSide.Left ? FieldValueSide.Right : FieldValueSide.Left;

        /// <summary>
        /// The words on <paramref name="side"/> of the label on its own line, nearest first, up to
        /// a column-wide gap or the next label. The first word may be any distance away: a value is
        /// often aligned to the far edge of the page. <paramref name="complete"/> says whether the
        /// run is a value of the query's kind — an amount query's run must reach an amount.
        /// </summary>
        private static List<PdfTextWord> ValueRun(Prepared p, KeyedLine kl, OwnWords own, FieldValueSide side,
                                                  FieldValueKind kind, out bool complete, out int valueLetters)
        {
            var run = new List<PdfTextWord>();
            complete = kind != FieldValueKind.Amount;
            valueLetters = 0;
            bool started = false;
            bool right = side == FieldValueSide.Right;
            PdfTextWord prev = null;
            for (int pos = right ? own.MinPosition : own.MaxPosition;
                 right ? pos < kl.ByX.Length : pos >= 0;
                 pos += right ? 1 : -1)
            {
                if (!p.Budget.Take()) break;
                int wi = kl.ByX[pos];
                if (!own.Rests.TryGetValue(wi, out PdfTextWord w))
                {
                    w = kl.Line.Words[wi];
                    if (own.Words.Contains(w) || kl.Glyphs[wi].Length == 0) continue;
                }
                bool onSide = right ? w.X >= own.Right - 0.5f : w.X + w.Width <= own.Left + 0.5f;
                if (!onSide) continue;

                if (p.IsStop(w, own.Words)) break;
                if (prev != null && complete && TooFar(prev, w)) break;
                // A second amount with decimals beside the first is the next column, however
                // narrow the gap a producer set between them (Word's tight tables).
                if (complete && kind == FieldValueKind.Amount && IsDecimalAmount(w) && run.Any(IsDecimalAmount)) break;
                run.Add(w);
                valueLetters += ValueLetters(kl, own, w, wi, ref started);
                prev = w;
                if (!complete && IsAmount(w) && !IsRateAlone(run, kl.Rtl)) complete = true;
            }
            return run;
        }

        // A word that is an amount with its decimals — "448.43", "2,477.32", "1.234,56" — two digits
        // after a separator and a digit before it: no quantity, no year, no count.
        private static bool IsDecimalAmount(PdfTextWord w)
        {
            if (!IsAmount(w)) return false;
            string t = ReadingText(w);
            for (int i = t.Length - 3; i >= 1; i--)
                if ((t[i] == '.' || t[i] == ',') && char.IsDigit(t[i - 1]) && char.IsDigit(t[i + 1]) && char.IsDigit(t[i + 2])
                    && (i + 3 == t.Length || !char.IsDigit(t[i + 3])))
                    return true;
            return false;
        }

        // A word of nothing but a filler's marks: "**********", "-----", "......".
        private static bool IsFiller(string t)
            => t.Length > 0 && t.All(c => c == '*' || c == '-' || c == '.' || c == '_' || c == '=');

        // A run that reads as a rate and nothing else: its number is an amount word by itself, but
        // the percent sign beside it is a word of its own — "סה"כ חייב במע"מ % 18.00 1,862.40".
        private static bool IsRateAlone(List<PdfTextWord> run, bool rtlLine)
        {
            string reading = Reading(run, rtlLine);
            return InvoiceValueParser.TryParsePercent(reading, out _) && !InvoiceValueParser.TryParseAmount(reading, out _, out _);
        }

        /// <summary>
        /// A row of column totals — <c>סה"כ כולל מע"מ 20,140.50 3,612.25 1,847.25 25,600.00</c>, a
        /// leasing invoice's rent, maintenance and exempt columns and their sum: past the amount
        /// <paramref name="run"/> reached, the label's line goes on, a column-wide gap before each,
        /// with nothing but amounts to its end. When the last is the sum of the others, the first
        /// included, the row ends in its total column, and the
        /// value is that column's amount, with the words of the run that are no amount (a rate,
        /// <c>18%</c>) kept before it. A filler's words (<c>**********</c>) between the columns are
        /// passed over. Otherwise <c>null</c>: a row whose last column is no sum
        /// is no row of totals this can read, and a label or a word on the way is another field.
        /// </summary>
        private static List<PdfTextWord> ColumnTotal(Prepared p, KeyedLine kl, OwnWords own, FieldValueSide side,
                                                     List<PdfTextWord> run)
        {
            if (!InvoiceValueParser.TryParseAmount(Reading(run, kl.Rtl), out decimal first, out _)) return null;

            var columns = new List<List<PdfTextWord>>();
            PdfTextWord end = run[run.Count - 1], prev = null;
            bool right = side == FieldValueSide.Right;
            for (int pos = right ? own.MinPosition : own.MaxPosition;
                 right ? pos < kl.ByX.Length : pos >= 0;
                 pos += right ? 1 : -1)
            {
                if (!p.Budget.Take()) return null;
                int wi = kl.ByX[pos];
                if (!own.Rests.TryGetValue(wi, out PdfTextWord w))
                {
                    w = kl.Line.Words[wi];
                    if (own.Words.Contains(w) || kl.Glyphs[wi].Length == 0) continue;
                }
                bool onSide = right ? w.X >= own.Right - 0.5f : w.X + w.Width <= own.Left + 0.5f;
                if (!onSide) continue;
                if (prev == null)
                {
                    if (ReferenceEquals(w, end)) prev = w;
                    continue;
                }
                if (p.IsStop(w, own.Words)) return null;
                // A row padded out with a filler — "*** סה"כ ********** 17,304.10 ..." — is still a row of amounts.
                if (IsFiller(ReadingText(w))) continue;
                if (columns.Count == 0 || TooFar(prev, w)
                    || (IsDecimalAmount(w) && columns[columns.Count - 1].Any(IsDecimalAmount)))
                    columns.Add(new List<PdfTextWord>());
                columns[columns.Count - 1].Add(w);
                prev = w;
            }
            if (columns.Count == 0) return null;

            try
            {
                decimal sum = first;
                for (int c = 0; c < columns.Count - 1; c++)
                {
                    if (!InvoiceValueParser.TryParseAmountValue(Reading(columns[c], kl.Rtl), out decimal v, out _))
                        return null;
                    sum += v;
                }
                var last = columns[columns.Count - 1];
                if (!InvoiceValueParser.TryParseAmountValue(Reading(last, kl.Rtl), out decimal total, out _)) return null;
                // A cent of rounding per column added.
                if (Math.Abs(sum - total) > 0.01m * columns.Count) return null;
                return run.Where(w => !IsAmount(w)).Concat(last).ToList();
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        /// <summary>
        /// The run around the label's nearest word on <paramref name="side"/> (or below it), when
        /// that word is no label and — for an amount — the run holds one; otherwise <c>null</c>.
        /// </summary>
        private static List<PdfTextWord> NeighbourRun(Prepared p, KeyedLine kl, OwnWords own, PdfTextWord anchor,
                                                      FieldValueSide side, bool amount, out bool rtlHome)
        {
            rtlHome = false;
            var seed = p.Neighbour(kl.Page, anchor, side);
            if (seed == null || own.Words.Contains(seed) || p.IsStop(seed, own.Words)) return null;
            var run = RunAround(p, own, seed, out rtlHome);
            return amount && !run.Any(IsAmount) ? null : run;
        }

        // A word that is an amount on its own: a number, and not a percentage.
        private static bool IsAmount(PdfTextWord w)
            => InvoiceValueParser.TryParseAmount(WordReadingOrder(w), out _, out _);

        /// <summary>
        /// The run of words around <paramref name="seed"/> on its own line, outward in both
        /// directions under the same rules as <see cref="ValueRun"/>.
        /// </summary>
        private static List<PdfTextWord> RunAround(Prepared p, OwnWords own, PdfTextWord seed, out bool rtlHome)
        {
            var run = new List<PdfTextWord> { seed };
            if (!p.TryGetHome(seed, out KeyedLine home, out int seedIndex))
            {
                rtlHome = seed.IsRtl;
                return run;
            }
            rtlHome = home.Rtl;

            foreach (int step in new[] { -1, 1 })
            {
                PdfTextWord prev = seed;
                for (int pos = home.PositionOf[seedIndex] + step; pos >= 0 && pos < home.ByX.Length; pos += step)
                {
                    if (!p.Budget.Take()) return run;
                    int wi = home.ByX[pos];
                    var w = home.Line.Words[wi];
                    if (home.Glyphs[wi].Length == 0) continue;
                    if (own.Words.Contains(w) || p.IsStop(w, own.Words) || TooFar(prev, w)) break;
                    run.Add(w);
                    prev = w;
                }
            }
            return run;
        }

        // Wider than ValueGapEm word heights: a column boundary, not a space inside one value.
        private static bool TooFar(PdfTextWord a, PdfTextWord b)
            => Gap(a, b) > ValueGapEm * Math.Max(a.Height, b.Height);

        /// <summary>
        /// Per word of the line, its segment: the words left to right, a new segment wherever a
        /// column-wide gap (<see cref="ValueGapEm"/> word heights) parts two of them — the gap that
        /// ends a value. A line holding a header's blocks side by side has a segment per block.
        /// </summary>
        internal static int[] Segments(KeyedLine kl)
        {
            var words = kl.Line.Words;
            var segmentOf = new int[words.Count];
            int segment = 0, previous = -1;
            foreach (int wi in kl.ByX)
            {
                if (kl.Glyphs[wi].Length == 0) continue;
                if (previous >= 0 && TooFar(words[previous], words[wi])) segment++;
                segmentOf[wi] = segment;
                previous = wi;
            }
            return segmentOf;
        }

        // Whether a run on the label's own line starts a column away from it: perhaps the next cell
        // of a row of labels rather than a value set beside its label.
        private static bool AColumnAway(OwnWords own, FieldValueSide side, List<PdfTextWord> run)
        {
            var near = PickAnchor(own.Words.ToList(), side);
            return near != null && TooFar(near, run[0]);
        }

        // The horizontal space between two words, whichever is on the left.
        private static float Gap(PdfTextWord a, PdfTextWord b)
            => a.X <= b.X ? b.X - (a.X + a.Width) : a.X - (b.X + b.Width);

        // A word that is itself a label: it has letters and starts or ends with a colon ("תאריך:",
        // "Date:"). A time ("14:32") has no letters; a URL has its colon inside.
        private static bool IsLabelStop(PdfTextWord w)
        {
            string t = ReadingText(w);
            if (t.Length < 2 || !t.Any(char.IsLetter)) return false;
            return t[0] == ':' || t[t.Length - 1] == ':';
        }

        // A word's text in reading order, its digits folded to ASCII: what the stop rules read.
        private static string ReadingText(PdfTextWord w)
            => InvoiceValueParser.NormalizeDigits(WordReadingOrder(w)).Trim();

        private static PdfTextWord PickAnchor(List<PdfTextWord> labelWords, FieldValueSide side)
        {
            if (labelWords.Count == 0) return null;
            // Value to the right -> anchor on the right-most label word; to the left -> the left-most.
            PdfTextWord best = labelWords[0];
            foreach (var w in labelWords)
            {
                if (side == FieldValueSide.Left) { if (w.X < best.X) best = w; }
                else                             { if (w.X > best.X) best = w; }
            }
            return best;
        }

        private static (float X, float Y, float Width, float Height) BBox(List<PdfTextWord> words)
        {
            float x = float.MaxValue, y = float.MaxValue, r = float.MinValue, t = float.MinValue;
            foreach (var w in words)
            {
                if (w.X < x) x = w.X;
                if (w.Y < y) y = w.Y;
                if (w.X + w.Width > r) r = w.X + w.Width;
                if (w.Y + w.Height > t) t = w.Y + w.Height;
            }
            // The empty case cannot happen from the call sites — both hand over a non-empty run —
            // so a mutation sweep reports this as a survivor and that is the expected result. Kept
            // as the bound on this helper's own contract: without it an empty list answers with
            // float.MaxValue corners, which is not a rectangle.
            return x == float.MaxValue ? (0f, 0f, 0f, 0f) : (x, y, r - x, t - y);
        }

        // A word's logical (reading-order) text: a stored RTL word on a left-to-right line, or a
        // number on a right-to-left one, is held in visual order.
        internal static string WordReadingOrder(PdfTextWord w)
            => TextDirection.HasOwnGlyphs(w)
                ? TextDirection.ToReadingOrder(w.Chars, applyBidi: true)
                : (w.Text ?? string.Empty);
    }
}
