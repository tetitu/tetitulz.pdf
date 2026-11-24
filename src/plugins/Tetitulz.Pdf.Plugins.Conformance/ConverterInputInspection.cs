// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// What a converter must know about its input <b>before</b> rewriting it: whether it is
    /// encrypted, and whether it carries a signature the rewrite would destroy.
    /// </summary>
    /// <remarks>
    /// <para>Both converters in this assembly rewrite the whole file through
    /// <c>PdfSanitizer.Sanitize</c>, which renumbers every object. That has two consequences a
    /// caller must be told about rather than discover:</para>
    /// <list type="bullet">
    ///   <item>An <b>encrypted</b> input makes <c>Sanitize</c> throw
    ///   <c>PdfEncryptedDocumentException</c> — out of a type the caller never invoked, while the
    ///   result object designed to carry blockers is never returned. Both PDF/A (A01) and PDF/X
    ///   (X01) forbid encryption outright, so "encrypted" is a first-class conformance blocker,
    ///   not an unexpected error.</item>
    ///   <item>A <b>signed</b> input is renumbered like any other, so its <c>/ByteRange</c> no
    ///   longer covers what was signed and the signature verifies as <i>corrupt</i> rather than as
    ///   modified.</item>
    /// </list>
    /// <para>One implementation, for <c>PdfXConverter</c> and the PDF/A converter alike, so the two
    /// converters cannot answer differently for one file.</para>
    /// </remarks>
    internal static class ConverterInputInspection
    {
        /// <summary>
        /// Reads <paramref name="input"/> without consuming it (the position is restored) and
        /// reports what a whole-file rewrite would do to it.
        /// </summary>
        /// <remarks>
        /// <para>Signature detection is deliberately <b>structural rather than cryptographic</b>:
        /// an <c>/AcroForm</c> field of <c>/FT /Sig</c> with a <c>/V</c>. Verifying would need
        /// <c>Tetitulz.Pdf.Signing</c>, and the question here is not "is this signature valid?" but
        /// "is there something conversion would destroy?" — for which the presence of the value is
        /// the answer, valid or not. The walk follows <c>/Kids</c> because a signature field is
        /// routinely a child of a parent field node.</para>
        /// <para>An encrypted document exposes no catalog (<c>PdfDocument.Open</c> stops at the
        /// trailer), so the signature walk is simply not reached for one — which is correct: the
        /// encryption refusal fires first and is the more specific answer.</para>
        /// </remarks>
        /// <returns><c>false</c> when the stream could not be read as a PDF at all.</returns>
        internal static bool TryInspect(Stream input, out bool isEncrypted, out bool isSigned,
                                        out string error)
        {
            isEncrypted = false;
            isSigned = false;
            error = null;

            long start = input.CanSeek ? input.Position : 0;
            try
            {
                using var doc = PdfDocument.Open(input);
                isEncrypted = doc.IsEncrypted;
                if (!isEncrypted)
                    isSigned = HasSignatureValue(doc);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (input.CanSeek) input.Position = start;
            }
        }

        /// <summary>
        /// True when the document carries a signature a whole-file rewrite would invalidate.
        /// </summary>
        /// <remarks>
        /// <para>One implementation, in <c>Core.Document.PdfSignatureDetection</c> —
        /// the same one <c>PdfOptimizer</c> and <c>PdfEncryptedRoundTrip</c> use.</para>
        ///
        /// <para><b>A certification signature counts.</b> The shared walk tests <c>/Perms</c> first,
        /// before the field walk runs at all: a document signed through <c>/Root → /Perms /DocMDP</c>
        /// need not list its field in <c>/Fields</c>, and a walk beginning at <c>/AcroForm /Fields</c>
        /// would call it <b>unsigned</b> — <c>PdfAConverter</c> and <c>PdfXConverter</c> would then
        /// skip their blocker, run <c>PdfSanitizer.Sanitize</c>, renumber every object and destroy
        /// the signature with no warning. One budget too, so a field tree whose signature field sits
        /// deep answers the same here as in Base.</para>
        /// </remarks>
        private static bool HasSignatureValue(PdfDocument doc)
            => PdfSignatureDetection.HasSignature(doc.Trailer, n => doc.ResolveObject(new PdfReference(n, 0)));
    }
}
