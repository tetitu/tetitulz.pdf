// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts.Type1
{
    /// <summary>
    /// Executes a Type 1 charstring and reports the outline it draws, its advance width, and
    /// whether it was an accented composite.
    /// </summary>
    /// <remarks>
    /// <para><b>Interpreting to a path is what makes the conversion tractable.</b> Translating
    /// Type 1 operators to Type 2 ones directly has to reproduce <c>callothersubr</c>'s flex and
    /// hint-replacement protocols in the target's vocabulary, which Type 2 does not have in the
    /// same shape. Running the charstring instead turns all of that into ordinary curves, and the
    /// writer then emits a Type 2 charstring from geometry rather than from a translation.</para>
    ///
    /// <para><b>Flex is the reason.</b> A Type 1 flex is <c>othersubr 1</c>, seven
    /// <c>rmoveto</c>/<c>othersubr 2</c> pairs collecting points, then <c>othersubr 0</c> — a
    /// protocol carried out through the PostScript operand stack, not an operator. The seven points
    /// are a reference point and two curves; running it produces those curves, and nothing
    /// downstream needs to know a flex was ever involved.</para>
    ///
    /// <para><b>Hints are dropped, and that is the honest cost.</b> Type 1 hint replacement is
    /// <c>othersubr 3</c> plus a subroutine call whose only effect is to re-declare stems mid-glyph;
    /// carrying it across would mean modelling Type 2's hintmask, which needs the stem list to be
    /// known before the outline is emitted. The outlines are exact; the rendering at small sizes
    /// on a hinting rasteriser is not as good as the original. That is a quality loss, not a
    /// correctness one, and <see cref="Type1ToCffConverter"/> says so.</para>
    ///
    /// <para><b>Bounded by construction.</b> A charstring may call subroutines that call
    /// subroutines; the depth cap and the step budget are what stop a font whose <c>Subrs</c> call
    /// each other from running forever.</para>
    /// </remarks>
    internal static class Type1CharstringInterpreter
    {
        /// <summary>Deepest <c>callsubr</c> nesting. The Type 1 spec's own limit is 10.</summary>
        private const int MaxCallDepth = 30;

        /// <summary>Largest number of operators one glyph may execute.</summary>
        private const int MaxSteps = 200_000;

        /// <summary>One drawing command, in absolute glyph-space coordinates.</summary>
        internal struct Segment
        {
            internal SegmentKind Kind;
            internal double X1, Y1, X2, Y2, X3, Y3;
        }

        internal enum SegmentKind
        {
            MoveTo,
            LineTo,
            CurveTo,
        }

        /// <summary>What one charstring drew.</summary>
        internal sealed class Glyph
        {
            internal readonly List<Segment> Segments = new List<Segment>();

            /// <summary>The advance width from <c>hsbw</c> or <c>sbw</c>.</summary>
            internal double Width;

            /// <summary>Whether a width was actually declared.</summary>
            internal bool HasWidth;

            /// <summary>Set when the glyph was a <c>seac</c> composite.</summary>
            internal bool IsComposite;

            internal double AccentX, AccentY;
            internal int BaseCharCode, AccentCharCode;

            /// <summary><c>seac</c>'s <c>asb</c>: the accent's own left sidebearing.</summary>
            internal double AccentSidebearing;

            /// <summary>The left sidebearing <c>hsbw</c> or <c>sbw</c> declared (<c>sbx</c>).</summary>
            internal double SidebearingX;
        }

        /// <summary>
        /// Runs <paramref name="charstring"/>, or answers <c>null</c> when it could not be read.
        /// </summary>
        internal static Glyph Run(byte[] charstring, IReadOnlyList<byte[]> subrs)
            => Run(charstring, subrs, MaxSteps, out _);

        /// <summary>
        /// As <see cref="Run(byte[], IReadOnlyList{byte[]})"/>, stopping at <paramref name="stepLimit"/>
        /// operators (at most <see cref="MaxSteps"/>) — what a whole-font or page budget has left — with
        /// <paramref name="steps"/> the operators it ran, one past the limit when the limit stopped it.
        /// </summary>
        internal static Glyph Run(byte[] charstring, IReadOnlyList<byte[]> subrs, int stepLimit, out int steps)
        {
            steps = 0;
            if (charstring == null) return null;

            var glyph = new Glyph();
            var state = new State { Subrs = subrs, Glyph = glyph, StepLimit = Math.Max(0, Math.Min(stepLimit, MaxSteps)) };

            try
            {
                Execute(charstring, state, 0);
            }
            catch (Exception)
            {
                steps = state.Steps;
                return null;
            }

            steps = state.Steps;
            return glyph;
        }

        /// <summary>The most operators one glyph may execute, for a caller sizing what it allows each glyph.</summary>
        internal static int MaxStepsPerGlyph => MaxSteps;

        private sealed class State
        {
            internal IReadOnlyList<byte[]> Subrs;
            internal Glyph Glyph;

            internal readonly List<double> Stack = new List<double>(48);
            internal readonly List<double> PostScript = new List<double>(8);

            internal double X, Y;
            internal int Steps;
            internal int StepLimit = MaxSteps;

            internal bool Flexing;
            internal readonly List<double> FlexPoints = new List<double>(16);

            internal bool Done;
        }

        private static void Execute(byte[] cs, State s, int depth)
        {
            if (depth > MaxCallDepth) throw new InvalidOperationException("subr nesting too deep");

            int i = 0;
            while (i < cs.Length)
            {
                if (s.Done) return;
                if (++s.Steps > s.StepLimit) throw new InvalidOperationException("charstring too long");

                int v = cs[i++];

                if (v >= 32 || v == 255)
                {
                    s.Stack.Add(ReadNumber(cs, ref i, v));
                    continue;
                }

                switch (v)
                {
                    case 13:                     // hsbw: sbx wx
                        if (s.Stack.Count >= 2)
                        {
                            s.X = s.Stack[0];
                            s.Y = 0;
                            s.Glyph.SidebearingX = s.Stack[0];
                            s.Glyph.Width = s.Stack[1];
                            s.Glyph.HasWidth = true;
                        }
                        s.Stack.Clear();
                        break;

                    case 9:                      // closepath -- the path is closed implicitly
                        s.Stack.Clear();
                        break;

                    case 1:                      // hstem
                    case 3:                      // vstem
                        s.Stack.Clear();
                        break;

                    case 21:                     // rmoveto: dx dy
                        if (s.Stack.Count >= 2) Move(s, s.Stack[s.Stack.Count - 2], s.Stack[s.Stack.Count - 1]);
                        s.Stack.Clear();
                        break;

                    case 22:                     // hmoveto: dx
                        if (s.Stack.Count >= 1) Move(s, s.Stack[s.Stack.Count - 1], 0);
                        s.Stack.Clear();
                        break;

                    case 4:                      // vmoveto: dy
                        if (s.Stack.Count >= 1) Move(s, 0, s.Stack[s.Stack.Count - 1]);
                        s.Stack.Clear();
                        break;

                    case 5:                      // rlineto: dx dy
                        if (s.Stack.Count >= 2) Line(s, s.Stack[0], s.Stack[1]);
                        s.Stack.Clear();
                        break;

                    case 6:                      // hlineto: dx
                        if (s.Stack.Count >= 1) Line(s, s.Stack[0], 0);
                        s.Stack.Clear();
                        break;

                    case 7:                      // vlineto: dy
                        if (s.Stack.Count >= 1) Line(s, 0, s.Stack[0]);
                        s.Stack.Clear();
                        break;

                    case 8:                      // rrcurveto
                        if (s.Stack.Count >= 6)
                            Curve(s, s.Stack[0], s.Stack[1], s.Stack[2], s.Stack[3], s.Stack[4], s.Stack[5]);
                        s.Stack.Clear();
                        break;

                    case 30:                     // vhcurveto: dy1 dx2 dy2 dx3
                        if (s.Stack.Count >= 4)
                            Curve(s, 0, s.Stack[0], s.Stack[1], s.Stack[2], s.Stack[3], 0);
                        s.Stack.Clear();
                        break;

                    case 31:                     // hvcurveto: dx1 dx2 dy2 dy3
                        if (s.Stack.Count >= 4)
                            Curve(s, s.Stack[0], 0, s.Stack[1], s.Stack[2], 0, s.Stack[3]);
                        s.Stack.Clear();
                        break;

                    case 10:                     // callsubr
                    {
                        if (s.Stack.Count == 0) break;
                        int index = SaturatingCast.ToInt(s.Stack[s.Stack.Count - 1]);
                        s.Stack.RemoveAt(s.Stack.Count - 1);

                        if (s.Subrs != null && index >= 0 && index < s.Subrs.Count &&
                            s.Subrs[index] != null)
                        {
                            Execute(s.Subrs[index], s, depth + 1);
                        }
                        break;
                    }

                    case 11:                     // return
                        return;

                    case 14:                     // endchar
                        s.Done = true;
                        return;

                    case 12:
                    {
                        if (i >= cs.Length) return;
                        int v2 = cs[i++];
                        Escape(v2, s, cs, ref i);
                        break;
                    }

                    default:
                        s.Stack.Clear();
                        break;
                }
            }
        }

        private static void Escape(int op, State s, byte[] cs, ref int i)
        {
            switch (op)
            {
                case 0:                          // dotsection
                case 1:                          // vstem3
                case 2:                          // hstem3
                    s.Stack.Clear();
                    break;

                case 6:                          // seac: asb adx ady bchar achar
                    if (s.Stack.Count >= 5)
                    {
                        s.Glyph.IsComposite = true;
                        s.Glyph.AccentSidebearing = s.Stack[0];
                        s.Glyph.AccentX = s.Stack[1];
                        s.Glyph.AccentY = s.Stack[2];
                        s.Glyph.BaseCharCode = SaturatingCast.ToInt(s.Stack[3]);
                        s.Glyph.AccentCharCode = SaturatingCast.ToInt(s.Stack[4]);
                    }
                    s.Stack.Clear();
                    s.Done = true;
                    break;

                case 7:                          // sbw: sbx sby wx wy
                    if (s.Stack.Count >= 4)
                    {
                        s.X = s.Stack[0];
                        s.Y = s.Stack[1];
                        s.Glyph.SidebearingX = s.Stack[0];
                        s.Glyph.Width = s.Stack[2];
                        s.Glyph.HasWidth = true;
                    }
                    s.Stack.Clear();
                    break;

                case 12:                         // div
                    if (s.Stack.Count >= 2)
                    {
                        double b = s.Stack[s.Stack.Count - 1];
                        double a = s.Stack[s.Stack.Count - 2];
                        s.Stack.RemoveAt(s.Stack.Count - 1);
                        s.Stack[s.Stack.Count - 1] = b == 0 ? 0 : a / b;
                    }
                    break;

                case 16:                         // callothersubr
                    CallOtherSubr(s);
                    break;

                case 17:                         // pop
                    s.Stack.Add(s.PostScript.Count > 0 ? Take(s.PostScript) : 0);
                    break;

                case 33:                         // setcurrentpoint
                    if (s.Stack.Count >= 2)
                    {
                        s.X = s.Stack[0];
                        s.Y = s.Stack[1];
                    }
                    s.Stack.Clear();
                    break;

                default:
                    s.Stack.Clear();
                    break;
            }
        }

        /// <summary>
        /// The <c>callothersubr</c> protocol: flex, hint replacement, and everything else.
        /// </summary>
        /// <remarks>
        /// <b>These are not operators, they are a calling convention over the PostScript operand
        /// stack</b>, which is why the interpreter has to model that stack at all. The arguments are
        /// pushed, then the count, then the subroutine number; whatever the "subroutine" leaves
        /// behind is retrieved by however many <c>pop</c>s follow. Handling the three defined ones
        /// and pushing the arguments straight back for the rest is what a font with private
        /// othersubrs needs to keep working.
        /// </remarks>
        private static void CallOtherSubr(State s)
        {
            if (s.Stack.Count < 2) { s.Stack.Clear(); return; }

            int otherSubr = SaturatingCast.ToInt(s.Stack[s.Stack.Count - 1]);
            int argCount = SaturatingCast.ToInt(s.Stack[s.Stack.Count - 2]);
            s.Stack.RemoveAt(s.Stack.Count - 1);
            s.Stack.RemoveAt(s.Stack.Count - 1);

            if (argCount < 0 || argCount > s.Stack.Count) argCount = s.Stack.Count;

            var args = new double[argCount];
            for (int k = 0; k < argCount; k++)
                args[k] = s.Stack[s.Stack.Count - argCount + k];
            s.Stack.RemoveRange(s.Stack.Count - argCount, argCount);

            s.PostScript.Clear();

            switch (otherSubr)
            {
                case 1:                          // flex begins
                    s.Flexing = true;
                    s.FlexPoints.Clear();
                    break;

                case 2:                          // flex collects a point -- the rmoveto did the work
                    break;

                case 0:                          // flex ends
                {
                    s.Flexing = false;

                    // Seven points were collected. The first is a reference point the rasteriser
                    // uses to decide whether to flatten the curve, and is not part of the outline;
                    // the remaining six are two cubic curves.
                    if (s.FlexPoints.Count >= 14)
                    {
                        int b = s.FlexPoints.Count - 12;
                        AbsoluteCurve(s, s.FlexPoints[b], s.FlexPoints[b + 1],
                                         s.FlexPoints[b + 2], s.FlexPoints[b + 3],
                                         s.FlexPoints[b + 4], s.FlexPoints[b + 5]);
                        AbsoluteCurve(s, s.FlexPoints[b + 6], s.FlexPoints[b + 7],
                                         s.FlexPoints[b + 8], s.FlexPoints[b + 9],
                                         s.FlexPoints[b + 10], s.FlexPoints[b + 11]);
                    }
                    s.FlexPoints.Clear();

                    // The two pops that follow read the final point back.
                    s.PostScript.Add(s.Y);
                    s.PostScript.Add(s.X);
                    break;
                }

                case 3:                          // hint replacement
                    s.PostScript.Add(3);
                    break;

                default:
                    // An othersubr this does not know: hand the arguments back, which is what the
                    // pops after it expect and what leaves the stack as the charstring assumes.
                    for (int k = args.Length - 1; k >= 0; k--) s.PostScript.Add(args[k]);
                    break;
            }
        }

        private static double Take(List<double> stack)
        {
            double v = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }

        private static void Move(State s, double dx, double dy)
        {
            s.X += dx;
            s.Y += dy;

            if (s.Flexing)
            {
                // Inside a flex an rmoveto is a point of the curve, not a subpath break.
                s.FlexPoints.Add(s.X);
                s.FlexPoints.Add(s.Y);
                return;
            }

            s.Glyph.Segments.Add(new Segment
            {
                Kind = SegmentKind.MoveTo, X1 = s.X, Y1 = s.Y,
            });
        }

        private static void Line(State s, double dx, double dy)
        {
            s.X += dx;
            s.Y += dy;
            s.Glyph.Segments.Add(new Segment
            {
                Kind = SegmentKind.LineTo, X1 = s.X, Y1 = s.Y,
            });
        }

        private static void Curve(State s, double dx1, double dy1, double dx2, double dy2,
                                  double dx3, double dy3)
        {
            double c1x = s.X + dx1, c1y = s.Y + dy1;
            double c2x = c1x + dx2, c2y = c1y + dy2;
            s.X = c2x + dx3;
            s.Y = c2y + dy3;

            s.Glyph.Segments.Add(new Segment
            {
                Kind = SegmentKind.CurveTo,
                X1 = c1x, Y1 = c1y, X2 = c2x, Y2 = c2y, X3 = s.X, Y3 = s.Y,
            });
        }

        private static void AbsoluteCurve(State s, double c1x, double c1y, double c2x, double c2y,
                                          double x, double y)
        {
            s.X = x;
            s.Y = y;
            s.Glyph.Segments.Add(new Segment
            {
                Kind = SegmentKind.CurveTo,
                X1 = c1x, Y1 = c1y, X2 = c2x, Y2 = c2y, X3 = x, Y3 = y,
            });
        }

        /// <summary>A Type 1 charstring number, per the Black Book's encoding.</summary>
        private static double ReadNumber(byte[] cs, ref int i, int v)
        {
            if (v == 255)
            {
                if (i + 4 > cs.Length) throw new InvalidOperationException("truncated 32-bit operand");
                int n = (cs[i] << 24) | (cs[i + 1] << 16) | (cs[i + 2] << 8) | cs[i + 3];
                i += 4;
                return n;
            }
            if (v <= 246) return v - 139;

            if (i >= cs.Length) throw new InvalidOperationException("truncated operand");
            int w = cs[i++];
            return v <= 250 ? ((v - 247) * 256) + w + 108 : -((v - 251) * 256) - w - 108;
        }
    }
}
