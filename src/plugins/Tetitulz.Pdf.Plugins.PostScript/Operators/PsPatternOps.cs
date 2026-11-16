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
    /// <c>makepattern</c> and <c>setpattern</c> (PLRM §4.9) as PDF patterns (ISO 32000-2 §8.7.3).
    ///
    /// <para><b>A tiling pattern's cell is drawn once, at <c>makepattern</c>.</b> Its
    /// <c>/PaintProc</c> runs with the pattern on the operand stack in a child interpreter over its
    /// own engine, emitter and resources — the way a Type 3 glyph procedure is run — and what it
    /// draws becomes the pattern stream, in pattern space. PostScript may defer the call to the
    /// first use, which only a <c>PaintProc</c> with side effects can tell. The graphics state the
    /// cell starts in is the one at <c>makepattern</c> (line style, and the colour of a coloured
    /// pattern); an uncoloured pattern's cell writes no colour, as §8.7.3.3 requires. A shading
    /// pattern's <c>/Shading</c> goes through <see cref="PsShading"/>.</para>
    ///
    /// <para><b>The pattern matrix is the program's matrix under the CTM at <c>makepattern</c></b>,
    /// re-expressed against the stream's own initial space (the engine's base matrix), which is the
    /// space a PDF pattern's <c>/Matrix</c> maps into. A later change of the CTM does not move the
    /// pattern, in either language.</para>
    ///
    /// <para>The instance <c>makepattern</c> pushes is a copy of the dictionary with an
    /// <c>/Implementation</c> entry naming the built pattern; one this converter could not build
    /// has none, and <c>setpattern</c> then leaves the colour as it was, said once. At most
    /// <see cref="PsLimits.MaxPatterns"/> <c>makepattern</c>s a conversion, the refused counted,
    /// and cells nest at most <see cref="PsLimits.MaxPatternNestingDepth"/> deep.</para>
    /// </summary>
    internal static class PsPatternOps
    {
        private sealed class Built
        {
            public PdfObject Pdf;
            public bool Uncoloured;
        }

        // Per conversion, reset by both public entry points, like PsType3GlyphBuilder's: a pattern
        // made in the page's interpreter is used from a child's (a glyph, a cell), so the table
        // cannot live on one interpreter.
        [ThreadStatic] private static List<Built> _built;
        [ThreadStatic] private static int _attempts;
        [ThreadStatic] private static bool _unbuiltReported;
        [ThreadStatic] private static bool _approximationReported;

        internal static void ResetPerConversionState()
        {
            _built = null;
            _attempts = 0;
            _unbuiltReported = false;
            _approximationReported = false;
        }

        private const string ImplementationKey = "Implementation";

        public static void Register(PsSystemDict.Registrar op)
        {
            op("makepattern", MakePattern);
            op("setpattern", SetPattern);
        }

        // ------------------------------------------------------------------
        //  makepattern
        // ------------------------------------------------------------------

        private static void MakePattern(PsInterpreter i)
        {
            PsObject matrixObject = i.Operands.Pop();
            PsObject dictObject = i.Operands.Pop();
            if (dictObject.Type != PsType.Dictionary || !TryMatrix(matrixObject, out PsMatrix matrix))
                throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern");

            PsDict source = dictObject.AsDict();
            if (!source.TryGetName("PatternType", out PsObject typeObject) || !IsInteger(typeObject))
                throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern", "/PatternType");
            int type = typeObject.AsInt();
            if (type != 1 && type != 2)
                throw new PsErrorException(PsErrorKind.RangeCheck, "makepattern", $"/PatternType {type}");
            Tiling tiling = type == 1 ? Tiling.Read(source) : null;

            var instance = new PsDict(source.Count + 1);
            foreach (var entry in source.Entries) instance.Put(entry.Key, entry.Value);

            PdfObject pdf = null;
            string refusal;
            PsGraphicsEngine g = i.Graphics;
            if (++_attempts > PsLimits.MaxPatterns)
            {
                refusal = _attempts == PsLimits.MaxPatterns + 1
                    ? $"the {PsLimits.MaxPatterns}-pattern cap was reached; later patterns are not built. Reported once."
                    : null;
            }
            else if (g == null) refusal = null;   // a tint transform's own child: nothing to paint into
            else if (PsCells.Depth >= PsLimits.MaxPatternNestingDepth)
                refusal = $"a pattern cell nested past {PsLimits.MaxPatternNestingDepth} cells; the pattern was not built";
            else
            {
                PsMatrix? inverseBase = g.BaseMatrix.Invert();
                PsMatrix pattern = matrix.Concat(g.Gs.Ctm);
                if (inverseBase.HasValue) pattern = pattern.Concat(inverseBase.Value);
                if (!pattern.IsFinite || pattern.IsSingular)
                    refusal = "its matrix under the current transform is singular or not finite; the pattern was not built";
                else
                    pdf = type == 1
                        ? TilingPattern(i, g, tiling, instance, pattern, out refusal)
                        : ShadingPattern(i, source, pattern, out refusal);
            }

            if (pdf != null)
            {
                (_built ??= new List<Built>()).Add(new Built { Pdf = pdf, Uncoloured = tiling?.PaintType == 2 });
                instance.PutName(ImplementationKey, PsObject.Int(_built.Count - 1));
            }
            else if (refusal != null)
                PostScriptDiagnostics.Report("PsPatternOps.makepattern", refusal);

            i.Operands.Push(PsObject.Dict(instance));
        }

        /// <summary>A tiling pattern's entries, judged before anything is built (PLRM Table 4.22).</summary>
        private sealed class Tiling
        {
            public int PaintType, TilingType;
            public double[] BBox;
            public double XStep, YStep;
            public PsArray PaintProc;

            public static Tiling Read(PsDict d)
            {
                var t = new Tiling
                {
                    PaintType = RequiredInteger(d, "PaintType"),
                    TilingType = RequiredInteger(d, "TilingType"),
                    XStep = RequiredNumber(d, "XStep"),
                    YStep = RequiredNumber(d, "YStep"),
                };
                if (t.PaintType != 1 && t.PaintType != 2)
                    throw new PsErrorException(PsErrorKind.RangeCheck, "makepattern", $"/PaintType {t.PaintType}");
                if (t.TilingType < 1 || t.TilingType > 3)
                    throw new PsErrorException(PsErrorKind.RangeCheck, "makepattern", $"/TilingType {t.TilingType}");
                if (t.XStep == 0 || t.YStep == 0)
                    throw new PsErrorException(PsErrorKind.RangeCheck, "makepattern", "a zero /XStep or /YStep");
                if (!d.TryGetName("BBox", out PsObject box) || box.Type != PsType.Array || box.AsArray().Length != 4)
                    throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern", "/BBox");
                t.BBox = new double[4];
                for (int k = 0; k < 4; k++)
                {
                    PsObject v = box.AsArray()[k];
                    if (!v.IsNumber || !PsNumeric.IsFinite(v.Num)) throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern", "/BBox");
                    t.BBox[k] = v.Num;
                }
                if (!d.TryGetName("PaintProc", out PsObject proc) || proc.Type != PsType.Array || !proc.IsExecutable)
                    throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern", "/PaintProc");
                t.PaintProc = proc.AsArray();
                return t;
            }
        }

        private static PdfObject TilingPattern(PsInterpreter i, PsGraphicsEngine g, Tiling tiling, PsDict instance,
            PsMatrix matrix, out string refusal)
        {
            refusal = null;
            // The cell starts in the graphics state at makepattern (PLRM §4.9.2) and writes it: a PDF
            // tiling cell starts in the page's initial state, not the one it is painted in. ISO 32000-2
            // §8.7.3.3: an uncoloured pattern's cell specifies no colour — the colour is setpattern's.
            byte[] content = PsCells.Run(i, g, tiling.PaintProc, PsObject.Dict(instance),
                PsLimits.MaxPatternCellContentBytes, colourSuppressed: tiling.PaintType == 2,
                inheritsState: false, "makepattern", out PdfDictionary resources);

            var stream = new PdfStream();
            stream[PdfName.Type] = PdfName.Pattern;
            stream[PdfName.PatternType] = new PdfNumber(1);
            stream[PdfName.PaintType] = new PdfNumber(tiling.PaintType);
            stream[PdfName.TilingType] = new PdfNumber(tiling.TilingType);
            stream[PdfName.BBox] = Numbers(tiling.BBox);
            stream[PdfName.XStep] = new PdfNumber(tiling.XStep);
            stream[PdfName.YStep] = new PdfNumber(tiling.YStep);
            stream[PdfName.Resources] = resources;
            if (!matrix.IsIdentity) stream[PdfName.Matrix] = Numbers(matrix);
            stream.RawData = content;
            return stream;
        }

        private static PdfObject ShadingPattern(PsInterpreter i, PsDict source, PsMatrix matrix, out string refusal)
        {
            if (!source.TryGetName("Shading", out PsObject shadingObject) || shadingObject.Type != PsType.Dictionary)
                throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern", "/Shading");
            PdfObject shading = PsShading.TryConvert(i, shadingObject.AsDict(), out string why, out string approximation, out _);
            if (shading == null)
            {
                PsShading.DiscardFileSources(i, shadingObject.AsDict());
                refusal = "a shading pattern was not built: " + why;
                return null;
            }
            refusal = null;
            if (approximation != null && !_approximationReported)
            {
                _approximationReported = true;
                PostScriptDiagnostics.Report("PsPatternOps.makepattern",
                    $"a shading pattern's colour space was carried as its device analogue: {approximation}. Reported once per conversion.");
            }

            var pattern = new PdfDictionary();
            pattern[PdfName.Type] = PdfName.Pattern;
            pattern[PdfName.PatternType] = new PdfNumber(2);
            pattern[PdfName.Shading] = shading;
            if (!matrix.IsIdentity) pattern[PdfName.Matrix] = Numbers(matrix);
            return pattern;
        }

        // ------------------------------------------------------------------
        //  setpattern, and setcolor in a Pattern space
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>pattern setpattern</c>, or <c>c1 … cn pattern setpattern</c> for an uncoloured one (PLRM
        /// §4.9): the colour becomes the pattern, an uncoloured one's components in the current space,
        /// and the space <c>[/Pattern cs]</c> over it — <c>/Pattern</c> for a coloured one.
        ///
        /// <para>The current space is the one <c>setcolorspace</c> set — <c>setgray</c>,
        /// <c>setrgbcolor</c> and <c>setcmykcolor</c> clear it, so it is never stale — or, with none,
        /// the device space of the current colour; in a Pattern space, its base. Making the space a
        /// Pattern space is what lets a <c>setcolor</c> after it take another pattern, and the next
        /// <c>setgray</c> clears it as it clears any other.</para>
        /// </summary>
        private static void SetPattern(PsInterpreter i)
        {
            PsObject pattern = i.Operands.Peek();
            if (pattern.Type != PsType.Dictionary) throw new PsErrorException(PsErrorKind.TypeCheck, "setpattern");

            PsGraphicsEngine g = i.Graphics;
            PsImageColorSpace underlying = null;
            PsImageColorSpace current = g?.Gs.ImageSpace;
            PsColor colour = g?.Gs.Color ?? PsColor.Black;
            if (IsUncoloured(pattern.AsDict()))
                underlying = current?.Family == "Pattern" ? current.PatternBase ?? DeviceSpaceOf(colour)
                    : current ?? DeviceSpaceOf(colour);

            // What currentcolorspace answers afterwards: /Pattern alone, the [/Pattern base] already
            // current, or the space the uncoloured pattern is painted over under /Pattern.
            PsObject spaceOperand = PsObject.LitName("Pattern");
            if (underlying != null)
            {
                spaceOperand = current?.Family == "Pattern" && current.PatternBase != null && g.Gs.ColorSpaceOperand != null
                    ? g.Gs.ColorSpaceOperand[0]
                    : PsObject.Arr(new PsArray(new[]
                      {
                          PsObject.LitName("Pattern"),
                          current != null && current.Family != "Pattern" && g.Gs.ColorSpaceOperand != null
                              ? g.Gs.ColorSpaceOperand[0]
                              : PsObject.LitName(PsSystemDict.DeviceFamily(colour.Space)),
                      }));
            }

            SetColor(i, underlying);
            if (g != null)
            {
                g.Gs.ImageSpace = PsImageColorSpace.PatternOver(underlying);
                g.Gs.ColorSpaceOperand = new[] { spaceOperand };
            }
            i.CurrentColorComponents = -1;
        }

        /// <summary>
        /// <c>setcolor</c> in a Pattern space over <paramref name="underlying"/> (null for
        /// <c>/Pattern</c> alone): the pattern on top, an uncoloured one's components below it.
        /// </summary>
        internal static void SetColor(PsInterpreter i, PsImageColorSpace underlying)
        {
            PsObject pattern = i.Operands.Pop();
            if (pattern.Type != PsType.Dictionary) throw new PsErrorException(PsErrorKind.TypeCheck, "setcolor");
            PsDict instance = pattern.AsDict();
            bool uncoloured = IsUncoloured(instance);

            double[] components = null;
            if (uncoloured)
            {
                int n = underlying?.SetColorArity ?? -1;
                if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "setpattern",
                    "an uncoloured pattern needs an underlying colour space of known arity");
                components = new double[n];
                for (int k = n - 1; k >= 0; k--)
                {
                    PsObject value = i.Operands.Pop();
                    if (!value.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, "setpattern");
                    components[k] = value.Num;
                }
            }

            PsGraphicsEngine g = i.Graphics;
            if (g == null) return;

            // currentcolor answers the operands as given: an uncoloured pattern's components, then the pattern.
            var operands = new PsObject[(components?.Length ?? 0) + 1];
            for (int k = 0; k < operands.Length - 1; k++) operands[k] = PsObject.Real(components[k]);
            operands[operands.Length - 1] = pattern;
            g.Gs.ColorOperands = operands;

            Built built = Lookup(instance);
            if (built == null || built.Uncoloured != uncoloured)
            {
                if (!_unbuiltReported)
                {
                    _unbuiltReported = true;
                    PostScriptDiagnostics.Report("PsPatternOps.setpattern",
                        "a pattern makepattern did not build was set; the colour was left as it was. Reported once.");
                }
                return;
            }

            string name = g.Resources.AddPattern(built.Pdf);
            if (!uncoloured)
            {
                g.Gs.Color = PsColor.Black.WithPattern(name, null);
                return;
            }

            if (!PsSpaceColor.TryColor(i, underlying, components, out PsColor colour, out _))
            {
                PostScriptDiagnostics.Report("PsPatternOps.setpattern",
                    $"an uncoloured pattern's /{underlying.Family} colour cannot be painted; the colour was left as it was");
                return;
            }
            colour = colour.WithoutPattern;
            g.Gs.Color = colour.WithPattern(name, g.Resources.AddPatternSpace(DevicePdfName(colour.Space)));
        }

        private static bool IsUncoloured(PsDict pattern) =>
            pattern.TryGetName("PatternType", out PsObject type) && type.IsNumber && type.Num == 1
            && pattern.TryGetName("PaintType", out PsObject paint) && paint.IsNumber && paint.Num == 2;

        private static Built Lookup(PsDict instance)
        {
            if (_built == null || !instance.TryGetName(ImplementationKey, out PsObject id) || !IsInteger(id)) return null;
            int index = id.AsInt();
            return index >= 0 && index < _built.Count ? _built[index] : null;
        }

        private static PsImageColorSpace DeviceSpaceOf(PsColor colour) =>
            PsImageColorSpace.Resolve(PsObject.LitName(DevicePdfName(colour.Space).Value));

        private static PdfName DevicePdfName(PsColorSpace space) => space switch
        {
            PsColorSpace.Gray => PdfName.DeviceGray,
            PsColorSpace.Rgb => PdfName.DeviceRGB,
            _ => PdfName.DeviceCMYK,
        };

        // ------------------------------------------------------------------
        //  Operands
        // ------------------------------------------------------------------

        private static bool TryMatrix(PsObject o, out PsMatrix matrix)
        {
            matrix = PsMatrix.Identity;
            if (o.Type != PsType.Array || o.AsArray().Length != 6) return false;
            var v = new double[6];
            for (int k = 0; k < 6; k++)
            {
                PsObject e = o.AsArray()[k];
                if (!e.IsNumber) return false;
                v[k] = e.Num;
            }
            matrix = new PsMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
            return true;
        }

        private static int RequiredInteger(PsDict d, string key)
        {
            if (!d.TryGetName(key, out PsObject o) || !IsInteger(o))
                throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern", "/" + key);
            return o.AsInt();
        }

        private static double RequiredNumber(PsDict d, string key)
        {
            if (!d.TryGetName(key, out PsObject o) || !o.IsNumber || !PsNumeric.IsFinite(o.Num))
                throw new PsErrorException(PsErrorKind.TypeCheck, "makepattern", "/" + key);
            return o.Num;
        }

        private static bool IsInteger(PsObject o) => o.IsNumber && PsNumeric.IsFinite(o.Num) && o.Num == Math.Floor(o.Num);


        private static PdfArray Numbers(double[] values) => Emit.PdfValues.Numbers(values);

        private static PdfArray Numbers(PsMatrix m) => Emit.PdfValues.Matrix(m);
    }
}
