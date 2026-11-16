// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.JavaScript
{
    /// <summary>
    /// <see cref="IJavaScriptPlugin"/> backed by <see href="https://github.com/sebastienros/jint">Jint</see>,
    /// a pure-managed ECMAScript interpreter. Registering it makes a document's
    /// <c>/S /JavaScript</c> actions, AcroForm calculations and field validation actually run.
    ///
    /// Registration, by name only:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new JintJavaScriptPlugin());
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off unless the host turns it on.</b> The plugin carries
    /// <see cref="PdfPluginExplicitRegistrationAttribute"/>, so plugin discovery
    /// (<c>RegisterFromLoadedAssemblies</c>, <c>RegisterFromDirectory</c>) never enables it, and with
    /// no JavaScript plugin registered no document script runs. Rendering, extraction and form filling
    /// run none either way; scripts run only through the calls that say so
    /// (<c>PdfOperations.ExecuteOpenScripts</c>, <c>ExecuteDocumentScripts</c>, <c>PdfFieldScripts</c>,
    /// <c>PdfActionRunner</c>), and a script sees a document's attachments only when its context asks
    /// (<see cref="PdfJsExecutionContext.ExposeAttachments"/>).
    /// </para>
    /// <para>
    /// <b>Why this is a separate package.</b> Every consumer of <c>Tetitulz.Pdf.Base</c> — all four
    /// feature DLLs, most plugins and the WinForms viewer including its <c>net462</c> leg — would
    /// otherwise carry a full script interpreter for a feature most of them never invoke.
    /// </para>
    /// <para>
    /// <b>A document's scripts share one environment when their contexts share a
    /// <c>PdfJsDocumentScope</c></b>: a document-level <c>/Names /JavaScript</c> entry runs first and
    /// what it defines is visible to the field scripts after it, as in a viewer. The scope keeps one
    /// Jint engine, used by one execution at a time (<see cref="PdfJsEngine"/> serialises them), under
    /// a ceiling on what its scripts allocate together. A context without a scope gets an engine of
    /// its own, and nothing it defines outlives the call.
    /// </para>
    /// <para>
    /// <b>The sandbox is the point, not an implementation detail.</b> A PDF's JavaScript is
    /// attacker-controlled. <see cref="PdfJsEngine"/> bounds every execution with a timeout, a
    /// memory ceiling and a recursion limit, and exposes only the <c>app</c>/<c>doc</c>/<c>event</c>
    /// façades — no file, network, process or CLR-reflection access. Do not relax those to make a
    /// stubborn document work.
    /// </para>
    /// <para>
    /// <b>Failure semantics.</b> <see cref="TryExecute"/> returns <c>true</c> whenever the engine
    /// ran the script, <i>including</i> when the script threw or hit a limit — that failure is
    /// reported on <see cref="PdfJsExecutionContext.Error"/>. Returning <c>false</c> would mean
    /// "not handled", which the caller reports as no script having run at all, and the two must
    /// stay distinguishable.
    /// </para>
    /// </remarks>
    [PdfPluginExplicitRegistration]
    public sealed class JintJavaScriptPlugin : IJavaScriptPlugin
    {
        /// <inheritdoc/>
        public string Name => "Tetitulz.Plugins.JavaScript";

        /// <inheritdoc/>
        public string Description =>
            "Executes PDF document JavaScript (form actions and calculations) via Jint.";

        /// <inheritdoc/>
        public Version Version => new Version(1, 0, 0);

        /// <inheritdoc/>
        public void Initialize() { }

        /// <inheritdoc/>
        public void Shutdown() { }

        /// <inheritdoc/>
        public bool TryExecute(string script, PdfJsExecutionContext context)
        {
            if (context == null) return false;

            // PdfJsEngine.Execute already tolerates a null/empty script and records any script
            // failure on context.Error, so it never throws out of here — which is what keeps
            // "the engine ran it" distinct from "no engine was available".
            PdfJsEngine.Execute(script, context);
            return true;
        }
    }
}
