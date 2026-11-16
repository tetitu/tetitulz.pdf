// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// One document's script environment, shared by every <see cref="PdfJsExecutionContext"/> that
    /// carries it: what a document-level script defines is visible to the field and page scripts run
    /// after it, as a viewer runs them.
    /// </summary>
    /// <remarks>
    /// <para>Base holds no engine. The registered <see cref="IJavaScriptPlugin"/> keeps its
    /// environment in <see cref="EngineState"/> and bounds it there — the time and memory a document's
    /// scripts may spend together are the plugin's to enforce.</para>
    /// <para>A scope belongs to one document and one thread at a time; the plugin serialises the
    /// executions that share it.</para>
    /// </remarks>
    public sealed class PdfJsDocumentScope
    {
        /// <summary>The engine's own state for this document; opaque to Base.</summary>
        public object EngineState { get; set; }
    }
}
