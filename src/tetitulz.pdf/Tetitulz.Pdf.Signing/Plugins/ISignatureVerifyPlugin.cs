// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Plugins;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Plugins
{
    /// <summary>
    /// Plugin that verifies cryptographic signatures in a PDF.
    ///
    /// <para><b>Discovery contract: nothing dispatches this.</b>
    /// <see cref="PdfSigningOperations.VerifySignatures"/> and
    /// <see cref="PdfSigningOperations.VerifySignature"/> construct
    /// <see cref="PdfSignatureVerifier"/> directly and never consult
    /// <c>PdfPluginHost.GetPlugins&lt;ISignatureVerifyPlugin&gt;()</c>, so registering an
    /// implementation does not change what those methods return. A host that wants a custom
    /// verification policy must resolve the plugin itself:
    /// <c>PdfPluginHost.Instance.GetFirstPlugin&lt;ISignatureVerifyPlugin&gt;()?.VerifyAll(stream)</c>.
    /// </para>
    ///
    /// <para>This matches the nine discovery contracts <c>PdfPluginHost</c> names in its own
    /// remarks; it is stated here because that list enumerates only the contracts that live in
    /// <c>Core</c>, and the three that do not — this one, <see cref="ISignPlugin"/> (two of its
    /// four members dispatched) and <c>Base.Scripting.IJavaScriptPlugin</c> (fully dispatched) —
    /// can only document themselves. Registering a plugin whose hooks are never called is
    /// invisible at runtime, because the built-in path returns perfectly plausible results.</para>
    /// </summary>
    public interface ISignatureVerifyPlugin : IPdfPlugin
    {
        /// <summary>Verifies all signatures in <paramref name="pdfStream"/>.</summary>
        IReadOnlyList<SignatureVerificationResult> VerifyAll(Stream pdfStream);
    }
}
