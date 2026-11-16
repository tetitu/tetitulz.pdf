// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Three defects in files the Images batch-3 sweep had covered, found by reading them again: the
/// image index reading one shared resource dictionary once per form, the inserter accepting a
/// non-finite position or size, and a null raster reported under a name the method does not have.
/// </summary>
public class ImagesFollowUpTests
{
    // ---------------------------------------------------------------- the image index

    /// <summary>
    /// Hostile input: <paramref name="forms"/> form XObjects whose <c>/Resources</c> is one shared
    /// dictionary naming all of them and an image. Every form is entered once already; the shared
    /// dictionary was read once per form, N×N entry reads. Now it is read once.
    /// </summary>
    [Theory]
    [InlineData(400)]
    public void ASharedResourceDictionary_HostileQuadraticFanOut_IsReadOnce(int forms)
    {
        using var doc = PdfDocument.Open(new MemoryStream(SharedResourcesPdf(forms)));

        var index = DocumentImageIndex.Build(doc);

        Assert.Equal(1, index.ResourcesWalked);
        Assert.Equal(forms, index.FormsVisited);
        Assert.NotNull(index.Find(5));
    }

    /// <summary>The control: two different resource dictionaries are two reads, so the probe counts.</summary>
    [Fact]
    public void TwoDistinctResourceDictionaries_AreTwoReads()
    {
        using var doc = PdfDocument.Open(new MemoryStream(new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /F 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "/F Do")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                          "/Resources << /XObject << /Logo 5 0 R >> >>", "/Logo Do")
            .Finish()));

        Assert.Equal(2, DocumentImageIndex.Build(doc).ResourcesWalked);
    }

    private static byte[] SharedResourcesPdf(int forms)
    {
        const int shared = 7, firstForm = 8;
        var names = new StringBuilder("<< /XObject << /Logo 5 0 R");
        for (int i = 0; i < forms; i++) names.Append($" /F{i} {firstForm + i} 0 R");
        names.Append(" >> >>");

        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources {shared} 0 R /Contents 4 0 R >>")
            .StreamObj(4, "<<", "/F0 Do")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .Obj(shared, names.ToString());
        for (int i = 0; i < forms; i++)
            b.StreamObj(firstForm + i, $"<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources {shared} 0 R", "/Logo Do");
        return b.Finish();
    }

    // ---------------------------------------------------------------- the inserter

    public static TheoryData<double, double, double, double, string> NonFinite() => new()
    {
        { double.NaN, 50, 100, 80, "xPt" },
        { 10, double.PositiveInfinity, 100, 80, "yPt" },
        { 10, 50, double.NaN, 80, "widthPt" },
        { 10, 50, 100, double.PositiveInfinity, "heightPt" },
    };

    /// <summary>
    /// A NaN or infinite position or size is refused under its own name. NaN passed the
    /// <c>&lt;= 0</c> size checks, and both were written into the content stream.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonFinite))]
    public void InsertRaw_ANonFiniteGeometry_IsRefusedUnderItsOwnName(double x, double y, double w, double h, string param)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImageInserter.InsertRaw(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()), new MemoryStream(),
                0, new byte[3], 1, 1, x, y, w, h));
        Assert.Equal(param, ex.ParamName);
    }

    [Fact]
    public void InsertRaw_NullSamples_AreNamedAsTheParameter()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            PdfImageInserter.InsertRaw(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()), new MemoryStream(),
                0, null, 1, 1, 0, 0, 10, 10));
        Assert.Equal("rgbBytes", ex.ParamName);
    }
}
