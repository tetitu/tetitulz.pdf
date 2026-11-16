// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.HtmlToPdf;
using Tetitulz.Pdf.Plugins.HtmlBrowser;

namespace Tetitulz.Pdf.Plugins.HtmlBrowser.Tests;

/// <summary>
/// The browser plugin's contract and its dispatch, verified <b>without</b> a browser installed.
/// </summary>
/// <remarks>
/// <para><b>What cannot be tested here, stated so a green run is not over-read.</b> Actually
/// rendering needs the Playwright binaries (<c>playwright install</c>, ~100 MB), which no CI job or
/// fresh clone has. So nothing here exercises Chromium. What it does cover is everything that can
/// be wrong <i>without</i> a browser: the request invariants that carry the security boundary, the
/// plugin's discoverability, and — the important one — that asking for the browser engine with no
/// plugin registered fails <b>loudly and actionably</b> rather than silently falling back.</para>
/// <para>That last point is why the engine moved to a plugin at all. A silent fallback to the
/// managed engine would hand a caller who asked for JavaScript a document that is wrong in ways
/// they cannot see.</para>
/// </remarks>
public class HtmlBrowserPluginTests
{
    // ---- the security-relevant request invariants ---------------------------------------

    /// <summary>
    /// An HTML <i>string</i> input must never permit <c>file://</c>. It has no directory that could
    /// legitimately be read from, and permitting it is how untrusted HTML pulls
    /// <c>file:///etc/passwd</c> into the output.
    /// </summary>
    [Fact]
    public void ForHtml_NeverAllowsTheFileScheme()
    {
        var r = HtmlBrowserRequest.ForHtml("<p>x</p>", new HtmlToPdfOptions());

        Assert.False(r.AllowFileScheme);
        Assert.Null(r.FileBaseDirectory);
        Assert.Equal("<p>x</p>", r.Html);
        Assert.Null(r.Url);
    }

    /// <summary>
    /// The file path carries its base directory through, because that directory is the only thing
    /// bounding which local files the render may read.
    /// </summary>
    [Fact]
    public void ForUrl_CarriesTheFileSchemeBoundaryThrough()
    {
        var r = HtmlBrowserRequest.ForUrl(
            "file:///c:/docs/in.html", new HtmlToPdfOptions(),
            allowFileScheme: true, fileBaseDirectory: @"c:\docs");

        Assert.True(r.AllowFileScheme);
        Assert.Equal(@"c:\docs", r.FileBaseDirectory);
        Assert.Equal("file:///c:/docs/in.html", r.Url);
        Assert.Null(r.Html);
    }

    // ---- the plugin itself --------------------------------------------------------------

    [Fact]
    public void ThePlugin_ImplementsTheContractAndIdentifiesItself()
    {
        var plugin = new PlaywrightHtmlBrowserPlugin();

        Assert.IsAssignableFrom<IHtmlBrowserPlugin>(plugin);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.False(string.IsNullOrWhiteSpace(plugin.Description));
        Assert.NotNull(plugin.Version);
    }

    [Fact]
    public async Task RenderAsync_WithANullRequest_Throws()
    {
        var plugin = new PlaywrightHtmlBrowserPlugin();
        await Assert.ThrowsAsync<ArgumentNullException>(() => plugin.RenderAsync(null!));
    }

    /// <summary>The plugin registers and resolves through the host like every other plugin.</summary>
    [Fact]
    public void ThePlugin_IsResolvableThroughTheHost()
    {
        var plugin = new PlaywrightHtmlBrowserPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            Assert.Contains(plugin, PdfPluginHost.Instance.GetPlugins<IHtmlBrowserPlugin>());
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }
    }

    // ---- dispatch ----------------------------------------------------------------------

    /// <summary>
    /// With no plugin registered, the browser engine fails with a message naming the package and the
    /// alternative — it does <b>not</b> quietly render with the managed engine.
    /// </summary>
    [Fact]
    public void BrowserEngine_WithNoPluginRegistered_FailsWithAnActionableMessage()
    {
        // Nothing registered: this assembly registers only inside the test above, in a finally.
        using var output = new MemoryStream();

        var ex = Assert.Throws<HtmlToPdfException>(() =>
            HtmlToPdfConverter.ConvertHtml("<p>x</p>", output,
                new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Browser }));

        // Names the package to add...
        Assert.Contains("Tetitulz.Pdf.Plugins.HtmlBrowser", ex.Message, StringComparison.Ordinal);
        // ...and the way out that needs nothing installed.
        Assert.Contains("Managed", ex.Message, StringComparison.Ordinal);
        // And it produced no output, rather than a silently-managed render.
        Assert.Empty(output.ToArray());
    }

    /// <summary>
    /// The managed engine is unaffected by the plugin's absence — the control that shows the failure
    /// above is about the browser engine specifically and not a broken converter.
    /// </summary>
    [Fact]
    public void ManagedEngine_WithNoPluginRegistered_StillConverts()
    {
        using var output = new MemoryStream();

        HtmlToPdfConverter.ConvertHtml("<p>Managed still works</p>", output,
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        // The TEXT, not merely that bytes came back: a blank page is also a valid non-empty PDF,
        // so a NotEmpty assertion passes with the managed engine broken.
        byte[] pdf = output.ToArray();
        using var input = new MemoryStream(pdf, writable: false);
        string text = string.Concat(Tetitulz.Pdf.Base.PdfOperations.ExtractStructuredText(input)
            .SelectMany(pg => pg.Lines)
            .SelectMany(l => l.Words)
            .Select(w => w.Text));

        Assert.Contains("Managed", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The old <c>HtmlToPdfConverter.InstallBrowsers</c> throws with a message naming its
    /// replacement, rather than having been deleted into a bare compile error.
    /// </summary>
    [Fact]
    public void TheOldInstallBrowsers_PointsAtItsReplacement()
    {
#pragma warning disable CS0618 // intentionally calling the obsolete stub
        var ex = Assert.Throws<HtmlToPdfException>(() => HtmlToPdfConverter.InstallBrowsers());
#pragma warning restore CS0618

        Assert.Contains("PlaywrightHtmlBrowserPlugin.InstallBrowsers", ex.Message, StringComparison.Ordinal);
    }
}
