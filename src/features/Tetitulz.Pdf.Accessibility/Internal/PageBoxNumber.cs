// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Resolving one page-box coordinate to a <b>finite single-precision</b> user-space value.
    /// </summary>
    /// <remarks>
    /// <para><c>PdfArtifactMarker</c> and <c>ContentStreamTagger</c> each read <c>/CropBox</c> else
    /// <c>/MediaBox</c> and pull <c>y0</c>/<c>y1</c> out of it, and they disagreed on one step:
    /// whether the value is still finite <b>after</b> the cast to <c>float</c>.</para>
    ///
    /// <para><b>A double can be finite and still overflow a float.</b> <c>/MediaBox [0 0 612 1e40]</c>
    /// passes <c>double.IsInfinity</c> and becomes <c>+Infinity</c> on the cast. The tagger re-tested
    /// and refused; the marker returned <c>true</c> with an infinite top, and the damage ran a long
    /// way from the cast:</para>
    ///
    /// <list type="bullet">
    ///   <item><c>top - bottom</c> is <c>+Infinity</c>, and <c>+Inf &gt; 0</c> is true, so the
    ///   extent read reported <b>success</b>.</item>
    ///   <item>The header/footer overlap guard compares <c>header + footer &gt;= height</c>, which
    ///   is false against an infinite height, so the page was processed.</item>
    ///   <item>The classifier's header arm evaluates <c>top - y</c> — <c>+Infinity</c> for every
    ///   line, never within the margin — so <b>no line was ever classified as a header</b>, while
    ///   the footer arm went on working. Headers silently stopped being marked as artifacts.</item>
    ///   <item><c>PageTagOptions.PageHeight</c> was set to <c>+Infinity</c> and handed to the
    ///   tagger — the very value the tagger's own resolver would have refused.</item>
    /// </list>
    ///
    /// <para>The tagger's stricter answer is the one kept: a coordinate that cannot be expressed is
    /// not a coordinate, and refusing the page's header/footer classification is a bounded loss
    /// where an unbounded band is not.</para>
    /// </remarks>
    internal static class PageBoxNumber
    {
        /// <summary>
        /// Resolves <paramref name="obj"/> to a finite <see cref="float"/>, reporting through
        /// <see cref="AccessibilityDiagnostics"/> and returning <c>false</c> when it cannot.
        /// </summary>
        /// <param name="operation">Diagnostic prefix — each caller keeps its own.</param>
        /// <param name="context">
        /// Prepended to every message, so a caller that knows <i>which page</i> it is reading can
        /// say so and one that does not can pass an empty string.
        /// </param>
        /// <param name="doc">The document, for resolving an indirect box element.</param>
        /// <param name="obj">The value to resolve — typically one element of a page box array.</param>
        /// <param name="boxName">
        /// Which box is being read (<c>/MediaBox</c>, <c>/CropBox</c>, …), named in the diagnostic.
        /// </param>
        /// <param name="value">The resolved value, or 0 when this returns <c>false</c>.</param>
        internal static bool TryResolveFinite(
            PdfDocument doc, PdfObject obj, string operation, string context, string boxName,
            out float value)
        {
            value = 0f;
            if (obj == null) return false;

            var number = obj as PdfNumber;
            if (number == null)
            {
                try
                {
                    number = doc?.ResolveObject(obj) as PdfNumber;
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(operation,
                        context + "a " + boxName + " coordinate could not be resolved", ex);
                    return false;
                }
            }

            if (number == null)
            {
                AccessibilityDiagnostics.Report(operation,
                    context + "a " + boxName + " coordinate is not a number");
                return false;
            }

            double d = number.DoubleValue;
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                AccessibilityDiagnostics.Report(operation,
                    context + "a " + boxName + " coordinate is not finite");
                return false;
            }

            value = (float)d;
            if (!float.IsNaN(value) && !float.IsInfinity(value)) return true;

            // A double that is finite but outside float range collapses to ±Infinity on the cast,
            // which would silently un-bound the band. This is the step the two copies disagreed on.
            AccessibilityDiagnostics.Report(operation,
                context + "a " + boxName + " coordinate is too large to express as a "
                + "single-precision user-space coordinate");
            value = 0f;
            return false;
        }
    }
}
