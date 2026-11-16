// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Attachment findings from the assembly scan: the MIME type written as a non-conformant PDF
/// name, and <c>ListAttachments</c> reporting attachments the other two entry points cannot see.
/// </summary>
public class AttachmentFindingsTests
{
    // ==================================================================
    //  The /Subtype media type is a PDF name, escaped by the writer
    // ==================================================================

    [Fact]
    public void AddAttachment_MimeType_SurvivesAsTheRealMediaType()
    {
        // '#' introduces a two-hex-digit escape (§7.3.5), so replacing '/' with '#' produced the
        // name value `application#pdf`, which PdfWriter then correctly escaped to
        // `/application#23pdf` — decoding back to the literal text "application#pdf". A common viewer
        // showed the wrong media type and a PDF/A-3 validator failed on it.
        byte[] pdf = AddAttachment("report.pdf", "application/pdf");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfStream embedded = FindEmbeddedFileStream(doc);
        Assert.Equal("application/pdf", (embedded[PdfName.Subtype] as PdfName)?.Value);
    }

    [Fact]
    public void AddAttachment_MimeType_IsEscapedInTheFileBytesAndNotWrittenRaw()
    {
        // A '/' inside a name must reach the file as #2F; a raw '/' would end the name token.
        byte[] pdf = AddAttachment("report.pdf", "application/pdf");
        string text = Encoding.ASCII.GetString(pdf);

        Assert.Contains("/application#2Fpdf", text, StringComparison.Ordinal);
        Assert.DoesNotContain("application#23pdf", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAttachment_Params_IncludesModDate()
    {
        // PDF Association AN002 (Associated Files): /Params should carry the modification date
        // so an AF-aware consumer can tell a stale embedded copy from a fresh one without
        // re-hashing the bytes.
        var before = DateTime.UtcNow.AddSeconds(-2);
        byte[] pdf = AddAttachment("report.pdf", "application/pdf");
        var after = DateTime.UtcNow.AddSeconds(2);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfStream embedded = FindEmbeddedFileStream(doc);
        var paramsDict = doc.ResolveObject(embedded[PdfName.Params]) as PdfDictionary;
        Assert.NotNull(paramsDict);

        string modDate = (paramsDict[PdfName.ModDate] as PdfString)?.GetText();
        Assert.NotNull(modDate);
        var parsed = Tetitulz.Pdf.Core.Util.PdfDate.TryParse(modDate);
        Assert.NotNull(parsed);
        Assert.InRange(parsed.Value.UtcDateTime, before, after);
    }

    [Fact]
    public void ListAttachments_ReportsTheMimeTypeThatWasWritten()
    {
        // The read path looked for /Subtype on the *filespec*, while AddAttachment writes it on
        // the /EmbeddedFile stream (Table 45) — so this library never reported a media type it
        // had written itself, and the round trip only looked correct because both halves were
        // silent.
        using var input = new MemoryStream(AddAttachment("report.pdf", "application/pdf"));
        var listed = PdfAttachmentManager.ListAttachments(input);

        var entry = Assert.Single(listed);
        Assert.Equal("report.pdf", entry.Filename);
        Assert.Equal("application/pdf", entry.MimeType);
    }

    private static byte[] AddAttachment(string filename, string mimeType)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(input, output, filename,
            Encoding.ASCII.GetBytes("payload"), mimeType);
        return output.ToArray();
    }

    private static PdfStream FindEmbeddedFileStream(PdfDocument doc)
    {
        var catalog = doc.ResolveObject(doc.Trailer[PdfName.Root]) as PdfDictionary;
        var names = doc.ResolveObject(catalog[PdfName.Names]) as PdfDictionary;
        var efNode = doc.ResolveObject(names[PdfName.EmbeddedFiles]) as PdfDictionary;
        var pairs = doc.ResolveObject(efNode[PdfName.Names]) as PdfArray;
        var filespec = doc.ResolveObject(pairs[1]) as PdfDictionary;
        var ef = doc.ResolveObject(filespec[PdfName.EF]) as PdfDictionary;
        return doc.ResolveObject(ef[PdfName.F]) as PdfStream;
    }

    // ==================================================================
    //  List / Extract / Remove agree on what an attachment is
    // ==================================================================

    private const string AnnotationAttachmentName = "note.txt";
    private static readonly byte[] AnnotationPayload = Encoding.ASCII.GetBytes("comment attachment");

    /// <summary>
    /// A one-page document whose only attachment is a <c>/FileAttachment</c> annotation — the
    /// normal shape for a comment attachment, and one the <c>/EmbeddedFiles</c> name tree does
    /// not mention at all.
    /// </summary>
    private static byte[] AnnotationOnlyAttachmentPdf()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[7];

        offsets[0] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[1] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[2] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                  + "/Contents 4 0 R /Annots [5 0 R] /Resources << >> >>\nendobj\n");

