// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Categories of render-time anomaly that the SkiaSharp + SVG pipelines can
    /// surface to a host application.  Each enum value corresponds to a
    /// <c>catch</c> or no-op fallback in the rendering code that reports rather than stays silent.
    /// </summary>
    public enum RenderDiagnosticKind
    {
        /// <summary>An image XObject's data could not be decoded; image was skipped.</summary>
        ImageDecodeFailed,

        /// <summary>
        /// An image was decoded at less than its native resolution because it is drawn into a much
        /// smaller area and <c>PdfRenderOptions.DownsampleImagesToTarget</c> is on.
        /// </summary>
        /// <remarks>
        /// Reported because it <b>changes output pixels</b>. Without it a caller comparing two
        /// renders could not tell a downsampled decode from a codec difference — and this codebase
        /// already treats the silent DPI reduction above <c>MaxImagePixelArea</c> as a defect for
        /// exactly that reason.
        /// </remarks>
        ImageDownsampled,

        /// <summary>
        /// The rendered page was produced at a lower resolution than the caller asked for, because
        /// the requested pixel dimensions exceeded <c>PdfLimits.MaxImageDimension</c> or
        /// <c>PdfLimits.MaxImagePixelArea</c>.
        /// </summary>
        /// <remarks>
        /// <para>The clamp itself is correct and must stay — it is what stops an absurd
        /// <c>/MediaBox</c> from being an out-of-memory allocation. It must not be <b>silent</b>:
        /// A4 at 1200 DPI is 9 917 × 14 033 = 139 MP, so a caller asking for 1200 receives ≈1017,
        /// and a reduced-resolution PNG is still a perfectly valid non-empty PNG.</para>
        /// <para>Sibling of <see cref="ImageDownsampled"/>, and reported for the same reason: it
        /// <b>changes output pixels</b>, and "the renderer reduced this" must be distinguishable
        /// from "this is what the page contains". The detail carries the requested and effective
        /// dimensions and the scale factor applied.</para>
        /// </remarks>
        RenderResolutionReduced,

        /// <summary>
        /// A transparency group could not be composited exactly as its <c>/I</c> (isolated),
        /// <c>/K</c> (knockout) or <c>/CS</c> (group colour space) attributes ask, and was
        /// approximated. Always reported rather than silently downgraded, because the difference is
        /// a real colour difference on the page.
        ///
        /// <para><c>/CS</c> is classified by one predicate (<c>PdfColor.TryGetGroupBlendSpace</c>)
        /// at a <c>Do</c>-invoked group and at the page's own group. The raster renderer composites
        /// a grey or CMYK group in its space (<c>GroupBlenders</c>) and reports only a space
        /// §11.3.4 does not admit (Lab, Indexed, …), which the group then inherits past; the SVG
        /// export composites in sRGB and reports every non-RGB space, so a CMYK group's blend
        /// modes and alpha run on RGB components there. Two scope rules are load-bearing: an
        /// <b>absent</b> <c>/CS</c> means "inherit" (§11.6.6) and is not reported, and a <b>soft
        /// mask's</b> <c>/G</c> group is never reported at all — §11.6.5.2 wants a luminosity mask
        /// group in <c>/DeviceGray</c>, so reporting it would fire on essentially every soft mask
        /// in existence.</para>
        /// </summary>
        TransparencyGroupApproximated,
        /// <summary>
        /// The file asked for overprint (<c>/OP</c> or <c>/op</c>) on an operation the simulator
        /// does not cover, so it painted normally. Reported rather than ignored so a caller cannot
        /// read "no overprint applied" as "the file does not overprint".
        /// </summary>
        OverprintNotSimulated,
        /// <summary>A Type 3 glyph CharProc stream could not be decoded; glyph was skipped.</summary>
        Type3GlyphDecodeFailed,
        /// <summary>A tiling pattern's content stream could not be decoded; pattern fell back to nothing.</summary>
        TilingPatternDecodeFailed,
        /// <summary>A Coons / tensor patch shader threw; renderer fell back to averaged-mesh.</summary>
        PatchShaderFailed,
        /// <summary>A Gouraud mesh shader threw on pixel-rasterise; rendered region may be incomplete.</summary>
        MeshShaderFailed,
        /// <summary>An ICC colour-transform threw; pixels were left untouched.</summary>
        IccTransformFailed,
        /// <summary>A DeviceN/NChannel image with &gt; 4 components fell back to grayscale-averaging.</summary>
        DeviceNFallback,
        /// <summary>A Pattern colour space was used as an <c>sc</c>/<c>scn</c> operand without a pattern name; opaque black was used.</summary>
        PatternOperandNoName,
        /// <summary>
        /// A pattern <em>was</em> named and could not be turned into a paint — the pattern object is
        /// missing, its shading type is one the renderer cannot express, or building it failed — so
        /// nothing was painted for that operation.
        /// <para>Reported because the alternative is worse and was the SVG renderer's behaviour: it
        /// fell through to whatever solid colour the graphics state happened to hold, which paints a
        /// confidently wrong result rather than an obviously missing one.</para>
        /// </summary>
        PatternFillUnresolved,
        /// <summary>
        /// A pattern <em>was</em> painted, but not exactly as the file describes it.
        ///
        /// <para>Distinct from <see cref="PatternFillUnresolved"/>, which means nothing was painted
        /// at all. Among its cases: a tiling pattern whose <c>/BBox</c> is so much larger than its
        /// <c>/XStep</c>/<c>/YStep</c> — deliberately overlapping tiles (§8.7.3.1) — that one tile
        /// would need more than <see cref="TilingLattice.MaxCopies"/> copies of the cell. Both
        /// renderers then draw the cell once per step; the overlap is lost. A smaller overlap is
        /// drawn exactly, from <see cref="TilingLattice"/>'s copies.</para>
        /// </summary>
        PatternApproximated,
        /// <summary>An annotation's <c>/Subtype</c> has no fallback renderer; placeholder rect was drawn.</summary>
        AnnotationSubtypeUnsupported,
        /// <summary>An annotation finished rendering — informational, not an error.  Carries the annotation's <c>/NM</c> (unique name) when present in <c>detail</c>.</summary>
        AnnotationRendered,
        /// <summary>SKShader.CreateBitmap threw for a tiling-pattern tile (e.g. bitmap with no pixel data); pattern was skipped.</summary>
        TilingPatternShaderFailed,
        /// <summary>A content-stream operator threw an unhandled exception; the operator was skipped and rendering continued.</summary>
        ContentStreamError,
        /// <summary>
        /// A non-embedded CID font with a standard Adobe ordering (Japan1, GB1, CNS1, Korea1) has no /ToUnicode
        /// CMap and no registered predefined CMap.  CJK characters will not render correctly until
        /// <c>CMapParser.RegisterPredefinedCMap</c> is called with the matching Adobe CMap data.
        /// </summary>
        MissingPredefinedCMap,
        /// <summary>
        /// An embedded font program (/FontFile, /FontFile2, /FontFile3) could not be loaded or
        /// parsed; the renderer fell back to a system substitute font.  Glyph shapes / metrics
        /// may differ from the document author's intent.  <c>detail</c> carries the font resource name.
        /// </summary>
        FontLoadFailed,

        /// <summary>
        /// The graphics state set a black-generation (/BG, /BG2) or undercolor-removal
        /// (/UCR, /UCR2) function.  These only affect how the black channel is synthesised in a
        /// CMYK compositing pipeline; this renderer composites in sRGB, so they have no visible
        /// effect and are intentionally ignored.  Reported once so prepress workflows are aware.
        /// </summary>
        BlackGenerationUndercolorIgnored,

        /// <summary>
        /// A registered plugin failed while servicing a render: an
        /// <c>IRendererPlugin</c> threw or returned empty output, an
        /// <c>IImageDecodePlugin</c> threw, or an <c>IPostScriptPlugin</c> threw or returned
        /// unusable output. The built-in implementation is used instead, so
        /// rendering still succeeds — but a host that registered the plugin expecting it to
        /// take over needs to know it did not.
        /// </summary>
        PluginFailure,

        /// <summary>
        /// Embedded PostScript — a <c>/Subtype /PS</c> XObject (ISO 32000-1 §8.10.7) or the
        /// legacy inline <c>PS</c> operator — was found and no registered
        /// <c>IPostScriptPlugin</c> handled it. The XObject falls back to the hatched
        /// placeholder over its <c>/BBox</c> (nothing at all when it has none); the inline
        /// operator is ignored. Register an <c>IPostScriptPlugin</c> to render it.
        /// <para>Reported at most once per content stream for the inline operator, which can
        /// appear hundreds of times in one stream.</para>
        /// </summary>
        PostScriptUnsupported,

        /// <summary>
        /// A parameter the file set is read and understood but not honoured, so it was ignored: the
        /// graphics-state halftone (<c>/HT</c>, <c>/HTO</c>), stroke adjustment (<c>/SA</c>), text
        /// knockout (<c>/TK true</c>, §9.3.8, the SVG writer only), or a DeviceN
        /// <c>/Subtype /NChannel</c> attributes dictionary. Every rendering intent is honoured, and
        /// so is alpha-is-shape (<c>/AIS</c>) by the raster renderer; in SVG it changes no pixel.
        ///
        /// <para><b>Both renderers report the whole graphics-state set</b>, through the one wording
        /// table in <c>IgnoredParameterReporting</c>. The two <b>scopes</b>
        /// differ, and a caller comparing them must know it: at most once per content stream in the
        /// raster path (a form XObject re-reports) and once per <i>page</i> in the SVG path, where
        /// the shared registry reaches every child writer. Match on the kind and the detail, never
        /// on the count.</para>
        ///
        /// <para><b>For the boolean parameter the reported value is the deviating one, not
        /// simply the set one.</b> <c>/TK</c> is reported only when <c>true</c>: glyph-by-glyph
        /// SrcOver painting <i>is</i> <c>/TK false</c> behaviour — so reporting <c>false</c>
        /// would name the one value that is honoured. <c>/TK</c>'s initial value is also
        /// <c>true</c>, and that default is deliberately not reported: it would fire on every
        /// text-bearing page and say nothing about what the file asked for.</para>
        ///
        /// <para>Each is named in <c>detail</c>. Sibling of
        /// <see cref="BlackGenerationUndercolorIgnored"/>, and it exists for the same reason: a
        /// prepress caller must be able to tell "the file asked and we ignored it" from "the file
        /// never asked". These are all parsed-and-dropped, which is indistinguishable from absent
        /// at the pixel level.</para>
        ///
        /// <para><c>/FL</c> flatness is deliberately <b>not</b> in this set — it is a
        /// curve-flattening hint the geometry subsumes, not a colour decision.</para>
        /// </summary>
        RenderParameterIgnored,


        /// <summary>
        /// Overprint (§11.7.4.2) <em>was</em> applied, but as an approximation whose pixels differ
        /// from the per-colorant composite — the SVG export's <c>mix-blend-mode:multiply</c>.
        ///
        /// <para>Distinct from <see cref="OverprintNotSimulated"/>, which means the operation
        /// painted normally: here something overprint-shaped did happen, and it is not what the
        /// raster path produces. Multiply is only equal to the nonzero-overprint rule where the
        /// source and the backdrop have <b>no colorant in common</b>. 50% cyan over 50% cyan
        /// composites to 50% cyan per §11.7.4.2 (the source's nonzero cyan replaces the
        /// backdrop's) and to 75% under multiply. Appended last deliberately: the API snapshot
        /// records numeric enum values, so inserting mid-enum renumbers everything after it.</para>
        /// </summary>
        OverprintApproximated,

        /// <summary>
        /// A type&#160;4/5 mesh shading had more triangles than
        /// <c>PdfLimits.MaxMeshTrianglesPerBatch</c>, so it was rasterised as several
        /// <c>DrawVertices</c> calls instead of one. <b>Nothing is dropped</b> — every triangle is
        /// still drawn, in order.
        ///
        /// <para>Reported because the split is observable in the pixels: within one draw call
        /// adjacent triangles are rasterised against each other, and across a batch boundary they
        /// are two separate composites, which is where a hairline seam can appear. A caller seeing
        /// one of these is looking at a mesh far larger than any real file's, and the alternative —
        /// one unbounded batch — is a ~290&#160;MB allocation a hostile file chooses the size of.
        /// Appended last deliberately: the API snapshot records numeric enum values, so inserting
        /// mid-enum renumbers everything after it.</para>
        /// </summary>
        MeshBatchSplit,

        /// <summary>
        /// A widget annotation with no <c>/AP</c> was reached and its field type
        /// (<c>/FT</c>, §12.7.3.1 Table 220) could not be resolved, so its value was not drawn.
        /// </summary>
        /// <remarks>
        /// <para><b>This exists because the alternative was a confident lie.</b> The fallback used
        /// to read <c>ft ??= "Tx"</c> — turning "I could not determine the type" into "the type is
        /// text", with nothing reported anywhere on that path. A check box whose <c>/FT</c> sat out
        /// of reach therefore typeset the word <c>Yes</c> where a tick belongs, and a caller had no
        /// way to know the type had never been established.</para>
        ///
        /// <para>Both causes are reported the same way and named in <c>detail</c>: no <c>/FT</c>
        /// anywhere in the chain, and a chain longer than
        /// <c>PdfLimits.MaxFieldInheritanceDepth</c>. §12.7.4 defines four field types and no
        /// default, so there is no value to fall back to.</para>
        /// </remarks>
        FormFieldTypeUnresolved,

        /// <summary>
        /// A widget annotation's inheritable <c>/Q</c> quadding (§12.7.3.3) asked for centred or
        /// right-aligned text and the no-<c>/AP</c> fallback drew it left-aligned.
        /// </summary>
        /// <remarks>
        /// <para>The fallback measures no text width, so honouring <c>/Q</c> needs a measurement it
        /// does not do. Reported rather than silently dropped, because a wrong alignment is
        /// indistinguishable at the pixel level from a file that never asked.</para>
        ///
        /// <para><b>Only <c>/Q 1</c> and <c>/Q 2</c> are reported.</b> <c>/Q 0</c> is left-aligned,
        /// which is the state we are already in — and per this repository's "presence is not
        /// deviation" rule, a channel that fires on conformant input is one a caller learns to
        /// ignore.</para>
        /// </remarks>
        FormFieldQuaddingIgnored,

        /// <summary>
        /// The interactive form set <c>/NeedAppearances</c> (§12.7.3.3) and a widget's existing
        /// appearance stream did not show its value, so the appearance was <b>regenerated</b> —
        /// the file's own <c>/AP</c> was not drawn for that widget.
        /// </summary>
        /// <remarks>
        /// <para><b>Reported because the two outcomes look identical in the output and are not.</b>
        /// A form filled by a producer that relies on the flag carries the blank template's
        /// appearance beside a written <c>/V</c>; drawing that appearance renders an empty field,
        /// which is indistinguishable from a field nobody filled in. Regenerating draws the value
        /// through the same synthetic path a widget with no <c>/AP</c> at all takes — a rougher
        /// drawing than a real appearance stream, so a caller comparing against another viewer needs
        /// to know which of the two it got.</para>
        ///
        /// <para><c>detail</c> names the field type and the value length. Appended last
        /// deliberately: the API snapshot records numeric enum values, so inserting mid-enum
        /// renumbers everything after it.</para>
        /// </remarks>
        FormFieldAppearanceRegenerated,

        /// <summary>
        /// A paint operation's <c>/Separation</c> or <c>/DeviceN</c> coverage could not be
        /// rasterised, so that area is missing from the <see cref="InkAccumulator"/>'s map while
        /// the render itself continued.
        /// </summary>
        /// <remarks>
        /// This channel exists because an ink map is read as a measurement, and a measurement that
        /// is quietly short is worse than one that refuses: a caller estimating ink cost cannot
        /// tell "this region carries no spot ink" from "this region was not measured". Only
        /// <c>PdfPageRenderer.ExtractInkCoverage</c> attaches an accumulator, so it cannot fire on
        /// an ordinary render. Appended last for the same reason the entry above is: the API
        /// snapshot records numeric enum values.
        /// </remarks>
        InkCoverageIncomplete,

        /// <summary>
        /// A JPEG 2000 image was decoded and drawn, but not exactly as the file describes it: a
        /// tile whose data ran out before its packets did, a JP2 header box that could not be read
        /// (the codestream's components were read directly), a channel the plan tolerated, or an
        /// opacity channel the raw sample path could not carry. The detail lists every one.
        /// </summary>
        /// <remarks>
        /// Not <see cref="ImageDecodeFailed"/>: something was painted. A host comparing two renders
        /// needs to tell a partial decode from a codec difference, exactly as
        /// <see cref="ImageDownsampled"/> lets it tell a reduced decode from one. Appended last
        /// because the API snapshot records numeric enum values.
        /// </remarks>
        JpxDecodeDeviation,

        /// <summary>
        /// A separation render (<see cref="PdfSeparationRenderer"/>) laid a paint on a plate through
        /// its sRGB colour rather than its own components — a shading, a shading pattern, or an
        /// image whose samples could not be read (RGB, CIE-based, <c>/Indexed</c>, inline, or
        /// decoded by a codec) — or under a blend mode or transfer function a single plate cannot
        /// express. Reported once per plate and reason.
        /// </summary>
        /// <remarks>
        /// Something was painted, on every plate, at a plausible tint: what is missing is
        /// exactness, which is the question a prepress caller asks of a separation. Only a
        /// separation render can fire it. Appended last because the API snapshot records numeric
        /// enum values.
        /// </remarks>
        SeparationApproximated,

        /// <summary>
        /// <c>PdfRenderOptions.ProofProfile</c> names a profile the proof cannot be built from — not
        /// an ICC profile, or one with no <c>B2A</c> table for the relative intent (a matrix/TRC
        /// display profile has none) — so the page was rendered <b>unproofed</b>. The detail names
        /// the reason.
        /// </summary>
        /// <remarks>
        /// Reported because the output pixels are not what the caller asked for: a proof that
        /// silently fell back to the ordinary render would look like a printer with sRGB's gamut.
        /// Appended last because the API snapshot records numeric enum values.
        /// </remarks>
        ProofProfileUnsupported,

        /// <summary>
        /// A halftoned render (<c>PdfHalftoning</c>) could not apply a halftone as the page described it: the page
        /// sets more than one, so the first serves every paint; a halftone is malformed, too large
        /// (<c>PdfLimits.MaxHalftoneCellPixels</c>) or names no spot function this knows, so the device default
        /// serves; <c>/AccurateScreens</c> was asked for and ordinary screening used. The detail names which.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        HalftoneApproximated,

        /// <summary>
        /// <c>PdfRenderOptions.DestinationProfile</c> named a profile the render cannot be converted into — not a
        /// profile, a CMYK one, a non-monotonic tone curve, a singular matrix — so the page was left in sRGB; or
        /// an encoder could not carry the profile (grey output of an RGB destination, WebP of a grey one), so the
        /// file holds the destination's values without saying which space they are in. The detail names which.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        DestinationProfileUnsupported,

        /// <summary>
        /// <c>PdfRenderOptions.NamedColorProfile</c> named bytes that are not a readable ICC named-colour profile,
        /// so every spot ink was drawn through its own alternate space and tint transform. The detail says why.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        NamedColorProfileUnsupported,

        /// <summary>
        /// The page's glyph outlines spent more charstring steps than <c>PdfLimits.MaxCharstringStepsPerPage</c>
        /// allows, so a glyph evaluated past it was drawn as nothing; reported once a page. Only glyphs drawn
        /// from the library's own charstring evaluators (an embedded Type 1 or CFF program SkiaSharp could not
        /// load) are charged.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        GlyphOutlineWorkExceeded,

        /// <summary>
        /// An embedded CFF program the renderer would have drawn through Skia (wrapped as OpenType, for its
        /// hinting) ran glyphs heavier than <c>PdfLimits.MaxCharstringStepsForHinting</c>, or past the page's
        /// charstring budget, so the font is drawn from the library's own outlines instead, unhinted; reported
        /// once a font. Skia evaluates charstrings under no budget of the library's.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        FontHintingSkipped,

        /// <summary>
        /// A font's <c>/ToUnicode</c> CMap did not decode, or decoded past
        /// <c>PdfLimits.MaxCMapStreamBytes</c>, so the font is read without it: its glyphs draw by the
        /// font's own encoding, and the text the SVG writer's overlay carries for them is the encoding's
        /// guess, not the producer's mapping. <c>detail</c> carries the font resource name and the reason.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        FontToUnicodeUnread,

        /// <summary>
        /// The page's paints covered more device area, at their kinds' prices, than
        /// <c>PdfLimits.MaxPaintWorkPerPage</c> allows, so the paints after it were not drawn; reported once a
        /// page. Raster only: the SVG writer paints no pixels.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        PaintWorkExceeded,

        /// <summary>
        /// A separation render named more spot inks than <c>PdfLimits.MaxSpotPlatesPerPage</c>, so the inks past it
        /// have no plate of their own; reported once a render.
        /// </summary>
        /// <remarks>Appended last because the API snapshot records numeric enum values.</remarks>
        SpotPlatesCapped,
    }

    /// <summary>
    /// Lightweight diagnostics sink for the rendering pipeline, so a failure path that
    /// catches never drops the exception silently.  Hosts can subscribe to <see cref="OnEvent"/>
    /// to forward render anomalies to logging, telemetry, or test assertions.
    /// </summary>
    /// <remarks>
    /// All members are thread-safe.  Call sites short-circuit when no listener
    /// has been registered, so the cost when nobody is listening is one volatile
    /// read + one null check.
    /// </remarks>
    public static class RenderDiagnostics
    {
        /// <summary>
        /// Subscribe to receive a callback every time the renderer hits a
        /// non-fatal anomaly.  Set to <c>null</c> to disable (default).
        /// </summary>
        public static event Action<RenderDiagnosticKind, string, Exception> OnEvent;

        /// <summary>
        /// Reports an anomaly.  Call sites use this instead of swallowing
        /// exceptions silently.  Safe to call when no listener is registered.
        /// </summary>
        /// <remarks>
        /// <para>Delivery goes through <see cref="DiagnosticFanOut"/>, the one implementation of the
        /// subscriber fan-out rule in this repo: each subscriber is invoked on its own and its
        /// exception caught, so a throwing handler neither skips the handlers after it nor escapes
        /// into the recovery path that reported — nearly every call site is inside a <c>catch</c>
        /// that keeps a broken document from costing the rest of the render.</para>
        ///
        /// <para>While a page's operators run (<see cref="ForPage"/>), each kind is delivered at most
        /// <see cref="PdfLimits.MaxRenderReportsPerKindPerPage"/> times for the page, the last saying
        /// the rest are suppressed: a site reached once an operator or a colour sample reports as
        /// often as the file repeats it.</para>
        /// </remarks>
        public static void Report(RenderDiagnosticKind kind, string detail = null, Exception ex = null)
        {
            RenderBudget page = _page;
            if (page != null)
            {
                switch (page.AdmitReport(kind))
                {
                    case RenderBudget.ReportAdmission.Suppress: return;
                    case RenderBudget.ReportAdmission.DeliverLast: detail += page.ReportsSuppressedNote; break;
                }
            }
            // The field is passed directly, which reads it exactly once — a null check followed by
            // an invoke reads it twice and races with a concurrent unsubscribe.
            DiagnosticFanOut.Invoke(OnEvent, kind, detail, ex);
        }

        /// <summary>
        /// The page whose operators run on this thread, or null. Not a hand-off between operators:
        /// the scope is opened and closed around a content stream's operator loop, on the thread
        /// that runs it.
        /// </summary>
        [ThreadStatic] private static RenderBudget _page;

        /// <summary>
        /// Opens a scope in which the reports made on this thread are counted against
        /// <paramref name="page"/>'s <see cref="PdfLimits.MaxRenderReportsPerKindPerPage"/>; null
        /// opens nothing. Both renderers' operator loops open it with the page's budget, so a nested
        /// form counts against the same page. Disposing restores the enclosing scope.
        /// </summary>
        internal static PageScope ForPage(RenderBudget page) => new PageScope(page);

        /// <summary>A <see cref="ForPage"/> scope.</summary>
        internal readonly struct PageScope : IDisposable
        {
            private readonly RenderBudget _previous;
            private readonly bool _opened;

            internal PageScope(RenderBudget page)
            {
                _previous = _page;
                _opened = page != null;
                if (_opened) _page = page;
            }

            public void Dispose()
            {
                if (_opened) _page = _previous;
            }
        }
    }
}
