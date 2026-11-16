// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts.Cff
{
    /// <summary>
    /// Walks a Type 2 charstring far enough to answer one question: <b>which other glyphs does it
    /// need?</b> It builds no outline and computes no coordinate.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not a second copy of <c>Base.Rendering.Type2CharstringEvaluator</c>.</b>
    /// That one turns a charstring into an <c>SKPath</c>; it lives in <c>Base</c>, needs SkiaSharp,
    /// and is unreachable from here — <c>Core</c> may not reference <c>Base</c>, and
    /// <c>Tetitulz.Pdf.Builder</c>, one of the callers this exists for, may reference neither. The
    /// jobs also differ: the evaluator may guess and draw something slightly wrong, whereas this
    /// must either be certain or refuse, because its answer decides which bytes get overwritten.</para>
    ///
    /// <para><b>The whole point is <c>seac</c>.</b> An accented glyph is normally stored as
    /// <c>adx ady bchar achar endchar</c> (TN #5177 §4.12 note J) — it draws two <i>other</i>
    /// glyphs, named by Adobe StandardEncoding code, and those may look unused to a subsetter
    /// counting only what the document's cmap resolved. Blanking a component turns "é" into an
    /// acute accent floating over nothing, in a font that still loads. This walker is what stops
    /// that.</para>
    ///
    /// <para><b>It refuses far more readily than it guesses</b>, and the two operator groups it
    /// refuses on are chosen deliberately:</para>
    /// <list type="bullet">
    ///   <item>The <b>arithmetic and storage</b> operators (<c>12 3</c>–<c>12 30</c>: <c>and</c>,
    ///     <c>add</c>, <c>div</c>, <c>put</c>/<c>get</c>, <c>ifelse</c>, <c>roll</c>, …) rearrange
    ///     the stack in ways that depend on values this walker does not track, so after one of them
    ///     it can no longer say how many operands <c>endchar</c> will see. They are near-extinct in
    ///     real Type 2 charstrings — they exist mostly for Type 1 conversion — so refusing costs a
    ///     subset almost nobody would have got, and guessing costs a broken glyph.</item>
    ///   <item>An <b>unknown</b> operator, or a subroutine index outside its INDEX. Either means the
    ///     font is not what this walker thinks it is.</item>
    /// </list>
    /// <para>The flex family (<c>12 34</c>–<c>12 37</c>) and <c>dotsection</c> (<c>12 0</c>) are
    /// modelled, because they are ordinary in shipping fonts and all they do to the stack is clear
    /// it.</para>
    /// </remarks>
    internal static class CffCharstringScanner
    {
        /// <summary>
        /// Appends to <paramref name="componentCodes"/> the StandardEncoding codes of every
        /// <c>seac</c> component glyph <paramref name="charstring"/> draws, following
        /// <c>callsubr</c>/<c>callgsubr</c> into <paramref name="localSubrs"/> and
        /// <paramref name="globalSubrs"/>.
        /// </summary>
        /// <returns>
        /// <c>false</c> when the charstring contains something this walker will not reason about,
        /// in which case <paramref name="componentCodes"/> must not be trusted and the caller is
        /// expected to abandon the subset rather than continue with a partial answer.
        /// </returns>
        internal static bool TryCollectSeacComponents(
            byte[] charstring,
            IReadOnlyList<byte[]> localSubrs,
            IReadOnlyList<byte[]> globalSubrs,
            List<int> componentCodes,
            CharstringWork.FontAccount fontSteps = null)
        {
            if (charstring == null || charstring.Length == 0) return true;

            var state = new ScanState
            {
                LocalSubrs = localSubrs ?? Array.Empty<byte[]>(),
                GlobalSubrs = globalSubrs ?? Array.Empty<byte[]>(),
                Components = componentCodes,
            };

            return Scan(charstring, state, fontSteps);
        }

        /// <summary>
        /// Walks under the steps the per-glyph cap, the open page's budget and the font's allow, and
        /// charges what it spent to the page and the font: past either, the walk stops and refuses, as
        /// at the per-glyph cap (<see cref="CharstringWork"/>).
        /// </summary>
        private static bool Scan(byte[] charstring, ScanState state, CharstringWork.FontAccount fontSteps)
        {
            int allowed = CharstringWork.StepsAllowed(fontSteps?.Left ?? long.MaxValue);
            state.StepsLeft = allowed;
            try { return Walk(charstring, state); }
            finally
            {
                long spent = (long)allowed - Math.Max(0, state.StepsLeft);
                CharstringWork.Charge(spent, allowed);
                fontSteps?.Spend(spent);
            }
        }

        /// <summary>
        /// Reads the optional advance-width operand a Type 2 charstring carries before its first
        /// stack-clearing operator (TN #5177 §3.1), following <c>callsubr</c>/<c>callgsubr</c>.
        /// </summary>
        /// <param name="charstring">The glyph's charstring.</param>
        /// <param name="localSubrs">The Private DICT's local subroutines for this glyph.</param>
        /// <param name="globalSubrs">The global subroutines.</param>
        /// <param name="width">
        /// The width operand — to be added to the Private DICT's <c>nominalWidthX</c> — or <c>null</c>
        /// when the charstring carries none and <c>defaultWidthX</c> applies.
        /// </param>
        /// <param name="fontSteps">The font's own step account (<see cref="CffFont.ScanSteps"/>), or null.</param>
        /// <returns>
        /// <c>false</c> when the width cannot be known: the charstring is empty, draws before its first
        /// moveto, ends without a stack-clearing operator, or contains anything
        /// <see cref="TryCollectSeacComponents"/> would refuse.
        /// </returns>
        /// <remarks>
        /// The width is recognised by operand parity at the first <c>hstem</c>, <c>vstem</c>,
        /// <c>hstemhm</c>, <c>vstemhm</c>, <c>hintmask</c>, <c>cntrmask</c>, <c>rmoveto</c>,
        /// <c>hmoveto</c>, <c>vmoveto</c> or <c>endchar</c>: one more operand than the operator
        /// takes. Stem operators take pairs, <c>rmoveto</c> two, <c>hmoveto</c>/<c>vmoveto</c> one,
        /// and <c>endchar</c> none or four (the <c>seac</c> form). The walk stops there, so nothing
        /// after the first such operator is read.
        /// </remarks>
        internal static bool TryReadWidth(
            byte[] charstring,
            IReadOnlyList<byte[]> localSubrs,
            IReadOnlyList<byte[]> globalSubrs,
            out double? width,
            CharstringWork.FontAccount fontSteps = null)
        {
            width = null;
            if (charstring == null || charstring.Length == 0) return false;

            var state = new ScanState
            {
                LocalSubrs = localSubrs ?? Array.Empty<byte[]>(),
                GlobalSubrs = globalSubrs ?? Array.Empty<byte[]>(),
                WidthOnly = true,
            };

            if (!Scan(charstring, state, fontSteps) || !state.WidthRead) return false;
            width = state.Width;
            return true;
        }

        /// <summary>
        /// Ends a width-only walk at its first stack-clearing operator: the width is present when the
        /// operand count exceeds what the operator takes by exactly one.
        /// </summary>
        private static bool FinishWidth(ScanState st, bool hasWidth)
        {
            st.Width = hasWidth ? st.Stack[0] : (double?)null;
            st.WidthRead = true;
            st.Depth--;
            return true;
        }

        /// <summary>
        /// The bias added to a <c>callsubr</c>/<c>callgsubr</c> operand (TN #5177 §4.7). Duplicated
        /// arithmetic rather than a shared helper because the only other copy is in <c>Base</c>,
        /// which this assembly cannot reference; it is three constants and cannot drift silently —
        /// <c>CffSeacSubsettingTests</c> exercises a biased subroutine call.
        /// </summary>
        private static int SubrBias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        private sealed class ScanState
        {
            internal IReadOnlyList<byte[]> LocalSubrs;
            internal IReadOnlyList<byte[]> GlobalSubrs;
            internal List<int> Components;
            internal readonly List<double> Stack = new List<double>();
            internal int StemCount;
            internal int Depth;
            internal int StepsLeft;
            internal bool EndcharSeen;

            /// <summary>Set by <see cref="TryReadWidth"/>: stop at the first stack-clearing operator.</summary>
            internal bool WidthOnly;
            internal bool WidthRead;
            internal double? Width;
        }

        private static bool Walk(byte[] cs, ScanState st)
        {
            if (++st.Depth > PdfLimits.MaxCharstringCallDepth) return false;

            int pos = 0;
            while (pos < cs.Length)
            {
                if (--st.StepsLeft <= 0) return false;

                byte b0 = cs[pos++];

                // ---- Operands (TN #5177 §4) ----
                if (b0 >= 32 || b0 == 28)
                {
                    if (!TryReadOperand(cs, ref pos, b0, st)) return false;
                    continue;
                }

                switch (b0)
                {
                    // Stem hints: the operand count (less an optional leading width) is twice the
                    // number of stems, and hintmask's size depends on the running total.
                    case 1:  // hstem
                    case 3:  // vstem
                    case 18: // hstemhm
                    case 23: // vstemhm
                        if (st.WidthOnly) return FinishWidth(st, st.Stack.Count % 2 == 1);
                        st.StemCount += st.Stack.Count / 2;
                        st.Stack.Clear();
                        break;

                    case 19: // hintmask
                    case 20: // cntrmask
                        if (st.WidthOnly) return FinishWidth(st, st.Stack.Count % 2 == 1);
                        // Operands still on the stack here are implicit vstem hints (§4.3), so they
                        // widen the mask that follows. Getting this wrong desynchronises the whole
                        // rest of the charstring, which is why it is modelled rather than skipped.
                        st.StemCount += st.Stack.Count / 2;
                        st.Stack.Clear();
                        pos += (st.StemCount + 7) / 8;
                        if (pos > cs.Length) return false;
                        break;

                    case 10: // callsubr
                    case 29: // callgsubr
                    {
                        if (st.Stack.Count == 0) return false;
                        var subrs = b0 == 10 ? st.LocalSubrs : st.GlobalSubrs;
                        double raw = st.Stack[st.Stack.Count - 1];
                        st.Stack.RemoveAt(st.Stack.Count - 1);
                        if (double.IsNaN(raw) || raw < int.MinValue || raw > int.MaxValue) return false;
                        int idx = SaturatingCast.ToInt(raw) + SubrBias(subrs.Count);
                        if (idx < 0 || idx >= subrs.Count) return false;
                        if (!Walk(subrs[idx], st)) return false;
                        if (st.EndcharSeen || st.WidthRead) { st.Depth--; return true; }
                        break;
                    }

                    case 11: // return
                        st.Depth--;
                        return true;

                    case 14: // endchar — the operator this whole walker exists for
                        if (st.WidthOnly)
                        {
                            // None or four operands (seac) without a width; one or five with one.
                            int n = st.Stack.Count;
                            if (n != 0 && n != 1 && n != 4 && n != 5) return false;
                            return FinishWidth(st, n == 1 || n == 5);
                        }
                        // Four or five operands is the deprecated seac form: the last two are the
                        // base and accent character codes. Reading them from the TOP of the stack
                        // sidesteps the width ambiguity entirely — whether or not a leading width
                        // operand is present, bchar and achar are always the final pair.
                        if (st.Stack.Count >= 4)
                        {
                            double achar = st.Stack[st.Stack.Count - 1];
                            double bchar = st.Stack[st.Stack.Count - 2];
                            if (!TryAddComponent(st, bchar) || !TryAddComponent(st, achar)) return false;
                        }
                        st.Stack.Clear();
                        st.EndcharSeen = true;
                        st.Depth--;
                        return true;

                    case 12: // escape
                    {
                        if (pos >= cs.Length) return false;
                        byte b1 = cs[pos++];
                        switch (b1)
                        {
                            case 0:  // dotsection (deprecated no-op)
                                if (st.WidthOnly && st.Stack.Count > 0) return false;
                                st.Stack.Clear();
                                break;
                            case 34: // hflex
                            case 35: // flex
                            case 36: // hflex1
                            case 37: // flex1
                                // Drawing before the first moveto: not a charstring whose width
                                // position can be trusted.
                                if (st.WidthOnly) return false;
                                st.Stack.Clear();
                                break;
                            default:
                                // Arithmetic / storage / conditionals, and anything unassigned. See
                                // the class remarks: after one of these the operand count at a later
                                // endchar is no longer knowable, so the subset is abandoned.
                                return false;
                        }
                        break;
                    }

                    case 4:  // vmoveto
                    case 22: // hmoveto
                        if (st.WidthOnly)
                        {
                            if (st.Stack.Count != 1 && st.Stack.Count != 2) return false;
                            return FinishWidth(st, st.Stack.Count == 2);
                        }
                        st.Stack.Clear();
                        break;

                    case 21: // rmoveto
                        if (st.WidthOnly)
                        {
                            if (st.Stack.Count != 2 && st.Stack.Count != 3) return false;
                            return FinishWidth(st, st.Stack.Count == 3);
                        }
                        st.Stack.Clear();
                        break;

                    // Path construction and painting: every one is stack-clearing (§4.1), and none
                    // may come before the first moveto, where a width would have been read.
                    case 5:  // rlineto
                    case 6:  // hlineto
                    case 7:  // vlineto
                    case 8:  // rrcurveto
                    case 24: // rcurveline
                    case 25: // rlinecurve
                    case 26: // vvcurveto
                    case 27: // hhcurveto
                    case 30: // vhcurveto
                    case 31: // hvcurveto
                        if (st.WidthOnly) return false;
                        st.Stack.Clear();
                        break;

                    default:
                        // 0, 2, 9, 13, 15, 16, 17 — reserved in Type 2, or CFF2-only (vsindex,
                        // blend). A CFF1 charstring containing one is not a font this walker
                        // understands.
                        return false;
                }
            }

            st.Depth--;
            return true;
        }

        private static bool TryReadOperand(byte[] cs, ref int pos, byte b0, ScanState st)
        {
            // An unbounded stack is the amplification a hostile charstring reaches for; the
            // interpreter limit is 48 operands (§4), so anything past it is malformed either way.
            if (st.Stack.Count >= 48) return false;

            double value;
            if (b0 == 28)
            {
                if (pos + 2 > cs.Length) return false;
                value = (short)((cs[pos] << 8) | cs[pos + 1]);
                pos += 2;
            }
            else if (b0 <= 246)
            {
                value = b0 - 139;
            }
            else if (b0 <= 250)
            {
                if (pos >= cs.Length) return false;
                value = (b0 - 247) * 256 + cs[pos++] + 108;
            }
            else if (b0 <= 254)
            {
                if (pos >= cs.Length) return false;
                value = -((b0 - 251) * 256) - cs[pos++] - 108;
            }
            else // 255 — 16.16 fixed
            {
                if (pos + 4 > cs.Length) return false;
                int fixedValue = (cs[pos] << 24) | (cs[pos + 1] << 16) | (cs[pos + 2] << 8) | cs[pos + 3];
                pos += 4;
                value = fixedValue / 65536.0;
            }

            st.Stack.Add(value);
            return true;
        }

        private static bool TryAddComponent(ScanState st, double code)
        {
            // A component code outside 0–255 is not a StandardEncoding code, so the charstring is
            // not the seac form this walker recognised — refuse rather than resolve nothing and
            // report success.
            if (double.IsNaN(code) || code < 0 || code > 255) return false;
            st.Components.Add(SaturatingCast.ToInt(code));
            return true;
        }
    }
}
