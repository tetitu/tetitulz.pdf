// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The two renderers must charge the page budget for the <b>same operations</b>, not merely produce
/// the same refusal text.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists beside <c>RenderFormXObjectBudgetTests</c>.</b> That suite's parity
/// assertion compares <c>BudgetDetail(raster)</c> with <c>BudgetDetail(svg)</c>, and the only string
/// either can return is <c>RenderBudget.RefusalMessage</c> — a constant built from
/// <c>PdfLimits.MaxFormXObjectInvocationsPerPage</c>. Once the two <c>Assert.NotNull</c> calls pass,
/// the comparison is one constant against itself and cannot fail. Worse, because the message is
/// derived from the constant rather than from the budget actually in force, it cannot differ even
/// when the enforced limits do.</para>
///
/// <para><b>What it hid.</b> Raster charged at two sites — the <c>Do</c> invocation and
/// <c>RenderToBitmap</c>, which is how a soft-mask group and a tiling-pattern cell reach the budget —
/// while the SVG writer charged only at <c>Do</c>. Its mask and pattern builds were guarded by the
/// recursion depth cap alone, and neither is memoised, so a mask group whose content is
/// <c>/M2 gs /M2 gs</c> is the identical doubling chain <c>RenderBudget</c> exists for: it repeats no
/// name, exceeds no depth cap, and ran unbudgeted in the export while raster stopped at 100 000.</para>
///
/// <para><b>Why the budget is injected rather than exhausted.</b> Reaching the real 100 000 through
/// the public entry points needs a doubling chain, and on the SVG side every charge emits a
/// <c>&lt;mask&gt;</c> or <c>&lt;pattern&gt;</c> definition carrying the group's content — tens of
/// megabytes of markup for one assertion. Base declares
/// <c>InternalsVisibleTo("Tetitulz.Pdf.Base.Tests")</c>, so a budget of 1 gives the same separation
/// in milliseconds. The number under test is "was this operation charged", which is scale-free.</para>
/// </remarks>
public class RenderBudgetChargeParityTests
{
    /// <summary>Applies the same soft mask twice. With a budget of 1 the second must be refused.</summary>
    private const string SoftMaskTwice = "/GS1 gs 0 0 10 10 re f /GS1 gs 0 0 10 10 re f";

    /// <summary>Paints the same tiling pattern twice. With a budget of 1 the second must be refused.</summary>
    private const string PatternTwice =
        "/Pattern cs /P1 scn 0 0 10 10 re f /Pattern cs /P1 scn 20 20 10 10 re f";

    /// <summary>
    /// Both renderers charge a soft-mask group build, and both refuse the second one under a budget
    /// of 1. Before the fix the SVG writer charged nothing here and completed silently.
    /// </summary>
    [Fact]
    public void SoftMaskGroupBuild_IsChargedByBothRenderers()
    {
        Assert.True(RasterRefuses(SoftMaskTwice), "the raster path must charge a soft-mask build");
        Assert.True(SvgRefuses(SoftMaskTwice), "the SVG writer must charge a soft-mask build too");
    }

    /// <summary>
    /// The same for a tiling-pattern cell. Raster reaches the budget through <c>RenderToBitmap</c>;
    /// the SVG writer builds a <c>&lt;pattern&gt;</c> and used to charge nothing for it.
    /// </summary>
    [Fact]
    public void TilingPatternCellBuild_IsChargedByBothRenderers()
    {
        Assert.True(RasterRefuses(PatternTwice), "the raster path must charge a pattern-cell build");
        Assert.True(SvgRefuses(PatternTwice), "the SVG writer must charge a pattern-cell build too");
    }

    /// <summary>
    /// The floor, and it is what stops the two tests above passing for the wrong reason. A budget
    /// large enough for both operations must refuse <b>nothing</b> — otherwise "it reported a
    /// refusal" could be satisfied by a renderer that refuses unconditionally, which is exactly as
    /// wrong as one that never refuses.
    /// </summary>
    [Theory]
    [InlineData(SoftMaskTwice)]
    [InlineData(PatternTwice)]
    public void WithBudgetForBoth_NeitherRendererRefuses(string content)
    {
        Assert.False(RasterRefuses(content, budget: 8), "raster refused inside its budget");
        Assert.False(SvgRefuses(content, budget: 8), "SVG refused inside its budget");
    }

    // ------------------------------------------------------------------ harness

    private static bool RasterRefuses(string content, int budget = 1)
    {
        var reported = Capture(() =>
        {
            using var doc = Open();
            using var bmp = new SKBitmap(40, 40, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            using var ctx = new PdfRenderContext(canvas, 40f, 40f, 1f);
            using var fonts = new FontCache();

            var interp = new ContentStreamInterpreter(ctx, doc, Resources(doc), fonts)
                { FormBudget = new RenderBudget(budget) };
            interp.Execute(Encoding.ASCII.GetBytes(content));
        });
        return reported;
    }

    private static bool SvgRefuses(string content, int budget = 1)
    {
        var reported = Capture(() =>
        {
            using var doc = Open();
            var writer = new SvgContentWriter(new StringBuilder(), doc, Resources(doc), 40f, 40f)
                { Shared = new SvgSharedState { FormBudget = new RenderBudget(budget) } };
            writer.Execute(Encoding.ASCII.GetBytes(content));
        });
        return reported;
    }

    /// <summary>
    /// True when a budget refusal was reported <b>on this thread</b>. <c>RenderDiagnostics.OnEvent</c>
    /// is process-wide, so an unfiltered handler would make this a claim about the whole test run.
    /// </summary>
    private static bool Capture(Action act)
    {
        int thread = Environment.CurrentManagedThreadId;
        bool refused = false;

        Action<RenderDiagnosticKind, string, Exception> handler = (_, detail, __) =>
        {
            if (Environment.CurrentManagedThreadId != thread) return;
            if (detail != null &&
                detail.IndexOf("form-XObject invocations", StringComparison.OrdinalIgnoreCase) >= 0)
                refused = true;
        };

        RenderDiagnostics.OnEvent += handler;
        try { act(); }
        finally { RenderDiagnostics.OnEvent -= handler; }

        return refused;
    }

    // ------------------------------------------------------------------ fixture

    private static PdfDocument Open() => PdfDocument.Open(new MemoryStream(Pdf()));

    private static PdfDictionary Resources(PdfDocument doc) =>
        doc.Pages[0].Resources;

    /// <summary>
    /// One page carrying both a soft mask (<c>/GS1</c>, whose <c>/SMask /G</c> is a real group) and a
    /// tiling pattern (<c>/P1</c>). The two content streams above draw against these resources.
    /// </summary>
    private static byte[] Pdf()
    {
        const string group = "0 0 0 rg 0 0 10 10 re f\n";
        const string cell  = "1 0 0 rg 0 0 4 4 re f\n";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Contents 4 0 R /Resources " +
            "<< /ExtGState << /GS1 5 0 R >> /Pattern << /P1 7 0 R >> >> >>",
            Stream("", "\n"),
            "<< /Type /ExtGState /SMask << /S /Luminosity /G 6 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Group << /S /Transparency " +
            "/CS /DeviceGray >> /Resources << >> /Length " + group.Length + " >>\nstream\n" +
            group + "endstream",
            "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 4 4] " +
            "/XStep 4 /YStep 4 /Resources << >> /Length " + cell.Length + " >>\nstream\n" +
            cell + "endstream");
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