        const string cs = "q Q";
        offsets[3] = sb.Length;
        sb.AppendFormat(ci, "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
                        cs.Length, cs);

        offsets[4] = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] "
                  + "/FS 6 0 R >>\nendobj\n");
        offsets[5] = sb.Length;
        sb.AppendFormat(ci,
            "6 0 obj\n<< /Type /Filespec /F ({0}) /UF ({0}) /EF << /F 7 0 R >> >>\nendobj\n",
            AnnotationAttachmentName);

        string payload = Encoding.ASCII.GetString(AnnotationPayload);
        offsets[6] = sb.Length;
        sb.AppendFormat(ci,
            "7 0 obj\n<< /Type /EmbeddedFile /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            payload.Length, payload);

        int xref = sb.Length;
        sb.AppendFormat(ci, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Length + 1);
        foreach (int o in offsets) sb.AppendFormat(ci, "{0:D10} 00000 n \n", o);
        sb.AppendFormat(ci, "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
                        offsets.Length + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void ExtractAttachment_FindsAnAttachmentThatListAttachmentsReports()
    {
        // The obvious loop — foreach (var a in ListAttachments(s)) Save(ExtractAttachment(s,
        // a.Filename)) — returned null for a comment attachment, which is the same value
        // "no such attachment" returns.
        byte[] pdf = AnnotationOnlyAttachmentPdf();

        using var listStream = new MemoryStream(pdf);
        var listed = PdfAttachmentManager.ListAttachments(listStream);
        var entry = Assert.Single(listed);
        Assert.Equal(AnnotationAttachmentName, entry.Filename);

        using var extractStream = new MemoryStream(pdf);
        byte[] data = PdfAttachmentManager.ExtractAttachment(extractStream, entry.Filename);
        Assert.Equal(AnnotationPayload, data);
    }

    [Fact]
    public void RemoveAttachment_AnnotationOnlyAttachment_SaysWhyItRemovedNothing()
    {
        // RemoveAttachment returns void and rewrites only the name tree, so without a diagnostic
        // "there is no such attachment" and "it is an annotation and was left alone" are the same
        // observable: an output byte-identical to the input.
        byte[] pdf = AnnotationOnlyAttachmentPdf();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfAttachmentManager.RemoveAttachment(input, output, AnnotationAttachmentName);

        Assert.Equal(pdf, output.ToArray());
        Assert.NotNull(PdfAttachmentManager.LastRemoveDiagnostic);
        Assert.Contains("annotation", PdfAttachmentManager.LastRemoveDiagnostic,
                        StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveAttachment_LastRemoveDiagnostic_IsClearedAtTheStartOfEveryCall()
    {
        using (var input = new MemoryStream(AnnotationOnlyAttachmentPdf()))
        using (var output = new MemoryStream())
            PdfAttachmentManager.RemoveAttachment(input, output, AnnotationAttachmentName);
        Assert.NotNull(PdfAttachmentManager.LastRemoveDiagnostic);

        using (var input = new MemoryStream(AddAttachment("report.pdf", null)))
        using (var output = new MemoryStream())
            PdfAttachmentManager.RemoveAttachment(input, output, "report.pdf");
        Assert.Null(PdfAttachmentManager.LastRemoveDiagnostic);
    }

    [Fact]
    public void ListAttachments_FilespecReachableBothWays_IsReportedOnce()
    {
        // A common viewer writes a comment attachment's filespec into the name tree AND the annotation.
        // Enumerating both without de-duplicating listed the same file twice.
        byte[] pdf = AnnotationOnlyAttachmentPdf();
        using var withName = new MemoryStream(pdf);
        using var output = new MemoryStream();

        // Adding the same filespec object to the name tree is what AddAttachment does for a new
        // file; here the annotation already points at object 6, so use a second attachment with
        // the same display name to prove de-duplication is by filespec object, not by name.
        PdfAttachmentManager.AddAttachment(withName, output, AnnotationAttachmentName,
            Encoding.ASCII.GetBytes("a different file"));

        using var reread = new MemoryStream(output.ToArray());
        var listed = PdfAttachmentManager.ListAttachments(reread);

        // Two distinct filespec objects sharing one display name are two attachments.
        Assert.Equal(2, listed.Count);
        Assert.All(listed, a => Assert.Equal(AnnotationAttachmentName, a.Filename));
    }
}
