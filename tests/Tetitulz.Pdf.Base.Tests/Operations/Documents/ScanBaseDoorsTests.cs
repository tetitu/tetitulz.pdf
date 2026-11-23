// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Doors that answered for what they could not read or did not bound: the clear-text writers' refusal of an
/// encrypted document, the redaction review's timeouts, the portfolio readers, the image extractor's total and the
/// image index's entries.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class ScanBaseDoorsTests
{
    private static byte[] Plain() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
        .Finish();

    private static byte[] Encrypted()
    {
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(Plain()), output, new EncryptionParameters
        {
            UserPassword = "user", OwnerPassword = "owner", Algorithm = EncryptionAlgorithm.AES128, Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    // ------------------------------------------------------------------ F-052, F-055: one refusal, one type

    /// <summary>
    /// The four clear-text writers refuse an encrypted document through the one refusal: the encrypted-document
    /// type a caller catches to send the user to the password route, the one wording, and the filler its own route.
    /// </summary>
    [Theory]
    [InlineData("fill")]
    [InlineData("xfa")]
    [InlineData("flatten")]
    [InlineData("filter")]
    public void EveryClearTextWriter_RefusesAnEncryptedDocument_Alike(string writer)
    {
        var values = new Dictionary<string, string> { ["a"] = "1" };
        Action write = writer switch
        {
            "fill" => () => PdfFormFiller.Fill(new MemoryStream(Encrypted()), new MemoryStream(), values, new FormFillOptions()),
            "xfa" => () => PdfXfaFormFiller.Fill(new MemoryStream(Encrypted()), new MemoryStream(), values),
            "flatten" => () => PdfFormFlattener.Flatten(new MemoryStream(Encrypted()), new MemoryStream()),
            _ => () => PdfContentFilter.Remove(new MemoryStream(Encrypted()), new MemoryStream(), PdfContentClass.Text),
        };

        var ex = Assert.Throws<PdfEncryptedDocumentException>(write);
        Assert.Contains("would be written in clear text where the reader expects per-object ciphertext", ex.Message, StringComparison.Ordinal);
        Assert.Contains(writer == "fill" ? "PdfEncryptedForms.Fill" : "PdfEncryptedRoundTrip.Run", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ F-053: the review says where a pattern timed out

    private static byte[] TextPdf(string text)
    {
        string content = $"BT /F1 12 Tf 10 30 Td ({text}) Tj ET";
        return new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 72] /Contents 4 0 R " +
                    "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Courier >> >> >> >>")
            .StreamObj(4, "<<", content)
            .Finish();
    }

    /// <summary>
    /// A pattern that backtracks past its timeout on a line lists nothing there and says which line; the control,
    /// a pattern that finishes, lists its match and says nothing.
    /// </summary>
    [Theory]
    [InlineData("(a+)+b", true)]
    [InlineData("aaaa", false)]
    public void APatternThatTimesOut_IsNamedByTheReview(string pattern, bool timesOut)
    {
        var options = new TextRedactionOptions { UseRegex = true, RegexTimeout = TimeSpan.FromMilliseconds(5) };
        var matches = PdfTextRedactor.FindMatches(new MemoryStream(TextPdf(new string('a', 40) + "!")), pattern, options);

        if (timesOut)
        {
            Assert.Empty(matches);
            Assert.Contains("page 1, line 1", PdfTextRedactor.LastFindMatchesWarning, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEmpty(matches);
            Assert.Null(PdfTextRedactor.LastFindMatchesWarning);
        }
    }

    // ------------------------------------------------------------------ F-054: the portfolio readers

    /// <summary>The three readers refuse an encrypted document, which exposes no catalog to read.</summary>
    [Theory]
    [InlineData("items")]
    [InlineData("schema")]
    [InlineData("is")]
    public void ThePortfolioReaders_RefuseAnEncryptedDocument(string reader)
    {
        Action read = reader switch
        {
            "items" => () => PdfPortfolioManager.ListItems(new MemoryStream(Encrypted())),
            "schema" => () => PdfPortfolioManager.GetSchema(new MemoryStream(Encrypted())),
            _ => () => PdfPortfolioManager.IsPortfolio(new MemoryStream(Encrypted())),
        };
        Assert.Contains("of an encrypted document without its password", Assert.Throws<PdfEncryptedDocumentException>(read).Message);
        Assert.False(PdfPortfolioManager.IsPortfolio(new MemoryStream(Plain())));   // the control: read, and answered
    }

    private static byte[] PortfolioOf(int items, string kids = null, string seventh = null)
    {
        string names = string.Concat(Enumerable.Range(0, items).Select(i => $"(f{i}) 6 0 R "));
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /Collection << /Type /Collection >> /Names << /EmbeddedFiles 4 0 R >> >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
            .Obj(4, kids ?? $"<< /Names [{names}] >>")
            .StreamObj(5, "<< /Type /EmbeddedFile", "hello")
            .Obj(6, "<< /Type /Filespec /F (f) /EF << /F 5 0 R >> >>");
        if (seventh != null) b.Obj(7, seventh);
        return b.Finish();
    }

    /// <summary>
    /// Items without a <c>/Size</c> are measured by decoding, once a stream however many items name it; the size is
    /// the stream's. A name tree that cannot be read whole (a kid that does not parse) is said.
    /// </summary>
    [Fact]
    public void PortfolioItems_AreMeasuredOnceAStream_AndATruncatedTreeIsSaid()
    {
        long before = Tetitulz.Pdf.Base.Operations.Extraction.DecodedLengths.Decodes;
        var items = PdfPortfolioManager.ListItems(new MemoryStream(PortfolioOf(20)));
        Assert.Equal(20, items.Count);
        Assert.All(items, i => Assert.Equal(5, i.Size));
        Assert.Equal(1, Tetitulz.Pdf.Base.Operations.Extraction.DecodedLengths.Decodes - before);
        Assert.Null(PdfPortfolioManager.LastReadWarning);

        PdfPortfolioManager.ListItems(new MemoryStream(PortfolioOf(1, "<< /Kids [7 0 R] >>", "]")));
        Assert.Contains("could not be read whole", PdfPortfolioManager.LastReadWarning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ F-050: one call's images under one total

    private static string Deflated(int bytes)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(new byte[bytes]);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>
    /// Two images of 600 decoded bytes under a total of a thousand: the first is written, the second is not and is
    /// named. The control, under the default total, writes both.
    /// </summary>
    [Theory]
    [InlineData(1000L, 1)]
    [InlineData(PdfLimits.DefaultMaxExtractedImageBytesPerCall, 2)]
    public void TheImagesOneCallWrites_AreHeldToATotal(long total, int written)
    {
        const string image = " /Type /XObject /Subtype /Image /Width 20 /Height 30 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode";
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << /XObject << /A 4 0 R /B 5 0 R >> >> >>")
            .StreamObj(4, "<<" + image, Deflated(600))
            .StreamObj(5, "<<" + image, Deflated(600))
            .Finish();
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            PdfLimits.MaxExtractedImageBytesPerCall = total;
            var images = PdfImageExtractor.ExtractImages(new MemoryStream(pdf), dir);

            Assert.Equal(written, images.Count);
            Assert.Equal(written, Directory.GetFiles(dir).Length);
            if (written == 1) Assert.Contains("not written: image 2", PdfImageExtractor.LastExtractionWarning, StringComparison.Ordinal);
            else Assert.Null(PdfImageExtractor.LastExtractionWarning);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------ F-051: the image index's entries

    /// <summary>
    /// Ten pages sharing one dictionary of twenty images are two hundred entry reads: under fifty the walk stops,
    /// says so, and leaves later pages unrecorded. The control records every image on all ten pages.
    /// </summary>
    [Theory]
    [InlineData(50L, true)]
    [InlineData(PdfLimits.DefaultMaxImageIndexEntriesPerDocument, false)]
    public void TheImageIndex_ReadsSharedEntriesUnderADocumentBudget(long budget, bool cut)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [" + string.Concat(Enumerable.Range(0, 10).Select(i => $"{10 + i} 0 R ")) + "] /Count 10 >>")
            .Obj(3, "<< /XObject << " + string.Concat(Enumerable.Range(0, 20).Select(i => $"/I{i} {30 + i} 0 R ")) + ">> >>");
        for (int i = 0; i < 10; i++) b.Obj(10 + i, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources 3 0 R >>");
        for (int i = 0; i < 20; i++) b.StreamObj(30 + i, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "\0");
        try
        {
            PdfLimits.MaxImageIndexEntriesPerDocument = budget;
            using var doc = PdfDocument.Open(new MemoryStream(b.Finish()));
            var index = DocumentImageIndex.Build(doc);

            Assert.Equal(cut, index.EntryCut != null);
            Assert.Equal(20, index.Entries.Count());
            Assert.Equal(cut, index.Entries.Any(e => e.Value.Pages.Count < 10));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The same budget holds a shared dictionary of tiling patterns or of Type 3 fonts, each a container the walk
    /// descends into: two hundred entries read under fifty stop and say so; the control reads them all.
    /// </summary>
    [Theory]
    [InlineData("Pattern", 50L, true)]
    [InlineData("Pattern", PdfLimits.DefaultMaxImageIndexEntriesPerDocument, false)]
    [InlineData("Font", 50L, true)]
    [InlineData("Font", PdfLimits.DefaultMaxImageIndexEntriesPerDocument, false)]
    public void TheImageIndex_ChargesSharedPatternAndFontEntries(string category, long budget, bool cut)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [" + string.Concat(Enumerable.Range(0, 10).Select(i => $"{10 + i} 0 R ")) + "] /Count 10 >>")
            .Obj(3, $"<< /{category} << " + string.Concat(Enumerable.Range(0, 20).Select(i => $"/E{i} {30 + i} 0 R ")) + ">> >>");
        for (int i = 0; i < 10; i++) b.Obj(10 + i, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources 3 0 R >>");
        for (int i = 0; i < 20; i++)
        {
            if (category == "Pattern")
                b.StreamObj(30 + i, "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1 1] /XStep 1 /YStep 1 /Resources << >>", "");
            else
                b.Obj(30 + i, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << >> " +
                              "/Encoding << /Differences [] >> /FirstChar 0 /LastChar 0 /Widths [0] /Resources << >> >>");
        }
        try
        {
            PdfLimits.MaxImageIndexEntriesPerDocument = budget;
            using var doc = PdfDocument.Open(new MemoryStream(b.Finish()));
            var index = DocumentImageIndex.Build(doc);

            Assert.Equal(cut, index.EntryCut != null);
            Assert.True(index.FormsVisited > 0);   // the walk reached the containers it charges
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
