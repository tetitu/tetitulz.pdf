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
    /// Evaluates a CFF Type 2 charstring (Adobe Technical Note #5177 "Type 2 Charstring Format")
    /// into an <see cref="IGlyphPathSink"/>, answering the glyph's advance width and its horizontal
    /// stems. The raster renderer draws through it (Base's <c>Type2CharstringEvaluator</c>, over a
    /// SkiaSharp path), and so does <see cref="CffFontSet"/>'s outline for a reader outside the
    /// renderers — one evaluator, the path its caller's.
    /// </summary>
    internal static class Type2Charstrings
    {
        // ----------------------------------------------------------------
        //  Result
        // ----------------------------------------------------------------

        internal struct Result
        {
            /// <summary>Advance width in charspace units (1/1000 em).</summary>
            public float  AdvanceWidth;
            /// <summary>
            /// Horizontal stem zones (y0, height) in charspace units.  Populated
            /// from the charstring's hstem / hstemhm operators for use by the
            /// grid-fitter (Type 1 / TrueType hinting).  Empty list when the
            /// charstring had no horizontal hints.
            /// </summary>
            public IReadOnlyList<(float y0, float h)> HorizontalStems;
            /// <summary>
            /// The page's charstring budget stopped this glyph short (<c>CharstringWork</c>): the outline
            /// is partial, and is not the glyph's.
            /// </summary>
            public bool CutByBudget;
            /// <summary>The charstring steps the evaluation ran.</summary>
            public int Steps;
        }

        // ----------------------------------------------------------------
        //  Entry point
        // ----------------------------------------------------------------

        /// <summary>
        /// Evaluates the charstring for a given GID into <paramref name="path"/> and answers its width
        /// and stems. A GID outside the font draws nothing; a malformed charstring leaves whatever it
        /// drew before it failed. A caller outside any page passes the font's own
        /// <paramref name="account"/> (<see cref="CffFont.ScanSteps"/>), which the glyph's steps then
        /// spend and which limits them.
        /// </summary>
        public static Result Evaluate(CffFont font, int gid, IGlyphPathSink path, CharstringWork.FontAccount account = null)
        {
            if (gid < 0 || gid >= font.Charstrings.Count)
                return new Result
                {
                    AdvanceWidth = font.DefaultWidthX,
                    HorizontalStems = Array.Empty<(float, float)>(),
                };

            var state = new EvalState(font);

            // For CID-keyed fonts with multiple FDs, override per-GID private data.
            if (font.LocalSubrsPerGid != null && gid < font.LocalSubrsPerGid.Length && font.LocalSubrsPerGid[gid] != null)
                state.GlyphLocalSubrs = font.LocalSubrsPerGid[gid];
            if (font.DefaultWidthsPerGid != null && gid < font.DefaultWidthsPerGid.Length)
                state.EffectiveDefaultWidth = font.DefaultWidthsPerGid[gid];
            if (font.NominalWidthsPerGid != null && gid < font.NominalWidthsPerGid.Length)
                state.EffectiveNominalWidth = font.NominalWidthsPerGid[gid];

            state.Path = path;
            state.StepLimit = CharstringWork.StepsAllowed(account?.Left ?? long.MaxValue);

            try
            {
                ExecuteCharstring(font.Charstrings[gid], state);
            }
            catch
            {
                // Malformed charstring — return whatever path was built.
            }
            bool cut = CharstringWork.Charge(state.Steps, state.StepLimit);
            account?.Spend(state.Steps);

            float advance = state.HasExplicitWidth
                ? state.EffectiveNominalWidth + state.Width
                : state.EffectiveDefaultWidth;

            return new Result
            {
                AdvanceWidth = advance,
                HorizontalStems = state.HorizontalStems.ToArray(),
                CutByBudget = cut,
                Steps = state.Steps,
            };
        }

        // ----------------------------------------------------------------
        //  State
        // ----------------------------------------------------------------

        private sealed class EvalState
        {
            public readonly CffFont Font;
            public IGlyphPathSink Path;
            // Current point in charspace
            public float   X, Y;
            // Operand stack
            public readonly List<float> Stack = new List<float>(48);
            // Hint state.  StemCount tracks the total number of stems encountered
            // (used for hintmask byte sizing).  HorizontalStems collects h-stem
            // (dx, y0, h) pairs in charspace — consumed by the future grid-fitter
            // to snap glyph edges to pixel rows at small sizes.  Empty today until
            // the snap path lands.
            public int     StemCount;
            public readonly List<(float y0, float h)> HorizontalStems = new List<(float, float)>();
            public bool    SeenMoveto;
            // Width
            public float   Width;
            public bool    HasExplicitWidth;
            // While an endchar-seac component runs (see ExecuteSeacComponent).
            public bool    InSeacComponent;
            public float   StemYOffset;
            // Subroutine call depth
            public int     CallDepth;
            // The random operator's generator (xorshift32), restarted for every glyph: a glyph
            // draws the same whatever was drawn before it and on whichever thread. It was a
            // per-thread System.Random, so a font using random drew differently from run to run.
            private uint   _random = 0x2545F491;
            // Total operators executed across the whole glyph evaluation. MaxCallDepth bounds
            // recursion *depth* but not the multiplicative *width* of subr calls: a font whose
            // subrs each invoke the next K times explodes to K^depth path operations (a charstring
            // "billion-laughs"). This per-evaluation counter bounds total work regardless of nesting.
            public int     Steps;
            // The steps this glyph may run: the per-glyph cap, or less when the page's budget has less left.
            public int     StepLimit;
            // Per TN #5177 §3.2: endchar in a subroutine terminates the ENTIRE charstring.
            public bool    EndcharSeen;
            // Delta operands per blended value for the CFF2 blend operator — the region count of
            // the active ItemVariationData (CFF2, TN #5177 §5.3), not the axis count.
            public int     BlendRegionCount;
            // Per-GID overrides for CID-keyed multi-FD fonts (null = use Font.LocalSubrs).
            public IReadOnlyList<byte[]> GlyphLocalSubrs;
            public float   EffectiveDefaultWidth;
            public float   EffectiveNominalWidth;

            public EvalState(CffFont font)
            {
                Font                 = font;
                BlendRegionCount     = font.BlendRegionCount;
                EffectiveDefaultWidth = font.DefaultWidthX;
                EffectiveNominalWidth = font.NominalWidthX;
            }

            /// <summary>
            /// The random operator's value: greater than 0 and at most 1 (Adobe TN #5177, the random operator), on
            /// a 24-bit grid a float holds exactly. <c>NextDouble</c> could answer 0, which the
            /// specification excludes.
            /// </summary>
            public float NextRandom()
            {
                _random ^= _random << 13;
                _random ^= _random >> 17;
                _random ^= _random << 5;
                return ((_random >> 8) + 1) / 16777216f;
            }

            public void Push(float v) => Stack.Add(v);
            public float Pop()
            {
                int last = Stack.Count - 1;
                float v = Stack[last];
                Stack.RemoveAt(last);
                return v;
            }
            public void ClearStack() => Stack.Clear();
        }

        // ----------------------------------------------------------------
        //  Charstring execution
        // ----------------------------------------------------------------

        private static int MaxCallDepth => PdfLimits.MaxCharstringCallDepth;
        // Total executed operators are capped per glyph (EvalState.StepLimit: PdfLimits.MaxCharstringSteps,
        // or what the page's budget has left, CharstringWork). A legitimate glyph runs at most a few
        // thousand; the cap bounds an adversarial nested-subr explosion while never tripping on real
        // fonts. Once exceeded, every loop level breaks on its next iteration (Steps never decreases)
        // and the recursion unwinds in O(CallDepth).

        private static void ExecuteCharstring(byte[] cs, EvalState st)
        {
            if (cs == null || cs.Length == 0) return;
            if (++st.CallDepth > MaxCallDepth) { --st.CallDepth; return; }

            int pos = 0;
            while (pos < cs.Length)
            {
                if (++st.Steps > st.StepLimit) break;
                byte b0 = cs[pos++];

                // ---- Numbers ----
                // Note: in Type 2 charstrings, byte 28 is shortint (2-byte), byte 255 is
                // fixed 16.16 (4-byte). There is NO 4-byte integer opcode — byte 29 is the
                // callgsubr operator (TN #5177 Table 1). Do NOT add a b0==29 integer case here.
                if (b0 == 28)
                {
                    if (pos + 1 >= cs.Length) break;
                    short v = (short)((cs[pos] << 8) | cs[pos + 1]);
                    pos += 2;
                    st.Push(v);
                    continue;
                }
                if (b0 == 255)
                {
                    // Fixed 16.16
                    if (pos + 3 >= cs.Length) break;
                    int raw = (cs[pos] << 24) | (cs[pos+1] << 16) | (cs[pos+2] << 8) | cs[pos+3];
                    pos += 4;
                    st.Push(raw / 65536f);
                    continue;
                }
                if (b0 >= 32 && b0 <= 246)  { st.Push(b0 - 139); continue; }
                if (b0 >= 247 && b0 <= 250) { if (pos >= cs.Length) break; st.Push((b0-247)*256 + cs[pos++] + 108); continue; }
                if (b0 >= 251 && b0 <= 254) { if (pos >= cs.Length) break; st.Push(-((b0-251)*256) - cs[pos++] - 108); continue; }

                // ---- Operators ----
                switch (b0)
                {
                    case 1:  // hstem
                    case 18: // hstemhm
                        OpStem(st, horizontal: true);
                        break;
                    case 3:  // vstem
                    case 23: // vstemhm
                        OpStem(st, horizontal: false);
                        break;

                    case 4:  // vmoveto
                        OpVMoveto(st);
                        break;
                    case 21: // rmoveto
                        OpRMoveto(st);
                        break;
                    case 22: // hmoveto
                        OpHMoveto(st);
                        break;

                    case 5:  // rlineto
                        OpRLineto(st);
                        break;
                    case 6:  // hlineto
                        OpHVLineto(st, horizFirst: true);
                        break;
                    case 7:  // vlineto
                        OpHVLineto(st, horizFirst: false);
                        break;

                    case 8:  // rrcurveto
                        OpRRCurveto(st);
                        break;
                    case 24: // rcurveline
                        OpRCurveline(st);
                        break;
                    case 25: // rlinecurve
                        OpRLinecurve(st);
                        break;
                    case 26: // vvcurveto
                        OpVVCurveto(st);
                        break;
                    case 27: // hhcurveto
                        OpHHCurveto(st);
                        break;
                    case 30: // vhcurveto
                        OpVHCurveto(st);
                        break;
                    case 31: // hvcurveto
                        OpHVCurveto(st);
                        break;

                    case 19: // hintmask
                    case 20: // cntrmask
                        // hintmask/cntrmask consume (stemCount+7)/8 bytes
                        // Any remaining stack args are implicit vstem hints — and, as the first
                        // stack-clearing operator, an odd count carries the width (OpStem reads both).
                        if (st.Stack.Count > 0) OpStem(st, horizontal: false);
                        int maskBytes = (st.StemCount + 7) / 8;
                        pos += maskBytes; // skip mask bytes
                        break;

                    case 15: // vsindex (CFF2 §5.3): selects the active ItemVariationData; consume
                             // its single operand so it can't misalign the following operator. We
                             // size blend from the default (vsindex 0) IVD, so switching to another
                             // IVD with a different region count is not applied (rare; documented).
                        if (st.Stack.Count > 0) st.Pop();
                        break;

                    case 16: // blend (CFF2 §5.3): pop count n, keep n defaults, discard n*k deltas
                    {
                        if (st.Stack.Count < 1) break;
                        int n = SaturatingCast.ToInt(st.Pop()); // count of values being blended
                        if (n < 0 || n > st.Stack.Count) break;
                        int k = st.BlendRegionCount;
                        int deltasCount = n * k;
                        // Stack below n: [prev_values] [n defaults] [n*k deltas].
                        // Remove the deltas; leave the n defaults in place.
                        int firstDelta = st.Stack.Count - deltasCount;
                        if (deltasCount > 0 && firstDelta >= n && firstDelta >= 0)
                            st.Stack.RemoveRange(firstDelta, deltasCount);
                        break;
                    }

                    case 10: // callsubr
                        if (st.Stack.Count > 0)
                        {
                            var lsubrs = st.GlyphLocalSubrs ?? st.Font.LocalSubrs;
                            int idx = SaturatingCast.ToInt(st.Pop()) + SubrBias(lsubrs.Count);
                            if (idx >= 0 && idx < lsubrs.Count)
                            {
                                ExecuteCharstring(lsubrs[idx], st);
                                // endchar inside a subroutine terminates the entire charstring (TN #5177 §3.2).
                                if (st.EndcharSeen) { --st.CallDepth; return; }
                            }
                        }
                        break;
                    case 29: // callgsubr
                        if (st.Stack.Count > 0)
                        {
                            int idx = SaturatingCast.ToInt(st.Pop()) + SubrBias(st.Font.GlobalSubrs.Count);
                            if (idx >= 0 && idx < st.Font.GlobalSubrs.Count)
                            {
                                ExecuteCharstring(st.Font.GlobalSubrs[idx], st);
                                if (st.EndcharSeen) { --st.CallDepth; return; }
                            }
                        }
                        break;
                    case 11: // return
                        --st.CallDepth;
                        return;

                    case 14: // endchar
                        OpEndchar(st);
                        st.EndcharSeen = true;
                        --st.CallDepth;
                        return;

                    case 12: // escape — two-byte operators
                        if (pos >= cs.Length) { --st.CallDepth; return; }
                        ExecuteEscape(cs[pos++], st);
                        break;

                    default:
                        // Unknown operator — clear stack and continue
                        st.ClearStack();
                        break;
                }
            }

            --st.CallDepth;
        }

        // ----------------------------------------------------------------
        //  Escaped operators (after byte 12)
        // ----------------------------------------------------------------

        private static void ExecuteEscape(byte op, EvalState st)
        {
            switch (op)
            {
                case 0:  // dotsection (deprecated, ignore)
                    st.ClearStack();
                    break;
                case 34: // hflex
                    OpHFlex(st);
                    break;
                case 35: // flex
                    OpFlex(st);
                    break;
                case 36: // hflex1
                    OpHFlex1(st);
                    break;
                case 37: // flex1
                    OpFlex1(st);
                    break;
                // Arithmetic ops (rarely seen in outlines; clear stack)
                case 3: case 4: case 5: case 9: case 10: case 11: case 12:
                case 14: case 15: case 18: case 20: case 21: case 22: case 23:
                case 24: case 26: case 27: case 28: case 29: case 30:
                    ExecuteArithmetic(op, st);
                    break;
                default:
                    st.ClearStack();
                    break;
            }
        }

        // ----------------------------------------------------------------
        //  Drawing operators
        // ----------------------------------------------------------------

        private static void OpStem(EvalState st, bool horizontal = false)
        {
            // Width may be first arg if stack has odd count
            if (st.Stack.Count % 2 != 0 && !st.HasExplicitWidth)
            {
                st.Width = st.Stack[0];
                st.HasExplicitWidth = true;
                st.Stack.RemoveAt(0);
            }
            int count = st.Stack.Count / 2;
            if (horizontal)
            {
                // Horizontal stems: pairs of (y_delta, height_delta) relative to
                // the previous stem's top.  First pair is relative to y = 0 — the
                // component's origin while a seac component runs.
                float y = st.StemYOffset;
                for (int i = 0; i + 1 < st.Stack.Count; i += 2)
                {
                    y += st.Stack[i];
                    float h = st.Stack[i + 1];
                    st.HorizontalStems.Add((y, h));
                    y += h;
                }
            }
            st.StemCount += count;
            st.ClearStack();
        }

        private static void OpVMoveto(EvalState st)
        {
            float dy = 0;
            if (st.Stack.Count >= 2 && !st.HasExplicitWidth)
            {
                st.Width = st.Stack[0]; st.HasExplicitWidth = true;
                dy = st.Stack[1];
            }
            else if (st.Stack.Count >= 1) dy = st.Stack[0];
            st.ClearStack();
            st.Y += dy;
            if (st.SeenMoveto) st.Path.Close();
            st.Path.MoveTo(st.X, st.Y);
            st.SeenMoveto = true;
        }

        private static void OpHMoveto(EvalState st)
        {
            float dx = 0;
            if (st.Stack.Count >= 2 && !st.HasExplicitWidth)
            {
                st.Width = st.Stack[0]; st.HasExplicitWidth = true;
                dx = st.Stack[1];
            }
            else if (st.Stack.Count >= 1) dx = st.Stack[0];
            st.ClearStack();
            st.X += dx;
            if (st.SeenMoveto) st.Path.Close();
            st.Path.MoveTo(st.X, st.Y);
            st.SeenMoveto = true;
        }

        private static void OpRMoveto(EvalState st)
        {
            float dx = 0, dy = 0;
            if (st.Stack.Count >= 3 && !st.HasExplicitWidth)
            {
                st.Width = st.Stack[0]; st.HasExplicitWidth = true;
                dx = st.Stack[1]; dy = st.Stack[2];
            }
            else if (st.Stack.Count >= 2) { dx = st.Stack[0]; dy = st.Stack[1]; }
            st.ClearStack();
            st.X += dx; st.Y += dy;
            if (st.SeenMoveto) st.Path.Close();
            st.Path.MoveTo(st.X, st.Y);
            st.SeenMoveto = true;
        }

        private static void OpRLineto(EvalState st)
        {
            CharstringPath.RLineto(st.Path, ref st.X, ref st.Y, st.Stack);
            st.ClearStack();
        }

        private static void OpHVLineto(EvalState st, bool horizFirst)
        {
            CharstringPath.AlternatingLineto(st.Path, ref st.X, ref st.Y, st.Stack, horizFirst);
            st.ClearStack();
        }

        private static void OpRRCurveto(EvalState st)
        {
            CharstringPath.RRCurveto(st.Path, ref st.X, ref st.Y, st.Stack);
            st.ClearStack();
        }

        private static void OpRCurveline(EvalState st)
        {
            // n*6 curve args + 2 line args
            int n = st.Stack.Count;
            int curves = (n - 2) / 6;
            for (int i = 0; i < curves; i++)
            {
                int b = i * 6;
                float x1 = st.X + st.Stack[b];
                float y1 = st.Y + st.Stack[b+1];
                float x2 = x1 + st.Stack[b+2];
                float y2 = y1 + st.Stack[b+3];
                st.X = x2 + st.Stack[b+4];
                st.Y = y2 + st.Stack[b+5];
                st.Path.CubicTo(x1, y1, x2, y2, st.X, st.Y);
            }
            int lb = curves * 6;
            if (lb + 1 < n)
            {
                st.X += st.Stack[lb];
                st.Y += st.Stack[lb + 1];
                st.Path.LineTo(st.X, st.Y);
            }
            st.ClearStack();
        }

        private static void OpRLinecurve(EvalState st)
        {
            // n*2 line args + 6 curve args
            int n = st.Stack.Count;
            int lines = (n - 6) / 2;
            for (int i = 0; i < lines; i++)
            {
                st.X += st.Stack[i*2];
                st.Y += st.Stack[i*2+1];
                st.Path.LineTo(st.X, st.Y);
            }
            int cb = lines * 2;
            if (cb + 5 < n)
            {
                float x1 = st.X + st.Stack[cb];
                float y1 = st.Y + st.Stack[cb+1];
                float x2 = x1 + st.Stack[cb+2];
                float y2 = y1 + st.Stack[cb+3];
                st.X = x2 + st.Stack[cb+4];
                st.Y = y2 + st.Stack[cb+5];
                st.Path.CubicTo(x1, y1, x2, y2, st.X, st.Y);
            }
            st.ClearStack();
        }

        private static void OpVVCurveto(EvalState st)
        {
            int n = st.Stack.Count;
            int i = 0;
            float dx1 = 0;
            if (n % 4 != 0) { dx1 = st.Stack[i++]; } // optional dx1
            while (i + 3 < n)
            {
                float x1 = st.X + dx1;
                float y1 = st.Y + st.Stack[i];
                float x2 = x1 + st.Stack[i+1];
                float y2 = y1 + st.Stack[i+2];
                st.X = x2;
                st.Y = y2 + st.Stack[i+3];
                st.Path.CubicTo(x1, y1, x2, y2, st.X, st.Y);
                i += 4; dx1 = 0;
            }
            st.ClearStack();
        }

        private static void OpHHCurveto(EvalState st)
        {
            int n = st.Stack.Count;
            int i = 0;
            float dy1 = 0;
            if (n % 4 != 0) { dy1 = st.Stack[i++]; }
            while (i + 3 < n)
            {
                float x1 = st.X + st.Stack[i];
                float y1 = st.Y + dy1;
                float x2 = x1 + st.Stack[i+1];
                float y2 = y1 + st.Stack[i+2];
                st.X = x2 + st.Stack[i+3];
                st.Y = y2;
                st.Path.CubicTo(x1, y1, x2, y2, st.X, st.Y);
                i += 4; dy1 = 0;
            }
            st.ClearStack();
        }

        private static void OpVHCurveto(EvalState st)
        {
            CharstringPath.AlternatingCurveto(st.Path, ref st.X, ref st.Y, st.Stack, verticalFirst: true);
            st.ClearStack();
        }

        private static void OpHVCurveto(EvalState st)
        {
            CharstringPath.AlternatingCurveto(st.Path, ref st.X, ref st.Y, st.Stack, verticalFirst: false);
            st.ClearStack();
        }

        private static void OpEndchar(EvalState st)
        {
            // TN #5177 §4.12 note J: endchar with 4 or 5 stack args is a deprecated
            // "seac" equivalent — the charstring encodes two component glyphs.
            // We handle this by composing the base and accent glyphs into the path.
            if (st.Stack.Count >= 4 && !st.InSeacComponent)
            {
                // Stack (bottom to top): [width?] adx ady bchar achar — the last four always; a
                // fifth below them is the width only while none has been read.
                int stackBase = st.Stack.Count - 4;
                if (st.Stack.Count == 5 && !st.HasExplicitWidth)
                {
                    st.Width = st.Stack[0];
                    st.HasExplicitWidth = true;
                }
                float adx   = st.Stack[stackBase];
                float ady   = st.Stack[stackBase + 1];
                int   bchar = SaturatingCast.ToInt(st.Stack[stackBase + 2]);
                int   achar = SaturatingCast.ToInt(st.Stack[stackBase + 3]);
                st.ClearStack();

                // Close any open contour before rendering components.
                if (st.SeenMoveto) { st.Path.Close(); st.SeenMoveto = false; }

                // Base at the origin, accent translated by (adx, ady).
                ExecuteSeacComponent(st, bchar, 0f, 0f);
                ExecuteSeacComponent(st, achar, adx, ady);
                return;
            }

            // Optional width as first arg
            if (st.Stack.Count == 1 && !st.HasExplicitWidth)
            {
                st.Width = st.Stack[0];
                st.HasExplicitWidth = true;
            }
            st.ClearStack();
            if (st.SeenMoveto) st.Path.Close();
        }

        /// <summary>
        /// One component of an <c>endchar</c> seac (TN #5177 Appendix C). The code is a
        /// <b>StandardEncoding</b> code, resolved by glyph name through the charset — never through
        /// <see cref="CffFont.CharCodeToGid"/>, which is the font's own encoding or the PDF's
        /// override of it. The component is its own charstring: its width operand is read and
        /// discarded (the composite's advance stands), its stems are counted afresh for its
        /// <c>hintmask</c> bytes, and its horizontal stems are moved with it. A seac inside a
        /// component is not followed — it cannot nest.
        /// </summary>
        private static void ExecuteSeacComponent(EvalState st, int code, float originX, float originY)
        {
            string name = AdobeStandardEncodingNames.Get(code);
            if (name == null || st.Font.GlyphNameToGid == null
                || !st.Font.GlyphNameToGid.TryGetValue(name, out int gid)
                || gid < 0 || gid >= st.Font.Charstrings.Count)
                return;

            var savedX = st.X; var savedY = st.Y;
            float savedWidth = st.Width;
            bool savedHasWidth = st.HasExplicitWidth;
            int savedStems = st.StemCount;
            st.X = originX; st.Y = originY;
            st.HasExplicitWidth = false;
            st.StemCount = 0;
            st.StemYOffset = originY;
            st.InSeacComponent = true;
            try { ExecuteCharstring(st.Font.Charstrings[gid], st); }
            finally
            {
                st.InSeacComponent = false;
                st.StemYOffset = 0;
                st.Width = savedWidth;
                st.HasExplicitWidth = savedHasWidth;
                st.StemCount = savedStems;
            }
            if (st.SeenMoveto) { st.Path.Close(); st.SeenMoveto = false; }
            st.X = savedX; st.Y = savedY;
            st.EndcharSeen = false;
        }

        // ----------------------------------------------------------------
        //  Flex operators (Type 2 §4.5). `flex` draws two Bézier curves as one
        //  smooth flex. The final argument `fd` is "flex depth" in hundredths of
        //  a character-space unit; when the joining point is within `fd/100` of
        //  the implied straight line we may draw a straight segment instead for
        //  hinting purposes. This evaluator renders the full two-curve form
        //  since we are rasterising paths, not producing hinted outlines.
        // ----------------------------------------------------------------

        private static void OpFlex(EvalState st)
        {
            // 12 args + flex depth: dx1..dy6 fd
            if (st.Stack.Count < 13) { st.ClearStack(); return; }
            float startX = st.X, startY = st.Y;
            float x1 = st.X + st.Stack[0],  y1 = st.Y + st.Stack[1];
            float x2 = x1 + st.Stack[2],    y2 = y1 + st.Stack[3];
            float x3 = x2 + st.Stack[4],    y3 = y2 + st.Stack[5];
            float x4 = x3 + st.Stack[6],    y4 = y3 + st.Stack[7];
            float x5 = x4 + st.Stack[8],    y5 = y4 + st.Stack[9];
            float x6 = x5 + st.Stack[10],   y6 = y5 + st.Stack[11];
            float fd = st.Stack[12] / 100f;

            // Straight-line collapse: if the midpoint x3,y3 lies within fd units of
            // the straight line between startX,startY and x6,y6, render a single line.
            float dx = x6 - startX, dy = y6 - startY;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float distToLine = len < 1e-6f ? 0 : Math.Abs((x3 - startX) * dy - (y3 - startY) * dx) / len;
            if (distToLine <= fd)
            {
                st.Path.LineTo(x6, y6);
            }
            else
            {
                st.Path.CubicTo(x1, y1, x2, y2, x3, y3);
                st.Path.CubicTo(x4, y4, x5, y5, x6, y6);
            }
            st.X = x6; st.Y = y6;
            st.ClearStack();
        }

        private static void OpHFlex(EvalState st)
        {
            // 7 args: dx1 dx2 dy2 dx3 dx4 dx5 dx6
            if (st.Stack.Count < 7) { st.ClearStack(); return; }
            float x1 = st.X + st.Stack[0], y1 = st.Y;
            float x2 = x1 + st.Stack[1],   y2 = y1 + st.Stack[2];
            float x3 = x2 + st.Stack[3],   y3 = y2;
            st.Path.CubicTo(x1, y1, x2, y2, x3, y3);
            x1 = x3 + st.Stack[4]; y1 = y3;
            x2 = x1 + st.Stack[5]; y2 = y1;
            st.X = x2 + st.Stack[6]; st.Y = y2;
            st.Path.CubicTo(x1, y1, x2, y2, st.X, st.Y);
            st.ClearStack();
        }

        private static void OpHFlex1(EvalState st)
        {
            if (st.Stack.Count < 9) { st.ClearStack(); return; }
            float x1 = st.X + st.Stack[0], y1 = st.Y + st.Stack[1];
            float x2 = x1 + st.Stack[2],   y2 = y1 + st.Stack[3];
            float x3 = x2 + st.Stack[4],   y3 = y2;
            st.Path.CubicTo(x1, y1, x2, y2, x3, y3);
            x1 = x3 + st.Stack[5]; y1 = y3 + st.Stack[6];
            x2 = x1 + st.Stack[7]; y2 = y1;
            st.X = x2 + st.Stack[8]; st.Y = y2;
            st.Path.CubicTo(x1, y1, x2, y2, st.X, st.Y);
            st.ClearStack();
        }

        private static void OpFlex1(EvalState st)
        {
            if (st.Stack.Count < 11) { st.ClearStack(); return; }
            // Save starting point: the "snap-back" axis returns to this position (TN #5177 §6.4).
            float startX = st.X, startY = st.Y;
            float x1 = st.X + st.Stack[0], y1 = st.Y + st.Stack[1];
            float x2 = x1 + st.Stack[2],   y2 = y1 + st.Stack[3];
            float x3 = x2 + st.Stack[4],   y3 = y2 + st.Stack[5];
            st.Path.CubicTo(x1, y1, x2, y2, x3, y3);
            x1 = x3 + st.Stack[6]; y1 = y3 + st.Stack[7];
            x2 = x1 + st.Stack[8]; y2 = y1 + st.Stack[9];
            // Determine dominant axis from total displacement across all 5 segments.
            float dx = st.Stack[0]+st.Stack[2]+st.Stack[4]+st.Stack[6]+st.Stack[8];
            float dy = st.Stack[1]+st.Stack[3]+st.Stack[5]+st.Stack[7]+st.Stack[9];
            if (Math.Abs(dx) > Math.Abs(dy))
                { st.X = x2 + st.Stack[10]; st.Y = startY; }  // y snaps back to start
            else
                { st.X = startX; st.Y = y2 + st.Stack[10]; }  // x snaps back to start
            st.Path.CubicTo(x1, y1, x2, y2, st.X, st.Y);
            st.ClearStack();
        }

        // ----------------------------------------------------------------
        //  Arithmetic operators (PostScript subset in charstrings)
        // ----------------------------------------------------------------

        private static void ExecuteArithmetic(byte op, EvalState st)
        {
            switch (op)
            {
                case 9:  if (st.Stack.Count >= 1) { var v = st.Pop(); st.Push(Math.Abs(v)); } break;
                case 10: if (st.Stack.Count >= 2) { float b = st.Pop(), a = st.Pop(); st.Push(a + b); } break;
                case 11: if (st.Stack.Count >= 2) { float b = st.Pop(), a = st.Pop(); st.Push(a - b); } break;
                case 12: if (st.Stack.Count >= 2) { float b = st.Pop(), a = st.Pop(); st.Push(b != 0 ? a / b : 0); } break;
                case 14: if (st.Stack.Count >= 1) { var v = st.Pop(); st.Push(-v); } break;
                case 23: st.Push(st.NextRandom()); break;
                case 24: if (st.Stack.Count >= 2) { float b = st.Pop(), a = st.Pop(); st.Push(a * b); } break;
                case 26: if (st.Stack.Count >= 1) { var v = st.Pop(); st.Push((float)Math.Sqrt(v)); } break;
                case 27: if (st.Stack.Count >= 1) st.Push(st.Stack[st.Stack.Count-1]); break; // dup
                case 28: if (st.Stack.Count >= 2) { float b = st.Pop(), a = st.Pop(); st.Push(b); st.Push(a); } break; // exch
                case 15: // index — push copy of n-th stack element (0 = top) (TN #5177 §4.6)
                    if (st.Stack.Count >= 1)
                    {
                        int n = SaturatingCast.ToInt(st.Pop());
                        if (n < 0) n = 0;
                        if (n < st.Stack.Count) st.Push(st.Stack[st.Stack.Count - 1 - n]);
                    }
                    break;
                case 18: if (st.Stack.Count >= 1) st.Pop(); break; // drop
                case 22: // ifelse — push v1 if s1 ≤ s2, else push v2 (TN #5177 §3.5)
                    if (st.Stack.Count >= 4)
                    {
                        float s2=st.Pop(), s1=st.Pop(), v2=st.Pop(), v1=st.Pop();
                        st.Push(s1 <= s2 ? v1 : v2);
                    }
                    break;
                default: st.ClearStack(); break;
            }
        }

        // ----------------------------------------------------------------
        //  Subroutine bias (per spec §2.1)
        // ----------------------------------------------------------------

        private static int SubrBias(int count)
        {
            if (count < 1240) return 107;
            if (count < 33900) return 1131;
            return 32768;
        }
    }
}
