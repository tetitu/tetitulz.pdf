// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.WpfViewer.Models;
using Tetitulz.Pdf.WpfViewer.Services;

namespace Tetitulz.Pdf.WpfViewer.Tests;

/// <summary>
/// The viewer's operations where the window depends on their answer: a protected document saved
/// protected, pages reorganised, redaction marks found by text, a picture placed from a PNG.
/// </summary>
public sealed class PdfServiceTests
{
    private static readonly PdfService Pdf = new();

    private static byte[] Encrypt(byte[] pdf, string password)
    {
        using var i = new MemoryStream(pdf, writable: false);
        using var o = new MemoryStream();
        PdfEncryptor.Encrypt(i, o, new EncryptionParameters { UserPassword = password, OwnerPassword = "owner", Algorithm = EncryptionAlgorithm.AES256 });
        return o.ToArray();
    }

    private static byte[] Decrypt(byte[] pdf, string password)
    {
        using var i = new MemoryStream(pdf, writable: false);
        using var o = new MemoryStream();
        PdfDecryptor.Decrypt(i, o, password);
        return o.ToArray();
    }

    private static (PdfDocumentModel Doc, OpenedProtection Protection) OpenProtected(byte[] plain, string password)
    {
        byte[] encrypted = Encrypt(plain, password);
        byte[] decrypted = Decrypt(encrypted, password);
        return (PdfService.OpenFromBytes(decrypted, "p.pdf", "p.pdf"), new OpenedProtection(encrypted, password, decrypted));
    }

    private static bool IsEncrypted(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var d = PdfDocument.Open(ms);
        return d.IsEncrypted;
    }

    [Fact]
    public void An_untouched_protected_document_is_saved_as_the_file_it_was()
    {
        var (doc, protection) = OpenProtected(MinimalPdfBuilder.CreateMinimalPdf(), "secret");
        ProtectedSave save = Pdf.ProtectForSave(protection, doc.Bytes, allowSignatureLoss: false);
        Assert.Same(protection.EncryptedOriginal, save.Bytes);
    }

    [Fact]
    public void A_changed_protected_document_is_saved_under_its_password()
    {
        var (doc, protection) = OpenProtected(MinimalPdfBuilder.CreateMultiPagePdf(2), "secret");
        using PdfDocumentModel changed = Pdf.RotatePage(doc, 0, 90);
        ProtectedSave save = Pdf.ProtectForSave(protection, changed.Bytes, allowSignatureLoss: false);

        Assert.NotNull(save.Bytes);
        Assert.True(IsEncrypted(save.Bytes!));
        using var reopened = PdfService.OpenFromBytes(Decrypt(save.Bytes!, "secret"), "p.pdf");
        Assert.Equal(new[] { 90, 0 }, PdfService.PageRotations(reopened));
        // The control: the empty password does not open it.
        Assert.ThrowsAny<Exception>(() => Decrypt(save.Bytes!, ""));
    }

    [Fact]
    public void Reorganizing_orders_turns_and_inserts_pages()
    {
        using var doc = PdfService.OpenFromBytes(MinimalPdfBuilder.CreateMultiPagePdf(3), "d.pdf");
        byte[] other = MinimalPdfBuilder.CreateMultiPagePdf(2);
        // Page 3, the inserted file's second page, then page 1 turned.
        using PdfDocumentModel result = Pdf.Reorganize(doc,
            new[] { new OrganizedPage(2, 0), new OrganizedPage(4, 0), new OrganizedPage(0, 90) }, new[] { other });

        Assert.Equal(3, result.PageCount);
        Assert.Equal(new[] { 0, 0, 90 }, PdfService.PageRotations(result));
        Assert.Equal("Page 3", Pdf.ExtractText(result, 0).Trim());
        Assert.Equal("Page 2", Pdf.ExtractText(result, 1).Trim());
        Assert.Equal("Page 1", Pdf.ExtractText(result, 2).Trim());
    }

    [Fact]
    public void A_document_keeps_at_least_one_page()
    {
        using var doc = PdfService.OpenFromBytes(MinimalPdfBuilder.CreateMultiPagePdf(2), "d.pdf");
        Assert.Throws<ArgumentException>(() => Pdf.Reorganize(doc, Array.Empty<OrganizedPage>(), Array.Empty<byte[]>()));
    }

