// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// A page's <c>/Contents</c> parts are each decoded under what is left of the ceiling their consumer
/// enforces — both content scanners and the optimizer's two passes that rewrite a page's content.
/// Decoded each up to <see cref="OptimizationOptions.MaxStreamDecodeBytes"/> and joined whole before
/// anything compared the total, a page listing many small Flate bombs, each under the ceiling, would
/// hold all of them at once, and past 2 GB the join would throw. And the glyph scan refuses a part it
/// cannot decode rather than skip it: the glyphs that part draws would otherwise be pruned as unused.
/// </summary>
public sealed class ContentPartCeilingTests
{
    private const int Part = 6 * 1024 * 1024;   // six MiB of zeros, a few kilobytes deflated
    private const int Parts = 20;               // 120 MiB joined; the scanners walk at most 4 or 8

    // ---------------------------------------------------------------- the font scanner

    [Fact]
    public void TheGlyphScan_RefusesAPageWhosePartsTogetherPassTheWalk_WithoutDecodingThemAll()
    {
        var (objects, trailer) = Page(BombParts(Parts));

        long before = GC.GetAllocatedBytesForCurrentThread();
        FontGlyphUsageScanner.Result result = FontGlyphUsageScanner.Scan(objects, trailer, new OptimizationOptions());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(result.Reason);
        Assert.Contains("larger than the scan could walk", result.Reason, StringComparison.Ordinal);
        // Two parts decoded at most, and the second twice: far below the 120 MiB (and the MemoryStream's
        // doubling above it) the whole join allocated.
        Assert.True(allocated < 48L * 1024 * 1024, "allocated " + allocated + " bytes");
    }

    [Fact]
    public void TheGlyphScan_RefusesAPageWithAPartItCannotDecode()
    {
        var (objects, trailer) = Page(Content("q Q"), Undecodable());

        FontGlyphUsageScanner.Result result = FontGlyphUsageScanner.Scan(objects, trailer, new OptimizationOptions());

        Assert.NotNull(result.Reason);
        Assert.Contains("could not be decoded", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>The control: two small readable parts are scanned whole, with no reason.</summary>
    [Fact]
    public void TheGlyphScan_ReadsAPageOfSmallParts()
    {
        var (objects, trailer) = Page(Content("q Q"), Content("q Q"));

        FontGlyphUsageScanner.Result result = FontGlyphUsageScanner.Scan(objects, trailer, new OptimizationOptions());

        Assert.Null(result.Reason);
    }

    // ---------------------------------------------------------------- the image scanner

    [Fact]
    public void TheImageScan_RefusesAPageWhosePartsTogetherPassTheWalk_WithoutDecodingThemAll()
    {
        var (objects, trailer) = Page(BombParts(Parts));

        long before = GC.GetAllocatedBytesForCurrentThread();
        ImagePlacementScanner.Scan(objects, trailer, new OptimizationOptions(), out string reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(reason);
        Assert.Contains("content parts together", reason, StringComparison.Ordinal);
        Assert.True(allocated < 48L * 1024 * 1024, "allocated " + allocated + " bytes");
    }

    /// <summary>
    /// A part that does not decode on its own is not a page too large to walk: the walk's ceiling is far
    /// below the per-stream one, so a refusal under it is asked again under the per-stream ceiling. The
    /// placement map skips such a part, as before, and reports nothing.
    /// </summary>
    [Fact]
    public void TheImageScan_SkipsAPartItCannotDecode_WithoutCallingThePageTooLarge()
    {
        var (objects, trailer) = Page(Content("q Q"), Undecodable());

        ImagePlacementScanner.Scan(objects, trailer, new OptimizationOptions(), out string reason);

        Assert.Null(reason);
    }

    // ---------------------------------------------------------------- the optimizer's join

    [Fact]
    public void TheOptimizersJoin_RefusesPartsThatTogetherPassTheCeiling()
    {
        var options = new OptimizationOptions { MaxStreamDecodeBytes = 1000 };
        var parts = new List<(int, PdfStream)> { (10, Flate(new byte[600])), (11, Flate(new byte[600])) };

        byte[] joined = PdfOptimizer.JoinContentParts(parts, options, out int failed, out string reason);

        Assert.Null(joined);
        Assert.Equal(11, failed);
        Assert.Contains("held together", reason, StringComparison.Ordinal);
    }

    /// <summary>The control: the same parts under a ceiling that holds both are joined, a newline after each.</summary>
    [Fact]
    public void TheOptimizersJoin_JoinsPartsWithinTheCeiling()
    {
        var options = new OptimizationOptions { MaxStreamDecodeBytes = 1300 };
        var parts = new List<(int, PdfStream)> { (10, Flate(new byte[600])), (11, Flate(new byte[600])) };

        byte[] joined = PdfOptimizer.JoinContentParts(parts, options, out int failed, out string reason);

        Assert.Null(reason);
        Assert.Equal(-1, failed);
        Assert.Equal(1202, joined.Length);
        Assert.Equal((byte)'\n', joined[600]);
        Assert.Equal((byte)'\n', joined[1201]);
    }

    // ---------------------------------------------------------------- fixtures

    private static PdfStream[] BombParts(int count)
    {
        var parts = new PdfStream[count];
        for (int i = 0; i < count; i++) parts[i] = Flate(new byte[Part]);   // NULs: white space to a parser
        return parts;
    }

    private static PdfStream Content(string text) => new PdfStream { RawData = Encoding.ASCII.GetBytes(text) };

    /// <summary>A Flate part declaring a decoded length past any ceiling: refused on the declaration.</summary>
    private static PdfStream Undecodable()
    {
        PdfStream s = Flate(new byte[16]);
        s.Set(PdfName.DL, new PdfNumber(1e12));
        return s;
    }

    private static PdfStream Flate(byte[] payload)
    {
        using var raw = new MemoryStream();
        raw.WriteByte(0x78);
        raw.WriteByte(0x9C);
        using (var deflate = new DeflateStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);

        var stream = new PdfStream { RawData = raw.ToArray() };
        stream.Set(PdfName.Filter, PdfName.FlateDecode);
        return stream;
    }

    private static (Dictionary<int, PdfObject> Objects, PdfDictionary Trailer) Page(params PdfStream[] parts)
    {
        var objects = new Dictionary<int, PdfObject>();

        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, new PdfName("Catalog"));
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        objects[1] = catalog;

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, new PdfName("Pages"));
        var kids = new PdfArray();
        kids.Add(new PdfReference(3, 0));
        pages.Set(PdfName.Kids, kids);
        pages.Set(PdfName.Count, new PdfNumber(1));
        objects[2] = pages;

        var contents = new PdfArray();
        for (int i = 0; i < parts.Length; i++)
        {
            objects[10 + i] = parts[i];
            contents.Add(new PdfReference(10 + i, 0));
        }

        var page = new PdfDictionary();
        page.Set(PdfName.Type, new PdfName("Page"));
        page.Set(PdfName.Parent, new PdfReference(2, 0));
        var box = new PdfArray();
        foreach (double v in new double[] { 0, 0, 612, 792 }) box.Add(new PdfNumber(v));
        page.Set(PdfName.MediaBox, box);
        page.Set(PdfName.Resources, new PdfDictionary());
        page.Set(PdfName.Contents, contents);
        objects[3] = page;

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        return (objects, trailer);
    }
}
