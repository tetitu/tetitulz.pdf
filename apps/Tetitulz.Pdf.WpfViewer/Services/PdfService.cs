// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Plugins.Ocr;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.Plugins.PageEdits;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.Plugins.Docx;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.WpfViewer.Models;
using Tetitulz.Pdf.Encryption;

namespace Tetitulz.Pdf.WpfViewer.Services;

/// <summary>A rectangle on a page, in the page's own coordinates: points, origin bottom-left.</summary>
public readonly record struct PageRect(int Page, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Top => Y + Height;
}

/// <summary>The part of the user's text selection on one page: its text and each selected glyph's box.</summary>
public sealed record SelectedTextPart(int Page, string Text, IReadOnlyList<PageRect> GlyphBoxes);

/// <summary>The text-markup annotations a selection can be marked with.</summary>
public enum TextMarkup
{
    Highlight,
    Underline,
    StrikeOut,
}

/// <summary>One page of a reorganised document: which page of the source, turned to which <c>/Rotate</c>.</summary>
public readonly record struct OrganizedPage(int SourcePage, int Rotation);

/// <summary>One signature's verdict, in words the status line and the report use.</summary>
public sealed record SignatureVerdict(string Field, string Signer, bool Valid, bool Trusted, IReadOnlyList<string> Problems);

/// <summary>
/// The library's operations as the viewer uses them. Each takes a document and returns a new one —
/// the document is never changed in place — and an operation's own report is returned beside the
/// document, never dropped.
/// </summary>
public sealed class PdfService
{
    // ======== Document lifecycle ========
    public PdfDocumentModel Open(string path) => FromBytes(File.ReadAllBytes(path), Path.GetFileName(path), path);

    // ======== Text ========
    // These read their own copy rather than the shared document: they run on worker threads, and the
    // parser's object cache is not thread-safe.
    public string ExtractText(PdfDocumentModel doc, int page)
    {
        if (page < 0 || page >= doc.PageCount) return "";
        using var ms = new MemoryStream(doc.ShownBytes);
        using var d = PdfDocument.Open(ms);
        return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(d.Pages[page]);
    }

    public string[][] ExtractTables(PdfDocumentModel doc, int page)
    { using var ms = new MemoryStream(doc.ShownBytes); var tables = PdfTableExtractor.Extract(ms); var t = tables.FirstOrDefault(x => x.PageIndex == page); return t?.Rows.Select(r => r.Cells.Select(c => c.Text).ToArray()).ToArray() ?? Array.Empty<string[]>(); }

    // ======== Text and pictures on a page ========
    public PdfDocumentModel AddText(PdfDocumentModel doc, int page, double x, double y, string text, double size)
        => Pipe(doc, (i, o) => PdfPageContentAppender.AppendContent(i, o, page, bld =>
        { var f = bld.AddStandardFont(StandardFonts.Helvetica); bld.BeginText().SetFillColorRgb(0, 0, 0).SetFont(f, (float)size).SetTextPosition(x, y).ShowText(text).EndText(); }));

    /// <summary>
    /// Draws a JPEG or PNG stretched over <paramref name="where"/>: a JPEG as it is, a PNG (or any
    /// other picture Windows reads) as its decoded samples, its transparency kept.
    /// </summary>
    public PdfDocumentModel InsertImage(PdfDocumentModel doc, PageRect where, byte[] image)
    {
        if (Tetitulz.Pdf.Core.Imaging.RasterImageInspector.TryInspect(image, out var info) && IsJpeg(image))
            return Pipe(doc, (i, o) => PdfImageInserter.InsertJpeg(i, o, where.Page, image, info.Width, info.Height,
                where.X, where.Y, where.Width, where.Height));

        var (rgb, alpha, w, h) = DecodePicture(image);
        return Pipe(doc, (i, o) => PdfPageContentAppender.AppendContent(i, o, where.Page, b =>
        {
            string name = b.AddImage(rgb, w, h, null, "DeviceRGB", 8, null, alpha);
            b.SaveState();
            b.Transform(where.Width, 0, 0, where.Height, where.X, where.Y);
            b.DrawImage(name, 0, 0, 1, 1);
            b.RestoreState();
        }));
    }

    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>A picture's straight RGB samples and alpha (null when fully opaque), rows from the top.</summary>
    internal static (byte[] Rgb, byte[]? Alpha, int Width, int Height) DecodePicture(byte[] image)
        => Pictures.Decode(image);

