// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.Plugins.PageEdits;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Smoke-test coverage for operations that had no dedicated tests.
/// All tests use in-memory PDFs and assert that:
///   (a) the operation does not throw, and
///   (b) the output is a valid PDF byte stream or the returned value is non-null.
/// </summary>
public sealed class PdfOperationsExtendedTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort cleanup */ }
        }
    }

    // ====================================================================
    //  Page count / metadata / version
    // ====================================================================

    /// <summary>
    /// A document with no <c>/Info</c> entries must come back as a metadata object whose fields are
    /// <b>empty</b> — not null, and certainly not populated with something invented. The sibling
    /// round-trip test covers the write path; this covers the read path's honest answer for a file
    /// that says nothing about itself, which <c>Assert.NotNull(meta)</c> could not.
    /// </summary>
    [Fact]
    public void GetMetadata_DocumentWithoutInfoEntries_ReturnsEmptyFieldsNotNulls()
    {
        var meta = PdfOperations.GetMetadata(Pdf());

        Assert.NotNull(meta);
        Assert.True(string.IsNullOrEmpty(meta.Title),    $"unexpected Title '{meta.Title}'");
        Assert.True(string.IsNullOrEmpty(meta.Author),   $"unexpected Author '{meta.Author}'");
        Assert.True(string.IsNullOrEmpty(meta.Subject),  $"unexpected Subject '{meta.Subject}'");
        Assert.True(string.IsNullOrEmpty(meta.Keywords), $"unexpected Keywords '{meta.Keywords}'");
    }

    [Fact]
    public void WriteMetadata_ThenGetMetadata_ReturnsUpdatedTitle()
    {
        using var output = new MemoryStream();
        PdfOperations.WriteMetadata(Pdf(), output, new PdfMetadataUpdate { Title = "ExtTest" });
        output.Position = 0;
        Assert.Equal("ExtTest", PdfOperations.GetMetadata(output).Title);
    }

    [Fact]
    public void GetPageCount_SinglePage_ReturnsOne()
        => Assert.Equal(1, PdfOperations.GetPageCount(TmpFile(MinimalPdfBuilder.CreateMinimalPdf())));

    [Fact]
    public void GetPageCount_MultiPage_ReturnsCorrectCount()
    {
        string path = TmpFile(MultiPageBytes(3));
        Assert.Equal(3, PdfOperations.GetPageCount(path));
    }

    [Fact]
    public void GetVersion_Returns14()
        => Assert.Equal("1.4", PdfOperations.GetVersion(TmpFile(MinimalPdfBuilder.CreateMinimalPdf())));

    // ====================================================================
    //  Bookmarks
    // ====================================================================

    [Fact]
    public void GetBookmarks_MinimalPdf_ReturnsEmptyList()
        => Assert.Empty(PdfOperations.GetBookmarks(Pdf()));

    [Fact]
    public void WriteBookmarks_ThenGet_ReturnsBookmarks()
    {
        using var output = new MemoryStream();
        var bm = new List<PdfBookmark>
        {
            new PdfBookmark("Chapter 1", 1, 0, null)
        };
        PdfOperations.WriteBookmarks(Pdf(), output, bm);
        output.Position = 0;
        var read = PdfOperations.GetBookmarks(output);
        Assert.NotEmpty(read);
        Assert.Equal("Chapter 1", read[0].Title);
    }

    // ====================================================================
    //  Watermark
    // ====================================================================

    [Fact]
    public void AddWatermark_DefaultOptions_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfWatermarker.AddWatermark(Pdf(), output, new WatermarkOptions { Text = "DRAFT" });
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void AddWatermark_NullOptions_Throws()
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() =>
            PdfWatermarker.AddWatermark(Pdf(), output, null));
    }

    // ====================================================================
    //  Header / Footer
    // ====================================================================

    [Fact]
    public void AddHeaderFooter_HeaderOnly_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.AddHeaderFooter(Pdf(), output, new HeaderFooterOptions
        {
            HeaderText = "My Document"
        });
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void AddHeaderFooter_FooterWithPageNumber_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.AddHeaderFooter(Pdf(), output, new HeaderFooterOptions
        {
            FooterText = "Page {page} of {total}"
        });
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void AddHeaderFooter_BothHeaderAndFooter_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.AddHeaderFooter(Pdf(), output, new HeaderFooterOptions
        {
            HeaderText = "Header",
            FooterText = "Footer"
        });
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  Annotations
    // ====================================================================

    [Fact]
    public void AddTextNote_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.AddTextNote(Pdf(), output, pageIndex: 0,
            x: 100, y: 700, text: "A note", author: "Tester");
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void AddHighlight_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.AddHighlight(Pdf(), output, pageIndex: 0,
            x1: 100, y1: 700, x2: 300, y2: 720, contents: "highlighted");
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void AddLink_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.AddLink(Pdf(), output, pageIndex: 0,
            x1: 100, y1: 700, x2: 300, y2: 720, uri: "https://example.com");
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  N-Up
    // ====================================================================

    [Fact]
    public void Create2Up_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.Create2Up(Pdf(), output);
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void Create4Up_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.Create4Up(Pdf(), output);
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void CreateNup_2x2_MultiPage_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.CreateNup(new MemoryStream(MultiPageBytes(4)), output, cols: 2, rows: 2);
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  OCG / Layers
    // ====================================================================

    [Fact]
    public void ListLayers_MinimalPdf_ReturnsEmpty()
        => Assert.Empty(PdfOperations.ListLayers(Pdf()));

    // ====================================================================
    //  Named Destinations
    // ====================================================================

    [Fact]
    public void ListDestinations_MinimalPdf_ReturnsEmpty()
        => Assert.Empty(PdfOperations.ListDestinations(Pdf()));

    [Fact]
    public void SetDestination_ThenList_ContainsEntry()
    {
        using var output = new MemoryStream();
        PdfOperations.SetDestination(Pdf(), output, "top", pageIndex: 0);
        output.Position = 0;
        var dests = PdfOperations.ListDestinations(output);
        Assert.NotEmpty(dests);
        Assert.Equal("top", dests[0].Name);
    }

    // ====================================================================
    //  Linearization
    // ====================================================================

    [Fact]
    public void Linearize_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(Pdf(), output);
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void Linearize_OutputContainsLinearizedKey()
    {
        using var output = new MemoryStream();
        PdfLinearizer.Linearize(Pdf(), output);
        output.Position = 0;
        string text = new StreamReader(output).ReadToEnd();
        Assert.Contains("Linearized", text);
    }

    // ====================================================================
    //  Color space conversion
    // ====================================================================

    [Fact]
    public void ConvertRgbToGrayscale_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.ConvertRgbToGrayscale(Pdf(), output);
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void ConvertRgbToCmyk_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.ConvertRgbToCmyk(Pdf(), output);
        Assert.True(IsPdf(output));
    }

    /// <summary>
    /// The minimal fixture declares no <c>/ColorSpace</c> resource, so the enumeration must come
    /// back <b>empty</b> — not merely non-null. An empty result and a result the code failed to
    /// populate look identical to <c>NotNull</c>; asserting the count says which one this is.
    /// </summary>
    [Fact]
    public void GetColorSpaces_DocumentWithNoColourSpaceResources_ReturnsEmpty()
        => Assert.Empty(PdfOperations.GetColorSpaces(Pdf()));

    // ====================================================================
    //  PDF/A, PDF/UA, PDF/X validation
    // ====================================================================

    [Fact]
    public void ValidatePdfA_MinimalPdf_NotConforming()
    {
        var result = PdfAValidator.Validate(Pdf());
        Assert.NotNull(result);
        Assert.False(result.IsConformant);
    }

    [Fact]
    public void ValidatePdfUa_MinimalPdf_NotConforming()
    {
        var result = PdfUaChecker.Validate(Pdf());
        Assert.NotNull(result);
        Assert.False(result.IsConforming);
    }

    [Fact]
    public void ValidatePdfX_MinimalPdf_NotConforming()
    {
        var result = PdfXValidator.Validate(Pdf());
        Assert.NotNull(result);
        Assert.False(result.IsConforming);
    }

    // ====================================================================
    //  MDP verification
    // ====================================================================

    /// <summary>
    /// An unsigned document has no DocMDP certification, so the verdict must say exactly that:
    /// no certification signature, no permission level, no incremental updates, and therefore
    /// not compliant. <c>NotNull</c> would pass on a result object whose every field was wrong.
    /// </summary>
    [Fact]
    public void VerifyMdp_UnsignedDocument_ReportsNoCertification()
    {
        var result = PdfOperations.VerifyMdp(Pdf());

        Assert.False(result.HasCertificationSignature);
        Assert.Equal(0, result.PermissionLevel);
        Assert.False(result.HasIncrementalUpdates);
        Assert.False(result.IsCompliant);
        Assert.Empty(result.Violations);
    }

    // ====================================================================
    //  ICC profile embedding
    // ====================================================================

    [Fact]
    public void EmbedIccProfile_ByteArray_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        // The 132-byte floor, 'acsp' at byte 36 and an RGB data colour space at byte 16. This used
        // to write "RGB " where the file signature belongs, which the embedder never checked.
        byte[] icc = new byte[132];
        icc[16] = 0x52; icc[17] = 0x47; icc[18] = 0x42; icc[19] = 0x20;
        icc[36] = 0x61; icc[37] = 0x63; icc[38] = 0x73; icc[39] = 0x70;
        PdfOperations.EmbedIccProfile(Pdf(), output, icc);
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  Form flattening
    // ====================================================================

    [Fact]
    public void FlattenForm_MinimalPdf_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.FlattenForm(Pdf(), output);
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  Redaction
    // ====================================================================

    [Fact]
    public void Redact_WithRegion_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfRedactor.Redact(Pdf(), output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 50, Y = 680, Width = 200, Height = 30 }
        });
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void Redact_EmptyRegions_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfRedactor.Redact(Pdf(), output, Array.Empty<RedactionRegion>());
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  Page manipulation
    // ====================================================================

    [Fact]
    public void RotatePage_90Degrees_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.RotatePage(Pdf(), output, pageIndex: 0, degrees: 90);
        Assert.True(IsPdf(output));
    }

    [Fact]
    public void InsertBlankPage_AtBeginning_IncreasesPageCount()
    {
        using var output = new MemoryStream();
        PdfOperations.InsertBlankPage(Pdf(), output, pageIndex: 0);
        output.Position = 0;
        string path = TmpFile(output.ToArray());
        Assert.Equal(2, PdfOperations.GetPageCount(path));
    }

    /// <summary>
    /// The inserted page carries its own <c>/Resources</c> (ISO 32000-1 Table 30: required, though
    /// inheritable) — in a page tree with nothing to inherit it had none, and an independent checker repaired the page.
    /// </summary>
    [Fact]
    public void InsertBlankPage_GivesThePageItsOwnResources()
    {
        byte[] input = TestInfrastructure.WriterCheckFixture.Fixture();
        using var output = new MemoryStream();
        PdfOperations.InsertBlankPage(new MemoryStream(input, writable: false), output, pageIndex: 1);

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(output.ToArray()));
        var inserted = doc.Pages[1].Dictionary;
        Assert.Null(inserted[Tetitulz.Pdf.Core.Objects.PdfName.Contents]);   // fixture: this is the blank one
        Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(
            doc.ResolveObject(inserted[Tetitulz.Pdf.Core.Objects.PdfName.Resources]));
    }

    // ====================================================================
    //  Document builder
    // ====================================================================

    [Fact]
    public void NewDocument_Build_ProducesValidPdf()
    {
        var builder = PdfOperations.NewDocument();
        Assert.NotNull(builder);
        var page = builder.AddPage(595, 842);
        page.BeginText().SetFont("Helvetica", 12).SetTextPosition(72, 720).ShowText("Hello").EndText();
        using var output = new MemoryStream();
        builder.Build(output);
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  Content appending
    // ====================================================================

    [Fact]
    public void AppendContent_ProducesValidPdf()
    {
        using var output = new MemoryStream();
        PdfOperations.AppendContent(Pdf(), output, pageIndex: 0, b =>
        {
            b.BeginText();
            b.SetFont("Helvetica", 12);
            b.SetTextPosition(100, 600);
            b.ShowText("Appended");
            b.EndText();
        });
        Assert.True(IsPdf(output));
    }

    // ====================================================================
    //  MediaBox dimension fix
    // ====================================================================

    [Fact]
    public void AppendContentToAllPages_NonZeroOriginMediaBox_CorrectDimensions()
    {
        // Build a PDF with MediaBox [50 50 662 842] — origin at (50,50).
        // Width  = 662 - 50 = 612   (NOT 662)
        // Height = 842 - 50 = 792   (NOT 842)
        byte[] pdf = BuildPdfWithMediaBox(50, 50, 662, 842);

        double observedWidth = 0, observedHeight = 0;
        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContentToAllPages(
            new MemoryStream(pdf), output,
            (b, info) => { observedWidth = info.Width; observedHeight = info.Height; });

        Assert.Equal(612, observedWidth, 0.001);
        Assert.Equal(792, observedHeight, 0.001);
    }

    private static byte[] BuildPdfWithMediaBox(double x1, double y1, double x2, double y2)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        string mb = System.FormattableString.Invariant($"[{x1} {y1} {x2} {y2}]");
        string cs = "q Q";
        sb.Append($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox {mb} /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length; sb.Append($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    private static Stream Pdf(string text = "Hello World")
        => new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(text));

    private static byte[] MultiPageBytes(int pages)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int total = 2 + pages * 2;
        var offsets = new int[total];
        int idx = 0;

        string kids = string.Join(" ", Enumerable.Range(3, pages).Where((_, i) => i % 2 == 0).Select((_, i) => $"{3 + i * 2} 0 R"));
        // simpler: just list every-other starting at 3
        kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i * 2} 0 R"));

        offsets[idx++] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[idx++] = sb.Length;
        sb.Append($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages} >>\nendobj\n");

        for (int p = 0; p < pages; p++)
        {
            string cs = $"BT /F1 12 Tf 100 700 Td (Page {p+1}) Tj ET";
            offsets[idx++] = sb.Length;
            sb.Append($"{3 + p * 2} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                      $"/Contents {4 + p * 2} 0 R " +
                      $"/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>\nendobj\n");
            offsets[idx++] = sb.Length;
            sb.Append($"{4 + p * 2} 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");
        }

        int xOff = sb.Length;
        sb.Append($"xref\n0 {total + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {total + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xOff}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private string TmpFile(byte[] bytes)
    {
        string path = TempFiles.NewPath();
        File.WriteAllBytes(path, bytes);
        _tempPaths.Add(path);
        return path;
    }

    private static bool IsPdf(MemoryStream ms)
    {
        if (ms.Length < 5) return false;
        ms.Position = 0;
        var h = new byte[5];
        ms.Read(h, 0, 5);
        return h[0]=='%' && h[1]=='P' && h[2]=='D' && h[3]=='F' && h[4]=='-';
    }
}
