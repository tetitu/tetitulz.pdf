// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// The colour a Level 2 <c>setcolor</c> sets, as a fill or stroke paints it (PLRM §4.8): the device
    /// spaces and their CIE analogues directly; <c>/Indexed</c> through its palette into its base;
    /// <c>/Separation</c> and <c>/DeviceN</c> through the program's own tint transform into the alternate
    /// space — the colour the separation stands for on a composite, without the plate, which this
    /// converter does not write (<see cref="TryColor"/> says it was approximated).
    /// </summary>
    internal static class PsSpaceColor
    {
        /// <summary>
        /// The components a space's colour starts at when <c>setcolorspace</c> establishes it (PLRM §4.8):
        /// black in a device space (CMYK's <c>0 0 0 1</c>), index 0, and a tint of 1.0 — full colorant —
        /// for a separation and for each of a DeviceN space's components.
        /// </summary>
        internal static double[] Initial(PsImageColorSpace space)
        {
            switch (space.Family)
            {
                case PdfColorSpaceNames.DeviceCMYK: return new[] { 0.0, 0, 0, 1 };
                case "Separation": return new[] { 1.0 };
                case "DeviceN":
                {
                    var full = new double[Math.Max(1, space.Components)];
                    for (int k = 0; k < full.Length; k++) full[k] = 1;
                    return full;
                }
                default: return new double[Math.Max(0, space.SetColorArity)];
            }
        }

        /// <summary>
        /// The colour <paramref name="components"/> stand for in <paramref name="space"/>, or false where
        /// this converter cannot paint it — an ICC, CIE, Lab or Pattern space, a separation whose alternate
        /// or transform it cannot use. <paramref name="approximated"/> is true for a separation drawn in its
        /// alternate space.
        /// </summary>
        internal static bool TryColor(PsInterpreter i, PsImageColorSpace space, double[] components,
            out PsColor color, out bool approximated)
            => ColorAt(i, space, components, 0, out color, out approximated);

        private static bool ColorAt(PsInterpreter i, PsImageColorSpace space, double[] c, int depth,
            out PsColor color, out bool approximated)
        {
            color = PsColor.Black;
            approximated = false;
            if (space == null || depth > 2) return false;

            // A CIE-based space: the colour evaluated to XYZ and painted as the Lab it is carried in.
            if (space.Cie != null)
            {
                double[] lab = space.Cie.ToLab(c);
                color = DeviceApproximation(PsImageColorSpace.LabOf(space.Cie), lab).InSpace(space.Cie.LabSpace, lab);
                return true;
            }

            // An ICC, Cal or Lab space carried as itself: the colour is painted there, each component
            // held to its range, and reads back as its device approximation.
            if (space.NamedRange != null && space.PdfColorSpace != null)
            {
                var held = new double[space.Components];
                for (int k = 0; k < held.Length; k++)
                    held[k] = Math.Max(space.NamedRange[2 * k], Math.Min(space.NamedRange[2 * k + 1], At(c, k)));
                color = DeviceApproximation(space, held).InSpace(space.PdfColorSpace, held);
                return true;
            }

            switch (space.Family)
            {
                case PdfColorSpaceNames.DeviceGray:
                case "CalGray":
                    color = PsColor.Gray(At(c, 0));
                    return true;
                case PdfColorSpaceNames.DeviceRGB:
                case "CalRGB":
                    color = PsColor.Rgb(At(c, 0), At(c, 1), At(c, 2));
                    return true;
                case PdfColorSpaceNames.DeviceCMYK:
                    color = PsColor.Cmyk(At(c, 0), At(c, 1), At(c, 2), At(c, 3));
                    return true;
                case "Indexed":
                {
                    if (space.Base == null || space.Lookup == null) return false;
                    int n = space.Base.SetColorArity;
                    if (n <= 0) return false;
                    double raw = At(c, 0);
                    int index = SaturatingCast.ToInt(Math.Max(0, Math.Min(space.Hival, Math.Round(raw))));
                    var baseComponents = new double[n];
                    for (int k = 0; k < n; k++)
                    {
                        // A palette byte spans its base component's range (§8.6.6.3): 0 to 1 for a
                        // device space, a Lab space's L 0 to 100 and a and b over its /Range.
                        int at = index * n + k;
                        double unit = at < space.Lookup.Length ? space.Lookup[at] / 255.0 : 0;
                        double lo = space.Base.NamedRange?[2 * k] ?? 0, hi = space.Base.NamedRange?[2 * k + 1] ?? 1;
                        baseComponents[k] = lo + unit * (hi - lo);
                    }
                    return ColorAt(i, space.Base, baseComponents, depth + 1, out color, out approximated);
                }
                case "Separation":
                case "DeviceN":
                {
                    PsImageColorSpace alternate = space.Alternate;
                    if (alternate == null || alternate.SetColorArity <= 0) return false;
                    double[] alternateComponents = Transform(i, space.TintTransform, c, alternate.SetColorArity);
                    if (alternateComponents == null) return false;
                    approximated = true;
                    return ColorAt(i, alternate, alternateComponents, depth + 1, out color, out _);
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// A one-ink <c>/Separation</c> image's samples as an <c>/Indexed</c> image over the alternate
        /// space: each sample value's tint — through the image's <c>/Decode</c>, folded into the palette, so
        /// the samples travel as they are — run through the tint transform once, at most 256 values for at
        /// most 8 bits a sample. Null where it cannot be: more than 8 bits, an alternate that is not a
        /// device space, a transform that gives no answer. The colour is the composite's; the plate is not
        /// written, which <see cref="PsImageColorSpace.Approximation"/> says.
        /// </summary>
        internal static PsImageColorSpace SeparationImageSpace(PsInterpreter i, PsImageColorSpace separation,
            int bitsPerComponent, double[] decode)
        {
            if (separation?.Family != "Separation" || bitsPerComponent < 1 || bitsPerComponent > 8) return null;
            PsImageColorSpace alternate = separation.Alternate;
            if (alternate?.PdfColorSpace == null || alternate.Base != null || alternate.SetColorArity <= 0) return null;
            int n = alternate.SetColorArity, hival = (1 << bitsPerComponent) - 1;
            double d0 = decode != null && decode.Length >= 2 ? decode[0] : 0, d1 = decode != null && decode.Length >= 2 ? decode[1] : 1;
            var lookup = new byte[(hival + 1) * n];
            for (int k = 0; k <= hival; k++)
            {
                double[] colour = Transform(i, separation.TintTransform, new[] { d0 + (d1 - d0) * k / hival }, n);
                if (colour == null) return null;
                for (int j = 0; j < n; j++)
                    lookup[k * n + j] = SaturatingCast.ToByte(Math.Round(Math.Max(0, Math.Min(1, colour[j])) * 255));
            }
            return PsImageColorSpace.IndexedOver(alternate, hival, lookup,
                "each tint is drawn as its alternate-space colour through an /Indexed palette of the tint " +
                "transform's values; the separation itself is not written");
        }

        /// <summary>The entries a one-ink space's sampled tint transform has.</summary>
        internal const int SeparationSamples = 256;

        /// <summary>
        /// A <c>/Separation</c> or <c>/DeviceN</c> space as PDF writes it —
        /// <c>[/Separation name alternate f]</c> or <c>[/DeviceN [names] alternate f]</c> — the plates
        /// named, and <c>f</c> the program's tint transform sampled into a FunctionType 0 over a grid of
        /// <paramref name="samplesPerInput"/> evenly spaced tints a colorant, eight bits a sample, which a
        /// reader interpolates linearly between. One ink takes <see cref="SeparationSamples"/>; several
        /// take the most a colorant that keeps the grid within
        /// <see cref="PsLimits.MaxTintTransformSamples"/>, at least two. Null, with
        /// <paramref name="refusal"/>, where the space names no colorants, its alternate is not a
        /// device space, its transform gives no answer, or two samples a colorant exceed the cap.
        /// </summary>
        internal static PdfArray SpecialSpace(PsInterpreter i, PsImageColorSpace space, out string refusal,
            out int samplesPerInput)
        {
            refusal = null;
            samplesPerInput = 0;
            bool separation = space?.Family == "Separation";
            if (!separation && space?.Family != "DeviceN")
                return Refuse(out refusal, "it is not a /Separation or /DeviceN space");
            int inputs = separation ? 1 : space.Colorants?.Length ?? 0;
            if (inputs == 0 || (separation ? space.Colorant == null : Array.IndexOf(space.Colorants, null) >= 0))
                return Refuse(out refusal, $"its /{space.Family} space does not name its colorants");
            PsImageColorSpace alternate = space.Alternate;
            if (alternate?.PdfColorSpace == null || alternate.Base != null || alternate.SetColorArity <= 0)
                return Refuse(out refusal, $"its /{space.Family} space's alternate is not a device space");

            int k = GridSide(inputs);
            if (k < 2)
                return Refuse(out refusal, $"its {inputs} colorants need more than {nameof(PsLimits.MaxTintTransformSamples)} " +
                                           $"({PsLimits.MaxTintTransformSamples}) samples at two a colorant");
            int outputs = alternate.SetColorArity;
            long points = 1;
            for (int d = 0; d < inputs; d++) points *= k;

            var samples = new byte[points * outputs];
            var tints = new double[inputs];
            for (long j = 0; j < points; j++)
            {
                long rest = j;
                for (int d = 0; d < inputs; d++)          // the first input varies fastest (ISO 32000-2 §7.10.2)
                {
                    tints[d] = (rest % k) / (double)(k - 1);
                    rest /= k;
                }
                double[] colour = Transform(i, space.TintTransform, tints, outputs);
                if (colour == null) return Refuse(out refusal, $"its /{space.Family} space's tint transform gave no answer");
                for (int o = 0; o < outputs; o++)
                    samples[j * outputs + o] = SaturatingCast.ToByte(Math.Round(Math.Max(0, Math.Min(1, colour[o])) * 255));
            }

            var function = new PdfStream();
            function[PdfName.FunctionType] = new PdfNumber(0);
            function[PdfName.Domain] = UnitRanges(inputs);
            function[PdfName.Range] = UnitRanges(outputs);
            var size = new PdfArray();
            for (int d = 0; d < inputs; d++) size.Add(new PdfNumber(k));
            function[PdfName.Size] = size;
            function[PdfName.BitsPerSample] = new PdfNumber(8);
            function.RawData = samples;

            var pdf = new PdfArray();
            if (separation)
            {
                pdf.Add(PdfName.Separation);
                pdf.Add(new PdfName(space.Colorant));
            }
            else
            {
                pdf.Add(new PdfName(PdfColorSpaceNames.DeviceN));
                var names = new PdfArray();
                foreach (string colorant in space.Colorants) names.Add(new PdfName(colorant));
                pdf.Add(names);
            }
            pdf.Add(alternate.PdfColorSpace);
            pdf.Add(function);
            samplesPerInput = k;
            return pdf;
        }

        /// <summary>The most samples a side, at most <see cref="SeparationSamples"/>, a grid of <paramref name="inputs"/> sides may have within the cap.</summary>
        private static int GridSide(int inputs)
        {
            int k = 1;
            while (true)
            {
                long next = 1;
                for (int d = 0; d < inputs && next <= PsLimits.MaxTintTransformSamples; d++) next *= k + 1;
                if (next > PsLimits.MaxTintTransformSamples || k + 1 > SeparationSamples) return k;
                k++;
            }
        }

        private static PdfArray UnitRanges(int count)
        {
            var array = new PdfArray();
            for (int k = 0; k < count; k++) { array.Add(new PdfNumber(0)); array.Add(new PdfNumber(1)); }
            return array;
        }

        private static PdfArray Refuse(out string refusal, string why)
        {
            refusal = why;
            return null;
        }

        /// <summary>
        /// A tint transform applied: a procedure run with the tints on the operand stack in a child
        /// interpreter sharing the program's dictionaries and work budget (as a Type 3 glyph procedure
        /// is), leaving <paramref name="outputs"/> numbers; or a Level 3 function dictionary of
        /// FunctionType 2 (exponential interpolation). Null where neither gives an answer.
        /// </summary>
        internal static double[] Transform(PsInterpreter parent, PsObject transform, double[] tints, int outputs)
        {
            if (transform.Type == PsType.Dictionary) return Exponential(transform.AsDict(), tints, outputs);
            if (transform.Type != PsType.Array || !transform.IsExecutable) return null;

            var child = new PsInterpreter(parent.Options, parent.Allocation, parent.SharedBudget, parent.Dicts);
            int dictDepth = parent.Dicts.Count;
            try
            {
                foreach (double tint in tints) child.Operands.Push(PsObject.Real(tint));
                PsArray body = transform.AsArray();
                var program = new List<PsObject>(body.Length);
                for (int k = 0; k < body.Length; k++) program.Add(body[k]);
                child.Run(program);
            }
            catch (PsErrorException ex)
            {
                PostScriptDiagnostics.Report("PsSpaceColor.Transform", "a tint transform failed; the colour was not changed", ex);
                return null;
            }
            finally
            {
                parent.Dicts.TruncateTo(dictDepth);
            }

            if (child.Operands.Count < outputs) return null;
            var result = new double[outputs];
            for (int k = outputs - 1; k >= 0; k--)
            {
                PsObject value = child.Operands.Pop();
                if (!value.IsNumber) return null;
                result[k] = value.Num;
            }
            return result;
        }

        /// <summary>FunctionType 2 (PLRM §3.10): <c>C0 + x^N × (C1 − C0)</c> for one input, the others refused.</summary>
        private static double[] Exponential(PsDict function, double[] tints, int outputs)
        {
            if (tints.Length != 1
                || !function.TryGetName("FunctionType", out PsObject type) || !type.IsNumber || type.AsInt() != 2
                || !function.TryGetName("N", out PsObject exponent) || !exponent.IsNumber)
                return null;
            double[] c0 = Numbers(function, "C0", outputs, 0), c1 = Numbers(function, "C1", outputs, 1);
            if (c0 == null || c1 == null) return null;
            double x = Math.Max(0, Math.Min(1, tints[0]));
            double weight = Math.Pow(x, exponent.Num);
            if (double.IsNaN(weight) || double.IsInfinity(weight)) return null;
            var result = new double[outputs];
            for (int k = 0; k < outputs; k++) result[k] = c0[k] + weight * (c1[k] - c0[k]);
            return result;
        }

        private static double[] Numbers(PsDict function, string key, int count, double fallback)
        {
            double[] values = PsNumeric.FiniteArray(function, key, count, out bool present);
            if (present) return values;   // a non-finite entry refused, as the CIE and shading readers refuse it
            values = new double[count];
            for (int k = 0; k < count; k++) values[k] = fallback;
            return values;
        }

        private static double At(double[] c, int k) => k < c.Length ? c[k] : 0;

        /// <summary>
        /// The device colour a colour in an ICC, Cal or Lab space reads back as, and is painted in past
        /// the colour space cap: an ICC space's components as the device space of its count, a Cal
        /// space's as its analogue, a Lab colour through XYZ to sRGB (<see cref="PsCie"/>).
        /// </summary>
        private static PsColor DeviceApproximation(PsImageColorSpace space, double[] c)
        {
            if (space.Family == PdfColorSpaceNames.Lab)
            {
                var (x, y, z) = PsCie.LabToXyz(c[0], c[1], c[2], space.WhitePoint ?? PsCie.D50);
                var (r, g, b) = PsCie.XyzToRgb(x, y, z, space.WhitePoint ?? PsCie.D50);
                return PsColor.Rgb(r, g, b);
            }
            return c.Length switch
            {
                1 => PsColor.Gray(c[0]),
                3 => PsColor.Rgb(c[0], c[1], c[2]),
                _ => PsColor.Cmyk(At(c, 0), At(c, 1), At(c, 2), At(c, 3)),
            };
        }
    }
}
