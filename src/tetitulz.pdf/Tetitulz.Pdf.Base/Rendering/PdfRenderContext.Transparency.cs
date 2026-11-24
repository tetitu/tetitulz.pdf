// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    internal sealed partial class PdfRenderContext : IDisposable
    {
        /// <summary>
        /// Installs a soft mask on the live graphics state, releasing whatever it replaces.
        /// The context takes ownership of <paramref name="mask"/>.
        /// </summary>
        /// <param name="mask">The mask bitmap; ownership transfers to the context.</param>
        /// <param name="isAlpha">
        /// True for an <c>/Alpha</c> soft mask, false for <c>/Luminosity</c> (§11.6.5.2).
        /// </param>
        /// <param name="offsetX">Device x of the mask bitmap's left edge. 0 for a page-sized mask.</param>
        /// <param name="offsetY">Device y of the mask bitmap's top edge.</param>
        public void SetSoftMask(SKBitmap mask, bool isAlpha, int offsetX = 0, int offsetY = 0)
        {
            var old = _gs.ActiveSoftMask;
            _gs.ActiveSoftMask  = mask;
            _gs.SoftMaskIsAlpha = isAlpha;
            _gs.SoftMaskOffsetX = offsetX;
            _gs.SoftMaskOffsetY = offsetY;
            ReleaseSoftMaskIfUnreferenced(old);
        }

        /// <summary>Clears the live soft mask (<c>gs</c> with <c>/SMask /None</c>), releasing it
        /// if no saved <c>q</c> frame is still using it.</summary>
        public void ClearSoftMask()
        {
            var old = _gs.ActiveSoftMask;
            _gs.ActiveSoftMask = null;
            ReleaseSoftMaskIfUnreferenced(old);
        }

        /// <summary>
        /// Disposes a soft-mask bitmap that has left the live state — but <b>only</b> once no
        /// graphics-state frame, live or saved, still references it.
        /// </summary>
        /// <remarks>
        /// <para>The soft mask is shared: <see cref="GraphicsState.Clone"/> propagates it across
        /// <c>q</c> (§8.4.2 Table 52). No one frame owns the bitmap, and the two obvious
        /// alternatives are both wrong: disposing on every replacement
        /// frees a bitmap a saved frame will draw with (use-after-free), and never disposing leaks
        /// a page-sized native allocation per <c>gs /SMask</c>.</para>
        /// <para>The stack is bounded by <see cref="MaxStateStackDepth"/> and a mask changes at
        /// most once per <c>gs</c>, so the scan is cheap enough to be exact rather than
        /// reference-counted.</para>
        /// </remarks>
        private void ReleaseSoftMaskIfUnreferenced(SKBitmap mask)
        {
            if (mask == null) return;
            if (ReferenceEquals(mask, _gs.ActiveSoftMask)) return;
            foreach (var saved in _stateStack)
                if (ReferenceEquals(mask, saved.ActiveSoftMask)) return;
            mask.Dispose();
        }

        // Number of compositing layers currently open on the canvas. While any layer is open the
        // pixels of _rootBitmap are stale — drawing goes to the layer, not the bitmap — so a
        // non-isolated group nested inside one cannot read its true backdrop and says so rather
        // than seeding from out-of-date pixels.
        private int _layerDepth;

        /// <summary>
        /// One entry per open transparency group: true when knockout is being <b>emulated</b> for
        /// that group, i.e. its elements composite with the group's initial backdrop instead of
        /// with each other (§11.4.5.6). Only the innermost group matters when painting, so this is
        /// peeked rather than summed.
        /// </summary>
        /// <remarks>
        /// The entry means "emulating knockout", <b>not</b> "isolated knockout". The condition is
        /// <c>knockout &amp;&amp; backdrop == null</c>, which an isolated group always satisfies
        /// and a non-isolated one often does; a name saying the narrower thing would be a
        /// disagreement no guard in this repository catches.
        /// </remarks>
        private readonly Stack<bool> _knockoutGroups = new Stack<bool>();

        /// <summary>
        /// Whether the element about to be painted lands in a group whose knockout is being
        /// emulated.
        /// </summary>
        private bool InEmulatedKnockoutGroup => _knockoutGroups.Count > 0 && _knockoutGroups.Peek();

        /// <summary>
        /// One entry per open transparency group: the blending colour space its elements composite
        /// in (§11.6.6 Table 147 <c>/CS</c>, inherited from the parent when the group names none).
        /// Pushed and popped beside <see cref="_knockoutGroups"/>.
        /// </summary>
        private readonly Stack<GroupBlendSpace> _blendSpaces = new Stack<GroupBlendSpace>();

        /// <summary>
        /// The page group's blending space (§11.6.6: the page's <c>/Group /CS</c> is honoured
        /// whatever its <c>/I</c>), which every group that names no space inherits — and the
        /// space the page's own content composites in. <see cref="GroupBlendSpace.Rgb"/> for a
        /// page without one.
        /// </summary>
        public GroupBlendSpace RootBlendSpace { get; set; } = GroupBlendSpace.Rgb;

        /// <summary>The blending space the element about to be painted composites in.</summary>
        public GroupBlendSpace CurrentBlendSpace => _blendSpaces.Count > 0 ? _blendSpaces.Peek() : RootBlendSpace;

        /// <summary>One report per render for a group blender Skia could not build.</summary>
        private bool _groupBlenderUnavailableReported;

        /// <summary>
        /// Sets how <paramref name="paint"/> composites <b>one element</b> under the graphics
        /// state's blend mode: Skia's own mode, or — inside a grey or CMYK group — the
        /// <see cref="GroupBlenders"/> program compositing that mode in the group's space.
        /// </summary>
        /// <param name="paint">The paint, with its colour or shader already set — the decision reads its alpha.</param>
        /// <param name="pdfBlendMode">The graphics state's <c>/BM</c>.</param>
        /// <param name="translucent">
        /// True when the paint may lay down partial alpha its colour does not show — an image with
        /// an alpha channel, a shader, a group's layer — so the group's space is used whatever the
        /// colour's alpha says.
        /// </param>
        /// <remarks>
        /// <para><b>The shortcut.</b> An opaque paint under Normal lands the same pixel in every
        /// blending space (the conversions are exact inverses, and Normal at alpha 1 is the source),
        /// so inside a CMYK group such a paint keeps Skia's fixed mode, which is several times
        /// cheaper per pixel than a runtime blender — and a print PDF's page group is very often
        /// <c>/DeviceCMYK</c>, with every paint on the page going through here. What the shortcut
        /// gives up is the coverage edge: an antialiased pixel of an opaque paint interpolates in RGB
        /// rather than in ink, the same sub-pixel class as antialiasing itself, and not reported.
        /// A grey group takes no shortcut, since every paint into it is converted to grey.</para>
        /// <para><see cref="SKBlendMode.Src"/> — an emulated knockout group's element — is a
        /// replacement in every space and keeps the fixed mode. A separation plate composites per
        /// colorant already and is left alone.</para>
        /// </remarks>
        private void ApplyElementBlend(SKPaint paint, string pdfBlendMode, bool translucent = false)
            => ApplyBlend(paint, ElementBlendMode(pdfBlendMode), translucent);

        /// <summary>As <see cref="ApplyElementBlend"/>, for a Skia mode already decided.</summary>
        private void ApplyBlend(SKPaint paint, SKBlendMode mode, bool translucent)
        {
            GroupBlendSpace space = CurrentBlendSpace;
            if (space != GroupBlendSpace.Rgb && Plate == null && mode != SKBlendMode.Src
                && (space == GroupBlendSpace.Gray || translucent || mode != SKBlendMode.SrcOver
                    || paint.Shader != null || paint.Color.Alpha < byte.MaxValue || _gs.ActiveSoftMask != null))
            {
                SKBlender blender = null;
                try { blender = GroupBlenders.For(space, mode); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException)
                {
                    if (!_groupBlenderUnavailableReported)
                    {
                        _groupBlenderUnavailableReported = true;
                        RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                            $"a {space} group's blender could not be built; the group is composited in sRGB.", ex);
                    }
                }
                if (blender != null) { paint.Blender = blender; return; }
            }
            paint.BlendMode = mode;
        }

        /// <summary>
        /// Blend mode for painting <b>one element</b> — a fill, a stroke, a text run, an image, a
        /// shading, or a nested group being composited onto its parent.
        ///
        /// <para>Inside a group whose <b>knockout is emulated</b> this is <see cref="SKBlendMode.Src"/>
        /// whatever the graphics state says, which is what knockout means: every element composites
        /// with the group's initial backdrop rather than with the elements painted before it, so a
        /// later element <i>replaces</i> an earlier one where they overlap instead of compositing
        /// over it. The blend mode can be discarded because an isolated group's initial backdrop is
        /// transparent — which is exactly the condition under which the emulation is switched on —
        /// and §11.3.6's compositing formula weights the blend function by the
        /// backdrop's alpha — at α<sub>b</sub> = 0 every blend mode reduces to the source colour.
        /// That equivalence is what makes a single blend-mode substitution a correct emulation
        /// here, rather than an approximation.</para>
        ///
        /// <para>A <b>non-isolated</b> knockout group is emulated too, whenever its layer was
        /// <i>not</i> seeded with a backdrop — which is common, and is why this is not simply the
        /// isolated case under another name. A
        /// group that really did seed its backdrop must let an element with α &lt; 1 composite
        /// <i>with</i> that backdrop, which <see cref="SKBlendMode.Src"/> would overwrite; those
        /// are reported and rendered non-knockout.</para>
        ///
        /// <para><b>Under <c>/AIS true</c> it is <see cref="SKBlendMode.SrcOver"/> instead</b>
        /// (§11.6.4.4): the alpha constants and the soft mask then supply the element's
        /// <i>shape</i>, its opacity is 1, and §11.4.5.6's knockout result
        /// <c>(1 − f)·C<sub>prev</sub> + f·C<sub>element</sub></c> — the element composited with
        /// the transparent initial backdrop at opacity 1, mixed in by its shape <c>f</c> — is
        /// exactly <c>SrcOver</c> at alpha <c>f</c>, whatever the blend mode. Outside a knockout
        /// group the two readings of alpha give the same pixel, because the compositing formulas
        /// use only the product of shape and opacity — so this is the one place
        /// <c>AlphaIsShape</c> is read.</para>
        /// </summary>
        private SKBlendMode ElementBlendMode(string pdfBlendMode)
            => InEmulatedKnockoutGroup
                ? (_gs.AlphaIsShape ? SKBlendMode.SrcOver : SKBlendMode.Src)
                : MapBlendMode(pdfBlendMode);

        /// <summary>One report per render for the knockout case the alpha source leaves approximated.</summary>
        private bool _shapeGroupKnockoutReported;

        /// <summary>
        /// Opens an off-screen layer for a transparency-group form XObject (PDF §11.6.6). The
        /// group's contents are composited onto the backdrop <b>once</b>, using the alpha and
        /// blend mode in effect at the <c>Do</c>, rather than each object inside compositing
        /// individually.
        /// </summary>
        /// <remarks>
        /// The group inherits the blending space in force (<see cref="CurrentBlendSpace"/>).
        /// </remarks>
        public void BeginTransparencyGroup(float alpha, string blendMode)
            => BeginTransparencyGroup(alpha, blendMode, isolated: true, knockout: false);

        /// <summary>
        /// As <see cref="BeginTransparencyGroup(float,string,bool,bool)"/>, inheriting the blending
        /// space in force.
        /// </summary>
        public void BeginTransparencyGroup(float alpha, string blendMode, bool isolated, bool knockout)
            => BeginTransparencyGroup(alpha, blendMode, isolated, knockout, blendSpace: null);

        /// <summary>
        /// As <see cref="BeginTransparencyGroup(float,string)"/>, honouring the group's
        /// <c>/I</c> (isolated) and <c>/K</c> (knockout) attributes (PDF §11.6.6, Table 147).
        /// </summary>
        /// <remarks>
        /// <para><b>Non-isolated groups.</b> Skia's <c>SaveLayer</c> always begins transparent, which
        /// would render a group isolated regardless of <c>/I</c> — visibly wrong where a non-Normal
        /// blend mode <i>inside</i> the group should blend against the page. The layer is
        /// <b>seeded with the backdrop</b> when <c>/I</c> is false, so blending
        /// inside sees the page beneath it.</para>
        /// <para>Seeding is sound rather than double-counted because of how the layer comes back
        /// down: where the group painted nothing the layer still holds the backdrop, so
        /// compositing it with <c>SrcOver</c> at the group's constant alpha α yields
        /// <c>B·(1−α) + S·α</c>, which is <c>B</c> exactly where <c>S == B</c>. A soft mask
        /// behaves the same way — masked-out areas resolve back to the untouched backdrop. This is
        /// why §11.4.7.3's explicit backdrop-removal step is not needed here, and it is also why
        /// seeding is refused for a <b>non-Normal blend mode at the <c>Do</c></b>: the group result
        /// would then be blended against a backdrop it already contains.</para>
        /// <para>Refused (and reported through <see cref="RenderDiagnostics"/>, never silently)
        /// when the backdrop cannot be observed: no root bitmap was supplied, or the group is
        /// nested inside another open layer, whose pixels Skia does not expose.</para>
        /// <para><b>Knockout (<c>/K</c>) is emulated whenever the layer's initial backdrop is
        /// transparent.</b> Each element in a knockout group composites with the group's
        /// <i>initial</i> backdrop rather than with the elements before it; where that backdrop is
        /// transparent the rule is expressible as a blend mode — see
        /// <see cref="ElementBlendMode"/>. That covers every isolated group, and also a
        /// <b>non-isolated</b> one whose seeding was refused by one of the four branches above:
        /// refusing to seed <i>makes</i> the initial backdrop transparent, so declining the
        /// emulation there withheld an answer that is exact under the approximation the group had
        /// already been given (and already reported). What remains unemulated is the case the
        /// deviation is actually about — a non-isolated group that <i>did</i> read the page, whose
        /// elements must composite <i>with</i> it and cannot be done by replacement. Those are
        /// reported and rendered non-knockout.</para>
        /// <para><b>The blending space</b> (<paramref name="blendSpace"/>, §11.6.6 Table 147
        /// <c>/CS</c>; null inherits). Every element painted inside the group composites in it
        /// (<see cref="ApplyBlend"/>), and the group's own result composites onto its parent in
        /// the <i>parent's</i> space — decided here before the push, as the knockout mode is. The
        /// layer's pixels stay sRGB throughout: the device conversions are exact inverses, so
        /// storing in sRGB and compositing in the group's space loses nothing.</para>
        /// </remarks>
        public void BeginTransparencyGroup(float alpha, string blendMode, bool isolated, bool knockout,
                                           GroupBlendSpace? blendSpace)
        {
            byte a = PdfColor.Channel(alpha);

            bool normalOuterBlend = PdfBlendModeEntry.IsNormal(blendMode);

            SKBitmap backdrop = null;
            if (!isolated)
            {
                if (!normalOuterBlend)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        $"non-isolated group composited with blend mode '{blendMode}' at the Do; " +
                        "seeding the backdrop would blend it against itself, so the group was " +
                        "rendered isolated.");
                else if (_rootBitmap == null)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "non-isolated group could not read its backdrop (this render target does " +
                        "not expose one); the group was rendered isolated.");
                else if (_layerDepth > 0)
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "non-isolated group is nested inside another compositing layer, whose " +
                        "pixels Skia does not expose; the group was rendered isolated.");
                else
                    backdrop = TrySnapshotBackdrop();
            }

            // Knockout is emulated exactly when this layer's initial backdrop is transparent — which
            // is what `backdrop == null` says, and it is the whole precondition (see
            // ElementBlendMode). Requiring `isolated` too would silently decline the emulation for a
            // non-isolated group that has just *failed* to seed a backdrop above: those four branches degrade the group to isolated, so its initial backdrop is
            // transparent and Src is exact under the approximation already made and already
            // reported. Only a group that really did seed a backdrop must composite *with* it, and
            // that is precisely the case `backdrop != null` still excludes.
            bool emulateKnockout = knockout && backdrop == null;
            if (knockout && !emulateKnockout)
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                    "non-isolated knockout group (/K true, /I false) rendered as non-knockout: its " +
                    "elements composite against the accumulated group result rather than the page " +
                    "backdrop they should each be knocked out against.");

            // Evaluated before the push, so a nested group composited onto a knockout parent is
            // itself one element of that parent and knocks out with Src.
            SKBlendMode groupBlend = ElementBlendMode(blendMode);

            // Under /AIS true the group's alpha is shape, but the group's own result opacity is
            // still opacity, and one layer holds only their product: where the group's content is
            // partly transparent, SrcOver lets more of the parent's earlier elements through than
            // the knockout leaves.
            if (InEmulatedKnockoutGroup && _gs.AlphaIsShape && !_shapeGroupKnockoutReported)
            {
                _shapeGroupKnockoutReported = true;
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                    "group composited into a knockout group under /AIS true: its alpha is applied " +
                    "as shape, but where the group's own content is partly transparent its " +
                    "opacity is not separated from its shape, so the knockout is incomplete there.");
            }

            using (var paint = new SKPaint { Color = new SKColor(0, 0, 0, a) })
            {
                // The group's result is partly transparent wherever it painted less than everything,
                // so it composites through the parent's space whatever its alpha constant says.
                ApplyBlend(paint, groupBlend, translucent: true);
                _canvas.SaveLayer(paint);
            }
            _layerDepth++;
            _knockoutGroups.Push(emulateKnockout);
            _blendSpaces.Push(blendSpace ?? CurrentBlendSpace);

            if (backdrop != null)
            {
                // Device space: the layer shares the canvas's pixel grid, so the backdrop lands
                // exactly where it came from regardless of the current CTM.
                _canvas.Save();
                _canvas.SetMatrix(SKMatrix.Identity);
                _canvas.DrawBitmap(backdrop, 0f, 0f, SKSamplingOptions.Default);
                _canvas.Restore();
                backdrop.Dispose();
            }
        }

        // ----------------------------------------------------------------
        //  Overprint simulation (PDF §11.7.4.2)
        // ----------------------------------------------------------------

        /// <summary>
        /// Enables overprint simulation for path fills and strokes. Off by default — overprint is
        /// a press behaviour, and viewers treat it as an "overprint preview" toggle rather than
        /// something that silently changes screen output. See <see cref="OverprintCompositor"/>.
        /// </summary>
        public bool SimulateOverprint { get; set; }

        /// <summary>
        /// Mirrors <c>PdfRenderOptions.DownsampleImagesToTarget</c>. Off by default, because it
        /// changes output pixels.
        /// </summary>
        public bool DownsampleImagesToTarget { get; set; }

        /// <summary>
        /// The thread budget a JPEG 2000 decode on this page may use: 1 for the calling thread —
        /// what a batch render's page worker gets, since the batch already runs a worker per page —
        /// N for at most N (<c>PdfRenderOptions.MaxRenderThreads</c> for a page render), 0 for the
        /// decoder's automatic degree.
        /// </summary>
        public int JpxDecodeThreads { get; set; }

        /// <summary>
        /// Mirrors <c>PdfRenderOptions.ImageResampler</c>: which filter a scaled bitmap is drawn
        /// with. The default defers to the image's own <c>/Interpolate</c> flag, which is what the
        /// spec asks for.
        /// </summary>
        /// <remarks>
        /// <b>Both sampling decisions read it</b> — the image draw and the soft-mask draw. Either
        /// reading <c>/Interpolate</c> directly would give a caller asking for nearest-neighbour to
        /// keep a scan pixel-sharp Mitchell wherever the flag is true, silently. A test that the
        /// property returns what was assigned to it would pass for an option that reached nothing,
        /// so the tests assert the pixels.
        /// </remarks>
        public ImageResampler ImageResampler { get; set; } = ImageResampler.PdfSpecInterpolate;

        /// <summary>
        /// The sampling to draw a scaled bitmap with, given the image's <c>/Interpolate</c> flag.
        /// </summary>
        /// <remarks>
        /// One place, because the image path and the soft-mask path both had their own copy of the
        /// same two-line decision — the shape CLAUDE.md calls a duplication report rather than
        /// documentation. <c>SKSamplingOptions.Default</c> is deliberately not among the answers:
        /// it is byte-identical to the obsolete draw overloads, and choosing it here would be a
        /// third meaning of "no opinion" rather than a filter.
        /// </remarks>
        internal SKSamplingOptions SamplingFor(bool interpolate)
        {
            bool cubic = ImageResampler switch
            {
                ImageResampler.Nearest    => false,
                ImageResampler.Mitchell   => true,
                ImageResampler.CatmullRom => true,
                _                         => interpolate,   // PdfSpecInterpolate
            };
            if (!cubic) return new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            return new SKSamplingOptions(ImageResampler == ImageResampler.CatmullRom
                ? SKCubicResampler.CatmullRom
                : SKCubicResampler.Mitchell);
        }

        /// <summary>
        /// The device-pixel extent the current CTM maps the image unit square onto — what an
        /// image drawn now would actually occupy.
        /// </summary>
        /// <remarks>
        /// An image is always drawn into the unit square (§8.9.5.2), so the CTM's column lengths
        /// give its device size directly. The canvas already carries the DPI scale, so it is
        /// applied here rather than being folded into the CTM.
        /// </remarks>
        public void GetImageDeviceExtent(out int width, out int height)
        {
            float[] m = _gs.CTM;
            double sx = Math.Sqrt((double)m[0] * m[0] + (double)m[1] * m[1]) * DpiScale;
            double sy = Math.Sqrt((double)m[2] * m[2] + (double)m[3] * m[3]) * DpiScale;

            width  = sx > 0 && sx < int.MaxValue ? SaturatingCast.ToInt(Math.Ceiling(sx)) : 0;
            height = sy > 0 && sy < int.MaxValue ? SaturatingCast.ToInt(Math.Ceiling(sy)) : 0;
        }

        // One-shot per reason per thread: an unsimulated overprint is a property of the
        // file/configuration, not of one path, so each reason is said once rather than once per
        // operator. [ThreadStatic] per the Diagnostics Pattern in CLAUDE.md.
        //
        // It was one bool, which made the first reason reached silence every other for the life of
        // the thread — see OverprintReportReason for why that could not simply carry more reasons.
        [ThreadStatic] private static OverprintReportReason _overprintReported;

        /// <summary>
        /// Re-arms every one-shot overprint diagnostic on this thread.
        /// </summary>
        /// <remarks>
        /// Called at the start of each page render, not only by tests. A slot a caller reads
        /// <i>after</i> an operation has to be reset at the <i>start</i> of that operation on every
        /// path (CLAUDE.md, the <c>LastPermsValidation</c> rule): without this the second and every
        /// later page rendered on a server thread reported nothing, so a host that rendered one
        /// overprinting document could never learn that the next one overprints too.
        /// </remarks>
        internal static void ResetOverprintReporting() => _overprintReported = OverprintReportReason.None;

        /// <summary>
        /// Reports, once per reason per thread, that the file asked for overprint on an operation
        /// the simulator does not cover. <paramref name="reason"/> supplies the wording, shared
        /// with the SVG path through <see cref="OverprintReporting.Describe"/>.
        /// </summary>
        internal static void ReportOverprintSkipped(OverprintReportReason reason)
            => OverprintReporting.ReportOnce(ref _overprintReported, reason,
                RenderDiagnosticKind.OverprintNotSimulated,
                OverprintReporting.Describe(reason) +
                " Overprint was not simulated for this operation; it painted normally. " +
                "Reported once per reason per thread.");

        /// <summary>
        /// Reports, once per reason per thread, that overprint <b>was</b> simulated but the result
        /// is not what the operation would otherwise have produced.
        /// </summary>
        /// <remarks>
        /// A different kind from <see cref="ReportOverprintSkipped"/> and a different claim: that
        /// one says the paint was left alone, this one says it was changed and the change is an
        /// approximation — the SVG writer's multiply approximation and the raster path's ICC-based
        /// CMYK image overprinting on colorants its own profile never reached alike.
        /// </remarks>
        internal static void ReportOverprintApproximated(OverprintReportReason reason)
            => OverprintReporting.ReportOnce(ref _overprintReported, reason,
                RenderDiagnosticKind.OverprintApproximated,
                OverprintReporting.Describe(reason) +
                " Reported once per reason per thread.");

        /// <summary>
        /// Reports, once per reason, that a paint the file set to overprint was drawn without
        /// simulation. Silent on a separation plate — a plate composites every paint per colorant —
        /// and when the file did not ask.
        /// </summary>
        /// <remarks>
        /// Simulation is the plate-based preview (<see cref="OverprintPreview"/>), which a page that
        /// overprints takes whenever <see cref="SimulateOverprint"/> is on, so a paint reaching here
        /// was rendered with it off — the report a caller needs to tell "this file overprints" from
        /// "it does not". Should a page's overprint escape the preview's resource walk, that is
        /// said too rather than reported as the opt-in.
        /// </remarks>
        internal void ReportOverprintUnavailable(bool stroke)
        {
            if (Plate != null || !(stroke ? _gs.OverprintStroke : _gs.OverprintFill)) return;
            if (!SimulateOverprint) { ReportOverprintSkipped(OverprintReportReason.SimulationOff); return; }
            RenderDiagnostics.Report(RenderDiagnosticKind.OverprintNotSimulated,
                "an overprinting paint was reached outside the overprint preview: its page's resource walk did not find the setting.", null);
        }

        /// <summary>
        /// Paints a captured glyph outline with the current fill paint — how a text run captured
        /// for ink measurement is drawn, so it is never lost.
        /// </summary>
        public void FillCapturedGlyphs(SKPath glyphPath)
        {
            if (glyphPath == null || glyphPath.IsEmpty) return;
            using var paint = BuildFillPaint(evenOdd: false);
            if (_gs.ActiveSoftMask != null)
            {
                if (!TryDrawPathWithMaskShader(glyphPath, paint))
                    DrawWithSoftMask(SoftMaskLayerBounds(glyphPath, paint),
                                     new PathDrawOp(glyphPath, paint));
            }
            else
                _canvas.DrawPath(glyphPath, paint);
        }

        /// <summary>
        /// Copies the current render target so it can seed a non-isolated group's layer. Returns
        /// null (with a diagnostic) rather than throwing if the copy fails — the caller then
        /// renders the group isolated, which is the pre-existing behaviour.
        /// </summary>
        private SKBitmap TrySnapshotBackdrop()
        {
            try
            {
                // A copy, not the live bitmap: the destination layer belongs to the same canvas,
                // and reading the base device while drawing into it is not a contract Skia makes.
                return _rootBitmap.Copy();
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                    "non-isolated group backdrop could not be copied; rendered isolated.", ex);
                return null;
            }
        }

        /// <summary>
        /// Closes the layer opened by <see cref="BeginTransparencyGroup(float,string,bool,bool)"/>, applying
        /// <paramref name="softMask"/> to the group result before compositing it down.
        /// </summary>
        public void EndTransparencyGroup(SKBitmap softMask)
        {
            if (softMask != null)
            {
                // Mask in device space (identity), matching DrawWithSoftMask.
                _canvas.Save();
                _canvas.SetMatrix(SKMatrix.Identity);
                using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
                _canvas.DrawBitmap(softMask, 0f, 0f, SKSamplingOptions.Default, maskPaint);
                _canvas.Restore();
            }
            if (_layerDepth > 0) _layerDepth--;
            if (_knockoutGroups.Count > 0) _knockoutGroups.Pop();
            if (_blendSpaces.Count > 0) _blendSpaces.Pop();
            _canvas.Restore();
        }

        /// <summary>
        /// Unwinds the graphics-state stack back to the page base, discarding every frame
        /// (and balancing the matching canvas saves) left behind by a content stream with
        /// unbalanced <c>q</c>/<c>Q</c> or a top-level <c>cm</c>.  PDF §7.8.2: a content
        /// stream must not leak its graphics state; this is called between the page-content
        /// pass and the annotation pass so a leftover CTM/clip cannot mis-place or mis-size
        /// annotation appearance streams (e.g. signature stamps).  The caller pushes one base
        /// frame before executing page content so there is always a base to restore to.
        /// </summary>
        public void ResetToBaseState()
        {
            while (_stateStack.Count > 0) PopState();
        }


        // ----------------------------------------------------------------
        //  Text clipping path (PDF §9.3.6 render modes 4-7)
        // ----------------------------------------------------------------

        /// <summary>
        /// One painting operation, handed to the soft-mask wrapper as a <b>struct</b> rather than a
        /// closure.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the shape had to change, since the cheaper repair does not work.</b> Every
        /// site below is on the per-operator — and for text, the per-glyph — path, and each used to
        /// pass an <c>Action</c>: one display class plus one delegate per paint, allocated whether
        /// or not a mask was set. Guarding the lambda with <c>if (_gs.ActiveSoftMask != null)</c>
        /// does <i>not</i> avoid that. The compiler hoists the captured locals (the paint, the
        /// path, the blob, the screen matrix) into a display class allocated where those
        /// <b>locals</b> are declared, not where the lambda literal appears — and every site
        /// declares <c>using var paint = …</c> before the branch. So the state has to travel as
        /// parameters or as a value, and a value is what keeps <see cref="DrawWithSoftMask"/> a
        /// single implementation.</para>
        /// <para>Implementations are <c>readonly struct</c>s and the wrapper takes them by
        /// <c>in</c>: with <c>where TOp : struct</c> the JIT specialises per shape and calls
        /// <see cref="Draw"/> directly, and <c>readonly</c> is what stops <c>in</c> from making a
        /// defensive copy at that call.</para>
        /// </remarks>
        private interface ICanvasDrawOp
        {
            /// <summary>Paints. Invoked <b>exactly once</b> by the wrapper — see its remarks.</summary>
            void Draw(SKCanvas canvas);
        }

        /// <summary>The commonest shape: one path with one paint. Seven call sites.</summary>
        private readonly struct PathDrawOp : ICanvasDrawOp
        {
            private readonly SKPath _path;
            private readonly SKPaint _paint;
            public PathDrawOp(SKPath path, SKPaint paint) { _path = path; _paint = paint; }
            public void Draw(SKCanvas canvas) => canvas.DrawPath(_path, _paint);
        }

        /// <summary>Floods the current clip — the <c>sh</c> operator (§8.7.4.2).</summary>
        private readonly struct PaintFloodOp : ICanvasDrawOp
        {
            private readonly SKPaint _paint;
            public PaintFloodOp(SKPaint paint) { _paint = paint; }
            public void Draw(SKCanvas canvas) => canvas.DrawPaint(_paint);
        }

        /// <summary>
        /// An image draw at an absolute screen matrix, so it can go through the soft-mask wrapper.
        /// </summary>
        /// <remarks>
        /// §11.6.4 applies the graphics-state soft mask to <b>every</b> painting operation.
        /// <c>DrawBitmap</c> straight onto the canvas would be the one painting operation that
        /// ignored it — while the SVG exporter emits <c>mask="url(#…)"</c> on its
        /// <c>&lt;image&gt;</c> elements, so the same file would render an unmasked image and
        /// export a masked one.
        /// This mirrors <see cref="ScreenTextOp"/>, including using the NULL-bounds overload: the op
        /// sets an absolute matrix, so a bounds value would be re-mapped by the canvas's current
        /// matrix into the wrong space.
        /// </remarks>
        private readonly struct ScreenImageOp : ICanvasDrawOp
        {
            private readonly SKImage _image;
            private readonly SKMatrix _screenMatrix;
            private readonly SKSamplingOptions _sampling;
            private readonly SKPaint _paint;

            public ScreenImageOp(SKImage image, SKMatrix screenMatrix,
                                 SKSamplingOptions sampling, SKPaint paint)
            {
                _image = image; _screenMatrix = screenMatrix; _sampling = sampling; _paint = paint;
            }

            public void Draw(SKCanvas canvas)
            {
                canvas.Save();
                canvas.SetMatrix(_screenMatrix);
                canvas.DrawImage(_image, 0, 0, _sampling, _paint);
                canvas.Restore();
            }
        }

        /// <summary>
        /// A string drawn under an explicit screen matrix. The matrix bypasses the canvas's base
        /// transform, so it is set and restored around the draw rather than concatenated.
        /// </summary>
        private readonly struct ScreenTextOp : ICanvasDrawOp
        {
            private readonly string _text;
            private readonly SKMatrix _screenMatrix;
            private readonly SKFont _font;
            private readonly SKPaint _paint;

            public ScreenTextOp(string text, SKMatrix screenMatrix, SKFont font, SKPaint paint)
            {
                _text = text; _screenMatrix = screenMatrix; _font = font; _paint = paint;
            }

            public void Draw(SKCanvas canvas)
            {
                canvas.Save();
                canvas.SetMatrix(_screenMatrix);
                // SKTextAlign.Left is the alignment the removed paint.TextAlign defaulted to.
                canvas.DrawText(_text, 0, 0, SKTextAlign.Left, _font, _paint);
                canvas.Restore();
            }
        }

        /// <summary>As <see cref="ScreenTextOp"/>, for a positioned glyph run (an SKTextBlob,
        /// which carries its own font per run).</summary>
        private readonly struct ScreenTextBlobOp : ICanvasDrawOp
        {
            private readonly SKTextBlob _blob;
            private readonly SKMatrix _screenMatrix;
            private readonly SKPaint _paint;

            public ScreenTextBlobOp(SKTextBlob blob, SKMatrix screenMatrix, SKPaint paint)
            {
                _blob = blob; _screenMatrix = screenMatrix; _paint = paint;
            }

            public void Draw(SKCanvas canvas)
            {
                canvas.Save();
                canvas.SetMatrix(_screenMatrix);
                canvas.DrawText(_blob, 0, 0, _paint);
                canvas.Restore();
            }
        }

        /// <summary>
        /// A glyph outline filled and then stroked (render modes 2 and 6, §9.3.6). The two passes
        /// share one scratch layer deliberately: they are one element, and masking them separately
        /// would composite the shared interior edge twice.
        /// </summary>
        private readonly struct FillThenStrokePathOp : ICanvasDrawOp
        {
            private readonly SKPath _path;
            private readonly SKPaint _fill;
            private readonly SKPaint _stroke;

            /// <param name="path">The glyph outline, in the canvas's own space.</param>
            /// <param name="fill">A paint configured for the fill half; mutated in place.</param>
            /// <param name="stroke">
            /// A paint carrying the stroke half's colour, width and §8.4.3 geometry. It is the
            /// <i>source</i> of those properties rather than the paint used, because both halves
            /// must be drawn with the one paint that the shared soft-mask layer was measured for.
            /// </param>
            public FillThenStrokePathOp(SKPath path, SKPaint fill, SKPaint stroke)
            {
                _path = path; _fill = fill; _stroke = stroke;
            }

            public void Draw(SKCanvas canvas)
            {
                // The paint arrives configured for the fill; it is mutated in place for the stroke,
                // and the caller owns its disposal.
                canvas.DrawPath(_path, _fill);

                // Every stroke property, not just the colour and the width: the cap, the join, the
                // miter limit and the dash, or they would be left at whatever the FILL paint carried
                // — Skia's defaults — and a mode-2 run ignore J, j, M and d while a stroke-only run
                // beside it honoured them.
                _fill.Style       = SKPaintStyle.Stroke;
                _fill.Color       = _stroke.Color;
                _fill.StrokeWidth = _stroke.StrokeWidth;
                _fill.StrokeCap   = _stroke.StrokeCap;
                _fill.StrokeJoin  = _stroke.StrokeJoin;
                _fill.StrokeMiter = _stroke.StrokeMiter;
                _fill.PathEffect  = _stroke.PathEffect;
                canvas.DrawPath(_path, _fill);
            }
        }

        /// <summary>
        /// Wraps a draw call in a SaveLayer/Restore pair so that the active soft mask
        /// is applied per-pixel via a DstIn composite rather than the previous flat 50% alpha hack.
        /// The soft mask bitmap was rendered (and luminosity-converted) by ApplySMask;
        /// drawing it with DstIn makes each pixel's alpha equal to the mask's alpha channel.
        /// </summary>
        private void DrawWithOptionalSoftMask<TOp>(in TOp op) where TOp : struct, ICanvasDrawOp
        {
            if (_gs.ActiveSoftMask != null) DrawWithSoftMask(null, in op);
            else op.Draw(_canvas);
        }

        /// <summary>
        /// As <see cref="DrawWithOptionalSoftMask{TOp}(in TOp)"/>, but tells Skia the region the
        /// draw can actually touch so the scratch layer is sized to the content instead of the page.
        /// </summary>
        /// <param name="canvasBounds">
        /// Bounds of the geometry about to be drawn, in the canvas coordinate system (the same
        /// space the path handed to <c>DrawPath</c> is in). Must be a superset of what the draw
        /// touches — too large only costs speed, too small clips the output.
        /// </param>
        /// <param name="op">The painting operation. See <see cref="ICanvasDrawOp"/>.</param>
        private void DrawWithOptionalSoftMask<TOp>(SKRect canvasBounds, in TOp op)
            where TOp : struct, ICanvasDrawOp
        {
            if (_gs.ActiveSoftMask != null) DrawWithSoftMask(canvasBounds, in op);
            else op.Draw(_canvas);
        }

        /// <summary>
        /// Draws into an isolated layer and multiplies the active soft mask into its alpha.
        /// </summary>
        /// <remarks>
        /// <para><b>The layer is bounded to the content wherever the caller can say what it is,
        /// and that is the whole performance story of soft masks here.</b> This wraps *every*
        /// painting operation while a mask is set — path fills and strokes, text, images,
        /// shadings — not just one of them. With an unbounded <c>SaveLayer(null)</c> Skia
        /// allocates a page-sized offscreen surface and the <c>DstIn</c> pass then composites the
        /// full-page mask over it, so a page-sized cost was being paid per operator regardless of
        /// how small the operator's geometry was. Measured: 200 small filled rectangles took
        /// <b>2 609 ms</b> masked against <b>37 ms</b> unmasked — ~13 ms each, ~70x.</para>
        /// <para>Bounding is <b>not</b> a change to the compositing model. The layer is scratch
        /// space; restricting it to a region the draw cannot paint outside of produces the same
        /// pixels. The mask is still drawn at identity from device (0,0), so it stays aligned —
        /// Skia clips it to the layer.</para>
        /// <para>What this deliberately does <b>not</b> do is hoist the layer to cover a whole
        /// text object or run. That would change which elements composite with each other, which
        /// is the <c>/TK</c> text-knockout question (§9.3.8), and it needs its own argument and
        /// pixel tests. Sizing the scratch surface needs neither.</para>
        /// </remarks>
        private void DrawWithSoftMask<TOp>(SKRect? canvasBounds, in TOp drawContent)
            where TOp : struct, ICanvasDrawOp
        {
            var mask = _gs.ActiveSoftMask;

            // In a knockout group the masked layer comes back down with SrcOver, which is knockout
            // under /AIS true (the mask is shape; see ElementBlendMode) and not under /AIS false. No
            // painting site reaches here then: every one tries the mask shader first
            // (TryDrawWithMaskShader, TryDrawImageWithMaskShader), which knocks out, and a combined
            // fill and stroke takes the knockout split of §11.7.4.4 (StrokeKnocksOutFill) whenever a
            // soft mask is set, so only a draw the shader route declines — a matrix with no inverse,
            // which paints nothing — would land here.

            // The mask sub-rectangle this draw can possibly touch, in device pixels. Computed
            // BEFORE SaveLayer, while the canvas still carries the CTM that maps canvas
            // coordinates to the device — SetMatrix(Identity) below throws that away.
            // The mask occupies this device rectangle — no longer necessarily the whole page.
            float mx = _gs.SoftMaskOffsetX, my = _gs.SoftMaskOffsetY;
            var maskRect = new SKRect(mx, my, mx + mask.Width, my + mask.Height);

            SKRect? deviceBounds = null;
            if (canvasBounds.HasValue)
            {
                SKRect d = _canvas.TotalMatrix.MapRect(canvasBounds.Value);
                d.Intersect(maskRect);
                if (d.IsEmpty) return;   // entirely outside the mask: nothing survives DstIn
                deviceBounds = d;
            }

            // Isolated layer: content is drawn into an off-screen surface, and merged back in the
            // element's blend mode. Inside the layer the element blends against transparency, where
            // every mode is the source colour, so the merge is the one place the blend can happen;
            // merging with SrcOver drew every soft-masked paint under /BM as Normal. In an emulated
            // knockout group the merge stays SrcOver — Src would clear the group outside the
            // element (see above).
            using var mergePaint = new SKPaint();
            // The merge is the one place the element composites with the group, so the group's
            // blending space applies here (ApplyBlend) — the masked layer is partly transparent.
            ApplyBlend(mergePaint, InEmulatedKnockoutGroup ? SKBlendMode.SrcOver : MapBlendMode(_gs.BlendMode),
                       translucent: true);
            if (canvasBounds.HasValue) _canvas.SaveLayer(canvasBounds.Value, mergePaint);
            else                       _canvas.SaveLayer(mergePaint);

            drawContent.Draw(_canvas);

            // Apply the mask in device-pixel space to avoid transform complications.
            // Reset to identity so (0,0) == top-left of the bitmap output surface.
            _canvas.Save();
            _canvas.SetMatrix(SKMatrix.Identity);
            using var maskPaint = new SKPaint { BlendMode = SKBlendMode.DstIn };
            if (deviceBounds.HasValue)
            {
                // Destination is the device rectangle; the source rectangle is the same region
                // expressed in the mask's own pixels, i.e. shifted back by its offset. Sampling
                // only that region avoids walking the whole mask for a draw touching a few
                // hundred pixels.
                SKRect dest = deviceBounds.Value;
                var src = new SKRect(dest.Left - mx, dest.Top - my,
                                     dest.Right - mx, dest.Bottom - my);
                _canvas.DrawBitmap(mask, src, dest, SKSamplingOptions.Default, maskPaint);
            }
            else
            {
                _canvas.DrawBitmap(mask, mx, my, SKSamplingOptions.Default, maskPaint);
            }
            // §11.6.5.2: beyond the mask bitmap the mask is 0 — a non-zero value outside the group's
            // /BBox arrives as a page-sized bitmap holding it (ApplySMask) — so the layer is cleared
            // there, as the shader route's decal tiling reads it. Left as drawn, the content outside
            // the box merged unmasked, and one file masked differently under /BM /Multiply (this
            // route) and /BM /Normal (the shader route).
            _canvas.ClipRect(maskRect, SKClipOperation.Difference);
            _canvas.Clear(SKColors.Transparent);
            _canvas.Restore();

            _canvas.Restore(); // merge layer back onto parent canvas
        }

        /// <summary>
        /// Draws one path with the soft mask folded into the paint as a shader, avoiding the
        /// offscreen layer entirely. Returns false when the fast path does not apply.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> Bounding the <c>SaveLayer</c> took 200 masked fills from
        /// 2 609 ms to 435 ms, but the residual still scales with page area — Skia allocates an
        /// offscreen per <c>SaveLayer</c> whatever bounds it is handed. The only way past that is
        /// not to use a layer: <c>DstIn</c>-compose the mask into the paint's own shader, so
        /// masking happens in the same pass as the fill.</para>
        ///
        /// <para><b>Two conditions are load-bearing, and both are ways this goes silently
        /// wrong:</b></para>
        /// <list type="number">
        ///   <item><b>The blend mode is SrcOver or Src.</b> A non-Normal mode keeps the layer,
        ///   which merges back in that mode (<see cref="DrawWithSoftMask{TOp}"/>) and so blends
        ///   against the page as the shader route would — a rare case, left on the route its test
        ///   exercises. <b>Src is the
        ///   knockout case:</b> a knockout group's element must replace what the group holds within
        ///   its shape, which the shader route does and the layer — merged with SrcOver there —
        ///   cannot.</item>
        ///   <item><b>The paint's alpha must move into the shader.</b> Skia multiplies a shader's
        ///   output by the paint's alpha, so leaving <c>ca</c> baked into <c>paint.Color</c>
        ///   <i>and</i> in the colour shader would square it. The colour (alpha included) becomes
        ///   the shader and the paint is set opaque.</item>
        /// </list>
        ///
        /// <para>A paint that already carries a shader (a pattern or shading fill) is left to the
        /// layer path: composing two shaders is possible but the tiling and local matrices
        /// interact, and that is a separate piece of work with its own failure modes.</para>
        /// </remarks>
        // The soft-mask bitmap wrapped as a shader, built once per distinct mask.
        private SKBitmap _maskShaderSource;
        private SKShader _maskShaderBase;

        /// <summary>
        /// A shader over the soft-mask bitmap, reused across draws.
        /// </summary>
        /// <remarks>
        /// <para><b>Rebuilding this per draw was the whole cost.</b> Composing the mask into the
        /// paint removed the offscreen layer and changed nothing measurable, because
        /// <c>SKShader.CreateBitmap</c> snapshots the bitmap — so a page-sized copy per fill had
        /// simply replaced a page-sized layer per fill. Caching the shader and varying only its
        /// local matrix is what actually removes the per-operation page-sized work.</para>
        /// <para>The bitmap is marked immutable first, which is what lets Skia wrap it instead of
        /// copying it. That is safe here because <c>ApplySMask</c> finishes building the mask
        /// before installing it and nothing writes to it afterwards.</para>
        /// </remarks>
        private SKShader MaskShaderFor(SKBitmap mask)
        {
            if (ReferenceEquals(mask, _maskShaderSource) && _maskShaderBase != null)
                return _maskShaderBase;

            _maskShaderBase?.Dispose();
            _maskShaderBase = null;
            _maskShaderSource = null;

            if (!mask.IsImmutable) mask.SetImmutable();
            var shader = SKShader.CreateBitmap(mask, SKShaderTileMode.Decal, SKShaderTileMode.Decal);
            if (shader == null) return null;

            _maskShaderSource = mask;
            _maskShaderBase = shader;
            return shader;
        }

        /// <summary>
        /// Draws one image with the soft mask folded into its paint — the image as a shader,
        /// <c>DstIn</c>-composed with the mask's — instead of through the masked layer. Returns
        /// false when the fast path does not apply.
        /// </summary>
        /// <remarks>
        /// <para>The same two conditions as <see cref="TryDrawPathWithMaskShader"/>, for the same
        /// reasons: SrcOver, or Src — a knockout group's paint, which the shader route knocks out
        /// within the image's quad at the masked opacity and the layer route cannot. The paint's
        /// alpha stays on the paint: Skia multiplies the shader's output by it once, as it does the
        /// image's in <c>DrawImage</c>. A separation plate's colour filter rides along: Skia filters
        /// unpremultiplied colour, so masking before filtering is the same as after.</para>
        /// <para>The layer route also could not be bounded for an image — it took a page-sized
        /// scratch layer per masked image — so this is a saving as well as the knockout case.</para>
        /// </remarks>
        private bool TryDrawImageWithMaskShader(SKImage image, SKMatrix screenMatrix,
                                                SKSamplingOptions sampling, SKPaint paint)
        {
            var mask = _gs.ActiveSoftMask;
            if (mask == null) return false;
            if (paint.Shader != null) return false;
            if (paint.BlendMode != SKBlendMode.SrcOver && paint.BlendMode != SKBlendMode.Src) return false;
            if (!screenMatrix.TryInvert(out SKMatrix inverse)) return false;

            SKShader baseShader = MaskShaderFor(mask);
            if (baseShader == null) return false;

            // Drawn in image-pixel space (the canvas matrix is screenMatrix), so the image shader
            // needs no local matrix and the mask's maps device pixels back through its inverse.
            using var maskShader = baseShader.WithLocalMatrix(SKMatrix.Concat(
                inverse, SKMatrix.CreateTranslation(_gs.SoftMaskOffsetX, _gs.SoftMaskOffsetY)));
            using var imageShader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
            if (maskShader == null || imageShader == null) return false;
            using var masked = SKShader.CreateCompose(imageShader, maskShader, SKBlendMode.DstIn);
            if (masked == null) return false;

            _canvas.Save();
            try
            {
                _canvas.SetMatrix(screenMatrix);
                paint.Shader = masked;
                _canvas.DrawRect(new SKRect(0, 0, image.Width, image.Height), paint);
            }
            finally
            {
                paint.Shader = null;
                _canvas.Restore();
            }
            return true;
        }

        private bool TryDrawPathWithMaskShader(SKPath path, SKPaint paint)
            => TryDrawWithMaskShader(paint, _canvas.TotalMatrix, new PathDrawOp(path, paint));

        /// <summary>
        /// Draws <paramref name="op"/> with the soft mask folded into <paramref name="paint"/> as a
        /// shader — the paint's own shader (a pattern or a shading), or its colour as one, composed
        /// <c>DstIn</c> with the mask's — instead of through the masked layer. Returns false when
        /// the fast path does not apply.
        /// </summary>
        /// <param name="paint">The paint <paramref name="op"/> draws with; its shader and colour are restored after.</param>
        /// <param name="drawMatrix">The canvas matrix in force while <paramref name="op"/> draws — the
        /// current one, or the screen matrix a text op sets — which the mask is placed back through.</param>
        /// <param name="op">The draw.</param>
        /// <remarks>
        /// <para><b>A shader paint keeps its own local matrix.</b> A compose shader's children are
        /// each sampled through their own, so a tiling pattern or a shading composes with the mask
        /// as a solid colour does; only the mask's matrix is chosen here.</para>
        /// <para><b>The alpha.</b> A solid paint's colour, alpha included, becomes the colour shader
        /// and the paint goes opaque, since Skia multiplies a shader's output by the paint's alpha.
        /// A shader paint keeps its colour, so that multiplication is its <c>ca</c>, once.</para>
        /// </remarks>
        private bool TryDrawWithMaskShader<TOp>(SKPaint paint, SKMatrix drawMatrix, in TOp op)
            where TOp : struct, ICanvasDrawOp
        {
            var mask = _gs.ActiveSoftMask;
            if (mask == null) return false;
            // Src is a knockout group's paint under /AIS false (ElementBlendMode), and drawn
            // through the masked shader it is exact: Skia's Src under coverage replaces the group
            // within the shape by the paint at the mask's opacity, which is what knocking out with
            // the mask as opacity means. The layer route (DrawWithSoftMask) cannot do that.
            if (paint.BlendMode != SKBlendMode.SrcOver && paint.BlendMode != SKBlendMode.Src) return false;

            // A shader is sampled at (M * localMatrix)^-1 · devicePoint for the draw's matrix M.
            // The mask bitmap's own (0,0) sits at device (offsetX, offsetY), so shader space must be
            // device space minus that offset: localMatrix = M^-1 · translate(offset) gives
            // (M * localMatrix) = translate(offset), whose inverse maps p to p - offset.
            if (!drawMatrix.TryInvert(out SKMatrix inverse)) return false;

            SKShader baseShader = MaskShaderFor(mask);
            if (baseShader == null) return false;

            using var maskShader = baseShader.WithLocalMatrix(SKMatrix.Concat(
                inverse, SKMatrix.CreateTranslation(_gs.SoftMaskOffsetX, _gs.SoftMaskOffsetY)));
            if (maskShader == null) return false;

            SKShader previousShader = paint.Shader;
            SKColor previousColor = paint.Color;
            using var colorShader = previousShader == null ? SKShader.CreateColor(previousColor) : null;
            using var masked = SKShader.CreateCompose(previousShader ?? colorShader, maskShader, SKBlendMode.DstIn);
            if (masked == null) return false;

            try
            {
                paint.Shader = masked;
                if (previousShader == null) paint.Color = SKColors.Black;
                op.Draw(_canvas);
            }
            finally
            {
                paint.Shader = previousShader;
                paint.Color = previousColor;
            }
            return true;
        }

        /// <summary>
        /// The region a path can paint into, in canvas coordinates: its bounds grown by whatever
        /// the paint adds outside them.
        /// </summary>
        /// <remarks>
        /// Deliberately generous. A stroke extends half its width either side of the path, and a
        /// miter join can reach <c>StrokeMiter</c> times further at a sharp corner; antialiasing
        /// and any path effect add a little more. Over-estimating costs a slightly larger scratch
        /// layer, under-estimating clips visible output — so the margin is padded rather than
        /// computed exactly.
        /// </remarks>
        private static SKRect SoftMaskLayerBounds(SKPath path, SKPaint paint)
        {
            SKRect r = path.Bounds;
            float margin = 1f; // antialiasing
            if (paint != null && paint.Style != SKPaintStyle.Fill)
            {
                float half = Math.Max(paint.StrokeWidth, 1f) * 0.5f;
                margin += half * Math.Max(1f, paint.StrokeMiter);
                if (paint.PathEffect != null) margin += half; // dashes/caps can extend further
            }
            r.Inflate(margin, margin);
            return r;
        }

        // ----------------------------------------------------------------
        //  Shading
        // ----------------------------------------------------------------
    }
}
