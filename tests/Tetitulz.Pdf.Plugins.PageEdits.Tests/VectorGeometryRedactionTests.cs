// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Redaction removes painted vector geometry that lies <b>wholly inside</b> a region, and leaves
/// everything else exactly where it was (plan 08 item 47).
/// </summary>
/// <remarks>
/// <para>
/// Vector geometry carries no extractable text or image data, so this is not a leak fix — the cover
/// box already hides it. What it buys is a file that no longer contains the <i>shape</i> of what was
/// removed: a signature box, a chart bar, a highlight rule.
/// </para>
/// <para>
/// <b>The two negative tests are the important ones.</b> Containment rather than intersection is the
/// whole design: a rule crossing the region belongs to the rest of the page, and dropping a
/// <c>W</c>/<c>W*</c> clip would <i>widen</i> what later operators paint — the opposite of
/// redaction. An implementation that used intersection, or that treated a clip like any other path,
/// passes the first test and fails these.
/// </para>
/// </remarks>
public class VectorGeometryRedactionTests
{
    // The region under test: x 100..200, y 100..200.
    private static RedactionRegion Region() =>
        new RedactionRegion { PageIndex = 0, X = 100, Y = 100, Width = 100, Height = 100 };

    [Fact]
    public void RedactRegions_RectangleWhollyInsideTheRegion_IsRemovedFromTheContentStream()
    {
        // 120,120 → 60×60: entirely within 100..200.
        byte[] pdf = BuildPdfWithPathContent("120 120 60 60 re f");

        string content = RedactAndReadContent(pdf);

        Assert.DoesNotContain("120 120 60 60 re", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rule that crosses the region belongs mostly to the rest of the page. Intersection-based
    /// removal would delete it; containment keeps it.
    /// </summary>
    [Fact]
    public void RedactRegions_RectangleMerelyOverlappingTheRegion_IsKept()
    {
        // 50,140 → 400×10: a horizontal rule straight through the region and far beyond it.
        byte[] pdf = BuildPdfWithPathContent("50 140 400 10 re f");

        string content = RedactAndReadContent(pdf);

        Assert.Contains("50 140 400 10 re", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror of the test above: a <b>vertical</b> rule, inside the region horizontally and far
    /// outside it vertically. Containment is a conjunction over both axes, and the horizontal case
    /// alone cannot show that — it fails on X, so the Y half of the test is never what decides it.
    /// Dropping the Y clauses leaves the horizontal rule correctly kept and deletes this one, which
    /// is <b>over</b>-redaction: content the caller never named, destroyed by a single-revision
    /// rewrite that cannot be undone.
    /// </summary>
    [Fact]
    public void RedactRegions_VerticalRuleMerelyOverlappingTheRegion_IsKept()
    {
        // 140,50 → 10×400: straight through the region and far beyond it, on the other axis.
        byte[] pdf = BuildPdfWithPathContent("140 50 10 400 re f");

        string content = RedactAndReadContent(pdf);

        Assert.Contains("140 50 10 400 re", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same geometry as the first test, but the path both <b>clips and paints</b> (<c>W f</c>).
    /// Dropping it would widen the clip for everything after, revealing content the caller never
    /// asked to expose.
    /// </summary>
    /// <remarks>
    /// <b>`W f`, not the more common `W n`, and the difference is the whole test.</b> `n` paints
    /// nothing, so it is already excluded from removal on its own — a fixture using it passes even
    /// with the clip guard deleted, proving only that `n` is skipped. Mutation testing caught
    /// exactly that: removing `!_hasClip` failed nothing until this fixture was changed to a paint
    /// operator that <i>is</i> a removal candidate.
    /// </remarks>
    [Fact]
    public void RedactRegions_PathThatClipsAndPaints_IsKeptEvenWhollyInsideTheRegion()
    {
        byte[] pdf = BuildPdfWithPathContent("120 120 60 60 re W f");

        string content = RedactAndReadContent(pdf);

        Assert.Contains("120 120 60 60 re", content, StringComparison.Ordinal);
        Assert.Contains("W", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fixture-validity control: without it, a redactor that emitted an empty content stream would
    /// pass every "DoesNotContain" assertion above.
    /// </summary>
    [Fact]
    public void RedactRegions_GeometryOutsideTheRegion_SurvivesUntouched()
    {
        byte[] pdf = BuildPdfWithPathContent("300 300 40 40 re f");

        string content = RedactAndReadContent(pdf);

        Assert.Contains("300 300 40 40 re", content, StringComparison.Ordinal);
    }

    private static string RedactAndReadContent(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[] { Region() });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData() ?? Array.Empty<byte>());
    }

    /// <summary>One page whose content stream is exactly <paramref name="pathContent"/>.</summary>
    private static byte[] BuildPdfWithPathContent(string pathContent)
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string content = "q 0 0 1 rg " + pathContent + " Q";

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
