// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using HashSetInt = System.Collections.Generic.HashSet<int>;
using SKPath = SkiaSharp.SKPath;
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Interprets a PDF content stream into SVG elements — the vector counterpart of
    /// <see cref="ContentStreamInterpreter"/>, which does the same job onto a Skia canvas.
    ///
    /// <para><b>A type of its own, split as the raster interpreter is</b> — .Path, .Text,
    /// .Shading, .Images and the rest — so a change to one renderer and not the other is a
    /// two-file diff. A silently wrong output is, as a rule, one renderer lagging the other.</para>
    ///
    /// <para>It stays <c>internal</c>. Base's three <c>InternalsVisibleTo</c> friends are all
    /// in-repo — the test project, FontDiag and the benchmarks — so no CONSUMER could see it
    /// before and none can now; promoting it changed its nesting, not its visibility.</para>
    /// </summary>
internal sealed partial class SvgContentWriter
    {
        /// <summary>
        /// The fill colour <b>as it must be painted</b> — the graphics state's colour with the
        /// active transfer function (<c>/TR</c>, <c>/TR2</c>, §10.4) applied.
        ///
        /// <para>Every emission site reads this rather than <c>_gs.FillColor</c>, and the two are
        /// separate for the reason §10.4 requires: a <c>gs</c> that installs a transfer function
        /// <i>after</i> the colour was set still changes what is painted, so the remap cannot
        /// happen where the colour is stored. That is also why the raster path applies it in
        /// <c>PdfRenderContext.ApplyAlpha</c> at paint time — and why this writer, which had no
        /// equivalent, exported a red rectangle where the renderer drew a cyan one.</para>
        ///
        /// <para>A pattern or gradient value (<c>url(#…)</c>) passes through untouched: §10.4
        /// transfers colour components, and a shading's stops are transferred where they are
        /// built.</para>
        /// </summary>
        private string FillPaint =>
            TransferFunctionLut.ApplyToCssColor(_gs.TransferLut, _gs.FillColor);

        /// <summary>The stroke counterpart of <see cref="FillPaint"/>.</summary>
        private string StrokePaint =>
            TransferFunctionLut.ApplyToCssColor(_gs.TransferLut, _gs.StrokeColor);

        private static string GrayColor(float g)
            => PdfColor.ToCssRgb(PdfColor.ComponentsToColor(new[] { g }, PdfColorSpaceNames.DeviceGray));

        private static string RgbColor(float r, float g, float b)
            => PdfColor.ToCssRgb(PdfColor.ComponentsToColor(new[] { r, g, b }, PdfColorSpaceNames.DeviceRGB));

        private static string CmykColor(float c, float m, float y, float k)
            => PdfColor.ToCssRgb(PdfColor.ComponentsToColor(new[] { c, m, y, k }, PdfColorSpaceNames.DeviceCMYK));

        private void OpSc(ContentStreamOperation op, bool stroke)
        {
            string cs = stroke ? _gs.ColorSpaceStroke : _gs.ColorSpaceFill;
            PdfArray csArr = stroke ? _gs.ColorSpaceArrayStroke : _gs.ColorSpaceArrayFill;
            string color;

            // For scn/SCN the last operand may be a pattern name — record it and skip.
            int numCount = op.Operands.Length;
            bool namedAPattern = false;
            while (numCount > 0 && op.Operands[numCount - 1] is PdfName patName)
            {
                if (stroke) { _gs.PatternNameStroke = patName.Value; _gs.StrokeIsPattern = true; }
                else        { _gs.PatternNameFill   = patName.Value; _gs.FillIsPattern   = true; }
                namedAPattern = true;
                numCount--;
            }

            // No name operand → a SOLID colour is being set, so any active pattern stops governing
            // this paint side (§8.6.8). The raster twin does exactly this
            // (ContentStreamInterpreter.Color.cs, the two lines above its Separation branch) and
            // this writer did not, so `/Pattern cs /P1 scn 1 0 0 sc` left the Pattern flag set —
            // and with the paint gates now asking for the flag, the stale pattern would still have
            // exported. Placed before the operand-count return so a zero-operand `sc` clears too,
            // as it does on the raster side.
            if (!namedAPattern)
            {
                if (stroke) _gs.StrokeIsPattern = false;
                else        _gs.FillIsPattern   = false;
            }
            if (numCount == 0) return;

            // Snapshot operands as floats once.
            float[] comps = new float[numCount];
            for (int i = 0; i < numCount; i++) comps[i] = Flt(op, i);

            // A /Separation or /DeviceN of PROCESS colorants is a CMYK source for overprint
            // (§11.7.4.5), through the same mapping the raster path uses, so the two cannot
            // disagree about which names are process inks. Decided in the colorant-set case and
            // written into the state at the tail, beside the colour itself.
            bool colorantSetIsCmyk   = false;
            int  colorantSetChannels = OverprintCompositor.DeviceCmykRule;
            bool colorantSetIsSpot   = false;

            switch (cs)
            {
                case PdfColorSpaceNames.DeviceGray:
                    color = GrayColor(comps[0]);
                    break;

                case PdfColorSpaceNames.CalGray:
                    // /Gamma, through the same helper the raster path and the image path use, never
                    // aliased to DeviceGray, which ignores it.
                    color = PdfColor.ToCssRgb(PdfColor.CalGrayToSRgb(
                        comps[0],
                        csArr != null && csArr.Count >= 2
                            ? _doc.ResolveObject(csArr[1]) as PdfDictionary
                            : null, _doc));
                    break;

                case PdfColorSpaceNames.DeviceCMYK:
                    color = numCount >= 4 ? CmykColor(comps[0], comps[1], comps[2], comps[3])
                                          : (stroke ? _gs.StrokeColor : _gs.FillColor);
                    break;

                case PdfColorSpaceNames.Lab:
                {
                    // The space's own /WhitePoint and /Range, not the D50 default and the
                    // spec-default component ranges — same helper as the raster path.
                    float L = comps.Length > 0 ? comps[0] : 0f;
                    float a = comps.Length > 1 ? comps[1] : 0f;
                    float b = comps.Length > 2 ? comps[2] : 0f;
                    color = PdfColor.ToCssRgb(PdfColor.LabToSRgb(
                        L, a, b,
                        csArr != null && csArr.Count >= 2
                            ? _doc.ResolveObject(csArr[1]) as PdfDictionary
                            : null, _doc));
                    break;
                }

                case PdfColorSpaceNames.CalRGB:
                {
                    // Apply the precise CalRGB gamma+matrix transform via the shared
                    // helper (the raster path uses the same code). [/CalRGB <<params>>]
                    var calDict = csArr != null && csArr.Count >= 2
                        ? _doc.ResolveObject(csArr[1]) as PdfDictionary
                        : null;
                    color = PdfColor.ToCssRgb(PdfColor.CalRgbToSRgb(comps, calDict, _doc));
                    break;
                }

                case PdfColorSpaceNames.ICCBased:
                {
                    // Ask the profile itself before falling back to the device space its /N
                    // merely stands for. The raster path routes through this same
                    // IccColorTransform from ContentStreamInterpreter.OpSc — consulting it in
                    // only one renderer is the one-renderer-only asymmetry this codebase keeps
                    // re-introducing (JPEG 2000, /I isolation, PostScript XObjects), so both
                    // call the one entry point, decline on exactly the same profiles, and say
                    // why through IccTransform.LastUnsupportedProfile.
                    if (IccColorTransform.TryResolveIccBasedColor(_doc, csArr, comps, CurrentIntent,
                                                                 out SKColor iccCol))
                    {
                        color = PdfColor.ToCssRgb(iccCol);
                        break;
                    }

                    // Declined — dispatch by /N from the profile stream, which is the documented
                    // fallback: 1 = Gray, 3 = RGB, 4 = CMYK.
                    int n = GetIccComponentCount(csArr);
                    color = n == PdfColorSpaceNames.GrayComponents ? GrayColor(comps[0])
                          : n == PdfColorSpaceNames.CmykComponents ? (numCount >= 4 ? CmykColor(comps[0], comps[1], comps[2], comps[3]) : GrayColor(comps[0]))
                          :          (numCount >= 3 ? RgbColor(comps[0], comps[1], comps[2]) : GrayColor(comps[0]));
                    break;
                }

                case PdfColorSpaceNames.Indexed:
                    // §8.6.6.3 rounds the index to NEAREST. A C# cast truncates, so a float
                    // round-trip of index 3 (2.9999999) would select palette entry 2, as in the raster
                    // path.
                    color = ResolveIndexedColor(csArr,
                                SaturatingCast.ToInt(Math.Round(comps[0], MidpointRounding.AwayFromZero)))
                            ?? (stroke ? _gs.StrokeColor : _gs.FillColor);
                    break;

                case PdfColorSpaceNames.Separation:
                case PdfColorSpaceNames.DeviceN:
                // /NChannel is an Adobe extension with no PdfColorSpaceNames constant.
                case "NChannel":
                    color = EvaluateTintTransform(csArr, comps)
                            ?? (stroke ? _gs.StrokeColor : _gs.FillColor);
                    // The names are what overprint marks. Process names make this a CMYK source
                    // whose written channels are the names; anything else is a spot ink, which the
                    // multiply approximation must not pretend to composite.
                    if (OverprintCompositor.TryProcessColorants(ColorantTints(csArr, comps),
                                                                out _, out colorantSetChannels))
                        colorantSetIsCmyk = true;
                    else
                        colorantSetIsSpot = true;
                    break;

                // An uncoloured pattern's components, in the Pattern space's underlying space
                // (§8.7.3.3) — converted through it, as the raster twin does, not guessed by count:
                // a spot base came out grey here.
                case PdfColorSpaceNames.Pattern when csArr != null && csArr.Count > 1:
                    color = PdfColor.ToCssRgb(PatternUnderlyingColor.ToColor(_doc, csArr[1], comps, CurrentIntent));
                    break;

                default: // DeviceRGB, unknown — best-effort by component count.
                    if (numCount == 1)       color = GrayColor(comps[0]);
                    else if (numCount == 3)  color = RgbColor(comps[0], comps[1], comps[2]);
                    else if (numCount == 4)  color = CmykColor(comps[0], comps[1], comps[2], comps[3]);
                    else return;
                    break;
            }
            // Clearing the DeviceCMYK flag is NOT optional, and leaving it out was a live defect.
            // `k`/`K` and a colorant set of process inks establish a colorant snapshot; every other
            // colour-setting operator discards it, which is exactly what the raster twin does — it
            // clears FillCmyk/StrokeCmyk at the top of its sc/scn handler and re-sets them only in
            // the DeviceCMYK and process-colorant branches.
            //
            // Without this, `0 0 0 1 k` followed by `/CS0 cs 1 0 0 sc` left FillIsCmyk true, so
            // with SimulateOverprint on and /OP true /OPM 1 the SVG path emitted
            // `mix-blend-mode:multiply` over a fill whose source colour is not CMYK at all — while
            // the raster path declined with NonCmykSource and painted normally. Same bytes, two
            // answers, and no diagnostic telling them apart.
            if (stroke)
            {
                _gs.StrokeColor = color;
                _gs.StrokeIsCmyk = colorantSetIsCmyk;
                _gs.StrokeOverprintChannels = colorantSetChannels;
                _gs.StrokeIsSpotColorant = colorantSetIsSpot;
            }
            else
            {
                _gs.FillColor = color;
                _gs.FillIsCmyk = colorantSetIsCmyk;
                _gs.FillOverprintChannels = colorantSetChannels;
                _gs.FillIsSpotColorant = colorantSetIsSpot;
            }
        }

        /// <summary>
        /// Forgets the colorant set a paint side was carrying — the channel mask and the spot
        /// flag — when a device colour operator replaces the colour. The raster twin does this in
        /// <c>ContentStreamInterpreter.ClearSepTints</c>.
        /// </summary>
        private void EndColorantSet(bool stroke)
        {
            if (stroke) { _gs.StrokeOverprintChannels = OverprintCompositor.DeviceCmykRule; _gs.StrokeIsSpotColorant = false; }
            else        { _gs.FillOverprintChannels   = OverprintCompositor.DeviceCmykRule; _gs.FillIsSpotColorant   = false; }
        }

        /// <summary>
        /// The colorants a <c>/Separation</c> or <c>/DeviceN</c> array names, paired with the
        /// operands the colour operator supplied for them — the raster path's <c>FillSepTints</c>
        /// snapshot, built the same way so <see cref="OverprintCompositor.TryProcessColorants"/>
        /// answers the same question on both sides.
        /// </summary>
        private System.Collections.Generic.Dictionary<string, float> ColorantTints(PdfArray csArr, float[] comps)
        {
            var tints = new System.Collections.Generic.Dictionary<string, float>(StringComparer.Ordinal);
            if (csArr == null || csArr.Count < 2) return tints;

            PdfObject names = _doc?.ResolveObject(csArr[1]) ?? csArr[1];
            if (names is PdfName single)
            {
                tints[single.Value] = comps != null && comps.Length > 0 ? Clamp(comps[0]) : 1f;
            }
            else if (names is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                    if ((_doc?.ResolveObject(arr[i]) ?? arr[i]) is PdfName nm)
                        tints[nm.Value] = comps != null && comps.Length > i ? Clamp(comps[i]) : 1f;
            }
            return tints;
        }

        // Reads /N from an /ICCBased profile stream through the one read the raster side uses.
        // Defaults to 3 (RGB) when the stream is missing or /N is absent.
        private int GetIccComponentCount(PdfArray csArr)
            => csArr == null || csArr.Count < 2
                ? PdfColorSpaceNames.RgbComponents
                : PdfColor.IccComponentCount(csArr[1], _doc);

        // [/Indexed base hival lookup] — look up the palette entry and convert
        // the base-space components back through the renderer's own converters.
        private string ResolveIndexedColor(PdfArray csArr, int index)
        {
            // One reader, in IndexedPalette.TryReadSpace — the /HiVal clamp this site already had
            // right, and the resolve, now shared with the three other /Indexed sites.
            if (csArr == null || csArr.Count < 4) return null;
            if (!IndexedPalette.TryReadSpace(_doc, csArr, out var baseObj, out int hival, out byte[] table))
                return null;
            if (table == null) return null;
            index = Math.Max(0, Math.Min(index, hival));

            // Delegate to the palette builder rather than decoding the table again here: a decode
            // of its own from a BARE-NAME base would make array-form bases ([/ICCBased …], [/Lab …],
            // [/CalRGB …]) 3-component DeviceRGB, drop CalGray's /Gamma, and keep a Separation/DeviceN
            // base from its tint transform. Four implementations of
            // one palette existed across the two renderers; there are now two, one per renderer,
            // each with a single entry point.
            var palette = BuildSvgIndexedPalette(baseObj, table, hival);

            return index < palette.Length ? PdfColor.ToCssRgb(palette[index]) : null;
        }

        // Evaluates the tint transform function of a /Separation or /DeviceN space
        // and converts through the alternate color space.
        // Array shape: [/Separation name alternateSpace tintTransform]
        //              [/DeviceN   names alternateSpace tintTransform attrs?]
        private string EvaluateTintTransform(PdfArray csArr, float[] tintValues)
        {
            if (csArr == null || csArr.Count < 4) return null;

            // §8.6.6.4: the reserved colorant name /All means the tint applies to EVERY
            // colorant the device has at once — the registration-mark case — so the tint
            // transform is not what defines the colour; full coverage of everything is. The
            // raster path applies the tint to the four process colorants
            // (ResolveSeparationColor); this branch did not exist here at all, so /All exported
            // whatever the transform happened to say — usually lighter than full coverage.
            if (PdfResolve.Name(_doc, csArr[1])?.Value == PdfColorSpaceNames.AllColorant
                && (csArr[0] as PdfName)?.Value == PdfColorSpaceNames.Separation)
            {
                float allTint = tintValues != null && tintValues.Length > 0
                    ? Clamp(tintValues[0]) : 1f;
                return PdfColor.ToCssRgb(PdfColor.ComponentsToColor(
                    new[] { allTint, allTint, allTint, allTint }, PdfColorSpaceNames.DeviceCMYK));
            }

            csArr = NamedSpotColors.Substitute(_doc, csArr);   // a spot the named-colour profile names
            if (_doc.ResolveObject(csArr[3]) is not PdfDictionary fnDict) return null;

            // Clamped, as the raster path clamps its operands before evaluating.
            var tints = new float[tintValues?.Length ?? 0];
            for (int i = 0; i < tints.Length; i++) tints[i] = Clamp(tintValues[i]);

            float[] converted;
            try
            {
                // WITH a resolver. Passing none — the previous call — meant a Type 3 stitching
                // function whose /Functions entries are indirect (the normal way to write one)
                // could not reach its sub-functions, so the evaluation yielded nothing and the
                // caller silently kept the PREVIOUS colour. The raster path has always passed
                // `_doc.ResolveObject` here.
                converted = PdfFunctionEvaluator.Evaluate(fnDict, tints, _doc.ResolveObject);
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.MeshShaderFailed,
                    "SVG Separation/DeviceN tint-transform evaluation failed", ex);
                return null;
            }
            if (converted == null || converted.Length == 0) return null;

            // The alternate space through the SINGLE resolver. Taking the array head's name —
            // what this did — collapsed `[/ICCBased s]` to the family "ICCBased", which matched
            // no case and fell to the 3-component default: an /N 4 profile was read as an RGB
            // triple with the K channel discarded, and an /N 1 profile as RGB with two garbage
            // components. PdfColor.ResolveSpace maps ICCBased onto the device space its /N
            // names, exactly as ContentStreamInterpreter.ResolveAlternateCsName does.
            string altCs = PdfColor.ResolveSpace(csArr[2], _doc).DeviceFamily;
            // ...and when the alternate carries an ICC profile, ask it rather than settling for
            // the device family it maps onto. Same entry point as the raster path's
            // ResolveSeparationColor, so the two cannot disagree about one file's spot colour.
            return PdfColor.ToCssRgb(
                IccColorTransform.ResolveAlternateColor(_doc, csArr[2], converted, altCs,
                                                        CurrentIntent));
        }

        // ---- ExtGState ----

        /// <summary>
        /// §8.6.5.6, the raster twin's <c>PaintThroughDefault</c>: under a declared default, a device
        /// operator selects that space and sets the colour in it — <c>cs</c> naming the default and
        /// <c>sc</c> with the same operands. False, doing nothing, when none applies.
        /// </summary>
        private bool PaintThroughDefault(string device, ContentStreamOperation op, bool stroke)
        {
            var substitute = DefaultColorSpaces.For(device, _resources, _doc);
            if (substitute == null) return false;
            var array = substitute as PdfArray;
            string family = array != null && array.Count > 0 && array[0] is PdfName first
                ? PdfColorSpaceNames.Expand(first.Value)
                : PdfColorSpaceNames.Expand((substitute as PdfName)?.Value ?? device);
            if (stroke)
            {
                _gs.ColorSpaceStroke      = family;
                _gs.ColorSpaceArrayStroke = array;
                _gs.StrokeIsPattern       = false;
            }
            else
            {
                _gs.ColorSpaceFill      = family;
                _gs.ColorSpaceArrayFill = array;
                _gs.FillIsPattern       = false;
            }
            OpSc(op, stroke);
            return true;
        }

        private void OpSetCs(ContentStreamOperation op, bool stroke)
        {
            if (op.Operands.Length < 1) return;
            string name = (op.Operands[0] as PdfName)?.Value;
            if (name == null) return;

            // Look up the colour-space definition in /ColorSpace resources when the
            // name refers to a named entry (/Indexed, /Separation, /ICCBased, …).
            // Simple device names (DeviceGray/RGB/CMYK) have no entry.
            // §8.6.3: the four device family names and /Pattern are RESERVED, so a /ColorSpace
            // resource cannot shadow them and they are not looked up. The raster path has always
            // short-circuited here; this one looked the resource up first, so a page binding
            // /DeviceGray to [/Separation /Spot /DeviceCMYK <fn>] and drawing `/DeviceGray cs`
            // filled with the SPOT COLOUR AT FULL TINT in the export and BLACK in the render.
            // Such a file is malformed -- but the two renderers disagreeing about it is not a
            // property of the file, and the spec says which answer is right.
            PdfArray csArray = null;
            bool reserved =
                name == PdfColorSpaceNames.DeviceGray ||
                name == PdfColorSpaceNames.DeviceRGB ||
                name == PdfColorSpaceNames.DeviceCMYK ||
                name == PdfColorSpaceNames.Pattern;
            var csRes = reserved ? null : _doc.ResolveObject(_resources?[PdfName.ColorSpace]) as PdfDictionary;
            // §8.6.5.6: a device space selected is the default the resources declare for it — the
            // raster twin's rule (DefaultColorSpaces), one implementation.
            if (reserved && name != PdfColorSpaceNames.Pattern
                && DefaultColorSpaces.For(name, _resources, _doc) is PdfObject substitute)
            {
                if (substitute is PdfArray defaultArray && defaultArray.Count > 0 && defaultArray[0] is PdfName defaultFamily)
                {
                    csArray = defaultArray;
                    name    = PdfColorSpaceNames.Expand(defaultFamily.Value);
                }
                else if (substitute is PdfName defaultName)
                    name = PdfColorSpaceNames.Expand(defaultName.Value);
            }
            if (csRes != null)
            {
                // A named space's device base or alternate is remapped too (§8.6.5.6).
                var entry = _doc.ResolveObject(DefaultColorSpaces.Remap(csRes[new PdfName(name)], _resources, _doc));
                if (entry is PdfArray arr && arr.Count > 0 && arr[0] is PdfName first)
                {
                    csArray = arr;
                    name    = PdfColorSpaceNames.Expand(first.Value);
                }
                else if (entry is PdfName direct)
                {
                    // A name-valued entry is legal (§8.6.3) and the raster path handles it through
                    // PdfColor.ResolveSpace. Not currently observable -- only a device space may be
                    // written as a bare name, and OpSc infers those from the operand count anyway --
                    // but leaving one resolver able to read a shape the other cannot is how the
                    // observable divergences in this file started.
                    name = PdfColorSpaceNames.Expand(direct.Value);
                }
            }

            bool isPattern = name == PdfColorSpaceNames.Pattern;
            // A named underlying space (`[/Pattern /CS0]`, §8.6.6.2), stored resolved — the raster twin's rule.
            if (isPattern && csArray != null && csArray.Count > 1 && csArray[1] is PdfName
                && ColorSpaceResources.Lookup((PdfName)csArray[1], _resources, _doc) is PdfObject namedBase)
                csArray = new PdfArray { csArray[0], namedBase };
            if (stroke)
            {
                _gs.ColorSpaceStroke      = name;
                _gs.ColorSpaceArrayStroke = csArray;
                _gs.StrokeIsPattern       = isPattern;
            }
            else
            {
                _gs.ColorSpaceFill      = name;
                _gs.ColorSpaceArrayFill = csArray;
                _gs.FillIsPattern       = isPattern;
            }

            SetInitialColorForSpace(name, csArray, stroke);
        }

        /// <summary>
        /// §8.6.8 Table 74: <c>cs</c>/<c>CS</c> "shall then set the current colour to its
        /// initial value, which depends on the colour space" — black for the device and
        /// CIE-based spaces, index 0 for <c>/Indexed</c>, tint 1.0 in every component for
        /// <c>/Separation</c> and <c>/DeviceN</c>, and for <c>/Pattern</c> a colour that paints
        /// nothing until <c>scn</c> selects a pattern.
        ///
        /// <para><b>Every family gets one, not only the three device families.</b> <c>rgb(0,0,0)</c>
        /// for the rest would be wrong in four different ways: an Indexed space painted
        /// black instead of palette entry 0, a Separation painted black instead of its tint
        /// transform at 1.0 (often a saturated spot colour), an ICCBased /N 4 space painted
        /// black-as-RGB rather than through its own conversion, and <c>/Pattern cs</c> left the
        /// previously selected pattern in place so the next fill re-used it.</para>
        ///
        /// <para>The rule itself is <see cref="PdfColor.InitialColorOperands"/>, shared with the
        /// raster renderer: one implementation.</para>
        /// </summary>
        /// <remarks>
        /// The initial components go through <see cref="OpSc"/> rather than being converted
        /// here, so a Separation's tint transform, an Indexed palette lookup and a CalGray
        /// <c>/Gamma</c> all apply exactly as they do for an explicit <c>sc</c> — one
        /// colour-setting path, not a second one free to drift from it.
        /// </remarks>
        private void SetInitialColorForSpace(string family, PdfArray csArray, bool stroke)
        {
            // The device-mapped family, never the raw one: /ICCBased is not a paintable family and
            // its /N names which device space's black Table 74 means.
            string deviceFamily = csArray != null ? PdfColor.ResolveSpace(csArray, _doc).DeviceFamily : family;

            PdfObject[] operands = PdfColor.InitialColorOperands(deviceFamily, csArray, _doc);
            if (operands == null)
            {
                // /Pattern: only this paint side's name, so `/Pattern CS` does not discard the
                // fill pattern a preceding `scn` selected.
                if (stroke) _gs.PatternNameStroke = null; else _gs.PatternNameFill = null;
                return;
            }

            OpSc(new ContentStreamOperation(
                stroke ? PdfOperators.SetColorStrokeN : PdfOperators.SetColorFillN, operands), stroke);
        }
        // ---- Clipping ----
    }
}
