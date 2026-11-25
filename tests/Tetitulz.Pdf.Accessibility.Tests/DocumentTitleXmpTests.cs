// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Every writer that sets a document title writes it to the XMP <c>dc:title</c> as well as to
/// <c>/Info /Title</c>: ISO 14289-1 7.1 reads the title from the catalog's Metadata stream alone,
/// a conforming reader ignoring the information dictionary. <c>SetDocumentTitle</c>, its plan for
/// the encrypted route, and the auto-tagger — each against a document with no title, and the
/// packet's other properties kept.
/// </summary>
public sealed class DocumentTitleXmpTests
{
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";
    private const string Creator = "Original Creator";

    public DocumentTitleXmpTests() => PdfEncryptionOperations.Register();

    /// <summary>
    /// The title lands in <c>/Info</c>, in <c>dc:title</c> beside the packet's own <c>dc:creator</c>,
    /// and <c>/DisplayDocTitle</c> — and no PDF/UA identifier is written, a declaration a title
    /// does not make.
    /// </summary>
    [Fact]
    public void SetDocumentTitle_WritesDcTitleIntoThePacket_KeepingItsOtherProperties()
    {
        using var input = new MemoryStream(Document(withPacket: true), writable: false);
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentTitle(input, output, "Annual Report"));

        (string info, string xmp, bool display) = Read(output.ToArray());
        Assert.Equal("Annual Report", info);
        Assert.Contains(">Annual Report</rdf:li>", xmp, System.StringComparison.Ordinal);
        Assert.Contains(Creator, xmp, System.StringComparison.Ordinal);
        Assert.DoesNotContain("pdfuaid", xmp, System.StringComparison.Ordinal);
        Assert.True(display);
    }

    /// <summary>
    /// The plan carries the same three edits through the encrypted writer: <c>/Info /Title</c>,
    /// <c>/DisplayDocTitle</c> and a new Metadata stream holding <c>dc:title</c>, referenced from the
    /// catalog.
    /// </summary>
    [Fact]
    public void PlanDocumentTitle_ThroughTheEncryptedWriter_WritesDcTitleAndDisplayDocTitle()
    {
        byte[] encrypted = Encrypt(Document(withPacket: false));

        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();
        PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword, (doc, firstFree) =>
        {
            StructureTreePlan plan = PdfStructureTreeEditor.PlanDocumentTitle(doc, firstFree, "Annual Report");
            return new EncryptedRevision(plan.Objects, plan.NewInfoObjectNumber);
        });

        (string info, string xmp, bool display) =
            Read(PdfDecryptedInput.ReadDecrypted(new MemoryStream(output.ToArray()), UserPassword));
        Assert.Equal("Annual Report", info);
        Assert.Contains(">Annual Report</rdf:li>", xmp, System.StringComparison.Ordinal);
        Assert.True(display);
    }

    /// <summary>
    /// The tagger's <see cref="AutoTagOptions.Title"/> reaches <c>dc:title</c>; with no title (the
    /// control) the tagger writes none there.
    /// </summary>
    [Fact]
    public void AutoTag_WithATitle_WritesDcTitle_WithoutOneWritesNone()
    {
        byte[] titled = AutoTag(new AutoTagOptions { Title = "Annual Report" });
        byte[] untitled = AutoTag(new AutoTagOptions());

        Assert.Contains(">Annual Report</rdf:li>", Read(titled).Xmp, System.StringComparison.Ordinal);
        Assert.Equal("Annual Report", Read(titled).Info);
        string untitledXmp = Read(untitled).Xmp;
        Assert.True(untitledXmp == null || !untitledXmp.Contains("dc:title", System.StringComparison.Ordinal));
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static byte[] AutoTag(AutoTagOptions options)
    {
        using var input = new MemoryStream(Document(withPacket: false), writable: false);
        using var output = new MemoryStream();
        PdfAccessibility.AutoTag(input, output, options);
        return output.ToArray();
    }

    /// <summary>One page of text; no /Info; a packet holding only dc:creator when asked for (object 6).</summary>
    private static byte[] Document(bool withPacket)
    {
        const string Content = "BT /F1 12 Tf 72 700 Td (Hello world.) Tj ET";
        const string Packet =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:creator><rdf:Seq><rdf:li>" + Creator + "</rdf:li></rdf:Seq></dc:creator>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + (withPacket ? " /Metadata 6 0 R" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + Content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + Content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (withPacket)
            bodies.Add("<< /Type /Metadata /Subtype /XML /Length " + Packet.Length.ToString(CultureInfo.InvariantCulture) +
                       " >>\nstream\n" + Packet + "\nendstream");
        return PdfUa2StructureCheckerTests.BuildPdf(bodies);
    }

    private static (string Info, string Xmp, bool Display) Read(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        PdfDictionary catalog = doc.Catalog.Dictionary;
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        string title = (doc.ResolveObject(info?[PdfName.Title]) as PdfString)?.GetText();
        var metadata = doc.ResolveObject(catalog[PdfName.Metadata]) as PdfStream;
        string xmp = metadata == null ? null : Encoding.UTF8.GetString(metadata.GetDecodedData());
        var prefs = doc.ResolveObject(catalog[PdfName.ViewerPreferences]) as PdfDictionary;
        bool display = doc.ResolveObject(prefs?[PdfName.DisplayDocTitle]) is PdfBoolean b && b.Value;
        return (title, xmp, display);
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var input  = new MemoryStream(plain, writable: false);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }
}
