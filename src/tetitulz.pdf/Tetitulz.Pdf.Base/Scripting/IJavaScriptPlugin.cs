// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// Plugin that executes a PDF document's JavaScript (ISO 32000-1 §12.6.4.16) against a
    /// <see cref="PdfJsExecutionContext"/>. Register an implementation to make
    /// <c>/S /JavaScript</c> actions, AcroForm calculations and field validation actually run;
    /// <b>with no plugin registered the library executes no script at all</b> and says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a plugin.</b> Running JavaScript needs an ECMAScript interpreter; carried by
    /// <c>Tetitulz.Pdf.Base</c> as a package reference (<c>Jint</c>), every consumer of Base,
    /// including all four feature DLLs, most plugins and the WinForms viewer's <c>net462</c> leg,
    /// would ship a full script engine for a feature most of them never invoke.
    /// The criterion in CLAUDE.md ("What makes something a plugin rather than part of Base") says a
    /// plugin earns its place by keeping a dependency out of the core package graph, which is
    /// exactly this case and the same call already made for PostScript, OCR and the crypto stack.
    /// <c>Tetitulz.Pdf.Plugins.JavaScript</c> ships the Jint-backed implementation.
    /// </para>
    /// <para>
    /// <b>Why the contract lives here and not in <c>Core/Plugins/</c>,</b> where the other plugin
    /// contracts live: it depends on <see cref="PdfJsExecutionContext"/>, a Base type, and Core
    /// cannot reference Base. That is the same reason <c>ISignPlugin</c> and
    /// <c>ISignatureVerifyPlugin</c> live in <c>Tetitulz.Pdf.Signing.Plugins</c> rather than in
    /// Core. Keeping the context in Base is what lets <c>PdfActionRunner</c>'s signatures stay
    /// unchanged.
    /// </para>
    /// <para>
    /// <b>Dispatch.</b> <see cref="PluginJavaScriptRunner"/> is the single site that consults the
    /// host, so the engine cannot be live on one code path and absent on another. The first
    /// registered plugin wins. A plugin that throws is reported and treated as "did not run" — a
    /// broken or absent engine degrades behaviour, it never breaks the caller.
    /// </para>
    /// <para>
    /// <b>Untrusted input.</b> A document's JavaScript is attacker-controlled. An implementation
    /// MUST bound execution — time, memory and recursion at minimum — and MUST NOT expose file,
    /// network or process access to the script.
    /// </para>
    /// <para><b>Thread safety.</b> <see cref="TryExecute"/> may be called concurrently for
    /// different documents and must not share mutable engine state across calls unless it
    /// serialises them itself.</para>
    /// </remarks>
    public interface IJavaScriptPlugin : IPdfPlugin
    {
        /// <summary>
        /// Executes <paramref name="script"/> against <paramref name="context"/>.
        /// </summary>
        /// <param name="script">The script source. May be null or empty, in which case the
        /// implementation should return <c>true</c> having done nothing.</param>
        /// <param name="context">The execution context to read field/event state from and write
        /// results back to. Never null when called through
        /// <see cref="PluginJavaScriptRunner"/>.</param>
        /// <returns>
        /// <c>true</c> if this plugin handled the script — <b>including</b> when the script itself
        /// threw, in which case the failure belongs on
        /// <see cref="PdfJsExecutionContext.Error"/> rather than being reported as "not handled".
        /// <c>false</c> to decline, so the caller treats the script as not executed.
        /// </returns>
        bool TryExecute(string script, PdfJsExecutionContext context);
    }
}
