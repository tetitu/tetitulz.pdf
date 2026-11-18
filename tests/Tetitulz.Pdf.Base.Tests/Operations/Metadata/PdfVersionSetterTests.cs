// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Metadata;

/// <summary>
/// <see cref="PdfVersionSetter.SetVersion"/> (<c>PdfOperations.SetVersion</c>): a raise is the catalog's
/// <c>/Version</c> in one appended revision over the original bytes; the same version is the input
/// unchanged; a lower version, one below 1.4, an unpublished one and an encrypted input are refused.
/// </summary>
public sealed class PdfVersionSetterTests
{
    private static byte[] Document(PdfVersion version)
    {
        var builder = new PdfDocumentBuilder().WithVersion(version);
        var page = builder.AddPage(200, 200);
        page.BeginText().SetFont(page.AddStandardFont("Helvetica"), 12).SetTextPosition(10, 10).ShowText("Hi").EndText();
        using var output = new MemoryStream();
        builder.Build(output);
        return output.ToArray();
    }

    private static (byte[] output, PdfVersionChange change) Set(byte[] input, PdfVersion version)
    {
        using var i = new MemoryStream(input, writable: false);
        using var o = new MemoryStream();
        PdfVersionChange change = PdfOperations.SetVersion(i, o, version);
        return (o.ToArray(), change);
    }

    private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 8);

    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 0)]
    public void ARaise_IsTheCatalogsVersion_InARevisionAfterTheOriginalBytes(int major, int minor)
    {
        byte[] input = Document(PdfVersion.Pdf14);
        var target = new PdfVersion(major, minor);

        var (output, change) = Set(input, target);

        Assert.True(change.RevisionAppended);
        Assert.Equal(PdfVersion.Pdf14, change.Previous);
        Assert.Equal(target, change.Version);

        // The original bytes, header included, come through unchanged: a signature over them keeps its range.
        Assert.True(output.Length > input.Length);
        Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input));
        Assert.Equal("%PDF-1.4", Header(output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(target, doc.Version);
        Assert.Equal(new PdfName(target.ToString()), doc.Catalog.Dictionary[PdfName.Version]);
        Assert.Single(doc.Pages);
        Assert.Equal("1.4", ReadVersionOfFirstRevision(input));
    }

    private static string ReadVersionOfFirstRevision(byte[] input)
    {
        using var doc = PdfDocument.Open(new MemoryStream(input));
        return doc.Version.ToString();
    }

    [Fact]
    public void ARaise_KeepsTheCatalogsOtherEntries()
    {
        byte[] input = Document(PdfVersion.Pdf14);
        using var withLayout = new MemoryStream();
        PdfOperations.SetInitialView(new MemoryStream(input), withLayout, new Tetitulz.Pdf.Base.Operations.Structure.InitialViewSettings
        {
            PageLayout = "TwoColumnLeft",
        });

        var (output, _) = Set(withLayout.ToArray(), PdfVersion.Pdf17);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(PdfVersion.Pdf17, doc.Version);
        Assert.Equal(new PdfName("TwoColumnLeft"), doc.Catalog.Dictionary[PdfName.PageLayout]);
        Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Pages]));
    }

    [Fact]
    public void TheSameVersion_IsTheInputUnchanged()
    {
        byte[] input = Document(PdfVersion.Pdf17);

        var (output, change) = Set(input, PdfVersion.Pdf17);

        Assert.False(change.RevisionAppended);
        Assert.Equal(PdfVersion.Pdf17, change.Previous);
        Assert.Equal(input, output);
    }

    [Fact]
    public void ALowerVersion_IsRefused_AndNothingIsWritten()
    {
        byte[] input = Document(PdfVersion.Pdf17);
        using var o = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => PdfOperations.SetVersion(new MemoryStream(input), o, PdfVersion.Pdf15));
        Assert.Contains("not lowered", ex.Message);
        Assert.Equal(0, o.Length);

        // Control: a raise of the same document is accepted.
        Assert.True(Set(input, PdfVersion.Pdf20).change.RevisionAppended);
    }

    [Fact]
    public void ARaiseBelow14_IsRefused_BecauseTheEntryIsItself14()
    {
        byte[] input = Document(PdfVersion.Pdf12);
        Assert.Equal("%PDF-1.2", Header(input));
        using var o = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => PdfOperations.SetVersion(new MemoryStream(input), o, PdfVersion.Pdf13));
        Assert.Contains("PDF 1.4", ex.Message);
        Assert.Equal(0, o.Length);

        // Control: the same document raised to 1.4 is accepted.
        var (output, change) = Set(input, PdfVersion.Pdf14);
        Assert.True(change.RevisionAppended);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Equal(PdfVersion.Pdf14, doc.Version);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(3, 0)]
    public void AnUnpublishedVersion_IsRefused(int major, int minor)
    {
        byte[] input = Document(PdfVersion.Pdf14);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOperations.SetVersion(new MemoryStream(input), new MemoryStream(), new PdfVersion(major, minor)));
        Assert.Equal("version", ex.ParamName);
    }

    [Fact]
    public void AnEncryptedDocument_IsRefused()
    {
        PdfEncryptionOperations.Register();
        byte[] plain = Document(PdfVersion.Pdf14);
        using var locked = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(plain), locked, new EncryptionParameters
        {
            UserPassword = "s3cret", OwnerPassword = "owner-only", Algorithm = EncryptionAlgorithm.AES128,
        });

        Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfOperations.SetVersion(new MemoryStream(locked.ToArray()), new MemoryStream(), PdfVersion.Pdf20));

        // Control: the same document unencrypted is raised.
        Assert.True(Set(plain, PdfVersion.Pdf20).change.RevisionAppended);
    }
}
