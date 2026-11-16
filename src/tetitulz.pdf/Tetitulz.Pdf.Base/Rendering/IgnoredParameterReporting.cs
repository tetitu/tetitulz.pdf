// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A graphics-state parameter (§8.4.5, Table 58) that both renderers read, store and do not
    /// honour — one bit per parameter, so a page that sets several says each of them once.
    /// </summary>
    /// <remarks>
    /// <para>Modelled on <see cref="OverprintReportReason"/>, and for the same reason: a single
    /// <c>bool</c> per parameter is what the raster path used, and mirroring four of them into the
    /// SVG path would have meant four more fields there plus four more copies of the wording. The
    /// storage stays with the caller — a field on <c>ContentStreamInterpreter</c> (once per content
    /// stream) or <see cref="SvgSharedState"/> (once per page) — because the two renderers scope it
    /// differently and neither scope belongs to this type.</para>
    /// <para><b>DeviceN <c>/Subtype /NChannel</c> is deliberately not in this table.</b> It is a
    /// colour-space attribute resolved in <c>ContentStreamInterpreter.Color</c>, not a graphics-state
    /// parameter, and the SVG writer has no counterpart site to mirror it into. It keeps its own
    /// one-shot flag.</para>
    /// </remarks>
    [Flags]
    internal enum IgnoredGraphicsStateParameter
    {
        /// <summary>Nothing reported yet.</summary>
        None = 0,

        /// <summary>The halftone (<c>/HT</c>) or its origin (<c>/HTO</c>), §10.5.</summary>
        Halftone = 1 << 0,

        /// <summary>Stroke adjustment (<c>/SA</c>), §10.7.5.</summary>
        StrokeAdjustment = 1 << 1,

        /// <summary>Black generation and undercolour removal (<c>/BG</c>, <c>/BG2</c>,
        /// <c>/UCR</c>, <c>/UCR2</c>), §10.3.4–10.3.5.</summary>
        BlackGenerationUndercolor = 1 << 3,

        /// <summary>Text knockout (<c>/TK</c>) in effect where the SVG writer cannot emulate it,
        /// §9.3.8. Reported by the SVG writer only — the raster path honours it.</summary>
        TextKnockout = 1 << 5,

    }

    /// <summary>
    /// The one-shot gate and the shared wording behind every "read, stored and not honoured"
    /// graphics-state diagnostic, reached from both renderers.
    /// </summary>
    /// <remarks>
    /// A leaf by construction: no static fields, so no type initializer. That is the property
    /// CLAUDE.md's "share downward into a leaf, never sideways between peers" rule is about —
    /// pointing one renderer at the other's static once crashed the Base test host part-way
    /// through a parallel run by creating a type-initialization edge.
    /// </remarks>
    internal static class IgnoredParameterReporting
    {
        /// <summary>
        /// Reports <paramref name="which"/> the first time it is seen in the scope
        /// <paramref name="reported"/> represents, and never again for that parameter.
        /// </summary>
        /// <param name="reported">The caller's one-shot set; scoped by the caller.</param>
        /// <param name="which">The parameter that was read and dropped.</param>
        /// <param name="renderer">Prefix identifying the renderer, e.g. <c>"SVG: "</c>. The raster
        /// path passes the empty string, which is what its diagnostics have always looked like.</param>
        /// <param name="parameterValue">The value the file set, where the wording names it.</param>
        internal static void ReportOnce(ref IgnoredGraphicsStateParameter reported,
                                        IgnoredGraphicsStateParameter which,
                                        string renderer,
                                        string parameterValue = null)
        {
            if ((reported & which) != 0) return;
            reported |= which;
            RenderDiagnostics.Report(KindFor(which), renderer + Describe(which, parameterValue));
        }

        /// <summary>
        /// Whether an ExtGState entry actually asks for something neither renderer does — as
        /// opposed to naming the value they already produce.
        /// </summary>
        /// <remarks>
        /// <para><b>Presence is not deviation, and four of the six entries in this table shipped
        /// as though it were.</b> <c>/HT /Default</c>, <c>/BG2 /Default</c> and
        /// <c>/UCR2 /Default</c> ask for the device's default behaviour (§10.5.1, §10.3.4-10.3.5:
        /// the name <c>/Default</c> is legal for exactly these three and means "revert"), and
        /// <c>/SA false</c> asks for no stroke adjustment (§10.7.5). All four describe the state
        /// both renderers are already in, so reporting them says a parameter was dropped when
        /// nothing was.</para>
        /// <para>This is the same mistake the <c>/TK</c> entry avoids by testing
        /// for <c>true</c> — a graphics-state key whose default value is spelled out is the
        /// ordinary way a producer resets state between blocks, so a presence test fires on files
        /// that deviate in no way at all. It is the noise-floor half of the point of
        /// <see cref="RenderDiagnosticKind.RenderParameterIgnored"/>: a diagnostic that fires on
        /// conformant input teaches a prepress caller to ignore the channel.</para>
        /// <para><c>/BG</c> and <c>/UCR</c> (the pre-1.3 spellings) admit no <c>/Default</c> — they
        /// are always functions — so their presence really is a deviation. <c>/HTO</c> is a
        /// halftone origin whose default is device-dependent rather than a named value, so it is
        /// reported whenever present.</para>
        /// </remarks>
        /// <param name="doc">Resolves indirect references; <c>/HT</c> in particular is usually a
        /// reference, since a halftone is a dictionary or a stream.</param>
        /// <param name="extGs">The ExtGState dictionary being applied.</param>
        /// <param name="which">The parameter to test. Only the four presence-tested parameters are
        /// meaningful here; anything else returns <c>true</c>, leaving the caller's own condition
        /// in charge.</param>
        internal static bool Deviates(PdfDocument doc, PdfDictionary extGs,
                                      IgnoredGraphicsStateParameter which)
        {
            if (extGs == null) return false;
            switch (which)
            {
                case IgnoredGraphicsStateParameter.Halftone:
                    return (extGs[PdfName.HT] != null && !IsDefaultName(doc, extGs, PdfName.HT))
                        || extGs[PdfName.HTO] != null;

                case IgnoredGraphicsStateParameter.StrokeAdjustment:
                    // Only /SA true deviates; /SA false IS what both renderers do.
                    return PdfResolve.Bool(doc, extGs, PdfName.SA)?.Value == true;

                case IgnoredGraphicsStateParameter.BlackGenerationUndercolor:
                    return extGs[PdfName.BG] != null
                        || extGs[PdfName.UCR] != null
                        || (extGs[PdfName.BG2] != null && !IsDefaultName(doc, extGs, PdfName.BG2))
                        || (extGs[PdfName.UCR2] != null && !IsDefaultName(doc, extGs, PdfName.UCR2));

                default:
                    return true;
            }
        }

        /// <summary>Whether <paramref name="key"/> resolves to the name <c>/Default</c>.</summary>
        private static bool IsDefaultName(PdfDocument doc, PdfDictionary dict, PdfName key)
            => PdfResolve.Name(doc, dict, key)?.Value == "Default";

        /// <summary>
        /// Which diagnostic kind a parameter belongs under. Black generation / undercolour removal
        /// has its own kind, and keeping the split here is what stops a mirror from quietly re-filing it under the general one.
        /// </summary>
        internal static RenderDiagnosticKind KindFor(IgnoredGraphicsStateParameter which)
            => which == IgnoredGraphicsStateParameter.BlackGenerationUndercolor
                ? RenderDiagnosticKind.BlackGenerationUndercolorIgnored
                : RenderDiagnosticKind.RenderParameterIgnored;

        /// <summary>
        /// The renderer-neutral sentence for a parameter, so the raster and SVG paths cannot
        /// describe the same gap two different ways — the divergence this repository keeps finding.
        /// </summary>
        /// <remarks>
        /// The wording is neutral on purpose. The raster copies these sentences replaced said
        /// things like "the rasteriser owns pixel-grid fitting", which is a true statement about
        /// Skia and a meaningless one about an SVG document; a mirrored copy would either have
        /// repeated it or quietly reworded it, and a reworded copy is a copy that has already
        /// started to drift.
        /// </remarks>
        internal static string Describe(IgnoredGraphicsStateParameter which,
                                        string parameterValue = null) => which switch
        {
            IgnoredGraphicsStateParameter.Halftone =>
                "Halftone (/HT, /HTO, §10.5) present; ignored — both renderers produce " +
                "continuous-tone sRGB, where a halftone screen has nothing to control.",
            IgnoredGraphicsStateParameter.StrokeAdjustment =>
                "Stroke adjustment (/SA, §10.7.5) present; ignored — it is a device pixel-grid " +
                "hint, and neither renderer fits stroke geometry to a grid.",
            IgnoredGraphicsStateParameter.BlackGenerationUndercolor =>
                "Black-generation/undercolour-removal (/BG, /BG2, /UCR, /UCR2, §10.3.4-10.3.5) " +
                "present; ignored — they decide only how the K channel is synthesised in a CMYK " +
                "compositing pipeline, and both renderers composite in sRGB.",
            // §9.3.8 and Table 52: /TK true makes the text object a knockout group, so each glyph
            // composites with the object's INITIAL backdrop rather than with the glyphs painted
            // before it — visible wherever glyphs overlap under a non-Normal blend mode or a
            // fractional alpha. Painting glyph-by-glyph with SrcOver is /TK FALSE behaviour, so it
            // is `true` that deviates. (An earlier plan had this the other way round and would
            // have reported the one value we get right.)
            //
            // ONLY THE SVG WRITER REPORTS THIS, and only where it cannot emulate it. The raster path
            // opens a knockout layer for the text object (ContentStreamInterpreter.BeginTextKnockout);
            // SVG has no knockout model, but glyphs sharing one alpha are emulated exactly as opaque
            // glyphs inside <g opacity> (SvgContentWriter.BeginTextKnockout). What is left — a soft
            // mask, a blend mode, a fill and a stroke at two alphas, or an alpha, blend mode or
            // soft mask changed inside the text object — is reported. It is decided on the state at
            // the first painting glyph rather than on an explicit /TK token, because true is the
            // parameter's initial value.
            IgnoredGraphicsStateParameter.TextKnockout =>
                "/TK true (§9.3.8): the text object is not composited as a knockout group, so " +
                "overlapping glyphs composite with each other rather than each with the object's " +
                "initial backdrop. SVG has no knockout compositing model: glyphs sharing one " +
                "alpha are emulated, but not under a soft mask or a blend mode, nor a fill and a " +
                "stroke at different alphas, nor after the alpha changes inside the text object. " +
                "The raster renderer honours /TK.",
            _ => "A graphics-state parameter was read and not honoured.",
        };
    }
}
