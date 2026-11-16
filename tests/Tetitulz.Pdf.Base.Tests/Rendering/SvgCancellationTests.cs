// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Cancellation on the SVG export path, which had none at all — and, since 2026-08-28, the raster
/// cases that make the pair symmetric.
/// </summary>
/// <remarks>
/// <para><b>The asymmetry this closes.</b> The raster interpreter polls its token every 256
/// operators and propagates it into nested forms and Type 3 glyphs, so a host can abandon a hostile
/// page. <c>PdfSvgRenderer</c> took no token on any of its four entry points and no
/// <c>SvgContentWriter</c> partial mentioned one — so the identical document was abandonable when
/// rendered and uninterruptible when exported. That is the repository's recurring defect class (one
/// renderer lagging the other) in its most consequential form: not a wrong pixel, but no way out.</para>
///
/// <para><b>Nothing here asserts a wall clock.</b> The token is cancelled before the call, so the
/// first poll throws deterministically; a guard test that measures elapsed time is the pattern this
/// repository bans, and a DoS test that finishes fast has quite possibly not run.</para>
/// </remarks>
public class SvgCancellationTests
{
    /// <summary>
    /// More operators than the polling interval, so the poll is reached at all. With fewer, a
    /// cancelled token would never be observed and this file would pass while asserting nothing.
    /// </summary>
    private const int OperatorCount = 2000;

    [Fact]
    public void SvgExport_CancelledMidStream_TerminatesWithOperationCanceled()
    {
        byte[] pdf = ManyOperatorsPdf();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var ms = new MemoryStream(pdf);
        Assert.ThrowsAny<OperationCanceledException>(
            () => PdfOperations.RenderPageToSvg(ms, 0, cts.Token));
    }

