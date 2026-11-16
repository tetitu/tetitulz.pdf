// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Reading an incrementally-updated PDF's earlier revisions back.
///
/// <para>This is the honest counterpart to <see cref="PdfImageReplacer"/>'s central caveat:
/// a signature-preserving edit hides the old content, it does not delete it. These tests prove
/// that by recovering the pre-replacement image and rendering it.</para>
/// </summary>
public sealed class PdfRevisionsTests
{
    private const int Dpi = 72;

    private static byte[] ReplaceImage(byte[] pdf, byte[] rgbSamples)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfImageReplacer.Replace(input, output, 0, 0, ReplacementImage.Rgb24(rgbSamples, 1, 1));
        return output.ToArray();
    }

    private static SKColor CenterPixel(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf, Dpi);
        return bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
    }

    private static void AssertIsRed(SKColor c, string what) =>
        Assert.True(c.Red > 200 && c.Green < 80 && c.Blue < 80, $"{what}: expected red, got {c}");

    private static void AssertIsBlue(SKColor c, string what) =>
        Assert.True(c.Blue > 200 && c.Red < 80 && c.Green < 80, $"{what}: expected blue, got {c}");

    // ================================================================

    [Fact]
    public void List_OnAFileWithNoIncrementalUpdate_ReportsExactlyOneRevision()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        using var input = new MemoryStream(pdf);
        var revisions = PdfRevisions.List(input);

        var only = Assert.Single(revisions);
        Assert.Equal(0, only.Index);
        Assert.Equal(pdf.Length, only.Length);
    }

    [Fact]
    public void List_CountsOneRevisionPerAppendedUpdate()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] once = ReplaceImage(pdf, ImageFixtures.Blue);
        byte[] twice = ReplaceImage(once, ImageFixtures.Green);

        using var input = new MemoryStream(twice);
        var revisions = PdfRevisions.List(input);

        Assert.Equal(3, revisions.Count);
        Assert.Equal(pdf.Length, revisions[0].Length);
        Assert.Equal(once.Length, revisions[1].Length);
        Assert.Equal(twice.Length, revisions[2].Length);
    }

    [Fact]
    public void Extract_RecoversThePreReplacementImage()
    {
        // The whole point: "replaced" means hidden, not deleted.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] replaced = ReplaceImage(pdf, ImageFixtures.Blue);
        AssertIsBlue(CenterPixel(replaced), "current revision");

        using var input = new MemoryStream(replaced);
        using var original = new MemoryStream();
        PdfRevisions.Extract(input, 0, original);

        AssertIsRed(CenterPixel(original.ToArray()), "revision 0 still holds the original image");
    }

    [Fact]
    public void Extract_ProducesTheOriginalBytesExactly()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] replaced = ReplaceImage(pdf, ImageFixtures.Blue);

        using var input = new MemoryStream(replaced);
        using var extracted = new MemoryStream();
        PdfRevisions.Extract(input, 0, extracted);

        Assert.Equal(pdf, extracted.ToArray());
    }

    [Fact]
    public void Extract_ExtractedRevisionItselfHasExactlyOneRevision()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] replaced = ReplaceImage(pdf, ImageFixtures.Blue);

        using var input = new MemoryStream(replaced);
        using var extracted = new MemoryStream();
        PdfRevisions.Extract(input, 0, extracted);

        using var again = new MemoryStream(extracted.ToArray());
        Assert.Single(PdfRevisions.List(again));
    }

    [Fact]
    public void TryExtractPrevious_ReturnsTheStateTheEditSuperseded()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] once = ReplaceImage(pdf, ImageFixtures.Blue);
        byte[] twice = ReplaceImage(once, ImageFixtures.Green);

        using var input = new MemoryStream(twice);
        using var previous = new MemoryStream();
        Assert.True(PdfRevisions.TryExtractPrevious(input, previous));

        AssertIsBlue(CenterPixel(previous.ToArray()), "the revision before the last");
    }

    [Fact]
    public void TryExtractPrevious_OnASingleRevisionFile_ReturnsFalseAndWritesNothing()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        Assert.False(PdfRevisions.TryExtractPrevious(input, output));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void Extract_IndexPastTheLastRevision_Throws()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfRevisions.Extract(input, 1, output));
        Assert.Contains("has 1 revision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void List_MalformedEofInsideAStream_IsNotCountedAsARevision()
    {
        // Adversarial: the five bytes "%%EOF" appear inside an image's sample data. A scanner
        // that trusts them would report two revisions and Extract(0) would hand back a truncated
        // file. Only a %%EOF preceded by a valid startxref ends a revision.
        byte[] poison = Encoding.Latin1.GetBytes("%%EOF\n and more");
        byte[] pdf = ImageFixtures.OneImagePdf(poison, poison.Length / 3 + 1, 1);

        using var input = new MemoryStream(pdf);
        var revisions = PdfRevisions.List(input);

        var only = Assert.Single(revisions);
        Assert.Equal(pdf.Length, only.Length);
    }

    [Fact]
    public void List_LinearizedFirstPageXref_IsNotCountedAsASeparateRevision()
    {
        // A linearized PDF closes its first-page cross-reference section with the literal
        // "startxref 0 %%EOF" near the front. It is one revision, not two — offset 0 addresses
        // the %PDF header, which is never a cross-reference section.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] linearizedPrefix = Encoding.Latin1.GetBytes("startxref\n0\n%%EOF\n");
        var combined = new byte[linearizedPrefix.Length + pdf.Length];
        Buffer.BlockCopy(pdf, 0, combined, 0, 9);                                    // "%PDF-1.7\n"
        Buffer.BlockCopy(linearizedPrefix, 0, combined, 9, linearizedPrefix.Length);
        Buffer.BlockCopy(pdf, 9, combined, 9 + linearizedPrefix.Length, pdf.Length - 9);

        using var input = new MemoryStream(combined);
        var revisions = PdfRevisions.List(input);

        var only = Assert.Single(revisions);
        Assert.Equal(combined.Length, only.Length);
    }

    [Fact]
    public void List_StartXrefPointingPastTheEofPosition_IsRejected()
    {
        // A forward-pointing offset cannot address a cross-reference section that precedes it,
        // so the trailer is not a real one.
        byte[] pdf = Encoding.Latin1.GetBytes("%PDF-1.7\nstartxref\n999999\n%%EOF\n");

        using var input = new MemoryStream(pdf);
        Assert.Empty(PdfRevisions.List(input));
    }

    [Fact]
    public void GetRevisions_FacadeAgreesWithTheOperation()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] replaced = ReplaceImage(pdf, ImageFixtures.Blue);

        using var viaFacade = new MemoryStream(replaced);
        using var viaOperation = new MemoryStream(replaced);
        var a = PdfOperations.GetRevisions(viaFacade);
        var b = PdfRevisions.List(viaOperation);

        Assert.Equal(b.Count, a.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(b[i].Length, a[i].Length);
    }
}
