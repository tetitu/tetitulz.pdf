// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The shapes the specification writes, read by the <c>Operations/Metadata</c> readers that were
/// built against a different one — and the two writers there that wrote clear text into encrypted
/// files.
/// </summary>
public class MetadataSpecShapeTests
{
    // ================================================================== usage rights

    /// <summary>
    /// §12.8.1: a usage rights signature's dictionary "shall be referenced from the UR3 (PDF 1.6)
    /// entry in the permissions dictionary … (not from a signature field)". That is what a
    /// Reader-extended document carries, and the reader walked the form's fields alone — an empty
    /// list for every one of them.
    /// </summary>
    [Fact]
    public void AUsageRightsSignatureInPermsAndInNoFieldIsRead()
    {
        byte[] pdf = UsageRightsPdf(
            permsUr3: "5 0 R",
            signature: "<< /Type /Sig /Name (Adobe) /Reference [<< /Type /SigRef /TransformMethod /UR3 " +
                       "/DigestMethod /SHA1 /TransformParams << /Type /TransformParams /V /2.2 " +
                       "/Document [/FullSave] /Form [/FillIn /Import] >> >>] >>");

        var entry = Assert.Single(PdfUsageRightsReader.Read(new MemoryStream(pdf)));

        Assert.Equal(UsageRights.DocumentSave | UsageRights.FormFillIn | UsageRights.FormImportExport,
            entry.Rights);
        Assert.Equal("Adobe", entry.SignerName);
        Assert.Equal("SHA1", entry.DigestMethod);
    }

    /// <summary>
    /// Table 256 names the method <c>/UR</c>; a common viewer writes <c>/UR3</c>. Both are the usage-rights
    /// transform, and a <c>/DocMDP</c> reference in the same place is not.
    /// </summary>
    [Theory]
    [InlineData("UR", true)]
    [InlineData("UR3", true)]
    [InlineData("DocMDP", false)]
    public void EitherSpellingOfTheMethodIsTheUsageRightsTransform(string method, bool isUsageRights)
    {
        byte[] pdf = UsageRightsPdf(
            permsUr3: "5 0 R",
            signature: $"<< /Type /Sig /Reference [<< /TransformMethod /{method} " +
                       "/TransformParams << /Document [/FullSave] >> >>] >>");

        var entries = PdfUsageRightsReader.Read(new MemoryStream(pdf));

        Assert.Equal(isUsageRights ? 1 : 0, entries.Count);
        Assert.Null(PdfUsageRightsReader.LastError);
    }

    /// <summary>
    /// A lone reference dictionary where Table 255 says an array — the shape
    /// <c>DocMdpAnalyzer.SignatureReferences</c> accepts, and this reader did not.
    /// </summary>
    [Fact]
    public void ALoneReferenceDictionaryIsRead()
    {
        byte[] pdf = UsageRightsPdf(
            permsUr3: "5 0 R",
            signature: "<< /Type /Sig /Reference << /TransformMethod /UR3 " +
                       "/TransformParams << /Annots [/Create] >> >> >>");

        var entry = Assert.Single(PdfUsageRightsReader.Read(new MemoryStream(pdf)));
        Assert.Equal(UsageRights.Annotation, entry.Rights);
    }

    /// <summary>The rights names are array elements, and any of them may be indirect (§7.3.10).</summary>
    [Fact]
    public void IndirectRightsNamesAreRead()
    {
        byte[] pdf = UsageRightsPdf(
            permsUr3: "5 0 R",
            signature: "<< /Type /Sig /Reference [<< /TransformMethod /UR3 " +
                       "/TransformParams << /Form [6 0 R] /Document [7 0 R] >> >>] >>",
            extraObjects: new[] { (6, "/SubmitStandalone"), (7, "/FullSave") });

        var entry = Assert.Single(PdfUsageRightsReader.Read(new MemoryStream(pdf)));
        Assert.Equal(UsageRights.FormSave | UsageRights.DocumentSave, entry.Rights);
    }

    /// <summary>
    /// One signature reachable from <c>/Perms</c> and from a field — the shape this library's own
    /// signer would write if it wrote both — is one signature.
    /// </summary>
    [Fact]
    public void ASignatureReachableFromPermsAndAFieldIsReportedOnce()
    {
        byte[] pdf = UsageRightsPdf(
            permsUr3: "5 0 R",
            signature: "<< /Type /Sig /Reference [<< /TransformMethod /UR3 " +
                       "/TransformParams << /Form [/FillIn] >> >>] >>",
            fieldValue: "5 0 R");

        var entry = Assert.Single(PdfUsageRightsReader.Read(new MemoryStream(pdf)));
        Assert.Equal(UsageRights.FormFillIn, entry.Rights);
    }

