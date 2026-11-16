// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The index → sRGB palette of an <c>/Indexed</c> colour space (§8.6.6.3), shared by both
    /// renderers.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is a type of its own.</b> It was two ~140-line bodies —
    /// <c>ContentStreamInterpreter.BuildIndexedPalette</c> and
    /// <c>SvgContentWriter.BuildSvgIndexedPalette</c> — and the SVG one's summary said it "Mirrors"
    /// the raster one. This project treats that sentence as a defect marker rather than
    /// documentation: nothing enforces prose. The pair had already drifted twice in opposite
    /// directions and been reconciled twice — CalGray lost <c>/Gamma</c> on the raster side, CalRGB
    /// on the SVG side — each fix leaving the renderers disagreeing until the other caught up. The
    /// second builder's own comment says so: "Fixing this in one palette builder and not the other
    /// is the divergence this pair of builders keeps producing."</para>
    ///
    /// <para><b>A leaf by construction.</b> No static fields, so no type initializer, so neither
    /// renderer acquires an initialization edge on the other by calling it — the property that makes
    /// <c>SvgSyntax</c> and <c>RenderBudget</c> safe shared leaves. Pointing one renderer at the
    /// other's static instead is what once crashed the Base test host part-way through a parallel
    /// run while still reporting success.</para>
    ///
    /// <para>Everything it needs was already leaf-level: <see cref="PdfColor"/> for the conversions,
    /// <c>IccColorTransform.ResolveAlternateColor</c> for the "profile if there is one,
    /// device family otherwise" decision, and <c>PdfFunctionEvaluator</c> for a tint transform.</para>
    /// </remarks>
    internal static class IndexedPalette
    {
        private const float MaxChannelValueF = 255f;

        /// <summary>Default Lab <c>/Range</c> for the a* and b* axes (§8.6.5.4, Table 65).</summary>
        private const float DefaultLabRangeMin = -100f;
        private const float DefaultLabRangeMax = 100f;

        /// <summary>
        /// Reads the three operands of <c>[/Indexed base hival lookup]</c> (§8.6.6.3) — the base
        /// space, the clamped <c>/HiVal</c>, and the lookup table's bytes. Returns <c>false</c> when
        /// <paramref name="csArr"/> is not an <c>/Indexed</c> array of at least two elements; the
        /// null-table policy is left to the caller, because the four call sites genuinely differ on
        /// it.
        /// </summary>
        /// <remarks>
        /// <para><b>One reading for all four sites, two per renderer</b>: <c>/HiVal</c> clamped with
        /// <see cref="PdfColorSpaceNames.MaxIndexedHiVal"/>, a <c>/Lookup</c> that is neither a
        /// string nor a stream refused, and a decode failure reported in one wording. Copies drift on
        /// each of the three.</para>
        ///
        /// <para><b>The unclamped one was a live divergence.</b> <see cref="Build"/> clamps
        /// internally, so the palette is always at most 256 entries — but the raster colour site
        /// clamped the <i>index</i> against the file's raw <c>/HiVal</c>. Given
        /// <c>/HiVal 400000000</c> and <c>sc 300</c> the raster path produced an index of 300, fell
        /// past the end of a 256-entry palette and painted <b>black</b>, where the SVG path clamped
        /// to 255 and painted a real palette entry. §8.6.6.3 caps <c>/HiVal</c> at 255, so the
        /// clamp is the conformant reading and it now happens once.</para>
        /// </remarks>
        internal static bool TryReadSpace(
            PdfDocument doc, PdfArray csArr, out PdfObject baseObj, out int hival, out byte[] table)
        {
            baseObj = null;
            hival = 0;
            table = null;
            if (csArr == null || csArr.Count < 2) return false;

            baseObj = PdfResolve.Value(doc, csArr[1]);

            hival = csArr.Count > 2 && PdfResolve.Value(doc, csArr[2]) is PdfNumber hn
                ? hn.IntValue
                : PdfColorSpaceNames.MaxIndexedHiVal;
            hival = Math.Max(0, Math.Min(PdfColorSpaceNames.MaxIndexedHiVal, hival));

            if (csArr.Count > 3)
            {
                var lookupObj = PdfResolve.Value(doc, csArr[3]);
                if (lookupObj is PdfString ps) table = ps.RawBytes;
                else if (lookupObj is PdfStream ls)
                {
                    try { table = ls.GetDecodedData(); }
                    catch (Exception ex)
                    {
                        RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                            "Indexed colour-space lookup table decode failed", ex);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Builds the palette for <c>[/Indexed base hival lookup]</c>.
        /// </summary>
        /// <param name="doc">Used to follow indirect references; may be null.</param>
        /// <param name="baseObj">The base colour space — a name or an array.</param>
        /// <param name="table">The lookup table's bytes; a null table yields an all-black palette.</param>
        /// <param name="hival">
        /// The file's <c>/HiVal</c>. <b>Clamped here, not at the call sites.</b> §8.6.6.3 caps it at
        /// 255, and everything below is sized and looped from it — an allocation, an init loop, and
        /// for a Separation/DeviceN base one tint-transform evaluation per entry. The image caller
        /// had always clamped; when <c>OpSc</c> was rewritten to share the raster builder it did
        /// not, so <c>/Indexed /DeviceRGB 400000000 (…)</c> made every <c>sc</c> allocate 1.6 GB and
        /// run a 400-million-iteration loop, and a negative <c>/HiVal</c> threw
        /// <c>OverflowException</c> out of the operator. One caller remembering the invariant and
        /// the other forgetting it is exactly what a shared helper is supposed to prevent, so the
        /// clamp lives with the code that depends on it.
        /// </param>
        /// <param name="intent">
        /// The rendering intent in force, so an <c>/Indexed</c> base that is an ICC profile picks
        /// the same <c>A2B</c> table a top-level fill through that profile would. Honouring it at
        /// one site and not the other would put two colours from one profile into one document.
        /// </param>
        internal static SKColor[] Build(PdfDocument doc, PdfObject baseObj, byte[] table, int hival,
                                        IccRenderingIntent intent)
        {
            hival = Math.Max(0, Math.Min(PdfColorSpaceNames.MaxIndexedHiVal, hival));

            var palette = new SKColor[hival + 1];
            for (int i = 0; i <= hival; i++) palette[i] = SKColors.Black;

            string baseName = (baseObj as PdfName)?.Value;
            PdfArray baseArr = baseObj as PdfArray;
            if (baseName == null && baseArr != null && baseArr.Count > 0)
                baseName = (baseArr[0] as PdfName)?.Value;

            // Expanded, because an inline image's /Indexed base can be written /G, /RGB or /CMYK
            // (§8.9.7, Table 93) and nothing upstream expands a name INSIDE an array. Unexpanded,
            // /G fell to the `default` arm below — three components per palette entry where the file
            // wrote one, so the table was read three bytes at a time and every colour was wrong.
            baseName = PdfColorSpaceNames.Expand(baseName);
            if (table == null) return palette;

            // Separation/DeviceN base: each palette entry is tint value(s) → alternate space.
            if ((baseName == PdfColorSpaceNames.Separation || baseName == PdfColorSpaceNames.DeviceN)
                && baseArr != null)
            {
                baseArr = NamedSpotColors.Substitute(doc, baseArr);   // a spot the named-colour profile names
                int inkN = baseName == PdfColorSpaceNames.Separation ? 1
                    : (baseArr.Count > 1 && PdfResolve.Value(doc, baseArr[1]) is PdfArray nm ? nm.Count : 1);
                var tintFn = baseArr.Count > 3 ? PdfResolve.Dict(doc, baseArr[3]) : null;
                PdfObject altObj = baseArr.Count > 2 ? baseArr[2] : null;
                string altCs = AlternateSpaceName(doc, altObj);
                var tints = new float[inkN];
                for (int idx = 0; idx <= hival; idx++)
                {
                    int off = idx * inkN;
                    if (tintFn == null || off + inkN - 1 >= table.Length) continue;
                    for (int c = 0; c < inkN; c++) tints[c] = table[off + c] / MaxChannelValueF;
                    // Two levels of nesting — an /Indexed base that is a Separation whose alternate
                    // is [/ICCBased …] — and the profile has to survive both.
                    palette[idx] = IccColorTransform.ResolveAlternateColor(doc, altObj,
                        PdfFunctionEvaluator.Evaluate(tintFn, tints, doc != null ? doc.ResolveObject : (Func<PdfObject, PdfObject>)null), altCs, intent);
                }
                return palette;
            }

            // The CIE parameter dictionary of a Cal*/Lab base, if any — /Gamma, /WhitePoint, /Range.
            // Through PdfResolve, which answers a direct object with no document. `doc?.ResolveObject`
            // answered null there, so a palette built without a document dropped the CIE parameters —
            // CalGray lost its gamma, Lab its range and white point — and a tint base its transform.
            PdfDictionary cieParams = baseArr?.Count > 1
                ? PdfResolve.Dict(doc, baseArr[1]) : null;

            int comps; string convCs;
            PdfArray iccBase = null;   // set only for an /ICCBased base; see the loop below
            switch (baseName)
            {
                case PdfColorSpaceNames.DeviceGray:
                    comps = PdfColorSpaceNames.GrayComponents; convCs = PdfColorSpaceNames.DeviceGray; break;

                // Cal* and Lab keep their FAMILY rather than folding into the device space: the
                // alias discards /Gamma, /Matrix and /WhitePoint. Each of these was got wrong in one
                // builder and right in the other at some point, in opposite directions.
                case PdfColorSpaceNames.CalGray:
                    comps = PdfColorSpaceNames.GrayComponents; convCs = PdfColorSpaceNames.CalGray; break;
                case PdfColorSpaceNames.CalRGB:
                    comps = PdfColorSpaceNames.RgbComponents;  convCs = PdfColorSpaceNames.CalRGB;  break;
                case PdfColorSpaceNames.Lab:
                    comps = PdfColorSpaceNames.RgbComponents;  convCs = PdfColorSpaceNames.Lab;     break;

                case PdfColorSpaceNames.DeviceCMYK:
                    comps = PdfColorSpaceNames.CmykComponents; convCs = PdfColorSpaceNames.DeviceCMYK; break;

                case PdfColorSpaceNames.ICCBased:
                {
                    int n = baseArr != null && baseArr.Count > 1
                            ? PdfColor.IccComponentCount(baseArr[1], doc)
                            : PdfColorSpaceNames.RgbComponents;
                    // The device space is the FALLBACK; iccBase carries the array so the loop below
                    // can consult the profile itself first.
                    iccBase = baseArr;
                    comps = n; convCs = PdfColor.DeviceSpaceForComponentCount(n); break;
                }

                default: // DeviceRGB and anything unrecognised
                    comps = PdfColorSpaceNames.RgbComponents; convCs = PdfColorSpaceNames.DeviceRGB; break;
            }

            float la = DefaultLabRangeMin, lA = DefaultLabRangeMax;
            float lb = DefaultLabRangeMin, lB = DefaultLabRangeMax;
            if (convCs == PdfColorSpaceNames.Lab && cieParams != null
                && PdfResolve.Array(doc, cieParams, PdfName.Range) is PdfArray lr && lr.Count >= 4)
            {
                la = PdfResolve.ArrayFloat(doc, lr, 0); lA = PdfResolve.ArrayFloat(doc, lr, 1);
                lb = PdfResolve.ArrayFloat(doc, lr, 2); lB = PdfResolve.ArrayFloat(doc, lr, 3);
            }

            var comp = new float[comps];
            for (int idx = 0; idx <= hival; idx++)
            {
                int off = idx * comps;
                if (off + comps - 1 >= table.Length) continue;

                if (convCs == PdfColorSpaceNames.Lab)
                {
                    // cieParams carries the space's /WhitePoint; the 3-argument overload assumes
                    // D50, which is only the default.
                    palette[idx] = PdfColor.LabToSRgb(
                        table[off] / MaxChannelValueF * 100f,
                        la + table[off + 1] / MaxChannelValueF * (lA - la),
                        lb + table[off + 2] / MaxChannelValueF * (lB - lb),
                        cieParams);
                }
                else if (convCs == PdfColorSpaceNames.CalGray)
                {
                    palette[idx] = PdfColor.CalGrayToSRgb(table[off] / MaxChannelValueF, cieParams);
                }
                else if (convCs == PdfColorSpaceNames.CalRGB)
                {
                    for (int c = 0; c < comps; c++) comp[c] = table[off + c] / MaxChannelValueF;
                    palette[idx] = PdfColor.CalRgbToSRgb(comp, cieParams);
                }
                else
                {
                    for (int c = 0; c < comps; c++) comp[c] = table[off + c] / MaxChannelValueF;
                    // An /ICCBased base: ask the profile before falling back to the device space its
                    // /N stands for. The palette entries are components in THAT space, so resolving
                    // them by /N alone is a silent deviation. ResolveAlternateColor rather than an
                    // open-coded try/else, so one place can be mutated to test every nested site.
                    palette[idx] = IccColorTransform.ResolveAlternateColor(doc, iccBase, comp, convCs,
                                                                          intent);
                }
            }
            return palette;
        }

        /// <summary>
        /// Resolves an <c>/Indexed</c> base or a Separation/DeviceN alternate to a name
        /// <see cref="PdfColor.ComponentsToColor"/> accepts — an <c>/ICCBased</c> space collapses to
        /// the device space its <c>/N</c> stands for.
        /// </summary>
        /// <remarks>
        /// The raster side was already a one-line forwarder to <see cref="PdfColor.ResolveSpace"/>;
        /// the SVG side open-coded the same decision in fifteen lines under a comment saying it
        /// mirrored the raster one. They agreed on every input — which is precisely why the
        /// consolidation is the assertion here and no <c>A(x) == B(x)</c> test is added: such a test
        /// becomes a tautology the moment the merge lands.
        /// </remarks>
        internal static string AlternateSpaceName(PdfDocument doc, PdfObject alt)
            => PdfColor.ResolveSpace(alt, doc).DeviceFamily;

        /// <summary>
        /// The palette of an <c>/Indexed</c> shading space, for the colours that arrive as an index,
        /// or null when the space is not <c>/Indexed</c> or has no lookup table.
        /// </summary>
        /// <param name="doc">Used to follow indirect references; may be null.</param>
        /// <param name="spaceObj">The shading's <c>/ColorSpace</c> entry, as written.</param>
        /// <param name="intent">The rendering intent in force, as for <see cref="Build"/>.</param>
        /// <param name="baseComponents">The base space's arity, which <see cref="ShadingColor"/>
        /// compares a colour's length with.</param>
        /// <remarks>
        /// Both renderers read an <c>/Indexed</c> shading in its <b>base</b> space, because producers'
        /// functions for types 1 to 3 output base components. But §8.7.4.5.1 lets a mesh of types 4
        /// to 7 use an <c>/Indexed</c> space with no <c>/Function</c>, and each vertex then carries one
        /// value — a palette index (its <c>/Decode</c> says one component) — and a conforming
        /// function-driven file outputs one index too. Those arrive here as a single value.
        /// </remarks>
        internal static SKColor[] ForShading(PdfDocument doc, PdfObject spaceObj, IccRenderingIntent intent,
                                             out int baseComponents)
        {
            baseComponents = 0;
            var rcs = PdfColor.ResolveSpace(spaceObj, doc);
            if (rcs.Family != PdfColorSpaceNames.Indexed
                || !TryReadSpace(doc, rcs.Array, out PdfObject baseObj, out int hival, out byte[] table)
                || table == null)
                return null;
            baseComponents = PdfColor.ResolveSpace(baseObj, doc).Components;
            return Build(doc, baseObj, table, hival, intent);
        }

        /// <summary>
        /// A shading colour through an <c>/Indexed</c> space's palette when it arrived as one value and
        /// the base takes more than one — the index a conforming file writes — or false, leaving the
        /// base-space reading. A grey base, where one value is both, keeps the base reading.
        /// </summary>
        internal static bool ShadingColor(SKColor[] palette, int baseComponents, float[] comps, out SKColor colour)
        {
            colour = default;
            if (palette == null || palette.Length == 0 || comps == null || comps.Length != 1 || baseComponents <= 1)
                return false;
            // The sc rule: round half away from zero, clamp into the palette (§8.6.6.3).
            int index = SaturatingCast.ToInt(Math.Round((double)comps[0], MidpointRounding.AwayFromZero));
            colour = palette[Math.Max(0, Math.Min(palette.Length - 1, index))];
            return true;
        }
    }
}
