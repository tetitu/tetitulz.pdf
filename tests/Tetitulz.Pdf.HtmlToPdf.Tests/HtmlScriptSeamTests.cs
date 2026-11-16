// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Tests that touch <see cref="PdfPluginHost.Instance"/> for <see cref="IHtmlScriptPlugin"/>. Run
/// alone: once a script plugin can be registered, every managed conversion of a page with a
/// <c>&lt;script&gt;</c> asks the host, so a fake registered here would reach a test running beside it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HtmlScriptPluginCollection
{
    public const string Name = "HtmlScriptPlugin";
}

/// <summary>
/// The managed engine's seam for page JavaScript: the page after its scripts is
/// what is laid out when scripting is on and a plugin is registered; anything less is reported.
/// </summary>
/// <remarks>
/// A fake plugin stands in for the Jint engine, which is 16b's — this is the contract, tested on its
/// own so a defect in the seam cannot hide behind one in the engine.
/// </remarks>
[Collection(HtmlScriptPluginCollection.Name)]
public sealed class HtmlScriptSeamTests
{
    private const string ScriptedPage =
        "<html><body><p>BEFORE</p><script>document.body.innerHTML = '';</script></body></html>";

    private sealed class FakeScriptPlugin : IHtmlScriptPlugin
    {
        public Func<HtmlScriptRequest, string?> Run { get; set; } = _ => "<html><body><p>AFTER</p></body></html>";
        public int Calls { get; private set; }
        public string Name => "Tests.FakeScriptPlugin";
        public string Description => "Stands in for a script engine.";
        public Version Version => new(1, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public string RunScripts(HtmlScriptRequest request)
        {
            Calls++;
            return Run(request)!;
        }
    }

    private static (string Text, List<string> Reports) Convert(
        string html, FakeScriptPlugin? plugin, Action<HtmlToPdfOptions>? configure = null)
    {
        var reports = new List<string>();
        var options = new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d =>
            {
                if (d.Kind == BuilderDiagnosticKind.ContentApproximated) reports.Add(d.Detail);
            },
        };
        configure?.Invoke(options);

