// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    internal sealed partial class ContentStreamInterpreter
    {
        private void OpSetCS(ContentStreamOperation op, bool stroke)
        {
            if (op.Operands.Length < 1) return;
            string csName = GetName(op, 0);
            if (csName == null) return;

            // Resolve and store the full array for Separation/DeviceN
            string resolved = ResolveColorSpaceName(csName, out PdfArray csArray);
            bool isPattern = resolved == PdfColorSpaceNames.Pattern;
            // A Pattern space's underlying space may itself be named from the resources (§8.6.6.2,
            // `[/Pattern /CS0]`); stored resolved, so every reader of it — the uncoloured pattern's
            // colour, a separation plate's spot record — sees the space and not a name.
            if (isPattern && csArray != null && csArray.Count > 1 && csArray[1] is PdfName
                && ColorSpaceResources.Lookup((PdfName)csArray[1], _resources, _doc) is PdfObject namedBase)
                csArray = new PdfArray { csArray[0], namedBase };
            if (stroke)
            {
                _ctx.GS.StrokeColorSpace      = resolved;
                _ctx.GS.StrokeColorSpaceArray = csArray;
                _ctx.GS.StrokeIsPattern       = isPattern;
            }
            else
            {
                _ctx.GS.FillColorSpace      = resolved;
                _ctx.GS.FillColorSpaceArray = csArray;
                _ctx.GS.FillIsPattern       = isPattern;
            }

            SetInitialColorForSpace(resolved, csArray, stroke);
        }

        /// <summary>
        /// §8.6.8: <c>cs</c>/<c>CS</c> "shall then set the current colour to its initial value,
        /// which depends on the colour space" (Table 74) — black for the device and CIE-based
        /// spaces, index 0 for <c>/Indexed</c>, tint 1.0 in every component for
        /// <c>/Separation</c> and <c>/DeviceN</c>, and, for <c>/Pattern</c>, a pattern that paints
        /// nothing until <c>scn</c> selects one.
        ///
        /// <para>Without this the operator changed only the space and left the previous colour, so
        /// <c>1 0 0 rg /DeviceGray cs 0 0 100 100 re f</c> painted RED — the previous colour
        /// reinterpreted in a space it did not come from.</para>
        /// </summary>
        /// <remarks>
        /// The initial components are pushed through <see cref="OpSc"/> rather than converted here,
        /// so a Separation's tint transform, an Indexed palette lookup and a CalRGB <c>/Gamma</c>
        /// all apply exactly as they do for an explicit <c>sc</c> — one colour-setting path, not a
        /// second one that would drift from it.
        /// </remarks>
        private void SetInitialColorForSpace(string family, PdfArray csArray, bool stroke)
        {
            PdfObject[] operands = PdfColor.InitialColorOperands(family, csArray, _doc);
            if (operands == null)
            {
                // /Pattern: only THIS paint side's name. `cs` and `CS` are separate operators, so
                // `/Pattern CS` must not discard the fill pattern a preceding `scn` selected.
                if (stroke) _ctx.GS.StrokePatternName = null;
                else        _ctx.GS.FillPatternName   = null;
                return;
            }

            OpSc(new ContentStreamOperation(
                stroke ? PdfOperators.SetColorStrokeN : PdfOperators.SetColorFillN, operands), stroke);
        }

        /// <summary>
        /// Resolves a <c>cs</c>/<c>CS</c> name operand through the page's <c>/Resources
        /// /ColorSpace</c> dictionary into the family the colour operators dispatch on, and
        /// hands back the array that carries the space's parameters.
        /// </summary>
        /// <remarks>
        /// <para>The array is returned for <b>every</b> array-form space, not only for the handful
        /// of families that were listed here one by one. That list had already omitted
        /// <c>/Pattern</c>, which is written <c>[/Pattern base]</c> for an uncoloured (PaintType 2)
        /// pattern — so <see cref="OpSc"/>'s underlying-colour-space lookup found nothing and always
        /// fell back to inferring the base from the operand count.</para>
        /// <para>The four device family names and <c>/Pattern</c> are reserved (§8.6.3) and are not
        /// looked up in the resource dictionary, so a resource cannot shadow them.</para>
        /// </remarks>
        private string ResolveColorSpaceName(string name, out PdfArray csArray)
        {
            csArray = null;
            switch (name)
            {
                case PdfColorSpaceNames.DeviceGray:
                case PdfColorSpaceNames.DeviceRGB:
                case PdfColorSpaceNames.DeviceCMYK:
                {
                    // §8.6.5.6: a device space selected is the default the resources declare for it.
                    var substitute = DefaultColorSpaces.For(name, _resources, _doc);
                    if (substitute == null) return name;
                    var remapped = PdfColor.ResolveSpace(substitute, _doc);
                    csArray = remapped.Array;
                    return remapped.DeviceFamily;
                }
                case PdfColorSpaceNames.Pattern: return name;
            }
            if (_resources?[PdfName.ColorSpace] is not PdfObject csRes) return name;
            if (_doc.ResolveObject(csRes) is not PdfDictionary cat) return name;

            // A named space's device base or alternate is remapped too (§8.6.5.6).
            var rcs = PdfColor.ResolveSpace(DefaultColorSpaces.Remap(cat[name], _resources, _doc), _doc);   // internal string indexer
            if (rcs.Family == null) return name;   // no such resource, or not a colour space
            csArray = rcs.Array;
            // ICCBased dispatches as the device space its /N names; every other family
            // dispatches as itself, so OpSc's Separation/DeviceN/Indexed/Cal*/Lab branches fire.
            return rcs.DeviceFamily;
        }

        // Named after the operator's meaning: `g`/`G` and `k`/`K` differ only by case.
        // Grey and RGB go through PdfColor for the same reason `k` does: the SVG renderer routes
        // its g/rg handlers through ComponentsToColor, so an open-coded quantisation here is a
        // raster/SVG divergence waiting to happen — and briefly was one, when the device spaces
        // were moved to rounding and these four handlers were left behind. GAPS.md §2b.
        /// <summary>
        /// §8.6.5.6: when the current resources declare a default for <paramref name="device"/>, the
        /// operator selects that space and sets the colour in it — exactly <c>cs</c> naming the
        /// default and <c>sc</c> with the same operands. False, doing nothing, when none applies.
        /// </summary>
        private bool PaintThroughDefault(string device, ContentStreamOperation op, bool stroke)
        {
            var substitute = DefaultColorSpaces.For(device, _resources, _doc);
            if (substitute == null) return false;
            var space = PdfColor.ResolveSpace(substitute, _doc);
            if (stroke)
            {
                _ctx.GS.StrokeColorSpace      = space.DeviceFamily;
                _ctx.GS.StrokeColorSpaceArray = space.Array;
                _ctx.GS.StrokeIsPattern       = false;
            }
            else
            {
                _ctx.GS.FillColorSpace      = space.DeviceFamily;
                _ctx.GS.FillColorSpaceArray = space.Array;
                _ctx.GS.FillIsPattern       = false;
            }
            OpSc(op, stroke);
            return true;
        }

        private void OpSetGrayFill(ContentStreamOperation op)
        {
            if (PaintThroughDefault(PdfColorSpaceNames.DeviceGray, op, stroke: false)) return;
            _ctx.GS.FillColor = GrayColor(GetFloat(op, 0));
            ClearSepTints(stroke: false);
            _ctx.GS.FillDeviceGray = true;
            _ctx.GS.FillCmyk = null;
            // PDF §8.6.8: g sets DeviceGray + a solid colour; this clears any active fill
            // pattern (set earlier via `/Pattern cs … scn`).  Without this, a following
            // `re f` keeps painting the leftover pattern — e.g. the magenta shading-pattern
            // banner bleeding through a QR drawn as black/white CMYK rectangles.
            _ctx.GS.FillIsPattern = false;
        }
        private void OpSetGrayStroke(ContentStreamOperation op)
        {
            if (PaintThroughDefault(PdfColorSpaceNames.DeviceGray, op, stroke: true)) return;
            _ctx.GS.StrokeColor = GrayColor(GetFloat(op, 0));
            ClearSepTints(stroke: true);
            _ctx.GS.StrokeDeviceGray = true;
            // The stroke half of the fill half's rule. `G` and `RG` select a device space that is
            // NOT DeviceCMYK, so the colorant snapshot overprint simulation reads goes with the
            // colour — kept, `1 0 0 0 K … 0 G` would leave cyan in StrokeCmyk and the overprint
            // compositor would composite a grey stroke against colorants the file had replaced.
            _ctx.GS.StrokeCmyk = null;
            _ctx.GS.StrokeIsPattern = false;
        }
        private void OpSetRgbFill(ContentStreamOperation op)
        {
            if (PaintThroughDefault(PdfColorSpaceNames.DeviceRGB, op, stroke: false)) return;
            _ctx.GS.FillColor = RgbColor(GetFloat(op, 0), GetFloat(op, 1), GetFloat(op, 2));
            ClearSepTints(stroke: false);
            _ctx.GS.FillDeviceRgb = true;
            _ctx.GS.FillCmyk = null;
            _ctx.GS.FillIsPattern = false;
        }
        private void OpSetRgbStroke(ContentStreamOperation op)
        {
            if (PaintThroughDefault(PdfColorSpaceNames.DeviceRGB, op, stroke: true)) return;
            _ctx.GS.StrokeColor = RgbColor(GetFloat(op, 0), GetFloat(op, 1), GetFloat(op, 2));
            ClearSepTints(stroke: true);
            _ctx.GS.StrokeDeviceRgb = true;
            _ctx.GS.StrokeCmyk = null;   // see OpSetGrayStroke
            _ctx.GS.StrokeIsPattern = false;
        }

        /// <summary>
        /// Forgets the <c>/Separation</c> or <c>/DeviceN</c> colorants of the colour this operator
        /// is about to replace, so no later paint deposits ink for a space the file has left.
        /// </summary>
        /// <remarks>
        /// Called from the six device-colour operators and from the top of <see cref="OpSc"/>,
        /// which between them are every way the current colour changes. The one caller that must
        /// <i>not</i> leave it cleared is <see cref="OpSc"/>'s Separation/DeviceN branch, which sets
        /// a fresh snapshot afterwards.
        /// </remarks>
        private void ClearSepTints(bool stroke)
        {
            // The channel mask goes with the tints: it is non-zero only for a colorant set, and a
            // `k` that follows a /Separation must fall back to the DeviceCMYK rule.
            if (stroke) { _ctx.GS.StrokeSepTints = null; _ctx.GS.StrokeDeviceGray = false; _ctx.GS.StrokeDeviceRgb = false; }
            else        { _ctx.GS.FillSepTints   = null; _ctx.GS.FillDeviceGray   = false; _ctx.GS.FillDeviceRgb   = false; }
        }

        /// <summary>DeviceGray → sRGB. Clamping lives in <c>PdfColor</c>, so no <c>Clamp</c> here.</summary>
        private static SKColor GrayColor(float g)
            => PdfColor.ComponentsToColor(new[] { g }, PdfColorSpaceNames.DeviceGray);

        /// <summary>DeviceRGB → sRGB.</summary>
        private static SKColor RgbColor(float r, float g, float b)
            => PdfColor.ComponentsToColor(new[] { r, g, b }, PdfColorSpaceNames.DeviceRGB);
        private void OpSetCmykFill(ContentStreamOperation op)
        {
            if (PaintThroughDefault(PdfColorSpaceNames.DeviceCMYK, op, stroke: false)) return;
            float c = Clamp(GetFloat(op,0)), m = Clamp(GetFloat(op,1)),
                  y = Clamp(GetFloat(op,2)), k = Clamp(GetFloat(op,3));
            _ctx.GS.FillColor = CmykToRgb(c, m, y, k);
            ClearSepTints(stroke: false);
            _ctx.GS.FillCmyk = new[] { c, m, y, k };
            _ctx.GS.FillIsPattern = false;
        }
        private void OpSetCmykStroke(ContentStreamOperation op)
        {
            if (PaintThroughDefault(PdfColorSpaceNames.DeviceCMYK, op, stroke: true)) return;
            float c = Clamp(GetFloat(op,0)), m = Clamp(GetFloat(op,1)),
                  y = Clamp(GetFloat(op,2)), k = Clamp(GetFloat(op,3));
            _ctx.GS.StrokeColor = CmykToRgb(c, m, y, k);
            ClearSepTints(stroke: true);
            _ctx.GS.StrokeCmyk = new[] { c, m, y, k };
            _ctx.GS.StrokeIsPattern = false;
        }

        private void OpSc(ContentStreamOperation op, bool stroke)
        {
            // The spot colorants belong to the colour being replaced, so they go before anything
            // else decides what this operator sets. Clearing here rather than in each of the ten
            // branches below is what makes "the tint snapshot travels with the colour" true by
            // construction: the Separation/DeviceN branch is the only one that re-sets it, and a
            // branch added later inherits the clear without having to remember it.
            ClearSepTints(stroke);

            // If last operand is a PdfName, it's a pattern name
            if (op.Operands.Length >= 1 && op.Operands[op.Operands.Length - 1] is PdfName patName)
            {
                // The name goes to the side this operator is for, so `SCN` cannot clobber the fill
                // pattern `scn` chose.
                if (stroke) { _ctx.GS.StrokePatternName = patName.Value; _ctx.GS.StrokeIsPattern = true; }
                else        { _ctx.GS.FillPatternName   = patName.Value; _ctx.GS.FillIsPattern   = true; }

                // §8.7.3.3: for an UNCOLOURED pattern (PaintType 2) the form is
                // `c1 … cn /Name scn`, and those leading components are the colour the
                // pattern cells are painted in, expressed in the Pattern space's
                // *underlying* colour space (`[/Pattern base]`).
                //
                // Dropped by this early return, the tile would be rendered with whatever fill colour
                // happened to be current — black by default — no matter what the content stream
                // asked for. (OpPaintPattern already passes GS.FillColor down to
                // RenderToBitmap for PaintType 2; it was simply never given the right colour.)
                if (op.Operands.Length >= 2)
                {
                    // The underlying space is element [1] of `[/Pattern base]`, and `base` may be
                    // an ARRAY — §8.7.3.1 admits any device, CIE-based or Separation/DeviceN space.
                    // Reading it as a bare name (which is all this did) silently discarded every
                    // array form, and the operand-count fallback below then guessed DeviceRGB, so
                    // `[/Pattern [/Lab …]] 50 60 -60 /P1 scn` painted the L*a*b* triple as raw RGB.
                    var underlying = stroke ? _ctx.GS.StrokeColorSpaceArray : _ctx.GS.FillColorSpaceArray;
                    var underlyingSpace = underlying != null && underlying.Count > 1
                        ? PdfColor.ResolveSpace(underlying[1], _doc)
                        : default;
                    string baseCs = underlyingSpace.Family != null ? underlyingSpace.DeviceFamily : null;

                    var comps = new float[op.Operands.Length - 1];
                    for (int i = 0; i < comps.Length; i++) comps[i] = GetFloat(op, i);

                    // With no declared base space, infer it from the component count — the
                    // usual DeviceGray/RGB/CMYK arity. Better than defaulting to black.
                    baseCs ??= comps.Length switch
                    {
                        1 => PdfColorSpaceNames.DeviceGray,
                        4 => PdfColorSpaceNames.DeviceCMYK,
                        _ => PdfColorSpaceNames.DeviceRGB,
                    };

                    // Through the underlying space itself — a tint transform, an ICC profile, CIE
                    // parameters — as a solid colour in it converts; the family name alone painted a
                    // spot-coloured pattern white.
                    SKColor patternColor = underlyingSpace.Family != null
                        ? PatternUnderlyingColor.ToColor(_doc, underlying[1], comps, CurrentIntent)
                        : PdfColor.ComponentsToColor(comps, baseCs);
                    // The components go with the colour, as every other branch sends them: a
                    // DeviceCMYK base keeps them (a separation plate and the overprint snapshot read
                    // them), anything else drops the previous colour's.
                    float[] patternCmyk = baseCs == PdfColorSpaceNames.DeviceCMYK && comps.Length >= 4
                        ? new[] { Clamp(comps[0]), Clamp(comps[1]), Clamp(comps[2]), Clamp(comps[3]) }
                        : null;
                    if (stroke) { _ctx.GS.StrokeColor = patternColor; _ctx.GS.StrokeCmyk = patternCmyk; }
                    else        { _ctx.GS.FillColor   = patternColor; _ctx.GS.FillCmyk   = patternCmyk; }
                    if ((underlyingSpace.Family == PdfColorSpaceNames.Separation || underlyingSpace.Family == PdfColorSpaceNames.DeviceN)
                        && underlyingSpace.Array != null)
                        SetColorantSnapshot(underlyingSpace.Array, comps, stroke);
                }
                return;
            }

            // No pattern name given → a solid colour is being set in a non-Pattern
            // colour space.  Clear any active pattern so a previously-selected pattern
            // does not bleed into the next path fill (PDF §8.6.8).
            if (stroke) _ctx.GS.StrokeIsPattern = false;
            else        _ctx.GS.FillIsPattern   = false;

            // Separation / DeviceN — evaluate tint transform
            string cs       = stroke ? _ctx.GS.StrokeColorSpace      : _ctx.GS.FillColorSpace;
            var    csArr    = stroke ? _ctx.GS.StrokeColorSpaceArray  : _ctx.GS.FillColorSpaceArray;

            if ((cs == PdfColorSpaceNames.Separation || cs == PdfColorSpaceNames.DeviceN) && csArr != null)
            {
                SKColor col = ResolveSeparationColor(csArr, op);
                if (stroke) { _ctx.GS.StrokeColor = col; _ctx.GS.StrokeCmyk = null; }
                else        { _ctx.GS.FillColor   = col; _ctx.GS.FillCmyk   = null; }

                var tints = new float[op.Operands.Length];
                for (int i = 0; i < tints.Length; i++) tints[i] = GetFloat(op, i);
                SetColorantSnapshot(csArr, tints, stroke);
                return;
            }

            // Indexed: map integer index to base-space colour via palette lookup.
            // §8.6.6.3: the operand "shall be rounded to the NEAREST integer" and then clamped to
            // [0, hival]. This rounded toward +infinity, so `sc 2.4` picked palette entry 3 — the
            // wrong colour for every non-integral index, and a file that writes 2.9999999 for
            // entry 3 (float round-trip) is the case the spec sentence exists for.
            // Midpoints go away from zero, matching the PostScript `round`.
            if (cs == PdfColorSpaceNames.Indexed && csArr != null && csArr.Count >= 4)
            {
                // One reader, in IndexedPalette.TryReadSpace, which all four /Indexed sites call. It
                // clamps /HiVal, so the index is clamped against the palette's real length: against the
                // file's raw value it would fall past the end of the palette to black.
                IndexedPalette.TryReadSpace(_doc, csArr, out var idxBase, out int hival, out byte[] table);

                float indexF = op.Operands.Length > 0 ? GetFloat(op, 0) : 0f;
                int index    = Math.Max(0, Math.Min(hival,
                    SaturatingCast.ToInt(Math.Round((double)indexF, MidpointRounding.AwayFromZero))));

                if (table != null)
                {
                    // Delegate to BuildIndexedPalette — the ONE place that understands every base
                    // form: array-form [/ICCBased …] / [/Lab …] / [/CalRGB …], and Separation or
                    // DeviceN, whose palette entries are TINTS that must go through the tint
                    // transform rather than being read as colour components.
                    //
                    // The palette is not decoded here from a bare-name base, which would make every
                    // array form 3-component DeviceRGB; and naming the base is not enough: a
                    // Separation entry read as a grey byte comes out INVERTED (tint 1 is full ink,
                    // grey 1 is white), and a DeviceN entry has n components, not 3. Two code paths
                    // reading one palette drift; there is one.
                    var palette = BuildIndexedPalette(idxBase, table, hival);
                    SKColor indexed = index < palette.Length ? palette[index] : SKColors.Black;
                    if (stroke) { _ctx.GS.StrokeColor = indexed; _ctx.GS.StrokeCmyk = null; }
                    else        { _ctx.GS.FillColor   = indexed; _ctx.GS.FillCmyk   = null; }
                    return;
                }
            }

            // CalRGB: apply /Gamma and /Matrix, then XYZ → sRGB (PDF §10.6.4.3).
            if (cs == PdfColorSpaceNames.CalRGB && csArr != null)
            {
                float[] calVals = new float[op.Operands.Length];
                for (int i = 0; i < op.Operands.Length; i++) calVals[i] = GetFloat(op, i);
                SKColor calCol = PdfColor.CalRgbToSRgb(calVals, ColorSpaceParams(csArr), _doc);
                if (stroke) { _ctx.GS.StrokeColor = calCol; _ctx.GS.StrokeCmyk = null; }
                else        { _ctx.GS.FillColor   = calCol; _ctx.GS.FillCmyk   = null; }
                return;
            }

            // CalGray: apply /Gamma (§8.6.5.2). Without this branch a CalGray fill would fall through
            // to ComponentsToColor, where CalGray is ALIASED to DeviceGray — /Gamma silently dropped
            // for every fill and stroke. Same helper as the image path, so the two cannot disagree
            // about one file's grey.
            if (cs == PdfColorSpaceNames.CalGray && csArr != null)
            {
                SKColor grayCol = PdfColor.CalGrayToSRgb(
                    op.Operands.Length > 0 ? GetFloat(op, 0) : 0f, ColorSpaceParams(csArr), _doc);
                if (stroke) { _ctx.GS.StrokeColor = grayCol; _ctx.GS.StrokeCmyk = null; }
                else        { _ctx.GS.FillColor   = grayCol; _ctx.GS.FillCmyk   = null; }
                return;
            }

            // Lab: clamp a*/b* to the space's /Range and convert against its /WhitePoint
            // (§8.6.5.4). The generic path below assumes a D50 white point and no /Range at all.
            if (cs == PdfColorSpaceNames.Lab && csArr != null)
            {
                SKColor labCol = PdfColor.LabToSRgb(
                    op.Operands.Length > 0 ? GetFloat(op, 0) : 0f,
                    op.Operands.Length > 1 ? GetFloat(op, 1) : 0f,
                    op.Operands.Length > 2 ? GetFloat(op, 2) : 0f,
                    ColorSpaceParams(csArr), _doc);
                if (stroke) { _ctx.GS.StrokeColor = labCol; _ctx.GS.StrokeCmyk = null; }
                else        { _ctx.GS.FillColor   = labCol; _ctx.GS.FillCmyk   = null; }
                return;
            }

            // ICCBased: ask the profile itself, instead of the device space its /N stands for.
            // ResolveColorSpaceName collapses the family to Device{Gray,RGB,CMYK} deliberately —
            // that IS the fallback — so the ARRAY is the only thing still saying ICCBased, and the
            // only carrier of the profile stream. Declines (leaving the fallback byte-identical)
            // for every profile the managed matrix/TRC + grey-TRC evaluator does not cover, and
            // says why through IccTransform.LastUnsupportedProfile.
            if (csArr != null)
            {
                float[] iccVals = new float[op.Operands.Length];
                for (int i = 0; i < op.Operands.Length; i++) iccVals[i] = GetFloat(op, i);
                if (IccColorTransform.TryResolveIccBasedColor(_doc, csArr, iccVals, CurrentIntent,
                                                             out SKColor iccCol))
                {
                    if (stroke) { _ctx.GS.StrokeColor = iccCol; _ctx.GS.StrokeCmyk = null; }
                    else        { _ctx.GS.FillColor   = iccCol; _ctx.GS.FillCmyk   = null; }
                    return;
                }
            }

            // Generic sc/SC — use current color space for correct conversion.
            string activCs = stroke ? _ctx.GS.StrokeColorSpace : _ctx.GS.FillColorSpace;
            float[] vals = new float[op.Operands.Length];
            for (int i = 0; i < op.Operands.Length; i++) vals[i] = GetFloat(op, i);
            SKColor scCol = ComponentsToColor(vals, activCs ?? PdfColorSpaceNames.DeviceRGB);
            if (stroke) { _ctx.GS.StrokeColor = scCol; _ctx.GS.StrokeCmyk = null; }
            else        { _ctx.GS.FillColor   = scCol; _ctx.GS.FillCmyk   = null; }
            if (activCs == PdfColorSpaceNames.DeviceGray && csArr == null)
            {
                if (stroke) _ctx.GS.StrokeDeviceGray = true;
                else        _ctx.GS.FillDeviceGray   = true;
            }
            if (activCs == PdfColorSpaceNames.DeviceRGB && csArr == null)
            {
                if (stroke) _ctx.GS.StrokeDeviceRgb = true;
                else        _ctx.GS.FillDeviceRgb   = true;
            }
        }

        /// <summary>
        /// Records the colorants a <c>/Separation</c> or <c>/DeviceN</c> colour names, with their
        /// tints — for a solid colour, and for an uncoloured pattern whose underlying space is one
        /// (§8.7.3.3), whose cells paint in it.
        /// </summary>
        /// <param name="csArr">The colour space array: <c>[/Separation name …]</c> or <c>[/DeviceN names …]</c>.</param>
        /// <param name="tints">The components, one per name; a missing one is 1.</param>
        /// <param name="stroke">Which side's colour.</param>
        /// <remarks>
        /// <para>The colorant snapshot the ink accumulator deposits from and a separation plate reads,
        /// taken here because this is the last moment the named inks and their tints still exist:
        /// the tint transform has already collapsed them into a single sRGB value, and
        /// <c>SPOT 185 C</c> at 40% and a flat pink are indistinguishable from then on. The
        /// pattern branch did not take it, so a spot-coloured uncoloured pattern reached a separation
        /// as no ink at all.</para>
        ///
        /// <para>Registering the channel is a separate statement from recording the tint, and the
        /// two answer different questions — "does this page use this ink at all" versus "how much of
        /// it lands where". A channel with an all-zero map is a real answer (the ink was selected and
        /// then nothing was painted with it), which is why the registration is not folded into the
        /// deposit.</para>
        /// </remarks>
        private void SetColorantSnapshot(PdfArray csArr, float[] tints, bool stroke)
        {
            // One reading of the names and their tints, shared with a shading's samples on a plate.
            var sepTints = SeparationPlate.ColorantTints(csArr, tints);
            foreach (string name in SeparationPlate.ColorantNames(csArr))
                _ctx.InkAccumulator?.GetOrCreateChannel(name);

            // Replaced wholesale, never mutated — the contract GraphicsState's shallow clone
            // depends on. Mutating a dictionary the q-saved frame also references is exactly
            // how a tint would leak back across a Q.
            if (stroke) _ctx.GS.StrokeSepTints = sepTints;
            else        _ctx.GS.FillSepTints   = sepTints;
        }

        /// <summary>
        /// The parameter dictionary of a CIE-based colour space — element [1] of
        /// <c>[/CalGray dict]</c>, <c>[/CalRGB dict]</c> or <c>[/Lab dict]</c>, resolved through an
        /// indirect reference.
        /// </summary>
        private PdfDictionary ColorSpaceParams(PdfArray csArr)
            => _doc?.ResolveObject(csArr != null && csArr.Count > 1 ? csArr[1] : null) as PdfDictionary
               ?? (csArr != null && csArr.Count > 1 ? csArr[1] as PdfDictionary : null);

        // Evaluate Separation/DeviceN tint transform and map to base color space
        private SKColor ResolveSeparationColor(PdfArray csArr, ContentStreamOperation op)
        {
            // csArr = [/Separation name base tintTransform] or [/DeviceN names base tintTransform]
            if (csArr.Count < 4) return SKColors.Black;
            csArr = NamedSpotColors.Substitute(_doc, csArr);   // a spot the named-colour profile names

            // §8.6.6.4: the reserved colorant name /All means the tint applies to EVERY colorant
            // the device has at once — the registration-mark case. It is not one ink whose
            // appearance the tint transform describes, so the transform is not what defines the
            // colour: full coverage of everything is what has to come out. This renderer's device
            // model for "all the inks" is the four process colorants, so the tint is applied to
            // each of them; at tint 1 that is black, at tint 0 nothing, which is the whole point.
            // Through the tint transform /All would paint whatever it happens to say — for the
            // common registration-mark space, a *lighter* colour than full coverage.
            // Resolved. §7.3.10 lets the colorant name be indirect, and a non-resolving read of it
            // answers "absent" — indistinguishable from a name that is not /All, so the space would
            // fall through to its tint transform: precisely the defect this branch exists to fix.
            // The SVG exporter resolves here too, so both agree. See
            // SeparationAll_NamedIndirectly_AgreesBetweenRenderers.
            if (PdfResolve.Name(_doc, csArr[1])?.Value == PdfColorSpaceNames.AllColorant
                && (csArr[0] as PdfName)?.Value == PdfColorSpaceNames.Separation)
            {
                float allTint = op.Operands.Length > 0 ? Clamp(GetFloat(op, 0)) : 1f;
                return PdfColor.ComponentsToColor(
                    new[] { allTint, allTint, allTint, allTint }, PdfColorSpaceNames.DeviceCMYK);
            }

            // Table 71: a DeviceN may carry a 5th element, an attributes dictionary. With
            // /Subtype /NChannel it names each colorant's process/spot identity and carries mixing
            // hints (dot gain, solidity, printing order) that describe how the inks combine — which
            // this renderer does not model, since it composites the tint transform's output in sRGB.
            // The colour below is therefore the tint transform's, exactly as for a plain DeviceN: the
            // appearance of the colorants in combination (§8.6.6.5). /Colorants, each ink's appearance
            // alone, is read by the preview and the PSD (OverprintPreview.Ink). Reported.
            if (!_nChannelReported && csArr.Count > 4
                && _doc?.ResolveObject(csArr[4]) is PdfDictionary nchanAttrs
                && (PdfResolve.Name(_doc, nchanAttrs, PdfName.Subtype))?.Value == PdfColorSpaceNames.NChannel)
            {
                _nChannelReported = true;
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    "DeviceN /Subtype /NChannel attributes (/Process, /MixingHints) present; not modelled — " +
                    "the colour comes from the tint transform, the colorants in combination.");
            }

            // The alternate space is commonly an ARRAY — [/ICCBased …], [/Lab …], [/CalRGB …] —
            // and reading it as a bare name silently made every one of those DeviceRGB, so a
            // Separation whose alternate is Lab was converted as if its three components were RGB.
            // ResolveAlternateCsName (used by the image path in this same file) handles every form.
            string baseCs = ResolveAlternateCsName(csArr[2]) ?? PdfColorSpaceNames.DeviceRGB;
            var fnObj = _doc.ResolveObject(csArr[3]);
            if (fnObj is not PdfDictionary fnDict) return SKColors.Black;

            // Gather tint values
            float[] tints = new float[op.Operands.Length];
            for (int i = 0; i < op.Operands.Length; i++)
                tints[i] = Clamp(GetFloat(op, i));

            float[] baseValues = PdfFunctionEvaluator.Evaluate(fnDict, tints, _doc.ResolveObject);

            // An [/ICCBased …] alternate: the tint transform's output lives in THAT space, so ask
            // the profile before falling back to the device space baseCs names. ResolveAlternateCsName
            // above deliberately collapses the family — that collapse is the fallback, not the answer.
            if (baseValues != null && baseValues.Length > 0)
                return IccColorTransform.ResolveAlternateColor(_doc, csArr[2], baseValues, baseCs,
                                                               CurrentIntent);

            // Build a PdfArray from the base values so ArrayToColor can use it
            var baseArr = new PdfArray();
            foreach (float v in baseValues)
                baseArr.Add(new PdfNumber(v));

            return ArrayToColor(baseArr, baseCs, SKColors.Black);
        }

        private static SKColor CmykToRgb(float c, float m, float y, float k)
            => PdfColor.CmykToRgb(c, m, y, k);

        // ---- Text ----
    }
}
