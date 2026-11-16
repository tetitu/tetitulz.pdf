// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// <c>form execform</c> (PLRM §4.7) as a PDF form XObject drawn by <c>Do</c>. The two mean the same:
    /// the content concatenated with <c>/Matrix</c>, clipped to <c>/BBox</c>, in the state the caller
    /// is in. The <c>/PaintProc</c> runs once, at the form's first use, with the form on its operand
    /// stack (<see cref="PsCells"/>); its stream inherits the state at each <c>Do</c>, as PLRM's own
    /// cache lets a form be drawn again unchanged. The <c>/Implementation</c> entry execform adds
    /// (PLRM Table 4.20) names the built XObject, so the next use draws it.
    ///
    /// <para>A malformed dictionary raises PLRM's error; a form past <see cref="PsLimits.MaxForms"/>
    /// (the refused counted) or nested past <see cref="PsLimits.MaxFormNestingDepth"/> cells is not
    /// drawn, said once.</para>
    /// </summary>
    internal static class PsFormOps
    {
        [ThreadStatic] private static List<PdfStream> _built;
        [ThreadStatic] private static int _attempts;
        [ThreadStatic] private static bool _capReported;

        internal static void ResetPerConversionState()
        {
            _built = null;
            _attempts = 0;
            _capReported = false;
        }

        private const string ImplementationKey = "Implementation";

        public static void Register(PsSystemDict.Registrar op) => op("execform", ExecForm);

        private static void ExecForm(PsInterpreter i)
        {
            PsDict form = i.Operands.PopDict("execform");
            PsGraphicsEngine g = i.Graphics;

            PdfStream built = Lookup(form);
            double[] bbox;
            PsMatrix matrix;
            if (built == null)
            {
                if (!form.TryGetName("FormType", out PsObject type) || !type.IsNumber || type.Num != 1)
                    throw new PsErrorException(PsErrorKind.RangeCheck, "execform", "/FormType is not 1");
                bbox = Box(form);
                matrix = Matrix(form);
                if (!form.TryGetName("PaintProc", out PsObject proc) || proc.Type != PsType.Array || !proc.IsExecutable)
                    throw new PsErrorException(PsErrorKind.TypeCheck, "execform", "/PaintProc");
                if (g == null) return;   // a tint transform's child: nothing to paint into

                if (++_attempts > PsLimits.MaxForms || PsCells.Depth >= PsLimits.MaxFormNestingDepth)
                {
                    if (!_capReported)
                    {
                        _capReported = true;
                        PostScriptDiagnostics.Report("PsFormOps.execform",
                            _attempts > PsLimits.MaxForms
                                ? $"the {PsLimits.MaxForms}-form cap was reached; later forms are not drawn. Reported once."
                                : $"a form nested past {PsLimits.MaxFormNestingDepth} cells was not drawn. Reported once.");
                    }
                    return;
                }

                byte[] content = PsCells.Run(i, g, proc.AsArray(), PsObject.Dict(form), PsLimits.MaxFormContentBytes,
                    colourSuppressed: false, inheritsState: true, "execform", out PdfDictionary resources);
                built = new PdfStream();
                built[PdfName.Type] = PdfName.XObject;
                built[PdfName.Subtype] = PdfName.Form;
                built[PdfName.BBox] = Numbers(bbox);
                if (!matrix.IsIdentity) built[PdfName.Matrix] = Numbers(matrix);
                built[PdfName.Resources] = resources;
                built.RawData = content;
                (_built ??= new List<PdfStream>()).Add(built);
                form.PutName(ImplementationKey, PsObject.Int(_built.Count - 1));
            }
            else
            {
                if (g == null) return;
                bbox = Box(form);
                matrix = Matrix(form);
            }

            g.Emitter.DrawForm(g.Resources.AddForm(built), g.Gs);
            g.NoteFormExtent(bbox, matrix);
        }

        private static PdfStream Lookup(PsDict form)
        {
            if (_built == null || !form.TryGetName(ImplementationKey, out PsObject id) || !id.IsNumber) return null;
            int index = id.AsInt();
            return index >= 0 && index < _built.Count ? _built[index] : null;
        }

        private static double[] Box(PsDict form)
        {
            if (!form.TryGetName("BBox", out PsObject box) || box.Type != PsType.Array || box.AsArray().Length != 4)
                throw new PsErrorException(PsErrorKind.TypeCheck, "execform", "/BBox");
            var values = new double[4];
            for (int k = 0; k < 4; k++)
            {
                PsObject v = box.AsArray()[k];
                if (!v.IsNumber || double.IsNaN(v.Num) || double.IsInfinity(v.Num))
                    throw new PsErrorException(PsErrorKind.TypeCheck, "execform", "/BBox");
                values[k] = v.Num;
            }
            return values;
        }

        private static PsMatrix Matrix(PsDict form)
        {
            if (!form.TryGetName("Matrix", out PsObject m) || m.Type != PsType.Array)
                throw new PsErrorException(PsErrorKind.TypeCheck, "execform", "/Matrix");
            return PsMatrix.FromOperands(m.AsArray(), "execform");
        }

        private static PdfArray Numbers(double[] values) => Emit.PdfValues.Numbers(values);

        private static PdfArray Numbers(PsMatrix m) => Emit.PdfValues.Matrix(m);
    }
}
