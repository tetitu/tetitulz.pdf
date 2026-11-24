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
        private void OpCm(ContentStreamOperation op)
        {
            if (op.Operands.Length < 6) return;
            float[] m = { Flt(op,0), Flt(op,1), Flt(op,2), Flt(op,3), Flt(op,4), Flt(op,5) };
            // The guard the raster interpreter has too (ContentStreamInterpreter.OpCm): a NaN/Inf
            // operand poisons the CTM for the rest of the q…Q scope, and here it would reach the
            // output as literal `d="MInfinity,NaN …"`, which no viewer renders. Same message as
            // the raster site, so the two can be compared.
            if (!MatrixGuard.AllFinite(m))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    "cm ignored: non-finite matrix operand");
                return;
            }
            _gs.CTM = Compose(m, _gs.CTM);
        }

        // ---- Path construction ----

        /// <summary>
        /// The §8.4.3 stroke <b>geometry</b> attributes — cap, join, miter limit, dash array and
        /// dash offset — for the current graphics state, at an element whose own transform scales
        /// user-space lengths by <paramref name="elementScale"/>.
        /// </summary>
        /// <param name="elementScale">
        /// The element's own local-to-page scale — what a page-space length must be divided by to
        /// resolve correctly inside it. <b>1 for a <c>&lt;path&gt;</c></b>, whose data is written
        /// in page space and which carries no transform; the full element matrix's scale for a
        /// <c>&lt;text&gt;</c> or a glyph-outline group, which bake in the font size and, for a
        /// charstring, the 1/1000 em. It is the same figure <c>stroke-width</c> is divided by at
        /// each of the three sites.
        /// </param>
        /// <remarks>
        /// <para><b>One place, three emitters.</b> If only the path emitter wrote these, a stroked
        /// <c>&lt;text&gt;</c> or glyph-outline run would export with SVG's defaults — butt caps, miter joins, limit 4, solid — however the file had set <c>J</c>,
        /// <c>j</c>, <c>M</c> or <c>d</c>. An outlined display-type run under <c>1 J 1 j</c> came
        /// out square-cornered, and under <c>[6 3] 0 d</c> came out solid.</para>
        /// <para>The dash array takes the same <b>two-step</b> conversion the width does, for the
        /// reason the width's own comment gives: a dash entry is a user-space length, and the
        /// element's transform is applied to it. Multiply into page space by the CTM scale
        /// (§8.4.3.2), then divide back out by the element's own. On a charstring outline that
        /// second scale includes the 1/1000 em, so an undivided array would be about a thousand
        /// times too long — the identical trap the stroke width fell into.</para>
        /// </remarks>
        private string StrokeGeometryAttributes(float elementScale)
        {
            string cap = (PdfLineCap)_gs.LineCap switch
            {
                PdfLineCap.Round            => "round",
                PdfLineCap.ProjectingSquare => "square",
                _                           => "butt",
            };
            string join = (PdfLineJoin)_gs.LineJoin switch
            {
                PdfLineJoin.Round => "round",
                PdfLineJoin.Bevel => "bevel",
                _                 => "miter",
            };

            var sb = new StringBuilder();
            sb.Append($" stroke-linecap=\"{cap}\" stroke-linejoin=\"{join}\"");

            // SVG requires stroke-miterlimit >= 1; PDFs may emit 0 or less, and a browser rejects
            // the attribute rather than clamping it. The raster path passes the raw value to Skia.
            if (join == "miter")
            {
                float miter = Math.Max(1f, _gs.MiterLimit);
                // Against SVG's default, which is 4 — not PDF's 10. Comparing against 10 omitted
                // the attribute for the DEFAULT graphics state, so an ordinary stroke exported at
                // an effective limit of 4 while the raster path stroked it at 10.
                if (Math.Abs(miter - SvgSyntax.SvgDefaultMiterLimit) > 0.01f)
                    sb.Append($" stroke-miterlimit=\"{F(miter)}\"");
            }

            // DashAttributes MULTIPLIES, so it takes the whole user-space → element-space factor,
            // not the element scale on its own. Handing it `elementScale` divided where it had to
            // multiply and multiplied where it had to divide: a 6-point dash on 40-point text came
            // out as 240 rather than 0.15. The <path> site is the one where the two coincide,
            // which is why nothing caught it there.
            float toElementSpace = CtmScale(_gs.CTM) / elementScale;
            if (!(toElementSpace > 0f) || float.IsInfinity(toElementSpace)) toElementSpace = 1f;

            sb.Append(DashAttributes(toElementSpace));
            return sb.ToString();
        }

        /// <summary>
        /// <c>stroke-dasharray</c> / <c>stroke-dashoffset</c> for the current graphics state, or
        /// the empty string when no dash is active.
        ///
        /// <para>Two things were missing and both changed what a dashed line looks like. The
        /// array is a set of user-space lengths, so it takes the same CTM factor the width does
        /// — the raster path scales it by exactly this figure
        /// (<c>PdfRenderContext.BuildEvenDash</c>). And a negative phase was emitted verbatim:
        /// SVG has no defined meaning for a negative <c>stroke-dashoffset</c>, while §8.4.3.6
        /// defines it as an offset one effective period further along (see
        /// <see cref="NormaliseDashPhase"/>).</para>
        ///
        /// <para>A pattern that sums to zero is invalid in PDF and renders as a solid line in
        /// both SVG and Skia, so the attribute is omitted rather than written as zeros.</para>
        /// </summary>
        /// <param name="ctmScale">
        /// The whole user-space → element-space factor, which for an element carrying its own
        /// transform is <b>not</b> the CTM scale alone — see
        /// <see cref="StrokeGeometryAttributes"/>, this method's only caller, which computes it.
        /// </param>
        private string DashAttributes(float ctmScale)
        {
            var pattern = _gs.DashPattern;
            if (pattern == null || pattern.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            float sum = 0f;
            for (int i = 0; i < pattern.Length; i++)
            {
                // A negative entry is invalid in PDF and rejected by SVG; the raster path
                // clamps it the same way.
                float v = Math.Max(0f, pattern[i] * ctmScale);
                sum += v;
                if (i > 0) sb.Append(',');
                sb.Append(F(v));
            }
            if (!(sum > 0f)) return string.Empty;

            string attrs = $" stroke-dasharray=\"{sb}\"";
            float phase = NormaliseDashPhase(_gs.DashPhase, pattern) * ctmScale;
            // 0 is SVG's own default for stroke-dashoffset, so it is left implicit.
            if (Math.Abs(phase) > 1e-3f) attrs += $" stroke-dashoffset=\"{F(phase)}\"";
            return attrs;
        }

        // ---- Color helpers ----
        //
        // All three delegate to PdfColor, the single source of truth for PDF colour-space →
        // sRGB conversion that the raster interpreter also uses.
        //
        // PdfColor ROUNDS — (byte)(clamped * 255 + 0.5f), the device spaces as the CIE spaces. A comment
        // asserting that two things agree is a claim with a shelf life; the parity tests in
        // ColourDuplicationGapTests are what actually hold it. GAPS.md §2b.

        // d [dash_array] phase — read through DashPattern, which the raster path also calls. This
        // copy accepted the array without the phase and supplied 0; the raster one ignored the
        // operator entirely. §8.4.3.6 gives `d` two operands, so a stream carrying one has not
        // written a `d` and the state is left alone.
        //
        // The phase is stored raw. SVG's stroke-dashoffset shares the PDF sign convention only for
        // non-negative values; the scaling and the §8.4.3.6 normalisation happen in
        // DashAttributes, at the point the CTM is known.
        private void OpDash(ContentStreamOperation op)
        {
            if (!DashPattern.TryRead(op, out float[] pattern, out float phase)) return;
            _gs.DashPattern = pattern;
            _gs.DashPhase   = phase;
        }

        private void OpGs(ContentStreamOperation op)
        {
            if (op.Operands.Length < 1) return;
            string name = (op.Operands[0] as PdfName)?.Value;
            if (name == null) return;

            var cat = _resources?[PdfName.ExtGState];
            if (cat == null) return;
            var catDict = _doc.ResolveObject(cat) as PdfDictionary;
            if (catDict == null) return;
            var extGs = _doc.ResolveObject(catDict[new PdfName(name)]) as PdfDictionary;
            if (extGs == null) return;

            // Every entry below is read through PdfResolve, which follows an indirect
            // reference: PdfDictionary's indexer returns the STORED value, so with direct
            // `is PdfNumber` / `is PdfBoolean` casts `/LW 12 0 R`, which is perfectly legal and does
            // occur, would match nothing while the raster path applied it.
            PdfNumber caS = PdfResolve.Number(_doc, extGs, PdfName.CA);
            PdfNumber caF = PdfResolve.Number(_doc, extGs, PdfName.ca);
            if (caS != null) _gs.StrokeAlpha = Clamp((float)caS.DoubleValue);
            if (caF != null) _gs.FillAlpha   = Clamp((float)caF.DoubleValue);
            // Inside a text object emulating /TK through one group alpha, a new alpha, blend mode
            // or soft mask ends what the group can stand for (SvgContentWriter.BeginTextKnockout).
            if (caS != null || caF != null || extGs.ContainsKey(PdfName.BM) || extGs.ContainsKey(PdfName.SMask))
                EndTextKnockoutEarly(strokeSet: caS != null, fillSet: caF != null);
            if (MatrixGuard.TryFinite(PdfResolve.Number(_doc, extGs, PdfName.LW), out float lw)) _gs.LineWidth = lw;
            if (PdfResolve.Number(_doc, extGs, PdfName.LC) is PdfNumber lc)  _gs.LineCap     = lc.IntValue;
            if (PdfResolve.Number(_doc, extGs, PdfName.LJ) is PdfNumber lj)  _gs.LineJoin    = lj.IntValue;
            if (MatrixGuard.TryFinite(PdfResolve.Number(_doc, extGs, PdfName.ML), out float ml)) _gs.MiterLimit = ml;

            // Blend mode → SVG mix-blend-mode. Which name a /BM value denotes — §11.6.3's "first
            // blend mode in the array that it recognizes (or Normal if it recognizes none of
            // them)" — is PdfBlendModeEntry's decision, shared with the raster reader; this used
            // to take element 0, as the raster path did, so `[/Foo /Multiply]` exported Normal.
            string bmMode = PdfBlendModeEntry.FirstRecognised(_doc, extGs[PdfName.BM]);
            if (bmMode != null) _gs.BlendMode = PdfBlendModeToSvg(bmMode);

            // Dash pattern. /D is [dashArray phase] — the SAME pair the `d` operator takes, so
            // the phase must be read here too, or every dash set through an
            // ExtGState would silently move to phase 0.
            // The pattern is `[dashArray phase]`; BOTH the outer array and the inner one are
            // resolved, because either may be indirect.
            if (DashPattern.TryReadExtGState(_doc, extGs, out float[] gsDash, out float gsDashPhase))
            {
                _gs.DashPattern = gsDash;       // null for `[] 0` — solid line
                _gs.DashPhase   = gsDashPhase;
            }

            // Soft mask — render as SVG <mask> element and store ID in GS.
            var smEntry = PdfResolve.Value(_doc, extGs[PdfName.SMask]);
            if (smEntry is PdfName smn && PdfSoftMaskNames.IsNoSoftMask(smn.Value))
                _gs.SvgMaskId = null;   // clear active mask
            else if (smEntry is PdfDictionary smDict)
                _gs.SvgMaskId = BuildSvgSoftMask(smDict);

            // Overprint (/OP, /op, /OPM).  SVG has no native overprint — using
            // mix-blend-mode:multiply on paths that set OP is the closest visual
            // approximation (ink-accumulating compositing).  Stored on GS; PaintPath
            // and text emission consult it when composing the style string.
            if (PdfResolve.Bool(_doc, extGs, PdfName.OP)    is PdfBoolean opS) _gs.OverprintStroke = opS.Value;
            if (PdfResolve.Bool(_doc, extGs, PdfName.op)    is PdfBoolean opF) _gs.OverprintFill   = opF.Value;
            if (PdfResolve.Number(_doc, extGs, PdfName.OPM) is PdfNumber opm)  _gs.OverprintMode   = opm.IntValue;

            // Alpha-is-shape (§11.6.4.4) — stored, not reported. Shape and opacity enter the
            // compositing formulas only as their product, so the distinction changes a pixel only
            // in a knockout group; this writer exports every knockout group as non-knockout
            // (reported there), which is what /AIS true asks of one anyway. The raster renderer
            // reads it (PdfRenderContext.ElementBlendMode). Text knockout (§9.3.8) is next.
            if (PdfResolve.Bool(_doc, extGs, PdfName.AIS) is PdfBoolean ais)
                _gs.AlphaIsShape = ais.Value;
            // Stored only; the report fires from BeginText, on the state a text object actually
            // opens with. Reporting it here missed the case that matters — /TK's initial value is
            // `true`, so a file that never mentions it still asks for knockout and still does not
            // get it from this writer.
            if (PdfResolve.Bool(_doc, extGs, PdfName.TK) is PdfBoolean tk) _gs.TextKnockout = tk.Value;

            // Rendering intent — stored only (SVG has no equivalent), and reported for the same
            // reason the raster path reports it: a fixed sRGB conversion has no gamut-mapping stage
            // for an intent to select, so honoured and dropped look identical in the output.
            if (PdfResolve.Name(_doc, extGs, PdfName.RI) is PdfName ri) SetRenderingIntent(ri.Value);

            // Flatness and stroke adjustment — stored only (SVG has no equivalent). /SA is
            // reported; /FL deliberately is NOT — it is a curve-flattening hint the geometry
            // subsumes, not a colour decision, so reporting it would be noise. The raster path
            // draws the same line.
            if (PdfResolve.Number(_doc, extGs, PdfName.FL) is PdfNumber fl) _gs.Flatness = (float)fl.DoubleValue;
            if (PdfResolve.Bool(_doc, extGs, PdfName.SA) is PdfBoolean sa)
            {
                _gs.StrokeAdjustment = sa.Value;
                // Only /SA true deviates -- see IgnoredParameterReporting.Deviates. The value is
                // still stored either way, so a later reader sees what the file asked for.
                if (IgnoredParameterReporting.Deviates(
                        _doc, extGs, IgnoredGraphicsStateParameter.StrokeAdjustment))
                    ReportIgnoredParameter(IgnoredGraphicsStateParameter.StrokeAdjustment);
            }

            // /HT, /HTO (halftone and its origin, §10.5) and /BG, /BG2, /UCR, /UCR2 (black
            // generation and undercolour removal, §10.3.4-10.3.5) are not even stored: an SVG
            // document is continuous-tone sRGB, so there is nothing for a halftone screen to
            // control and no CMYK stage for a K-synthesis curve to feed. Presence alone is NOT the
            // condition: /HT /Default, /BG2 /Default and /UCR2 /Default name the behaviour this
            // renderer already has, so IgnoredParameterReporting.Deviates -- shared with the raster
            // path, which had the identical bug -- decides, and it resolves indirect values because
            // a halftone is normally written as a reference.
            if (IgnoredParameterReporting.Deviates(_doc, extGs, IgnoredGraphicsStateParameter.Halftone))
                ReportIgnoredParameter(IgnoredGraphicsStateParameter.Halftone);
            if (IgnoredParameterReporting.Deviates(
                    _doc, extGs, IgnoredGraphicsStateParameter.BlackGenerationUndercolor))
                ReportIgnoredParameter(IgnoredGraphicsStateParameter.BlackGenerationUndercolor);

            // /UseBlackPtComp (PDF 2.0, ISO 32000-2 §8.6.5.9 / AN001): honoured, as on the raster path.
            if (PdfResolve.Name(_doc, extGs, PdfName.UseBlackPtComp) is PdfName bpc)
                _gs.BlackPointCompensation = IccRenderingIntents.UseBlackPtComp(bpc.Value, _gs.BlackPointCompensation);

            // Transfer function (/TR2 overrides /TR per PDF spec §8.6.5).
            var trEntry = PdfResolve.Value(_doc, extGs[PdfName.TR2] ?? extGs[PdfName.TR]);
            if (TransferFunctionLut.IsIdentityName(trEntry))
            {
                _gs.TransferFunction = null;
                _gs.TransferLut = null;
            }
            else if (trEntry != null)
            {
                _gs.TransferFunction = trEntry;                       // for the image path
                // …and the same function as a table, for fill and stroke colours. Without this
                // line §10.4 applied to images here and to images *and paint* in the raster path.
                _gs.TransferLut = TransferFunctionLut.Build(trEntry, _doc);
            }

            // /Font: [fontDictRef size] — §8.4.5 Table 58. Updates the current font selection so
            // subsequent text ops use the right face without a separate `Tf`. Read through
            // ExtGStateFont, which the raster interpreter also calls, so both renderers select the
            // same font from the same entry.
            if (ExtGStateFont.TryRead(_doc, extGs, _resources, "SVG: ",
                                      out string gsFontName, out float? gsFontSize))
            {
                // Only when the entry named a reachable font: §8.4.5 sets a font, never unsets one.
                if (gsFontName != null) _gs.FontName = gsFontName;
                if (gsFontSize != null) _gs.FontSize = gsFontSize.Value;
            }
        }

        // ---- Graphics state restore ----

        // q's refused over the depth cap; each one skips a matching Q so the stack stays
        // balanced. Mirrors PdfRenderContext.PushState / PopState — this writer had no cap at
        // all, so an unbalanced `q` in an ordinary page stream grew memory without limit.
        private int _skippedPushes;
        private bool _stateDepthReported;

        // Overprint one-shots when no shared registry was supplied (a defensively constructed
        // writer), mirroring _localIdCounter. The real storage is SvgSharedState.OverprintReports:
        // a prepress page sets overprint once and paints hundreds of times under it, and a
        // per-writer field re-reported for every form XObject and pattern cell on the page.
        private OverprintReportReason _localOverprintReports;

        // Same fallback for the ignored-graphics-state-parameter set. See ReportIgnoredParameter.
        private IgnoredGraphicsStateParameter _localIgnoredParameterReports;

        /// <summary>
        /// Reports, once per page, that a graphics-state parameter was read, stored and not
        /// honoured — the SVG half of the pair the raster interpreter reports.
        ///
        /// <para>The scope differs from the raster path's on purpose and the difference is
        /// observable: this one is per <i>page</i> (the shared registry reaches every child writer)
        /// while <c>ContentStreamInterpreter</c>'s is per <i>content stream</i>, so a form XObject
        /// that sets <c>/HT</c> again makes the raster path report twice and this one once. Assert
        /// on the SET of kinds each renderer raises, never on the count.</para>
        /// </summary>
        private void ReportIgnoredParameter(IgnoredGraphicsStateParameter which,
                                            string parameterValue = null)
        {
            if (Shared != null)
                IgnoredParameterReporting.ReportOnce(ref Shared.IgnoredParameterReports, which,
                                                     "SVG: ", parameterValue);
            else
                IgnoredParameterReporting.ReportOnce(ref _localIgnoredParameterReports, which,
                                                     "SVG: ", parameterValue);
        }

        /// <summary>
        /// Stores the rendering intent (the <c>ri</c> operator or ExtGState <c>/RI</c>). Every intent
        /// is honoured, so none is reported.
        /// </summary>
        /// <remarks>
        /// The counterpart of <c>ContentStreamInterpreter.SetRenderingIntent</c>, and it exists so
        /// the operator and the ExtGState key cannot be covered by one path and dropped by the
        /// other, which is exactly what happened here: <c>/RI</c> was stored and <c>ri</c> fell
        /// through a bare <c>break</c>. Both renderers now <b>honour</b> the intent by selecting the
        /// profile's matching table, so reporting it as ignored would be false on this side too.
        /// </remarks>
        private void SetRenderingIntent(string intent) => _gs.RenderingIntent = intent;

        /// <summary>
        /// The graphics state's rendering intent as the enum the ICC evaluator selects tables with —
        /// the SVG counterpart of <c>ContentStreamInterpreter.CurrentIntent</c>.
        /// </summary>
        private IccRenderingIntent CurrentIntent
            => IccRenderingIntents.Of(_gs.RenderingIntent, _gs.BlackPointCompensation);

        /// <summary>
        /// The intent an image XObject is drawn under — its own <c>/Intent</c> beats the graphics
        /// state's (§8.9.5.2 Table 89). The rule is shared with the raster interpreter.
        /// </summary>
        private IccRenderingIntent ImageIntent(PdfDictionary imageDict)
            => IccRenderingIntents.ForImage(_doc, imageDict, CurrentIntent);

        /// <summary>
        /// Reports an overprint diagnostic once per reason for the whole page.
        /// </summary>
        private void ReportOverprintOnce(OverprintReportReason reason,
                                         RenderDiagnosticKind kind, string detail)
        {
            if (Shared != null)
                OverprintReporting.ReportOnce(ref Shared.OverprintReports, reason, kind, detail);
            else
                OverprintReporting.ReportOnce(ref _localOverprintReports, reason, kind, detail);
        }

        /// <summary>
        /// Reports, once per page, that the file asked for overprint on an operation this exporter
        /// does not simulate. The wording is <see cref="OverprintReporting.Describe"/>'s, shared
        /// with the raster path so the two cannot describe the same gap differently.
        /// </summary>
        private void ReportOverprintSkipped(OverprintReportReason reason)
            => ReportOverprintOnce(reason, RenderDiagnosticKind.OverprintNotSimulated,
                "SVG: " + OverprintReporting.Describe(reason) +
                " It painted normally. Reported once per reason per page.");

        /// <summary>
        /// The <c>style</c> attribute a painting element carries — its blend mode, or the overprint
        /// approximation — as <c> style="…"</c> with its leading space, or the empty string.
        /// </summary>
        /// <param name="fill">Whether the element paints its interior.</param>
        /// <param name="stroke">Whether the element paints a pen along its outline.</param>
        /// <remarks>
        /// <para><b>One decision for paths, <c>&lt;text&gt;</c> and glyph outlines.</b> It lived on
        /// the path emitter alone, and text reported itself as "a renderer that composites no text
        /// per colorant" — true, and a divergence rather than a shared gap, because the raster path
        /// did composite text. Text and paths are the same painting operation to §11.7.4.2, so they
        /// take the same attribute from the same place.</para>
        /// <para>Overprint is simulated on exactly the raster path's terms: only when the caller
        /// opts in, only under <c>/OPM 1</c> (the nonzero-overprint rule), and only for a DeviceCMYK
        /// source colour. SVG has no native overprint operator, so <c>multiply</c> is the
        /// PDF-viewer approximation, and it is <b>reported as an approximation</b> rather than left
        /// looking like parity: <c>multiply</c> equals §11.7.4.2's rule only where the source and
        /// the backdrop share no colorant, and keeps darkening where they do. Where the conditions
        /// fail, the first failing one is reported in the raster path's own order, so the two
        /// renderers name the same cause for the same file.</para>
        /// <para>A blend mode wins over overprint, as it always has here: an element can carry one
        /// <c>mix-blend-mode</c>, and the file's explicit one is the stronger claim.</para>
        /// </remarks>
        private string PaintStyleAttribute(bool fill, bool stroke)
        {
            if (_gs.BlendMode != null) return $" style=\"mix-blend-mode:{_gs.BlendMode};\"";
            if (!((fill && _gs.OverprintFill) || (stroke && _gs.OverprintStroke))) return string.Empty;

            bool sourceIsCmyk = (fill && _gs.FillIsCmyk) || (stroke && _gs.StrokeIsCmyk);
            int channels = fill ? _gs.FillOverprintChannels : _gs.StrokeOverprintChannels;
            bool spot = (fill && _gs.FillIsSpotColorant) || (stroke && _gs.StrokeIsSpotColorant);

            // §11.7.4.5: a colorant set overprints whatever /OPM says; only the DeviceCMYK rule
            // (§11.7.4.2) is gated on nonzero overprint mode. A set that writes all four process
            // colorants preserves nothing, so the normal paint is already exact and neither
            // renderer reports it — the raster path's AffectsOutput returns false there.
            bool modeAllows = channels != OverprintCompositor.DeviceCmykRule || _gs.OverprintMode == 1;
            if (Options.SimulateOverprint && sourceIsCmyk && modeAllows
                && (channels & OverprintCompositor.AllChannels) == OverprintCompositor.AllChannels)
                return string.Empty;

            if (Options.SimulateOverprint && sourceIsCmyk && modeAllows)
            {
                ReportOverprintOnce(OverprintReportReason.MultiplyApproximation,
                    RenderDiagnosticKind.OverprintApproximated,
                    "SVG: overprint approximated with mix-blend-mode:multiply. This diverges " +
                    "from the raster path wherever the source and the backdrop share a " +
                    "colorant — 50% cyan over 50% cyan composites to 50% cyan under " +
                    "§11.7.4.2 (the source's nonzero cyan replaces the backdrop's) and to " +
                    "75% under multiply. Reported once per page.");
                return " style=\"mix-blend-mode:multiply;\"";
            }

            // A pattern is its own reason and comes first: the ink is the pattern cell's colour, so
            // there is no source colorant set to preserve, and calling that "non-CMYK" would send a
            // caller looking at the wrong thing. "Is a pattern actually being painted?", not "is
            // the space /Pattern?" — the raster path reports this reason from inside
            // RenderTilingPattern, which it reaches only when the flag AND the name agree.
            ReportOverprintSkipped(
                (fill   && SelectedPatternName(stroke: false) != null) ||
                (stroke && SelectedPatternName(stroke: true)  != null)
                    ? OverprintReportReason.Pattern
                : !Options.SimulateOverprint ? OverprintReportReason.SimulationOff
                : spot                       ? OverprintReportReason.SpotColorant
                : _gs.OverprintMode != 1     ? OverprintReportReason.OverprintMode
                :                              OverprintReportReason.NonCmykSource);
            return string.Empty;
        }

        /// <summary>
        /// Render options, never null — <see cref="PdfRenderOptions.Default"/> when the caller
        /// supplied none. Propagated to every child writer, so a pattern cell or annotation
        /// appearance renders under the same policy as the page.
        /// </summary>
        internal PdfRenderOptions Options = PdfRenderOptions.Default;

        private void OpQSave()
        {
            if (_gsStack.Count >= PdfLimits.MaxGraphicsStateStackDepth)
            {
                _skippedPushes++;
                if (!_stateDepthReported)
                {
                    _stateDepthReported = true;
                    RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                        $"SVG: graphics-state (q) nesting exceeded {PdfLimits.MaxGraphicsStateStackDepth} " +
                        "levels; further q operators ignored to bound memory.");
                }
                return;
            }
            _gsStack.Push(_gs.Clone());
            // The counters are per-scope — that is what OpQ's close loop assumes and what the
            // field comments say — so the live state starts the new scope at zero. Without this
            // the count accumulated across scopes and a nested clip made the OUTER Q emit
            // </g> for groups the inner Q had already closed, unbalancing the document.
            _gs.ClipGroupCount = 0;
            _gs.MarkedContentGroupCount = 0;
        }

        private void OpQ()
        {
            // A W/W* whose painting operator never arrived dies with the scope, as in the
            // raster interpreter (`case Restore: _pendingClip = false;`).
            _pendingClip = false;
            if (_skippedPushes > 0) { _skippedPushes--; return; } // matches a refused push
            if (_gsStack.Count == 0) return;
            // Close any SVG groups opened in the current q/Q scope.
            int close = _gs.ClipGroupCount + _gs.MarkedContentGroupCount;
            for (int i = 0; i < close; i++) _sb.Append("</g>\n");
            _unclosedGroups -= close;

            // §9.3.1: the text matrices are not graphics state, so Q leaves them where the text has
            // reached, as the raster renderer's PopState does.
            float[] tm = _gs.Tm, tlm = _gs.Tlm;
            _gs = _gsStack.Pop();
            _gs.Tm = tm;
            _gs.Tlm = tlm;
        }

        // ---- Soft mask ----

        /// <summary>
        /// The soft mask's <c>/BC</c> backdrop as a CSS colour, or <c>null</c> when absent.
        ///
        /// <para>Through the shared reader, which resolves the space the spec actually names —
        /// the mask group's <c>/G /Group /CS</c> — rather than guessing it from the component
        /// count — and the raster path reads it the same way, so the two cannot invert each other on a
        /// four-component backdrop.</para>
        /// </summary>
        private string BackdropColour(PdfDictionary smDict)
        {
            SKColor? bc = PdfColor.SoftMaskBackdrop(smDict, _doc);
            return bc.HasValue ? PdfColor.ToCssRgb(bc.Value) : null;
        }

        /// <summary>
        /// Builds an SVG &lt;mask&gt; element from an SMask dictionary and returns its id.
        /// The mask contents are rendered into a temporary StringBuilder via a child SvgContentWriter.
        /// Returns null if the SMask cannot be processed.
        /// </summary>
        private string BuildSvgSoftMask(PdfDictionary smDict)
        {
            if (Depth >= PdfLimits.MaxRenderRecursionDepth) return null;

            // /G is the transparency group Form XObject for the mask.
            var gObj = smDict[PdfName.G];
            var gStream = _doc.ResolveObject(gObj) as PdfStream;
            if (gStream == null) return null;

            // A mask group is a form XObject execution and doubles exactly as one: a group whose
            // content is `/M2 gs /M2 gs` repeats no name and exceeds no depth cap. The raster path
            // charges this inside RenderToBitmap; charging only there is what let the export run
            // the chain unbudgeted.
            byte[] data;
            try { data = gStream.GetDecodedData(); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed,
                    "SVG soft-mask transparency-group stream decode failed", ex);
                return null;
            }
            if (Shared?.FormBudget != null && !Shared.FormBudget.TryChargeFormInvocation(data.Length)) return null;

            var maskRes = gStream[PdfName.Resources] is PdfObject ro
                ? _doc.ResolveObject(ro) as PdfDictionary ?? _resources
                : _resources;

            int maskId = NextId();
            string id = $"smask{maskId}";

            // §11.6.5.2: /G is a transparency-group FORM XObject, so §8.10.2 applies to it in
            // full — its /Matrix concatenates onto the CTM in effect at the `gs`, and its /BBox
            // clips its content. Both were ignored: a mask group declaring
            // `/Matrix [2 0 0 2 0 0]` painted at half size, and one whose content ran outside
            // its declared box masked with pixels the box excludes.
            float[] maskCtm = _gs.CTM;
            var matArr = PdfResolve.Array(_doc, gStream, PdfName.Matrix);
            if (matArr != null && matArr.Count >= 6)
            {
                float[] fm =
                {
                    GetArrFloat(matArr, 0), GetArrFloat(matArr, 1),
                    GetArrFloat(matArr, 2), GetArrFloat(matArr, 3),
                    GetArrFloat(matArr, 4), GetArrFloat(matArr, 5)
                };
                maskCtm = Compose(fm, maskCtm);
            }

            // Render the mask group into SVG content, then wrap in <mask>.
            var maskSb = new StringBuilder();
            var child = new SvgContentWriter(maskSb, _doc, maskRes, _pageW, _pageH);
            child.InheritFrom(this);
            child._gs = _gs.Clone();
            // The mask's group is rendered on its own, as the raster's offscreen render is: the
            // alpha, blend mode and mask of the state that set it apply once, where the mask is
            // used, not again inside its content — which would mask at the alpha squared.
            child._gs.FillAlpha   = 1f;
            child._gs.StrokeAlpha = 1f;
            child._gs.BlendMode   = null;
            child._gs.SvgMaskId   = null;
            // Copied, not aliased: with no /Matrix, maskCtm IS this writer's own array.
            child._gs.CTM = CopyArr(maskCtm);
            // §8.7.3.1: a pattern used in the mask's group maps through the group's space.
            child._gs.PatternBase = CopyArr(maskCtm);
            child.Execute(data);

            // The /BBox is in FORM space, so the clip is mapped through /Matrix ∘ CTM — the
            // same mapping RunContentStreamAsForm performs, and the reason maskCtm is built
            // before the child runs rather than concatenated inside it.
            // One emitter, in EmitBBoxClip. Unlike the shading and form-XObject callers this one
            // does NOT open the <g> here: the id is held and applied to a group opened below, which
            // carries the mask's other attributes too.
            var bboxArr = PdfResolve.Array(_doc, gStream, PdfName.BBox);
            string bboxClipId = EmitBBoxClip(bboxArr, maskCtm, "smclip", openGroup: false);

            string maskType = (PdfResolve.Name(_doc, smDict, PdfName.S))?.Value ?? PdfSoftMaskNames.Alpha;
            bool isLuminosity = maskType == PdfSoftMaskNames.Luminosity;

            // An SVG <mask> takes its value from the LUMINANCE of its children by default, so without
            // `mask-type:alpha` a /S /Alpha mask would render as if it were a luminosity mask — a black
            // opaque shape masking everything out instead of nothing. `mask-type:alpha` selects
            // the alpha channel (CSS Masking / SVG 2).
            string typeStyle = isLuminosity ? "" : " style=\"mask-type:alpha\"";
            // §11.6.5.2's luminosity is taken of the group's device colours with no gamma
            // compensation — BT.601 luma of the sRGB values, as the raster takes it — so the mask
            // interpolates in sRGB: under linearRGB a viewer linearises first and a mid-grey mask
            // passes about a fifth where the raster passes a half.
            string colorAttr = isLuminosity ? " color-interpolation=\"sRGB\"" : "";

            // /BC — the backdrop the mask group composites onto, which is what the mask
            // evaluates to wherever the group paints nothing (§11.6.5.2). SVG's default there
            // is black, i.e. fully masked out; painting the backdrop as the first child gives
            // the declared value instead. Only meaningful for a luminosity mask — an alpha
            // mask's backdrop is transparent by definition.
            string backdrop = isLuminosity ? BackdropColour(smDict) : null;

            // maskUnits="userSpaceOnUse" over the page box. Without it the region defaults to
            // objectBoundingBox — the REFERENCING element's box grown by 10% — which makes the
            // mask's extent depend on what it is attached to. Two consequences, both real:
            // a zero-extent box (a horizontal stroked line, fill="none") disables rendering of
            // the element outright per the SVG mask spec, and the /BC backdrop below is a page
            // rect that would be cropped to whatever element happened to reference it. A soft
            // mask is a property of the graphics state, not of one shape, so its region is the
            // page — and that also makes it identical whether it lands on an element or (see
            // RunContentStreamAsForm) on a transparency-group wrapper.
            //
            // The origin is the page box's lower-left corner, not (0,0): RenderPage's outer
            // group applies translate(-pdfX,-pdfY), so a CropBox with a non-zero origin leaves
            // content at raw PDF coordinates. The /BC rect takes the same origin, which is a
            // fix in its own right — it was page-sized but anchored at 0,0.
            float regionX = Shared?.PageX ?? 0f, regionY = Shared?.PageY ?? 0f;
            string region = $" maskUnits=\"userSpaceOnUse\" x=\"{F(regionX)}\" y=\"{F(regionY)}\"" +
                            $" width=\"{F(_pageW)}\" height=\"{F(_pageH)}\"";

            _sb.Append($"<defs><mask id=\"{id}\"{region}{colorAttr}{typeStyle}>\n");
            if (backdrop != null)
                _sb.Append($"<rect x=\"{F(regionX)}\" y=\"{F(regionY)}\"" +
                           $" width=\"{F(_pageW)}\" height=\"{F(_pageH)}\"" +
                           $" fill=\"{backdrop}\"/>\n");
            // The /BBox clips the GROUP, never the backdrop: §11.6.5.2 makes the mask value
            // outside the group's bounds the one derived from /BC, so cropping the backdrop
            // with it would erase exactly the area /BC exists to define.
            if (bboxClipId != null) _sb.Append($"<g clip-path=\"url(#{bboxClipId})\">\n");
            _sb.Append(maskSb.ToString());
            if (bboxClipId != null) _sb.Append("</g>\n");
            _sb.Append($"</mask></defs>\n");

            // /TR maps each computed mask value through a function before use. There is no SVG
            // primitive for it (feComponentTransfer applies to a filter, not a mask), so the
            // mask is exported untransformed — reported, never silently dropped.
            var trEntry = _doc.ResolveObject(smDict[PdfName.TR]);
            if (trEntry != null && trEntry is not PdfNull &&
                !(trEntry is PdfName trName && trName.Value == "Identity"))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                    "SVG: soft-mask /TR transfer function is not expressible in SVG; " +
                    "the mask was exported without it.");
            }

            return id;
        }

        // ---- Color space ----
    }
}
