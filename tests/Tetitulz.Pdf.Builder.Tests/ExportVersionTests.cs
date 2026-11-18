// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExportOptions.Version"/>: unset, the header is the lowest version the content needs
/// (never below 1.4); set, it is written as asked, or raised to the content and reported as
/// <see cref="BuilderDiagnosticKind.VersionRaised"/>. Headers are read from the file's own bytes.
/// </summary>
public sealed class ExportVersionTests
{
    private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 8);

    private static Document Plain(string text = "Hello")
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(new Paragraph { Runs = { new TextRun { Text = text } } });
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>Cyrillic text in a CFF font the provider supplies: embedded as <c>/FontFile3 /Subtype /OpenType</c>, PDF 1.6.</summary>
    private static (Document doc, PdfExportOptions options) OpenTypeFont(List<BuilderDiagnostic> seen, PdfVersion? version)
    {
        string alphabet = new string(Enumerable.Range(0x0410, 8).Select(c => (char)c).ToArray());
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalOpenTypeCff.Build(alphabet),
            OnDiagnostic = seen.Add,
            Version = version,
        };
        return (Plain(alphabet.Substring(0, 3)), options);
    }

    [Fact]
    public void Unset_WritesTheLowestTheContentNeeds_AndReportsNothing()
    {
        var seen = new List<BuilderDiagnostic>();
        Assert.Equal("%PDF-1.4", Header(Plain().ToPdf(new PdfExportOptions { OnDiagnostic = seen.Add })));

        var (doc, options) = OpenTypeFont(seen, version: null);
        Assert.Equal("%PDF-1.6", Header(doc.ToPdf(options)));

        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.VersionRaised);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 0)]
    public void AHigherVersion_IsWrittenAsAsked(int major, int minor)
    {
        byte[] pdf = Plain().ToPdf(new PdfExportOptions { Version = new PdfVersion(major, minor) });
        Assert.Equal($"%PDF-{major}.{minor}", Header(pdf));
    }

    [Fact]
    public void AVersionBelowTheContent_IsRaisedToIt_AndReportedNamingTheFeature()
    {
        var seen = new List<BuilderDiagnostic>();
        var (doc, options) = OpenTypeFont(seen, PdfVersion.Pdf14);

        Assert.Equal("%PDF-1.6", Header(doc.ToPdf(options)));

        BuilderDiagnostic raised = Assert.Single(seen, d => d.Kind == BuilderDiagnosticKind.VersionRaised);
        Assert.Contains("PDF 1.4 was asked for", raised.Detail);
        Assert.Contains("PDF 1.6", raised.Detail);
        Assert.Contains("OpenType", raised.Detail);
    }

    [Fact]
    public void AVersionTheContentFits_IsNotReported()
    {
        var seen = new List<BuilderDiagnostic>();
        var (doc, options) = OpenTypeFont(seen, PdfVersion.Pdf16);

        Assert.Equal("%PDF-1.6", Header(doc.ToPdf(options)));
        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.VersionRaised);
    }

    [Fact]
    public void AVersionBelowCompression_IsRaisedTo12()
    {
        // Every exported stream is FlateDecode (PDF 1.2), so 1.0 and 1.1 can never stand.
        var seen = new List<BuilderDiagnostic>();
        byte[] pdf = Plain().ToPdf(new PdfExportOptions { Version = PdfVersion.Pdf10, OnDiagnostic = seen.Add });

        Assert.Equal("%PDF-1.2", Header(pdf));
        Assert.Contains("FlateDecode", Assert.Single(seen, d => d.Kind == BuilderDiagnosticKind.VersionRaised).Detail);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 1)]
    [InlineData(0, 9)]
    public void AnUnpublishedVersion_IsRefused(int major, int minor)
    {
        var options = new PdfExportOptions();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Version = new PdfVersion(major, minor));
        Assert.Equal("value", ex.ParamName);
        Assert.Null(options.Version);

        // Control: a published version and null are both accepted.
        options.Version = PdfVersion.Pdf20;
        Assert.Equal(PdfVersion.Pdf20, options.Version);
        options.Version = null;
        Assert.Null(options.Version);
    }

    [Fact]
    public void ARaisedFile_OpensAtTheVersionItsHeaderNames()
    {
        var seen = new List<BuilderDiagnostic>();
        var (doc, options) = OpenTypeFont(seen, PdfVersion.Pdf13);

        using var ms = new System.IO.MemoryStream(doc.ToPdf(options));
        using var opened = PdfDocument.Open(ms);
        Assert.Equal(PdfVersion.Pdf16, opened.Version);
        Assert.Single(opened.Pages);
    }
}
