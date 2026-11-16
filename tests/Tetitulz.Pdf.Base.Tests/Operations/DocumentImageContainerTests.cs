// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>DocumentImageIndex</c> reaches the images every resource-bearing container draws: an
/// annotation's appearance streams, a tiling pattern, a Type 3 font, as well as a form XObject.
/// </summary>
/// <remarks>
/// It walked the pages' resources and the forms under them, and nothing else, so an RGB logo on a
/// stamp, in a tiling pattern or in a Type 3 glyph survived a grayscale conversion that reported
/// the document converted, and <see cref="PdfImageReplacer.FindImages(Stream)"/> did not list it.
/// Each case asserts the public list, and the stamp case the conversion too, because the index's
/// two consumers are the point: fixing one would leave the other behind.
/// </remarks>
public class DocumentImageContainerTests
{
    [Fact]
    public void AnImageOnAStampsAppearanceIsListedAndConverted()
    {
        byte[] pdf = Page("/Annots [7 0 R]", "",
            b => b.StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                                "/Resources << /XObject << /Logo 5 0 R >> >>", "/Logo Do")
                  .Obj(7, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 10 10] /AP << /N 6 0 R >> >>"));

        var image = Assert.Single(PdfImageReplacer.FindImages(new MemoryStream(pdf)));
        Assert.Equal(5, image.ObjectNumber);
        Assert.Equal(new[] { 0 }, image.Pages);
        Assert.True(image.OnlyInsideFormXObject);

        using var output = new MemoryStream();
        Assert.Equal(1, PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), output));
    }

    /// <summary>Every appearance state is drawn at some point, so each one's images are listed.</summary>
    [Fact]
    public void EveryAppearanceStateIsWalked()
    {
        byte[] pdf = Page("/Annots [7 0 R]", "",
            b => b.StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                                "/Resources << /XObject << /Logo 5 0 R >> >>", "/Logo Do")
                  .Obj(7, "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AS /Off " +
                          "/AP << /N << /On 9 0 R >> /D << /Off 6 0 R >> >> >>")
                  .Obj(8, ImageFixtures.OnePixelImageDict(ImageFixtures.Green), ImageFixtures.Green)
                  .StreamObj(9, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                                "/Resources << /XObject << /Tick 8 0 R >> >>", "/Tick Do"));

        var numbers = PdfImageReplacer.FindImages(new MemoryStream(pdf)).Select(i => i.ObjectNumber).ToArray();

        Assert.Equal(new[] { 5, 8 }, numbers);
    }

    [Fact]
    public void AnImageInATilingPatternIsListed()
    {
        byte[] pdf = Page("", "/Pattern << /P1 6 0 R >>",
            b => b.StreamObj(6, "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
                                "/BBox [0 0 1 1] /XStep 1 /YStep 1 " +
                                "/Resources << /XObject << /Logo 5 0 R >> >>", "/Logo Do"));

        Assert.Equal(5, Assert.Single(PdfImageReplacer.FindImages(new MemoryStream(pdf))).ObjectNumber);
    }

    [Fact]
    public void AnImageInAType3FontsResourcesIsListed()
    {
        byte[] pdf = Page("", "/Font << /T3 6 0 R >>",
            b => b.Obj(6, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] " +
                          "/CharProcs << /a 7 0 R >> /Encoding << /Differences [97 /a] >> " +
                          "/FirstChar 97 /LastChar 97 /Widths [1] " +
                          "/Resources << /XObject << /Logo 5 0 R >> >> >>")
                  .StreamObj(7, "<<", "1 0 d0 /Logo Do"));

        Assert.Equal(5, Assert.Single(PdfImageReplacer.FindImages(new MemoryStream(pdf))).ObjectNumber);
    }

    /// <summary>
    /// The controls: a shading pattern has no resources and draws no image, and a plain font is not
    /// a container; neither makes the walk descend.
    /// </summary>
    /// <remarks>
    /// A shading pattern is a dictionary (§8.7.4.2), which the walk's stream test already passes
    /// over; object 8 is one written as a stream, the one shape where <c>/PatternType</c> decides.
    /// A reader draws it as a shading and never uses its resources. Without it the pattern-type
    /// mutation survived.
    /// </remarks>
    [Fact]
    public void AShadingPatternAndAPlainFontAreNotContainers()
    {
        byte[] pdf = Page("", "/Pattern << /S1 6 0 R /S2 8 0 R >> /Font << /F1 7 0 R >>",
            b => b.Obj(6, "<< /Type /Pattern /PatternType 2 /Shading << /ShadingType 2 " +
                          "/ColorSpace /DeviceRGB /Coords [0 0 1 0] /Function << /FunctionType 2 " +
                          "/Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >> >> " +
                          "/Resources << /XObject << /Logo 5 0 R >> >> >>")
                  .Obj(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                          "/Resources << /XObject << /Logo 5 0 R >> >> >>")
                  .StreamObj(8, "<< /Type /Pattern /PatternType 2 /Shading << /ShadingType 2 " +
                                "/ColorSpace /DeviceRGB /Coords [0 0 1 0] /Function << /FunctionType 2 " +
                                "/Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >> >> " +
                                "/Resources << /XObject << /Logo 5 0 R >> >>", ""));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var index = DocumentImageIndex.Build(doc);

        Assert.Equal(0, index.FormsVisited);
        Assert.Null(index.Find(5));
    }

    /// <summary>
    /// Hostile input: a tiling pattern whose resources name itself twice. Unguarded, the walk doubles
    /// per level down to the depth cap; guarded, the pattern is entered once.
    /// </summary>
    [Fact]
    public void ATilingPatternListingItselfTwice_HostileBranchingCycle_IsEnteredOnce()
    {
        byte[] pdf = Page("", "/Pattern << /P1 6 0 R >>",
            b => b.StreamObj(6, "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
                                "/BBox [0 0 1 1] /XStep 1 /YStep 1 " +
                                "/Resources << /Pattern << /A 6 0 R /B 6 0 R >> /XObject << /Logo 5 0 R >> >>",
                             "/Logo Do"));

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var index = DocumentImageIndex.Build(doc);

        Assert.Equal(1, index.FormsVisited);
        Assert.NotNull(index.Find(5));
    }

    /// <summary>
    /// A one-page document with the RGB image as object 5, reached from nowhere unless
    /// <paramref name="extra"/> adds the container that draws it.
    /// </summary>
    private static byte[] Page(string pageExtras, string resourceExtras, System.Func<PdfFixtureBuilder, PdfFixtureBuilder> extra)
        => extra(new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    $"/Resources << {resourceExtras} >> /Contents 4 0 R {pageExtras} >>")
            .StreamObj(4, "<<", "")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red))
            .Finish();
}
