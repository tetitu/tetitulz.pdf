// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// The operators that call a procedure for each item of something: <c>pathforall</c> per path
    /// segment, <c>kshow</c> between each pair of characters, <c>cshow</c> per character (PLRM §8.2).
    ///
    /// <para><b>Through the step loop, never from inside an operator.</b> The interpreter runs a
    /// procedure by pushing a frame and returning; it cannot run one and come back, which is why
    /// these three were arity-table entries that consumed their operands and drew nothing. Each now
    /// pushes one <see cref="PsFrameKind.Callback"/> frame whose <see cref="PsFrame.Advance"/> does
    /// the operator's own part of an item — pushing a segment's coordinates, showing a character,
    /// measuring one — and hands back the procedure to run for it, as <c>forall</c> hands back its
    /// body. A procedure therefore sees and leaves the one operand stack, and <c>exit</c> ends the
    /// operator, as PLRM says it does.</para>
    /// </summary>
    internal static class PsCallbackOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("pathforall", PathForAll);
            op("kshow", KShow);
            op("cshow", CShow);
        }

        /// <summary>
        /// <c>move line curve close pathforall</c>: the current path's segments, as they stand when
        /// it is called, each pushing its coordinates in the current user space and running its
        /// procedure. A path built by <c>strokepath</c> is enumerated as the path it outlines, which
        /// is what this converter holds, and that is said.
        /// </summary>
        private static void PathForAll(PsInterpreter i)
        {
            PsArray close = i.Operands.PopProcedure("pathforall").AsArray();
            PsArray curve = i.Operands.PopProcedure("pathforall").AsArray();
            PsArray line = i.Operands.PopProcedure("pathforall").AsArray();
            PsArray move = i.Operands.PopProcedure("pathforall").AsArray();

            var g = i.Graphics;
            if (g == null || g.Path.IsEmpty) return;
            if (g.Path.StrokeOutlinePending)
                PostScriptDiagnostics.Report("PsCallbackOps.pathforall",
                    "'pathforall' after 'strokepath' enumerated the path the stroke outlines; this " +
                    "converter does not build the outline");

            var segments = new List<Graphics.PsSegment>(g.Path.Segments);
            i.Tick(segments.Count);
            int at = 0;
            i.ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Callback,
                Advance = interpreter =>
                {
                    if (at >= segments.Count) return null;
                    Graphics.PsSegment s = segments[at++];
                    switch (s.Kind)
                    {
                        case Graphics.PsSegmentKind.Move:
                            Push(interpreter, s.X3, s.Y3);
                            return move;
                        case Graphics.PsSegmentKind.Line:
                            Push(interpreter, s.X3, s.Y3);
                            return line;
                        case Graphics.PsSegmentKind.Curve:
                            Push(interpreter, s.X1, s.Y1);
                            Push(interpreter, s.X2, s.Y2);
                            Push(interpreter, s.X3, s.Y3);
                            return curve;
                        default:
                            return close;
                    }
                },
            });
        }

        /// <summary>
        /// <c>proc string kshow</c>: each character shown as <c>show</c> shows it, and between each
        /// pair the two codes pushed and <c>proc</c> run — the kerning a driver writes between them.
        /// A composite font has no character pairs PLRM lets <c>kshow</c> see (it is an
        /// <c>invalidfont</c> there); its string is shown whole and the procedure not run, said.
        /// </summary>
        private static void KShow(PsInterpreter i)
        {
            PsString text = i.Operands.PopString("kshow");
            PsArray proc = i.Operands.PopProcedure("kshow").AsArray();
            if (text.Length == 0) return;

            if (i.CurrentFontCMap != null)
            {
                PsTextOps.ShowText(i, "kshow", text);
                PostScriptDiagnostics.Report("PsCallbackOps.kshow",
                    "'kshow' in a composite font was shown as 'show'; its procedure was not run between characters");
                return;
            }

            int shown = 0;
            i.ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Callback,
                Advance = interpreter =>
                {
                    PsTextOps.ShowText(interpreter, "kshow", text.Interval(shown, 1));
                    shown++;
                    if (shown >= text.Length) return null;
                    interpreter.Operands.Push(PsObject.Int(text[shown - 1]));
                    interpreter.Operands.Push(PsObject.Int(text[shown]));
                    return proc;
                },
            });
        }

        /// <summary>
        /// <c>proc string cshow</c>: for each character — each code of a composite font's string,
        /// split by its CMap — its code, then its advance <c>wx wy</c> in the current font, pushed and
        /// <c>proc</c> run. Nothing is shown; the procedure does the showing. In a composite font the
        /// procedure is given the code's last byte, and showing that byte shows the code (PLRM §8.2,
        /// <see cref="PsCShowSelection"/>).
        /// </summary>
        private static void CShow(PsInterpreter i)
        {
            PsString text = i.Operands.PopString("cshow");
            PsArray proc = i.Operands.PopProcedure("cshow").AsArray();
            if (text.Length == 0) return;

            bool composite = i.CurrentFontCMap != null;
            PsCShowSelection outer = i.CShowSelection;
            var codes = new List<(int Offset, int Length)>();
            if (i.CurrentFontCMap is { } cmap)
            {
                int offset = 0;
                foreach (byte[] code in PsCMapOps.SplitCodes(i, cmap, text.ToArray()))
                {
                    codes.Add((offset, code.Length));
                    offset += code.Length;
                }
            }
            else
                for (int k = 0; k < text.Length; k++) codes.Add((k, 1));
            i.Tick(codes.Count);

            int next = 0;
            PsFrame frame = null;
            frame = new PsFrame
            {
                Kind = PsFrameKind.Callback,
                Advance = interpreter =>
                {
                    interpreter.CShowSelection = outer;
                    if (next >= codes.Count) return null;
                    var (offset, length) = codes[next++];
                    PsString character = new PsString(text.Interval(offset, length).ToArray());
                    var (wx, wy) = PsTextOps.WidthOf(interpreter, "cshow", character);
                    if (composite)
                    {
                        interpreter.Operands.Push(PsObject.Int(character[length - 1]));
                        interpreter.CShowSelection = new PsCShowSelection(frame, interpreter.CurrentFontDict, character);
                    }
                    else
                    {
                        interpreter.Operands.Push(PsObject.Int(character[0]));
                    }
                    interpreter.Operands.Push(PsObject.Real(wx));
                    interpreter.Operands.Push(PsObject.Real(wy));
                    return proc;
                },
            };
            i.ExecStack.Push(frame);
        }

        private static void Push(PsInterpreter i, double x, double y)
        {
            i.Operands.Push(PsObject.Real(x));
            i.Operands.Push(PsObject.Real(y));
        }
    }
}