    /// <summary>
    /// A cancellation raised inside a nested form XObject must escape, not be fault-isolated into a
    /// diagnostic.
    /// </summary>
    /// <remarks>
    /// <b>Removing <c>catch (OperationCanceledException) { throw; }</c> fails BOTH assertions.</b>
    /// The cancellation has exactly one source here: the page content is a single <c>/F1 Do</c>, and
    /// <c>RenderCancellation.Poll</c> throws only every <c>CheckInterval</c> (256) operators, so the
    /// outer loop's own poll never fires. It is the <i>child</i> writer — which inherits the token
    /// and runs the form's 4 001 operators — that throws. Swallow that and nothing else raises, so
    /// the export returns normally with the form's cancellation converted into a
    /// <c>ContentStreamError</c> for a file that has no error in it.
    /// <para>An earlier revision of this comment claimed the first assertion survived that mutation
    /// "because the outer loop's own poll still throws on its next check". There is no next check —
    /// one operator never reaches 256. <b>Do not lengthen the page content to "fix" that</b>: the
    /// outer poll would then throw first, the rethrow this test exists for would stop executing, and
    /// the test would silently become a duplicate of
    /// <see cref="SvgExport_CancelledMidStream_TerminatesWithOperationCanceled"/> under a name
    /// claiming otherwise.</para>
    /// </remarks>
    [Fact]
    public void SvgExport_CancelledInsideAFormXObject_IsNotSwallowedAsAContentStreamError()
    {
        byte[] pdf = FormWithManyOperatorsPdf();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        int thread = Environment.CurrentManagedThreadId;
        var contentStreamErrors = new List<string>();
        Action<RenderDiagnosticKind, string, Exception> handler = (kind, detail, _) =>
        {
            if (Environment.CurrentManagedThreadId != thread) return;
            if (kind != RenderDiagnosticKind.ContentStreamError) return;
            lock (contentStreamErrors) contentStreamErrors.Add(detail ?? string.Empty);
        };

        RenderDiagnostics.OnEvent += handler;
        try
        {
            using var ms = new MemoryStream(pdf);
            Assert.ThrowsAny<OperationCanceledException>(
                () => PdfOperations.RenderPageToSvg(ms, 0, cts.Token));
        }
        finally
        {
            RenderDiagnostics.OnEvent -= handler;
        }

        Assert.DoesNotContain(contentStreamErrors, d =>
            d.IndexOf("threw and was skipped", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The raster twin of the test above: cancelling inside a form XObject must escape
    /// <c>RenderPageToPixels</c>, not be converted into a <c>ContentStreamError</c> the file does
    /// not have.
    /// </summary>
    /// <remarks>
    /// <para><b>It exists because the pair was asymmetric.</b> Both loops carry comments asserting
    /// they agree — the raster one says the poll cadence and the "poll OUTSIDE the try" rule are
    /// "one decision for both renderers", the SVG one says "same cadence and same shared constant
    /// as the raster interpreter" — and prose is not enforcement. The nested case had a test on the
    /// SVG side only, which is the shape (one renderer covered, the other trusted) this
    /// repository's recurring defect class is made of.</para>
    ///
    /// <para><b>The consequence on the raster side is worse than a fabricated diagnostic.</b> Its
    /// own comment spells it out: <c>OpDo</c> has already run <c>PushState</c> and
    /// <c>BeginTransparencyGroup</c> when the nested <c>Execute</c> throws, so swallowing the
    /// cancellation unwinds past the matching <c>Pop</c>/<c>End</c> and the parent keeps drawing
    /// with an unbalanced graphics-state and layer stack — every later operator on the page lands
    /// somewhere wrong.</para>
    ///
    /// <para><b>The token is cancelled DURING the render, not before, and that is forced.</b>
    /// <c>PdfPageRenderer</c> calls <c>ThrowIfCancellationRequested</c> at six entry points, so a
    /// pre-cancelled token — which is all the SVG sibling needs — never reaches the interpreter at
    /// all: the render throws from the entry guard and the loop under test does not run. A first
    /// draft of this test did exactly that, passed, and <b>passed under the mutation too</b>, which
    /// is the "satisfied by the wrong mechanism" failure. Cancelling from inside the diagnostics
    /// handler is deterministic — no wall clock — and lands the cancellation where the nested poll
    /// will see it.</para>
    ///
    /// <para>The second assertion is the load-bearing one. Even cancelled mid-render, a later entry
    /// guard could raise <c>OperationCanceledException</c> on its own, so the throw alone would not
    /// prove the loop rethrew. What only the rethrow can produce is the <i>absence</i> of a
    /// fabricated <c>ContentStreamError</c>: swallow the cancellation and the parent reports
    /// <c>Operator '…' threw: …</c> for a file that has no error in it.</para>
    ///
    /// <para>Same fixture shape as its sibling: the page has one operator, so the outer loop's own
    /// poll never reaches the 256-operator interval and it is the <i>child</i> interpreter that
    /// throws. <b>Do not lengthen the page content</b> — the outer poll would fire first and this
    /// would silently become a duplicate of
    /// <see cref="CancelledToken_IsHonouredByBothRenderers"/>.</para>
    /// </remarks>
    [Fact]
    public void RasterRender_CancelledInsideAFormXObject_IsNotSwallowedAsAContentStreamError()
    {
        byte[] pdf = FormWithAFailingFirstOperatorPdf();

        // NOT pre-cancelled: see the remarks. The handler below cancels it from inside the render.
        using var cts = new CancellationTokenSource();

        int thread = Environment.CurrentManagedThreadId;
        var contentStreamErrors = new List<string>();
        Action<RenderDiagnosticKind, string, Exception> handler = (kind, detail, _) =>
        {
            // Thread-filtered: RenderDiagnostics.OnEvent is process-wide, so an unfiltered handler
            // would make this an assertion about every concurrent render in the test host.
            if (Environment.CurrentManagedThreadId != thread) return;

            cts.Cancel();

            if (kind != RenderDiagnosticKind.ContentStreamError) return;
            lock (contentStreamErrors) contentStreamErrors.Add(detail ?? string.Empty);
        };

        RenderDiagnostics.OnEvent += handler;
        try
        {
            using var ms = new MemoryStream(pdf);
            Assert.ThrowsAny<OperationCanceledException>(
                () => PdfPageRenderer.RenderPageToPixels(ms, 0, 72, null, cts.Token));
        }
        finally
        {
            RenderDiagnostics.OnEvent -= handler;
        }

        // The raster loop's message is "Operator 'X' threw: …"; the SVG one's is "… threw and was
        // skipped". Matching each renderer's own wording is what makes these two tests distinct
        // rather than one test run twice.
        Assert.DoesNotContain(contentStreamErrors, d =>
            d.IndexOf("threw:", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// Both renderers must abandon the same document on the same terms. Rendering and exporting are
    /// two answers to one question, and a host that can escape one and not the other has no usable
    /// timeout at all.
    /// </summary>
    [Fact]
    public void CancelledToken_IsHonouredByBothRenderers()
    {
        byte[] pdf = ManyOperatorsPdf();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var rasterStream = new MemoryStream(pdf);
        var raster = Record.Exception(() =>
            PdfPageRenderer.RenderPageToPixels(rasterStream, 0, 72, null, cts.Token));

        using var svgStream = new MemoryStream(pdf);
        var svg = Record.Exception(() =>
            PdfOperations.RenderPageToSvg(svgStream, 0, cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(raster);
        Assert.IsAssignableFrom<OperationCanceledException>(svg);
    }

    /// <summary>
    /// The floor. An uncancelled export must still produce a page, or every assertion above passes
    /// against a renderer that has simply stopped working.
    /// </summary>
    [Fact]
    public void UncancelledExport_StillProducesTheDrawnContent()
    {
        byte[] pdf = ManyOperatorsPdf();

        using var ms = new MemoryStream(pdf);
        string svg = PdfOperations.RenderPageToSvg(ms, 0, CancellationToken.None);

        // Parsed as XML, never substring-matched: this suite shipped an export that was not
        // well-formed XML for its entire history because every assertion was a substring search.
        var doc = System.Xml.Linq.XDocument.Parse(svg);
        Assert.NotNull(doc.Root);

        // The rectangles really reached the output, so the fixture exercises the loop it claims to.
        int rects = 0;
        foreach (var e in doc.Root.Descendants())
            if (e.Name.LocalName == "path" || e.Name.LocalName == "rect") rects++;

        Assert.True(rects > 0, "The export produced no drawn element, so the fixture draws nothing.");
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A page whose content stream is far longer than the cancellation-polling interval.</summary>
    private static byte[] ManyOperatorsPdf()
    {
        var content = new StringBuilder("0 g\n");
        for (int i = 0; i < OperatorCount; i++)
            content.Append(CultureInfo.InvariantCulture, $"{i % 100} {i % 50} 3 3 re f\n");

        return Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>"),
            StreamObj(4, "", content.ToString()),
        });
    }

    /// <summary>
    /// A page whose own content is short — under the polling interval — and which draws a form whose
    /// content is long. The first poll therefore falls inside the nested Execute, which is what makes
    /// the swallowing test meaningful.
    /// </summary>
    private static byte[] FormWithManyOperatorsPdf()
    {
        var formContent = new StringBuilder("0 g\n");
        for (int i = 0; i < OperatorCount; i++)
            formContent.Append(CultureInfo.InvariantCulture, $"{i % 100} {i % 50} 3 3 re f\n");

        return Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                 + "/Resources << /XObject << /F1 5 0 R >> >> >>"),
            StreamObj(4, "", "/F1 Do\n"),
            StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200]", formContent.ToString()),
        });
    }

    /// <summary>
    /// As <see cref="FormWithManyOperatorsPdf"/>, but the form's <b>first</b> operator invokes a
    /// PostScript XObject — which, with no <c>IPostScriptPlugin</c> registered, is documented to
    /// draw a hatched placeholder and report <c>PostScriptUnsupported</c>. That report is the raster
    /// test's deterministic cancellation trigger; the 2 000 operators after it are what carry the
    /// child interpreter past its 256-operator poll interval.
    /// </summary>
    /// <remarks>
    /// A missing XObject was tried first and reports <b>nothing</b> — the render simply skips it —
    /// so the trigger has to be a case the renderer is specified to announce.
    /// </remarks>
    private static byte[] FormWithAFailingFirstOperatorPdf()
    {
        var formContent = new StringBuilder("/PS1 Do\n0 g\n");
        for (int i = 0; i < OperatorCount; i++)
            formContent.Append(CultureInfo.InvariantCulture, $"{i % 100} {i % 50} 3 3 re f\n");

        const string psProgram = "0 0 moveto 10 10 lineto stroke\n";

        return Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                 + "/Resources << /XObject << /F1 5 0 R >> >> >>"),
            StreamObj(4, "", "/F1 Do\n"),
            StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                       + "/Resources << /XObject << /PS1 6 0 R >> >>", formContent.ToString()),
            StreamObj(6, " /Type /XObject /Subtype /PS", psProgram),
        });
    }

    private static string Obj(int num, string body) => $"{num} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int num, string extraKeys, string content) =>
        $"{num} 0 obj\n<<{extraKeys} /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n";

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        foreach (string body in bodies)
        {
            offsets.Add(sb.Length);
            sb.Append(body);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