    [Fact]
    public void Text_is_found_for_redaction_and_redacting_it_removes_it()
    {
        using var doc = PdfService.OpenFromBytes(MinimalPdfBuilder.CreateMinimalPdf("Account 12345 here"), "d.pdf");
        IReadOnlyList<PageRect> found = Pdf.FindForRedaction(doc, "12345", matchCase: false);
        Assert.Single(found);
        Assert.Equal(0, found[0].Page);

        var (redacted, _) = Pdf.Redact(doc, found);
        using (redacted)
        {
            string text = Pdf.ExtractText(redacted, 0);
            Assert.DoesNotContain("12345", text, StringComparison.Ordinal);
            Assert.Contains("Account", text, StringComparison.Ordinal);   // the rest of the line stays
        }
    }

    [Fact]
    public void A_png_is_placed_with_its_transparency()
    {
        // A 2 x 1 PNG: an opaque red pixel and a fully transparent one.
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 0, 0, 0 }, 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        var (rgb, alpha, w, h) = PdfService.DecodePicture(png.ToArray());
        Assert.Equal((2, 1), (w, h));
        Assert.Equal(new byte[] { 255, 0, 0 }, rgb[..3]);
        Assert.Equal(new byte[] { 255, 0 }, alpha);

        using var doc = PdfService.OpenFromBytes(MinimalPdfBuilder.CreateBlankPdf(), "d.pdf");
        using PdfDocumentModel placed = Pdf.InsertImage(doc, new PageRect(0, 100, 100, 200, 100), png.ToArray());
        var page = Tetitulz.Pdf.Base.PdfOperations.RenderPageToPixels(new MemoryStream(placed.Bytes), 0, 72);
        int Px(int x, int y) => y * page.Stride + x * 4;
        // The red half at the left of the box; the transparent half shows the white page.
        Assert.Equal(255, page.Pixels[Px(150, 792 - 150)]);
        Assert.Equal(0, page.Pixels[Px(150, 792 - 150) + 1]);
        Assert.Equal(255, page.Pixels[Px(250, 792 - 150) + 1]);
    }

    private static PageRect Glyph(double x, double y, double w = 6, double h = 12) => new(0, x, y, w, h);

    [Fact]
    public void Selected_glyphs_join_into_one_box_per_line()
    {
        IReadOnlyList<PageRect> lines = PdfService.LineBoxes(new[]
        {
            Glyph(100, 700), Glyph(106, 701), Glyph(112, 699),   // one line, a point of jitter
            Glyph(100, 680), Glyph(106, 680),                    // the next line down
        });

        Assert.Equal(2, lines.Count);
        Assert.Equal(new PageRect(0, 100, 699, 18, 14), lines[0]);
        Assert.Equal(new PageRect(0, 100, 680, 12, 12), lines[1]);
    }

    [Fact]
    public void A_right_to_left_line_and_a_far_glyph_on_it()
    {
        // Right to left in reading order joins into one box; a glyph two heights away across the
        // same baseline (the next column of a rectangle selection) starts a box of its own.
        IReadOnlyList<PageRect> lines = PdfService.LineBoxes(new[]
        {
            Glyph(112, 700), Glyph(106, 700), Glyph(100, 700),
            Glyph(70, 700),
        });

        Assert.Equal(new[] { new PageRect(0, 100, 700, 18, 12), new PageRect(0, 70, 700, 6, 12) }, lines);
    }

    [Fact]
    public void A_glyph_on_the_line_above_starts_a_box_of_its_own()
    {
        // Glyphs selected upwards: the next one stands just above the line, beside it across, so
        // only its height parts it from the line.
        IReadOnlyList<PageRect> lines = PdfService.LineBoxes(new[]
        {
            Glyph(100, 700), Glyph(106, 700),
            Glyph(112, 714),
        });

        Assert.Equal(new[] { new PageRect(0, 100, 700, 12, 12), new PageRect(0, 112, 714, 6, 12) }, lines);
    }

    [Fact]
    public void A_glyph_box_that_is_not_finite_or_has_no_area_marks_nothing()
    {
        IReadOnlyList<PageRect> lines = PdfService.LineBoxes(new[]
        {
            Glyph(double.NaN, 700), Glyph(100, 700, w: 0), Glyph(100, double.PositiveInfinity),
            Glyph(120, 700),
        });

        Assert.Equal(new[] { Glyph(120, 700) }, lines);
    }

    [Fact]
    public void Marking_a_selection_writes_one_annotation_a_page_with_a_quad_a_line_in_one_revision()
    {
        byte[] source = MinimalPdfBuilder.CreateMultiPagePdf(2);
        using var doc = PdfService.OpenFromBytes(source, "d.pdf");
        var selection = new[]
        {
            new SelectedTextPart(0, "first line second", new[] { Glyph(100, 700), Glyph(106, 700), Glyph(100, 680) }),
            new SelectedTextPart(1, "more", new[] { Glyph(50, 500, h: 10) }),
        };

        using PdfDocumentModel marked = Pdf.MarkText(doc, TextMarkup.Underline, selection);

        static int Revisions(byte[] pdf) => System.Text.RegularExpressions.Regex.Matches(
            System.Text.Encoding.Latin1.GetString(pdf), "%%EOF").Count;
        Assert.Equal(Revisions(source) + 1, Revisions(marked.Bytes));

        using var ms = new MemoryStream(marked.Bytes, writable: false);
        using var d = PdfDocument.Open(ms);
        PdfDictionary First(int page)
        {
            var annots = Assert.IsType<PdfArray>(d.ResolveObject(d.Pages[page].Dictionary[PdfName.Annots]));
            return Assert.IsType<PdfDictionary>(d.ResolveObject(Assert.Single(annots)));
        }
        double[] Numbers(PdfDictionary a, PdfName key)
            => Assert.IsType<PdfArray>(d.ResolveObject(a[key])).Select(n => ((PdfNumber)n).DoubleValue).ToArray();

        PdfDictionary onFirst = First(0);
        Assert.Equal("Underline", Assert.IsType<PdfName>(onFirst[PdfName.Subtype]).Value);
        Assert.Equal("first line second", Assert.IsType<PdfString>(d.ResolveObject(onFirst[PdfName.Contents])).GetText());
        // Two lines, each upper-left, upper-right, lower-left, lower-right.
        Assert.Equal(new double[] { 100, 712, 112, 712, 100, 700, 112, 700, 100, 692, 106, 692, 100, 680, 106, 680 },
            Numbers(onFirst, PdfName.QuadPoints));
        Assert.Equal(new double[] { 100, 680, 112, 712 }, Numbers(onFirst, PdfName.Rect));
        Assert.Equal(new double[] { 0, 0, 1 }, Numbers(onFirst, PdfName.C));
        Assert.IsType<PdfDictionary>(d.ResolveObject(onFirst[PdfName.AP]));

        Assert.Equal(new double[] { 50, 510, 56, 510, 50, 500, 56, 500 }, Numbers(First(1), PdfName.QuadPoints));
    }

    [Fact]
    public void A_selection_without_a_glyph_box_is_refused_for_having_no_text()
    {
        using var doc = PdfService.OpenFromBytes(MinimalPdfBuilder.CreateMinimalPdf(), "d.pdf");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Pdf.MarkText(doc, TextMarkup.Highlight, new[] { new SelectedTextPart(0, "", Array.Empty<PageRect>()) }));
        Assert.Contains("no text", ex.Message, StringComparison.Ordinal);

        // The control: the same call with a glyph marks.
        using PdfDocumentModel marked = Pdf.MarkText(doc, TextMarkup.Highlight, new[] { new SelectedTextPart(0, "x", new[] { Glyph(10, 10) }) });
        var listed = Tetitulz.Pdf.Base.Operations.Extraction.PdfAnnotationExtractor.ListAnnotations(new MemoryStream(marked.Bytes, writable: false));
        Assert.Equal("Highlight", Assert.Single(listed).Subtype);
    }
}
