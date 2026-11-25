// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// An encrypted document in the compressed layout. Writing every object back out individually under
/// a classic table would undo the optimizer's object streams in the round trip and grow the output
/// past its input, so the layout the input has is kept —
/// <see cref="EncryptionParameters.UseObjectStreams"/> overrides it either way — through
/// <c>PdfCompressedFileWriter</c>'s encryptor seam: the object streams encrypted whole, their members
/// in the clear inside, the <c>/Encrypt</c> dictionary and the cross-reference stream unencrypted.
/// </summary>
public class EncryptorObjectStreamLayoutTests
{
    private const string UserPassword = "user-pw";
    private const string Secret = "secret-string";
    private const string Attachment = "ATTACHMENT-BYTES-0123456789";
    private static readonly PdfName Marker = new PdfName("Marker");

    /// <summary>
    /// A compressed input is encrypted in the compressed layout: the output holds object streams and a
    /// cross-reference stream, no string of it is readable in the file, and decrypting with the password
    /// reads the packed dictionary's string and the page's content back — so the members were not
    /// encrypted a second time inside their container, and <c>/O</c>, <c>/U</c> were left in the clear
    /// for the password check to pass. The control: the same objects in a classic input stay classic.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void ACompressedInput_IsEncryptedInTheCompressedLayout_WhereAClassicOneKeepsItsTable(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(Compressed(), new EncryptionParameters { UserPassword = UserPassword, Algorithm = algorithm });

        Assert.True(HasObjectStreams(encrypted), "the compressed input was written as a classic table");
        Assert.Matches(@"/Type\s*/XRef", Latin1(encrypted));
        Assert.DoesNotContain(Secret, Latin1(encrypted), StringComparison.Ordinal);

        byte[] decrypted = Decrypt(encrypted, UserPassword);
        Assert.Equal(Secret, MarkerString(decrypted));
        Assert.Contains("(hello)", PageContent(decrypted), StringComparison.Ordinal);

