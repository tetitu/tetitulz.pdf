// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The aggregate form-invocation budget for rendering <b>one page</b> — how many times a form,
    /// a pattern cell, a soft-mask group or a Type 3 glyph procedure may run, and how many decoded
    /// bytes of their content may run in all — shared in shape (not in instance) by the raster and
    /// SVG renderers.
    /// </summary>
    /// <remarks>
    /// <para><b>What it stops.</b> N forms where form <i>i</i>'s content is
    /// <c>/F{i+1} Do /F{i+1} Do</c>. No form draws itself, so a cycle guard never fires; no path
    /// exceeds <see cref="PdfLimits.MaxRenderRecursionDepth"/>, so the depth cap never fires; and the
    /// walk still performs 2^(N-1) invocations. Eighteen one-line forms — about four kilobytes —
    /// measured 131 072 invocations and roughly two seconds per renderer, and the depth cap allows
    /// twenty-four levels, which is 8.4 million.</para>
    ///
    /// <para><b>And the product the count alone does not bound.</b> A form of two thousand
    /// <c>Do</c> operators drawing itself is a 16 KB stream run whole at every invocation: a hundred
    /// thousand invocations is 1.6 GB of content from a 20 KB file, and the SVG writer, which caches
    /// no parse, ran for the whole of the structure fuzzer's two-minute window on four such files
    /// while the invocation count was still inside its ceiling. Every invocation is therefore also
    /// charged its decoded length against <see cref="PdfLimits.MaxFormContentBytesPerPage"/>, and
    /// the first ceiling reached refuses.</para>
    ///
    /// <para><b>And the price an invocation has before its content runs.</b> A tiling pattern's cell
    /// and a soft-mask group are rendered offscreen: a bitmap of the cell's size is allocated and
    /// cleared, and a tile composed from its copies, before a byte of content is interpreted. The
    /// coverage-guided fuzzer's pattern whose cell fills with itself twice — a thousand points
    /// square at a one-point step — doubled inside the invocation count, the content count, the
    /// depth cap and the lattice's copy cap at once, and each of its hundred thousand invocations
    /// was a four-megabyte bitmap: minutes from two kilobytes. The raster renderer therefore also
    /// charges every offscreen bitmap its area against
    /// <see cref="PdfLimits.MaxOffscreenPixelsPerPage"/>; the SVG writer, which renders nothing
    /// offscreen, never spends this ceiling, so the two renderers' budgets differ in this one
    /// dimension by construction rather than by lag.</para>
    ///
    /// <para><b>Why it reuses <see cref="PdfLimits.MaxFormXObjectInvocationsPerPage"/> rather than
    /// minting a constant.</b> That limit's doc comment describes this exact doubling chain, and
    /// <c>PdfStructuredTextExtractor</c> reads it too, so a document the extractor refuses in
    /// milliseconds both renderers refuse as well. A second constant would have been a second name
    /// for one value, which is the failure <c>PdfLimits</c>' own header opens by warning about.</para>
    ///
    /// <para><b>Why it refuses rather than throws.</b> Both renderers wrap operator dispatch in a
    /// blanket <c>catch (Exception)</c>, so a thrown budget exception would be swallowed and
    /// re-reported as a fabricated content-stream error — and on the raster side the graphics state
    /// and any transparency-group layer have already been pushed by the time a form's content runs,
    /// so unwinding past them would leave both stacks unbalanced. Charging at the invocation site,
    /// before any state is pushed, is also what the extraction side chose, so the two halves of the
    /// codebase agree on policy.</para>
    ///
    /// <para><b>No static fields, deliberately.</b> This type therefore has no type initializer, so
    /// neither renderer acquires an initialization edge on the other by using it — the property that
    /// makes <c>SvgSyntax</c> a safe shared leaf, and whose absence once crashed the Base test host
    /// part-way through a parallel run while still reporting success.</para>
    ///
    /// <para>Not thread-safe: build and use one per page render, as with
    /// <see cref="WalkBudget"/> which it wraps.</para>
    /// </remarks>
    internal sealed class RenderBudget
    {
        private readonly WalkBudget _budget;
        private readonly int _invocations;
        private readonly long _contentBytes;
        private readonly long _offscreenPixels;
        private long _bytesLeft;
        private long _pixelsLeft;
        private bool _exhausted;
        private bool _reported;

        internal RenderBudget()
            : this(PdfLimits.MaxFormXObjectInvocationsPerPage, PdfLimits.MaxFormContentBytesPerPage) { }

        internal RenderBudget(int invocations)
            : this(invocations, PdfLimits.MaxFormContentBytesPerPage) { }

        internal RenderBudget(int invocations, long contentBytes)
            : this(invocations, contentBytes, PdfLimits.MaxOffscreenPixelsPerPage) { }

        internal RenderBudget(int invocations, long contentBytes, long offscreenPixels)
            : this(invocations, contentBytes, offscreenPixels, PdfLimits.MaxFormOperationsPerPage) { }

        internal RenderBudget(int invocations, long contentBytes, long offscreenPixels, long operations)
        {
            _budget = new WalkBudget(invocations);
            _invocations = invocations;
            _contentBytes = contentBytes;
            _bytesLeft = contentBytes;
            _offscreenPixels = offscreenPixels;
            _pixelsLeft = offscreenPixels;
            _operations = operations;
            _operationsLeft = operations;
            _paintWorkLeft = _paintWork;
        }

        private readonly long _operations;
        private long _operationsLeft;

        private readonly long _paintWork = PdfLimits.MaxPaintWorkPerPage;
        private double _paintWorkLeft;
        private bool _paintRefused;
        private bool _depthReported;

        // The price of one device pixel of each kind of paint, in pixels of a solid fill, measured on
        // the shipped build at a hundred megapixels (a page-sized fill is 42 ms): a stroke 2.4, a
        // shading 15, an image drawn with nearest sampling 13 and with Mitchell sampling — what
        // /Interpolate true asks for — 88. Each is rounded up, so no kind is charged less than it costs.
        internal const int FillPaintWeight = 1;
        internal const int StrokePaintWeight = 3;
        internal const int ShadingPaintWeight = 15;
        internal const int ImagePaintWeight = 13;
        internal const int InterpolatedImagePaintWeight = 88;

        /// <summary>The paint work this page has been charged so far, in fill pixels.</summary>
        internal double PaintWorkSpent => _paintWork - _paintWorkLeft;

        /// <summary>
        /// Charges a paint <paramref name="units"/> of work — the device area it covers inside the clip
        /// times its kind's weight — against the page's <see cref="PdfLimits.MaxPaintWorkPerPage"/>.
        /// Returns false once the ceiling is spent, in which case the caller paints nothing; the
        /// refusal is reported once. Raster only: the SVG writer paints no pixels.
        /// </summary>
        /// <remarks>
        /// Separate from the form ceilings on purpose: a page whose paints are spent still runs its
        /// remaining operators (a later text advance, a clip, a nested form's own charges), and a page
        /// whose forms are refused still paints its own content.
        /// </remarks>
        internal bool TryChargePaint(double units)
        {
            if (_paintRefused) return false;
            if (!(units > 0)) return true;
            _paintWorkLeft -= units;
            if (_paintWorkLeft >= 0) return true;
            _paintRefused = true;
            RenderDiagnostics.Report(RenderDiagnosticKind.PaintWorkExceeded, PaintRefusalMessage, null);
            return false;
        }

        /// <summary>
        /// True the first time this page's nesting reaches <see cref="PdfLimits.MaxRenderRecursionDepth"/>,
        /// so the refusal is reported once a page: a form at the cap holding a thousand <c>Do</c>
        /// operators, reached by every branch the invocation ceiling admits, refuses millions of times.
        /// </summary>
        internal bool FirstDepthRefusal()
        {
            if (_depthReported) return false;
            _depthReported = true;
            return true;
        }

        private bool _tilingApproximationReported;

        /// <summary>
        /// True the first time this page builds a tiling pattern whose overlap needs more cell copies
        /// than one tile holds, so the approximation is reported once a page: the report is made before
        /// the build is charged, and a cell that paints itself reaches it on every attempt the
        /// invocation ceiling refuses as well as every one it admits.
        /// </summary>
        internal bool FirstTilingApproximation()
        {
            if (_tilingApproximationReported) return false;
            _tilingApproximationReported = true;
            return true;
        }

        private readonly int _reportsPerKind = PdfLimits.MaxRenderReportsPerKindPerPage;
        private System.Collections.Generic.Dictionary<RenderDiagnosticKind, int> _reportsByKind;

        /// <summary>
        /// Counts one report of <paramref name="kind"/> against the page's
        /// <see cref="PdfLimits.MaxRenderReportsPerKindPerPage"/>: <see cref="ReportAdmission.Deliver"/>
        /// below it, <see cref="ReportAdmission.DeliverLast"/> at it, <see cref="ReportAdmission.Suppress"/>
        /// past it.
        /// </summary>
        internal ReportAdmission AdmitReport(RenderDiagnosticKind kind)
        {
            _reportsByKind ??= new System.Collections.Generic.Dictionary<RenderDiagnosticKind, int>();
            _reportsByKind.TryGetValue(kind, out int delivered);
            if (delivered >= _reportsPerKind) return ReportAdmission.Suppress;
            _reportsByKind[kind] = delivered + 1;
            return delivered + 1 == _reportsPerKind ? ReportAdmission.DeliverLast : ReportAdmission.Deliver;
        }

        /// <summary>What <see cref="AdmitReport"/> decided about one report.</summary>
        internal enum ReportAdmission
        {
            /// <summary>Delivered.</summary>
            Deliver,
            /// <summary>Delivered, and the last of its kind on this page: it says the rest are suppressed.</summary>
            DeliverLast,
            /// <summary>Not delivered.</summary>
            Suppress,
        }

        /// <summary>The note the last delivered report of a kind carries.</summary>
        internal string ReportsSuppressedNote =>
            " [the page's limit of " + _reportsPerKind.ToString("N0", CultureInfo.InvariantCulture) +
            " reports of this kind is reached (PdfLimits.MaxRenderReportsPerKindPerPage); its later ones are suppressed]";

        /// <summary>The paint refusal: the area the page's paints covered, which no count prices.</summary>
        internal string PaintRefusalMessage =>
            "This page's paints covered more than " +
            _paintWork.ToString("N0", CultureInfo.InvariantCulture) +
            " units of device area (PdfLimits.MaxPaintWorkPerPage; a unit is one pixel of a solid fill, and " +
            "strokes, shadings and images cost several); the remaining paints were not drawn. A paint costs " +
            "the area it covers, and the page box chooses the bitmap's size up to the area ceiling whatever " +
            "resolution was asked for.";

        private WorkBudget _imageWork;
        private bool _imageWorkRefused;
        private WorkBudget _functionWork;

        /// <summary>
        /// The page's Type 4 function budget (<see cref="PdfLimits.MaxFunctionWorkPerPage"/>), which
        /// every evaluation while the page's operators run spends as its parent
        /// (<c>PdfFunctionEvaluator.SpendFrom</c>, opened by both renderers' operator loops); made on
        /// first use.
        /// </summary>
        internal WorkBudget FunctionWork => _functionWork ??= new WorkBudget(PdfLimits.MaxFunctionWorkPerPage,
            "the Type 4 functions this page evaluates (PdfLimits.MaxFunctionWorkPerPage)");

        /// <summary>
        /// The page's image-decoding budget (<see cref="PdfLimits.MaxImageDecodeWorkPerPage"/>), the
        /// parent every image stream's own codec budget spends through
        /// <see cref="StreamFilter.Work"/>; made on first use. Both renderers hand it to
        /// <c>DecodeImageStream</c>.
        /// </summary>
        internal WorkBudget ImageWork => _imageWork ??= new WorkBudget(PdfLimits.MaxImageDecodeWorkPerPage,
            "image decoding on this page (PdfLimits.MaxImageDecodeWorkPerPage)");

        private WorkBudget _charstringWork;

        /// <summary>
        /// The page's charstring budget (<see cref="PdfLimits.MaxCharstringStepsPerPage"/>), which every
        /// glyph outline evaluated while the page's operators run spends (<c>CharstringWork.SpendFrom</c>,
        /// opened by both renderers' operator loops); made on first use.
        /// </summary>
        internal WorkBudget CharstringWork => _charstringWork ??= new WorkBudget(PdfLimits.MaxCharstringStepsPerPage,
            "the glyph outlines this page evaluates (PdfLimits.MaxCharstringStepsPerPage)");

        /// <summary>
        /// Reports, once a page, that the page's charstring budget stopped a glyph: it and the glyphs
        /// after it whose outlines had not been evaluated yet draw nothing.
        /// </summary>
        internal static void ReportCharstringRefusal()
        {
            if (!Core.Fonts.CharstringWork.FirstRefusal()) return;
            RenderDiagnostics.Report(RenderDiagnosticKind.GlyphOutlineWorkExceeded,
                "This page's glyph outlines ran more than " +
                PdfLimits.MaxCharstringStepsPerPage.ToString("N0", CultureInfo.InvariantCulture) +
                " charstring steps (PdfLimits.MaxCharstringStepsPerPage); the glyphs whose outlines were " +
                "not evaluated by then were not drawn. A font's charstrings can all call one subroutine " +
                "that runs to the per-glyph cap, and that cap is per glyph; this one is the page's.", null);
        }

        /// <summary>
        /// Whether another image may be decoded: false once the page's image-decoding budget has
        /// refused, in which case the refusal has been reported (once) and the caller draws nothing.
        /// </summary>
        internal bool TryDecodeImage()
        {
            if (_imageWork == null || !_imageWork.IsExhausted) return true;
            // Every refused image comes through here or through IsImageWorkRefusal; RefuseImageWork
            // alone decides that the page reports once, so a mutation of its guard shows.
            RefuseImageWork();
            return false;
        }

        /// <summary>
        /// Whether <paramref name="ex"/> is the page's image-decoding budget refusing — not a
        /// stream's own — in which case the refusal is recorded and reported once, and the caller
        /// reports nothing else for this image.
        /// </summary>
        internal bool IsImageWorkRefusal(Exception ex)
        {
            if (!(ex is WorkBudgetExceededException w) || _imageWork == null || w.Budget != _imageWork) return false;
            RefuseImageWork();
            return true;
        }

        private void RefuseImageWork()
        {
            if (_imageWorkRefused) return;
            _imageWorkRefused = true;
            RenderDiagnostics.Report(RenderDiagnosticKind.ImageDecodeFailed, ImageWorkRefusalMessage, null);
        }

        /// <summary>The image-decoding refusal: the page's images together spent more than the ceiling allows.</summary>
        internal string ImageWorkRefusalMessage =>
            "This page's image decodes spent more than " +
            PdfLimits.MaxImageDecodeWorkPerPage.ToString("N0", CultureInfo.InvariantCulture) +
            " units of codec work (PdfLimits.MaxImageDecodeWorkPerPage); the remaining images were " +
            "not decoded. A JBIG2 stream of a few dozen bytes can spend its whole composition budget, " +
            "and that budget is per stream; this one is the page's.";

        /// <summary>
        /// Charges an offscreen bitmap's area — a pattern cell's, a soft-mask group's, a tile's
        /// copies' — against the page's pixel ceiling. Returns false when it is spent, in which case
        /// the caller draws nothing, as for a refused invocation. Raster only: the SVG writer has no
        /// offscreen bitmap to charge.
        /// </summary>
        internal bool TryChargeOffscreenPixels(long pixels)
        {
            if (_exhausted) return false;
            _pixelsLeft -= Math.Max(0L, pixels);
            if (_pixelsLeft < 0)
            {
                Refuse(OffscreenRefusalMessage);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Charges the <paramref name="count"/> operations a nested content stream is about to run —
        /// a form's, a pattern cell's, a soft-mask group's, a Type 3 glyph procedure's — against the
        /// page's operation ceiling (<see cref="PdfLimits.MaxFormOperationsPerPage"/>). Returns false
        /// when it is spent, in which case the caller runs none of them. Both renderers, after the
        /// parse and before the first operator.
        /// </summary>
        internal bool TryChargeFormOperations(int count)
        {
            if (_exhausted) return false;
            _operationsLeft -= Math.Max(0, count);
            if (_operationsLeft < 0)
            {
                Refuse(OperationsRefusalMessage);
                return false;
            }
            return true;
        }

        /// <summary>True once the budget has refused at least one invocation, by either ceiling.</summary>
        internal bool Exhausted => _exhausted || _budget.Exhausted;

        /// <summary>
        /// Charges one invocation whose content length is not known — a caller that has nothing to
        /// run yet. Prefer <see cref="TryChargeFormInvocation(long)"/> with the decoded length.
        /// </summary>
        internal bool TryChargeFormInvocation() => TryChargeFormInvocation(0);

        /// <summary>
        /// Charges one form-XObject invocation — or one pattern cell, soft-mask group or Type 3 glyph
        /// procedure, which are the same shape of work (a content stream run under a pushed state,
        /// from a resource that can name its own kind) — and its <paramref name="contentBytes"/> of
        /// decoded content. Returns false when either of the page's ceilings is spent, in which case
        /// the caller must return <b>without</b> pushing any graphics state.
        /// </summary>
        /// <remarks>
        /// Reports at most once per budget. A page that trips this does so tens of thousands of
        /// times over, and a diagnostic sink that receives 30 000 copies of one message is a denial
        /// of service against the host's logging rather than a report to it.
        /// </remarks>
        internal bool TryChargeFormInvocation(long contentBytes)
        {
            if (_exhausted) return false;

            if (!_budget.Take())
            {
                Refuse(RefusalMessage);
                return false;
            }

            _bytesLeft -= Math.Max(0L, contentBytes);
            if (_bytesLeft < 0)
            {
                Refuse(ContentRefusalMessage);
                return false;
            }
            return true;
        }

        private void Refuse(string message)
        {
            _exhausted = true;
            if (_reported) return;
            _reported = true;
            RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError, message, null);
        }

        /// <summary>
        /// The invocation-count refusal, identical in both renderers so a parity test can compare them
        /// directly rather than matching each against its own wording.
        /// </summary>
        /// <remarks>
        /// It names <b>this budget's</b> ceiling. It read the process-wide limit, so a budget given
        /// its own ceiling reported a number it had not refused at.
        /// </remarks>
        internal string RefusalMessage =>
            "This page exceeded " +
            _invocations.ToString("N0", CultureInfo.InvariantCulture) +
            " form-XObject invocations (Type 3 glyph procedures counted among them); the remaining " +
            "forms and glyphs were not drawn. A depth cap bounds one branch, not the number of " +
            "branches, so a chain of forms each drawing the next twice — or a Type 3 glyph showing " +
            "four of its own font — reaches this without ever repeating a name or exceeding the " +
            "nesting limit.";

        /// <summary>The offscreen-pixel refusal: the bitmaps an invocation allocates before its content runs, which neither count bounds.</summary>
        internal string OffscreenRefusalMessage =>
            "This page's offscreen renders — tiling pattern cells and their tiles, soft-mask groups — " +
            "allocated more than " + _offscreenPixels.ToString("N0", CultureInfo.InvariantCulture) +
            " pixels; the remaining ones were not drawn. A pattern cell is a bitmap of its own size " +
            "before its content runs, so a cell that fills with itself twice doubles inside the " +
            "invocation count with a bitmap at every step.";

        /// <summary>The decoded-content refusal: the product of invocations and their size, which the count alone does not bound.</summary>
        internal string ContentRefusalMessage =>
            "This page's form invocations ran through more than " +
            _contentBytes.ToString("N0", CultureInfo.InvariantCulture) +
            " bytes of decoded content; the remaining forms and glyphs were not drawn. The invocation " +
            "count bounds how many times a form runs, not how large each run is: a form of two " +
            "thousand Do operators drawing itself is parsed whole at every invocation.";

        /// <summary>The operation refusal: what the nested runs executed, which their bytes do not price.</summary>
        internal string OperationsRefusalMessage =>
            "This page's form invocations ran more than " +
            _operations.ToString("N0", CultureInfo.InvariantCulture) +
            " operations (PdfLimits.MaxFormOperationsPerPage); the remaining forms, pattern cells and " +
            "glyphs were not drawn. An operation's cost is not its length: a two-byte q clones the " +
            "graphics state, and a fill is a microsecond or more.";
    }
}
