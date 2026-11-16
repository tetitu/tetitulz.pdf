// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The resolved bidi state of one paragraph: an embedding level per character, plus the
    /// per-line operations (L1 whitespace reset, L2 reordering, L4 mirroring) that depend on
    /// where the lines were broken.
    ///
    /// <para><b>Why this exists alongside <see cref="UnicodeBidi.Reorder(string)"/>.</b>
    /// <c>Reorder</c> answers "what does this string look like" and throws the levels away. A
    /// layout engine cannot use that: it must measure text in <i>logical</i> order to choose line
    /// breaks, and only then reorder — because L1 and L2 are defined per <b>line</b>, and the line
    /// is not known until the break is chosen. Handing back a reordered string forces a caller to
    /// reorder something smaller than a line, which is exactly the defect this type was added to
    /// fix: reordering per whitespace-delimited token makes a multi-word Latin phrase inside
    /// Hebrew render word-reversed, because each token resolves its own base direction.</para>
    ///
    /// <para>Levels are resolved once for the paragraph; <see cref="VisualOrder"/> is then called
    /// per line. That split is the whole point — the expensive part is shared, and the part that
    /// genuinely varies per line is not.</para>
    /// </summary>
    public sealed class BidiParagraph
    {
        // L1 reset classes, from the ORIGINAL character types (UAX #9 L1 is explicit that the
        // original types are used, not the resolved ones).
        internal const byte L1None = 0;
        internal const byte L1Segment = 1;   // S — segment separator (tab)
        internal const byte L1Paragraph = 2; // B — paragraph separator (newline)
        internal const byte L1Whitespace = 3; // WS or an isolate formatting character

        private readonly string _text;
        private readonly int[] _levels;
        private readonly byte[] _l1Class;

        internal BidiParagraph(string text, int[] levels, byte[] l1Class, int paragraphLevel)
        {
            _text = text;
            _levels = levels;
            _l1Class = l1Class;
            ParagraphLevel = paragraphLevel;
        }

        /// <summary>The paragraph embedding level, 0 (left-to-right) or 1 (right-to-left).</summary>
        public int ParagraphLevel { get; }

        /// <summary>True when the paragraph reads right-to-left.</summary>
        public bool IsRightToLeft => (ParagraphLevel & 1) == 1;

        /// <summary>The paragraph text's length in UTF-16 code units.</summary>
        public int Length => _text.Length;

        /// <summary>The resolved embedding level of the character at <paramref name="index"/>.</summary>
        public int LevelAt(int index)
        {
            if (index < 0 || index >= _levels.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _levels[index];
        }

        /// <summary>
        /// The character to <b>draw</b> at logical <paramref name="index"/>: mirrored (UAX #9 L4)
        /// when it sits at an odd embedding level, so <c>(</c> in a right-to-left run draws as
        /// <c>)</c>.
        ///
        /// <para>Keyed on the <b>logical</b> index deliberately. Applying L4 by walking the
        /// already-reordered output and consulting <c>levels[i]</c> — as a string-returning
        /// implementation is tempted to do — pairs each output position with the level of a
        /// different character, and mirrors the wrong ones whenever a line carries more than one
        /// level. Returning characters by logical index makes that mistake unavailable.</para>
        /// </summary>
        public char MirroredCharAt(int index)
        {
            if (index < 0 || index >= _text.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            char c = _text[index];
            return (_levels[index] & 1) == 1 ? UnicodeBidi.GetMirror(c) : c;
        }

        /// <summary>
        /// The display order of one line: UAX #9 L1 (reset separators and trailing whitespace to
        /// the paragraph level) followed by L2 (reverse runs from the highest level down to the
        /// lowest odd level), over <c>[start, start+length)</c> only.
        /// </summary>
        /// <returns>
        /// <b>Logical</b> indices, in left-to-right visual order. The caller draws
        /// <c>MirroredCharAt(result[0])</c> leftmost, and so on.
        /// </returns>
        public int[] VisualOrder(int start, int length)
        {
            // `length > Length - start`, not `start + length > Length`: the sum overflows for a
            // large start and passed the check, failing later as an IndexOutOfRangeException.
            if (start < 0 || length < 0 || length > _text.Length - start)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (length == 0) return Array.Empty<int>();

            int[] lv = LineLevels(start, length);

            var order = new int[length];
            for (int i = 0; i < length; i++) order[i] = start + i;

            int maxLevel = 0, minOddLevel = int.MaxValue;
            for (int i = 0; i < length; i++)
            {
                if (lv[i] > maxLevel) maxLevel = lv[i];
                if ((lv[i] & 1) == 1 && lv[i] < minOddLevel) minOddLevel = lv[i];
            }

            // L2. `lv` stays in logical order throughout while only `order` is reversed: a run at
            // a higher level is wholly contained in the run at each lower level, so the logical
            // levels remain the correct guide on every pass.
            for (int level = maxLevel; level >= minOddLevel; level--)
            {
                int runStart = -1;
                for (int i = 0; i <= length; i++)
                {
                    if (i < length && lv[i] >= level)
                    {
                        if (runStart < 0) runStart = i;
                    }
                    else if (runStart >= 0)
                    {
                        Reverse(order, runStart, i - 1);
                        runStart = -1;
                    }
                }
            }

            KeepSurrogatePairsTogether(order);
            return order;
        }

        /// <summary>
        /// The levels of one line, <c>[start, start+length)</c>, after L1 — what L2 reverses by, and what Unicode's
        /// conformance file states. A copy, because L1 is a per-LINE reset: the same paragraph broken differently
        /// resets differently, so it is never written back to the paragraph's levels.
        /// </summary>
        internal int[] LineLevels(int start, int length)
        {
            var lv = new int[length];
            for (int i = 0; i < length; i++) lv[i] = _levels[start + i];
            ApplyL1(lv, start, length);
            return lv;
        }

        // UAX #9 L1: reset to the paragraph level (1) segment separators, (2) paragraph
        // separators, (3) any run of whitespace/isolate formatting characters immediately before
        // one of those, and (4) any such run at the end of the line.
        //
        // Its absence is why trailing punctuation and spaces landed at the wrong end of a
        // right-to-left line: with nothing resetting them, they kept whatever level N1/N2 had
        // inferred from their neighbours.
        private void ApplyL1(int[] lv, int start, int length)
        {
            int wsRunStart = -1;
            for (int i = 0; i < length; i++)
            {
                byte cls = _l1Class[start + i];
                if (cls == L1Whitespace)
                {
                    if (wsRunStart < 0) wsRunStart = i;
                    continue;
                }
                if (cls == L1Segment || cls == L1Paragraph)
                {
                    if (wsRunStart >= 0)
                        for (int k = wsRunStart; k < i; k++) lv[k] = ParagraphLevel;
                    lv[i] = ParagraphLevel;
                }
                wsRunStart = -1;
            }
            if (wsRunStart >= 0)
                for (int k = wsRunStart; k < length; k++) lv[k] = ParagraphLevel;
        }

        // A surrogate pair is one character and must not be turned inside out by an L2 reversal.
        // Reversing UTF-16 code units blindly — which a char-array implementation does — emits the
        // low surrogate first and corrupts every astral character inside a right-to-left run.
        private void KeepSurrogatePairsTogether(int[] order)
        {
            for (int i = 0; i + 1 < order.Length; i++)
            {
                int first = order[i], second = order[i + 1];
                if (second == first - 1
                    && char.IsHighSurrogate(_text[second])
                    && char.IsLowSurrogate(_text[first]))
                {
                    order[i] = second;
                    order[i + 1] = first;
                    i++;
                }
            }
        }

        private static void Reverse(int[] arr, int lo, int hi)
        {
            while (lo < hi) { int t = arr[lo]; arr[lo++] = arr[hi]; arr[hi--] = t; }
        }
    }
}
