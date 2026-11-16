// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The official package's extension methods, in the namespaces a host imports. That this file
// compiles is half of what it tests: while the vendored copy kept AngleSharp.Js's namespaces,
// `Configuration.Default.WithJs()` below was CS0121, an ambiguous call between two assemblies.
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Js;
using AngleSharp.Scripting;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// The vendored AngleSharp.Js (VENDORED.md) lives in <c>Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs</c>,
/// so a host that also references the official AngleSharp.Js package — this test project does — sees
/// each name once. Its types stay public (AngleSharp.Js finds its DOM globals among its own assembly's
/// exported types); the namespace is what keeps them out of a host's way.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class NamespaceIsolationTests
{
    /// <summary>
    /// No type the plugin exports sits in an <c>AngleSharp</c> namespace, where a host's
    /// <c>using AngleSharp;</c> would meet it. The potency check: the vendored types are exported,
    /// from the private namespace.
    /// </summary>
    [Fact]
    public void NoExportedTypeSitsInAnAngleSharpNamespace()
    {
        Type[] exported = typeof(JintHtmlScriptPlugin).Assembly.GetExportedTypes();
        var shared = exported.Where(t => t.Namespace != null &&
            (t.Namespace == "AngleSharp" || t.Namespace.StartsWith("AngleSharp.", StringComparison.Ordinal)))
            .Select(t => t.FullName).ToList();
        Assert.Empty(shared);

        Assert.Contains(exported, t => t.FullName == "Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs.JsScriptingService");
        Assert.Contains(exported, t => t.FullName == "Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs.JsConfigurationExtensions");
        Assert.True(exported.Count(t => t.Namespace?.StartsWith("Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs", StringComparison.Ordinal) == true) >= 16);   // the 16 AngleSharp.Js 1.1.0 exports
    }

    /// <summary>
    /// The official engine and the plugin run in one process: the official package's
    /// <c>WithJs()</c> runs a page's script, and the plugin then runs another page's — neither
    /// takes the other's types or its DOM globals.
    /// </summary>
    [Fact]
    public async Task TheOfficialPackageAndThePluginRunSideBySide()
    {
        var context = BrowsingContext.New(Configuration.Default.WithJs());
        IDocument document = await context.OpenAsync(r => r.Content(
            "<html><body><p id='a'>BEFORE</p><script>document.getElementById('a').textContent = 'OFFICIAL';</script></body></html>"));
        Assert.Equal("OFFICIAL", document.GetElementById("a")!.TextContent);
        // The host's scripting service is the official package's own type.
        Assert.Equal("AngleSharp.Js", context.GetService<JsScriptingService>()!.GetType().Assembly.GetName().Name);

        var (text, _) = PageScriptTests.Convert(
            "<html><body><p id='b'>BEFORE</p><script>document.getElementById('b').textContent = 'PLUGIN';</script></body></html>");
        Assert.Contains("PLUGIN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEFORE", text, StringComparison.Ordinal);
    }
}