    /// <summary>The control for the dedup: two different signatures are two entries.</summary>
    [Fact]
    public void ASecondSignatureInAFieldIsASecondEntry()
    {
        byte[] pdf = UsageRightsPdf(
            permsUr3: "5 0 R",
            signature: "<< /Type /Sig /Reference [<< /TransformMethod /UR3 " +
                       "/TransformParams << /Form [/FillIn] >> >>] >>",
            fieldValue: "6 0 R",
            extraObjects: new[] { (6, "<< /Type /Sig /Reference [<< /TransformMethod /UR3 " +
                                      "/TransformParams << /Document [/FullSave] >> >>] >>") });

        var entries = PdfUsageRightsReader.Read(new MemoryStream(pdf));

        Assert.Equal(2, entries.Count);
        Assert.Equal(UsageRights.FormFillIn, entries[0].Rights);
        Assert.Equal(UsageRights.DocumentSave, entries[1].Rights);
    }

    /// <summary>
    /// A catalog with /Perms, a usage-rights signature as object 5, and optionally a signature
    /// field (object 4) whose /V is <paramref name="fieldValue"/>.
    /// </summary>
    private static byte[] UsageRightsPdf(
        string permsUr3, string signature, string? fieldValue = null,
        (int Num, string Body)[]? extraObjects = null)
    {
        string acroForm = fieldValue == null ? "" : "/AcroForm << /Fields [4 0 R] >> ";
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, $"<< /Type /Catalog /Pages 2 0 R /Perms << /UR3 {permsUr3} >> {acroForm}>>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .Obj(4, fieldValue == null ? "<< >>" : $"<< /FT /Sig /T (UR) /V {fieldValue} >>")
            .Obj(5, signature);
        foreach (var (num, body) in extraObjects ?? Array.Empty<(int, string)>())
            b.Obj(num, body);
        return b.Finish();
    }

    // ================================================================== unencrypted wrapper

    /// <summary>
    /// §7.6.7: the payload is the file specification marked <c>/AFRelationship
    /// /EncryptedPayload</c>, and its <c>/EP /Subtype</c> names the filter that opens it.
    /// </summary>
    [Fact]
    public void AWrapperReportsItsPayloadsCryptographicFilter()
    {
        byte[] pdf = WrapperPdf(
            catalogExtras: "/AF [5 0 R] /Collection << /View /H >>",
            fileSpec: "<< /Type /Filespec /UF (secret.pdf) /AFRelationship /EncryptedPayload " +
                      "/EP << /Type /EncryptedPayload /Subtype /AcmeCustomCrypto /Version /1.0 >> >>");

        var info = PdfUnencryptedWrapperReader.Inspect(new MemoryStream(pdf));

        Assert.True(info.IsWrapper);
        Assert.Equal("secret.pdf", info.PayloadFilename);
        Assert.Equal("AcmeCustomCrypto", info.Type);
    }

    /// <summary>
    /// A portfolio is a collection, and a wrapper is a collection too — but a collection with no
    /// encrypted payload is not a wrapper. This reported every portfolio as one, typed "Collection".
    /// </summary>
    [Fact]
    public void APortfolioWithNoEncryptedPayloadIsNotAWrapper()
    {
        byte[] pdf = WrapperPdf(
            catalogExtras: "/AF [5 0 R] /Collection << /View /D >>",
            fileSpec: "<< /Type /Filespec /UF (report.pdf) /AFRelationship /Data >>");

        var info = PdfUnencryptedWrapperReader.Inspect(new MemoryStream(pdf));

        Assert.False(info.IsWrapper);
        Assert.Null(info.Type);
    }

    /// <summary>
    /// §7.6.7 puts the payload in the <c>/EmbeddedFiles</c> tree as well as <c>/AF</c>; a producer
    /// that wrote only the tree still made a wrapper.
    /// </summary>
    [Fact]
    public void APayloadOnlyInTheEmbeddedFilesTreeIsFound()
    {
        byte[] pdf = WrapperPdf(
            catalogExtras: "/Names << /EmbeddedFiles << /Names [(secret.pdf) 5 0 R] >> >>",
            fileSpec: "<< /Type /Filespec /UF (secret.pdf) /AFRelationship /EncryptedPayload " +
                      "/EP << /Subtype /AcmeCustomCrypto >> >>");

        var info = PdfUnencryptedWrapperReader.Inspect(new MemoryStream(pdf));

        Assert.True(info.IsWrapper);
        Assert.Equal("AcmeCustomCrypto", info.Type);
    }

    /// <summary>A payload with no encrypted payload dictionary is a wrapper that does not say how to open it.</summary>
    [Fact]
    public void APayloadWithoutAnEncryptedPayloadDictionaryHasNoFilter()
    {
        byte[] pdf = WrapperPdf(
            catalogExtras: "/AF [5 0 R]",
            fileSpec: "<< /Type /Filespec /UF (secret.pdf) /AFRelationship /EncryptedPayload >>");

        var info = PdfUnencryptedWrapperReader.Inspect(new MemoryStream(pdf));

        Assert.True(info.IsWrapper);
        Assert.Null(info.Type);
    }

