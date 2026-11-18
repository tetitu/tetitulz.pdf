// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfDocumentBuilder"/>'s header is the version <see cref="PdfDocumentBuilder.WithVersion"/>
/// asks for, raised to what the document uses, and <see cref="PdfDocumentBuilder.WrittenVersion"/> /
/// <see cref="PdfDocumentBuilder.VersionRaisedBecause"/> say which and why. Every header is read from
/// the file's first bytes, never through the parser, which reads a header it cannot place as 1.4.
/// </summary>
public sealed class PdfDocumentBuilderVersionTests
{
    private static string HeaderOf(byte[] pdf)
    {
        int end = Array.IndexOf(pdf, (byte)'\n');
        return Encoding.ASCII.GetString(pdf, 0, end);
    }

    private static byte[] Build(PdfDocumentBuilder builder)
    {
        using var output = new MemoryStream();
        builder.Build(output);
        return output.ToArray();
    }

    [Fact]
    public void PlainDocument_WritesTheDefault14_AndReportsNoRaise()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 200);
        page.BeginText().SetFont(page.AddStandardFont("Helvetica"), 12).SetTextPosition(10, 10).ShowText("Hi").EndText();

        byte[] pdf = Build(builder);

        Assert.Equal("%PDF-1.4", HeaderOf(pdf));
        Assert.Equal(PdfVersion.Pdf14, builder.WrittenVersion);
        Assert.Null(builder.VersionRaisedBecause);
    }

    [Theory]
    [InlineData(1, 7, "%PDF-1.7")]
    [InlineData(2, 0, "%PDF-2.0")]
    [InlineData(1, 4, "%PDF-1.4")]
    public void RequestedVersion_AboveWhatTheContentNeeds_IsWrittenAsAsked(int major, int minor, string header)
    {
        var builder = new PdfDocumentBuilder().WithVersion(new PdfVersion(major, minor));
        builder.AddPage(200, 200);

        byte[] pdf = Build(builder);

        Assert.Equal(header, HeaderOf(pdf));
        Assert.Equal(new PdfVersion(major, minor), builder.WrittenVersion);
        Assert.Null(builder.VersionRaisedBecause);
    }

    [Fact]
    public void JpegImage2000_RaisesThe14Default_To15_NamingJpxDecode()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 200);
        string im = page.AddJpxImage(new byte[] { 0, 0, 0, 12 }, 4, 4, "DeviceRGB", 8, 0);
        page.DrawImage(im, 0, 0, 100, 100);

        byte[] pdf = Build(builder);

        Assert.Equal("%PDF-1.5", HeaderOf(pdf));
        Assert.Equal(PdfVersion.Pdf15, builder.WrittenVersion);
        Assert.Contains("JPXDecode", builder.VersionRaisedBecause);
    }

    [Fact]
    public void OpenTypeFontProgram_RaisesThe14Default_To16()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 200);
        TrueTypeFontProgram cff = TrueTypeFontProgram.Parse(Type0FontSubsetTagTests.BuildFont(asCff: true));
        Assert.True(cff.HasCff); // the premise: this program is embedded as /FontFile3 /OpenType
        PdfDocumentBuilder.EmbeddedFont font = builder.AddEmbeddedFont(cff);
        page.UseEmbeddedFont(font.ResourceName);
        page.BeginText().SetFont(font.ResourceName, 12).SetTextPosition(10, 10).ShowGlyphs(new ushort[] { 1 }).EndText();
        font.Note(1, 'A');

        byte[] pdf = Build(builder);

        Assert.Equal("%PDF-1.6", HeaderOf(pdf));
        Assert.Contains("OpenType", builder.VersionRaisedBecause);
    }

    [Fact]
    public void ConstantAlpha_RaisesARequested13_To14_AndLeavesA14RequestAlone()
    {
        static PdfDocumentBuilder WithAlpha(PdfVersion asked)
        {
            var builder = new PdfDocumentBuilder().WithVersion(asked);
            var page = builder.AddPage(200, 200);
            page.SetGraphicsState(page.AddExtGState(fillAlpha: 0.5f)).Rectangle(0, 0, 10, 10).Fill();
            return builder;
        }

        PdfDocumentBuilder raised = WithAlpha(PdfVersion.Pdf13);
        Assert.Equal("%PDF-1.4", HeaderOf(Build(raised)));
        Assert.Contains("/ca", raised.VersionRaisedBecause);

        PdfDocumentBuilder kept = WithAlpha(PdfVersion.Pdf14);
        Assert.Equal("%PDF-1.4", HeaderOf(Build(kept)));
        Assert.Null(kept.VersionRaisedBecause);
    }

    [Fact]
    public void OptionalContentInsideATransparencyGroup_RaisesTo15_ThroughTheForm()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 200);
        var group = new PdfContentStreamBuilder();
        group.BeginOptionalContent(new PdfReference(99, 0)).Rectangle(0, 0, 5, 5).Fill().EndMarkedContent();
        page.DrawForm(page.AddTransparencyGroup(group, 0, 0, 10, 10));

        byte[] pdf = Build(builder);

        Assert.Equal("%PDF-1.5", HeaderOf(pdf));
        Assert.Contains("optional content", builder.VersionRaisedBecause);
    }

    [Fact]
    public void ArtifactSubtype_RaisesTo17_AndAnArtifactWithoutOneDoesNot()
    {
        var withSubtype = new PdfDocumentBuilder();
        withSubtype.AddPage(200, 200).BeginArtifact("Pagination", "Header").EndMarkedContent();
        Assert.Equal("%PDF-1.7", HeaderOf(Build(withSubtype)));
        Assert.Contains("/Subtype", withSubtype.VersionRaisedBecause);

        var withoutSubtype = new PdfDocumentBuilder();
        withoutSubtype.AddPage(200, 200).BeginArtifact("Pagination").EndMarkedContent();
        Assert.Equal("%PDF-1.4", HeaderOf(Build(withoutSubtype)));
        Assert.Null(withoutSubtype.VersionRaisedBecause);
    }

    [Fact]
    public void HigherRequest_IsNeverLowered_ByContentThatNeedsLess()
    {
        var builder = new PdfDocumentBuilder().WithVersion(PdfVersion.Pdf20);
        var page = builder.AddPage(200, 200);
        page.DrawImage(page.AddJpxImage(new byte[] { 0, 0, 0, 12 }, 4, 4, "DeviceRGB", 8, 0), 0, 0, 10, 10);

        Assert.Equal("%PDF-2.0", HeaderOf(Build(builder)));
        Assert.Null(builder.VersionRaisedBecause);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(0, 9)]
    [InlineData(-1, 4)]
    public void WithVersion_UnpublishedVersion_IsRefused(int major, int minor)
    {
        var builder = new PdfDocumentBuilder();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithVersion(new PdfVersion(major, minor)));
        Assert.Equal("version", ex.ParamName);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 7)]
    [InlineData(2, 0)]
    public void WithVersion_PublishedVersion_IsAccepted(int major, int minor)
    {
        var builder = new PdfDocumentBuilder().WithVersion(new PdfVersion(major, minor));
        builder.AddPage(200, 200);
        builder.Build(Stream.Null);
        Assert.True(builder.WrittenVersion >= new PdfVersion(major, minor));
    }

    [Fact]
    public void FailedBuild_ClearsTheLastBuildsAnswer()
    {
        var builder = new PdfDocumentBuilder().WithVersion(PdfVersion.Pdf13);
        var page = builder.AddPage(200, 200);
        page.SetGraphicsState(page.AddExtGState(fillAlpha: 0.5f));
        builder.Build(Stream.Null);
        Assert.Equal(PdfVersion.Pdf14, builder.WrittenVersion);
        Assert.NotNull(builder.VersionRaisedBecause);

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!));

        Assert.Null(builder.WrittenVersion);
        Assert.Null(builder.VersionRaisedBecause);
    }

    [Fact]
    public void RaisedFile_StillOpens_WithItsPageAndTheRaisedVersion()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(200, 300);
        page.DrawImage(page.AddJpxImage(new byte[] { 0, 0, 0, 12 }, 4, 4, "DeviceRGB", 8, 0), 0, 0, 10, 10);

        using var doc = PdfDocument.Open(new MemoryStream(Build(builder), writable: false));

        Assert.Equal(PdfVersion.Pdf15, doc.Version);
        Assert.Single(doc.Pages);
    }
}
