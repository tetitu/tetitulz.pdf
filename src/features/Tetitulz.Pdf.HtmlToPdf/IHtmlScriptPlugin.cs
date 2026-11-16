// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Plugin that runs a page's own JavaScript for the <b>managed</b> engine
    /// (<see cref="HtmlRenderEngine.Managed"/>), so what is laid out is the page after its scripts
    /// have rearranged it — a jQuery ready handler moving a cell, adding a class, writing a value.
    /// With no plugin registered the managed engine lays out the page as written and <b>says so</b>
    /// when the page carries scripts.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a plugin.</b> Running script needs a JavaScript engine bound to a DOM —
    /// Jint and its AngleSharp bindings — and CLAUDE.md keeps Jint out of every feature ("Jint is in
    /// a plugin, and must stay there"). A plugin earns its place by keeping a dependency out of the
    /// core package graph; this one keeps three. <c>Tetitulz.Pdf.Plugins.HtmlScript</c> ships the
    /// implementation. It is <b>not</b> <c>Plugins.JavaScript</c>, which runs a PDF's document
    /// JavaScript and must not drag this assembly into every host that only wants that.</para>
    ///
    /// <para><b>The contract speaks strings, not DOM types.</b> The page goes in as HTML and comes
    /// back as the post-script DOM serialised as HTML, which the engine parses exactly as it parses
    /// any page. So the implementation's AngleSharp packages never meet this assembly's parser in a
    /// signature, and a host with no plugin runs byte-for-byte the code it ran before.</para>
    ///
    /// <para><b>Untrusted input is the implementation's problem, and it is a real one:</b> the
    /// scripts are arbitrary code from the page. An implementation MUST bound them — statements,
    /// recursion, memory, time and timer callbacks — and MUST NOT give them the network or the file
    /// system except through <see cref="HtmlScriptRequest.LoadScript"/>, which applies this engine's
    /// own rules (<see cref="HtmlToPdfOptions.RemoteResourceLoader"/> for remote sources, the base
    /// directory for local ones).</para>
    ///
    /// <para><b>Why the contract lives here</b> rather than in <c>Core/Plugins/</c>: it names
    /// <see cref="HtmlToPdfOptions"/>, a type in this assembly — the reason
    /// <see cref="IHtmlBrowserPlugin"/> lives here too.</para>
    /// </remarks>
    public interface IHtmlScriptPlugin : IPdfPlugin
    {
        /// <summary>
        /// Runs the scripts of <see cref="HtmlScriptRequest.Html"/> and returns the resulting DOM,
        /// serialised as HTML.
        /// </summary>
        /// <returns>
        /// The post-script document. A script that throws is the page's own error, not the
        /// plugin's: report it through <see cref="HtmlScriptRequest.Report"/> and return the DOM as
        /// the scripts left it, exactly as a browser keeps showing the page.
        /// </returns>
        /// <exception cref="HtmlToPdfException">
        /// The engine itself could not run. The managed engine reports it and lays out the page as
        /// written — the output is still a document, and the report says which one.
        /// </exception>
        string RunScripts(HtmlScriptRequest request);
    }

    /// <summary>What an <see cref="IHtmlScriptPlugin"/> is asked to run.</summary>
    public sealed class HtmlScriptRequest
    {
        internal HtmlScriptRequest(
            string html, HtmlToPdfOptions options,
            Func<string, string?> loadScript, Action<string> report)
        {
            Html = html;
            Options = options;
            LoadScript = loadScript;
            Report = report;
        }

        /// <summary>The page, as the caller supplied it. Never null.</summary>
        public string Html { get; }

        /// <summary>Conversion options. Never null.</summary>
        public HtmlToPdfOptions Options { get; }

        /// <summary>
        /// The text of the script a <c>&lt;script src&gt;</c> names, or <c>null</c> when it may not
        /// or cannot be loaded — in which case the engine has already reported why.
        /// </summary>
        /// <remarks>
        /// <b>The only way to reach a script outside the page.</b> It goes through the same paths as
        /// the engine's images and stylesheets: a remote URL only through
        /// <see cref="HtmlToPdfOptions.RemoteResourceLoader"/> (the SSRF-guarded fetch, under
        /// <see cref="HtmlToPdfOptions.MaxRemoteResources"/>), a local one only inside the base
        /// directory. A second copy of any of those rules is how one kind of subresource would end
        /// up fetched where the others are refused.
        /// </remarks>
        public Func<string, string?> LoadScript { get; }

        /// <summary>
        /// Reports something the scripts did not do as written — a script that threw, a limit that
        /// stopped one, a capability the engine lacks. Surfaces as a
        /// <c>ContentApproximated</c> diagnostic.
        /// </summary>
        public Action<string> Report { get; }
    }
}
