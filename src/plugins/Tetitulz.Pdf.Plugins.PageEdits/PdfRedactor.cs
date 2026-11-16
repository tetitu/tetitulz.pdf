// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// Draws opaque filled rectangles ("cover boxes") over regions of a PDF page.
    ///
    /// <para><b>SECURITY LIMITATION — this is a VISUAL cover, NOT a true redaction.</b>
    /// The cover box is appended on top of the existing page content; the underlying
    /// text-showing operators (<c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c>) and image XObjects
    /// are NOT removed from the content stream. The hidden content remains fully
    /// recoverable by anything that reads the raw content stream (copy/paste,
    /// <c>PdfOperations.ExtractText</c>, a different viewer, <c>pdftotext</c>, etc.).
    /// Do NOT rely on this class to remove confidential data — it only changes what is
    /// visible when the page is rendered normally.</para>
    ///
    /// <para>For genuine data removal use <see cref="RedactRegions"/>, which physically
    /// deletes the covered text glyphs / images and writes a full single-revision PDF.</para>
    /// </summary>
    public static class PdfRedactor
    {
        /// <summary>
        /// TRUE redaction: physically removes the text glyphs and images that fall inside
        /// each region (preserving the layout of surviving text), drops intersecting
        /// annotations, stamps an opaque cover box, and writes a full single-revision PDF
        /// so the removed data is recoverable from no revision.
        ///
        /// <para>Fails closed (<see cref="PdfRedactionException"/>) rather than
        /// risk a leak when content cannot be safely removed — notably forms nested past the depth
        /// cap, a pattern or soft mask drawing text, a region over a paint through one drawing an
        /// image, or an encrypted document. See
        /// <see cref="PdfRedactionEngine"/> for the full list of guarantees and documented limits.</para>
        /// </summary>
        public static void RedactRegions(
            Stream input,
            Stream output,
            IEnumerable<RedactionRegion> redactions)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (redactions == null) throw new ArgumentNullException(nameof(redactions));

            var areas = new List<PdfRedactionArea>();
            foreach (var r in redactions)
            {
                if (r == null)
                    throw new ArgumentException("redactions contains a null region.", nameof(redactions));
                areas.Add(new PdfRedactionArea
                {
                    PageIndex = r.PageIndex,
                    X = (float)r.X,
                    Y = (float)r.Y,
                    Width = (float)r.Width,
                    Height = (float)r.Height,
                    FillR = (float)r.FillR,
                    FillG = (float)r.FillG,
                    FillB = (float)r.FillB,
                });
            }

            PdfRedactionEngine.Redact(input, output, areas);
        }

        /// <summary>
        /// TRUE redaction driven by a <b>text search</b>: finds every occurrence of the patterns
        /// and removes it, rather than requiring the caller to supply rectangles.
        /// </summary>
        /// <param name="input">The PDF to redact. Not disposed.</param>
        /// <param name="output">Where the redacted PDF is written. Not disposed.</param>
        /// <param name="patterns">Literals, or regular expressions when the options say so.</param>
        /// <param name="options">Search policy; null uses the defaults.</param>
        /// <returns>What was found and removed.</returns>
        /// <remarks>
        /// <para>A front end over <see cref="RedactRegions"/>'s engine, not a second one — so every
        /// refusal it makes still applies, including an encrypted document and a form shared with a
        /// page the caller did not redact.</para>
        /// <para><b>Look before you destroy:</b> <see cref="FindTextMatches"/> reports exactly what
        /// this would remove, without changing anything.</para>
        /// <para>The matches leave the document information, the outline's titles and the XMP packet
        /// too (<see cref="TextRedactionOptions.IncludeDocumentText"/>; where, in
        /// <see cref="PdfTextRedactor.LastDocumentTextRemoved"/>), which the review step does not list.</para>
        /// </remarks>
        public static IReadOnlyList<TextRedactionMatch> RedactText(
            Stream input, Stream output, IEnumerable<string> patterns,
            TextRedactionOptions options = null)
            => PdfTextRedactor.Redact(input, output, patterns, options);

        /// <summary>
        /// Reports what <see cref="RedactText"/> would remove, without changing the document.
        /// </summary>
        /// <param name="input">The PDF to search. Not disposed.</param>
        /// <param name="patterns">Literals, or regular expressions when the options say so.</param>
        /// <param name="options">Search policy; null uses the defaults.</param>
        public static IReadOnlyList<TextRedactionMatch> FindTextMatches(
            Stream input, IEnumerable<string> patterns, TextRedactionOptions options = null)
            => PdfTextRedactor.FindMatches(input, patterns, options);

        /// <summary>
        /// Draws an opaque cover box over one or more regions on the specified pages.
        ///
        /// <para><b>This does NOT remove the underlying content.</b> See the type-level
        /// remarks: text and images beneath the box remain extractable. Use this only
        /// to hide content visually, never to delete confidential data.</para>
        /// </summary>
        public static void CoverRegions(
            Stream input,
            Stream output,
            IEnumerable<RedactionRegion> redactions)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (redactions == null) throw new ArgumentNullException(nameof(redactions));

            var byPage = new Dictionary<int, List<RedactionRegion>>();
            foreach (var r in redactions)
            {
                if (r == null)
                    throw new ArgumentException(
                        "redactions contains a null region.", nameof(redactions));
                if (r.PageIndex < 0)
                    throw new ArgumentOutOfRangeException(nameof(redactions),
                        $"RedactionRegion.PageIndex must be >= 0 (got {r.PageIndex}).");
                // The writer puts a non-finite number as 0, so a NaN width would draw `x y 0 h re f`,
                // covering nothing — refused, as RedactRegions refuses it.
                Core.Util.NumericArguments.RequireFinite(r.X, nameof(redactions), nameof(RedactionRegion.X));
                Core.Util.NumericArguments.RequireFinite(r.Y, nameof(redactions), nameof(RedactionRegion.Y));
                Core.Util.NumericArguments.RequireFinite(r.Width, nameof(redactions), nameof(RedactionRegion.Width));
                Core.Util.NumericArguments.RequireFinite(r.Height, nameof(redactions), nameof(RedactionRegion.Height));
                Core.Util.NumericArguments.RequireFinite(r.FillR, nameof(redactions), nameof(RedactionRegion.FillR));
                Core.Util.NumericArguments.RequireFinite(r.FillG, nameof(redactions), nameof(RedactionRegion.FillG));
                Core.Util.NumericArguments.RequireFinite(r.FillB, nameof(redactions), nameof(RedactionRegion.FillB));
                if (!byPage.ContainsKey(r.PageIndex))
                    byPage[r.PageIndex] = new List<RedactionRegion>();
                byPage[r.PageIndex].Add(r);
            }

            // One revision for every page: a revision per page would rewrite the whole file the one
            // before wrote, pages².
            var builders = new Dictionary<int, Action<PdfContentStreamBuilder>>();
            foreach (var kvp in byPage)
            {
                var regions = kvp.Value;
                builders[kvp.Key] = builder => BuildRedactionContent(builder, regions);
            }
            PdfPageContentAppender.AppendContentToPages(input, output, builders, compressStream: false);
        }

        /// <summary>
        /// Deprecated alias for <see cref="CoverRegions"/>.
        ///
        /// <para><b>Misleading name — this does NOT remove the underlying content.</b>
        /// Despite the "redact" name, the covered text/images remain extractable from
        /// the content stream (see the type-level remarks). It is retained for source
        /// compatibility and forwards to <see cref="CoverRegions"/>. New code should
        /// call <see cref="CoverRegions"/> so the cover-only semantics are explicit.</para>
        /// </summary>
        [Obsolete("Redact only DRAWS AN OPAQUE COVER; it does NOT remove the underlying " +
                  "text/image data, which stays extractable from the content stream. " +
                  "For true content removal call RedactRegions. " +
                  "If you really do want a cover box, call CoverRegions, which is named for it.")]
        public static void Redact(
            Stream input,
            Stream output,
            IEnumerable<RedactionRegion> redactions)
            => CoverRegions(input, output, redactions);

        private static void BuildRedactionContent(
            PdfContentStreamBuilder builder,
            List<RedactionRegion> regions)
        {
            builder.SaveState();

            foreach (var r in regions)
            {
                builder.SetFillColorRgb(
                    (float)r.FillR, (float)r.FillG, (float)r.FillB);
                builder.Rectangle(r.X, r.Y, r.Width, r.Height);
                builder.Fill();
            }

            builder.RestoreState();
        }
    }
}
