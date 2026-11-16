// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Reads a page's <c>/Contents</c> — one stream or an array of them — as the single content
    /// stream §7.7.3.3 says it is.
    /// </summary>
    /// <remarks>
    /// <para><b>The separator is required, not cosmetic.</b> §7.7.3.3: "the division between streams
    /// may occur only at the boundaries between lexical tokens", and a conforming reader must insert
    /// white space when it concatenates. Joining <c>… 10 0</c> to <c>0 10 cm</c> with nothing between
    /// yields the token <c>00</c> and a different transform.</para>
    ///
    /// <para><b>Six places did this.</b> <c>PdfPage.GetContentStreamData</c>,
    /// <c>Plugins.Conformance.PdfUaChecker.PageContentBytes</c>,
    /// <c>Plugins.Maintenance.FontGlyphUsageScanner</c>, <c>…ImagePlacementScanner</c>,
    /// <c>…PdfOptimizer.ConcatenateContentStreams</c> and
    /// <c>Accessibility.ContentStreamTagger.ConcatenatePageContent</c>. The four that share the
    /// lenient contract — skip a part that will not decode, keep what does — call
    /// <see cref="Concatenate"/>. The other two do not, and why is written where they are; see the
    /// note below.</para>
    ///
    /// <para><b>The decode is a delegate because it is the one thing that legitimately varies.</b>
    /// Core throws on a damaged part, the Maintenance scanners cap each part at
    /// <c>OptimizationOptions.MaxStreamDecodeBytes</c>, and the accessibility tagger charges a
    /// shrinking budget per part. Folding any of those into a single policy here would have made
    /// one caller's contract everyone's.</para>
    ///
    /// <para><b>Two deliberate non-callers, for the same reason as each other.</b>
    /// <c>PdfOptimizer</c> and <c>ContentStreamTagger</c> both <i>refuse</i> rather than skip when a
    /// part will not decode — they produce the page's new content, so keeping the parts they could
    /// read would silently drop the rest — and both name the part that failed. The tagger
    /// additionally interleaves a ceiling check <i>between</i> parts, so nothing can wrap a method
    /// that returns a finished array. <see cref="Parts"/> is available to them and is not enough on
    /// its own, because the resolve failure they report is per index.</para>
    ///
    /// <para><b>A trailing separator after the last part is deliberate and preserved.</b> All four
    /// merged copies emitted one; a single-stream <c>/Contents</c> gets none, because there is no
    /// join. Content streams tolerate trailing white space, but the byte counts are asserted
    /// elsewhere, so this is behaviour rather than tidiness.</para>
    ///
    /// <para>A leaf by construction — no static fields, so no type initializer.</para>
    /// </remarks>
    public static class PdfPageContent
    {
        /// <summary>
        /// The white space inserted between concatenated parts (§7.7.3.3). A newline rather than a
        /// space so a fragment ending in a comment cannot swallow the next fragment's first line.
        /// </summary>
        public const byte PartSeparator = (byte)'\n';

        /// <summary>
        /// The page's <c>/Contents</c> parts in drawing order: one element for a single stream, the
        /// array's stream elements for an array, empty when there is no <c>/Contents</c> or it is
        /// neither.
        /// </summary>
        /// <param name="page">The page dictionary. A <c>PdfPage</c> is deliberately not required —
        /// an audit that walks an encrypted document by hand has only dictionaries.</param>
        /// <param name="resolve">
        /// Follows an indirect reference. <c>/Contents</c> and its elements are almost always
        /// indirect, and <c>PdfDictionary</c>'s indexer does not follow one — unresolved, a page
        /// reads as having no content at all.
        /// </param>
        /// <param name="isArray">
        /// Whether <c>/Contents</c> was an <i>array</i>. The two shapes are separated because a
        /// single stream takes no separator.
        /// </param>
        public static IReadOnlyList<PdfStream> Parts(
            PdfDictionary page, Func<PdfObject, PdfObject> resolve, out bool isArray)
        {
            isArray = false;
            if (page == null) return Array.Empty<PdfStream>();

            PdfObject contents = page[PdfName.Contents];
            if (contents == null) return Array.Empty<PdfStream>();
            if (resolve != null) contents = resolve(contents);

            if (contents is PdfStream single) return new[] { single };

            if (!(contents is PdfArray array)) return Array.Empty<PdfStream>();

            isArray = true;
            var parts = new List<PdfStream>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                PdfObject item = array[i];
                if (!(item is PdfStream) && resolve != null) item = resolve(item);
                // A null or non-stream element is legal padding in a damaged file. It contributes
                // nothing, so no offset moves.
                if (item is PdfStream part) parts.Add(part);
            }
            return parts;
        }

        /// <summary>
        /// The page's content stream: every part decoded through <paramref name="decode"/> and
        /// joined with <see cref="PartSeparator"/>.
        /// </summary>
        /// <param name="page">The page dictionary whose <c>/Contents</c> is read.</param>
        /// <param name="resolve">
        /// Follows an indirect reference. <c>/Contents</c> is routinely written as one, and the
        /// dictionary indexer does not follow it, so an unresolved read is indistinguishable from
        /// a page with no content at all.
        /// </param>
        /// <param name="decode">
        /// Decodes one part, or returns <c>null</c> to skip it. It may throw, and callers whose
        /// contract is to propagate that — <c>PdfPage.GetContentStreamData</c> does — simply hand
        /// in a decoder that throws.
        /// </param>
        /// <returns>
        /// The concatenated bytes, or <c>null</c> when the page has no <c>/Contents</c> at all —
        /// which every merged copy distinguished from an empty one, because an absent
        /// <c>/Contents</c> is a legal blank page and an empty one is a producer defect.
        /// </returns>
        public static byte[] Concatenate(
            PdfDictionary page, Func<PdfObject, PdfObject> resolve, Func<PdfStream, byte[]> decode)
        {
            if (decode == null) throw new ArgumentNullException(nameof(decode));

            IReadOnlyList<PdfStream> parts = Parts(page, resolve, out bool isArray);
            if (parts.Count == 0 && !isArray) return null;

            if (!isArray) return decode(parts[0]);

            // Each part decodes under MaxDecompressedBytes on its own, but their SUM is unbounded: a
            // /Contents array repeating one stream, or listing thousands, concatenates past 2 GiB —
            // where this MemoryStream threw an untyped IOException ("Stream was too long") only after
            // allocating it all. Refuse as a PdfException when the page's combined content would cross
            // the same ceiling a single decode does, so the MemoryStream never exceeds it (F2). A real
            // page's content is kilobytes; nothing legitimate approaches 512 MiB.
            long ceiling = Constants.PdfLimits.MaxDecompressedBytes;
            long total = 0;
            using var combined = new MemoryStream();
            for (int i = 0; i < parts.Count; i++)
            {
                byte[] decoded = decode(parts[i]);
                if (decoded == null || decoded.Length == 0) continue;
                long projected = total + (long)decoded.Length + 1;   // +1 for the separator below
                if (projected > ceiling)
                    throw new PdfException(
                        "The page's concatenated /Contents exceeds the " + ceiling +
                        "-byte decode limit (PdfLimits.MaxDecompressedBytes); its array has too many or too large parts.");
                combined.Write(decoded, 0, decoded.Length);
                combined.WriteByte(PartSeparator);
                total = projected;
            }
            return combined.ToArray();
        }
    }
}