    private static byte[] WrapperPdf(string catalogExtras, string fileSpec) =>
        new PdfFixtureBuilder().Header()
            .Obj(1, $"<< /Type /Catalog /Pages 2 0 R {catalogExtras} >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .Obj(5, fileSpec)
            .Finish();

    // ================================================================== viewports

    /// <summary>
    /// The last unresolved read in the viewport walk: an indirect <c>/BBox</c> coordinate read as 0,
    /// moving the viewport's corner to the page origin.
    /// </summary>
    [Fact]
    public void AnIndirectViewportCoordinateIsRead()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    "/VP [<< /Type /Viewport /BBox [4 0 R 20 300 400] >>] >>")
            .Obj(4, "10")
            .Finish();

        var viewport = Assert.Single(PdfGeoReader.ReadViewports(new MemoryStream(pdf)));
        Assert.Equal(new[] { 10f, 20f, 300f, 400f }, viewport.Bbox);
    }

    // ================================================================== ICC output intent

    /// <summary>
    /// Bytes that are not an ICC profile are refused. The embedder read byte 16 itself and answered
    /// "three components" for anything it did not recognise, so a PNG became an RGB output intent.
    /// </summary>
    [Fact]
    public void EmbedProfile_RefusesBytesThatAreNotAnIccProfile()
    {
        byte[] notAProfile = new byte[256];
        Encoding.ASCII.GetBytes("RGB ").CopyTo(notAProfile, 16);   // no 'acsp' signature

        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(() => PdfIccEmbedder.EmbedProfile(
            new MemoryStream(OnePagePdf()), output, notAProfile));

        Assert.Equal("iccBytes", ex.ParamName);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The control, and the arity: <c>/N</c> is the profile's own component count, which the old
    /// reader collapsed to 3 for everything but grey and CMYK.
    /// </summary>
    [Theory]
    [InlineData("GRAY", 1)]
    [InlineData("CMYK", 4)]
    [InlineData("5CLR", 5)]
    public void EmbedProfile_WritesTheProfilesOwnComponentCount(string space, int components)
    {
        var profile = new byte[132];
        Encoding.ASCII.GetBytes(space).CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);

        using var output = new MemoryStream();
        PdfIccEmbedder.EmbedProfile(new MemoryStream(OnePagePdf()), output, profile);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var intents = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OutputIntents]));
        var intent = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(Assert.Single(intents)));
        var stream = Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(intent[PdfName.DestOutputProfile]));
        Assert.Equal(components, (doc.ResolveObject(stream[PdfName.N]) as PdfNumber)?.IntValue);
    }

    // ================================================================== metadata writers

    /// <summary>
    /// <c>WriteInfo</c> appended clear-text strings to an encrypted document, whose reader decrypts
    /// them into noise, and returned normally.
    /// </summary>
    [Fact]
    public void WriteInfo_EncryptedDocument_FailsClosed()
    {
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(() => PdfMetadataWriter.WriteInfo(
            new MemoryStream(EncryptedPdf()), output, new PdfMetadataUpdate { Title = "Report" }));

        Assert.Contains("/Info", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// <c>WriteXmpMetadata</c> threw "PDF has no catalog dictionary" — true only because an
    /// encrypted document loads none, and nothing a caller could act on.
    /// </summary>
    [Fact]
    public void WriteXmpMetadata_EncryptedDocument_FailsClosedWithTheReason()
    {
        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(() => PdfMetadataWriter.WriteXmpMetadata(
            new MemoryStream(EncryptedPdf()), output, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));

        Assert.Contains("/Metadata", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The acceptance control: the same writes on the unencrypted document succeed.</summary>
    [Fact]
    public void BothMetadataWritersAcceptTheUnencryptedDocument()
    {
        using var info = new MemoryStream();
        PdfMetadataWriter.WriteInfo(new MemoryStream(OnePagePdf()), info, new PdfMetadataUpdate { Title = "Report" });
        using (var doc = PdfDocument.Open(new MemoryStream(info.ToArray())))
            Assert.Equal("Report", doc.Metadata.Title);

        using var xmp = new MemoryStream();
        PdfMetadataWriter.WriteXmpMetadata(new MemoryStream(OnePagePdf()), xmp, "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
        using (var doc = PdfDocument.Open(new MemoryStream(xmp.ToArray())))
            Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata]));
    }

    private static byte[] OnePagePdf() =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .Finish();

    private static byte[] EncryptedPdf() =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
            .Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                    "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                    "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>")
            .Obj(5, "<< /Title (Old) >>")
            .Finish("/Encrypt 4 0 R /Info 5 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>] ");
}
