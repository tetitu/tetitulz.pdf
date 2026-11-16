// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>Tests that register the plugin with the process-wide host run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginHostCollection
{
    public const string Name = "HtmlScriptPluginHost";
}

/// <summary>
/// A page's own scripts, run by <see cref="JintHtmlScriptPlugin"/>, decide what the managed engine
/// lays out. End to end: HTML in, PDF out, text read back.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class PageScriptTests
{
    internal static (string Text, List<string> Reports) Convert(string html)
    {
        var plugin = new JintHtmlScriptPlugin();
        var reports = new List<string>();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            using var output = new MemoryStream();
            HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                OnDiagnostic = d =>
                {
                    if (d.Kind == BuilderDiagnosticKind.ContentApproximated) reports.Add(d.Detail);
                },
            });
            output.Position = 0;
            using var doc = PdfDocument.Open(output);
            return (Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc), reports);
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }
    }

    /// <summary>A script's change to the document is what is laid out.</summary>
    [Fact]
    public void AScriptsChangeToTheTextIsWhatIsLaidOut()
    {
        var (text, _) = Convert(
            "<html><body><p id='a'>BEFORE</p>" +
            "<script>document.getElementById('a').textContent = 'AFTER';</script></body></html>");

        Assert.Contains("AFTER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner's case in plain DOM: a script moves a cell's content into a header placeholder and
    /// removes the cell — the body row loses its extra column.
    /// </summary>
    [Fact]
    public void AScriptThatMovesContentMovesItInTheOutput()
    {
        var (text, _) = Convert(
            "<html><body><table><thead><tr><th>HEAD</th><th class='holder'></th></tr></thead>" +
            "<tbody><tr><td>CELL</td><td class='moveup'>PHOTO</td></tr></tbody></table>" +
            "<script>var m = document.querySelector('.moveup');" +
            "document.querySelector('.holder').textContent = m.textContent;" +
            "m.parentNode.removeChild(m);</script></body></html>");

        // PHOTO now sits in the header row, before the body row's CELL.
        int photo = text.IndexOf("PHOTO", StringComparison.Ordinal);
        int cell = text.IndexOf("CELL", StringComparison.Ordinal);
        Assert.True(photo >= 0 && cell >= 0, $"both words should be present: {text}");
        Assert.True(photo < cell, $"PHOTO should have moved into the header, before CELL: {text}");
    }

    /// <summary>A <c>DOMContentLoaded</c> handler — where jQuery's ready callbacks run — takes effect.</summary>
    [Fact]
    public void ADomContentLoadedHandlerTakesEffect()
    {
        var (text, _) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "document.addEventListener('DOMContentLoaded', function () {" +
            "  document.getElementById('a').textContent = 'READY'; });</script></body></html>");

        Assert.Contains("READY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero-delay timer scheduled from a ready handler — how plugins defer work "until after the
    /// page settles" — has run before the page is laid out. Ten runs, because one pass can be a race
    /// won; ten are evidence — and they showed a zero-delay timer needs no settle window at all.
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void AZeroDelayTimerFromAReadyHandlerTakesEffect(int run)
    {
        _ = run;
        var (text, _) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "document.addEventListener('DOMContentLoaded', function () {" +
            "  setTimeout(function () { document.getElementById('a').textContent = 'TIMED'; }, 0); });" +
            "</script></body></html>");

        Assert.Contains("TIMED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A short-delay timer — 30 ms, inside the default <c>TimerSettleTime</c> — has run too. This is
    /// the case the settle window exists for: a zero-delay timer already runs before the page is
    /// serialised without it (the window's mutation survived the zero-delay theory), a 30 ms one does
    /// not. Ten runs, for the same reason as above.
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void AShortDelayTimerInsideTheSettleWindowTakesEffect(int run)
    {
        _ = run;
        var (text, _) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "setTimeout(function () { document.getElementById('a').textContent = 'SETTLED'; }, 30);" +
            "</script></body></html>");

        Assert.Contains("SETTLED", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timer due long after the settle window does not run — and does not hold the conversion up
    /// waiting for it: the page is laid out as it stood, well inside the page deadline.
    /// </summary>
    [Fact]
    public void ALateTimerDoesNotRunAndDoesNotHoldTheConversion()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (text, reports) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "setTimeout(function () { document.getElementById('a').textContent = 'LATE'; }, 5000);" +
            "</script></body></html>");
        clock.Stop();

        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4),
            $"the conversion took {clock.Elapsed.TotalSeconds:F1} s: it waited for a timer it should not run");
    }

    /// <summary>
    /// A window <c>load</c> handler — jQuery's <c>$(window).on('load', …)</c>, later than
    /// <c>DOMContentLoaded</c> — takes effect too.
    /// </summary>
    [Fact]
    public void AWindowLoadHandlerTakesEffect()
    {
        var (text, _) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "window.addEventListener('load', function () {" +
            "  document.getElementById('a').textContent = 'LOADED'; });</script></body></html>");

        Assert.Contains("LOADED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A style a script writes is honoured: <c>display: none</c> set through <c>element.style</c>
    /// hides the element. This is the CSS object model the plugin must configure — without
    /// AngleSharp.Css, <c>element.style</c> is null and this script throws.
    /// </summary>
    [Fact]
    public void AStyleAScriptWritesIsHonoured()
    {
        var (text, reports) = Convert(
            "<html><body><p>KEPT</p><p id='gone'>HIDDEN</p>" +
            "<script>document.getElementById('gone').style.display = 'none';</script></body></html>");

        Assert.Contains("KEPT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN", text, StringComparison.Ordinal);
        Assert.DoesNotContain(reports, r => r.Contains("threw", StringComparison.Ordinal));
    }

    /// <summary>
    /// A script that throws is reported, and the scripts after it still run — the page's own error,
    /// handled as a browser handles it.
    /// </summary>
    [Fact]
    public void AThrowingScriptIsReportedAndTheNextOneStillRuns()
    {
        var (text, reports) = Convert(
            "<html><body><p id='a'>BEFORE</p>" +
            "<script>undefinedFunctionCall();</script>" +
            "<script>document.getElementById('a').textContent = 'AFTER';</script></body></html>");

        Assert.Contains("AFTER", text, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("threw", StringComparison.Ordinal)
                                      && r.Contains("undefinedFunctionCall", StringComparison.Ordinal));
    }

    /// <summary>The control: scripts that do not throw report nothing about throwing.</summary>
    [Fact]
    public void ScriptsThatDoNotThrowReportNothing()
    {
        var (_, reports) = Convert(
            "<html><body><p id='a'>BEFORE</p>" +
            "<script>document.getElementById('a').textContent = 'AFTER';</script></body></html>");

        Assert.DoesNotContain(reports, r => r.Contains("threw", StringComparison.Ordinal));
    }

    /// <summary>
    /// Hebrew survives the round trip through the script engine and back to HTML — the owner's
    /// pages are Hebrew, and the post-script DOM is serialised before it is laid out.
    /// </summary>
    [Fact]
    public void HebrewTextSurvivesTheRoundTrip()
    {
        var (text, _) = Convert(
            "<html dir='rtl'><head><meta charset='utf-8'></head><body><p id='a'>x</p>" +
            "<script>document.getElementById('a').textContent = 'שלום עולם';</script></body></html>");

        Assert.Contains("שלום", text, StringComparison.Ordinal);
    }
}
