// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Templating
{
    /// <summary>
    /// Discovery wrapper so a host holding only <c>Tetitulz.Pdf.Core</c> can see that templating is
    /// available, via <c>PdfPluginHost.RegisterFromLoadedAssemblies()</c>.
    ///
    /// <para><b>There is deliberately no <c>ITemplatePlugin</c> interface.</b> Core defines a plugin
    /// contract per capability, and every one of them exists because <i>Base</i> needs to call
    /// <i>out</i> to a plugin without referencing it — conformance validation is the worked example.
    /// Nothing in Base calls templating, so an interface here would be an abstraction with one
    /// implementation and no caller, which this repo's KISS rule rejects. Callers reference this
    /// assembly and call <see cref="PdfTemplateFiller"/> directly, exactly as they do
    /// <c>PdfMerger.Merge</c> or <c>PdfSanitizer.Sanitize</c>.</para>
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new TemplatingPlugin());
    /// </code>
    /// </summary>
    public sealed class TemplatingPlugin : IPdfPlugin
    {
        public string Name        => "Tetitulz.Plugins.Templating";
        public string Description => "Fills PDF templates: AcroForm fields and {{token}} placeholders, with RTL/Hebrew support.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }
    }
}
