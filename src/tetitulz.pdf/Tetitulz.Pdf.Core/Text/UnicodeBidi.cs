// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The Unicode Bidirectional Algorithm (UAX #9) through rule L2, held to Unicode's own conformance file:
    /// P2/P3 (an isolate's content skipped); X1–X8 with the directional status stack, its depth of 125 and the
    /// overflow and valid-isolate counts; X9's removals; X10's level runs joined into isolating run sequences, each
    /// from its own sos and eos; W1–W7, N0 (BD16's bracket pairs, under canonical equivalence), N1/N2 and I1/I2 per
    /// sequence; L1 and L2 per line (<see cref="BidiParagraph"/>); and L4's mirroring of the common pairs.
    ///
    /// <para><b>Conformance is a test, not a claim</b> (<c>BidiCharacterConformanceTests</c>: every case of the
    /// Character Database's <c>BidiCharacterTest.txt</c>, levels and order) — splitting a sequence at isolates alone,
    /// an embedding's run taking the paragraph's sos, would read an RTL embedding of <c>1 2 3</c> as written, 72 of the
    /// file's 91 707 cases in the wrong order.</para>
    ///
    /// <para><b>Every table is the Character Database's</b>: the classes from
    /// <c>tools/unicode-bidi/Generate-BidiClasses.ps1</c> (a hand-written classifier typing what it does not list
    /// strong <c>L</c> would anchor brackets, General Punctuation and the shekel sign left to right inside
    /// right-to-left text), the bracket pairs from <c>Generate-BidiBrackets.ps1</c> (all 65).</para>
    /// </summary>
    public static partial class UnicodeBidi
    {
        // -----------------------------------------------------------------------
        // Unicode Bidi_Class values (UAX #9 Table 4)
        // -----------------------------------------------------------------------
        private enum BidiClass : byte
        {
            L,   // Left-to-right
            R,   // Right-to-left
            AL,  // Arabic letter
            AN,  // Arabic number
            EN,  // European number
            ES,  // European number separator (+ -)
            ET,  // European number terminator (# $ %)
            CS,  // Common separator (, . / :)
            NSM, // Non-spacing mark
            BN,  // Boundary neutral
            B,   // Paragraph separator
            S,   // Segment separator
            WS,  // Whitespace
            ON,  // Other neutral
            LRE, LRO, RLE, RLO, PDF, // Deprecated embedding/override
            LRI, RLI, FSI, PDI        // Isolate
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Reorders a string according to the Unicode BiDi algorithm.
        /// Returns the visually ordered string suitable for display.
        /// If the string contains no RTL characters, it is returned unchanged.
        ///
        /// <para>The paragraph direction is auto-detected from the first strong character (P2/P3).
        /// When the direction is known from context rather than content — a form field, a table
        /// cell, a template slot — use <see cref="Reorder(string, BaseDirection)"/> instead; see the
        /// remarks there for why auto-detection is wrong in that case.</para>
        /// </summary>
        public static string Reorder(string text)
            => Reorder(text, BaseDirection.Auto);

        /// <summary>
        /// Reorders a string according to the Unicode BiDi algorithm, with an explicitly supplied
        /// paragraph direction.
        ///
        /// <para><b>Why this overload exists.</b> P2/P3 take the paragraph level from the first
        /// strong character, which is the right rule for a paragraph and the wrong one for a
        /// <i>field</i>. A Hebrew form whose value happens to start with a Latin word or a digit —
        /// <c>"Tel Aviv 12"</c>, <c>"1,234 ₪"</c> — resolves to an LTR paragraph, so trailing
        /// punctuation anchors on the wrong side and any Hebrew/Latin mix reads inside-out.
        /// Direction there is a property of the form, and cannot be recovered from the value.</para>
        ///
        /// <para><b>This is an overload rather than an optional parameter on purpose.</b> An
        /// optional parameter changes the compiled signature; this assembly ships as a NuGet
        /// package, and a consumer built against <c>Reorder(string)</c> would take a
        /// <see cref="System.MissingMethodException"/>. Two real methods keep the original entry
        /// point in metadata permanently.</para>
        /// </summary>
        /// <param name="text">Logical-order text. <c>null</c> and empty are returned unchanged.</param>
        /// <param name="baseDirection">
        /// <see cref="BaseDirection.Auto"/> reproduces <see cref="Reorder(string)"/> exactly,
        /// including its pure-LTR fast path. The forced values skip P2/P3 entirely.
        /// </param>
        public static string Reorder(string text, BaseDirection baseDirection)
            => ReorderWithMap(text, baseDirection, out _);

        /// <summary>
        /// <see cref="Reorder(string, BaseDirection)"/>, also handing back where each visual
        /// character came from: <paramref name="logicalIndex"/>[i] is the logical index of visual
        /// character i (<c>null</c> when the text is returned unchanged, which is the identity).
        /// </summary>
        internal static string ReorderWithMap(string text, BaseDirection baseDirection, out int[] logicalIndex)
        {
            logicalIndex = null;
            if (string.IsNullOrEmpty(text))
                return text;

            // The pure-LTR fast path is valid ONLY under Auto. Under a forced RTL paragraph a
            // string with no RTL characters at all still reorders — "Tel Aviv 12" must render as
            // "12 Tel Aviv" — so returning it unchanged here would silently ignore the caller's
            // direction. Keeping the early-out gated on Auto also means every existing caller
            // (PdfTextExtractor, PdfStructuredTextExtractor, Plugins.Text) keeps today's exact
            // behaviour and cost.
            if (baseDirection == BaseDirection.Auto && !ContainsRtl(text))
                return text;

            var paragraph = ResolveParagraph(text, baseDirection);
            int[] order = paragraph.VisualOrder(0, text.Length);

            var result = new char[order.Length];
            for (int i = 0; i < order.Length; i++)
                result[i] = paragraph.MirroredCharAt(order[i]);
            logicalIndex = order;
            return new string(result);
        }

        /// <summary>
        /// <paramref name="text"/> in visual order under a directional override of the whole of it —
        /// what <c>unicode-bidi: bidi-override</c> (or <c>isolate-override</c>) asks for a run drawn on
        /// its own: every character takes the override's direction (X6), so a left-to-right override
        /// keeps the logical order and a right-to-left one reverses it, mirroring what L4 mirrors.
        /// </summary>
        /// <remarks>
        /// The resolver's own override, through an LRO or RLO and its PDF around the text, rather than a
        /// reversal written here — one implementation of X6. The two controls are never drawn, so they
        /// are left out of the result and of <paramref name="logicalIndex"/>, whose entries index
        /// <paramref name="text"/>.
        /// </remarks>
        internal static string ReorderOverriddenWithMap(string text, bool rightToLeft, out int[] logicalIndex)
        {
            logicalIndex = null;
            if (string.IsNullOrEmpty(text)) return text;

            string wrapped = (rightToLeft ? Controls.RightToLeftOverride : Controls.LeftToRightOverride)
                             + text + Controls.PopDirectionalFormatting;
            var paragraph = ResolveParagraph(wrapped, rightToLeft ? BaseDirection.RightToLeft : BaseDirection.LeftToRight);
            int[] order = paragraph.VisualOrder(0, wrapped.Length);

            var result = new char[text.Length];
            var index = new int[text.Length];
            int n = 0;
            foreach (int o in order)
            {
                if (o == 0 || o == wrapped.Length - 1) continue;   // the override's own controls
                result[n] = paragraph.MirroredCharAt(o);
                index[n++] = o - 1;
            }
            logicalIndex = index;
            return new string(result);
        }

        /// <summary>
        /// Resolves this text's bidi embedding levels — P2/P3, X1–X9, W1–W7, N0, N1/N2, I1/I2 —
        /// and returns them, stopping short of the per-line steps (L1, L2, L4).
        ///
        /// <para>This is the entry point for a <b>layout</b> caller. <see cref="Reorder(string)"/>
        /// answers "what does this look like" for a caller that already has a complete line;
        /// anything that must choose its own line breaks needs the levels, because it has to
        /// measure in logical order first and can only reorder once a line exists. See
        /// <see cref="BidiParagraph"/>.</para>
        ///
        /// <para><see cref="Reorder(string, BaseDirection)"/> is implemented on top of this, so
        /// there is one resolution pipeline rather than two that can drift.</para>
        /// </summary>
        /// <param name="text">Logical-order text. Empty yields an empty paragraph.</param>
        /// <param name="baseDirection">
        /// <see cref="BaseDirection.Auto"/> applies P2/P3; the forced values skip them, per
        /// UAX #9 §3.3.1's "higher-level protocol" allowance.
        /// </param>
        public static BidiParagraph ResolveParagraph(string text, BaseDirection baseDirection)
        {
            text ??= string.Empty;
            int n = text.Length;
            var initial = new BidiClass[n];
            var l1Class = new byte[n];

            // Both units of a surrogate pair take the pair's class, so every array stays index-aligned with the
            // string; L1 reads the ORIGINAL classes, captured before any rule rewrites them.
            for (int i = 0; i < n; i++)
            {
                var t = ClassAt(text, i, out int units);
                initial[i] = t;
                l1Class[i] = L1ClassOf(t);
                if (units == 2) { initial[i + 1] = t; l1Class[i + 1] = l1Class[i]; i++; }
            }

            int[] matching = MatchIsolates(initial);

            // P2/P3, unless the caller states the direction (§3.3.1's higher-level protocol).
            int paragraphLevel = baseDirection == BaseDirection.LeftToRight ? 0
                : baseDirection == BaseDirection.RightToLeft ? 1
                : FirstStrongLevel(initial, matching, 0, n, 0);

            var types = (BidiClass[])initial.Clone();
            var levels = new int[n];
            ResolveExplicitLevels(initial, types, levels, matching, paragraphLevel);        // X1–X9
            ResolveSequences(text, initial, types, levels, matching, paragraphLevel);       // X10, W1–W7, N0–N2, I1–I2
            AssignRemovedLevels(initial, levels, paragraphLevel);

            return new BidiParagraph(text, levels, l1Class, paragraphLevel);
        }

        /// <summary>
        /// The Unicode bidi formatting characters, by the effect they express. Producing one of
        /// these is how a caller says "override the algorithm here" — an HTML <c>&lt;bdo&gt;</c> or
        /// a CSS <c>unicode-bidi</c> declaration means exactly one of them.
        /// </summary>
        public static class Controls
        {
            /// <summary>U+202A — begin a left-to-right embedding.</summary>
            public const char LeftToRightEmbedding = '\u202A';
            /// <summary>U+202B — begin a right-to-left embedding.</summary>
            public const char RightToLeftEmbedding = '\u202B';
            /// <summary>U+202C — end the innermost embedding or override.</summary>
            public const char PopDirectionalFormatting = '\u202C';
            /// <summary>U+202D — begin a left-to-right override (<c>&lt;bdo dir="ltr"&gt;</c>).</summary>
            public const char LeftToRightOverride = '\u202D';
            /// <summary>U+202E — begin a right-to-left override (<c>&lt;bdo dir="rtl"&gt;</c>).</summary>
            public const char RightToLeftOverride = '\u202E';
            /// <summary>U+2066 — begin a left-to-right isolate.</summary>
            public const char LeftToRightIsolate = '\u2066';
            /// <summary>U+2067 — begin a right-to-left isolate.</summary>
            public const char RightToLeftIsolate = '\u2067';
            /// <summary>U+2068 — begin a first-strong isolate (<c>dir="auto"</c>).</summary>
            public const char FirstStrongIsolate = '\u2068';
            /// <summary>U+2069 — end the innermost isolate.</summary>
            public const char PopDirectionalIsolate = '\u2069';
        }

        /// <summary>
        /// True for a bidi formatting character (U+202A–U+202E, U+2066–U+2069).
        ///
        /// <para>These affect embedding levels and are <b>zero-width and never drawn</b>. A
        /// renderer must strip them before mapping glyphs — a font has no glyph for them, so they
        /// would otherwise be substituted (typically by a space) and, worse, recorded in
        /// <c>/ToUnicode</c>, putting invisible control characters into every copy-paste and
        /// making a text search for the surrounding words fail.</para>
        /// </summary>
        public static bool IsBidiControl(char c)
            => (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069');

        /// <summary>
        /// True for a character that only steers layout and is never drawn: the bidi formatting
        /// characters (<see cref="IsBidiControl"/>), the left-to-right, right-to-left and Arabic
        /// letter marks (U+200E, U+200F, U+061C) and the zero-width no-break space (U+FEFF). A
        /// number laid out inside right-to-left text routinely carries them; they are no part of
        /// what it says.
        /// </summary>
        /// <remarks>
        /// <b>The one list.</b> The invoice value parser's normalisers and
        /// <see cref="IsraeliIdentifiers"/> read it; it was three copies, each beside a comment
        /// saying it agreed with the others, and the tests pinned only the newest.
        /// </remarks>
        internal static bool IsInvisibleLayoutMark(char c)
            => c == (char)0x200E || c == (char)0x200F || c == (char)0x061C || c == (char)0xFEFF
               || IsBidiControl(c);

        /// <summary>
        /// <paramref name="text"/> with every bidi formatting character removed — what actually
        /// gets drawn. Returns the same instance when there is nothing to strip, so the common
        /// case allocates nothing.
        /// </summary>
        public static string StripBidiControls(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            bool any = false;
            foreach (char c in text) if (IsBidiControl(c)) { any = true; break; }
            if (!any) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text) if (!IsBidiControl(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// The paragraph direction <see cref="Reorder(string, BaseDirection)"/> would resolve for
        /// this text: <c>true</c> when the resolved embedding level is odd (right-to-left).
        ///
        /// <para>Exposed because a caller that has to <i>place</i> a run — right-align a field,
        /// choose which end of a slot the run starts from — needs the same answer the reorder used,
        /// and re-deriving it from the text is how the two get to disagree.</para>
        /// </summary>
        public static bool IsRightToLeftParagraph(string text, BaseDirection baseDirection)
        {
            if (baseDirection == BaseDirection.LeftToRight) return false;
            if (baseDirection == BaseDirection.RightToLeft) return true;
            if (string.IsNullOrEmpty(text)) return false;

            // P2/P3 as ResolveParagraph applies them: the first strong character outside every
            // isolate, an initiator's content skipped to its matching PDI and an unmatched initiator
            // ending the scan — so a run placed by this answer and reordered by that one agree when
            // the first strong character stands inside an isolate.
            int n = text.Length;
            var initial = new BidiClass[n];
            for (int i = 0; i < n; i++)
            {
                var t = ClassAt(text, i, out int units);
                initial[i] = t;
                if (units == 2 && i + 1 < n) { initial[i + 1] = t; i++; }
            }
            return FirstStrongLevel(initial, MatchIsolates(initial), 0, n, 0) == 1;
        }

        /// <summary>
        /// The direction to read a line already in visual (drawn) order back to logical order in,
        /// when nothing says which paragraph it came from — the extractors' question.
        /// </summary>
        /// <remarks>
        /// <para>P2/P3 over a visual line asks its leftmost strong character, which is the end of a
        /// right-to-left line, not its start: a Hebrew line ending in a Latin code — an invoice's
        /// "order number ABC123" — read back left to right, the code first. When the line's two ends
        /// agree, that is the direction. When they disagree the visual text cannot say: drawn "ABC" then
        /// a Hebrew word is a left-to-right line in that order or a right-to-left one the other way. The
        /// line's strong characters then decide by number — a mostly Hebrew line is most often a Hebrew
        /// paragraph — and a tie keeps what P2/P3 says. A guess either way; the line's own paragraph,
        /// where a caller knows it, is the answer.</para>
        /// </remarks>
        internal static BaseDirection VisualLineDirection(string visual)
        {
            if (string.IsNullOrEmpty(visual)) return BaseDirection.LeftToRight;
            BidiClass? leftmost = null, rightmost = null;
            int strongRtl = 0, strongLtr = 0;
            for (int i = 0; i < visual.Length; i++)
            {
                var t = ClassAt(visual, i, out int units);
                i += units - 1;
                if (t == BidiClass.R || t == BidiClass.AL) { strongRtl++; leftmost ??= t; rightmost = t; }
                else if (t == BidiClass.L) { strongLtr++; leftmost ??= t; rightmost = t; }
            }
            bool leftRtl = leftmost == BidiClass.R || leftmost == BidiClass.AL;
            bool rightRtl = rightmost == BidiClass.R || rightmost == BidiClass.AL;
            if (leftmost != null && leftRtl != rightRtl && strongRtl != strongLtr)
                return strongRtl > strongLtr ? BaseDirection.RightToLeft : BaseDirection.LeftToRight;
            return leftRtl ? BaseDirection.RightToLeft : BaseDirection.LeftToRight;
        }

        // -----------------------------------------------------------------------
        // Internal steps
        // -----------------------------------------------------------------------

        internal static bool ContainsRtl(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                var bc = ClassAt(text, i, out int units);
                if (bc == BidiClass.R || bc == BidiClass.AL || bc == BidiClass.AN)
                    return true;
                i += units - 1;
            }
            return false;
        }

        /// <summary>
        /// The bidi class of the character at <paramref name="i"/>, and how many code units it
        /// takes: a surrogate pair is one character, classified as one.
        /// </summary>
        /// <remarks>
        /// The one place a string is classified a character at a time — the resolution, the
        /// paragraph-direction query and the fast path's right-to-left test alike. Classifying each
        /// code unit sends both halves of a pair to the surrogates' own class, strong <c>L</c>: every
        /// astral right-to-left script — Adlam, Mende Kikakui, Kharoshthi, Old Hungarian, the Arabic
        /// mathematical alphabets — then resolved left to right in the reorder, and later answered
        /// "left to right" to the paragraph query and "nothing to reorder" to the fast path, which
        /// scanned code units after the resolution had stopped doing so.
        /// </remarks>
        private static BidiClass ClassAt(string text, int i, out int units)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                units = 2;
                return GetSupplementaryBidiClass(char.ConvertToUtf32(text[i], text[i + 1]));
            }
            units = 1;
            return GetBidiClass(text[i]);
        }

        /// <summary>The L1 reset class of a character's original type: separators, and the whitespace L1 resets before them and at a line's end — the isolate controls and the characters X9 removes among it (UAX #9 L1, and its implementation note that a removed character is ignored).</summary>
        private static byte L1ClassOf(BidiClass t) => t switch
        {
            BidiClass.S => BidiParagraph.L1Segment,
            BidiClass.B => BidiParagraph.L1Paragraph,
            BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI
                or BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN
                => BidiParagraph.L1Whitespace,
            _ => BidiParagraph.L1None,
        };

        /// <summary>The characters X9 removes: the embedding and override controls, PDF, and BN.</summary>
        private static bool IsRemovedByX9(BidiClass t)
            => t == BidiClass.LRE || t == BidiClass.RLE || t == BidiClass.LRO || t == BidiClass.RLO
               || t == BidiClass.PDF || t == BidiClass.BN;

        private static bool IsIsolateInitiator(BidiClass t)
            => t == BidiClass.LRI || t == BidiClass.RLI || t == BidiClass.FSI;

        /// <summary>
        /// BD9: for each isolate initiator the index of its matching PDI, and for each such PDI its initiator's;
        /// -1 for one with no match. A paragraph separator ends every open isolate.
        /// </summary>
        private static int[] MatchIsolates(BidiClass[] types)
        {
            var match = new int[types.Length];
            for (int i = 0; i < match.Length; i++) match[i] = -1;
            var open = new List<int>();
            for (int i = 0; i < types.Length; i++)
            {
                BidiClass t = types[i];
                if (IsIsolateInitiator(t)) open.Add(i);
                else if (t == BidiClass.PDI && open.Count > 0)
                {
                    int start = open[open.Count - 1];
                    open.RemoveAt(open.Count - 1);
                    match[start] = i;
                    match[i] = start;
                }
                else if (t == BidiClass.B) open.Clear();
            }
            return match;
        }

        /// <summary>
        /// P2/P3 over [<paramref name="start"/>, <paramref name="end"/>): 1 for a first strong character R or AL, 0
        /// for L, skipping every isolate initiator's content to its matching PDI — and stopping at an initiator with
        /// none, whose content runs to the paragraph's end; <paramref name="fallback"/> with no strong character.
        /// </summary>
        private static int FirstStrongLevel(BidiClass[] types, int[] matching, int start, int end, int fallback)
        {
            for (int i = start; i < end; i++)
            {
                BidiClass t = types[i];
                if (t == BidiClass.L) return 0;
                if (t == BidiClass.R || t == BidiClass.AL) return 1;
                if (IsIsolateInitiator(t))
                {
                    if (matching[i] < 0) return fallback;
                    i = matching[i];
                }
            }
            return fallback;
        }

        /// <summary>The deepest explicit embedding level, max_depth (BD2).</summary>
        private const int MaxExplicitDepth = 125;

        /// <summary>
        /// X1–X9: each character's explicit embedding level, a directional override's class over the characters in
        /// its scope (X6), and the characters X9 removes typed <c>BN</c>. The directional status stack holds at most
        /// <see cref="MaxExplicitDepth"/> levels; the overflow isolate and overflow embedding counts and the valid
        /// isolate count decide what a PDI and a PDF close (X5–X7). A paragraph separator closes everything (X8).
        /// </summary>
        private static void ResolveExplicitLevels(BidiClass[] initial, BidiClass[] types, int[] levels, int[] matching,
            int paragraphLevel)
        {
            int n = initial.Length;

            // No embedding, override or isolate: every character is at the paragraph's level and keeps its class —
            // the common case, without the stack.
            bool anyExplicit = false;
            for (int i = 0; i < n && !anyExplicit; i++)
            {
                BidiClass t = initial[i];
                anyExplicit = t == BidiClass.RLE || t == BidiClass.LRE || t == BidiClass.RLO || t == BidiClass.LRO
                              || t == BidiClass.PDF || IsIsolateInitiator(t) || t == BidiClass.PDI;
            }
            if (!anyExplicit)
            {
                for (int i = 0; i < n; i++) levels[i] = paragraphLevel;
                return;
            }

            var stackLevel = new int[MaxExplicitDepth + 2];
            var stackOverride = new BidiClass[MaxExplicitDepth + 2];
            var stackIsolate = new bool[MaxExplicitDepth + 2];
            int depth = 1;
            stackLevel[0] = paragraphLevel;
            stackOverride[0] = BidiClass.ON;
            int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;

            for (int i = 0; i < n; i++)
            {
                BidiClass t = initial[i];
                int top = depth - 1;
                switch (t)
                {
                    case BidiClass.RLE:
                    case BidiClass.LRE:
                    case BidiClass.RLO:
                    case BidiClass.LRO:
                    {
                        // X2–X5.
                        bool rtl = t == BidiClass.RLE || t == BidiClass.RLO;
                        int level = rtl ? (stackLevel[top] + 1) | 1 : (stackLevel[top] + 2) & ~1;
                        if (level <= MaxExplicitDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            stackLevel[depth] = level;
                            stackOverride[depth] = t == BidiClass.RLO ? BidiClass.R : t == BidiClass.LRO ? BidiClass.L : BidiClass.ON;
                            stackIsolate[depth] = false;
                            depth++;
                        }
                        else if (overflowIsolates == 0) overflowEmbeddings++;
                        types[i] = BidiClass.BN;
                        break;
                    }
                    case BidiClass.RLI:
                    case BidiClass.LRI:
                    case BidiClass.FSI:
                    {
                        // X5a–X5c: the initiator takes the level and override outside it.
                        levels[i] = stackLevel[top];
                        if (stackOverride[top] != BidiClass.ON) types[i] = stackOverride[top];
                        bool rtl = t == BidiClass.RLI
                                   || (t == BidiClass.FSI
                                       && FirstStrongLevel(initial, matching, i + 1, matching[i] >= 0 ? matching[i] : n, 0) == 1);
                        int level = rtl ? (stackLevel[top] + 1) | 1 : (stackLevel[top] + 2) & ~1;
                        if (level <= MaxExplicitDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            validIsolates++;
                            stackLevel[depth] = level;
                            stackOverride[depth] = BidiClass.ON;
                            stackIsolate[depth] = true;
                            depth++;
                        }
                        else overflowIsolates++;
                        break;
                    }
                    case BidiClass.PDI:
                        // X6a: an overflowed isolate's PDI closes nothing; a valid one closes everything to its isolate.
                        if (overflowIsolates > 0) overflowIsolates--;
                        else if (validIsolates > 0)
                        {
                            overflowEmbeddings = 0;
                            while (!stackIsolate[depth - 1]) depth--;
                            depth--;
                            validIsolates--;
                        }
                        top = depth - 1;
                        levels[i] = stackLevel[top];
                        if (stackOverride[top] != BidiClass.ON) types[i] = stackOverride[top];
                        break;
                    case BidiClass.PDF:
                        // X7.
                        if (overflowIsolates > 0) { }
                        else if (overflowEmbeddings > 0) overflowEmbeddings--;
                        else if (!stackIsolate[top] && depth >= 2) depth--;
                        types[i] = BidiClass.BN;
                        break;
                    case BidiClass.B:
                        // X8: nothing open inside a paragraph reaches past its separator.
                        depth = 1;
                        overflowIsolates = overflowEmbeddings = validIsolates = 0;
                        levels[i] = paragraphLevel;
                        break;
                    case BidiClass.BN:
                        break;   // X9
                    default:
                        // X6.
                        levels[i] = stackLevel[top];
                        if (stackOverride[top] != BidiClass.ON) types[i] = stackOverride[top];
                        break;
                }
            }
        }

        /// <summary>
        /// X10: the level runs of the characters X9 leaves (BD7), joined into isolating run sequences (BD13) — a run
        /// ending in an isolate initiator continues at the run its matching PDI begins — each resolved from its own
        /// sos and eos: the direction of the higher of its level and the level of the character before it (after
        /// it), the paragraph's where there is none or where the sequence ends in an unmatched initiator.
        /// </summary>
        private static void ResolveSequences(string text, BidiClass[] initial, BidiClass[] types, int[] levels,
            int[] matching, int paragraphLevel)
        {
            int n = initial.Length;
            var kept = new List<int>(n);
            var posOf = new int[n];
            for (int i = 0; i < n; i++)
                if (!IsRemovedByX9(initial[i])) { posOf[i] = kept.Count; kept.Add(i); }
            int m = kept.Count;
            if (m == 0) return;

            var runStart = new List<int>();
            var runOf = new int[n];
            for (int k = 0; k < m; k++)
            {
                if (k == 0 || levels[kept[k]] != levels[kept[k - 1]]) runStart.Add(k);
                runOf[kept[k]] = runStart.Count - 1;
            }
            int runs = runStart.Count;
            var explicitLevels = (int[])levels.Clone();

            var sequence = new List<int>();
            for (int r = 0; r < runs; r++)
            {
                int first = kept[runStart[r]];
                if (initial[first] == BidiClass.PDI && matching[first] >= 0) continue;   // its initiator's sequence

                sequence.Clear();
                int from = runStart[r], run = r;
                while (true)
                {
                    int end = run + 1 < runs ? runStart[run + 1] : m;
                    for (int k = from; k < end; k++) sequence.Add(kept[k]);
                    int last = sequence[sequence.Count - 1];
                    if (!IsIsolateInitiator(initial[last]) || matching[last] < 0) break;
                    int pdi = matching[last];
                    run = runOf[pdi];
                    from = posOf[pdi];
                }

                // From the explicit levels: a sequence resolved before this one has raised its own by I1/I2.
                int firstChar = sequence[0], lastChar = sequence[sequence.Count - 1];
                int level = explicitLevels[firstChar];
                int before = posOf[firstChar] > 0 ? explicitLevels[kept[posOf[firstChar] - 1]] : paragraphLevel;
                int after = IsIsolateInitiator(initial[lastChar]) || posOf[lastChar] + 1 >= m
                    ? paragraphLevel : explicitLevels[kept[posOf[lastChar] + 1]];
                BidiClass sos = (System.Math.Max(level, before) & 1) == 1 ? BidiClass.R : BidiClass.L;
                BidiClass eos = (System.Math.Max(level, after) & 1) == 1 ? BidiClass.R : BidiClass.L;
                ResolveSequence(text, initial, types, levels, sequence, sos, eos);
            }
        }

        /// <summary>W1–W7, N0, N1, N2 and I1–I2 over one isolating run sequence — its character indices <paramref name="p"/>, in order.</summary>
        private static void ResolveSequence(string text, BidiClass[] initial, BidiClass[] types, int[] levels,
            List<int> p, BidiClass sos, BidiClass eos)
        {
            int m = p.Count;
            BidiClass e = (levels[p[0]] & 1) == 1 ? BidiClass.R : BidiClass.L;

            // W1: an NSM takes the class before it — ON after an isolate initiator or PDI, sos at the start.
            for (int k = 0; k < m; k++)
                if (types[p[k]] == BidiClass.NSM)
                    types[p[k]] = k == 0 ? sos
                        : IsIsolateInitiator(initial[p[k - 1]]) || initial[p[k - 1]] == BidiClass.PDI ? BidiClass.ON
                        : types[p[k - 1]];

            // W2: EN after AL, looking back to sos, becomes AN.
            BidiClass strong = sos;
            for (int k = 0; k < m; k++)
            {
                BidiClass t = types[p[k]];
                if (t == BidiClass.L || t == BidiClass.R || t == BidiClass.AL) strong = t;
                else if (t == BidiClass.EN && strong == BidiClass.AL) types[p[k]] = BidiClass.AN;
            }

            // W3: AL becomes R.
            for (int k = 0; k < m; k++)
                if (types[p[k]] == BidiClass.AL) types[p[k]] = BidiClass.R;

            // W4: a single ES between two ENs becomes EN; a single CS between two numbers of one type takes it.
            for (int k = 1; k < m - 1; k++)
            {
                BidiClass t = types[p[k]], before = types[p[k - 1]], after = types[p[k + 1]];
                if (t == BidiClass.ES && before == BidiClass.EN && after == BidiClass.EN) types[p[k]] = BidiClass.EN;
                else if (t == BidiClass.CS && before == after && (before == BidiClass.EN || before == BidiClass.AN))
                    types[p[k]] = before;
            }

            // W5: a sequence of ETs next to an EN becomes EN.
            for (int k = 0; k < m; k++)
            {
                if (types[p[k]] != BidiClass.ET) continue;
                int end = k;
                while (end < m && types[p[end]] == BidiClass.ET) end++;
                bool next = (k > 0 && types[p[k - 1]] == BidiClass.EN) || (end < m && types[p[end]] == BidiClass.EN);
                if (next) for (int j = k; j < end; j++) types[p[j]] = BidiClass.EN;
                k = end - 1;
            }

            // W6: the separators and terminators left become ON.
            for (int k = 0; k < m; k++)
            {
                BidiClass t = types[p[k]];
                if (t == BidiClass.ES || t == BidiClass.ET || t == BidiClass.CS) types[p[k]] = BidiClass.ON;
            }

            // W7: EN after L, looking back to sos, becomes L.
            strong = sos;
            for (int k = 0; k < m; k++)
            {
                BidiClass t = types[p[k]];
                if (t == BidiClass.L || t == BidiClass.R) strong = t;
                else if (t == BidiClass.EN && strong == BidiClass.L) types[p[k]] = BidiClass.L;
            }

            ResolveBracketPairs(text, initial, types, p, sos, e);

            // N1/N2: a run of neutrals between two strong types of one direction takes it (EN and AN count as R),
            // and the embedding direction otherwise; sos and eos at the sequence's ends.
            for (int k = 0; k < m; k++)
            {
                if (!IsNeutralOrIsolate(types[p[k]])) continue;
                int end = k;
                while (end < m && IsNeutralOrIsolate(types[p[end]])) end++;
                BidiClass leading = k == 0 ? sos : NeutralContext(types[p[k - 1]]);
                BidiClass trailing = end == m ? eos : NeutralContext(types[p[end]]);
                BidiClass resolved = leading == trailing ? leading : e;
                for (int j = k; j < end; j++) types[p[j]] = resolved;
                k = end - 1;
            }

            // I1/I2.
            for (int k = 0; k < m; k++)
            {
                int i = p[k];
                BidiClass t = types[i];
                if ((levels[i] & 1) == 0)
                {
                    if (t == BidiClass.R) levels[i] += 1;
                    else if (t == BidiClass.AN || t == BidiClass.EN) levels[i] += 2;
                }
                else if (t == BidiClass.L || t == BidiClass.EN || t == BidiClass.AN) levels[i] += 1;
            }
        }

        private static bool IsNeutralOrIsolate(BidiClass t)
            => t == BidiClass.B || t == BidiClass.S || t == BidiClass.WS || t == BidiClass.ON
               || t == BidiClass.LRI || t == BidiClass.RLI || t == BidiClass.FSI || t == BidiClass.PDI;

        /// <summary>The direction a neutral run sees in a resolved neighbour: L, or R for R, EN and AN (N1).</summary>
        private static BidiClass NeutralContext(BidiClass t) => t == BidiClass.L ? BidiClass.L : BidiClass.R;

        /// <summary>The bracket pairing stack's depth (BD16); an opening bracket past it ends the pairing.</summary>
        private const int MaxPairingDepth = 63;

        /// <summary>
        /// BD16 and N0 over one isolating run sequence: brackets whose class is ON at this point pair by
        /// Bidi_Paired_Bracket under canonical equivalence, and each pair, in the order of its opening bracket, takes
        /// the embedding direction where it encloses a strong type of it, the opposite where it encloses only the
        /// opposite and the context before it is the opposite too, and is left to N1/N2 otherwise; the NSMs after a
        /// bracket that changed follow it.
        /// </summary>
        private static void ResolveBracketPairs(string text, BidiClass[] initial, BidiClass[] types, List<int> p,
            BidiClass sos, BidiClass e)
        {
            int m = p.Count;
            var expected = new char[MaxPairingDepth];
            var openAt = new int[MaxPairingDepth];
            int depth = 0;
            List<(int Open, int Close)> pairs = null;

            for (int k = 0; k < m; k++)
            {
                int i = p[k];
                if (types[i] != BidiClass.ON) continue;
                char c = text[i];
                if (TryGetOpeningBracket(c, out char closing))
                {
                    if (depth == MaxPairingDepth) { pairs = null; break; }
                    expected[depth] = CanonicalBracket(closing);
                    openAt[depth] = k;
                    depth++;
                }
                else if (IsClosingBracket(c))
                {
                    char canonical = CanonicalBracket(c);
                    for (int s = depth - 1; s >= 0; s--)
                    {
                        if (expected[s] != canonical) continue;
                        (pairs ??= new List<(int, int)>()).Add((openAt[s], k));
                        depth = s;
                        break;
                    }
                }
            }
            if (pairs == null) return;
            pairs.Sort((a, b) => a.Open.CompareTo(b.Open));

            BidiClass opposite = e == BidiClass.L ? BidiClass.R : BidiClass.L;
            foreach (var (openK, closeK) in pairs)
            {
                bool foundE = false, foundOpposite = false;
                for (int k = openK + 1; k < closeK && !foundE; k++)
                {
                    BidiClass d = StrongForBrackets(types[p[k]]);
                    if (d == e) foundE = true;
                    else if (d == opposite) foundOpposite = true;
                }

                BidiClass resolved;
                if (foundE) resolved = e;                              // N0 b
                else if (foundOpposite)
                {
                    BidiClass prior = sos;                             // N0 c: the context before the opening bracket
                    for (int k = openK - 1; k >= 0; k--)
                    {
                        BidiClass d = StrongForBrackets(types[p[k]]);
                        if (d == BidiClass.ON) continue;
                        prior = d;
                        break;
                    }
                    resolved = prior == opposite ? opposite : e;       // c.1, c.2
                }
                else continue;                                         // N0 d

                foreach (int k in new[] { openK, closeK })
                {
                    types[p[k]] = resolved;
                    for (int j = k + 1; j < m && initial[p[j]] == BidiClass.NSM; j++) types[p[j]] = resolved;
                }
            }
        }

        /// <summary>L, or R for R, AN and EN (N0 counts numbers as R); ON for anything else.</summary>
        private static BidiClass StrongForBrackets(BidiClass t) => t switch
        {
            BidiClass.L => BidiClass.L,
            BidiClass.R or BidiClass.AN or BidiClass.EN => BidiClass.R,
            _ => BidiClass.ON,
        };

        /// <summary>
        /// A character X9 removed takes the level of the character before it — the paragraph's at the start — so it
        /// opens no level break where there is none (UAX #9 §5.2).
        /// </summary>
        private static void AssignRemovedLevels(BidiClass[] initial, int[] levels, int paragraphLevel)
        {
            for (int i = 0; i < initial.Length; i++)
                if (IsRemovedByX9(initial[i])) levels[i] = i == 0 ? paragraphLevel : levels[i - 1];
        }

        // -----------------------------------------------------------------------
        // Character classification
        // -----------------------------------------------------------------------

        /// <summary>
        /// Bidi class of a supplementary-plane (astral) code point, from the generated table
        /// (<c>UnicodeBidi.Classes.cs</c>): the range that holds it.
        /// </summary>
        /// <remarks>
        /// Not the right-to-left blocks enumerated by hand with <c>L</c> for the rest, which would
        /// type the astral numbers, marks, symbols and format characters strong left-to-right.
        /// </remarks>
        private static BidiClass GetSupplementaryBidiClass(int cp)
        {
            var starts = BidiClassRanges.Starts;
            int lo = 0, hi = starts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (starts[mid] <= cp) lo = mid;
                else hi = mid - 1;
            }
            return BidiClassRanges.Values[BidiClassRanges.Class[lo]];
        }

        /// <summary>
        /// The class of every BMP code unit, laid out from the generated ranges once, so the
        /// classification at the heart of every reorder is one array read. A surrogate code unit is
        /// <c>L</c>, as the Character Database types it; a pair is classified whole, through
        /// <see cref="GetSupplementaryBidiClass"/>.
        /// </summary>
        /// <remarks>
        /// A nested class of its own, so it is built only when first read, after the generated
        /// ranges it reads — the partial class's two files have no static initialisation order.
        /// </remarks>
        private static class BmpBidiClasses
        {
            internal static readonly BidiClass[] Of = Build();

            private static BidiClass[] Build()
            {
                var of = new BidiClass[0x10000];
                var starts = BidiClassRanges.Starts;
                for (int i = 0; i < starts.Length && starts[i] < 0x10000; i++)
                {
                    int end = i + 1 < starts.Length ? System.Math.Min(starts[i + 1], 0x10000) : 0x10000;
                    var cls = BidiClassRanges.Values[BidiClassRanges.Class[i]];
                    for (int cp = starts[i]; cp < end; cp++) of[cp] = cls;
                }
                return of;
            }
        }

        /// <summary>
        /// One glyph's text as an extractor building a visual line must append it: reversed when it
        /// is more than one character and begins with a right-to-left letter, so that reordering the
        /// line (<see cref="Reorder(string)"/>, which reverses a right-to-left run code unit by code
        /// unit) gives it back in logical order. A Hebrew presentation form's glyph maps to its letter
        /// and point, an Arabic ligature's to its letters, and a reader that appended them as mapped
        /// turned bet-with-dagesh into dagesh-then-bet. Anything else is returned as it is.
        /// </summary>
        internal static string InVisualOrder(string glyphText)
        {
            if (glyphText == null || glyphText.Length < 2 || !IsStrongRtl(glyphText[0])) return glyphText;
            var reversed = new char[glyphText.Length];
            for (int i = 0; i < glyphText.Length; i++) reversed[i] = glyphText[glyphText.Length - 1 - i];
            return new string(reversed);
        }

        /// <summary>
        /// A glyph's text as a §14.8.2.3.3 <c>/ReversedChars</c> run draws it: each Bidi_Mirrored
        /// character turned to its mirror.
        /// </summary>
        /// <remarks>
        /// Such a run's characters decode to their logical selves but stand where a right-to-left
        /// line draws them — the closing bracket at the run's visual left, drawn as an opening one.
        /// An extractor reorders a visual line through the bidi algorithm, whose L4 mirrors a
        /// bracket at a right-to-left level back, so it must be handed the shape the glyph is drawn
        /// in; handed the logical bracket, it turned it round a second time, and a Chromium-made
        /// Hebrew line read <c>)לפני מע"מ(</c> — as other readers read it too.
        /// </remarks>
        internal static string AsDrawnInReversedRun(string glyphText)
        {
            if (string.IsNullOrEmpty(glyphText)) return glyphText;
            char[] chars = null;
            for (int i = 0; i < glyphText.Length; i++)
            {
                char mirror = GetMirror(glyphText[i]);
                if (mirror == glyphText[i]) continue;
                chars ??= glyphText.ToCharArray();
                chars[i] = mirror;
            }
            return chars == null ? glyphText : new string(chars);
        }

        /// <summary>
        /// Whether <paramref name="c"/> is a <b>strong</b> right-to-left character — bidi class
        /// <c>R</c> or <c>AL</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Derived from <see cref="GetBidiClass"/> rather than restated, and that is the
        /// whole point.</b> A caller deciding how to lay a run out needs the same answer the
        /// reorder used; <see cref="IsRightToLeftParagraph"/>'s own remarks already say that
        /// re-deriving it from the text is how the two get to disagree. They did:
        /// <c>Base.Operations.TextDirection.IsRtlChar</c> listed seven ranges by hand and covered
        /// only <c>0750–077F</c> and <c>08A0–08FF</c> of the <c>0700–08FF</c> block this classifier
        /// returns <c>AL</c> for — so Syriac (U+0710), Thaana (U+0780), NKo, Samaritan, Mandaic and
        /// Arabic Extended-B were strong RTL to Core and left-to-right to Base, inside a single
        /// extractor that calls both.</para>
        ///
        /// <para><b>It takes a <see cref="char"/>, so an astral RTL script answers <c>false</c> at
        /// the surrogate</b> — the same limit the hand-written copy had. Fixing that means taking a
        /// code point and routing to <c>GetSupplementaryBidiClass</c>, which is a wider change than
        /// the divergence this closes; callers that hold whole strings should keep using the
        /// reorder rather than this predicate.</para>
        /// </remarks>
        internal static bool IsStrongRtl(char c)
        {
            BidiClass cls = GetBidiClass(c);
            return cls == BidiClass.R || cls == BidiClass.AL;
        }

        /// <summary>
        /// Bidi class of a BMP code unit: the Unicode Character Database's, from the generated table
        /// (<c>UnicodeBidi.Classes.cs</c>, written by <c>tools/unicode-bidi/Generate-BidiClasses.ps1</c>).
        /// </summary>
        /// <remarks>
        /// <para><b>Generated, never written by hand.</b> A hand-written classifier types whatever it
        /// does not list as strong <c>L</c>, and that default is wrong for ASCII punctuation and brackets, General Punctuation (the curly apostrophe of
        /// <c>מס’</c>, the right-to-left mark), and the currency signs past <c>€</c> — <c>₪</c>, at the
        /// visual left of an Israeli invoice row, made the whole row read left to right. It also typed
        /// the Hebrew points <c>R</c>, the Arabic-Indic digits and the harakat <c>AL</c> (so an
        /// Arabic number was reversed like a word), and the no-break space <c>WS</c>.</para>
        /// </remarks>
        private static BidiClass GetBidiClass(char c) => BmpBidiClasses.Of[c];

        // -----------------------------------------------------------------------
        // Bracket / mirror table (L4, partial)
        // -----------------------------------------------------------------------

        // -----------------------------------------------------------------------
        // Bidi_Paired_Bracket (BD14/BD15) — a STRICT SUBSET of the mirrored characters.
        //
        // This is the trap: `<`/`>` and the guillemets are all Bidi_Mirrored and
        // all appear in GetMirror below, but their Bidi_Paired_Bracket_Type is None, so N0 must NOT
        // pair them. Feeding the mirror table to N0 would make `a < b > c` a bracket pair and drag
        // both operators to one direction. The two tables answer different questions and are
        // deliberately separate.
        // -----------------------------------------------------------------------

        /// <summary>Whether <paramref name="c"/> is an opening paired bracket, and its closing one (the generated table, <c>UnicodeBidi.Brackets.cs</c>).</summary>
        private static bool TryGetOpeningBracket(char c, out char closing)
        {
            int at = System.Array.BinarySearch(BidiBracketPairs.Openings, c);
            closing = at >= 0 ? BidiBracketPairs.ClosingOf[at] : '\0';
            return at >= 0;
        }

        private static bool IsClosingBracket(char c) => System.Array.BinarySearch(BidiBracketPairs.Closings, c) >= 0;

        /// <summary>
        /// Whether <paramref name="c"/> is a Bidi_Paired_Bracket, opening or closing — the set rule
        /// N0 pairs, for a caller asking whether a word is bracketed.
        /// </summary>
        /// <remarks>
        /// Either kind answers: which way round a right-to-left producer's bracket reads depends on
        /// how it encoded the line — the shape drawn, or the logical character at the drawn place.
        /// </remarks>
        internal static bool IsPairedBracket(char c) => TryGetOpeningBracket(c, out _) || IsClosingBracket(c);

        /// <summary>
        /// A bracket under canonical equivalence, which BD16 matches by: U+2329 and U+232A are U+3008 and U+3009, so an
        /// opening one closes with either form - the table's singletons, generated from <c>UnicodeData.txt</c>.
        /// </summary>
        private static char CanonicalBracket(char c)
        {
            int at = System.Array.BinarySearch(BidiBracketPairs.Decomposable, c);
            return at >= 0 ? BidiBracketPairs.CanonicalOf[at] : c;
        }

        // Internal so BidiParagraph can apply L4 by LOGICAL index — see MirroredCharAt.
        internal static char GetMirror(char c)
        {
            switch (c)
            {
                case '(': return ')';
                case ')': return '(';
                case '[': return ']';
                case ']': return '[';
                case '{': return '}';
                case '}': return '{';
                case '<': return '>';
                case '>': return '<';
                // Double angle quotation marks: Bidi_Mirrored, and the pair a Hebrew or
                // Arabic line is actually quoted with. The SINGLE guillemets below were
                // here and these were not, so U+00AB / U+00BB in an RTL line drew pointing
                // the Latin way. They are NOT Bidi_Paired_Bracket, so N0 must still not
                // pair them - see the note on the bracket table above. U+00A1 and U+00BF
                // are deliberately absent: ON, but not Bidi_Mirrored, so mirroring them
                // would be a second wrong answer.
                case '\u00AB': return '\u00BB';
                case '\u00BB': return '\u00AB';
                case '\u2039': return '\u203A'; // ‹ ›
                case '\u203A': return '\u2039';
                // The curly quotes U+2018–U+201D are not Bidi_Mirrored: a right-to-left line draws
                // them as they are. The table held U+2018 and U+201C (one way only), so an opening
                // quote at an odd level came back as the closing one.
                default: return c;
            }
        }
    }
}
