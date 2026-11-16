// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// A writer that appends clear-text objects to an encrypted document refuses it. The incremental
/// writer only reports that "the appended objects are written in the clear"; a reader then decrypts
/// those objects to garbage. Each of these writers is checked against a real encrypted file, and each
/// must refuse for that reason and write nothing. Before the refusal, rotate, insert, the page boxes
/// and the form fields failed with an <c>ArgumentOutOfRangeException</c> (an encrypted document
/// loads no pages), the page labels with a <c>NullReferenceException</c>, and delete, the initial
/// view and the bookmarks refused for a reason that was not the real one.
/// </summary>
public sealed class EncryptedRevisionWriterRefusalTests
{
    public static IEnumerable<object[]> Writers => new[]
    {
        new object[] { "rotate" }, new object[] { "delete" }, new object[] { "insert" },
        new object[] { "crop box" }, new object[] { "initial view" }, new object[] { "page labels" },
        new object[] { "form field" }, new object[] { "bookmarks" },
    };

    [Theory]
    [MemberData(nameof(Writers))]
    public void Adversarial_encrypted_input_is_refused_by_every_revision_writer(string writer)
    {
        byte[] input = Encrypted(MinimalPdfBuilder.CreateMultiPagePdf(2));
        var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(() => Write(writer, input, output));
        Assert.Contains("encrypted document", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void A_plain_document_is_written_by_every_revision_writer(string writer)
    {
        byte[] input = MinimalPdfBuilder.CreateMultiPagePdf(2);
        var output = new MemoryStream();

        Write(writer, input, output);

        Assert.True(output.Length > input.Length, writer + " appended no revision");
        using var reopened = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.NotEmpty(reopened.Pages);
    }

    /// <summary>
    /// The shared revision tail refuses too, so a writer that opens and builds without a refusal
    /// of its own (the OCR layer, the Bates second route, two Conformance repairs) still writes nothing.
    /// </summary>
    [Fact]
    public void Adversarial_encrypted_input_is_refused_by_the_shared_revision_tail()
    {
        byte[] input = Encrypted(MinimalPdfBuilder.CreateMultiPagePdf(1));
        var source = new MemoryStream(input);
        var output = new MemoryStream();
        using var doc = PdfDocument.Open(source);
        int next = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;

        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfPageContentAppender.AppendRevision(source, output, doc, new List<IndirectObject>(), next));
        Assert.Equal(0, output.Length);
    }

    private static void Write(string writer, byte[] input, Stream output)
    {
        switch (writer)
        {
            case "rotate": PdfPageManipulator.RotatePage(new MemoryStream(input), output, 0, 90); break;
            case "delete": PdfPageManipulator.DeletePage(new MemoryStream(input), output, 0); break;
            case "insert": PdfPageManipulator.InsertBlankPage(new MemoryStream(input), output, 0, 200, 200); break;
            case "crop box":
                PdfPageManipulator.SetPageBox(new MemoryStream(input), output, 0, PdfPageBoxKind.CropBox, 0, 0, 100, 100);
                break;
            case "initial view":
                PdfInitialView.SetInitialView(new MemoryStream(input), output,
                    new InitialViewSettings { PageMode = "UseOutlines" });
                break;
            case "page labels":
                PdfPageLabels.SetLabels(new MemoryStream(input), output,
                    new[] { new PageLabelRange { StartPageIndex = 0, Style = "D" } });
                break;
            case "form field":
                PdfFormFieldBuilder.AddTextField(new MemoryStream(input), output,
                    new FormTextField { FieldName = "added", PageIndex = 0, X = 10, Y = 10, Width = 50, Height = 20 });
                break;
            case "bookmarks":
                PdfBookmarksManager.WriteBookmarks(new MemoryStream(input), output,
                    new[] { new PdfBookmark("One", 0, 0) });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(writer), writer, null);
        }
    }

    private static byte[] Encrypted(byte[] plain)
    {
        global::Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        global::Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(input, output, new Base.Encryption.EncryptionParameters
        {
            UserPassword = "",
            OwnerPassword = "owner",
            Algorithm = Base.Encryption.EncryptionAlgorithm.AES128,
            Permissions = Base.Encryption.PdfPermissions.All,
        });
        return output.ToArray();
    }
}
