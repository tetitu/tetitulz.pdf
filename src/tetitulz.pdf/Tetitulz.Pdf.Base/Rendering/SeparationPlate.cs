// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// One colorant of a separation render: the page is
    /// rendered once per plate, every paint's colour replaced by its tint on this plate, drawn as
    /// grey <c>1 − tint</c> on a white surface — so white is no ink and the existing compositor's
    /// separable blending is the subtractive rule §11.3.5 states for a CMYK blending space.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a pass per plate and not an N-channel surface.</b> Every paint already funnels
    /// its colour through one read (<see cref="PdfRenderContext"/>'s paint colour), so asking that
    /// read for a plate's tint reuses the whole renderer — clipping, transparency groups, soft
    /// masks, text, the display list — where an N-channel buffer would need a second compositor
    /// beside Skia's. The plates are exactly what an N-channel buffer would hold; only the order the
    /// channels are filled in differs.</para>
    ///
    /// <para><b>Overprint is exact here and simulated everywhere else.</b> A paint that does not
    /// name this plate's colorant and overprints leaves the plate as it was (§11.7.4); with
    /// overprint off it knocks the plate out to no ink. That is a per-colorant decision, which a
    /// plate is, and which an sRGB surface is not.</para>
    /// </remarks>
    internal sealed class SeparationPlate
    {
        /// <summary><see cref="ProcessIndex"/> for a spot colorant.</summary>
        internal const int SpotColorant = -1;

        private static readonly string[] ProcessColorants =
        {
            PdfColorSpaceNames.CyanColorant, PdfColorSpaceNames.MagentaColorant,
            PdfColorSpaceNames.YellowColorant, PdfColorSpaceNames.BlackColorant,
        };

        private readonly SpotColorantList _spots;
        private readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        internal SeparationPlate(string colorant, SpotColorantList spots, SeparationOutput output = null)
        {
            Colorant = colorant ?? throw new ArgumentNullException(nameof(colorant));
            ProcessIndex = Array.IndexOf(ProcessColorants, colorant);
            _spots = spots ?? new SpotColorantList();
            Output = output;
        }

        /// <summary>
        /// The CMYK output profile colours without CMYK components reach the process plates
        /// through, shared by every pass of a page; null for the naive inverse of
        /// <see cref="PdfColor.CmykToRgb"/>.
        /// </summary>
        internal SeparationOutput Output { get; }

        /// <summary>The plate's colorant name: a process colorant (§8.6.6.4) or a spot ink.</summary>
        internal string Colorant { get; }

        /// <summary>0 to 3 for Cyan, Magenta, Yellow, Black; <see cref="SpotColorant"/> otherwise.</summary>
        internal int ProcessIndex { get; }

        /// <summary>The four process plates, in device order.</summary>
        internal static IReadOnlyList<string> Process => ProcessColorants;

        /// <summary>Is <paramref name="name"/> a process colorant (§8.6.6.4)?</summary>
        internal static bool IsProcess(string name) => Array.IndexOf(ProcessColorants, name) >= 0;

        /// <summary>
        /// The spot colorants seen while rendering, in first-seen order — shared by every pass of
        /// one page, so the process passes discover the spot plates the later passes render.
        /// </summary>
        internal SpotColorantList Spots => _spots;

        /// <summary>How many paints on this plate fell back to the sRGB conversion where the file's own components were lost (<see cref="NoteApproximation"/>).</summary>
        internal int ApproximatedPaints { get; private set; }

        /// <summary>
        /// This plate's tint for a paint, or false when the paint leaves the plate untouched.
        /// </summary>
        /// <param name="sepTints">The colorants a <c>/Separation</c> or <c>/DeviceN</c> colour names, with their tints; null for any other space.</param>
        /// <param name="cmyk">DeviceCMYK components, or null.</param>
        /// <param name="rgb">The colour as sRGB, which every space has.</param>
        /// <param name="overprint">The paint's <c>/OP</c> or <c>/op</c>.</param>
        /// <param name="overprintMode">The graphics state's <c>/OPM</c>.</param>
        /// <param name="deviceGray">The colour was set in DeviceGray itself — black plate alone, output profile or not (DeviceGray to K).</param>
        /// <param name="intent">The paint's rendering intent, which picks the output profile's table.</param>
        /// <param name="tint">The plate's ink, 0 (none) to 1 (full).</param>
        /// <remarks>
        /// <para><b>The order is the specification's.</b> A colorant set (§8.6.6.4) marks exactly the
        /// colorants it names: <c>/All</c> marks every plate at its tint, <c>/None</c> marks none,
        /// a named plate takes its tint, and every other plate is knocked out — or, overprinting,
        /// left alone (§11.7.4.5, whatever the mode). DeviceCMYK marks the process plates with its
        /// components; under nonzero overprint mode a zero component leaves its plate alone
        /// (§11.7.4.3), and overprinting it leaves every spot plate alone, since the space names
        /// no spot. Any other space reaches the process plates through the inverse of
        /// <see cref="PdfColor.CmykToRgb"/> — DeviceGray lands on black alone — and is treated
        /// like DeviceCMYK for the spot plates. A DeviceRGB colour under the graphics state's
        /// <c>/BG</c> or <c>/UCR</c> and no output profile goes through them instead (§10.3.4,
        /// <see cref="BlackGeneration"/>).</para>
        /// </remarks>
        /// <param name="space">The paint's colour space, recorded with each spot ink it names (<see cref="SpotColorantList.SpaceOf"/>); null when not known.</param>
        /// <param name="deviceRgb">The colour was set in DeviceRGB itself, the one colour <paramref name="blackGeneration"/> converts.</param>
        /// <param name="blackGeneration">The graphics state's <c>/BG</c> and <c>/UCR</c>, or null for the device's own conversion.</param>
        internal bool TryTint(IReadOnlyDictionary<string, float> sepTints, float[] cmyk, SKColor rgb,
            bool overprint, int overprintMode, out float tint,
            bool deviceGray = false, IccRenderingIntent intent = IccRenderingIntents.Default, PdfArray space = null,
            bool deviceRgb = false, BlackGeneration blackGeneration = null)
        {
            tint = 0f;
            if (sepTints != null && sepTints.Count > 0)
            {
                foreach (var name in sepTints.Keys) _spots.Note(name, space);

                if (sepTints.TryGetValue(PdfColorSpaceNames.AllColorant, out float all)) { tint = Clamp01(all); return true; }
                if (sepTints.TryGetValue(Colorant, out float named)) { tint = Clamp01(named); return true; }
                if (OnlyNone(sepTints)) return false;
                return !overprint;
            }

            if (ProcessIndex == SpotColorant) return !overprint;

            if (cmyk != null && cmyk.Length >= 4)
            {
                tint = Clamp01(cmyk[ProcessIndex]);
                return !(overprint && overprintMode == 1 && tint <= 0f);
            }

            if (Output != null && !deviceGray && Output.TryConvert(rgb, intent, out float[] converted))
            {
                tint = Clamp01(converted[ProcessIndex]);
                return true;
            }

            float c, m, y, k;
            if (deviceRgb && blackGeneration != null) blackGeneration.ToCmyk(rgb, out c, out m, out y, out k);
            else OverprintCompositor.RgbToCmykExactInverse(rgb, out c, out m, out y, out k);
            tint = Clamp01(ProcessIndex switch { 0 => c, 1 => m, 2 => y, _ => k });
            return true;
        }

        /// <summary>
        /// How an image's samples reach this plate — <see cref="TryTint"/>'s rules, decided once
        /// for the colour space rather than per sample — or null when the space has no components a
        /// plate can read (RGB, the CIE-based spaces, <c>/Indexed</c>), which the sRGB filter then
        /// approximates.
        /// </summary>
        /// <param name="family">The image's device family (an <c>/ICCBased</c> space as its <c>/N</c>'s).</param>
        /// <param name="colorants">The names of a <c>/Separation</c> or <c>/DeviceN</c> space, in component order; null otherwise.</param>
        /// <param name="overprint">The graphics state's <c>/op</c>.</param>
        /// <remarks>
        /// No overprint mode: §8.6.7 — "non-zero overprint mode … shall not, however, apply to the
        /// painting of images or shadings" — so an overprinting DeviceCMYK image writes all four
        /// process plates whatever its samples, and leaves only the spot plates alone.
        ///
        /// </remarks>
        /// <param name="space">The image's colour space, recorded with each spot ink it names; null when not known.</param>
        internal ImageRoute? RouteImage(string family, IReadOnlyList<string> colorants, bool overprint, PdfArray space = null)
        {
            if (family == PdfColorSpaceNames.Separation || family == PdfColorSpaceNames.DeviceN)
            {
                if (colorants == null) return null;
                bool onlyNone = true;
                int named = -1, all = -1;
                for (int i = 0; i < colorants.Count; i++)
                {
                    string name = colorants[i];
                    _spots.Note(name, space);
                    if (name != PdfColorSpaceNames.NoneColorant) onlyNone = false;
                    if (name == PdfColorSpaceNames.AllColorant && all < 0) all = i;
                    if (name == Colorant && named < 0) named = i;
                }
                if (all >= 0) return ImageRoute.Component(all, invert: false);
                if (named >= 0) return ImageRoute.Component(named, invert: false);
                if (onlyNone) return ImageRoute.Preserve;
                return overprint ? ImageRoute.Preserve : ImageRoute.KnockOut;
            }

            // A space that names no colorant — every family but a colorant set, and not /Indexed, whose
            // base may be one — puts no ink on a spot plate: it knocks the plate out, or leaves it
            // alone when it overprints. Exact whatever the samples are.
            if (ProcessIndex == SpotColorant && family != PdfColorSpaceNames.Indexed)
                return overprint ? ImageRoute.Preserve : ImageRoute.KnockOut;

            bool cmyk = family == PdfColorSpaceNames.DeviceCMYK, grey = family == PdfColorSpaceNames.DeviceGray;
            if (!cmyk && !grey) return null;
            if (cmyk) return ImageRoute.Component(ProcessIndex, invert: false);
            return ProcessIndex == 3 ? ImageRoute.Component(0, invert: true) : ImageRoute.KnockOut;
        }

        /// <summary>
        /// The colorants a <c>/Separation</c> or <c>/DeviceN</c> space names, in order — element [1],
        /// one name or an array of them.
        /// </summary>
        internal static List<string> ColorantNames(PdfArray space)
        {
            var names = new List<string>();
            var entry = space != null && space.Count > 1 ? space[1] : null;
            if (entry is PdfName single) names.Add(single.Value);
            else if (entry is PdfArray array)
                for (int i = 0; i < array.Count; i++)
                    if (array[i] is PdfName name) names.Add(name.Value);
            return names;
        }

        /// <summary>
        /// Each named colorant's tint from the space's components, clamped to [0, 1]; a missing
        /// component is 1. The snapshot a solid paint takes and a shading's samples alike.
        /// </summary>
        internal static Dictionary<string, float> ColorantTints(PdfArray space, float[] components)
        {
            var tints = new Dictionary<string, float>(StringComparer.Ordinal);
            List<string> names = ColorantNames(space);
            // A single /Separation name reads the first component; DeviceN's names read theirs in order.
            for (int i = 0; i < names.Count; i++)
                tints[names[i]] = components != null && components.Length > i ? PdfColor.Clamp01(components[i]) : 1f;
            return tints;
        }

        /// <summary>The colour that lays <paramref name="tint"/> on a plate surface: grey <c>1 − tint</c> at <paramref name="alpha"/>.</summary>
        internal static SKColor PlateColor(float tint, byte alpha)
        {
            byte v = (byte)(255 - PdfColor.Channel(tint));
            return new SKColor(v, v, v, alpha);
        }

        /// <summary>
        /// Records that a paint reached this plate through its sRGB pixels rather than its own
        /// components — an image, a shading or a pattern, whose colour space the surface has
        /// already discarded — and reports each reason once per pass through
        /// <see cref="RenderDiagnosticKind.SeparationApproximated"/>.
        /// </summary>
        internal void NoteApproximation(string reason)
        {
            ApproximatedPaints++;
            if (_reported.Add(reason))
                RenderDiagnostics.Report(RenderDiagnosticKind.SeparationApproximated,
                    $"Plate {Colorant}: {reason} reached the plate through its sRGB colour (the inverse of the CMYK conversion), not its own components.", null);
        }

        /// <summary>
        /// The colour filter that turns sRGB source pixels into this plate's grey by the same
        /// inverse <see cref="TryTint"/> uses for a space without components; a spot plate gets
        /// no ink from them. Built once per plate.
        /// </summary>
        internal SKColorFilter SrgbFilter => _srgbFilter ??= BuildSrgbFilter(ProcessIndex);

        private SKColorFilter _srgbFilter;

        private static SKColorFilter BuildSrgbFilter(int processIndex)
        {
            if (processIndex == SpotColorant)
                return SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn);

            // k = 1 − max(r, g, b), so the black plate's grey (1 − k) is max(r, g, b); a chromatic
            // plate's ink is 1 − channel/max, so its grey is channel/max (white where max is 0,
            // since black needs no chromatic ink) — RgbToCmykExactInverse, per pixel.
            string grey = processIndex switch
            {
                3 => "mx",
                _ => $"mx > 0 ? c.{"rgb"[processIndex]} / mx : 1",
            };
            string sksl =
                "half4 main(half4 c) {\n" +
                "  half mx = max(c.r, max(c.g, c.b));\n" +
                $"  half v = {grey};\n" +
                "  return half4(v, v, v, c.a);\n" +
                "}\n";
            using var effect = SKRuntimeEffect.CreateColorFilter(sksl, out string errors);
            if (effect == null) throw new InvalidOperationException("separation colour filter: " + errors);
            return effect.ToColorFilter();
        }

        private static bool OnlyNone(IReadOnlyDictionary<string, float> sepTints)
        {
            foreach (var name in sepTints.Keys)
                if (name != PdfColorSpaceNames.NoneColorant) return false;
            return true;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// A separation's CMYK output profile, shared by every plate pass of a page:
    /// an sRGB colour through the profile's table for the paint's intent, with black-point
    /// compensation when asked (<see cref="IccOutputTransform"/>), each colour converted once.
    /// </summary>
    internal sealed class SeparationOutput
    {
        private readonly byte[] _profile;
        private readonly bool _blackPointCompensation;
        private readonly IccOutputTransform[] _transforms = new IccOutputTransform[8];
        private readonly Dictionary<(uint, IccRenderingIntent), float[]> _converted = new Dictionary<(uint, IccRenderingIntent), float[]>();

        private SeparationOutput(byte[] profile, bool blackPointCompensation)
        {
            _profile = profile;
            _blackPointCompensation = blackPointCompensation;
        }

        /// <summary>The output for a CMYK profile, or null and the reason it cannot be one.</summary>
        internal static SeparationOutput TryCreate(byte[] profile, bool blackPointCompensation, out string reason)
        {
            var transform = IccOutputTransform.TryCreate(profile, IccRenderingIntents.Default, blackPointCompensation, out reason);
            if (transform == null) return null;
            if (transform.Channels != 4)
            {
                reason = $"the output profile produces {transform.Channels} channels; a separation's process plates need CMYK";
                return null;
            }
            var output = new SeparationOutput(profile, blackPointCompensation);
            output._transforms[(int)IccRenderingIntents.Default] = transform;
            return output;
        }

        /// <summary><paramref name="rgb"/>'s CMYK through the profile at <paramref name="intent"/>.</summary>
        internal bool TryConvert(SKColor rgb, IccRenderingIntent intent, out float[] cmyk)
        {
            var key = ((uint)rgb.WithAlpha(255), intent);
            if (_converted.TryGetValue(key, out cmyk)) return cmyk != null;

            // A slot per table and compensation: /UseBlackPtComp /OFF turns the output side's off too.
            IccRenderingIntent table = IccRenderingIntents.Table(intent);
            bool compensates = IccRenderingIntents.Compensates(intent);
            int slot = (int)table + (compensates ? 0 : 4);
            var transform = _transforms[slot] ??= IccOutputTransform.TryCreate(_profile, table, _blackPointCompensation && compensates, out _);
            var device = new float[4];
            cmyk = transform != null && transform.TryConvert(rgb.WithAlpha(255), device) ? device : null;
            _converted[key] = cmyk;
            return cmyk != null;
        }
    }

    /// <summary>What a plate takes from each sample of an image (<see cref="SeparationPlate.RouteImage"/>).</summary>
    internal readonly struct ImageRoute
    {
        private ImageRoute(int kind, int component, bool invert)
        {
            _kind = kind;
            ComponentIndex = component;
            Invert = invert;
        }

        private readonly int _kind;

        /// <summary>The image leaves the plate as it was (overprint, or <c>/None</c>).</summary>
        internal static ImageRoute Preserve => new ImageRoute(0, -1, false);

        /// <summary>The image lays no ink where it paints.</summary>
        internal static ImageRoute KnockOut => new ImageRoute(1, -1, false);

        /// <summary>The plate's ink is one component of each sample (inverted for a grey sample on black).</summary>
        internal static ImageRoute Component(int index, bool invert) => new ImageRoute(2, index, invert);

        internal bool Preserves => _kind == 0;
        internal bool KnocksOut => _kind == 1;

        /// <summary>The component a sample's ink is read from; -1 unless this is a component route.</summary>
        internal int ComponentIndex { get; }

        /// <summary>Ink is <c>1 − component</c> (DeviceGray on black).</summary>
        internal bool Invert { get; }

    }

    /// <summary>
    /// The spot colorants one page uses, in the order first seen: every name a colorant set
    /// carries except the process colorants and the reserved <c>/All</c> and <c>/None</c>.
    /// </summary>
    internal sealed class SpotColorantList
    {
        private readonly List<string> _names = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, PdfArray> _spaces = new Dictionary<string, PdfArray>(StringComparer.Ordinal);

        internal IReadOnlyList<string> Names => _names;

        /// <summary>
        /// Records a spot ink, and with it the first <c>/Separation</c> or <c>/DeviceN</c> space seen
        /// naming it — what an overprint preview evaluates the ink's appearance through. An uncoloured
        /// pattern's <c>[/Pattern base]</c> records its base.
        /// </summary>
        internal void Note(string name, PdfArray space = null)
        {
            if (string.IsNullOrEmpty(name) || SeparationPlate.IsProcess(name)
                || name == PdfColorSpaceNames.AllColorant || name == PdfColorSpaceNames.NoneColorant) return;
            if (_seen.Add(name)) _names.Add(name);
            if (space != null && space.Count > 1 && (space[0] as PdfName)?.Value == PdfColorSpaceNames.Pattern)
                space = space[1] as PdfArray;
            if (space != null && !_spaces.ContainsKey(name) && SeparationPlate.ColorantNames(space).Contains(name))
                _spaces[name] = space;
        }

        /// <summary>The colour space recorded with <paramref name="name"/>, or null.</summary>
        internal PdfArray SpaceOf(string name) => _spaces.TryGetValue(name, out PdfArray space) ? space : null;
    }
}
