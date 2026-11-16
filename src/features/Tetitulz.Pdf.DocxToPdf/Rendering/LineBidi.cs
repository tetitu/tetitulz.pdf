// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// A laid-out line's segments in left-to-right visual order — the legacy renderer's one bidi
    /// pass, for a body paragraph's line and a table cell's alike.
    /// </summary>
    /// <remarks>
    /// <para>Each run was shaped, and so reordered, on its own, and a line's runs were then drawn
    /// left to right in logical order: a Hebrew paragraph whose words fell in several runs — a bold
    /// word, a Latin name, a list marker — showed them in reverse, the space ending one run on the
    /// wrong side of it, and the marker on the left. The Builder path resolved the line as a whole
    /// all along.</para>
    /// <para>UAX #9 is resolved once over the paragraph's text in its direction
    /// (<see cref="Paragraph"/>), and each line takes its own part of the levels, L1 applied to the
    /// line; each segment is cut where its level changes, a piece at an odd level drawn right to left
    /// by the caller's <c>piece</c> function, and L2 is applied to the pieces. A left-to-right
    /// paragraph with no right-to-left character and no direction control has no
    /// <see cref="Paragraph"/>, and its lines are handed back as they are, drawn as they always were.
    /// Resolved a line at a time, a bracket pair a line break splits would pair on neither line (N0),
    /// and a neutral at a line's start would take no direction from the line before.</para>
    /// <para><b>A <c>w:dir</c> or <c>w:bdo</c> is the controls ECMA-376 makes it</b> (§17.3.2.8,
    /// §17.3.2.3: an LRE/RLE or LRO/RLO before its runs and a PDF after them), each a segment of its
    /// own the caller marks atomic and draws as nothing; resolved over the paragraph, an embedding a
    /// line break splits goes on embedding. A piece inside an override is drawn with it (the
    /// <c>piece</c> function's <c>overridden</c>), since an override places a Latin letter right to
    /// left, which a level does not say.</para>
    /// </remarks>
    internal static class LineBidi
    {
        /// <summary>
        /// What an equation stands for in a line's logical text: one object, U+FFFC OBJECT
        /// REPLACEMENT CHARACTER, a neutral — as the Builder takes an inline box.
        /// </summary>
        internal static readonly string ObjectReplacement = new string((char)0xFFFC, 1);

        /// <summary>
        /// A paragraph's text resolved once (UAX #9, its levels and which characters an override
        /// holds), and where each of its lines lies in it, the lines asked for in order.
        /// </summary>
        /// <remarks>
        /// A wrapped line's text is the paragraph's, in order, but for the spaces a break took
        /// (<c>UnicodeText.SpacedWords</c>, <c>WrapWords</c>): so a line lies where its text next
        /// appears after nothing but whitespace. A line not found so — a wrap that changed a
        /// segment's text — is resolved alone and reported, never misplaced.
        /// </remarks>
        internal sealed class Paragraph
        {
            private readonly string _text;
            private readonly BidiParagraph _bidi;
            private readonly bool[] _overridden;
            private int _next;

            private Paragraph(string text, bool rtlParagraph)
            {
                _text = text;
                _bidi = UnicodeBidi.ResolveParagraph(text,
                    rtlParagraph ? BaseDirection.RightToLeft : BaseDirection.LeftToRight);
                _overridden = Overridden(text);
            }

            /// <summary>
            /// The paragraph its <paramref name="segments"/>, before wrapping, make — or null when it
            /// is left to right with no right-to-left character and no direction control, so none of
            /// its lines needs reordering.
            /// </summary>
            internal static Paragraph Of<T>(IEnumerable<T> segments, bool rtlParagraph, Func<T, string> logicalText)
            {
                var logical = new StringBuilder();
                foreach (var seg in segments) logical.Append(logicalText(seg));
                string text = logical.ToString();
                if (!rtlParagraph && !UnicodeBidi.ContainsRtl(text) && !HasControl(text)) return null;
                return new Paragraph(text, rtlParagraph);
            }

            /// <summary>
            /// Where <paramref name="line"/> lies: the first place at or after the end of the last
            /// line found that holds it with only whitespace skipped on the way.
            /// </summary>
            internal bool TryLocate(string line, out int start)
            {
                for (int s = _next; line.Length <= _text.Length - s; s++)
                {
                    if (string.CompareOrdinal(_text, s, line, 0, line.Length) == 0)
                    {
                        start = s;
                        _next = s + line.Length;
                        return true;
                    }
                    if (!char.IsWhiteSpace(_text[s])) break;
                }
                start = -1;
                return false;
            }

            /// <summary>
            /// The levels (L1 applied to the line) and override flags of <paramref name="line"/>,
            /// the next line of the paragraph: its part of the paragraph's, or the line's own when it
            /// cannot be found there.
            /// </summary>
            internal (int[] Levels, bool[] Overridden, int ParagraphLevel) LineOf(string line, bool rtlParagraph)
            {
                if (TryLocate(line, out int start))
                {
                    var overridden = new bool[line.Length];
                    Array.Copy(_overridden, start, overridden, 0, line.Length);
                    return (_bidi.LineLevels(start, line.Length), overridden, _bidi.ParagraphLevel);
                }
                DocxDiagnostics.Report("LineBidi.LineNotInParagraph",
                    "a wrapped line's text is not its paragraph's; the line's bidi was resolved on its own");
                var alone = new Paragraph(line, rtlParagraph);
                return (alone._bidi.LineLevels(0, line.Length), alone._overridden, alone._bidi.ParagraphLevel);
            }
        }

        /// <summary><paramref name="line"/> in visual order.</summary>
        /// <param name="line">The line's segments in logical order.</param>
        /// <param name="paragraph">
        /// The line's paragraph (<see cref="Paragraph.Of"/>), whose lines are asked for in order;
        /// null for one that needs no reordering.
        /// </param>
        /// <param name="rtlParagraph">Whether the paragraph is right to left (<c>w:bidi</c>).</param>
        /// <param name="logicalText">The characters a segment stands for in the line's text.</param>
        /// <param name="atomic">A segment never cut — a tab, an equation, a direction control.</param>
        /// <param name="piece">
        /// A one-level part of a segment as drawn at its level: (segment, its text, the level,
        /// whether it lies inside an override, whether the part is the whole segment).
        /// </param>
        internal static List<T> InVisualOrder<T>(List<T> line, Paragraph paragraph, bool rtlParagraph,
            Func<T, string> logicalText, Func<T, bool> atomic, Func<T, string, int, bool, bool, T> piece)
        {
            if (paragraph == null) return line;
            var logical = new StringBuilder();
            foreach (var seg in line) logical.Append(logicalText(seg));
            var (levels, overridden, paragraphLevel) = paragraph.LineOf(logical.ToString(), rtlParagraph);
            int at = 0;

            var pieces = new List<T>();
            var pieceLevels = new List<int>();
            foreach (var seg in line)
            {
                string own = logicalText(seg);
                if (atomic(seg) || own.Length == 0)
                {
                    pieces.Add(seg);
                    pieceLevels.Add(own.Length == 0 ? paragraphLevel : levels[at]);
                    at += own.Length;
                    continue;
                }
                int from = 0;
                for (int i = 1; i <= own.Length; i++)
                {
                    // Only a level change cuts: a control is a segment of its own, so a segment lies
                    // wholly inside an override or wholly outside one.
                    if (i < own.Length && levels[at + i] == levels[at + from]) continue;
                    pieces.Add(piece(seg, own.Substring(from, i - from), levels[at + from], overridden[at + from],
                        from == 0 && i == own.Length));
                    pieceLevels.Add(levels[at + from]);
                    from = i;
                }
                at += own.Length;
            }

            // L2: from the highest level down to the lowest odd one, reverse every run of pieces at
            // that level or above.
            int max = 0, minOdd = int.MaxValue;
            foreach (int level in pieceLevels)
            {
                if (level > max) max = level;
                if ((level & 1) == 1 && level < minOdd) minOdd = level;
            }
            for (int level = max; level >= minOdd; level--)
            {
                int runStart = -1;
                for (int i = 0; i <= pieces.Count; i++)
                {
                    if (i < pieces.Count && pieceLevels[i] >= level) { if (runStart < 0) runStart = i; }
                    else if (runStart >= 0)
                    {
                        pieces.Reverse(runStart, i - runStart);
                        pieceLevels.Reverse(runStart, i - runStart);
                        runStart = -1;
                    }
                }
            }
            return pieces;
        }

        private static bool HasControl(string text)
        {
            foreach (char c in text) if (UnicodeBidi.IsBidiControl(c)) return true;
            return false;
        }

        /// <summary>
        /// Whether each character of <paramref name="text"/> lies inside an override (Core's
        /// <see cref="BidiControlStack"/>).
        /// </summary>
        private static bool[] Overridden(string text)
        {
            var stack = new BidiControlStack();
            var overridden = new bool[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                stack.Feed(text[i]);
                overridden[i] = stack.OverrideDirection != null;
            }
            return overridden;
        }

        /// <summary>Whether a piece at <paramref name="level"/> is drawn right to left.</summary>
        internal static bool IsRightToLeft(int level) => (level & 1) == 1;

        /// <summary>
        /// Whether a whole shaped segment already reads in <paramref name="level"/>'s direction —
        /// its own shaping took the direction from its first strong character — so it need not be
        /// shaped again.
        /// </summary>
        internal static bool ShapedAsLevel(string text, int level)
            => IsRightToLeft(level) == UnicodeBidi.IsRightToLeftParagraph(text, BaseDirection.Auto);

        /// <summary>The direction a piece at <paramref name="level"/> is shaped in.</summary>
        internal static BaseDirection DirectionOf(int level)
            => IsRightToLeft(level) ? BaseDirection.RightToLeft : BaseDirection.LeftToRight;

        /// <summary>
        /// A standard-14 piece's text as drawn at <paramref name="level"/>: at an odd level reversed
        /// and mirrored (UAX #9 L4). Such a piece holds neutrals only, since a letter a standard-14
        /// run can draw is Latin and so at an even level.
        /// </summary>
        internal static string AsDrawn(string text, int level)
        {
            if (!IsRightToLeft(level)) return text;
            var drawn = new StringBuilder(text.Length);
            for (int i = text.Length - 1; i >= 0; i--) drawn.Append(UnicodeBidi.GetMirror(text[i]));
            return drawn.ToString();
        }
    }
}
