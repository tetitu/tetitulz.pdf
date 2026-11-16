// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Flattening only where a page paints transparently
/// (<see cref="TransparencyFlattenOptions.KeepOpaqueContentAsVector"/>): the opaque content stays
/// vector, what painted transparently is taken out of it, and an image covers just its bounds.
/// </summary>
/// <remarks>
/// The fixture is <see cref="TransparencyFlattenerTests"/>' page: an opaque blue square on the left
/// (0–100 × 0–100), and a red rectangle at half alpha across the middle (50–150 × 25–75).
/// </remarks>
public class RegionFlatteningTests
{
    private const string Art = "0 0 1 rg 0 0 100 100 re f /GS0 gs 1 0 0 rg 50 25 100 50 re f\n";
    private const string HalfAlpha = "/ExtGState << /GS0 << /ca 0.5 >> >>";
    private const string Helvetica = " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>";

    private static TransparencyFlattenOptions Regions(bool text = false)
        => new TransparencyFlattenOptions { Dpi = 72, KeepOpaqueContentAsVector = true, KeepTextSearchable = text };

    // ---------------------------------------------------------------- the page as before

    /// <summary>
    /// A page flattened by regions renders as the original did — at every rotation, with a crop box
    /// away from the origin — keeps its opaque content as vector, and holds no transparency.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void APageFlattenedByRegions_RendersAsTheOriginalDid(int rotate)
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha, Art.Replace("0 0 100 100 re", "10 5 100 100 re"),
            pageExtras: $"/Rotate {rotate} /CropBox [10 5 210 105] /Group << /S /Transparency /CS /DeviceRGB >>",
            mediaBox: "[-20 -10 240 120]");
        var (flattened, result) = TransparencyFlattenerTests.Flatten(original, Regions());

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        Assert.Equal(new[] { 0 }, result.PagesKeptAsVector);
        Assert.Empty(result.Remaining);
        Assert.Empty(PdfTransparencyFlattener.FindTransparency(new MemoryStream(flattened)));
        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);
    }

    /// <summary>
    /// The blue square is still a filled path; the red rectangle's paint is <c>n</c> now; and the image
    /// covers the red rectangle and a pixel round it — 102 × 52 at 72 dpi — not the 200 × 100 page.
    /// </summary>
    [Fact]
    public void TheOpaqueSquare_StaysVector_AndTheImageCoversOnlyTheTransparentRectangle()
    {
        var (flattened, _) = TransparencyFlattenerTests.Flatten(TransparencyFlattenerTests.Pdf(HalfAlpha, Art), Regions());

        var ops = PageOps(flattened);
        Assert.Single(ops, o => o.Operator == "f");
        int red = ops.FindIndex(o => o.Operator == "re" && Number(o, 0) == 50);
        Assert.Equal("n", ops[red + 1].Operator);

        PdfStream image = Images(flattened).Single();
        Assert.Equal(102, ((PdfNumber)image[PdfName.Width]).IntValue);
        Assert.Equal(52, ((PdfNumber)image[PdfName.Height]).IntValue);
    }

    /// <summary>The CMYK option crops its image the same way, in DeviceCMYK.</summary>
    [Fact]
    public void TheCmykOption_CropsItsImageTheSameWay()
    {
        var options = Regions();
        options.ColorSpace = FlattenedColorSpace.Cmyk;
        var (flattened, result) = TransparencyFlattenerTests.Flatten(TransparencyFlattenerTests.Pdf(HalfAlpha, Art), options);

        Assert.Equal(new[] { 0 }, result.PagesKeptAsVector);
        PdfStream image = Images(flattened).Single();
        Assert.Equal(PdfName.DeviceCMYK, image[PdfName.ColorSpace]);
        Assert.Equal(102, ((PdfNumber)image[PdfName.Width]).IntValue);
    }

    /// <summary>
    /// A page whose only transparency is its group keeps all of itself as vector: no image, no group,
    /// the same picture.
    /// </summary>
    [Fact]
    public void APageWhoseOnlyTransparencyIsItsGroup_KeepsAllOfItselfAsVector()
    {
        byte[] original = TransparencyFlattenerTests.Pdf("", "0 0 1 rg 0 0 100 100 re f\n", pageExtras: "/Group << /S /Transparency /CS /DeviceRGB >>");
        var (flattened, result) = TransparencyFlattenerTests.Flatten(original, Regions());

        Assert.Equal(new[] { 0 }, result.PagesKeptAsVector);
        Assert.Empty(Images(flattened));
        using (var doc = PdfDocument.Open(new MemoryStream(flattened)))
            Assert.Null(doc.Pages[0].Dictionary[PdfName.Group]);
        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);
    }

    /// <summary>An unused transparent graphics state is made opaque, since a validator reads it unused or not.</summary>
    [Fact]
    public void AnUnusedTransparentGraphicsState_IsMadeOpaque()
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha, "0 0 1 rg 0 0 100 100 re f\n");
        var (flattened, result) = TransparencyFlattenerTests.Flatten(original, Regions());

        Assert.Equal(new[] { 0 }, result.PagesKeptAsVector);
        Assert.Empty(PdfTransparencyFlattener.FindTransparency(new MemoryStream(flattened)));
        Assert.Empty(Images(flattened));
    }

    /// <summary>
    /// Content that leaves its state dirty — a <c>Q</c> with no <c>q</c>, a <c>cm</c> outside any
    /// <c>q</c>, a <c>q</c> never closed, a text object never ended — still gets its image drawn in the
    /// page's initial state.
    /// </summary>
    [Theory]
    [InlineData("Q 0.5 0 0 0.5 0 0 cm q ", "")]
    [InlineData("", "BT /F1 12 Tf 10 80 Td (Open) Tj ")]
    public void ContentThatLeavesItsStateDirty_StillGetsItsImageInThePagesInitialState(string before, string after)
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha + Helvetica, before + Art + after);
        var (flattened, _) = TransparencyFlattenerTests.Flatten(original, Regions());

        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);
        var ops = PageOps(flattened);
        Assert.Equal(ops.Count(o => o.Operator == "BT"), ops.Count(o => o.Operator == "ET"));

        // Balanced as written, not only as this renderer forgives: it ignores a Q with no q, and a
        // viewer need not, so no restore may come before its save.
        int depth = 0;
        foreach (var op in ops)
        {
            if (op.Operator == "q") depth++;
            if (op.Operator == "Q") depth--;
            Assert.True(depth >= 0, "a Q with no q");
        }
        Assert.Equal(0, depth);
    }

    /// <summary>
    /// Each transparent entry of a graphics state is taken out of it — a stroke alpha, a blend mode, a
    /// soft mask as well as a fill alpha — and what it painted is flattened.
    /// </summary>
    [Theory]
    [InlineData("/CA 0.5")]
    [InlineData("/BM /Multiply")]
    [InlineData("/SMask << /S /Luminosity /G 9 0 R >>")]
    public void EachTransparentEntry_IsTakenOutOfTheGraphicsState(string entry)
    {
        byte[] original = TransparencyFlattenerTests.Pdf($"/ExtGState << /GS0 << {entry} >> >>", Art);
        var (flattened, result) = TransparencyFlattenerTests.Flatten(original, Regions());

        Assert.Equal(new[] { 0 }, result.PagesKeptAsVector);
        Assert.Empty(PdfTransparencyFlattener.FindTransparency(new MemoryStream(flattened)));
        Assert.Single(Images(flattened));
        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);
    }

    /// <summary>
    /// A later graphics state setting the entry back to opaque ends the transparency: the green square
    /// painted after it stays a vector fill, and the image covers only the red rectangle.
    /// </summary>
    [Theory]
    [InlineData("/ca 0.5", "/ca 1")]
    [InlineData("/CA 0.5", "/CA 1")]
    [InlineData("/BM /Multiply", "/BM /Normal")]
    [InlineData("/SMask << /S /Luminosity /G 9 0 R >>", "/SMask /None")]
    public void ALaterOpaqueGraphicsState_EndsTheTransparency(string transparent, string opaque)
    {
        byte[] original = TransparencyFlattenerTests.Pdf($"/ExtGState << /GS0 << {transparent} >> /GS1 << {opaque} >> >>",
            "/GS0 gs 1 0 0 rg 50 25 100 50 re f /GS1 gs 0 1 0 rg 160 10 30 30 re f\n");
        var (flattened, _) = TransparencyFlattenerTests.Flatten(original, Regions());

        var ops = PageOps(flattened);
        int green = ops.FindIndex(o => o.Operator == "re" && Number(o, 0) == 160);
        Assert.Equal("f", ops[green + 1].Operator);
        Assert.Equal(102, ((PdfNumber)Images(flattened).Single()[PdfName.Width]).IntValue);
    }

    /// <summary>
    /// The image is clipped to each region, not their bounding box: two rectangles far apart are two
    /// <c>re</c>, each a pixel larger than its region, before the <c>W</c>.
    /// </summary>
    [Fact]
    public void TheImageIsClippedToEachRegion()
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha, "/GS0 gs 1 0 0 rg 10 10 20 20 re f 150 60 30 30 re f\n");
        var ops = PageOps(TransparencyFlattenerTests.Flatten(original, Regions()).Output);

        int clip = ops.FindIndex(o => o.Operator == "W");
        Assert.Equal(("re", "re", "n"), (ops[clip - 2].Operator, ops[clip - 1].Operator, ops[clip + 1].Operator));
        Assert.Equal(new[] { 9.0, 9, 22, 22 }, ops[clip - 2].Operands.Select(o => ((PdfNumber)o).DoubleValue));
        Assert.Equal(new[] { 149.0, 59, 32, 32 }, ops[clip - 1].Operands.Select(o => ((PdfNumber)o).DoubleValue));
    }

    /// <summary>An image drawn opaquely in itself, but under the half alpha, is taken out like any paint.</summary>
    [Fact]
    public void AnOpaqueImageDrawnUnderHalfAlpha_IsTakenOut()
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha + " /XObject << /Im 5 0 R >>",
            "0 0 1 rg 0 0 100 100 re f /GS0 gs q 20 0 0 20 160 10 cm /Im Do Q\n");
        var (flattened, _) = TransparencyFlattenerTests.Flatten(original, Regions());

        Assert.DoesNotContain(PageOps(flattened), o => o.Operator == "Do" && o.Operands[0] is PdfName { Value: "Im" });
        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);
    }

    /// <summary>
    /// A page already naming an XObject <c>Flattened0</c> keeps it: the region image takes the next
    /// free name, and both draw.
    /// </summary>
    [Fact]
    public void TheRegionImage_TakesAFreeName()
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha + " /XObject << /Flattened0 5 0 R >>",
            "q 20 0 0 20 160 10 cm /Flattened0 Do Q " + Art);
        var (flattened, _) = TransparencyFlattenerTests.Flatten(original, Regions());

        var names = PageOps(flattened).Where(o => o.Operator == "Do").Select(o => ((PdfName)o.Operands[0]).Value).ToList();
        Assert.Equal(new[] { "Flattened0", "Flattened1" }, names);
        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);
    }

    // ---------------------------------------------------------------- text

    /// <summary>
    /// Text shown transparently stays text, shown invisibly: <c>3 Tr</c> before it, the mode it had
    /// after — and it still extracts with no text layer laid over it.
    /// </summary>
    [Fact]
    public void TextShownTransparently_StaysText_ShownInvisibly()
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha + Helvetica, "/GS0 gs BT /F1 12 Tf 10 80 Td (Faded words) Tj ET\n");
        var (flattened, _) = TransparencyFlattenerTests.Flatten(original, Regions());

        var ops = PageOps(flattened);
        int shown = ops.FindIndex(o => o.Operator == "Tj");
        Assert.Equal(("Tr", 3.0), (ops[shown - 1].Operator, Number(ops[shown - 1], 0)));
        Assert.Equal(("Tr", 0.0), (ops[shown + 1].Operator, Number(ops[shown + 1], 0)));
        Assert.Contains("Faded words", TransparencyFlattenerTests.Text(flattened));
        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);

        // Kept as text, it is not laid over itself again by a text layer: the page has its one
        // content stream. (Extraction cannot tell — it drops an overlapping duplicate.)
        var (searchable, _) = TransparencyFlattenerTests.Flatten(original, Regions(text: true));
        Assert.Contains("Faded words", TransparencyFlattenerTests.Text(searchable));
        using var doc = PdfDocument.Open(new MemoryStream(searchable));
        Assert.IsType<PdfReference>(doc.Pages[0].Dictionary[PdfName.Contents]);
    }

    /// <summary>Text that also clips (mode 5) is shown in mode 7, so its clip still takes effect.</summary>
    [Fact]
    public void TextThatAlsoClips_IsShownInTheClippingMode()
    {
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha + Helvetica, "/GS0 gs BT 5 Tr /F1 12 Tf 10 80 Td (Clip) Tj ET\n");
        var ops = PageOps(TransparencyFlattenerTests.Flatten(original, Regions()).Output);

        int shown = ops.FindIndex(o => o.Operator == "Tj");
        Assert.Equal(7.0, Number(ops[shown - 1], 0));
        Assert.Equal(5.0, Number(ops[shown + 1], 0));
    }

    /// <summary>
    /// A form with a transparency group is dropped — from the content and from the resources — and the
    /// text it showed is laid back as invisible text; without the text layer it is gone.
    /// </summary>
    [Fact]
    public void AGroupFormsText_IsLaidBack_WhenTheFormIsDropped()
    {
        string form = TransparencyFlattenerTests.Stream("1 0 0 rg 0 0 100 30 re f BT /F1 10 Tf 5 10 Td (Inside group) Tj ET\n",
            "/Type /XObject /Subtype /Form /BBox [0 0 100 30] /Group << /S /Transparency >> " +
            "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> ");
        byte[] original = TransparencyFlattenerTests.Pdf("/XObject << /Fg 14 0 R >>", "0 0 1 rg 0 0 40 40 re f q 1 0 0 1 60 60 cm /Fg Do Q\n",
            extraObjects: new[] { form });

        var (kept, result) = TransparencyFlattenerTests.Flatten(original, Regions(text: true));
        Assert.Equal(new[] { 0 }, result.PagesKeptAsVector);
        Assert.Contains("Inside group", TransparencyFlattenerTests.Text(kept));
        using (var doc = PdfDocument.Open(new MemoryStream(kept)))      // the layer: a second content stream
            Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]));
        Assert.DoesNotContain(PageOps(kept), o => o.Operator == "Do" && o.Operands[0] is PdfName { Value: "Fg" });
        TransparencyFlattenerTests.AssertRendersAlike(original, kept);

        var (dropped, _) = TransparencyFlattenerTests.Flatten(original, Regions(text: false));
        Assert.DoesNotContain("Inside group", TransparencyFlattenerTests.Text(dropped));
        Assert.Contains("rg", PageOps(dropped).Select(o => o.Operator));
    }

    // ---------------------------------------------------------------- images

    /// <summary>An image with a soft mask leaves the content and the resources; the page looks as it did.</summary>
    [Fact]
    public void AnImageWithASoftMask_IsDropped()
    {
        byte[] original = TransparencyFlattenerTests.Pdf("/XObject << /Im 7 0 R >>", "0 0 1 rg 0 0 100 100 re f q 20 0 0 20 160 10 cm /Im Do Q\n");
        var (flattened, _) = TransparencyFlattenerTests.Flatten(original, Regions());

        Assert.DoesNotContain(PageOps(flattened), o => o.Operator == "Do" && o.Operands[0] is PdfName { Value: "Im" });
        using (var doc = PdfDocument.Open(new MemoryStream(flattened)))
        {
            var xobjects = (PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]))[PdfName.XObject]);
            Assert.False(xobjects.ContainsKey(new PdfName("Im")));
        }
        TransparencyFlattenerTests.AssertRendersAlike(original, flattened);
    }

    /// <summary>
    /// An inline image painted opaquely is written back and draws as before; one painted under the
    /// half alpha is taken out.
    /// </summary>
    [Fact]
    public void AnInlineImage_IsKeptWhenOpaque_AndTakenOutWhenTransparent()
    {
        const string Inline = "q 20 0 0 20 160 10 cm BI /W 1 /H 1 /CS /G /BPC 8 ID @ EI Q\n";
        byte[] opaque = TransparencyFlattenerTests.Pdf(HalfAlpha, Inline + Art);
        var (kept, _) = TransparencyFlattenerTests.Flatten(opaque, Regions());
        Assert.Contains(PageOps(kept), o => o.Operator == "BI");
        TransparencyFlattenerTests.AssertRendersAlike(opaque, kept);

        byte[] translucent = TransparencyFlattenerTests.Pdf(HalfAlpha, "/GS0 gs " + Inline);
        var (taken, _) = TransparencyFlattenerTests.Flatten(translucent, Regions());
        Assert.DoesNotContain(PageOps(taken), o => o.Operator == "BI");
        TransparencyFlattenerTests.AssertRendersAlike(translucent, taken);
    }

    // ---------------------------------------------------------------- where it gives up

    /// <summary>
    /// A pattern or a Type 3 font that reaches transparency paints it from an operation that looks
    /// opaque, so the page is flattened whole, and the warning says why.
    /// </summary>
    [Theory]
    [InlineData("/Pattern << /P 14 0 R >>", "tiling pattern /P")]
    [InlineData("/Pattern << /P << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /N 1 >> >> /ExtGState << /ca 0.5 >> >> >>", "shading pattern /P")]
    [InlineData("/Font << /T3 << /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << >> /FirstChar 97 /LastChar 97 /Widths [1] /Resources << /ExtGState << /G << /ca 0.5 >> >> >> >> >>", "Type 3 font /T3")]
    public void APatternOrType3FontReachingTransparency_FlattensThePageWhole(string resources, string why)
    {
        string tiling = TransparencyFlattenerTests.Stream("/G gs 1 0 0 rg 0 0 5 5 re f\n",
            "/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 " +
            "/Resources << /ExtGState << /G << /ca 0.5 >> >> >> ");
        byte[] original = TransparencyFlattenerTests.Pdf(resources, "0 0 1 rg 0 0 100 100 re f\n", extraObjects: new[] { tiling });
        var (_, result) = TransparencyFlattenerTests.Flatten(original, Regions());

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        Assert.Empty(result.PagesKeptAsVector);
        Assert.Contains(result.Warnings, w => w.Contains("flattened whole") && w.Contains(why));
    }

    /// <summary>The control: the same tiling pattern painting opaquely does not stop a page being kept as vector.</summary>
    [Fact]
    public void AnOpaqueTilingPattern_DoesNotStopIt()
    {
        string tiling = TransparencyFlattenerTests.Stream("1 0 0 rg 0 0 5 5 re f\n",
            "/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> ");
        byte[] original = TransparencyFlattenerTests.Pdf(HalfAlpha + " /Pattern << /P 14 0 R >>", Art, extraObjects: new[] { tiling });
        Assert.Equal(new[] { 0 }, TransparencyFlattenerTests.Flatten(original, Regions()).Result.PagesKeptAsVector);
    }

    // ---------------------------------------------------------------- the pieces

    /// <summary>
    /// Each operation's painted bound, in user space — a box away from the origin included — and a
    /// form's paint reported against the <c>Do</c> that drew it.
    /// </summary>
    [Fact]
    public void MeasureOperations_ReportsEachOperationsBoundInUserSpace()
    {
        // The form paints twice — the bound is both.
        string form = TransparencyFlattenerTests.Stream("0 0 10 10 re f 30 20 10 10 re f\n",
            "/Type /XObject /Subtype /Form /BBox [0 0 40 30] ");
        byte[] pdf = TransparencyFlattenerTests.Pdf("/XObject << /Fm 14 0 R >>", "q 10 20 30 40 re f Q q 1 0 0 1 100 50 cm /Fm Do Q\n",
            pageExtras: "/CropBox [5 5 210 105]", mediaBox: "[-20 -10 240 120]", extraObjects: new[] { form });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var bounds = PdfPageRenderer.MeasureOperations(doc.Pages[0], doc, null, out var ops);

        Assert.Equal("f", ops[2].Operator);
        AssertRect(new SKRect(10, 20, 40, 60), bounds[2]);
        Assert.Equal("Do", ops[6].Operator);
        AssertRect(new SKRect(100, 50, 140, 80), bounds[6]);
        Assert.False(bounds.ContainsKey(1));
    }

    /// <summary>The crop: whole pixels outward round the regions, and the matrix placing them in the unit square.</summary>
    [Fact]
    public void Crop_TakesWholePixelsRoundTheRegions()
    {
        var samples = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var raster = new PdfTransparencyFlattener.PageRaster(samples, 10, 10, 1, PdfName.DeviceGray);
        var box = PageBoxMetrics.Of(new PdfArray { new PdfNumber(0), new PdfNumber(0), new PdfNumber(100), new PdfNumber(100) });

        Assert.True(PdfTransparencyFlattener.Crop(raster, Matrix(box, 0), new[] { new SKRect(25, 25, 50, 75) },
            out var cropped, out float[] within));
        Assert.Equal((3, 6), (cropped.Width, cropped.Height));
        Assert.Equal(22, cropped.Samples[0]);          // row 2, column 2
        Assert.Equal(new[] { 0.3f, 0f, 0f, 0.6f, 0.2f, 0.2f }, within.Select(v => (float)System.Math.Round(v, 4)));

        // A quarter turn: user y runs along the image, user x down it.
        Assert.True(PdfTransparencyFlattener.Crop(raster, Matrix(box, 90), new[] { new SKRect(12, 62, 18, 88) },
            out cropped, out _));
        Assert.Equal((3, 1), (cropped.Width, cropped.Height));
        Assert.Equal(16, cropped.Samples[0]);          // row 1, column 6

        Assert.False(PdfTransparencyFlattener.Crop(raster, Matrix(box, 0), new[] { new SKRect(200, 200, 300, 300) }, out _, out _));
    }

    /// <summary>The regions are grown by a device pixel and cut to the page box; one outside it is dropped.</summary>
    [Fact]
    public void ClipRegions_GrowsByAPixel_AndCutsToTheBox()
    {
        var box = PageBoxMetrics.Of(new PdfArray { new PdfNumber(0), new PdfNumber(0), new PdfNumber(100), new PdfNumber(100) });
        var clipped = PdfTransparencyFlattener.ClipRegions(
            new[] { new SKRect(10, 10, 20, 20), new SKRect(90, 90, 120, 120), new SKRect(150, 150, 160, 160) }, box, 36);

        Assert.Equal(2, clipped.Count);
        AssertRect(new SKRect(8, 8, 22, 22), clipped[0]);
        AssertRect(new SKRect(88, 88, 100, 100), clipped[1]);
    }

    // ---------------------------------------------------------------- helpers

    private static float[] Matrix(PageBoxMetrics box, int rotation)
        => PdfTransparencyFlattener.Placement(box, rotation).Split(' ').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static void AssertRect(SKRect expected, SKRect actual)
    {
        Assert.Equal(expected.Left, actual.Left, 2);
        Assert.Equal(expected.Top, actual.Top, 2);
        Assert.Equal(expected.Right, actual.Right, 2);
        Assert.Equal(expected.Bottom, actual.Bottom, 2);
    }

    private static double Number(ContentStreamOperation op, int i) => ((PdfNumber)op.Operands[i]).DoubleValue;

    private static List<ContentStreamOperation> PageOps(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
    }

    private static List<PdfStream> Images(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var images = new List<PdfStream>();
        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]);
        if (doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary xobjects)
            foreach (var kvp in xobjects)
                if (doc.ResolveObject(kvp.Value) is PdfStream s && s[PdfName.Subtype] is PdfName { Value: "Image" })
                    images.Add(s);
        return images;
    }
}
