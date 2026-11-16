// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// ISO 32000-2 Table 334: a rich media annotation's <c>/Assets</c> is a name tree of file
/// specifications, the payload under <c>/EF /F</c>. The lister and the extractor follow it, through
/// a tree with a <c>/Kids</c> leaf too, and take the content type from the embedded file's
/// <c>/Subtype</c>; a value that is a bare stream, which some producers write, is read as before.
/// </summary>
public class RichMediaFileSpecAssetsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAssetThatIsAFileSpecification_IsListedAndExtracted(bool twoLevelTree)
    {
        byte[] pdf = Pdf(assetValue: "6 0 R", twoLevelTree);

        var info = Assert.Single(PdfRichMediaExtractor.ListRichMedia(new MemoryStream(pdf)));
        Assert.Equal(22, info.DataLength);
        Assert.Null(info.DataLengthUnavailableReason);
        Assert.Equal("video/mp4", info.ContentType);

        Assert.Equal(22, PdfRichMediaExtractor.ExtractMedia(new MemoryStream(pdf), 0).Length);
    }

    /// <summary>The control: a bare stream as the asset's value reads as it did.</summary>
    [Fact]
    public void AnAssetThatIsABareStream_IsStillRead()
    {
        byte[] pdf = Pdf(assetValue: "7 0 R", twoLevelTree: false);

        var info = Assert.Single(PdfRichMediaExtractor.ListRichMedia(new MemoryStream(pdf)));
        Assert.Equal(22, info.DataLength);
        Assert.Equal("video/mp4", info.ContentType);
    }

    [Fact]
    public void AFileSpecificationWithNoEmbeddedFile_IsListedWithTheReason()
    {
        byte[] pdf = Pdf(assetValue: "8 0 R", twoLevelTree: false);

        var info = Assert.Single(PdfRichMediaExtractor.ListRichMedia(new MemoryStream(pdf)));
        Assert.Equal(0, info.DataLength);
        Assert.Contains("/EF /F", info.DataLengthUnavailableReason ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Object 6 is a file specification over embedded file 7 (22 bytes, <c>/Subtype /video#2Fmp4</c>);
    /// 8 is a file specification naming an external file only; 9 is the one-level /Assets tree, 10 a
    /// root whose /Kids names leaf 9.
    /// </summary>
    private static byte[] Pdf(string assetValue, bool twoLevelTree)
    {
        const string data = "0123456789abcdef012345";
        string assets = twoLevelTree ? "10 0 R" : "9 0 R";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [10 10 100 100] /RichMediaContent << /Assets " + assets + " >> >>",
            "null",
            "<< /Type /Filespec /F (a.mp4) /UF (a.mp4) /EF << /F 7 0 R >> >>",
            "<< /Type /EmbeddedFile /Subtype /video#2Fmp4 /Length " + data.Length + " >>\nstream\n" + data + "\nendstream",
            "<< /Type /Filespec /F (external.mp4) >>",
            "<< /Names [(a.mp4) " + assetValue + "] >>",
            "<< /Kids [9 0 R] >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
