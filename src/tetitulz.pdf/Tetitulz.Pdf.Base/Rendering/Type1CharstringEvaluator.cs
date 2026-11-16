// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Evaluates a Type 1 charstring and produces an <see cref="SKPath"/> together with
    /// the glyph's advance width.
    /// Reference: Adobe Type 1 Font Format (Black Book), ATN #5002.
    /// </summary>
    internal static class Type1CharstringEvaluator
    {
        internal struct GlyphOutline
        {
            public SKPath Path;
            /// <summary>Advance width in charspace units (before FontMatrix scale).</summary>
            public float  AdvanceWidth;
            /// <summary>
            /// The page's charstring budget stopped this glyph short (<c>CharstringWork</c>): the outline
            /// is partial, and is not the glyph's.
            /// </summary>
            public bool   CutByBudget;
            /// <summary>The charstring steps the evaluation ran.</summary>
            public int    Steps;
        }

        private static int MaxCallDepth => PdfLimits.MaxCharstringCallDepth;
        // Total executed operators are capped per glyph (State.StepLimit: PdfLimits.MaxCharstringSteps,
        // or what the page's budget has left, CharstringWork) — bounds a nested-subr "billion-laughs"
        // explosion (K^depth path operations) that the depth cap alone does not. A real glyph
        // runs a few thousand operators.

        // ----------------------------------------------------------------
        //  State
        // ----------------------------------------------------------------

        private sealed class State
        {
            public readonly Type1Font Font;
            public SKPathBuilder Path;
            // Path, as the shared path operators (CharstringPath) draw into it.
            public IGlyphPathSink Sink;
            public float  X, Y;
            public readonly List<float> Stack = new List<float>(48);
            // Width + sidebearing (set by hsbw/sbw)
            public float  Wx;             // advance width
            public bool   HasWidth;
            // Sidebearing
            public float  Sbx;
            // While a seac component runs: its hsbw/sbw only places the current point at
            // (ComponentX + sbx, ComponentY + sby); the composite's width and sidebearing stand.
            public bool   InSeacComponent;
            public float  ComponentX, ComponentY;
            // callothersubr → pop hand-off stack. callothersubr leaves results here
            // (top = last added) so the matching 'pop' operators can retrieve them.
            public readonly List<float> OtherSubrStack = new List<float>(4);
            // Flex state (OtherSubrs 0/1/2). While InFlex, rmoveto records the point
            // into FlexPts (x,y pairs) instead of starting a new subpath.
            public bool   InFlex;
            public readonly List<float> FlexPts = new List<float>(16);
            // Subroutine call depth
            public int    CallDepth;
            public bool   EndcharSeen;
            public bool   SeenMoveto;
            // Total operators executed across the whole glyph evaluation — bounds an adversarial
            // nested-subr call explosion that MaxCallDepth (depth only) does not.
            public int    Steps;
            // The steps this glyph may run: the per-glyph cap, or less when the page's budget has less left.
            public int    StepLimit;

            public State(Type1Font font) { Font = font; }

            public void Push(float v) => Stack.Add(v);
            public float Pop()
            {
                int last = Stack.Count - 1;
                float v = Stack[last];
                Stack.RemoveAt(last);
                return v;
            }
            public void Clear() => Stack.Clear();
        }

        // ----------------------------------------------------------------
        //  Entry point
        // ----------------------------------------------------------------

        public static GlyphOutline Evaluate(Type1Font font, string glyphName)
        {
            if (!font.CharStrings.TryGetValue(glyphName, out byte[] cs))
                return new GlyphOutline { Path = new SKPath(), AdvanceWidth = 0 };

            var state = new State(font);
            state.Path = new SKPathBuilder();
            state.Path.FillType = SKPathFillType.EvenOdd;
            state.Sink = new SkiaGlyphPathSink(state.Path);
            state.StepLimit = CharstringWork.StepsAllowed();

            try { Execute(cs, state); }
            catch { /* malformed charstring — return whatever path was built */ }
            bool cut = CharstringWork.Charge(state.Steps, state.StepLimit);

            // See Type2CharstringEvaluator: snapshot, then dispose the builder.
            SKPath outline = state.Path.Snapshot();
            state.Path.Dispose();
            return new GlyphOutline { Path = outline, AdvanceWidth = state.Wx, CutByBudget = cut, Steps = state.Steps };
        }

        // ----------------------------------------------------------------
        //  Execution engine
        // ----------------------------------------------------------------

        private static void Execute(byte[] cs, State st)
        {
            if (cs == null || cs.Length == 0) return;
            if (++st.CallDepth > MaxCallDepth) { --st.CallDepth; return; }

            int pos = 0;
            while (pos < cs.Length && !st.EndcharSeen)
            {
                if (++st.Steps > st.StepLimit) break;
                byte b0 = cs[pos++];

                // ---- Numbers ----
                // Type 1 number encoding is identical to Type 2 except byte 255 is
                // a 4-byte signed integer (not 16.16 fixed-point).
                if (b0 >= 32 && b0 <= 246) { st.Push(b0 - 139); continue; }
                if (b0 >= 247 && b0 <= 250) { if (pos >= cs.Length) break; st.Push((b0-247)*256 + cs[pos++] + 108); continue; }
                if (b0 >= 251 && b0 <= 254) { if (pos >= cs.Length) break; st.Push(-((b0-251)*256) - cs[pos++] - 108); continue; }
                if (b0 == 28) // shortint
                {
                    if (pos + 1 >= cs.Length) break;
                    st.Push((short)((cs[pos] << 8) | cs[pos + 1]));
                    pos += 2; continue;
                }
                if (b0 == 255) // 4-byte signed integer (Type 1 specific)
                {
                    if (pos + 3 >= cs.Length) break;
                    int v = (cs[pos] << 24) | (cs[pos+1] << 16) | (cs[pos+2] << 8) | cs[pos+3];
                    pos += 4; st.Push(v); continue;
                }

                // ---- Operators ----
                switch (b0)
                {
                    case 1:  // hstem (ignore hint)
                    case 3:  // vstem (ignore hint)
                        st.Clear();
                        break;

                    case 4:  // vmoveto
                        OpVMoveto(st);
                        break;
                    case 5:  // rlineto
                        OpRLineto(st);
                        break;
                    case 6:  // hlineto
                        OpHLineto(st);
                        break;
                    case 7:  // vlineto
                        OpVLineto(st);
                        break;
                    case 8:  // rrcurveto
                        OpRRCurveto(st);
                        break;
                    case 9:  // closepath (Type 1 only — explicitly closes current subpath)
                        if (st.SeenMoveto) st.Path.Close();
                        st.SeenMoveto = false;
                        break;
                    case 10: // callsubr
                        if (st.Stack.Count > 0)
                        {
                            int idx = SaturatingCast.ToInt(st.Pop());
                            if (idx >= 0 && idx < st.Font.Subrs.Count)
                            {
                                Execute(st.Font.Subrs[idx], st);
                                if (st.EndcharSeen) { --st.CallDepth; return; }
                            }
                        }
                        break;
                    case 11: // return
                        --st.CallDepth;
                        return;
                    case 13: // hsbw — horizontal sidebearing and width
                        OpHSBW(st);
                        break;
                    case 14: // endchar
                        if (st.SeenMoveto) st.Path.Close();
                        st.HasWidth   = true;
                        st.EndcharSeen = true;
                        st.Clear();
                        break;
                    case 21: // rmoveto
                        OpRMoveto(st);
                        break;
                    case 22: // hmoveto
                        OpHMoveto(st);
                        break;
                    case 30: // vhcurveto (same as Type 2)
                        OpVHCurveto(st);
                        break;
                    case 31: // hvcurveto (same as Type 2)
                        OpHVCurveto(st);
                        break;
                    case 12: // escape — two-byte operators
                        if (pos >= cs.Length) { --st.CallDepth; return; }
                        ExecuteEscape(cs[pos++], st);
                        break;
                    default:
                        st.Clear();
                        break;
                }
            }
            --st.CallDepth;
        }

        private static void ExecuteEscape(byte op, State st)
        {
            switch (op)
            {
                case 0:  // dotsection (ignore)
                    st.Clear();
                    break;
                case 1:  // vstem3 (3 vertical stems — ignore)
                case 2:  // hstem3 (3 horizontal stems — ignore)
                    st.Clear();
                    break;
                case 6:  // seac — standard encoding accented character
                    OpSeac(st);
                    break;
                case 7:  // sbw — set sidebearing and width (x and y)
                    OpSBW(st);
                    break;
                case 12: // div
                    if (st.Stack.Count >= 2)
                    {
                        float b = st.Pop(), a = st.Pop();
                        st.Push(b != 0 ? a / b : 0);
                    }
                    break;
                case 16: // callothersubr — built-in OtherSubrs (flex, hint replacement)
                    OpCallOtherSubr(st);
                    break;
                case 17: // pop — retrieve value from OtherSubrStack
                    if (st.OtherSubrStack.Count > 0)
                    {
                        st.Push(st.OtherSubrStack[st.OtherSubrStack.Count - 1]);
                        st.OtherSubrStack.RemoveAt(st.OtherSubrStack.Count - 1);
                    }
                    else
                    {
                        st.Push(0f); // nothing to pop — push 0 as placeholder
                    }
                    break;
                case 33: // setcurrentpoint x y
                    if (st.Stack.Count >= 2)
                    {
                        st.Y = st.Pop();
                        st.X = st.Pop();
                    }
                    break;
                default:
                    st.Clear();
                    break;
            }
        }

        // ----------------------------------------------------------------
        //  Operator implementations
        // ----------------------------------------------------------------

        private static void OpHSBW(State st)
        {
            // Stack (bottom→top): sbx wx
            // ATN #5002: hsbw sets the left sidebearing (sbx) and advance width (wx).
            // The current point is set to (sbx, 0) in glyph space.
            if (st.Stack.Count >= 2)
            {
                float wx  = st.Pop(); // top = wx
                float sbx = st.Pop(); // next = sbx
                if (PlacesSeacComponent(st, sbx, 0f)) return;
                st.Sbx = sbx;
                st.Wx  = wx;
                st.X   = sbx;
                st.Y   = 0;
                st.HasWidth = true;
            }
            else if (st.Stack.Count == 1 && !st.InSeacComponent)
            {
                st.Wx = st.Pop();
                st.HasWidth = true;
            }
            st.Clear();
        }

        /// <summary>
        /// A seac component's hsbw/sbw: only the current point moves, to the component's origin plus
        /// its sidebearing. The composite's width and sidebearing stand. False outside a component.
        /// </summary>
        private static bool PlacesSeacComponent(State st, float sbx, float sby)
        {
            if (!st.InSeacComponent) return false;
            st.X = st.ComponentX + sbx;
            st.Y = st.ComponentY + sby;
            st.Clear();
            return true;
        }

        private static void OpSBW(State st)
        {
            // sbx sby wx wy sbw
            if (st.Stack.Count >= 4)
            {
                float wy  = st.Stack[st.Stack.Count - 1];
                float wx  = st.Stack[st.Stack.Count - 2];
                float sby = st.Stack[st.Stack.Count - 3];
                float sbx = st.Stack[st.Stack.Count - 4];
                if (PlacesSeacComponent(st, sbx, sby)) return;
                st.Sbx = sbx;
                st.Wx  = wx;
                st.X   = sbx;
                st.Y   = sby;
                st.HasWidth = true;
            }
            st.Clear();
        }

        private static void OpVMoveto(State st)
        {
            if (st.Stack.Count >= 1)
            {
                st.Y += st.Pop();
            }
            st.Clear();
            if (RecordFlexPoint(st)) return;
            if (st.SeenMoveto) st.Path.Close();
            st.Path.MoveTo(st.X, st.Y);
            st.SeenMoveto = true;
        }

        private static void OpHMoveto(State st)
        {
            if (st.Stack.Count >= 1)
            {
                st.X += st.Pop();
            }
            st.Clear();
            if (RecordFlexPoint(st)) return;
            if (st.SeenMoveto) st.Path.Close();
            st.Path.MoveTo(st.X, st.Y);
            st.SeenMoveto = true;
        }

        private static void OpRMoveto(State st)
        {
            if (st.Stack.Count >= 2)
            {
                float dy = st.Pop();
                float dx = st.Pop();
                st.X += dx;
                st.Y += dy;
            }
            st.Clear();
            if (RecordFlexPoint(st)) return;
            if (st.SeenMoveto) st.Path.Close();
            st.Path.MoveTo(st.X, st.Y);
            st.SeenMoveto = true;
        }

        /// <summary>
        /// During a Flex sequence (between OtherSubrs 1 and 0) each rmoveto/hmoveto/vmoveto
        /// merely records the resulting current point as a flex control point; it must NOT
        /// start a new subpath.  Returns <c>true</c> when the point was recorded (flex active).
        /// </summary>
        private static bool RecordFlexPoint(State st)
        {
            if (!st.InFlex) return false;
            st.FlexPts.Add(st.X);
            st.FlexPts.Add(st.Y);
            return true;
        }

        /// <summary>
        /// Handles <c>callothersubr</c> (operator 12 16).  The operand stack layout is
        /// <c>arg1 … argN  N  othersubr#</c> (othersubr# on top).  We implement the four
        /// standard built-in OtherSubrs (Type 1 spec §8):
        ///   1 — begin Flex   2 — collect Flex point   0 — end Flex
        ///   3 — hint replacement
        /// Any other OtherSubr is treated per the spec's default rule: pop the N arguments
        /// and make them available to subsequent <c>pop</c> operators in the original order.
        /// </summary>
        private static void OpCallOtherSubr(State st)
        {
            if (st.Stack.Count < 2) { st.Clear(); return; }

            int otherSubr = SaturatingCast.ToInt(st.Pop());   // top of stack
            int n         = SaturatingCast.ToInt(st.Pop());   // argument count
            if (n < 0) n = 0;

            switch (otherSubr)
            {
                case 1: // begin flex
                    st.InFlex = true;
                    st.FlexPts.Clear();
                    st.OtherSubrStack.Clear();
                    for (int i = 0; i < n && st.Stack.Count > 0; i++) st.Pop();
                    break;

                case 2: // collect flex point — the point is recorded by the rmoveto itself
                    st.OtherSubrStack.Clear();
                    for (int i = 0; i < n && st.Stack.Count > 0; i++) st.Pop();
                    break;

                case 0: // end flex: args (bottom→top) = flexHeight endX endY
                {
                    float endY = st.Stack.Count > 0 ? st.Pop() : st.Y;
                    float endX = st.Stack.Count > 0 ? st.Pop() : st.X;
                    // discard any remaining args (flexHeight and beyond)
                    for (int i = 2; i < n && st.Stack.Count > 0; i++) st.Pop();
                    EndFlex(st, endX, endY);
                    // The standard end-flex epilogue is "pop pop setcurrentpoint", which
                    // expects the first pop to yield endX and the second endY.  'pop' reads
                    // the *last* element of OtherSubrStack, so push Y first then X.  We use
                    // the path's real current point (set by EndFlex) rather than the raw
                    // args so setcurrentpoint can never desync from where the path actually is.
                    st.OtherSubrStack.Clear();
                    st.OtherSubrStack.Add(st.Y);
                    st.OtherSubrStack.Add(st.X);
                    break;
                }

                case 3: // hint replacement: arg = subr#; followed by "pop callsubr"
                {
                    float subrNo = st.Stack.Count > 0 ? st.Pop() : 3f;
                    for (int i = 1; i < n && st.Stack.Count > 0; i++) st.Pop();
                    st.OtherSubrStack.Clear();
                    st.OtherSubrStack.Add(subrNo);
                    break;
                }

                default: // unknown OtherSubr — hand args back to subsequent 'pop's in order
                {
                    st.OtherSubrStack.Clear();
                    // Pop yields argN … arg1 (top→bottom); store reversed so the first
                    // 'pop' (which reads the last element) returns argN, matching PostScript.
                    int take = Math.Min(n, st.Stack.Count);
                    var tmp = new List<float>(take);
                    for (int i = 0; i < take; i++) tmp.Add(st.Pop());
                    for (int i = tmp.Count - 1; i >= 0; i--) st.OtherSubrStack.Add(tmp[i]);
                    break;
                }
            }
            st.Clear();
        }

        /// <summary>
        /// Completes a Flex sequence: replaces the seven recorded flex points with the two
        /// Bézier curves they describe.  Points are absolute glyph-space coordinates;
        /// point 0 is the (geometrically unused) reference point, points 1–3 form the first
        /// curve and points 4–6 the second.  The first curve continues from the path's
        /// current point (the position before the flex began).
        /// </summary>
        private static void EndFlex(State st, float endX, float endY)
        {
            st.InFlex = false;
            var p = st.FlexPts;
            if (p.Count >= 14)
            {
                st.Path.CubicTo(p[2],  p[3],  p[4],  p[5],  p[6],  p[7]);   // start → p1 → p2 → p3
                st.Path.CubicTo(p[8],  p[9],  p[10], p[11], p[12], p[13]);  // p3 → p4 → p5 → p6
                st.X = p[12];
                st.Y = p[13];
            }
            else if (p.Count >= 2)
            {
                // Malformed flex (fewer than 7 points): degrade to a straight line.
                st.X = p[p.Count - 2];
                st.Y = p[p.Count - 1];
                if (st.SeenMoveto) st.Path.LineTo(st.X, st.Y);
            }
            else
            {
                st.X = endX;
                st.Y = endY;
            }
            st.FlexPts.Clear();
        }

        // The five path operators below are Core.Fonts.CharstringPath, shared with the Type 2
        // evaluator. Each wrapper keeps only what is genuinely Type 1's: clearing the operand
        // stack afterwards, which the two evaluators spell differently (State.Clear vs
        // EvalState.ClearStack) though both are Stack.Clear().
        private static void OpRLineto(State st)
        {
            CharstringPath.RLineto(st.Sink, ref st.X, ref st.Y, st.Stack);
            st.Clear();
        }

        private static void OpHLineto(State st)
        {
            CharstringPath.AlternatingLineto(st.Sink, ref st.X, ref st.Y, st.Stack, horizontalFirst: true);
            st.Clear();
        }

        private static void OpVLineto(State st)
        {
            CharstringPath.AlternatingLineto(st.Sink, ref st.X, ref st.Y, st.Stack, horizontalFirst: false);
            st.Clear();
        }

        private static void OpRRCurveto(State st)
        {
            CharstringPath.RRCurveto(st.Sink, ref st.X, ref st.Y, st.Stack);
            st.Clear();
        }

        private static void OpVHCurveto(State st)
        {
            CharstringPath.AlternatingCurveto(st.Sink, ref st.X, ref st.Y, st.Stack, verticalFirst: true);
            st.Clear();
        }

        private static void OpHVCurveto(State st)
        {
            CharstringPath.AlternatingCurveto(st.Sink, ref st.X, ref st.Y, st.Stack, verticalFirst: false);
            st.Clear();
        }

        /// <summary>
        /// seac: Standard Encoding Accented Character composite.
        /// Args: asb adx ady bchar achar seac
        ///   bchar/achar are StandardEncoding char codes for base + accent glyphs.
        /// <para>Placement is Skia's (measured against it): the base is drawn at the glyph
        /// origin through its own sidebearing; the accent's origin is at
        /// (adx − asb + the composite's sbx, ady), its own hsbw adding its sidebearing to that. Neither
        /// component's hsbw changes the composite's advance width. A seac inside a component is
        /// ignored, as Skia refuses it — it cannot nest.</para>
        /// </summary>
        private static void OpSeac(State st)
        {
            if (st.Stack.Count < 4 || st.InSeacComponent) { st.Clear(); return; }
            // Stack (bottom→top): [asb, adx, ady, bchar, achar]
            float achar = st.Pop();
            float bchar = st.Pop();
            float ady   = st.Pop();
            float adx   = st.Pop();
            float asb   = st.Stack.Count > 0 ? st.Pop() : 0f;
            st.Clear();

            if (st.SeenMoveto) { st.Path.Close(); st.SeenMoveto = false; }

            // Base glyph at the origin; accent at (adx - asb + composite sbx, ady).
            ExecuteSeacComponent(st, StandardEncodingName(SaturatingCast.ToInt(bchar)), 0f, 0f);
            ExecuteSeacComponent(st, StandardEncodingName(SaturatingCast.ToInt(achar)), adx - asb + st.Sbx, ady);
        }

        private static void ExecuteSeacComponent(State st, string name, float originX, float originY)
        {
            if (name == null || !st.Font.CharStrings.TryGetValue(name, out byte[] cs)) return;
            var savedX = st.X; var savedY = st.Y;
            st.X = originX; st.Y = originY;
            st.ComponentX = originX; st.ComponentY = originY;
            st.InSeacComponent = true;
            try { Execute(cs, st); }
            finally { st.InSeacComponent = false; }
            if (st.SeenMoveto) { st.Path.Close(); st.SeenMoveto = false; }
            st.X = savedX; st.Y = savedY;
            st.EndcharSeen = false;
        }

        // ----------------------------------------------------------------
        //  Standard Encoding lookup (ATN #5002 Appendix B = PDF Annex D.2)
        // ----------------------------------------------------------------

        // The table is Core's single AdobeStandardEncodingNames, which FontCache reads too.
        private static string StandardEncodingName(int code)
        {
            return AdobeStandardEncodingNames.Get(code);
        }
    }
}