    public PdfDocumentModel AddBarcode(PdfDocumentModel doc, int page, double x, double y, string data)
        => Pipe(doc, (i, o) => PdfPageContentAppender.AppendContent(i, o, page, bld =>
            bld.AppendRaw(PdfBarcodeWriter.GenerateContentStream(data, BarcodeType.Code128, (float)x, (float)y))));

    // ======== Annotations ========
    public PdfDocumentModel AddNote(PdfDocumentModel doc, int page, double x, double y, string text)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddTextNote(i, o, page, x, y, text));
    public PdfDocumentModel AddHighlight(PdfDocumentModel doc, PageRect r)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddHighlight(i, o, r.Page, r.X, r.Y, r.Right, r.Top));
    public PdfDocumentModel AddUnderline(PdfDocumentModel doc, PageRect r)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddUnderline(i, o, r.Page, r.X, r.Y, r.Right, r.Top));
    public PdfDocumentModel AddStrikeOut(PdfDocumentModel doc, PageRect r)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddStrikeOut(i, o, r.Page, r.X, r.Y, r.Right, r.Top));

    /// <summary>
    /// Marks the selected text: one annotation a page, one quad for each line the selection crosses
    /// there, the page's selected text as its <c>/Contents</c> — every page in one revision.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing could be written: the selection holds no glyph, or every page it names was refused.
    /// </exception>
    public PdfDocumentModel MarkText(PdfDocumentModel doc, TextMarkup kind, IReadOnlyList<SelectedTextPart> selection)
    {
        var annotations = new List<FormDataAnnotation>();
        foreach (SelectedTextPart part in selection)
        {
            IReadOnlyList<PageRect> lines = LineBoxes(part.GlyphBoxes);
            if (lines.Count == 0) continue;
            var quads = new double[lines.Count * 8];
            double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
            for (int i = 0; i < lines.Count; i++)
            {
                PageRect l = lines[i];
                // §12.5.6.10's order: upper-left, upper-right, lower-left, lower-right.
                double[] corners = { l.X, l.Top, l.Right, l.Top, l.X, l.Y, l.Right, l.Y };
                Array.Copy(corners, 0, quads, i * 8, 8);
                left = Math.Min(left, l.X); bottom = Math.Min(bottom, l.Y);
                right = Math.Max(right, l.Right); top = Math.Max(top, l.Top);
            }
            annotations.Add(new FormDataAnnotation(SubtypeOf(kind))
            {
                PageIndex = part.Page,
                Rect = new[] { left, bottom, right, top },
                QuadPoints = quads,
                Color = ColourOf(kind),
                Contents = string.IsNullOrWhiteSpace(part.Text) ? null : part.Text,
                Flags = PdfAnnotationFlags.Print,
            });
        }
        if (annotations.Count == 0) throw new InvalidOperationException("The selection holds no text to mark.");

        return Pipe(doc, (i, o) =>
        {
            if (PdfAnnotationWriter.WriteAnnotations(i, o, annotations) == 0)
                throw new InvalidOperationException("Nothing was marked: " + PdfAnnotationWriter.LastAnnotationsSkipped);
        });
    }

    private static string SubtypeOf(TextMarkup kind) => kind switch
    {
        TextMarkup.Highlight => PdfAnnotationSubtypes.Highlight,
        TextMarkup.Underline => PdfAnnotationSubtypes.Underline,
        TextMarkup.StrikeOut => PdfAnnotationSubtypes.StrikeOut,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // The colours the library's one-box writers give each kind, so the two routes mark alike.
    private static double[] ColourOf(TextMarkup kind) => kind switch
    {
        TextMarkup.Highlight => new[] { 1.0, 1.0, 0.0 },
        TextMarkup.Underline => new[] { 0.0, 0.0, 1.0 },
        _ => new[] { 1.0, 0.0, 0.0 },
    };

    /// <summary>
    /// The selected glyphs of one page, in reading order, joined into one box per line: a glyph
    /// joins the line before it when its middle lies within the line's height and it is less than
    /// two glyph heights away across. Boxes that are not finite or have no area are passed over.
    /// </summary>
    internal static IReadOnlyList<PageRect> LineBoxes(IReadOnlyList<PageRect> glyphs)
    {
        var lines = new List<PageRect>();
        PageRect? line = null;
        foreach (PageRect g in glyphs)
        {
            if (!IsFinite(g.X) || !IsFinite(g.Y) || !IsFinite(g.Width) || !IsFinite(g.Height) || g.Width <= 0 || g.Height <= 0) continue;
            if (line is PageRect l)
            {
                double middle = g.Y + g.Height / 2;
                double across = Math.Max(0, Math.Max(g.X - l.Right, l.X - g.Right));
                if (middle >= l.Y && middle <= l.Top && across < 2 * g.Height)
                {
                    double x = Math.Min(l.X, g.X), y = Math.Min(l.Y, g.Y);
                    line = new PageRect(l.Page, x, y, Math.Max(l.Right, g.Right) - x, Math.Max(l.Top, g.Top) - y);
                    continue;
                }
                lines.Add(l);
            }
            line = g;
        }
        if (line is PageRect last) lines.Add(last);
        return lines;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    public PdfDocumentModel AddRectangle(PdfDocumentModel doc, PageRect r)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddRectangle(i, o, r.Page, r.X, r.Y, r.Right, r.Top, r: 1, g: 0, b: 0));
    public PdfDocumentModel AddFreeText(PdfDocumentModel doc, PageRect r, string text)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddFreeText(i, o, r.Page, r.X, r.Y, r.Width, r.Height, text));
    public PdfDocumentModel AddStamp(PdfDocumentModel doc, PageRect r, string name)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddStamp(i, o, r.Page, r.X, r.Y, r.Width, r.Height, name));
    public PdfDocumentModel AddLink(PdfDocumentModel doc, PageRect r, string uri)
        => Pipe(doc, (i, o) => PdfAnnotationWriter.AddUriLink(i, o, r.Page, r.X, r.Y, r.Right, r.Top, uri));

    // ======== Watermark, header, footer ========
    public PdfDocumentModel AddWatermark(PdfDocumentModel doc, string text, float opacity)
        => Pipe(doc, (i, o) => PdfWatermarker.AddWatermark(i, o, new WatermarkOptions { Text = text, Opacity = opacity }));
    public PdfDocumentModel AddHeaderFooter(PdfDocumentModel doc, string header, string footer)
        => Pipe(doc, (i, o) => PdfHeaderFooterWriter.AddHeaderFooter(i, o, new HeaderFooterOptions { HeaderText = header, FooterText = footer }));

    // ======== Signing ========

    /// <summary>
    /// Signs with a visible picture over <paramref name="where"/>, under <paramref name="parameters"/>
    /// (a PFX, or a Windows store certificate's <see cref="SignatureParameters.ExternalSigner"/>).
    /// Returns the signer's own warning beside the document: what the library's verifier, under its
    /// defaults, refuses this signature for (an expired certificate, one issued for another purpose,
    /// SHA-1, a small key), or null — read inside, because the slot is the signing thread's.
    /// </summary>
    public (PdfDocumentModel Doc, string? Warning) SignVisible(PdfDocumentModel doc, SignatureParameters parameters,
        byte[] picture, PageRect where)
        => Pipe(doc, (i, o) =>
        {
            VisualSignatureBuilder.Sign(i, o, picture, parameters, where.Page + 1,
                (float)where.X, (float)where.Y, (float)where.Width, (float)where.Height);
            return PdfSigner.LastSignerWarning;
        });

    /// <summary>
    /// Every signature's verdict — integrity, and trust under <paramref name="options"/>' anchors. A
    /// document opened under a password is verified as the file it was: the signatures cover the
    /// file's own bytes, and the decrypted copy the commands work on has every string rewritten.
    /// </summary>
    public IReadOnlyList<SignatureVerdict> VerifySignatures(PdfDocumentModel doc, OpenedProtection? opened, SignatureVerificationOptions options)
    {
        IReadOnlyList<SignatureVerificationResult> results;
        if (opened != null)
        {
            using var ms = new MemoryStream(opened.EncryptedOriginal, writable: false);
            results = PdfEncryptionOperations.VerifySignatures(ms, opened.Password, options);
        }
        else
        {
            using var ms = new MemoryStream(doc.Bytes, writable: false);
            results = PdfSigningOperations.VerifySignatures(ms, options);
        }
        return results.Select(r => new SignatureVerdict(
            r.FieldName ?? "(no field)",
            r.SignerCommonName ?? r.SignerSubject ?? "(unknown signer)",
            r.IsValid,
            r.IsFullyTrusted,
            r.ValidationErrors.Concat(r.MdpViolations).ToList())).ToList();
    }

    /// <summary>Whether the document carries a digital signature.</summary>
    public static bool HasSignature(byte[] pdf) => ProtectedDocuments.HasSignature(pdf);

    // ======== Forms ========
    public PdfDocumentModel FlattenForms(PdfDocumentModel doc)
        => Pipe(doc, (i, o) => PdfFormFlattener.Flatten(i, o));
    public string[] GetFormFieldNames(PdfDocumentModel doc)
    { using var ms = new MemoryStream(doc.ShownBytes); using var d = PdfDocument.Open(ms); return d.AcroForm?.Fields?.Select(f => f.FullyQualifiedName).ToArray() ?? Array.Empty<string>(); }

    // ======== Pages ========
    public PdfDocumentModel RotatePage(PdfDocumentModel doc, int page, int degrees)
        => Pipe(doc, (i, o) => PdfPageManipulator.RotatePage(i, o, page, degrees));
    public PdfDocumentModel DeletePage(PdfDocumentModel doc, int page)
        => Pipe(doc, (i, o) => PdfPageManipulator.DeletePage(i, o, page));
    public PdfDocumentModel InsertBlankPage(PdfDocumentModel doc, int page)
        => Pipe(doc, (i, o) => PdfPageManipulator.InsertBlankPage(i, o, page, PdfPageSizes.LetterWidth, PdfPageSizes.LetterHeight));
    public PdfDocumentModel CropPage(PdfDocumentModel doc, PageRect r)
        => Pipe(doc, (i, o) => PdfPageManipulator.CropPage(i, o, r.Page, r.X, r.Y, r.Width, r.Height));

    /// <summary>The <c>/Rotate</c> of each page, normalised to 0, 90, 180 or 270.</summary>
    public static int[] PageRotations(PdfDocumentModel doc)
        => doc.Doc.Pages.Select(p => PdfPageRotation.Normalize(p.Rotate)).ToArray();

    /// <summary>
    /// The document rebuilt as <paramref name="pages"/> lists it — pages in that order, each turned
    /// as it says — with the pages of <paramref name="inserted"/> numbered after the document's own
    /// (source page <c>PageCount + k</c> is the inserted files' k-th page, in order).
    /// </summary>
    public PdfDocumentModel Reorganize(PdfDocumentModel doc, IReadOnlyList<OrganizedPage> pages, IReadOnlyList<byte[]> inserted)
    {
        if (pages.Count == 0) throw new ArgumentException("A document keeps at least one page.", nameof(pages));
        byte[] source = doc.Bytes;
        if (inserted.Count > 0)
        {
            var streams = new[] { source }.Concat(inserted).Select(b => (Stream)new MemoryStream(b, writable: false)).ToArray();
            using var merged = new MemoryStream();
            try { PdfMerger.Merge(merged, streams); }
            finally { foreach (var s in streams) s.Dispose(); }
            source = merged.ToArray();
        }

        byte[] ordered;
        using (var i = new MemoryStream(source, writable: false))
        using (var o = new MemoryStream())
        {
            PdfSplitter.ExtractPages(i, o, pages.Select(p => p.SourcePage + 1).ToList());
            ordered = o.ToArray();
        }

        // Each page whose turn differs from what it now has is turned, all in one model at the end.
        PdfDocumentModel result = FromBytes(ordered, doc.FileName, doc.FilePath);
        int[] now = PageRotations(result);
        for (int p = 0; p < pages.Count; p++)
        {
            if (now[p] == pages[p].Rotation) continue;
            var next = RotatePage(result, p, pages[p].Rotation);
            result.Dispose();
            result = next;
        }
        return result;
    }

    // ======== Merge / split ========
    public PdfDocumentModel Merge(byte[][] pdfs)
    { var ss = pdfs.Select(b => (Stream)new MemoryStream(b)).ToArray(); using var o = new MemoryStream(); PdfMerger.Merge(o, ss); foreach (var s in ss) s.Dispose(); return FromBytes(o.ToArray(), "merged.pdf", ""); }

    public byte[] ExtractPage(PdfDocumentModel doc, int page)
    { using var i = new MemoryStream(doc.Bytes); using var o = new MemoryStream(); PdfSplitter.SplitRange(i, o, page + 1, page + 1); return o.ToArray(); }

    public PdfDocumentModel NUp(PdfDocumentModel doc)
    { using var i = new MemoryStream(doc.Bytes); using var o = new MemoryStream(); PdfNup.Create4Up(i, o); return FromBytes(o.ToArray(), doc.FileName, doc.FilePath); }

    // ======== Optimize / repair / sanitize / linearize ========
    public (PdfDocumentModel Doc, OptimizationResult Result) Optimize(PdfDocumentModel doc) => Pipe(doc, (i, o) => PdfOptimizer.Optimize(i, o));
    public (PdfDocumentModel Doc, PdfRepairResult Result) Repair(PdfDocumentModel doc) => Pipe(doc, (i, o) => PdfRepair.Repair(i, o));

    /// <summary>The document sanitized, and the sanitizer's own notes (what it left, what it could not read).</summary>
    public (PdfDocumentModel Doc, string? Notes) Sanitize(PdfDocumentModel doc) => Pipe(doc, (i, o) =>
    {
        PdfSanitizer.Sanitize(i, o);
        string?[] notes = { PdfSanitizer.LastXfaWarning, PdfSanitizer.LastObjectWarning };
        string joined = string.Join(" ", notes.Where(n => !string.IsNullOrEmpty(n)));
        return joined.Length == 0 ? null : joined;
    });

    public PdfDocumentModel Linearize(PdfDocumentModel doc) => Pipe(doc, (i, o) => PdfLinearizer.Linearize(i, o));

    // ======== Encrypt / decrypt ========
    // AES-256: the library's default is AES-128, kept for old readers, and its key is derived from
    // the password by fifty rounds of MD5 — a password someone chose deserves revision 6's derivation.
    public PdfDocumentModel Encrypt(PdfDocumentModel doc, string userPassword, string ownerPassword)
        => Pipe(doc, (i, o) => PdfEncryptor.Encrypt(i, o,
            new Tetitulz.Pdf.Base.Encryption.EncryptionParameters
            {
                UserPassword = userPassword, OwnerPassword = ownerPassword,
                Algorithm = Tetitulz.Pdf.Base.Encryption.EncryptionAlgorithm.AES256,
            }));
    public PdfDocumentModel Decrypt(PdfDocumentModel doc, string password)
        => Pipe(doc, (i, o) => PdfDecryptor.Decrypt(i, o, password));

    /// <summary>
    /// What a save writes for a document that was opened under a password, given the plaintext the
    /// commands have made of it: <see cref="ProtectedDocuments.ProtectForSave"/>.
    /// </summary>
    public ProtectedSave ProtectForSave(OpenedProtection protection, byte[] plaintext, bool allowSignatureLoss)
        => ProtectedDocuments.ProtectForSave(protection, plaintext, allowSignatureLoss);

    // ======== Redaction ========

    /// <summary>
    /// True redaction of every region in one pass: <see cref="PdfRedactor.RedactRegions"/> removes the
    /// glyphs and images inside them and writes a single-revision PDF, so the content is recoverable
    /// from no revision. Returns the engine's note about a shared form left reachable from other
    /// pages, or null.
    /// </summary>
    /// <remarks>
    /// There is deliberately no fallback to a cover box: <c>RedactRegions</c> fails closed — it throws
    /// rather than risk a leak it cannot prevent — and a box over text that is still there is the
    /// leak. The caller reports the refusal.
    /// </remarks>
    public (PdfDocumentModel Doc, string? Note) Redact(PdfDocumentModel doc, IReadOnlyList<PageRect> regions)
        => Pipe(doc, (i, o) =>
        {
            PdfRedactor.RedactRegions(i, o, regions.Select(r => new RedactionRegion
            { PageIndex = r.Page, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height }).ToList());
            return PdfRedactionEngine.LastSharedFormWarning;
        });

    /// <summary>Where <paramref name="text"/> is drawn, as regions to mark for redaction.</summary>
    public IReadOnlyList<PageRect> FindForRedaction(PdfDocumentModel doc, string text, bool matchCase)
    {
        using var ms = new MemoryStream(doc.ShownBytes, writable: false);
        return PdfTextRedactor.FindMatches(ms, text, new TextRedactionOptions { MatchCase = matchCase })
            .Select(m => new PageRect(m.PageIndex, m.Area.X, m.Area.Y, m.Area.Width, m.Area.Height)).ToList();
    }

    // ======== Conversion ========
    public PdfDocumentModel OpenDocx(string path)
        => FromBytes(Tetitulz.Pdf.DocxToPdf.DocxToPdfConverter.ConvertToBytes(path), Path.GetFileNameWithoutExtension(path) + ".pdf", "");
    public string ToHtml(PdfDocumentModel doc) { using var ms = new MemoryStream(doc.ShownBytes); return PdfHtmlConverter.ConvertToLayoutHtml(ms); }
    public string ToSvg(PdfDocumentModel doc, int page) { using var ms = new MemoryStream(doc.ShownBytes); return PdfSvgRenderer.RenderPageToSvg(ms, page); }
    public byte[] ExportToDocx(PdfDocumentModel doc) => PdfToDocxConverter.Convert(doc.ShownBytes);
    public byte[] ExportToExcel(PdfDocumentModel doc) => PdfToExcelConverter.Convert(doc.ShownBytes);
    public byte[] ExportToPptx(PdfDocumentModel doc) => PdfToPptxConverter.Convert(doc.ShownBytes);

    public PdfDocumentModel ImportSvg(string svgPath)
        => FromBytes(SvgToPdfConverter.Convert(File.ReadAllText(svgPath)), Path.GetFileNameWithoutExtension(svgPath) + ".pdf", "");

    public void ExtractImagesToFolder(PdfDocumentModel doc, string outputDir)
    { using var ms = new MemoryStream(doc.ShownBytes); Tetitulz.Pdf.Base.Images.PdfImageExtractor.ExtractImages(ms, outputDir); }

    // ======== Metadata, bookmarks, attachments ========
    public Dictionary<string, string> GetMetadata(PdfDocumentModel doc)
    { using var ms = new MemoryStream(doc.ShownBytes); using var d = PdfDocument.Open(ms); var m = d.Metadata; return new() { ["Title"] = m?.Title ?? "", ["Author"] = m?.Author ?? "", ["Subject"] = m?.Subject ?? "", ["Creator"] = m?.Creator ?? "", ["Producer"] = m?.Producer ?? "" }; }

    // PageNumber is int? on purpose: null means the destination could not be resolved, which a 0
    // would make indistinguishable from the first page.
    public (string title, int? page)[] GetBookmarks(PdfDocumentModel doc)
    { using var ms = new MemoryStream(doc.ShownBytes); return PdfBookmarksManager.ReadBookmarks(ms).Select(b => (b.Title, b.PageNumber)).ToArray(); }

    public string[] GetAttachmentNames(PdfDocumentModel doc)
    { using var ms = new MemoryStream(doc.ShownBytes); return PdfAttachmentManager.ListAttachments(ms).Select(a => a.Filename).ToArray(); }
    public PdfDocumentModel AddAttachment(PdfDocumentModel doc, string name, byte[] data, string mime)
        => Pipe(doc, (i, o) => PdfAttachmentManager.AddAttachment(i, o, name, data, mime));

    // ======== Accessibility and validation ========
    public string AuditAccessibility(PdfDocumentModel doc)
    { using var ms = new MemoryStream(doc.ShownBytes); var r = Tetitulz.Pdf.Accessibility.PdfAccessibility.Audit(ms); return $"Errors: {r.ErrorCount}, Warnings: {r.WarningCount}\n" + string.Join("\n", r.Issues.Select(x => $"[{x.Severity}] {x.Message}")); }
    // The fixer's own report comes back with the document: its two pass-through exits (encrypted, no
    // catalog) copy the bytes and say why in Warnings.
    public (PdfDocumentModel Doc, AccessibilityFixResult Result) FixAccessibility(PdfDocumentModel doc)
        => Pipe(doc, (i, o) => PdfAccessibilityFixer.Fix(i, o));
    public string ValidatePdfA(PdfDocumentModel doc)
    { using var ms = new MemoryStream(doc.ShownBytes); var r = PdfAValidator.Validate(ms); return r.Violations.Count == 0 ? "Valid PDF/A" : $"Invalid: {string.Join("; ", r.Violations.Take(5).Select(v => v.Message))}"; }
    public string ValidatePdfX(PdfDocumentModel doc)
    { using var ms = new MemoryStream(doc.ShownBytes); var r = PdfXValidator.Validate(ms); return r.Violations.Count == 0 ? "Valid PDF/X" : $"Invalid: {string.Join("; ", r.Violations.Take(5).Select(v => v.Message))}"; }
    public PdfDocumentModel ConvertToPdfA(PdfDocumentModel doc) => Pipe(doc, (i, o) => PdfAConverter.ConvertToPdfA1b(i, o));

    public string Compare(PdfDocumentModel d1, PdfDocumentModel d2)
    { using var m1 = new MemoryStream(d1.ShownBytes); using var m2 = new MemoryStream(d2.ShownBytes); var r = PdfComparer.CompareText(m1, m2); return r.AreTextEqual ? "The two documents' text is the same" : $"{r.TextDiffs.Count} text difference(s)"; }

    // ======== OCR ========
    // OCR answers null when it could not run (no tessdata folder, an unusable page) and puts the cause
    // on LastError; an empty string would read as a page with no text.
    private static string OcrOrReason(string? text) =>
        text ?? $"OCR did not run: {PdfOcrEngine.LastError ?? "no reason was reported."}";
    public string OcrPage(PdfDocumentModel doc, int page) => OcrOrReason(PdfOcrEngine.OcrPage(doc.ShownBytes, page));
    public string OcrDocument(PdfDocumentModel doc) => OcrOrReason(PdfOcrEngine.OcrDocument(doc.ShownBytes));

    // ======== Opening ========

    /// <summary>
    /// True when the file is encrypted. <see cref="PdfDocument.Open(Stream)"/> does not throw on an
    /// encrypted file; it sets <see cref="PdfDocument.IsEncrypted"/>.
    /// </summary>
    public static bool IsEncrypted(string path)
    {
        try { using var fs = File.OpenRead(path); return ProtectedDocuments.IsEncrypted(fs); }
        catch (IOException) { return false; }
    }

    // ======== Helpers ========
    private delegate void Op(Stream i, Stream o);
    private static PdfDocumentModel Pipe(PdfDocumentModel doc, Op op)
    { using var i = new MemoryStream(doc.Bytes); using var o = new MemoryStream(); op(i, o); return FromBytes(o.ToArray(), doc.FileName, doc.FilePath); }

    // Result-carrying sibling of Pipe: returns the operation's own report alongside the document.
    private static (PdfDocumentModel Doc, T Result) Pipe<T>(PdfDocumentModel doc, Func<Stream, Stream, T> op)
    { using var i = new MemoryStream(doc.Bytes); using var o = new MemoryStream(); T result = op(i, o); return (FromBytes(o.ToArray(), doc.FileName, doc.FilePath), result); }

    public static PdfDocumentModel OpenFromBytes(byte[] bytes, string name, string path = "")
        => FromBytes(bytes, name, path);

    private static PdfDocumentModel FromBytes(byte[] bytes, string name, string path)
        => PdfDocumentModel.FromBytes(bytes, name, path);
}
