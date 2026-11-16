// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// The PDF Association's PDF 2.0 examples (CC BY-SA 4.0, <c>Fixtures/pdf20examples/README.md</c>),
/// each read against what its own README says a correct reader shows.
/// </summary>
public class Pdf20ExamplesTests
{
    public static TheoryData<string> Files => new TheoryData<string>
    {
        "PDF 2.0 UTF-8 string and annotation.pdf",
        "PDF 2.0 image with BPC.pdf",
        "PDF 2.0 via incremental save.pdf",
        "PDF 2.0 with offset start.pdf",
        "PDF 2.0 with page level output intent.pdf",
        "Simple PDF 2.0 file.pdf",
        "pdf20-utf8-test.pdf",
    };

    private static byte[] Bytes(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pdf20examples", name));

    private static PdfDocument Open(string name) => PdfDocument.Open(new MemoryStream(Bytes(name)));

    /// <summary>Every example opens, has its page, and renders it with ink on it.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void EveryExample_OpensAndRendersItsPage(string name)
    {
        using (PdfDocument doc = Open(name))
            Assert.True(doc.Pages.Count >= 1, $"{name} opened with no page");

        using SKBitmap page = PixelAssert.Render(Bytes(name));
        Assert.True(PixelAssert.InkBounds(page).HasValue, $"{name} rendered blank");
    }

    /// <summary>
    /// A PDF 1.7 file updated to 2.0 by an incremental save. Its README says a reader of the update
    /// shows "PDF 2.0 files have spacing" and one that misses it something else; the file's own
    /// streams say it more exactly. The original revision draws "PDF 1.7 Words Need Spacing", and the
    /// update replaces that stream (object 6) with "PDF 2.0 Words Have Spacing". So the update's string
    /// is read and the original's is not.
    /// </summary>
    [Fact]
    public void TheIncrementalSave_IsRead_AndThePageSaysSo()
    {
        using PdfDocument doc = Open("PDF 2.0 via incremental save.pdf");
        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("PDF 2.0 Words Have Spacing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PDF 1.7 Words Need Spacing", text, StringComparison.Ordinal);
    }

    /// <summary>A file whose PDF data does not start at byte 0 — its offsets relative to where it does — opens and reads.</summary>
    [Fact]
    public void AFileWhosePdfDataStartsLate_OpensAndReadsItsText()
    {
        byte[] bytes = Bytes("PDF 2.0 with offset start.pdf");
        Assert.NotEqual((byte)'%', bytes[0]);   // the premise: comments come first
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(bytes));
        Assert.False(string.IsNullOrWhiteSpace(PdfTextExtractor.ExtractText(doc)));
    }

    /// <summary>
    /// "The content entry for the annotation contains Thai language text encoded in UTF-8", new in PDF
    /// 2.0: read as UTF-8, it is Thai, not the Latin-1 reading of its bytes (no <c>à¸</c>, no BOM).
    /// </summary>
    [Fact]
    public void AnAnnotationsUtf8Contents_DecodesAsThai()
    {
        using PdfDocument doc = Open("PDF 2.0 UTF-8 string and annotation.pdf");
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]);
        string contents = annots.Cast<PdfObject>()
            .Select(o => doc.ResolveObject(((PdfDictionary)doc.ResolveObject(o))[PdfName.Contents]))
            .OfType<PdfString>().Select(s => s.GetText()).First();

        Assert.Contains(contents, c => c >= '฀' && c <= '๿');
        Assert.DoesNotContain('﻿', contents);
        Assert.DoesNotContain("à¸", contents, StringComparison.Ordinal);
    }

    /// <summary>
    /// The UTF-8 test file's strings — the outline's titles, the layers' names and the Info values —
    /// decode as UTF-8: some are beyond ASCII, and none is the Latin-1 reading of a UTF-8 byte-order
    /// mark (<c>ï»¿</c>).
    /// </summary>
    [Fact]
    public void TheUtf8TestFilesStrings_DecodeAsUtf8()
    {
        using PdfDocument doc = Open("pdf20-utf8-test.pdf");
        var catalog = (PdfDictionary)doc.ResolveObject(doc.Trailer[PdfName.Root]);
        var strings = new List<string>();

        if (doc.ResolveObject(doc.Trailer[PdfName.Info]) is PdfDictionary info)
            foreach (var kvp in info)
                if (doc.ResolveObject(kvp.Value) is PdfString s) strings.Add(s.GetText());

        if (doc.ResolveObject(catalog[PdfName.Outlines]) is PdfDictionary outlines)
            for (var item = doc.ResolveObject(outlines[PdfName.First]) as PdfDictionary; item != null;
                 item = doc.ResolveObject(item[PdfName.Next]) as PdfDictionary)
                if (doc.ResolveObject(item[PdfName.Title]) is PdfString title) strings.Add(title.GetText());

        if (doc.ResolveObject(catalog[PdfName.OCProperties]) is PdfDictionary oc
            && doc.ResolveObject(oc[PdfName.OCGs]) is PdfArray groups)
            foreach (PdfObject g in groups)
                if (doc.ResolveObject(g) is PdfDictionary group && doc.ResolveObject(group[PdfName.Name]) is PdfString n)
                    strings.Add(n.GetText());

        Assert.True(strings.Count >= 3, $"only {strings.Count} strings were found to check");
        Assert.Contains(strings, s => s.Any(c => c > '\u007F'));
        Assert.DoesNotContain(strings, s => s.Contains("ï»¿", StringComparison.Ordinal) || s.Contains('﻿'));
    }
}
