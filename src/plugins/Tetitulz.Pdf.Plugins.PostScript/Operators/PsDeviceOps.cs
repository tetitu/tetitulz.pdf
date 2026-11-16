// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// The device-dependent graphics-state operators (PLRM §4.2, §7.2) a PDF <c>/ExtGState</c> can
    /// carry (ISO 32000-1 Table 58): <c>setoverprint</c> as <c>/OP</c> and <c>/op</c>,
    /// <c>setblackgeneration</c> and <c>setundercolorremoval</c> as <c>/BG</c> and <c>/UCR</c> (each
    /// procedure sampled as a transfer function is, <see cref="PsTransferOps.Sample"/>), <c>setflat</c>,
    /// <c>setsmoothness</c> and <c>setstrokeadjust</c> as <c>/FL</c>, <c>/SM</c> and <c>/SA</c>; each
    /// written with <c>gs</c> as the program sets it and rolled back by <c>grestore</c>, and each
    /// <c>current*</c> form answering what was set. A reader that simulates overprint or separates
    /// the page (this library's raster renderer does both) then sees what the press would.
    /// </summary>
    internal static class PsDeviceOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("setoverprint", i => Set(i, d => d with { Overprint = i.Operands.PopBool("setoverprint") }));
            op("currentoverprint", i => i.Operands.Push(PsObject.Bool(Device(i).Overprint)));

            op("setflat", i => Set(i, d => d with { Flatness = Finite(i.Operands.PopNumber("setflat"), "setflat") }));
            op("currentflat", i => i.Operands.Push(PsObject.Real(Device(i).Flatness)));

            op("setsmoothness", i => Set(i, d => d with { Smoothness = Finite(i.Operands.PopNumber("setsmoothness"), "setsmoothness") }));
            op("currentsmoothness", i => i.Operands.Push(PsObject.Real(Device(i).Smoothness)));

            op("setstrokeadjust", i => Set(i, d => d with { StrokeAdjust = i.Operands.PopBool("setstrokeadjust") }));
            op("currentstrokeadjust", i => i.Operands.Push(PsObject.Bool(Device(i).StrokeAdjust)));

            op("setblackgeneration", i => SetFunction(i, "setblackgeneration", black: true));
            op("currentblackgeneration", i => i.Operands.Push(Procedures(i)[0]));
            op("setundercolorremoval", i => SetFunction(i, "setundercolorremoval", black: false));
            op("currentundercolorremoval", i => i.Operands.Push(Procedures(i)[1]));
        }

        private static PsDeviceParameters Device(PsInterpreter i) => i.Graphics?.Gs.Device ?? PsDeviceParameters.Initial;

        /// <summary>The state's parameters changed by <paramref name="change"/> — its operand popped whether or not there is an engine to set.</summary>
        private static void Set(PsInterpreter i, Func<PsDeviceParameters, PsDeviceParameters> change)
        {
            PsDeviceParameters changed = change(Device(i));
            i.Graphics?.SetDevice(changed);
        }

        /// <summary>
        /// <paramref name="value"/>, written and read back as the program gave it; a non-finite one is a
        /// <c>rangecheck</c>, since it is no PDF number (the emitter would write it as 0).
        /// </summary>
        private static double Finite(double value, string op)
            => double.IsNaN(value) || double.IsInfinity(value)
                ? throw new PsErrorException(PsErrorKind.RangeCheck, op, "a non-finite parameter")
                : value;

        /// <summary>
        /// <c>proc setblackgeneration</c> / <c>proc setundercolorremoval</c>: the procedure sampled —
        /// black generation over 0 to 1, undercolour removal over −1 to 1 (ISO 32000-1 §10.3.4) — and
        /// set; one that gives no number, or past the sampling cap, leaves the function as it was, said
        /// by the sampler. An empty procedure is a function too (the identity), not the device default.
        /// </summary>
        private static void SetFunction(PsInterpreter i, string op, bool black)
        {
            PsObject proc = i.Operands.Pop();
            if (proc.Type != PsType.Array || !proc.IsExecutable)
                throw new PsErrorException(PsErrorKind.TypeCheck, op, "the function is a procedure");

            var g = i.Graphics;
            if (g == null) return;
            PdfObject function = PsTransferOps.Sample(i, proc, black ? 0 : -1, black ? "black-generation" : "undercolour-removal");
            if (function == null) return;

            PsObject[] procedures = (PsObject[])Procedures(i).Clone();
            procedures[black ? 0 : 1] = proc;
            g.Gs.BlackGenerationProcedures = procedures;
            g.SetDevice(black ? g.Gs.Device with { BlackGeneration = function }
                              : g.Gs.Device with { UnderColorRemoval = function });
        }

        /// <summary>The black-generation and undercolour-removal procedures set, an empty one standing for each never set.</summary>
        private static PsObject[] Procedures(PsInterpreter i)
        {
            PsObject[] set = i.Graphics?.Gs.BlackGenerationProcedures;
            if (set != null) return set;
            var empty = PsObject.Proc(new PsArray(Array.Empty<PsObject>()));
            return new[] { empty, empty };
        }
    }
}
