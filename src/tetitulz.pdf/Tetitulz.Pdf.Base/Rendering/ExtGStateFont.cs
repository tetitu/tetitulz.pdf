// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// ISO 32000-1 §8.4.5 Table 58's ExtGState <c>/Font</c> entry, read once for both renderers.
    ///
    /// <para>Table 58 defines the entry as an <b>array of two elements</b>: an <b>indirect
    /// reference to a font dictionary</b>, and a size in unscaled text-space units. It is
    /// <i>not</i> a name to be looked up in <c>/Resources /Font</c> — that is what <c>Tf</c>'s
    /// operand is — and the two are not interchangeable, because the font a <c>gs</c> selects
    /// need not be a named resource of the page at all.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a shared type.</b> Read separately, the two renderers could take this
    /// entry in <i>mutually exclusive</i> shapes, so that exactly one of them selected the font for
    /// any given file. A reader accepting only a <see cref="PdfName"/> at index 0 as the resource
    /// name — the shape Table 58 does not define — would let a spec-conformant
    /// <c>/Font [5 0 R 12]</c> change the size and leave the <i>face</i> at whatever the last
    /// <c>Tf</c> had selected (or at none at all, when the stream has no <c>Tf</c>); one accepting
    /// only a <see cref="PdfReference"/> would ignore the other shape. A file written either way
    /// would render one way and export another, the disagreement total rather than partial.</para>
    ///
    /// <para><b>Both shapes are accepted, and the spec one is what the code is built around.</b>
    /// A reference is matched against the page's <c>/Resources /Font</c> to recover the resource
    /// name, because everything downstream of this — <see cref="FontCache"/> and every decode,
    /// width and glyph query in both renderers — is keyed by <c>(resource name, resources)</c>.
    /// The non-conformant name form is tolerated rather than dropped: a name in that slot can only
    /// mean a resource name, and refusing it would break files that render correctly with it. It
    /// is not <i>reported</i>, because tolerating it changes nothing about the output.</para>
    ///
    /// <para><b>The limitation is reported.</b> A font that is genuinely not among the page's font
    /// resources — legal, and the reason the entry exists — cannot be selected through a
    /// name-keyed cache, so it is left alone and named through
    /// <see cref="RenderDiagnosticKind.ContentStreamError"/> rather than silently drawn in the
    /// previous face. Both renderers report it, with their own prefix.</para>
    ///
    /// <para><b>A leaf by construction.</b> No static fields, so no type initializer: sharing it
    /// sideways between the two renderers cannot create the type-initialization edge that once
    /// crashed the Base test host.</para>
    /// </remarks>
    internal static class ExtGStateFont
    {
        /// <summary>
        /// Reads <c>/Font</c> out of an ExtGState dictionary.
        /// </summary>
        /// <param name="doc">The document, for reference resolution.</param>
        /// <param name="extGState">The resolved ExtGState dictionary named by <c>gs</c>.</param>
        /// <param name="resources">
        /// The resources in force for the content stream — the font reference is matched against
        /// its <c>/Font</c> sub-dictionary.
        /// </param>
        /// <param name="diagnosticPrefix">
        /// Prepended to any reported message, so a host can tell the two renderers apart
        /// (<c>""</c> for the raster path, <c>"SVG: "</c> for the exporter).
        /// </param>
        /// <param name="fontResourceName">
        /// The <c>/Resources /Font</c> key the entry selects, or <c>null</c> when the entry names
        /// no font this renderer can reach. A caller must leave the current font untouched on
        /// <c>null</c> rather than clearing it — §8.4.5 says the entry <i>sets</i> a font, never
        /// that it unsets one.
        /// </param>
        /// <param name="fontSize">The size, or <c>null</c> when the second element is not a number.</param>
        /// <returns><c>true</c> when the entry supplied a font, a size, or both.</returns>
        internal static bool TryRead(PdfDocument doc, PdfDictionary extGState, PdfDictionary resources,
                                     string diagnosticPrefix,
                                     out string fontResourceName, out float? fontSize)
        {
            fontResourceName = null;
            fontSize = null;

            PdfArray entry = PdfResolve.Array(doc, extGState, PdfName.Font);
            if (entry == null || entry.Count < 2) return false;

            // Both elements resolve. Table 58 requires an indirect reference for the first and
            // §7.3.10 permits one for the second; the two renderers each demanded a direct number
            // here, so `/Font [5 0 R 9 0 R]` set no size in either.
            if (PdfResolve.Value(doc, entry[1]) is PdfNumber size && MatrixGuard.TryFinite(size, out float finiteSize))
                fontSize = finiteSize;

            fontResourceName = ResolveResourceName(doc, entry[0], resources, diagnosticPrefix);
            return fontResourceName != null || fontSize != null;
        }

        private static string ResolveResourceName(PdfDocument doc, PdfObject fontEntry,
                                                  PdfDictionary resources, string diagnosticPrefix)
        {
            // Non-conformant shape, tolerated: a name here can only mean a /Resources /Font
            // key.
            if (fontEntry is PdfName asResourceName) return asResourceName.Value;

            PdfDictionary fontCategory = PdfResolve.Dict(doc, resources, PdfName.Font);
            if (fontCategory != null)
            {
                // The spec shape. Match the reference itself first — object AND generation
                // number, so two objects that differ only by generation cannot be confused.
                if (fontEntry is PdfReference reference)
                {
                    foreach (System.Collections.Generic.KeyValuePair<PdfName, PdfObject> kv in fontCategory)
                    {
                        if (kv.Value is PdfReference candidate && candidate.Equals(reference))
                            return kv.Key.Value;
                    }
                }

                // Failing that, match the font dictionary the entry resolves to. This is what
                // reaches a resource written as a direct dictionary, and a reference whose
                // generation number differs from the resource entry's.
                if (PdfResolve.Value(doc, fontEntry) is PdfDictionary target)
                {
                    foreach (System.Collections.Generic.KeyValuePair<PdfName, PdfObject> kv in fontCategory)
                    {
                        if (ReferenceEquals(PdfResolve.Value(doc, kv.Value), target))
                            return kv.Key.Value;
                    }
                }
            }

            RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                diagnosticPrefix + "ExtGState /Font names a font that is not among the current " +
                "/Resources /Font; the font in force was left unchanged (§8.4.5 permits a font " +
                "that is not a named resource, which a name-keyed font cache cannot select).");
            return null;
        }
    }
}
