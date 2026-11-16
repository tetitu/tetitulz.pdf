// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Which transfer-function keys an <c>/ExtGState</c> sets: <c>/TR</c> or <c>/TR2</c> present
    /// with any value but <c>/Default</c>. PDF 2.0's deprecation report and PDF/X's "no transfer
    /// curves" (X14) ask the same question.
    /// </summary>
    /// <remarks>
    /// Presence is not deviation: <c>/TR2 /Default</c> asks for the device's own transfer function
    /// (ISO 32000-1 Table 58), which is what an absent key means. <c>/Identity</c> is counted. It
    /// changes nothing, but it is still a transfer function the file sets, and reporting it is the
    /// stricter of the two readings.
    /// </remarks>
    internal static class TransferFunctionEntries
    {
        private static readonly PdfName[] Keys = { PdfName.TR, PdfName.TR2 };

        /// <summary>The keys of <paramref name="gs"/> that set a transfer function, in order.</summary>
        internal static IEnumerable<PdfName> Declared(PdfDocument doc, PdfDictionary gs)
        {
            foreach (var key in Keys)
            {
                var value = doc.ResolveObject(gs[key]) ?? gs[key];
                if (value != null && !(value is PdfName n && n.Value == "Default"))
                    yield return key;
            }
        }
    }
}
