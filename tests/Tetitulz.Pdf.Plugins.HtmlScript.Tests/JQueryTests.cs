// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// Real jQuery, on a page shaped like the owner's Bootstrap 3 forms: loaded
/// through <c>&lt;script src&gt;</c> from the base directory, a ready handler moving a photo cell
/// into its header placeholder, setting text, adding a class and writing a style.
/// </summary>
/// <remarks>
/// Both 1.12.4 (what Bootstrap 3 pages load) and 3.7.1. The first fails outright without the CSS
/// object model — it assigns <c>a.style.cssText</c> during its own start-up — so a pass for it is
/// the evidence that <c>AngleSharp.Css</c> is wired; 3.x would only have lost its <c>.css()</c>.
/// </remarks>
[Collection(PluginHostCollection.Name)]
public sealed class JQueryTests
{
    private const string Form =
        "<!DOCTYPE html><html dir='rtl'><head><meta charset='utf-8'><script src='JQUERY'></script></head><body>" +
        "<table><thead><tr><th>HEAD</th><th class='moveupholder'></th></tr></thead>" +
        "<tbody><tr><td>CELL</td><td class='moveup'>PHOTO</td></tr></tbody></table>" +
        "<p id='status'>BEFORE</p><p class='hide-me'>HIDDEN</p>" +
        "<script>$(function () {" +
        "  $('.moveup').each(function () { $('.moveupholder').append($(this).html()); $(this).remove(); });" +
        "  $('#status').text('READY-' + $.fn.jquery).addClass('done');" +
        "  $('.hide-me').css('display', 'none');" +
        "});</script></body></html>";

    [Theory]
    [InlineData("jquery-1.12.4.min.js", "1.12.4")]
    [InlineData("jquery-3.7.1.min.js", "3.7.1")]
    public void JQueryRunsTheFormsReadyHandler(string file, string version)
    {
        var reports = new List<string>();
        var plugin = new JintHtmlScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);
        string text;
        try
        {
            using var output = new MemoryStream();
            HtmlToPdfConverter.ConvertHtml(Form.Replace("JQUERY", file), output, new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                BasePath = Path.Combine(AppContext.BaseDirectory, "Fixtures"),
                OnDiagnostic = d =>
                {
                    if (d.Kind == BuilderDiagnosticKind.ContentApproximated) reports.Add(d.Detail);
                },
            });
            output.Position = 0;
            using var doc = PdfDocument.Open(output);
            text = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }

        Assert.DoesNotContain(reports, r => r.Contains("threw", StringComparison.Ordinal));
        Assert.Contains("READY-" + version, text, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN", text, StringComparison.Ordinal);

        // PHOTO moved into the header row, ahead of the body row's CELL.
        int photo = text.IndexOf("PHOTO", StringComparison.Ordinal);
        int cell = text.IndexOf("CELL", StringComparison.Ordinal);
        Assert.True(photo >= 0 && photo < cell, $"PHOTO should have moved into the header: {text}");
    }
}