        if (plugin != null) PdfPluginHost.Instance.Register(plugin);
        try
        {
            using var output = new MemoryStream();
            HtmlToPdfConverter.ConvertHtml(html, output, options);
            output.Position = 0;
            using var doc = PdfDocument.Open(output);
            return (Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc), reports);
        }
        finally
        {
            if (plugin != null) PdfPluginHost.Instance.Unregister(plugin.Name);
        }
    }

    // ------------------------------------------------------------------ the scripted DOM

    /// <summary>
    /// With scripting on and a plugin registered, the plugin's post-script document is what is laid
    /// out — not the page as written.
    /// </summary>
    [Fact]
    public void ThePluginsPostScriptDocumentIsWhatIsLaidOut()
    {
        var plugin = new FakeScriptPlugin();
        var (text, reports) = Convert(ScriptedPage, plugin);

        Assert.Equal(1, plugin.Calls);
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE", text, StringComparison.Ordinal);
        Assert.DoesNotContain(reports, r => r.Contains("did not run", StringComparison.Ordinal));
    }

    /// <summary>
    /// <see cref="HtmlToPdfOptions.JavaScriptEnabled"/> off: the plugin is never asked, even though
    /// it is registered — and nothing is reported, because the caller turned scripting off.
    /// </summary>
    [Fact]
    public void WithScriptingOffThePluginIsNotAsked()
    {
        var plugin = new FakeScriptPlugin();
        var (text, reports) = Convert(ScriptedPage, plugin, o => o.JavaScriptEnabled = false);

        Assert.Equal(0, plugin.Calls);
        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.DoesNotContain(reports, r => r.Contains("did not run", StringComparison.Ordinal));
    }

    /// <summary>
    /// A page with no script never reaches the plugin: a host that registered one converts static
    /// pages exactly as a host that did not.
    /// </summary>
    [Fact]
    public void APageWithoutScriptsNeverReachesThePlugin()
    {
        var plugin = new FakeScriptPlugin();
        var (text, _) = Convert("<html><body><p>BEFORE</p></body></html>", plugin);

        Assert.Equal(0, plugin.Calls);
        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ reporting what did not happen

    /// <summary>
    /// No plugin, scripts present, scripting on: the page as written is laid out and the report
    /// says the scripts did not run and what would run them.
    /// </summary>
    [Fact]
    public void ScriptsThatDidNotRunAreReported()
    {
        var (text, reports) = Convert(ScriptedPage, plugin: null);

        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("1 script did not run", StringComparison.Ordinal)
                                      && r.Contains("IHtmlScriptPlugin", StringComparison.Ordinal));
    }

    /// <summary>The control: a page with no script reports nothing about scripts.</summary>
    [Fact]
    public void APageWithoutScriptsReportsNothingAboutScripts()
    {
        var (_, reports) = Convert("<html><body><p>BEFORE</p></body></html>", plugin: null);

        Assert.DoesNotContain(reports, r => r.Contains("script", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A plugin that throws degrades to the page as written — reported with the plugin's name —
    /// rather than failing the conversion.
    /// </summary>
    [Fact]
    public void AFailingPluginIsReportedAndThePageAsWrittenIsLaidOut()
    {
        var plugin = new FakeScriptPlugin { Run = _ => throw new InvalidOperationException("engine down") };
        var (text, reports) = Convert(ScriptedPage, plugin);

        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("Tests.FakeScriptPlugin", StringComparison.Ordinal)
                                      && r.Contains("engine down", StringComparison.Ordinal));
    }

    /// <summary>
    /// A plugin returning no document degrades the same way, and says so — a null must not be
    /// parsed as an empty page.
    /// </summary>
    [Fact]
    public void APluginReturningNothingIsReportedAndThePageAsWrittenIsLaidOut()
    {
        var plugin = new FakeScriptPlugin { Run = _ => null };
        var (text, reports) = Convert(ScriptedPage, plugin);

        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("returned no document", StringComparison.Ordinal));
    }

    /// <summary>What a plugin reports reaches the caller's diagnostic sink.</summary>
    [Fact]
    public void APluginsReportReachesTheCaller()
    {
        var plugin = new FakeScriptPlugin
        {
            Run = r => { r.Report("a script threw: boom"); return "<html><body><p>AFTER</p></body></html>"; },
        };
        var (_, reports) = Convert(ScriptedPage, plugin);

        Assert.Contains("a script threw: boom", reports);
    }

    // ------------------------------------------------------------------ <canvas>

    /// <summary>
    /// A <c>&lt;canvas&gt;</c> is reported as not drawn — with no plugin at all, since no script
    /// engine here has a 2D context either.
    /// </summary>
    [Fact]
    public void ACanvasIsReportedAsNotDrawn()
    {
        var (_, reports) = Convert(
            "<html><body><p>BEFORE</p><canvas id='signature' width='300' height='100'></canvas></body></html>",
            plugin: null);

        Assert.Single(reports, r => r.Contains("<canvas>", StringComparison.Ordinal)
                                    && r.Contains("HtmlRenderEngine.Browser", StringComparison.Ordinal));
    }

    /// <summary>
    /// A canvas a script creates is reported too: the check reads the document that is laid out,
    /// after the scripts ran.
    /// </summary>
    [Fact]
    public void ACanvasCreatedByAScriptIsReportedToo()
    {
        var plugin = new FakeScriptPlugin { Run = _ => "<html><body><p>AFTER</p><canvas></canvas></body></html>" };
        var (_, reports) = Convert(ScriptedPage, plugin);

        Assert.Contains(reports, r => r.Contains("<canvas>", StringComparison.Ordinal));
    }

    /// <summary>The control: a page without a canvas says nothing about one.</summary>
    [Fact]
    public void APageWithoutACanvasSaysNothingAboutOne()
    {
        var (_, reports) = Convert("<html><body><p>BEFORE</p></body></html>", plugin: null);

        Assert.DoesNotContain(reports, r => r.Contains("canvas", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ LoadScript: the engine's own rules

    /// <summary>
    /// A remote script with no <see cref="HtmlToPdfOptions.RemoteResourceLoader"/> is not fetched —
    /// the engine's no-network default — and the refusal is the engine's own report.
    /// </summary>
    [Fact]
    public void ARemoteScriptWithNoLoaderIsRefusedAndReported()
    {
        string? loaded = "unset";
        var plugin = new FakeScriptPlugin
        {
            Run = r => { loaded = r.LoadScript("https://cdn.example.test/app.js"); return r.Html; },
        };
        var (_, reports) = Convert(ScriptedPage, plugin);

        Assert.Null(loaded);
        Assert.Contains(reports, r => r.Contains("app.js", StringComparison.Ordinal)
                                      && r.Contains("RemoteResourceLoader", StringComparison.Ordinal));
    }

    /// <summary>
    /// A local script inside the base directory is read; one outside it is refused and reported —
    /// the same containment every local image and stylesheet goes through.
    /// </summary>
    [Fact]
    public void ALocalScriptIsReadInsideTheBaseDirectoryAndRefusedOutsideIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "tetitulz-script-" + Guid.NewGuid().ToString("N"));
        string inside = Path.Combine(root, "site");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "app.js"), "var fromDisk = 1;");
        File.WriteAllText(Path.Combine(root, "secret.js"), "var secret = 1;");
        try
        {
            string? inBase = "unset", outOfBase = "unset";
            var plugin = new FakeScriptPlugin
            {
                Run = r =>
                {
                    inBase = r.LoadScript("app.js");
                    outOfBase = r.LoadScript("../secret.js");
                    return r.Html;
                },
            };
            var (_, reports) = Convert(ScriptedPage, plugin, o => o.BasePath = inside);

            Assert.Equal("var fromDisk = 1;", inBase);
            Assert.Null(outOfBase);
            Assert.Contains(reports, r => r.Contains("secret.js", StringComparison.Ordinal)
                                          && r.Contains("BasePath", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
