// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>PdfImageReplacer</c>'s decisions the Images sweep (batch 2) found no test reaching: the plan's
/// object-number floor, an image no page reaches, the order dropped keys are reported in, the fit
/// guards (an original with no size, the aspect tolerance), the matte copy, and the area cap.
/// </summary>
/// <remarks>
/// The fixture is one page drawing image object 4, a 10×10 DeviceGray image whose dictionary the
/// test chooses, plus object 5 — another image no page reaches.
/// </remarks>
[Collection(PdfLimitsCollection.Name)]
public class ImageReplacementDecisionTests
{
    private static readonly byte[] TenByTenGray = new byte[100];

    /// <summary>
    /// The plan never allocates below <c>firstFreeObjectNumber</c>: the soft mask a transparent
    /// replacement brings takes that number, and the revision's /Size is one past it — as an
    /// encrypted document's appended revision needs, whose decrypted copy declares fewer objects.
    /// </summary>
    [Fact]
    public void APlan_AllocatesFromTheFloorItIsGiven()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf("")));
        var rgba = new byte[10 * 10 * 4];
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 128;

        ImageReplacementPlan plan = PdfImageReplacer.Plan(doc, 4, ReplacementImage.Rgba32(rgba, 10, 10),
            firstFreeObjectNumber: 500);

        Assert.Equal(500, plan.Result.SoftMaskObjectNumber);
        Assert.Equal(501, plan.NextObjectNumber);
    }

    /// <summary>An image object no page reaches is still replaced by its number.</summary>
    [Fact]
    public void AnImageNoPageReaches_IsReplacedByItsNumber()
    {
        using var output = new MemoryStream();
        ImageReplacementResult result = PdfImageReplacer.ReplaceByObjectNumber(
            new MemoryStream(Pdf("")), output, 5, ReplacementImage.Gray8(new byte[4], 2, 2));

        Assert.Equal(5, result.ObjectNumber);
        Assert.Empty(result.Pages);
        using var written = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsType<PdfStream>(written.GetObject(5));
        Assert.Equal(2, ((PdfNumber)image[PdfName.Width]).IntValue);
    }

    /// <summary>
    /// The keys a replacement drops are reported in ordinal order, whatever order the original
    /// dictionary held them in.
    /// </summary>
    [Fact]
    public void DroppedKeys_AreReportedSorted()
    {
        var result = Replace(Pdf("/Zeta 1 /Alpha 2 /Mask [0 0] /Name /Im0"), ReplacementImage.Gray8(TenByTenGray, 10, 10));

        Assert.Equal(new[] { "Alpha", "Mask", "Name", "Zeta" }, result.DroppedKeys);
    }

    /// <summary>
    /// An original with no declared size has no aspect ratio to fit to: the replacement is written
    /// as given, neither letterboxed nor cropped.
    /// </summary>
    [Theory]
    [InlineData(ImageFit.Contain)]
    [InlineData(ImageFit.Cover)]
    public void AnOriginalWithNoSize_IsNotFitted(ImageFit fit)
    {
        var result = Replace(Pdf("", declareSize: false), ReplacementImage.Gray8(new byte[20 * 10], 20, 10),
            new ImageReplacementOptions { Fit = fit });

        Assert.False(result.Letterboxed);
        Assert.False(result.Cropped);
        Assert.Equal((20, 10), (result.NewWidth, result.NewHeight));
    }

    /// <summary>
    /// A ratio within one percent of the original's is left alone — integer pixel sizes rarely land
    /// exactly — while one further off is letterboxed.
    /// </summary>
    [Fact]
    public void ARatioWithinOnePercent_IsNotLetterboxed()
    {
        var options = new ImageReplacementOptions { Fit = ImageFit.Contain };

        var close = Replace(Pdf(""), ReplacementImage.Gray8(new byte[200 * 199], 200, 199), options);
        Assert.False(close.Letterboxed);
        Assert.Equal((200, 199), (close.NewWidth, close.NewHeight));

        var off = Replace(Pdf(""), ReplacementImage.Gray8(new byte[200 * 190], 200, 190), options);
        Assert.True(off.Letterboxed);
    }

    /// <summary>
    /// The matte is copied when it is given: changing the caller's array afterwards does not change
    /// the <c>/Matte</c> written.
    /// </summary>
    [Fact]
    public void TheMatte_IsTheValuesGiven_NotTheCallersArray()
    {
        float[] matte = { 1f };
        var image = ReplacementImage.Gray8(TenByTenGray, 10, 10).WithSoftMask(new byte[100], 10, 10, matte);
        matte[0] = 0.25f;

        using var output = new MemoryStream();
        var result = PdfImageReplacer.Replace(new MemoryStream(Pdf("")), output, 0, 0, image);

        using var written = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var mask = Assert.IsType<PdfStream>(written.GetObject(result.SoftMaskObjectNumber));
        var written0 = Assert.IsType<PdfArray>(mask[PdfName.Matte]);
        Assert.Equal(1.0, ((PdfNumber)written0[0]).DoubleValue);
    }

    /// <summary>
    /// A replacement past the pixel-area limit is refused when it is built, whichever factory builds
    /// it; one within the limit is accepted (the control).
    /// </summary>
    [Fact]
    public void AReplacementPastTheAreaLimit_IsRefused()
    {
        try
        {
            PdfLimits.MaxImagePixelArea = 50;
            Assert.Throws<ArgumentOutOfRangeException>(() => ReplacementImage.Gray8(new byte[60], 10, 6));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReplacementImage.Rgb24(new byte[180], 10, 6));
            ReplacementImage withinLimit = ReplacementImage.Gray8(new byte[50], 10, 5);
            Assert.Equal((10, 5), (withinLimit.Width, withinLimit.Height));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static ImageReplacementResult Replace(byte[] pdf, ReplacementImage image, ImageReplacementOptions options = null)
    {
        using var output = new MemoryStream();
        return PdfImageReplacer.Replace(new MemoryStream(pdf), output, 0, 0, image, options);
    }

    private static byte[] Pdf(string extraImageKeys, bool declareSize = true)
    {
        string size = declareSize ? "/Width 10 /Height 10 " : "";
        string sample = new string('\u0080', 100);
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 6 0 R /Resources << /XObject << /Im0 4 0 R >> >> >>",
            $"<< /Type /XObject /Subtype /Image {size}/ColorSpace /DeviceGray /BitsPerComponent 8 {extraImageKeys} /Length 100 >>\nstream\n{sample}\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 100 >>\nstream\n{sample}\nendstream",
            "<< /Length 30 >>\nstream\nq 100 0 0 100 0 0 cm /Im0 Do Q\nendstream",
        };

        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(ms.Length);
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Length;
        var sb = new StringBuilder($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }
}