        byte[] classic = Encrypt(Classic(), new EncryptionParameters { UserPassword = UserPassword, Algorithm = algorithm });
        Assert.False(HasObjectStreams(classic), "a classic input was packed without being asked");
        Assert.Equal(Secret, MarkerString(Decrypt(classic, UserPassword)));
    }

    /// <summary><see cref="EncryptionParameters.UseObjectStreams"/> decides over the input's layout, either way.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UseObjectStreams_OverridesTheInputsLayout(bool useObjectStreams, bool compressedInput)
    {
        byte[] input = compressedInput ? Compressed() : Classic();
        byte[] encrypted = Encrypt(input, new EncryptionParameters { UserPassword = UserPassword, UseObjectStreams = useObjectStreams });

        Assert.Equal(useObjectStreams, HasObjectStreams(encrypted));
        Assert.Equal(Secret, MarkerString(Decrypt(encrypted, UserPassword)));
    }

    /// <summary>The PDF MAC placeholder is carried into the cross-reference stream's dictionary and sealed there: the token verifies over the compressed file.</summary>
    [Fact]
    public void APdfMacToken_IsSealedInTheCrossReferenceStreamDictionary()
    {
        byte[] encrypted = Encrypt(Compressed(), new EncryptionParameters
        {
            UserPassword = UserPassword, Algorithm = EncryptionAlgorithm.AES256, EnablePdfMac = true,
        });

        Assert.True(HasObjectStreams(encrypted));
        PdfMacVerification mac = PdfMacVerifier.Verify(new MemoryStream(encrypted, writable: false), UserPassword);
        Assert.True(mac.IsProtected, "no PDF MAC token was found");
        Assert.True(mac.IsValid, mac.Error);
        Assert.Equal(Secret, MarkerString(Decrypt(encrypted, UserPassword)));
    }

    /// <summary>
    /// Encrypting only the attachments in the compressed layout: the page content stays readable in the
    /// file, the embedded file's bytes do not, and they decrypt — including a file a file specification's
    /// <c>/EF</c> names without declaring its <c>/Type</c>, which the writer tells by type alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncryptOnlyAttachments_InTheCompressedLayout_EncryptsTheEmbeddedFileAlone(bool attachmentDeclaresItsType)
    {
        byte[] encrypted = Encrypt(Compressed(withAttachment: true, attachmentDeclaresItsType),
            new EncryptionParameters { UserPassword = UserPassword, Algorithm = EncryptionAlgorithm.AES128, EncryptOnlyAttachments = true });

        Assert.True(HasObjectStreams(encrypted));
        string text = Latin1(encrypted);
        Assert.Contains("(hello)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Attachment, text, StringComparison.Ordinal);

        byte[] decrypted = Decrypt(encrypted, UserPassword);
        using var doc = PdfDocument.Open(new MemoryStream(decrypted));
        var file = Assert.IsType<PdfStream>(doc.ResolveObject(new PdfReference(7, 0)));
        Assert.Equal(Attachment, Encoding.ASCII.GetString(file.GetDecodedData()));
    }

    /// <summary>The public-key encryptor keeps the compressed layout too — one rule with the password encryptor.</summary>
    [Fact]
    public void ThePublicKeyEncryptor_KeepsACompressedInputsLayout()
    {
        (byte[] certDer, byte[] pfx) = PublicKeyRoundTripTests.MakeRecipient();
        using var input = new MemoryStream(Compressed());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, certDer, PdfPermissions.All, EncryptionAlgorithm.AES256);
        byte[] encrypted = output.ToArray();

        Assert.True(HasObjectStreams(encrypted));
        Assert.DoesNotContain(Secret, Latin1(encrypted), StringComparison.Ordinal);
        Assert.Equal(Secret, MarkerString(PublicKeyRoundTripTests.Decrypt(encrypted, pfx)));
    }

    // ------------------------------------------------------------------ fixtures and helpers

    /// <summary>Objects 1 to 6 (and 7, 8 with an attachment) — a page showing <c>(hello)</c> and a dictionary holding the secret — in object streams and a cross-reference stream.</summary>
    private static byte[] Compressed(bool withAttachment = false, bool attachmentDeclaresItsType = true)
    {
        using var ms = new MemoryStream();
        PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 7), Objects(withAttachment, attachmentDeclaresItsType), Trailer());
        return ms.ToArray();
    }

    /// <summary>The same objects under a classic cross-reference table.</summary>
    private static byte[] Classic()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteHeader(new PdfVersion(1, 7));
        var entries = new List<(int objNum, int gen, long offset)>();
        foreach ((int number, PdfObject obj) in Objects(false, true))
            entries.Add((number, 0, writer.WriteIndirectObject(number, 0, obj)));
        long xref = writer.WriteXrefTable(entries, 0);
        writer.WriteTrailer(Trailer(), xref);
        return ms.ToArray();
    }

    private static List<(int, PdfObject)> Objects(bool withAttachment, bool attachmentDeclaresItsType)
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        catalog.Set(Marker, new PdfReference(6, 0));
        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        var kids = new PdfArray();
        kids.Add(new PdfReference(3, 0));
        pages.Set(PdfName.Kids, kids);
        pages.Set(PdfName.Count, new PdfNumber(1));
        var page = new PdfDictionary();
        page.Set(PdfName.Type, PdfName.Page);
        page.Set(PdfName.Parent, new PdfReference(2, 0));
        var box = new PdfArray();
        foreach (int v in new[] { 0, 0, 612, 792 }) box.Add(new PdfNumber(v));
        page.Set(PdfName.MediaBox, box);
        page.Set(PdfName.Contents, new PdfReference(4, 0));
        var fonts = new PdfDictionary();
        fonts.Set(new PdfName("F1"), new PdfReference(5, 0));
        var resources = new PdfDictionary();
        resources.Set(PdfName.Font, fonts);
        page.Set(PdfName.Resources, resources);
        var content = new PdfStream();
        content.RawData = Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (hello) Tj ET");
        var font = new PdfDictionary();
        font.Set(PdfName.Type, PdfName.Font);
        font.Set(PdfName.Subtype, PdfName.Type1);
        font.Set(PdfName.BaseFont, new PdfName("Helvetica"));
        var marker = new PdfDictionary();
        marker.Set(Marker, new PdfString(Encoding.ASCII.GetBytes(Secret)));

        var objects = new List<(int, PdfObject)> { (1, catalog), (2, pages), (3, page), (4, content), (5, font), (6, marker) };
        if (withAttachment)
        {
            var file = new PdfStream();
            if (attachmentDeclaresItsType) file.Set(PdfName.Type, PdfName.EmbeddedFile);
            file.RawData = Encoding.ASCII.GetBytes(Attachment);
            var ef = new PdfDictionary();
            ef.Set(PdfName.F, new PdfReference(7, 0));
            var filespec = new PdfDictionary();
            filespec.Set(PdfName.Type, PdfName.Filespec);
            filespec.Set(PdfName.F, new PdfString(Encoding.ASCII.GetBytes("a.txt")));
            filespec.Set(PdfName.EF, ef);
            catalog.Set(new PdfName("Attachment"), new PdfReference(8, 0));
            objects.Add((7, file));
            objects.Add((8, filespec));
        }
        return objects;
    }

    private static PdfDictionary Trailer()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        return trailer;
    }

    private static byte[] Encrypt(byte[] pdf, EncryptionParameters parameters)
    {
        parameters.OwnerPassword ??= "";
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, parameters);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), output, password);
        return output.ToArray();
    }

    /// <summary>Names are never encrypted, so an object stream's <c>/Type</c> is readable in any encrypted file.</summary>
    private static bool HasObjectStreams(byte[] pdf) => Regex.IsMatch(Latin1(pdf), @"/Type\s*/ObjStm");

    private static string MarkerString(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var marker = Assert.IsType<PdfDictionary>(doc.ResolveObject(new PdfReference(6, 0)));
        return Encoding.ASCII.GetString(Assert.IsType<PdfString>(marker[Marker]).RawBytes);
    }

    private static string PageContent(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string Latin1(byte[] bytes) => Core.Text.Latin1Text.Decode(bytes, 0, bytes.Length);
}
