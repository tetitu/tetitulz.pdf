// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Base.Tests.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// The read doors that take no password refuse an encrypted document. <c>PdfDocument.Open</c>
/// decrypts nothing and loads no catalog for one, so each found no fields, bookmarks, labels,
/// layers, links, annotations or structure and said so — "none" for a document that may hold
/// plenty, most with no diagnostic. The usage-rights reader, whose failures all go to its
/// <c>LastError</c>, says it there.
/// </summary>
public sealed class EncryptedReadDoorRefusalTests
{
    public static TheoryData<string> Doors() => new()
    {
        "fields", "fields-path", "xfa", "xfa-values", "export", "structure", "bookmarks", "outline-json",
        "labels", "layers", "view", "links", "links-path", "annotations", "annotations-path", "tagged",
        "tagged-text", "destinations",
    };

    [Theory]
    [MemberData(nameof(Doors))]
    public void Adversarial_encrypted_document_is_refused_by_every_read_door(string door)
    {
        var ex = Assert.Throws<PdfEncryptedDocumentException>(() => Read(door, Pdf(encrypted: true)));
        Assert.Contains("without its password", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: the same document without <c>/Encrypt</c> is read by every door, and its bookmark found.</summary>
    [Theory]
    [MemberData(nameof(Doors))]
    public void The_same_document_unencrypted_is_read(string door)
    {
        string expected = door switch
        {
            "xfa" or "tagged" => "False",
            "xfa-values" => "null",
            "bookmarks" => "1",
            "outline-json" => "True",
            "view" => "none",
            "structure" => "read",
            _ => "0",
        };
        Assert.Equal(expected, Read(door, Pdf(encrypted: false)));
    }

    [Fact]
    public void Adversarial_encrypted_document_usage_rights_are_said_to_be_unread()
    {
        Assert.Empty(PdfUsageRightsReader.Read(new MemoryStream(Pdf(encrypted: true))));
        Assert.Contains("without its password", PdfUsageRightsReader.LastError, StringComparison.Ordinal);

        Assert.Empty(PdfUsageRightsReader.Read(new MemoryStream(Pdf(encrypted: false))));
        Assert.Null(PdfUsageRightsReader.LastError);
    }

    // What the door answered, in a word: a count, a flag, or a mark that it read.
    private static string Read(string door, byte[] pdf)
    {
        Stream S() => new MemoryStream(pdf);
        return door switch
        {
            "fields" => PdfFormReader.GetFields(S()).Count.ToString(),
            "fields-path" => WithFile(pdf, p => PdfFormReader.GetFields(p).Count.ToString()),
            "xfa" => PdfFormReader.HasXfa(S()).ToString(),
            "xfa-values" => PdfFormReader.ReadXfaValues(S()) == null ? "null" : "values",
            "export" => PdfFormDataExchange.Export(S()).Fields.Count.ToString(),
            "structure" => PdfStructureTreeReader.ReadStructureTree(S()) == null ? "null" : "read",
            "bookmarks" => PdfBookmarksManager.ReadBookmarks(S()).Count.ToString(),
            "outline-json" => PdfOutlineJson.Export(S()).Contains("One", StringComparison.Ordinal).ToString(),
            "labels" => PdfPageLabels.GetLabels(S()).Count.ToString(),
            "layers" => PdfOcgManager.ListLayers(S()).Count.ToString(),
            "view" => PdfInitialView.GetInitialView(S()).PageMode ?? "none",
            "links" => PdfLinkExtractor.ExtractLinks(S()).Count.ToString(),
            "links-path" => WithFile(pdf, p => PdfLinkExtractor.ExtractLinks(p).Count.ToString()),
            "annotations" => PdfAnnotationExtractor.ListAnnotations(S()).Count.ToString(),
            "annotations-path" => WithFile(pdf, p => PdfAnnotationExtractor.ListAnnotations(p).Count.ToString()),
            "tagged" => PdfTaggedTextExtractor.IsTagged(S()).ToString(),
            "tagged-text" => PdfTaggedTextExtractor.Extract(S()).Count.ToString(),
            "destinations" => PdfNamedDestinations.ListDestinations(S()).Count.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(door)),
        };
    }

    private static string WithFile(byte[] pdf, Func<string, string> read)
    {
        string path = Path.Combine(Path.GetTempPath(), "enc_read_" + Path.GetRandomFileName() + ".pdf");
        File.WriteAllBytes(path, pdf);
        try { return read(path); }
        finally { File.Delete(path); }
    }

    // A one-page document with one bookmark; encrypted, its /Encrypt names the standard handler.
    private static byte[] Pdf(bool encrypted)
    {
        var builder = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
            .Obj(4, "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>")
            .Obj(5, "<< /Title (One) /Parent 4 0 R /Dest [3 0 R /Fit] >>")
            .Obj(6, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                    "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                    "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>");
        return encrypted
            ? builder.Finish(" /Encrypt 6 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]")
            : builder.Finish();
    }
}
