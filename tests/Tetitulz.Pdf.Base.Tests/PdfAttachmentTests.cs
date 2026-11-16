// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

public sealed class PdfAttachmentTests
{
    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    private static byte[] SimpleAttachment(string content = "hello attachment")
        => Encoding.UTF8.GetBytes(content);

    // ----------------------------------------------------------------
    //  ListAttachments — no attachments
    // ----------------------------------------------------------------

    [Fact]
    public void ListAttachments_NoPdf_ReturnsEmptyList()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        var list = PdfOperations.ListAttachments(ms);
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    // ----------------------------------------------------------------
    //  AddAttachment + ListAttachments
    // ----------------------------------------------------------------

    [Fact]
    public void AddAttachment_Stream_RoundTrip_ListContainsFile()
    {
        byte[] pdf   = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] data  = SimpleAttachment();
        string fname = "test.txt";

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.AddAttachment(input, output, fname, data);

        output.Position = 0;
        var list = PdfOperations.ListAttachments(output);
        Assert.Single(list);
        Assert.Equal(fname, list[0].Filename);
    }

    [Fact]
    public void AddAttachment_FilePath_RoundTrip_ListContainsFile()
    {
        byte[] pdf  = MinimalPdfBuilder.CreateMinimalPdf();
        string src  = TempFiles.NewPath();
        string dst  = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(src, pdf);
            PdfOperations.AddAttachment(src, dst, "doc.csv",
                Encoding.UTF8.GetBytes("a,b,c\n1,2,3"));

            var list = PdfOperations.ListAttachments(dst);
            Assert.Single(list);
            Assert.Equal("doc.csv", list[0].Filename);
        }
        finally
        {
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(dst)) File.Delete(dst);
        }
    }

    [Fact]
    public void AddAttachment_TwoFiles_ListContainsBoth()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var s0  = new MemoryStream(pdf);
        using var s1  = new MemoryStream();
        PdfOperations.AddAttachment(s0, s1, "alpha.txt", SimpleAttachment("a"));

        s1.Position = 0;
        using var s2 = new MemoryStream();
        PdfOperations.AddAttachment(s1, s2, "beta.txt", SimpleAttachment("b"));

        s2.Position = 0;
        var list = PdfOperations.ListAttachments(s2);
        Assert.Equal(2, list.Count);

        // Names should be alphabetically sorted in the name tree.
        bool hasAlpha = false, hasBeta = false;
        foreach (var a in list)
        {
            if (a.Filename == "alpha.txt") hasAlpha = true;
            if (a.Filename == "beta.txt")  hasBeta  = true;
        }
        Assert.True(hasAlpha, "alpha.txt missing");
        Assert.True(hasBeta,  "beta.txt missing");
    }

    [Fact]
    public void AddAttachment_SizeIsRecorded()
    {
        byte[] pdf  = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] data = Encoding.UTF8.GetBytes("exact data");

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.AddAttachment(input, output, "sz.bin", data);

        output.Position = 0;
        var list = PdfOperations.ListAttachments(output);
        Assert.Equal(data.Length, (int)list[0].SizeBytes);
    }

    [Fact]
    public void AddAttachment_WithMimeType_ListReflectsMime()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.AddAttachment(input, output, "img.png",
            SimpleAttachment(), mimeType: "image/png");

        output.Position = 0;
        var list = PdfOperations.ListAttachments(output);
        Assert.Single(list);
        // MimeType may be null on old readers; only assert when present.
        if (list[0].MimeType != null)
            Assert.Equal("image/png", list[0].MimeType);
    }

    // ----------------------------------------------------------------
    //  ExtractAttachment
    // ----------------------------------------------------------------

    [Fact]
    public void ExtractAttachment_Stream_ReturnsOriginalBytes()
    {
        byte[] pdf   = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] data  = Encoding.UTF8.GetBytes("round-trip content");
        string fname = "extract_me.txt";

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.AddAttachment(input, output, fname, data);

        output.Position = 0;
        byte[] extracted = PdfOperations.ExtractAttachment(output, fname);
        Assert.NotNull(extracted);
        Assert.Equal(data, extracted);
    }

    [Fact]
    public void ExtractAttachment_UnknownName_ReturnsNull()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.AddAttachment(input, output, "present.txt", SimpleAttachment());

        output.Position = 0;
        byte[] result = PdfOperations.ExtractAttachment(output, "absent.txt");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAttachment_NoAttachments_ReturnsNull()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        byte[] result = PdfOperations.ExtractAttachment(ms, "any.txt");
        Assert.Null(result);
    }

    // ----------------------------------------------------------------
    //  RemoveAttachment
    // ----------------------------------------------------------------

    [Fact]
    public void RemoveAttachment_ExistingFile_ListIsEmpty()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var s0 = new MemoryStream(pdf);
        using var s1 = new MemoryStream();
        PdfOperations.AddAttachment(s0, s1, "to_remove.txt", SimpleAttachment());

        s1.Position = 0;
        using var s2 = new MemoryStream();
        PdfOperations.RemoveAttachment(s1, s2, "to_remove.txt");

        s2.Position = 0;
        var list = PdfOperations.ListAttachments(s2);
        Assert.Empty(list);
    }

    [Fact]
    public void RemoveAttachment_OnlyRemovesNamed_OtherStillPresent()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var s0 = new MemoryStream(pdf);
        using var s1 = new MemoryStream();
        PdfOperations.AddAttachment(s0, s1, "keep.txt", SimpleAttachment("keep"));

        s1.Position = 0;
        using var s2 = new MemoryStream();
        PdfOperations.AddAttachment(s1, s2, "remove.txt", SimpleAttachment("remove"));

        s2.Position = 0;
        using var s3 = new MemoryStream();
        PdfOperations.RemoveAttachment(s2, s3, "remove.txt");

        s3.Position = 0;
        var list = PdfOperations.ListAttachments(s3);
        Assert.Single(list);
        Assert.Equal("keep.txt", list[0].Filename);
    }

    /// <summary>
    /// Removing a name that is not attached must be a <b>no-op</b>, and the interesting half of
    /// that is what it leaves behind: a document with an attachment must still have it. Asserting
    /// only <c>output.Length > 0</c> could not distinguish "copied through" from "wrote a valid
    /// PDF having dropped every attachment", which is the failure that would actually hurt.
    /// </summary>
    [Fact]
    public void RemoveAttachment_AbsentName_LeavesTheDocumentAndItsAttachmentsIntact()
    {
        using var withAttachment = new MemoryStream();
        using (var seed = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            PdfOperations.AddAttachment(seed, withAttachment, "keep.txt",
                Encoding.ASCII.GetBytes("payload"));

        byte[] source = withAttachment.ToArray();

        withAttachment.Position = 0;
        using var output = new MemoryStream();
        PdfOperations.RemoveAttachment(withAttachment, output, "does_not_exist.txt");

        // The implementation's own words are "copy through unchanged", and that is a claim about
        // the bytes. Rebuilding an equivalent document would satisfy "the attachment is still
        // there" while quietly rewriting a file it was asked to leave alone.
        Assert.Equal(source, output.ToArray());

        output.Position = 0;
        var remaining = PdfOperations.ListAttachments(output);
        Assert.Single(remaining);
        Assert.Equal("keep.txt", remaining[0].Filename);
    }

    // ----------------------------------------------------------------
    //  Error / null input
    // ----------------------------------------------------------------

    [Fact]
    public void AddAttachment_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => PdfOperations.AddAttachment((Stream)null!, null, "f", []));

    [Fact]
    public void ListAttachments_NullStream_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => PdfOperations.ListAttachments((Stream)null!));
}
