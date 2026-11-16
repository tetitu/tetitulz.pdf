// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The name, relationship and size an attachment is reported with come from its file specification
/// the way ISO 32000-2 Table 43 says, whichever way each entry is spelled.
/// </summary>
/// <remarks>
/// <para><b>"A PDF reader shall use the value of the UF key, when present, instead of the F key."</b>
/// The two readers of a file specification's name disagreed about that: the wrapper reader took
/// <c>/UF</c> first and the attachment manager's annotation walk took <c>/F</c> first — so an
/// attachment whose real name is Hebrew was listed under its byte-string fallback, the name no
/// viewer shows. Both also read raw, as did the listing's <c>/AFRelationship</c> and
/// <c>/Params /Size</c>.</para>
///
/// <para>Object 5 is a file-attachment annotation, 6 its file specification, 7 the embedded file
/// stream (five bytes, declaring an original size of 42), and 8–10 whatever a case references.</para>
/// </remarks>
public class AttachmentFileSpecNameTests
{
    private const string HebrewName = "קובץ.txt";
    private const string HebrewNameHex = "<FEFF05E705D505D105E5002E007400780074>";

    [Fact]
    public void AnAnnotationAttachmentIsListedByItsUnicodeNameNotItsByteStringFallback()
    {
        var attachment = Assert.Single(PdfOperations.ListAttachments(new MemoryStream(
            Pdf(fileSpecEntries: "/F (kovetz.txt) /UF " + HebrewNameHex))));

        Assert.Equal(HebrewName, attachment.Filename);
    }

    [Theory]
    [InlineData(false)]   // /UF (report.txt)
    [InlineData(true)]    // /UF 8 0 R
    public void AnAttachmentsNameIsReadWhicheverWayItIsSpelled(bool indirect)
    {
        var attachment = Assert.Single(PdfOperations.ListAttachments(new MemoryStream(
            Pdf(fileSpecEntries: "/UF " + (indirect ? "8 0 R" : "(report.txt)")))));

        Assert.Equal("report.txt", attachment.Filename);
    }

    /// <summary>
    /// The declared size is 42 and the stored stream five bytes, so reading <c>/Size</c> as absent —
    /// which falls back to the stored length — cannot coincide with reading it.
    /// </summary>
    [Theory]
    [InlineData(false)]   // /AFRelationship /Data, /Params << /Size 42 >>
    [InlineData(true)]    // /AFRelationship 9 0 R, /Params << /Size 10 0 R >>
    public void AnAttachmentsRelationshipAndSizeAreReadWhicheverWayTheyAreSpelled(bool indirect)
    {
        var attachment = Assert.Single(PdfOperations.ListAttachments(new MemoryStream(Pdf(
            fileSpecEntries: "/UF (report.txt) /AFRelationship " + (indirect ? "9 0 R" : "/Data"),
            size: indirect ? "10 0 R" : "42"))));

        Assert.Equal("Data", attachment.AFRelationship);
        Assert.Equal(42, attachment.SizeBytes);
    }

    /// <summary>
    /// An unencrypted wrapper's payload is found by its <c>/AFRelationship</c>, and named by its
    /// <c>/UF</c>, whichever way each is spelled.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWrappersPayloadIsFoundAndNamedWhicheverWayItsEntriesAreSpelled(bool indirect)
    {
        byte[] pdf = Pdf(
            fileSpecEntries: "/UF " + (indirect ? "8 0 R" : "(report.txt)")
                + " /AFRelationship " + (indirect ? "11 0 R" : "/EncryptedPayload"),
            catalogExtra: "/AF [6 0 R] /Collection << /View /H >>");

        var info = PdfUnencryptedWrapperReader.Inspect(new MemoryStream(pdf));

        Assert.True(info.IsWrapper);
        Assert.Equal("report.txt", info.PayloadFilename);
    }

    // ------------------------------------------------------------------

    private static byte[] Pdf(string fileSpecEntries, string size = "42", string catalogExtra = "")
        => new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + " >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R /Annots [5 0 R] >>")
            .StreamObj(4, "<<", "")
            .Obj(5, "<< /Type /Annot /Subtype /FileAttachment /Rect [10 10 30 30] /FS 6 0 R >>")
            .Obj(6, "<< /Type /Filespec " + fileSpecEntries + " /EF << /F 7 0 R >> >>")
            .StreamObj(7, "<< /Type /EmbeddedFile /Params << /Size " + size + " >>", "hello")
            .Obj(8, "(report.txt)")
            .Obj(9, "/Data")
            .Obj(10, "42")
            .Obj(11, "/EncryptedPayload")
            .Finish();
}
