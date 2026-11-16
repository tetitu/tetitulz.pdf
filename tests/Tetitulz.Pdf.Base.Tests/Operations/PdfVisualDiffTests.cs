// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The three parts of <see cref="PdfVisualDiff"/>'s verdict that its existing tests do not reach.
/// </summary>
/// <remarks>
/// <para><b>What is already covered, so this does not repeat it:</b>
/// <c>Wave2FacadeTests</c> pins identical documents as equal and a black rectangle as unequal, and
/// <c>EncryptedVerdictRefusalTests</c> pins the encrypted-side refusal. What none of them reach is
/// the <b>page-count mismatch</b> branch, the <b>threshold</b> that decides the verdict, and the
/// zero-total guard on <see cref="PdfVisualDiff.PageDiff.DifferencePct"/>.</para>
///
/// <para>These matter because <c>AreEqual</c> is a <b>verdict</b>: its failure mode is not an
/// exception but a confident wrong answer, and a caller told two documents match ships the wrong
/// one. The rendered comparison is deliberately run at a coarse dpi and asserted on which side of
/// the threshold a page lands — never on an exact pixel count, which is a rasteriser detail that
/// would make this a change-detector.</para>
/// </remarks>
public class PdfVisualDiffTests
{
    /// <summary>
    /// A page-count mismatch makes the verdict false <b>on its own</b>, and both counts are
    /// reported. The pages the two documents share can be pixel-identical and they are still not
    /// the same document — so a verdict derived only from the per-page differences would say yes.
    /// </summary>
    [Fact]
    public void Compare_DifferentPageCounts_IsNotEqualAndReportsBothCounts()
    {
        using var l = new MemoryStream(OnePage());
        using var r = new MemoryStream(TwoPages());
        var result = PdfVisualDiff.Compare(l, r, dpi: 36);

        Assert.False(result.AreEqual);
        Assert.Equal(1, result.PageCountMismatchLeft);
        Assert.Equal(2, result.PageCountMismatchRight);

        // Only the shared page is compared, and it is identical — which is exactly why the count
        // has to be what decides this.
        Assert.Equal(0, Assert.Single(result.Pages).DifferingPixels);
    }

    /// <summary>
    /// The threshold is the caller's dial and is applied per page: the same pair is unequal at a
    /// strict threshold and equal at a permissive one, with the measured difference unchanged.
    /// </summary>
    [Fact]
    public void Compare_ThresholdDecidesTheVerdictForTheSamePair()
    {
        byte[] plain = PageWithContent("");
        byte[] marked = PageWithContent("0 0 0 rg 50 50 40 40 re f");

        using var l1 = new MemoryStream(plain);
        using var r1 = new MemoryStream(marked);
        var strict = PdfVisualDiff.Compare(l1, r1, dpi: 36, thresholdPct: 0f);

        using var l2 = new MemoryStream(plain);
        using var r2 = new MemoryStream(marked);
        var permissive = PdfVisualDiff.Compare(l2, r2, dpi: 36, thresholdPct: 100f);

        Assert.False(strict.AreEqual);
        Assert.True(permissive.AreEqual);

        // The threshold is the only thing that moved: the measurement is identical.
        Assert.Equal(strict.Pages[0].DifferingPixels, permissive.Pages[0].DifferingPixels);
        Assert.True(strict.Pages[0].DifferingPixels > 0, "the fixture must actually differ");
    }

    /// <summary>A page that rendered nothing reports 0 %, rather than dividing by zero.</summary>
    [Fact]
    public void PageDiff_WithNoPixels_ReportsZeroPercent()
    {
        var diff = new PdfVisualDiff.PageDiff(0, differingPixels: 0, totalPixels: 0);

        Assert.Equal(0f, diff.DifferencePct);
    }

    /// <summary>The control: with pixels, it really is their share of the total.</summary>
    [Fact]
    public void PageDiff_ReportsTheShareOfPixelsThatDiffer()
    {
        var diff = new PdfVisualDiff.PageDiff(0, differingPixels: 25, totalPixels: 100);

        Assert.Equal(25f, diff.DifferencePct);
    }

    /// <summary>
    /// Two pages of the same area and different shapes — a portrait page against a landscape one —
    /// are not equal: what lies outside the rectangle both cover has no counterpart on the other
    /// side, and all of it counts as differing. The difference of the two areas is 0 here, so a
    /// verdict from the common rectangle alone would say equal. The control, two blank pages of
    /// one shape, differs nowhere.
    /// </summary>
    [Fact]
    public void Compare_SameAreaDifferentShapes_IsNotEqual()
    {
        using var l = new MemoryStream(PageOf(200, 400, ""));
        using var r = new MemoryStream(PageOf(400, 200, ""));
        var result = PdfVisualDiff.Compare(l, r, dpi: 72);

        Assert.False(result.AreEqual);
        var page = Assert.Single(result.Pages);
        Assert.True(page.DifferingPixels > 0, "the strips outside the common square must count as differing");
        // Each side's strip outside the 200 × 200 square: 200 × 200 twice, out of 200 × 400 × 2 − 200 × 200.
        Assert.Equal(200 * 200 * 2, page.DifferingPixels);
        Assert.Equal(200 * 400 * 2 - 200 * 200, page.TotalPixels);

        using var l2 = new MemoryStream(PageOf(200, 400, ""));
        using var r2 = new MemoryStream(PageOf(200, 400, ""));
        var control = PdfVisualDiff.Compare(l2, r2, dpi: 72);
        Assert.True(control.AreEqual);
        Assert.Equal(0, Assert.Single(control.Pages).DifferingPixels);
    }

    // ------------------------------------------------------------------ fixtures

    private static byte[] PageOf(int width, int height, string content) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << >> >>",
        "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream");

    private static byte[] OnePage() => PageWithContent("");

    private static byte[] PageWithContent(string content) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream");

    private static byte[] TwoPages() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>");

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
