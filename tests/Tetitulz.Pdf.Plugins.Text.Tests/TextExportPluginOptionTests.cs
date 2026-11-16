// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Text.Tests;

/// <summary>
/// <see cref="TextExportPlugin"/>'s handling of the <see cref="ExportOptions"/> bag.
///
/// <para>Plain UTF-8 text has nothing to configure, so this plugin recognises no key. That is a
/// legitimate answer — but <b>silently discarding</b> the bag was not: the sibling adapters honour
/// theirs, so a caller reusing one <see cref="ExportOptions"/> across formats got a settings-shaped
/// object accepted here and applied nowhere, with no way to find out. Refusing is the only
/// difference a caller can observe.</para>
/// </summary>
public class TextExportPluginOptionTests
{
    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Export(PdfDocument doc, ExportOptions? options)
    {
        using var output = new MemoryStream();
        new TextExportPlugin().Export(doc, output, options);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Fact]
    public void Export_refuses_any_option_key_and_names_it()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf("Content"));
        var options = new ExportOptions();
        options.Extra["ImageDpi"] = 300;

        var ex = Assert.Throws<ArgumentException>(() => Export(doc, options));

        Assert.Contains("ImageDpi", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no configurable settings", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Every supplied key is named, not just the first — a caller fixes them in one pass.</summary>
    [Fact]
    public void Export_names_every_supplied_key()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf("Content"));
        var options = new ExportOptions();
        options.Extra["Encoding"] = "utf-16";
        options.Extra["LineEnding"] = "crlf";

        var ex = Assert.Throws<ArgumentException>(() => Export(doc, options));

        Assert.Contains("Encoding", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LineEnding", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contrast that makes the refusal meaningful: no bag and an empty bag both export
    /// normally, so the plugin has not simply become impossible to call with an options object.
    /// </summary>
    [Fact]
    public void Export_with_no_keys_still_exports()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf("ExportableContent"));

        Assert.Contains("ExportableContent", Export(doc, null), StringComparison.Ordinal);
        Assert.Contains("ExportableContent", Export(doc, new ExportOptions()), StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal happens before the stream is touched, so a rejected call leaves the caller's
    /// document exactly where it was — the plugin does not own that stream.
    /// </summary>
    [Fact]
    public void A_refused_export_does_not_move_the_document_stream()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        const int position = 42;
        doc.BaseStream.Position = position;

        var options = new ExportOptions();
        options.Extra["Nope"] = true;

        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => new TextExportPlugin().Export(doc, output, options));

        Assert.Equal(position, doc.BaseStream.Position);
        Assert.Equal(0, output.Length);
    }
}
