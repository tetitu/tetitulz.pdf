// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// A document writes an image once for every page that registers the same arrays with the same dictionary
/// (<see cref="ImageObjectShare"/>): the pages' resources name one object. A content-equal copy — another array —
/// and the same array under another <c>/Decode</c> are other images.
/// </summary>
public class ImageObjectShareTests
{
    private static readonly byte[] Rgb = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 };   // 2 x 2
    private static readonly byte[] Alpha = { 255, 128, 0, 64 };

    /// <summary>The image object each page's <c>/Im…</c> resource names, in page order.</summary>
    private static int[] ImageObjects(Action<PdfDocumentBuilder> pages)
    {
        var builder = new PdfDocumentBuilder();
        pages(builder);
        using var ms = new MemoryStream();
        builder.Build(ms);
        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));
        return Enumerable.Range(0, doc.Pages.Count).Select(i =>
        {
            var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[i].Dictionary[PdfName.Resources]);
            var xobjects = (PdfDictionary)doc.ResolveObject(resources[PdfName.XObject]);
            return ((PdfReference)xobjects.Single().Value).ReferenceObjectNumber;
        }).ToArray();
    }

    private static void Page(PdfDocumentBuilder builder, byte[] data, double[] decode = null)
    {
        var page = builder.AddPage(100, 100);
        string name = page.AddImage(data, 2, 2, null, "DeviceRGB", 8, decode, Alpha);
        page.DrawImage(name, 0, 0, 50, 50);
    }

    /// <summary>Three pages with the same arrays: one image object, its soft mask with it; the control, a copy, is its own.</summary>
    [Fact]
    public void TheSameArraysOnSeveralPages_AreOneObject_AndACopyIsAnother()
    {
        int[] objects = ImageObjects(b => { Page(b, Rgb); Page(b, Rgb); Page(b, Rgb); Page(b, (byte[])Rgb.Clone()); });

        Assert.Equal(objects[0], objects[1]);
        Assert.Equal(objects[0], objects[2]);
        Assert.NotEqual(objects[0], objects[3]);
    }

    /// <summary>The same array under another <c>/Decode</c> is another image.</summary>
    [Fact]
    public void TheSameArrayUnderAnotherDecode_IsAnotherObject()
    {
        int[] objects = ImageObjects(b => { Page(b, Rgb); Page(b, Rgb, new double[] { 1, 0, 1, 0, 1, 0 }); Page(b, Rgb, new double[] { 1, 0, 1, 0, 1, 0 }); });

        Assert.NotEqual(objects[0], objects[1]);
        Assert.Equal(objects[1], objects[2]);   // a /Decode equal by value is the same image

        // Of one length, values apart: two images.
        int[] apart = ImageObjects(b => { Page(b, Rgb, new double[] { 1, 0, 1, 0, 1, 0 }); Page(b, Rgb, new double[] { 0, 1, 0, 1, 0, 1 }); });
        Assert.NotEqual(apart[0], apart[1]);
    }

    /// <summary>
    /// The shared object is written whole — deflated samples and its soft mask — and every registration is still
    /// judged by the size rule: a second registration past it is refused though the first was written.
    /// </summary>
    [Fact]
    public void TheSharedObject_IsWrittenWhole_AndEveryRegistrationIsStillJudged()
    {
        var builder = new PdfDocumentBuilder();
        Page(builder, Rgb);
        Page(builder, Rgb);
        using var ms = new MemoryStream();
        builder.Build(ms);
        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));
        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[1].Dictionary[PdfName.Resources]);
        var image = (PdfStream)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(resources[PdfName.XObject])).Single().Value);
        Assert.Equal(PdfName.FlateDecode, image[PdfName.Filter]);
        Assert.Equal(Rgb, image.GetDecodedData());
        var mask = (PdfStream)doc.ResolveObject(image[PdfName.SMask]);
        Assert.Equal(PdfName.FlateDecode, mask[PdfName.Filter]);
        Assert.Equal(Alpha, mask.GetDecodedData());

        var page = new PdfDocumentBuilder().AddPage(100, 100);
        page.AddImage(Rgb, 2, 2, null, "DeviceRGB", 8, null, null);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.AddImage(Rgb, 0, 2, null, "DeviceRGB", 8, null, null));
    }
}
