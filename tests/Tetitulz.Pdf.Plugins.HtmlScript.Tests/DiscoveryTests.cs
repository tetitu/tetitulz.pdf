// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// A page's own JavaScript runs only when the host registers the engine by name: discovery passes
/// <see cref="JintHtmlScriptPlugin"/> over (<see cref="PdfPluginExplicitRegistrationAttribute"/>), as it
/// does the document-script engine.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class DiscoveryTests
{
    [Fact]
    public void DiscoveryNeverEnablesPageScripts_OnlyRegisteringTheEngineByNameDoes()
    {
        var plugin = new JintHtmlScriptPlugin();
        PdfPluginHost.Instance.Unregister(plugin.Name);
        try
        {
            Assert.Equal(0, PdfPluginHost.Instance.RegisterFromAssembly(typeof(JintHtmlScriptPlugin).Assembly));
            Assert.Empty(PdfPluginHost.Instance.GetPlugins<IHtmlScriptPlugin>());
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }

        // Control: registered by name, a script runs.
        var (text, _) = PageScriptTests.Convert(
            "<html><body><p id='a'>BEFORE</p><script>document.getElementById('a').textContent = 'RAN';</script></body></html>");
        Assert.Contains("RAN", text, StringComparison.Ordinal);
    }
}
