// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Resolve-then-cast: follow an indirect reference, then narrow to the type the caller wants.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the order matters.</b> §7.3.10 permits any value in a dictionary to be indirect,
    /// and <see cref="PdfDictionary"/>'s indexer does not follow a reference — so
    /// <c>dict[key] as PdfDictionary</c> yields <c>null</c> for a value written as <c>n 0 R</c>,
    /// which is indistinguishable from an absent key. Resolving first is the whole point, and it is
    /// the defect class the non-resolving-reads register was built around.</para>
    ///
    /// <para><b>Eight private copies of this existed across five files in this assembly</b>, and
    /// they disagreed about what to do when resolution <i>throws</i>: five caught the exception and
    /// reported it through <see cref="AccessibilityDiagnostics"/>, two let it escape into a public
    /// entry point, where a damaged file would surface as a crash rather than as a diagnostic. That
    /// is the difference this type removes.</para>
    ///
    /// <para><b>One thing that looked like a divergence and is not</b>, recorded so it is not
    /// re-raised: <c>PdfFontEmbeddingRemediator</c>'s copy had no <c>obj is PdfDictionary direct</c>
    /// fast path, which reads like "a direct dictionary is dropped". It is not —
    /// <c>PdfParser.ResolveObject</c> begins <c>while (obj is PdfReference …)</c>, so a direct
    /// object is returned unchanged. The fast path is an optimisation, not a correctness rule.</para>
    ///
    /// <para>This lives in the feature rather than in Core because the reporting sink it needs
    /// (<see cref="AccessibilityDiagnostics"/>) is internal to the feature. There is nothing to
    /// share downward: every caller is in this assembly.</para>
    /// </remarks>
    internal static class Resolve
    {
        /// <summary>
        /// The object <paramref name="obj"/> names, or <paramref name="obj"/> itself when it is
        /// already direct. Returns <c>null</c> when resolution fails, reporting the reason.
        /// </summary>
        /// <param name="operation">
        /// Names the caller in the diagnostic — conventionally <c>"Type.Method"</c>, so a reader of
        /// the sink can tell which walk gave up rather than only that one did.
        /// </param>
        /// <param name="doc">The document to resolve through. A null document resolves nothing.</param>
        /// <param name="obj">The possibly-indirect object.</param>
        internal static PdfObject Value(PdfDocument doc, PdfObject obj, string operation)
        {
            if (obj == null) return null;
            if (!(obj is PdfReference)) return obj;
            if (doc == null) return null;

            try
            {
                return doc.ResolveObject(obj);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(operation,
                    "an indirect object could not be resolved; it is treated as absent", ex);
                return null;
            }
        }

        /// <summary>The value as a dictionary, resolved first, or <c>null</c>.</summary>
        internal static PdfDictionary Dict(PdfDocument doc, PdfObject obj, string operation)
            => Value(doc, obj, operation) as PdfDictionary;

        /// <summary>The value as an array, resolved first, or <c>null</c>.</summary>
        internal static PdfArray Array(PdfDocument doc, PdfObject obj, string operation)
            => Value(doc, obj, operation) as PdfArray;

        /// <summary>The value as a name, resolved first, or <c>null</c>.</summary>
        internal static PdfName Name(PdfDocument doc, PdfObject obj, string operation)
            => Value(doc, obj, operation) as PdfName;

        /// <summary>The value as a number, resolved first, or <c>null</c>.</summary>
        internal static PdfNumber Number(PdfDocument doc, PdfObject obj, string operation)
            => Value(doc, obj, operation) as PdfNumber;
    }
}
