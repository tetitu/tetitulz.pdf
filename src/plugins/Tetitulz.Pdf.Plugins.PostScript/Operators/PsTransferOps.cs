// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Runtime.CompilerServices;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Transfer functions (PLRM §7.3): <c>settransfer</c> and <c>setcolortransfer</c> as PDF's
    /// <c>/TR</c> in an <c>/ExtGState</c> (ISO 32000-1 §10.5) — one function, or four for red, green,
    /// blue and grey, in the order both write them. Each procedure is sampled once, at
    /// <see cref="Samples"/> points over 0 to 1, through the program's own interpreter, into a
    /// FunctionType 0 a reader interpolates; an empty procedure is the identity, written as
    /// <c>/Identity</c> where nothing else stands for it. <c>currenttransfer</c> and
    /// <c>currentcolortransfer</c> answer the procedures set. All four were arity-table entries:
    /// the procedure was consumed and the page printed as if it had none.
    ///
    /// <para>Halftones (<c>setscreen</c>, <c>sethalftone</c>) stay ignored: a PDF reader screens for
    /// its own device, and PDF 2.0 deprecates the entries that would carry them.</para>
    /// </summary>
    internal static class PsTransferOps
    {
        /// <summary>The points a transfer procedure is sampled at.</summary>
        internal const int Samples = 256;

        [ThreadStatic] private static int _built;
        [ThreadStatic] private static bool _capReported;

        internal static void ResetPerConversionState()
        {
            _built = 0;
            _capReported = false;
        }

        /// <summary>A procedure sampled once: the same procedure set again is the same function.</summary>
        private static readonly ConditionalWeakTable<PsArray, PdfObject> Sampled = new();

        /// <summary>The same, for a procedure sampled over −1 to 1 (undercolour removal), a different function of the same procedure.</summary>
        private static readonly ConditionalWeakTable<PsArray, PdfObject> SampledSigned = new();

        public static void Register(PsSystemDict.Registrar op)
        {
            op("settransfer", SetTransfer);
            op("setcolortransfer", SetColorTransfer);
            op("currenttransfer", i => i.Operands.Push(Procedures(i)[3]));
            op("currentcolortransfer", i => { foreach (PsObject p in Procedures(i)) i.Operands.Push(p); });
        }

        /// <summary><c>proc settransfer</c>: one function for every component (PLRM: all four set to it).</summary>
        private static void SetTransfer(PsInterpreter i)
        {
            PsObject proc = Procedure(i.Operands.Pop(), "settransfer");
            var g = i.Graphics;
            if (g == null) return;
            PdfObject function = FunctionOf(i, proc);
            if (function == null && !IsIdentity(proc)) return;
            g.Gs.TransferProcedures = new[] { proc, proc, proc, proc };
            g.SetTransfer(function);
        }

        /// <summary><c>redproc greenproc blueproc grayproc setcolortransfer</c>: one function a component.</summary>
        private static void SetColorTransfer(PsInterpreter i)
        {
            var procs = new PsObject[4];
            for (int k = 3; k >= 0; k--) procs[k] = Procedure(i.Operands.Pop(), "setcolortransfer");
            var g = i.Graphics;
            if (g == null) return;

            bool identity = true;
            var functions = new PdfArray();
            foreach (PsObject proc in procs)
            {
                identity &= IsIdentity(proc);
                PdfObject function = FunctionOf(i, proc) ?? (IsIdentity(proc) ? IdentityFunction() : null);
                if (function == null) return;
                functions.Add(function);
            }
            g.Gs.TransferProcedures = procs;
            g.SetTransfer(identity ? null : functions);
        }

        private static PsObject[] Procedures(PsInterpreter i)
        {
            PsObject[] procs = i.Graphics?.Gs.TransferProcedures;
            if (procs != null) return procs;
            var empty = PsObject.Proc(new PsArray(Array.Empty<PsObject>()));
            return new[] { empty, empty, empty, empty };
        }

        private static PsObject Procedure(PsObject value, string op) =>
            value.Type == PsType.Array && value.IsExecutable ? value
            : throw new PsErrorException(PsErrorKind.TypeCheck, op, "a transfer function is a procedure");

        private static bool IsIdentity(PsObject proc) => proc.AsArray().Length == 0;

        /// <summary>
        /// The procedure as a sampled function, or null for the identity (nothing to write) and for one
        /// that gives no number, or past <see cref="PsLimits.MaxTransferFunctions"/> — both said.
        /// </summary>
        private static PdfObject FunctionOf(PsInterpreter i, PsObject proc)
            => IsIdentity(proc) ? null : Sample(i, proc, 0, "transfer");

        /// <summary>
        /// <paramref name="proc"/> sampled at <see cref="Samples"/> points over 0 to 1 into a
        /// FunctionType 0 whose range is 0 to 1, or −1 to 1 from <paramref name="low"/> −1 (undercolour
        /// removal, ISO 32000-1 §10.3.4) — each answer clamped into it — or null for a procedure that
        /// gives no number, or past <see cref="PsLimits.MaxTransferFunctions"/>, both said.
        /// <paramref name="what"/> names the function in the report.
        /// </summary>
        internal static PdfObject Sample(PsInterpreter i, PsObject proc, double low, string what)
        {
            PsArray body = proc.AsArray();
            var cache = low < 0 ? SampledSigned : Sampled;
            if (cache.TryGetValue(body, out PdfObject known)) return known;
            if (_built >= PsLimits.MaxTransferFunctions)
            {
                if (!_capReported)
                {
                    _capReported = true;
                    PostScriptDiagnostics.Report("PsTransferOps",
                        $"the {PsLimits.MaxTransferFunctions}-function transfer, black-generation and undercolour-removal " +
                        "cap was reached; later ones were ignored. Reported once.");
                }
                return null;
            }

            var samples = new byte[Samples];
            for (int k = 0; k < Samples; k++)
            {
                double[] answer = PsSpaceColor.Transform(i, proc, new[] { k / (double)(Samples - 1) }, 1);
                if (answer == null || double.IsNaN(answer[0]) || double.IsInfinity(answer[0]))
                {
                    PostScriptDiagnostics.Report("PsTransferOps",
                        $"a {what} procedure gave no number; the {what} function was left as it was");
                    return null;
                }
                samples[k] = SaturatingCast.ToByte(Math.Round((Math.Max(low, Math.Min(1, answer[0])) - low) / (1 - low) * 255));
            }
            _built++;
            var function = Sampled1D(samples, low);
            cache.Add(body, function);
            return function;
        }

        private static PdfStream IdentityFunction()
        {
            var samples = new byte[Samples];
            for (int k = 0; k < Samples; k++) samples[k] = (byte)k;
            return Sampled1D(samples, 0);
        }

        private static PdfStream Sampled1D(byte[] samples, double low)
        {
            var function = new PdfStream();
            function[PdfName.FunctionType] = new PdfNumber(0);
            function[PdfName.Domain] = Unit();
            var range = new PdfArray();
            range.Add(new PdfNumber(low));
            range.Add(new PdfNumber(1));
            function[PdfName.Range] = range;
            var size = new PdfArray();
            size.Add(new PdfNumber(samples.Length));
            function[PdfName.Size] = size;
            function[PdfName.BitsPerSample] = new PdfNumber(8);
            function.RawData = samples;
            return function;
        }

        private static PdfArray Unit()
        {
            var array = new PdfArray();
            array.Add(new PdfNumber(0));
            array.Add(new PdfNumber(1));
            return array;
        }
    }
}
