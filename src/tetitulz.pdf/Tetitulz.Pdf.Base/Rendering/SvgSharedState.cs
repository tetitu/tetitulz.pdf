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
    /// Per-page <c>&lt;defs&gt;</c>/<c>&lt;use&gt;</c> registry, deduplicating repeated images and
    /// glyphs across every writer working on one page.
    /// </summary>
    internal sealed class SvgSharedState
    {
        // XObject object number → assigned SVG element id (for repeated images).
        public readonly Dictionary<long, string> ImageDefs = new Dictionary<long, string>();
        // Glyph outline path-data (in unit/glyph space) → assigned SVG <path> id. Keying on the
        // serialized outline means identical glyphs (every 'e', every space-less repeat) share a
        // single <path>, regardless of which font produced them.
        public readonly Dictionary<string, string> GlyphDefs = new Dictionary<string, string>();
        /// <summary>
        /// The streams whose decode failed on this page — an image, a tiling cell — so a later reference
        /// writes nothing without decoding again; the raster context keeps the same memo
        /// (<c>PdfRenderContext.FailedDecodes</c>). By reference identity, one entry a stream.
        /// </summary>
        public readonly HashSet<Core.Objects.PdfStream> FailedDecodes = new HashSet<Core.Objects.PdfStream>(Core.Objects.PdfReferenceEquality.Instance);

        /// <summary>
        /// Font program object number → the CSS family its <c>@font-face</c> was minted under,
        /// or null when the program was examined and refused.
        /// </summary>
        /// <remarks>
        /// <b>Both halves of that are load-bearing.</b> A page draws thousands of runs from a
        /// handful of fonts and a font program is megabytes, so emitting the rule once per page is
        /// the difference between an SVG and an unusable one — and it must be once per <i>page</i>,
        /// not per writer, because every form XObject, pattern cell and annotation appearance is a
        /// child writer that would otherwise re-emit the same base64. Caching the <b>refusal</b>
        /// matters for the same reason in reverse: a bare-CFF font would otherwise be decoded,
        /// sniffed and reported on every run that draws it.
        /// </remarks>
        public readonly Dictionary<long, string> FontFaceDefs = new Dictionary<long, string>();

        /// <summary>
        /// Font descriptor → the program <see cref="SvgFontEmbedder.TryRead"/> read from it, usable
        /// or refused: read once a page.
        /// </summary>
        /// <remarks>
        /// <see cref="FontFaceDefs"/> is keyed by the program's object number, which only the read
        /// knows, so every writer read the program — decoded, sniffed, a Type 1 converted and
        /// wrapped — before the registry answered: once per form invocation drawing the font.
        /// </remarks>
        public readonly Dictionary<PdfDictionary, SvgFontEmbedder.Program> FontPrograms =
            new Dictionary<PdfDictionary, SvgFontEmbedder.Program>(PdfReferenceEquality.Instance);

        /// <summary>
        /// The page's one font cache, which every writer draws through — as the raster renderer
        /// hands one cache to every nested interpreter. The page render owns it and disposes it.
        /// </summary>
        /// <remarks>
        /// Each writer built its own, so a form invoked a hundred thousand times parsed its fonts
        /// — the dictionary read, the program extracted, the widths built — as many times. The
        /// cache keys a font by its resources dictionary or its object number, so writers with
        /// different <c>/Resources</c> share it as the raster interpreters do.
        /// </remarks>
        public FontCache Fonts;
        // Monotonic counter feeding every generated def id, shared across all child writers.
        public int IdCounter;

        /// <summary>
        /// The page's aggregate form-XObject invocation budget. Lives here for the same reason
        /// <see cref="IdCounter"/> does — <c>Shared</c> is already propagated to every child writer,
        /// and a budget that a child writer restarted would bound nothing at all, which is exactly
        /// the shape of the attack it exists to stop.
        /// </summary>
        /// <remarks>
        /// The raster renderer carries the identical budget on <c>ContentStreamInterpreter</c>. Each
        /// side holds its own <b>instance</b> and neither calls the other's statics; what they share
        /// is the type, which has no static fields and therefore no type initializer.
        /// </remarks>
        public RenderBudget FormBudget;

        /// <summary>
        /// The page box's lower-left corner in PDF user space. Every element this writer emits
        /// carries raw PDF coordinates, because <see cref="PdfSvgRenderer.RenderPageToSvg(Stream,int,PdfRenderOptions)"/>'s outer group applies
        /// <c>translate(-pdfX,-pdfY)</c> — so the visible page occupies
        /// <c>[PageX, PageX+pageW] × [PageY, PageY+pageH]</c> in the coordinate system every
        /// <c>userSpaceOnUse</c> reference resolves against, NOT <c>[0, pageW]</c>.
        ///
        /// <para>Lives here rather than on <c>SvgWriter</c> because <c>Shared</c> is already
        /// propagated to every child writer (form XObject, pattern cell, soft mask, Type 3
        /// glyph, annotation appearance) and the origin has to reach all of them. A writer
        /// constructed without a registry falls back to (0, 0), which is the common case.</para>
        /// </summary>
        public float PageX;

        /// <inheritdoc cref="PageX"/>
        public float PageY;

        /// <summary>
        /// Which overprint diagnostics this page has already emitted — one bit per reason, so a
        /// page that hits several says each of them once.
        /// </summary>
        /// <remarks>
        /// <para><b>It lives here rather than on the writer, and that matters.</b>
        /// A one-shot field on <c>SvgContentWriter</c> is per <i>writer</i>, and a page runs many:
        /// every form XObject, pattern cell, soft mask, Type 3 glyph and annotation appearance is a
        /// child writer with its own fields. On the writer, an overprinting page whose content also
        /// invokes a form would report the same thing twice, and a page built entirely out of form
        /// XObjects once per form.</para>
        /// <para>A writer constructed without a registry falls back to its own copy, exactly as
        /// <c>NextId</c> does — the defensive case, not the common one.</para>
        /// </remarks>
        public OverprintReportReason OverprintReports;

        /// <summary>
        /// Which graphics-state parameters this page has already reported as read-and-not-honoured
        /// — one bit per parameter, scoped exactly like <see cref="OverprintReports"/>.
        /// </summary>
        /// <remarks>
        /// The SVG writer stored <c>/AIS</c>, <c>/TK</c>, <c>/RI</c>, <c>/FL</c> and <c>/SA</c> and
        /// reported none of them, while the raster path reported four (<c>/HT</c>, <c>/HTO</c>,
        /// <c>/SA</c>, <c>ri</c>/<c>/RI</c>) plus <c>/BG</c>–<c>/UCR</c>. That is the recurring
        /// defect shape in this repository — one renderer telling a prepress caller what it dropped
        /// while the other stays silent about the same file — so the set is mirrored here.
        /// <c>/FL</c> is deliberately excluded: flatness is a curve-approximation hint, not a
        /// colour decision, so reporting it would be noise.
        /// </remarks>
        public IgnoredGraphicsStateParameter IgnoredParameterReports;
    }
}
