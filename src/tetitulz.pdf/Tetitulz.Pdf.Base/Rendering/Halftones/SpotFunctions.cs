// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering.Halftones
{
    /// <summary>
    /// ISO 32000-2's predefined spot functions (§10.6.3, Table 126), each defined by the PostScript calculator
    /// code the table gives for it and evaluated by the library's own Type 4 evaluator — the specification's
    /// program, never a transcription of its formula.
    /// </summary>
    /// <remarks>
    /// <para><b>One correction.</b> The table's Diamond reads <c>du mul</c> in its last branch where every other
    /// branch and its formula, <c>(|x| − 1)² + (|y| − 1)² − 1</c>, mean <c>dup mul</c>; read as printed, the
    /// program has an unknown operator. The code below carries <c>dup</c>.</para>
    /// <para>A spot function is asked at a pixel's centre in the cell's own coordinates, −1 to +1 on each axis,
    /// and only the order of its answers matters (§10.6.3).</para>
    /// </remarks>
    internal static class SpotFunctions
    {
        /// <summary>Table 126, name → its PostScript calculator code, in the table's order.</summary>
        private static readonly (string Name, string Code)[] Table =
        {
            ("SimpleDot", "{ dup mul exch dup mul add 1 exch sub }"),
            ("InvertedSimpleDot", "{ dup mul exch dup mul add 1 sub }"),
            ("DoubleDot", "{ 360 mul sin 2 div exch 360 mul sin 2 div add }"),
            ("InvertedDoubleDot", "{ 360 mul sin 2 div exch 360 mul sin 2 div add neg }"),
            ("CosineDot", "{ 180 mul cos exch 180 mul cos add 2 div }"),
            ("Double", "{ 360 mul sin 2 div exch 2 div 360 mul sin 2 div add }"),
            ("InvertedDouble", "{ 360 mul sin 2 div exch 2 div 360 mul sin 2 div add neg }"),
            ("Line", "{ exch pop abs neg }"),
            ("LineX", "{ pop }"),
            ("LineY", "{ exch pop }"),
            ("Round", "{ abs exch abs 2 copy add 1 le { dup mul exch dup mul add 1 exch sub } { 1 sub dup mul exch 1 sub dup mul add 1 sub } ifelse }"),
            ("Ellipse", "{ abs exch abs 2 copy 3 mul exch 4 mul add 3 sub dup 0 lt { pop dup mul exch 0.75 div dup mul add 4 div 1 exch sub } { dup 1 gt { pop 1 exch sub dup mul exch 1 exch sub 0.75 div dup mul add 4 div 1 sub } { 0.5 exch sub exch pop exch pop } ifelse } ifelse }"),
            ("EllipseA", "{ dup mul 0.9 mul exch dup mul add 1 exch sub }"),
            ("InvertedEllipseA", "{ dup mul 0.9 mul exch dup mul add 1 sub }"),
            ("EllipseB", "{ dup 5 mul 8 div mul exch dup mul exch add sqrt 1 exch sub }"),
            ("EllipseC", "{ dup mul exch dup mul 0.9 mul add 1 exch sub }"),
            ("InvertedEllipseC", "{ dup mul exch dup mul 0.9 mul add 1 sub }"),
            ("Square", "{ abs exch abs 2 copy lt { exch } if pop neg }"),
            ("Cross", "{ abs exch abs 2 copy gt { exch } if pop neg }"),
            ("Rhomboid", "{ abs exch abs 0.9 mul add 2 div }"),
            ("Diamond", "{ abs exch abs 2 copy add 0.75 le { dup mul exch dup mul add 1 exch sub } { 2 copy add 1.23 le { 0.85 mul add 1 exch sub } { 1 sub dup mul exch 1 sub dup mul add 1 sub } ifelse } ifelse }"),
        };

        private static readonly Dictionary<string, PdfStream> Programs = Build();

        private static Dictionary<string, PdfStream> Build()
        {
            var programs = new Dictionary<string, PdfStream>(StringComparer.Ordinal);
            foreach (var (name, code) in Table) programs[name] = Program(code);
            return programs;
        }

        /// <summary>A Type 4 function of two inputs over the cell, its range wide enough for every table entry.</summary>
        private static PdfStream Program(string code)
        {
            var stream = new PdfStream { RawData = Encoding.ASCII.GetBytes(code) };
            stream.Set(PdfName.FunctionType, new PdfNumber(4));
            stream.Set(PdfName.Domain, Numbers(-1, 1, -1, 1));
            stream.Set(PdfName.Range, Numbers(-4, 4));
            return stream;
        }

        private static PdfArray Numbers(params double[] values) => PdfArray.OfNumbers(values);

        /// <summary>Whether <paramref name="name"/> is one of Table 126's.</summary>
        internal static bool IsPredefined(string name) => name != null && Programs.ContainsKey(name);

        /// <summary>The names in Table 126, in order.</summary>
        internal static IEnumerable<string> Names
        {
            get { foreach (var (name, _) in Table) yield return name; }
        }

        /// <summary>
        /// A predefined spot function by name, or <c>null</c> when Table 126 has none of that name. The function
        /// takes the cell coordinates and answers the whitening order's key.
        /// </summary>
        internal static Func<double, double, double> Named(string name)
            => name != null && Programs.TryGetValue(name, out var program) ? Of(program, null) : null;

        /// <summary>A spot function given as a PDF function object of two inputs (§10.6.5.2), resolved through <paramref name="resolve"/>.</summary>
        internal static Func<double, double, double> Of(PdfDictionary function, Func<PdfObject, PdfObject> resolve)
        {
            var input = new float[2];
            return (x, y) =>
            {
                input[0] = (float)x;
                input[1] = (float)y;
                float[] output = resolve == null
                    ? PdfFunctionEvaluator.Evaluate(function, input)
                    : PdfFunctionEvaluator.Evaluate(function, input, resolve);
                return output != null && output.Length > 0 ? output[0] : 0;
            };
        }
    }
}
