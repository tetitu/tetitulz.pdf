// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfGeoReader"/> — geospatial viewports (§8.8.2 / PDF 2.0 §14.7). A public entry point,
/// reached through <c>PdfOperations.ReadGeo</c>, that <b>no test called</b>.
/// </summary>
/// <remarks>
/// <para>The reader's whole job is to report what a page declares, so its failure mode is a silent
/// omission: a viewport that is present and comes back absent, or a measure whose subtype comes back
/// <see langword="null"/>. Neither throws, and a caller has nothing to compare against.</para>
///
/// <para><b>One defect closed here.</b> Every read in the walk resolved its reference except the
/// measure's <c>/Subtype</c>, which used the raw indexer — and the indexer does not follow an
/// indirect reference. A conforming file writing <c>/Subtype 9 0 R</c> (§7.3.10 permits it
/// anywhere) reported a viewport with no subtype, indistinguishable from a <c>/Measure</c> that
/// never named one. The surrounding lines already showed the right shape.</para>
/// </remarks>
public class GeoViewportReaderTests
{
    [Fact]
    public void AViewportIsReportedWithItsBoxAndMeasureSubtype()
    {
        var vp = Assert.Single(Read(WithViewport("/Subtype /GEO")));

        Assert.Equal(0, vp.PageIndex);
        Assert.Equal(new[] { 0f, 0f, 100f, 200f }, vp.Bbox);
        Assert.Equal("GEO", vp.MeasureSubtype);
        Assert.NotNull(vp.Measure);
    }

    /// <summary>
    /// §7.3.10 lets the subtype be an indirect reference. A non-resolving read answers
    /// <see langword="null"/>, which the caller cannot tell from a measure that declares no subtype.
    /// </summary>
    [Fact]
    public void AMeasureSubtypeWrittenIndirectlyIsStillReported()
    {
        var vp = Assert.Single(Read(WithViewport("/Subtype 9 0 R", extraObject: "/GEO")));

        Assert.Equal("GEO", vp.MeasureSubtype);
    }

    /// <summary>
    /// A malformed box is dropped, but the viewport is still reported — losing the whole record
    /// because one of its entries is unusable would hide a viewport the page really declares.
    /// </summary>
    [Fact]
    public void AShortBoundingBoxLeavesTheViewportReportedWithoutOne()
    {
        var vp = Assert.Single(Read(WithViewport("/Subtype /GEO", bbox: "[0 0 100]")));

        Assert.Null(vp.Bbox);
        Assert.Equal("GEO", vp.MeasureSubtype);
    }

    /// <summary>
    /// <b>Malformed input:</b> a <c>/VP</c> array element that is not a dictionary is skipped, and
    /// the well-formed viewport beside it is still reported. Reading it regardless dereferences
    /// null on a file the reader is supposed to survive.
    /// </summary>
    [Fact]
    public void AMalformedViewportEntryIsSkippedAndItsNeighbourStillRead()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // 42 is not a viewport dictionary; object 4 is.
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /VP [42 4 0 R] >>",
            "<< /Type /Viewport /BBox [0 0 100 200] /Measure << /Type /Measure /Subtype /GEO >> >>");

        var vp = Assert.Single(Read(pdf));

        Assert.Equal("GEO", vp.MeasureSubtype);
    }

    /// <summary>The control: a page declaring no <c>/VP</c> yields nothing.</summary>
    [Fact]
    public void APageWithNoViewportsYieldsNone()
    {
        Assert.Empty(Read(Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")));
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<PdfGeoReader.Viewport> Read(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfGeoReader.ReadViewports(ms);
    }

    /// <summary>
    /// One page with a <c>/VP</c> array holding a single viewport. <paramref name="measureEntries"/>
    /// goes inside the <c>/Measure</c> dictionary; <paramref name="extraObject"/> becomes object 9
    /// when a fixture needs something to point at.
    /// </summary>
    private static byte[] WithViewport(
        string measureEntries, string bbox = "[0 0 100 200]", string extraObject = null)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /VP [4 0 R] >>",
            "<< /Type /Viewport /BBox " + bbox + " /Measure << /Type /Measure " +
                measureEntries + " >> >>",
        };
        // Pad to object 9 so a fixture can name `9 0 R` without counting.
        while (bodies.Count < 8) bodies.Add("<< /Type /Filler >>");
        if (extraObject != null) bodies.Add(extraObject);

        return Build(bodies.ToArray());
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
