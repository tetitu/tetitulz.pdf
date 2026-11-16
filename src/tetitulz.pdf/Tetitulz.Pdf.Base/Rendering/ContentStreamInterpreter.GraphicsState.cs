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
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Interprets a PDF content stream, executing drawing operators against a
    /// <see cref="PdfRenderContext"/> backed by SkiaSharp.
    ///
    /// <para>Supported operator families:</para>
    /// <list type="bullet">
    ///   <item>Path construction: m l c v y h re</item>
    ///   <item>Path painting: S s F f f* B B* b b* n</item>
    ///   <item>Color: g G rg RG k K cs CS sc SC scn SCN</item>
    ///   <item>Graphics state: q Q cm w J j M d ri i gs</item>
    ///   <item>Text: BT ET Tf Td TD Tm T* Tj TJ ' " Tc Tw Tz TL Tr Ts</item>
    ///   <item>XObject: Do (Form XObjects recursed; Image XObjects rasterized)</item>
    ///   <item>Clipping: W W*</item>
    /// </list>
    /// </summary>
    internal sealed partial class ContentStreamInterpreter
    {
        // ---- CTM ----
        private void OpCm(ContentStreamOperation op)
        {
            if (op.Operands.Length < 6) return;
            float[] m =
            {
                GetFloat(op, 0), GetFloat(op, 1),
                GetFloat(op, 2), GetFloat(op, 3),
                GetFloat(op, 4), GetFloat(op, 5)
            };
            // A NaN/Inf operand would permanently poison GS.CTM for the rest of the
            // q…Q scope, blanking every later draw in it. Ignore the bad operator.
            if (!AllFinite(m))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "cm ignored: non-finite matrix operand");
                return;
            }
            _ctx.ConcatCTM(m);
        }

        // ---- Line style ----
        // Named after the operator's meaning, not its token: `w`/`W` and `j`/`J` differ
        // only by case.
        private void OpSetLineWidth(ContentStreamOperation op)  => _ctx.GS.LineWidth  = GetFloat(op, 0);
        private void OpSetLineCap(ContentStreamOperation op)    => _ctx.GS.LineCap    = GetInt(op, 0);
        private void OpSetLineJoin(ContentStreamOperation op)   => _ctx.GS.LineJoin   = GetInt(op, 0);
        private void OpSetMiterLimit(ContentStreamOperation op) => _ctx.GS.MiterLimit = GetFloat(op, 0);

        // d [dash_array] phase — read through DashPattern, which the SVG writer also calls, so the
        // two agree on `d [3 3]` with the phase missing, and an empty (solid-line) array leaves no
        // stale offset for whatever `d` comes next.
        private void OpD(ContentStreamOperation op)
        {
            if (!DashPattern.TryRead(op, out float[] pattern, out float phase)) return;
            _ctx.GS.DashPattern = pattern;
            _ctx.GS.DashPhase   = phase;
        }

        private void OpSetExtGState(ContentStreamOperation op)
        {
            // gs name — look up in /ExtGState
            if (op.Operands.Length < 1) return;
            string name = GetName(op, 0);
            var extGs = ResolveResourceDict(PdfName.ExtGState, name);
            if (extGs == null) return;

            if (MatrixGuard.TryFinite(PdfResolve.Number(_doc, extGs, PdfName.LW), out float lw)) _ctx.GS.LineWidth = lw;

            var lc = PdfResolve.Number(_doc, extGs, PdfName.LC);
            if (lc != null) _ctx.GS.LineCap = lc.IntValue;

            var lj = PdfResolve.Number(_doc, extGs, PdfName.LJ);
            if (lj != null) _ctx.GS.LineJoin = lj.IntValue;

            if (MatrixGuard.TryFinite(PdfResolve.Number(_doc, extGs, PdfName.ML), out float ml)) _ctx.GS.MiterLimit = ml;

            // Stroking alpha (CA) and non-stroking alpha (ca)
            var ca_stroke = PdfResolve.Number(_doc, extGs, PdfName.CA);
            if (ca_stroke != null) _ctx.GS.StrokeAlpha = Clamp((float)ca_stroke.DoubleValue);

            var ca_fill = PdfResolve.Number(_doc, extGs, PdfName.ca);
            if (ca_fill != null) _ctx.GS.FillAlpha = Clamp((float)ca_fill.DoubleValue);

            // Blend mode (BM) — a name, or an array of names, of which §11.6.3 says "a PDF
            // processor shall use the first blend mode in the array that it recognizes (or Normal
            // if it recognizes none of them)". This took element 0, as did the SVG twin, so
            // `[/Foo /Multiply]` painted Normal in both renderers. One decision, in
            // PdfBlendModeEntry, reached from here, from SvgContentWriter.GraphicsState and from the
            // annotation compositor; it resolves the value and each element, so an indirect /BM —
            // once the one unresolved read in this method — is read as well.
            string bm = PdfBlendModeEntry.FirstRecognised(_doc, extGs[PdfName.BM]);
            if (bm != null) _ctx.GS.BlendMode = bm;

            // Overprint flags
            var op_stroke = PdfResolve.Value(_doc, extGs[PdfName.OP]);
            if (op_stroke is PdfBoolean opStrokeBool)
                _ctx.GS.OverprintStroke = opStrokeBool.Value;
            var op_fill = PdfResolve.Value(_doc, extGs[PdfName.op]);
            if (op_fill is PdfBoolean opFillBool)
                _ctx.GS.OverprintFill = opFillBool.Value;
            var opm = PdfResolve.Number(_doc, extGs, PdfName.OPM);
            if (opm != null) _ctx.GS.OverprintMode = opm.IntValue;

            // /Font [fontRef size] — §8.4.5 Table 58. Read through ExtGStateFont, which the SVG
            // writer also calls. Table 58's first element is an indirect reference to a font
            // DICTIONARY; a /Name there is accepted too, by both renderers alike, so neither shape
            // leaves exactly one renderer able to select the font for a given file.
            if (ExtGStateFont.TryRead(_doc, extGs, _resources, "",
                                      out string gsFontName, out float? gsFontSize))
            {
                // Only when the entry actually named a reachable font: §8.4.5 sets a font, it
                // never unsets one, so an unreachable font must leave the current face alone.
                if (gsFontName != null) _ctx.GS.FontResourceName = gsFontName;
                if (gsFontSize != null) _ctx.GS.FontSize = gsFontSize.Value;
            }

            // /FL (Flatness) — rendering hint for curve approximation
            // Flatness is a SkiaSharp internal; ignored.

            // /RI (Rendering Intent) from ExtGState
            var ri = PdfResolve.Name(_doc, extGs, PdfName.RI);
            if (ri != null) SetRenderingIntent(ri.Value);

            // /HT, /HTO (halftone and halftone origin, §10.5) and /SA (stroke adjustment,
            // §10.7.5). None of the three has a code path in this renderer: it composites
            // continuous-tone sRGB, so a halftone screen has nothing to control, and Skia's
            // own rasteriser owns pixel-grid fitting. Report so the difference between
            // "ignored" and "absent" is visible; see RenderParameterIgnored. The wording is
            // IgnoredParameterReporting's, shared with the SVG writer, which reads the same keys.
            if (IgnoredParameterReporting.Deviates(_doc, extGs, IgnoredGraphicsStateParameter.Halftone))
                ReportIgnoredParameter(IgnoredGraphicsStateParameter.Halftone);
            if (IgnoredParameterReporting.Deviates(_doc, extGs, IgnoredGraphicsStateParameter.StrokeAdjustment))
                ReportIgnoredParameter(IgnoredGraphicsStateParameter.StrokeAdjustment);

            // /UseBlackPtComp (PDF 2.0, ISO 32000-2 §8.6.5.9 / AN001): honoured — /OFF converts
            // ICC colour without black-point compensation until the state is restored.
            if (PdfResolve.Name(_doc, extGs, PdfName.UseBlackPtComp) is PdfName bpc)
                _ctx.GS.BlackPointCompensation = IccRenderingIntents.UseBlackPtComp(bpc.Value, _ctx.GS.BlackPointCompensation);

            // /D [array phase] — dash pattern from ExtGState (same pair the 'd' operator takes).
            // One implementation, in DashPattern.TryReadExtGState, which the SVG writer also calls:
            // this copy resolved only the outer two objects, so an indirect dash length or phase
            // read as 0, and an empty array left a stale phase behind.
            if (DashPattern.TryReadExtGState(_doc, extGs, out float[] gsDash, out float gsDashPhase))
            {
                _ctx.GS.DashPattern = gsDash;
                _ctx.GS.DashPhase   = gsDashPhase;
            }

            // /AIS (Alpha Is Shape, §11.6.4.4) is HONOURED: PdfRenderContext.ElementBlendMode reads
            // it. The compositing formulas use only the product of shape and opacity, so whether
            // the alpha constants and the soft mask are one or the other changes a pixel only in a
            // knockout group — where an element whose alpha is shape composites over what the group
            // holds instead of replacing it. Stored, not reported.
            var ais = PdfResolve.Bool(_doc, extGs, PdfName.AIS);
            if (ais != null) _ctx.GS.AlphaIsShape = ais.Value;

            // /TK is HONOURED here (§9.3.8) — see ContentStreamInterpreter.BeginTextKnockout —
            // so it is stored and not reported. The SVG writer reports, because SVG has no knockout
            // compositing model, and does so on the effective deviation rather than on the token,
            // since `true` is also the initial value.
            var tk = PdfResolve.Bool(_doc, extGs, PdfName.TK);
            if (tk != null) _ctx.GS.TextKnockout = tk.Value;

            // /TR2 overrides /TR when present (PDF 1.3+).  Both hold a function dict,
            // an array of per-component function dicts, or the name /Identity or /Default.
            // Both are RESOLVED, and the identity test is TransferFunctionLut's — the same two
            // things the SVG writer already did.
            //
            // Unlike the /SMask and /D reads beside it, an unresolved read of this pair would NOT
            // produce wrong output: an indirect entry falls to the else branch, and
            // TransferFunctionLut.Build resolves its argument itself, so a referenced function is
            // still built and a reference to /Identity still yields a null LUT. The resolve and the
            // shared identity test are about SHAPE — the two renderers read this entry the same way
            // — not behaviour; no test asserts a difference because there is none to assert.
            var trEntry = PdfResolve.Value(_doc, extGs[PdfName.TR2] ?? extGs[PdfName.TR]);
            if (trEntry != null)
            {
                // /Identity or /Default (as a PdfName) → clear any active transfer function.
                if (TransferFunctionLut.IsIdentityName(trEntry))
                {
                    _ctx.GS.TransferFunction = null;
                    _ctx.GS.TransferLut = null;
                }
                else
                {
                    _ctx.GS.TransferFunction = trEntry;          // used by image ApplyTransferFunction
                    _ctx.GS.TransferLut = BuildTransferLut(trEntry); // used for fill/stroke colours
                }
            }

            // /BG, /BG2 (black generation) and /UCR, /UCR2 (undercolour removal) convert a DeviceRGB
            // colour to the process inks (§10.3.4): HONOURED on a separation plate (BlackGeneration,
            // read by SeparationPlate.TryTint) — and so in the plates' overprint preview and PSD. The
            // composite render composites sRGB, where they have no effect, and reports them ignored.
            if (_ctx.Plate != null) _ctx.GS.BlackGeneration = BlackGeneration.Read(_doc, extGs, _ctx.GS.BlackGeneration);
            else if (IgnoredParameterReporting.Deviates(
                    _doc, extGs, IgnoredGraphicsStateParameter.BlackGenerationUndercolor))
                ReportIgnoredParameter(IgnoredGraphicsStateParameter.BlackGenerationUndercolor);

            // SMask — soft mask from ExtGState (§11.6.4.4).
            // RESOLVED before the name test, and /Identity clears as well as /None — both of which
            // the SVG writer already did. Unresolved, an indirect /SMask /None never cleared; and
            // /SMask /Identity left the previous mask masking everything drawn after it, which is
            // visible output rather than a latent difference.
            var smask = PdfResolve.Value(_doc, extGs[PdfName.SMask]);
            if (smask is PdfName smaskName)
            {
                if (PdfSoftMaskNames.IsNoSoftMask(smaskName.Value)) _ctx.ClearSoftMask();
            }
            else if (smask is PdfDictionary smaskDict)
            {
                ApplySMask(smaskDict);
            }
        }

        /// <summary>
        /// Stores the rendering intent (<c>ri</c> operator or ExtGState <c>/RI</c>), and reports it
        /// only for the one intent this renderer cannot select a table for.
        /// </summary>
        /// <remarks>
        /// <para><b>The intent is not ignored.</b> <see cref="GraphicsState.RenderingIntent"/> has
        /// readers: an ICC profile carrying
        /// separate <c>A2B0</c>/<c>A2B1</c>/<c>A2B2</c> tables has one per intent, and
        /// <see cref="CurrentIntent"/> selects between them at every site that evaluates a
        /// profile.</para>
        /// <para><b>None is reported now.</b> <c>/AbsoluteColorimetric</c> was, while the relative
        /// table stood in for it; it is the relative table times the profile's media white
        /// (<c>IccProfile.MediaWhiteScale</c>, checked against an independent colour engine). A profile with
        /// one table gives the same colour under several intents, which is an answer rather than a gap.</para>
        /// </remarks>
        private void SetRenderingIntent(string intent) => _ctx.GS.RenderingIntent = intent;

        /// <summary>
        /// The graphics state's rendering intent as the enum the ICC evaluator selects tables with.
        /// </summary>
        /// <remarks>
        /// One place per renderer turns the stored name into an intent, so the two cannot disagree
        /// about what an unrecognised name means — §8.6.5.8 says a reader shall ignore one it does
        /// not recognise, which <see cref="IccRenderingIntents.Parse"/> implements as the default.
        /// </remarks>
        private IccRenderingIntent CurrentIntent => _ctx.GS.Intent;

        /// <summary>
        /// The intent an image XObject is drawn under — its own <c>/Intent</c> beats the graphics
        /// state's (§8.9.5.2 Table 89). The rule itself is shared with the SVG writer.
        /// </summary>
        private IccRenderingIntent ImageIntent(PdfDictionary imageDict)
            => IccRenderingIntents.ForImage(_doc, imageDict, CurrentIntent);

        /// <summary>
        /// Reports, once per content stream, that a graphics-state parameter was read, stored and
        /// not honoured. The scope is this interpreter, so a form XObject re-reports — the SVG
        /// writer scopes the same set per page instead, which is why the flag set lives with each
        /// caller and only the gate and the wording are shared.
        /// </summary>
        private void ReportIgnoredParameter(IgnoredGraphicsStateParameter which,
                                            string parameterValue = null)
            => IgnoredParameterReporting.ReportOnce(ref _ignoredParametersReported, which,
                                                    renderer: string.Empty, parameterValue);

        // Soft mask rendering
        /// <summary>
        /// The axis-aligned page-space rectangle a mask group's <c>/BBox</c> occupies, clipped to
        /// the page. False when there is no usable <c>/BBox</c> or it lands off-page, in which
        /// case the caller falls back to a page-sized mask.
        /// </summary>
        /// <remarks>
        /// The bbox is in form space, so it is mapped through the group's <c>/Matrix</c> and then
        /// the CTM in force at the <c>gs</c> — the same composition <c>RenderToBitmap</c> applies
        /// internally, in the same order. All four corners are transformed rather than two,
        /// because a rotated or skewed matrix makes the opposite corners insufficient.
        /// </remarks>
        private bool TryGetGroupPageBounds(PdfDictionary groupDict, float[] ctm,
            out float x0, out float y0, out float x1, out float y1)
        {
            x0 = y0 = x1 = y1 = 0f;

            var bboxArr = PdfResolve.Array(_doc, groupDict, PdfName.BBox);
            if (bboxArr == null || bboxArr.Count < 4) return false;

            float bx0 = GetArrayFloat(bboxArr, 0), by0 = GetArrayFloat(bboxArr, 1);
            float bx1 = GetArrayFloat(bboxArr, 2), by1 = GetArrayFloat(bboxArr, 3);
            float lx = Math.Min(bx0, bx1), ly = Math.Min(by0, by1);
            float hx = Math.Max(bx0, bx1), hy = Math.Max(by0, by1);
            if (!(hx > lx) || !(hy > ly)) return false;

            // Form space → the space the CTM maps from.
            float[] m = ctm;
            var formMatrix = PdfResolve.Array(_doc, groupDict, PdfName.Matrix);
            if (formMatrix != null && formMatrix.Count >= 6)
            {
                var fm = new float[6];
                for (int i = 0; i < 6; i++) fm[i] = GetArrayFloat(formMatrix, i);
                m = GraphicsState.Compose(fm, ctm);
            }

            // One four-corner walk, in the PdfMatrix leaf — this was a local closure doing what
            // PdfPageRenderer.TransformedBBox already did for both renderers. The finiteness test
            // stays HERE: PdfMatrix maps and nothing else, because the three callers want three
            // different answers to a non-finite corner (see its remarks).
            PdfMatrix.TransformedBounds(m, lx, ly, hx, hy,
                                        out float minX, out float minY, out float maxX, out float maxY);

            if (float.IsNaN(minX) || float.IsNaN(minY) || float.IsInfinity(maxX) || float.IsInfinity(maxY))
                return false;

            // Clip to the page: rendering mask outside it is wasted, and a negative origin would
            // put the bitmap's device offset off-canvas.
            minX = Math.Max(0f, minX);
            minY = Math.Max(0f, minY);
            maxX = Math.Min(_ctx.PageWidth, maxX);
            maxY = Math.Min(_ctx.PageHeight, maxY);
            if (!(maxX > minX) || !(maxY > minY)) return false;

            // A bbox that already covers the page gains nothing and costs a code path.
            if (maxX - minX >= _ctx.PageWidth && maxY - minY >= _ctx.PageHeight) return false;

            x0 = minX; y0 = minY; x1 = maxX; y1 = maxY;
            return true;
        }

        private void ApplySMask(PdfDictionary smaskDict)
        {
            var sType = PdfResolve.Name(_doc, smaskDict, PdfName.S);
            bool isAlpha = sType == null || sType.Value == PdfSoftMaskNames.Alpha;

            var gXObj = _doc.ResolveObject(smaskDict[PdfName.G]);
            if (gXObj == null) return;

            // Resolve the transparency group XObject
            PdfDictionary groupDict = null;
            if (gXObj is PdfStream gs2) groupDict = gs2;
            else if (gXObj is PdfDictionary gd) groupDict = gd;
            if (groupDict == null) return;

            // Render the group into a temporary bitmap, applying the CTM in effect
            // at the `gs` operator so the mask aligns with the content it modulates.
            //
            // Bounded to the group's /BBox where it has one: page-sized always, a mask covering 1%
            // of the page would allocate a full-page bitmap and run the luminosity pass over every
            // pixel of it — 24 such masks measured 945 ms and 193 MB. The offset is what lets every consumer place a smaller bitmap correctly.
            int maskOffsetX = 0, maskOffsetY = 0;
            int pixW, pixH;
            float innerW = 0f, innerH = 0f, originX = 0f, originY = 0f;

            bool bounded = TryGetGroupPageBounds(groupDict, _ctx.GS.CTM,
                                                 out float px0, out float py0, out float px1, out float py1);
            if (bounded)
            {
                innerW = px1 - px0;
                innerH = py1 - py0;
                originX = px0;
                originY = py0;
                pixW = SaturatingCast.ToInt(Math.Ceiling(innerW * _ctx.DpiScale));
                pixH = SaturatingCast.ToInt(Math.Ceiling(innerH * _ctx.DpiScale));

                // Device position of the bitmap's top-left. X is straightforward; Y flips,
                // because PDF y grows upward and the bitmap's first row is the region's TOP
                // edge — which is the higher PDF y.
                maskOffsetX = SaturatingCast.ToInt(Math.Floor(px0 * _ctx.DpiScale));
                maskOffsetY = SaturatingCast.ToInt(Math.Floor((_ctx.PageHeight - py1) * _ctx.DpiScale));
            }
            else
            {
                pixW = SaturatingCast.ToInt(_ctx.PageWidth * _ctx.DpiScale);
                pixH = SaturatingCast.ToInt(_ctx.PageHeight * _ctx.DpiScale);
            }

            var groupBitmap = RenderToBitmap(groupDict, pixW, pixH,
                                              innerWidth: innerW, innerHeight: innerH,
                                              bboxOriginX: originX, bboxOriginY: originY,
                                              initialCtm: _ctx.GS.CTM,
                                              honourFormMatrixAndBBox: true);
            // The mask the file asked for could not be built (an unreadable /G, or the page's form
            // budget refusing this group). Returning alone leaves the PREVIOUS mask in force, so the
            // next draw is masked by an unrelated one — a confidently wrong result. Clearing matches
            // the SVG writer, which assigns BuildSvgSoftMask's null straight onto SvgMaskId.
            //
            // No unwind is owed here: the budget charge precedes every allocation and push, and this
            // method pushes no graphics state of its own.
            if (groupBitmap == null) { _ctx.ClearSoftMask(); return; }

            // The /BC backdrop (§11.6.5.2), through the shared reader so this and the SVG
            // writer cannot disagree about what the components mean. They did: this loop used
            // to take bcArr[0..2] as R, G, B whatever the length, so a four-component CMYK
            // backdrop lost /K and `/BC [0 0 0 0]` — CMYK *white* — masked everything out.
            // Absent /BC is black, which is the spec default.
            SKColor? backdrop = PdfColor.SoftMaskBackdrop(smaskDict, _doc);
            float bcR = (backdrop?.Red   ?? 0) / MaxChannelValueF;
            float bcG = (backdrop?.Green ?? 0) / MaxChannelValueF;
            float bcB = (backdrop?.Blue  ?? 0) / MaxChannelValueF;
            // The mask's value where the group paints nothing — everywhere outside its /BBox:
            // the backdrop's luminosity, or an alpha mask's 0 — which /TR maps like every other.
            byte outside = isAlpha ? (byte)0
                : PdfColor.Channel(LumaRedWeight * bcR + LumaGreenWeight * bcG + LumaBlueWeight * bcB);

            // For Luminosity: convert to grayscale mask using ITU-R BT.601 luma.
            // PDF §11.6.5.3: blend the group composite *throughout* with the
            // backdrop colour weighted by (1 - group_alpha), then take the luma
            // of the composite as the mask value.
            if (!isAlpha)
            {

                // Straight through the bitmap's own buffer, not SKBitmap.Pixels, which allocates a
                // managed SKColor[] over the WHOLE bitmap and copies into it, and its setter, which
                // copies back: measured at 33 MB and ~264 ms
                // for one page-sized mask at 300 dpi, and 65 MB / ~351 ms when a /TR adds the
                // second pass below (benchmarks/…/RenderBenchmarks.PageSizedSoftMask_300Dpi).
                // Same move ImageSampleLayout.BuildBitmap already made for image samples.
                //
                // TWO things change with the buffer rather than the SKColor[], and both are why
                // this is not a mechanical swap:
                //
                // 1. The buffer is PREMULTIPLIED (RenderToBitmap makes SKAlphaType.Premul) where
                //    SKColor is straight, so the getter was un-premultiplying every pixel. The
                //    old expression `(c.Red / 255) * a` re-applied the very alpha that had just
                //    been divided out — so the premultiplied byte IS that term, exactly, and
                //    reading it directly removes a lossy round-trip rather than approximating it.
                // 2. Only the ALPHA channel of this bitmap is ever read again: all three
                //    consumers composite it with SKBlendMode.DstIn (PdfRenderContext.Transparency
                //    :637, :902 and the shader compose at :1011), whose result depends on source
                //    alpha alone. So RGB is written as lum for a valid premultiplied pixel
                //    (RGB <= A) and is otherwise dead.
                var span = groupBitmap.GetPixelSpan();
                int stride = groupBitmap.RowBytes;
                // Read the layout rather than assuming it — GAPS §13d records a bitmap arriving
                // as the platform-preferred Bgra8888 where Rgba8888 was assumed. Both put alpha
                // last; they differ only in whether byte 0 is red or blue.
                int rOff = groupBitmap.ColorType == SKColorType.Bgra8888 ? 2 : 0;
                if (groupBitmap.ColorType != SKColorType.Rgba8888 &&
                    groupBitmap.ColorType != SKColorType.Bgra8888)
                {
                    // Neither 8888 layout: no byte offsets to trust, so take the slow, allocating
                    // path rather than reinterpreting the buffer. Unreachable today — and counted,
                    // like every other slow branch beside a buffer walk, so that stops being a claim.
                    Images.BitmapPixels.PropertyFallbacks++;
                    var px = groupBitmap.Pixels;
                    for (int i = 0; i < px.Length; i++)
                    {
                        var c = px[i];
                        float pa = c.Alpha / MaxChannelValueF;
                        byte l = PdfColor.Channel(
                            LumaRedWeight   * ((c.Red   / MaxChannelValueF) * pa + bcR * (1f - pa)) +
                            LumaGreenWeight * ((c.Green / MaxChannelValueF) * pa + bcG * (1f - pa)) +
                            LumaBlueWeight  * ((c.Blue  / MaxChannelValueF) * pa + bcB * (1f - pa)));
                        px[i] = new SKColor(l, l, l, l);
                    }
                    groupBitmap.Pixels = px;
                }
                else
                {
                    // The row is re-sliced rather than indexed as `span[y * stride + x * 4 + k]`:
                    // a slice of known length with CONSTANT offsets 0..3 lets the JIT drop the
                    // bounds check on all four reads, where a computed index cannot be proved
                    // in range and pays for one per access. Same reason the weights below read
                    // into locals first.
                    bool swapRb = rOff != 0;
                    for (int y = 0; y < groupBitmap.Height; y++)
                    {
                        var row = span.Slice(y * stride, groupBitmap.Width * 4);
                        for (int o = 0; o + 3 < row.Length; o += 4)
                        {
                            byte c0 = row[o], c1 = row[o + 1], c2 = row[o + 2], ab = row[o + 3];
                            byte rp = swapRb ? c2 : c0;
                            byte bp = swapRb ? c0 : c2;

                            float a = ab / MaxChannelValueF;
                            float inv = 1f - a;
                            // rp/c1/bp are already colour x alpha — see note 1 above.
                            float r = rp / MaxChannelValueF + bcR * inv;
                            float g = c1 / MaxChannelValueF + bcG * inv;
                            float b = bp / MaxChannelValueF + bcB * inv;
                            // One quantisation rule, PdfColor.Channel — this was a hand-copy that
                            // happened to agree. The weights are applied to the normalised values
                            // and the scale to 0..255 is Channel's, rather than being distributed
                            // across three terms.
                            byte lum = PdfColor.Channel(
                                LumaRedWeight * r + LumaGreenWeight * g + LumaBlueWeight * b);
                            row[o] = row[o + 1] = row[o + 2] = row[o + 3] = lum;
                        }
                    }
                    // Matches ImageSampleLayout.BuildBitmap, which notifies after its own span
                    // writes. Mutating BOTH of these calls out leaves all 82 soft-mask tests
                    // green, and the reason is call ORDER rather than the call being inert: the
                    // notify bumps the generation id that SKImage and shader caches key on, and
                    // every consumer of this bitmap (SetSoftMask, then the DstIn composite) is
                    // constructed after the mutation, so there is nothing cached to invalidate
                    // yet. Kept because that ordering is not a property this method controls.
                    groupBitmap.NotifyPixelsChanged();
                }
            }

            // Apply /TR transfer function to the mask channel if present.
            var smTrObj = smaskDict[PdfName.TR];
            if (smTrObj != null)
            {
                var smTrResolved = _doc?.ResolveObject(smTrObj) ?? smTrObj;
                // `is PdfDictionary`, not `is PdfStream`: §11.6.5.2's /TR is any function object,
                // and a PdfStream *is* a PdfDictionary here — which is what admits the Type 0
                // sampled and Type 4 PostScript forms alongside the Type 2/3 dictionaries.
                if (smTrResolved is PdfDictionary smTrDict)
                {
                    // One LUT, not one evaluator call per pixel. The mask channel is a single
                    // byte, so 256 entries cover every possible input exactly — this is a
                    // memoisation, not an approximation, and no pixel moves. A page-sized mask
                    // is millions of pixels and a Type 4 /TR re-runs a whole PostScript program
                    // for each of them; the same substitution was already made for image
                    // transfer functions (BuildTransferChannelLut, ContentStreamInterpreter.Images).
                    //
                    // BuildTransferChannelLut rather than the sibling BuildTransferLut: that one
                    // reads the 3/4-element /TR *array* of §8.6.5's graphics state and returns
                    // byte[][], whereas an SMask /TR is a single function (or /Identity), so
                    // routing through it would change what the mask channel means.
                    byte[] lut = BuildTransferChannelLut(smTrDict);
                    // Null means "identity, nothing to remap" — unreachable for a non-null
                    // function today, but BuildTransferLut already collapses an all-identity
                    // result to null and its per-channel sibling may grow the same shortcut.
                    // Skipping is then the right answer; dereferencing would be an NRE.
                    if (lut != null)
                    {
                        outside = lut[outside];
                        // Through the buffer, for the reason the luminosity pass above gives at
                        // length: SKBitmap.Pixels would allocate and copy the whole bitmap twice
                        // more, which is the entire +32 MB / +87 ms this /TR pass measured against
                        // the same fixture without one.
                        //
                        // The LUT is indexed by the mask channel, which is the ALPHA byte in both
                        // 8888 layouts, so this pass needs no red/blue offsets at all. For a
                        // luminosity mask every channel is written (matching the pass above, and
                        // dead for the same DstIn reason); for an /Alpha mask only alpha moves,
                        // and its colour bytes are left exactly as they were.
                        var trSpan = groupBitmap.GetPixelSpan();
                        int trStride = groupBitmap.RowBytes;
                        bool bulk = groupBitmap.ColorType == SKColorType.Rgba8888 ||
                                    groupBitmap.ColorType == SKColorType.Bgra8888;
                        if (!bulk)
                        {
                            Images.BitmapPixels.PropertyFallbacks++;
                            var trPx = groupBitmap.Pixels;
                            for (int i = 0; i < trPx.Length; i++)
                            {
                                var c = trPx[i];
                                byte m = lut[c.Alpha];
                                trPx[i] = isAlpha
                                    ? new SKColor(c.Red, c.Green, c.Blue, m)
                                    : new SKColor(m, m, m, m);
                            }
                            groupBitmap.Pixels = trPx;
                        }
                        else
                        {
                            for (int y = 0; y < groupBitmap.Height; y++)
                            {
                                var row = trSpan.Slice(y * trStride, groupBitmap.Width * 4);
                                for (int o = 0; o + 3 < row.Length; o += 4)
                                {
                                    byte mapped = lut[row[o + 3]];
                                    if (isAlpha)
                                    {
                                        // Lowering alpha alone can leave a colour byte above it,
                                        // which is not a valid premultiplied pixel. Nothing here
                                        // reads these bytes, so clamping is what keeps the
                                        // RGB <= A invariant
                                        // without inventing a colour.
                                        if (row[o]     > mapped) row[o]     = mapped;
                                        if (row[o + 1] > mapped) row[o + 1] = mapped;
                                        if (row[o + 2] > mapped) row[o + 2] = mapped;
                                        row[o + 3] = mapped;
                                    }
                                    else row[o] = row[o + 1] = row[o + 2] = row[o + 3] = mapped;
                                }
                            }
                            groupBitmap.NotifyPixelsChanged();
                        }
                    }
                }
            }

            // §11.6.5.2: outside the group's /BBox the mask is the backdrop's value — /BC's
            // luminosity through /TR, or an alpha mask's TR(0). Both consumers read a bounded
            // bitmap's outside as 0 (the shader route's decal tiling, the layer route's clear), so
            // the common case keeps the bounded bitmap, and a non-zero outside value — a luminosity
            // mask with /BC [1] shows everything outside its box — is carried by a page-sized mask
            // holding it, the bounded bitmap copied in where it lies.
            if (bounded && outside != 0)
            {
                int fullW = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(_ctx.PageWidth * _ctx.DpiScale)));
                int fullH = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(_ctx.PageHeight * _ctx.DpiScale)));
                if (FormBudget == null || FormBudget.TryChargeOffscreenPixels((long)fullW * fullH))
                {
                    var full = new SKBitmap(fullW, fullH, SKColorType.Rgba8888, SKAlphaType.Premul);
                    full.Erase(new SKColor(outside, outside, outside, outside));
                    using (var fullCanvas = new SKCanvas(full))
                    using (var copy = new SKPaint { BlendMode = SKBlendMode.Src })
                        fullCanvas.DrawBitmap(groupBitmap, maskOffsetX, maskOffsetY, copy);
                    groupBitmap.Dispose();
                    groupBitmap = full;
                    maskOffsetX = 0;
                    maskOffsetY = 0;
                }
                else
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "soft mask: the page's offscreen budget is spent, so the mask's value outside its group's /BBox " +
                        $"({outside} of 255) is not carried; the region outside the box is masked out.");
                }
            }

            // The context owns the bitmap from here: a q may already have copied the reference
            // this replaces into a saved frame, so the interpreter must not dispose it itself.
            _ctx.SetSoftMask(groupBitmap, isAlpha, maskOffsetX, maskOffsetY);
        }

        // ---- Inline image ----
    }
}
