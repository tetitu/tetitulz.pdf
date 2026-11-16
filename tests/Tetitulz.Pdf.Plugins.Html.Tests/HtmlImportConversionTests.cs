// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// <see cref="HtmlImportPlugin"/>'s <b>conversion</b>, which used to be recorded as deliberately
/// untestable.
///
/// <para><b>The reason for that stopped being true.</b> Both this suite's own doc comments and
/// GAPS.md said import "goes through Playwright's headless browser by design", which resolves one
/// pinned build — so a skip condition would be unreliable and a test could essentially never turn
/// green. But <c>HtmlToPdfOptions.RenderEngine</c> defaults to <c>Managed</c>, and this plugin
/// never sets it: its default path is pure-managed, in-process, needs no browser and no network,
/// and is perfectly testable. What the note protected was real when it was written; it outlived the
/// fact it rested on, and left the plugin's actual shipping default with no coverage at all.</para>
///
/// <para><b>What is asserted is what the plugin itself contributes</b>, not what
/// <c>HtmlToPdf.Tests</c> already covers: it decodes the source stream as UTF-8, it hands the
/// markup to the converter, and the PDF that comes back carries the document's text. Asserting
/// only that bytes were returned would pass with the conversion call deleted — an empty page is a
/// perfectly valid PDF.</para>
/// </summary>
[Collection(PluginHostCollection.Name)]
public class HtmlImportConversionTests
{
    private static byte[] Import(string html, ImportOptions options = null)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(html));
        using var output = new MemoryStream();
        new HtmlImportPlugin().Import(source, output, options);
        return output.ToArray();
    }

    [Fact]
    public void Import_WithNoOptions_UsesTheManagedEngine_AndDrawsTheDocumentsText()
    {
        // No browser is registered in this process, so reaching Playwright would throw rather than
        // return an empty document — which is itself half the claim: the default path is managed.
        byte[] pdf = Import("<html><body><p>Imported text</p></body></html>");

        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(pdf, 0, 5), StringComparison.Ordinal);

        // Both tokens, separately: Builder's exporter positions every piece with its own `Tm`, so
        // a two-word phrase is two `Tj` operations rather than one string.
        string content = PdfContentProbe.JoinKernedStrings(PdfContentProbe.DecodeStreams(pdf));   // kerned words read whole
        Assert.Contains("(Imported) Tj", content, StringComparison.Ordinal);
        Assert.Contains("(text) Tj", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_DecodesTheSourceStreamAsUtf8_NotAsLatin1()
    {
        // This is the plugin's own contribution — HtmlToPdfConverter takes a string, so the byte
        // decoding happens here and nowhere else. `é` is two bytes in UTF-8; read as Latin-1 it
        // becomes "Ã©", which is what a wrong Encoding argument produces. Both forms are drawable
        // by a WinAnsi standard-14 font, so the failure is silent mojibake rather than an
        // exception — and only a two-sided assertion can see it.
        byte[] pdf = Import("<p>café</p>");

        string content = PdfContentProbe.JoinKernedStrings(PdfContentProbe.DecodeStreams(pdf));   // kerned words read whole
        Assert.Contains("café", content, StringComparison.Ordinal);
        Assert.DoesNotContain("cafÃ©", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_StripsAUtf8ByteOrderMark_RatherThanDrawingIt()
    {
        // The reader is constructed with detectEncodingFromByteOrderMarks: true, so a BOM must be
        // consumed. Left in the string it becomes U+FEFF at the head of the markup, which the
        // parser treats as text — an invisible character in the output and in every copy-paste.
        using var source = new MemoryStream(new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("<p>Body</p>")).ToArray());
        using var output = new MemoryStream();
        new HtmlImportPlugin().Import(source, output, null);

        string content = PdfContentProbe.DecodeStreams(output.ToArray());
        Assert.Contains("(Body) Tj", content, StringComparison.Ordinal);
        Assert.DoesNotContain("﻿", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_HonoursThePageSizeItWasGiven()
    {
        // The option keys reach HtmlToPdfOptions rather than being parsed and dropped: page size
        // is the one whose effect is legible in the file without decoding anything.
        var options = new ImportOptions();
        options.Extra["PageWidth"] = "300pt";
        options.Extra["PageHeight"] = "400pt";

        string raw = PdfContentProbe.Raw(Import("<p>x</p>", options));

        Assert.Contains("/MediaBox [0 0 300 400]", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/MediaBox [0 0 612 792]", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_LeavesTheSourceStreamOpen_SoACallerCanKeepReadingIt()
    {
        // `leaveOpen: true` on the StreamReader. A caller that passed a stream it still owns —
        // a request body, a zip entry — would otherwise find it disposed on return, and the
        // failure would surface far from here.
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("<p>x</p>"));
        using var output = new MemoryStream();

        new HtmlImportPlugin().Import(source, output, null);

        Assert.True(source.CanRead);
        source.Position = 0;
        Assert.Equal('<', (char)source.ReadByte());
    }

    [Fact]
    public void ImportThroughTheFacade_FindsThePluginByFormatKeyAndConverts()
    {
        // The host's path: registered with PdfPluginHost and reached by format key, which is what
        // an application actually does. Dispatch and conversion are separate failures.
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            PdfPluginHost.Instance.Register(new HtmlImportPlugin());

            using var source = new MemoryStream(Encoding.UTF8.GetBytes("<p>Dispatched</p>"));
            using var output = new MemoryStream();
            Tetitulz.Pdf.Base.PdfOperations.Import(source, output, "html");

            Assert.Contains("(Dispatched) Tj",
                PdfContentProbe.DecodeStreams(output.ToArray()), StringComparison.Ordinal);
        }
        finally { PdfPluginHost.Instance.ResetForTesting(); }
    }
}
