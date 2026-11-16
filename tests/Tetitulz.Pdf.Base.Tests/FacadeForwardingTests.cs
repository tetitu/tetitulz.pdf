// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// What the facade hands on: the caller's export and import options reach the plugin as given, and
/// a null stream is refused under the facade's own parameter name. Found by the first mixed-mode
/// sweep (rows drafted by a local model), where each of these survived.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class FacadeForwardingTests : IDisposable
{
    public FacadeForwardingTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    private sealed class CapturingExporter : IExportPlugin
    {
        internal ExportOptions Seen;
        internal int Calls;
        public string Name => "Tetitulz.Tests.CapturingExporter";
        public string Description => "Records the options it is handed.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public string FormatKey => "capture";
        public string TargetMimeType => "application/octet-stream";
        public void Export(PdfDocument document, Stream output, ExportOptions options)
        {
            Calls++;
            Seen = options;
        }
    }

    private sealed class CapturingImporter : IImportPlugin
    {
        internal ImportOptions Seen;
        public string Name => "Tetitulz.Tests.CapturingImporter";
        public string Description => "Records the options it is handed.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public string FormatKey => "capture";
        public string SourceMimeType => "application/octet-stream";
        public void Import(Stream source, Stream pdfOutput, ImportOptions options) => Seen = options;
    }

    [Fact]
    public void Export_HandsThePluginTheCallersOptions()
    {
        var exporter = new CapturingExporter();
        PdfPluginHost.Instance.Register(exporter);
        var options = new ExportOptions();

        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        PdfOperations.Export(input, new MemoryStream(), "capture", options);

        Assert.Equal(1, exporter.Calls);
        Assert.Same(options, exporter.Seen);
    }

    /// <summary>
    /// The caller's options are passed through as the same object; with none, the plugin is given
    /// a fresh default rather than <c>null</c> — the contract <see cref="IImportPlugin.Import"/>
    /// documents.
    /// </summary>
    [Fact]
    public void Import_HandsThePluginTheCallersOptions_OrADefaultForNone()
    {
        var importer = new CapturingImporter();
        PdfPluginHost.Instance.Register(importer);
        var options = new ImportOptions();

        PdfOperations.Import(new MemoryStream(new byte[] { 1 }), new MemoryStream(), "capture", options);
        Assert.Same(options, importer.Seen);

        PdfOperations.Import(new MemoryStream(new byte[] { 1 }), new MemoryStream(), "capture", null);
        Assert.NotNull(importer.Seen);
        Assert.NotSame(options, importer.Seen);
    }

    /// <summary>
    /// A null stream is refused naming the facade's parameter, <c>input</c> — not the name of
    /// whatever helper further down would have tripped over it. The per-page overload is the
    /// control: it has always named it.
    /// </summary>
    [Fact]
    public void ExtractText_NullStream_IsRefusedUnderItsOwnName()
    {
        var whole = Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractText((Stream)null));
        Assert.Equal("input", whole.ParamName);

        var page = Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractText((Stream)null, 1));
        Assert.Equal("input", page.ParamName);
    }
}
