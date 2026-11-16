// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The single answer to "does this font carry an embedded program?", shared by PDF/A's A06,
    /// PDF/UA's UA25 and PDF/X's X04.
    /// </summary>
    /// <remarks>
    /// <para><b>The edges of the walk are where separate copies would disagree.</b> The walk
    /// follows <c>/DescendantFonts</c> for a composite font, otherwise looks for <c>/FontFile</c>,
    /// <c>/FontFile2</c> or <c>/FontFile3</c> on the <c>/FontDescriptor</c>, and:</para>
    ///
    /// <list type="bullet">
    ///   <item><b>A Type 3 font</b> is exempt. Its glyphs <i>are</i> content streams, held in
    ///   <c>/CharProcs</c>; there is no font program to embed. Without this branch a Type 3 font
    ///   with no <c>/FontDescriptor</c> would fall through to "no descriptor — not embedded" and
    ///   raise a <b>false</b> A06 on a conforming file.</item>
    ///
    ///   <item><b>A composite font whose <c>/DescendantFonts</c> does not resolve to an array</b>
    ///   is reported, never skipped: silence would be a <b>missing</b> A06, the worse direction
    ///   for a conformance checker.</item>
    ///
    ///   <item><b>A font dictionary with no <c>/Subtype</c></b> falls through to the descriptor
    ///   check, the answer that finds a real unembedded font; abandoning the check on it would
    ///   hide one.</item>
    ///
    ///   <item><b>Every descendant</b> is examined, not <c>descArr[0]</c> alone. §9.7.1 gives a
    ///   Type 0 font exactly one, so the difference is latent rather than live, but every caller
    ///   reads the same set.</item>
    ///
    ///   <item><b>A key is not a program.</b> The entry must resolve to a stream: a descriptor
    ///   naming an object the file does not hold (test file 6-2-11-4-1-t01-fail-a:
    ///   <c>/FontFile3 19 0 R</c> and no object 19) is not embedded.</item>
    /// </list>
    ///
    /// <para><b>The cycle guard is not optional and a depth cap does not replace it.</b> Nothing in
    /// the format stops <c>10 0 obj &lt;&lt; /Subtype /Type0 /DescendantFonts [10 0 R 11 0 R]
    /// &gt;&gt;</c>, and the parser hands back the same cached instance every time, so an unguarded
    /// walk repeats the frame forever — a <c>StackOverflowException</c> .NET Core cannot catch,
    /// killing the host process from a ~150-byte document. With two descendants per level a depth
    /// cap alone converts that crash into 2<sup>depth</sup> visits, so both are kept.</para>
    ///
    /// <para>Each caller keeps its own violation code, its own de-duplication and its own wording;
    /// only the traversal and the verdict live here — the shape <c>AcroFormFieldWalk</c> has in
    /// this assembly: one traversal, several consumers.</para>
    /// </remarks>
    internal static class FontEmbeddingProbe
    {
        /// <summary>
        /// A CIDFont may not itself be a Type 0 font, so a valid <c>/DescendantFonts</c> chain is
        /// one level deep. The cap is for invalid files; see the cycle-guard note above for why it
        /// does not stand alone.
        /// </summary>
        internal const int MaxDescendantFontDepth = 4;

        /// <summary>
        /// Invokes <paramref name="reportUnembedded"/> once per font in this font's tree that
        /// carries no embedded program, passing the font's label.
        /// </summary>
        /// <param name="font">The font dictionary to examine.</param>
        /// <param name="doc">Resolves indirect references.</param>
        /// <param name="reportUnembedded">Receives the label of each unembedded font found.</param>
        /// <param name="fallbackLabel">
        /// Used when the font names itself neither by <c>/BaseFont</c> nor by <c>/Name</c> — the
        /// resource key, where the caller has one, is more useful to a reader than a constant.
        /// </param>
        /// <param name="reportDiagnostic">Optional sink for the depth-cap refusal.</param>
        internal static void ReportUnembedded(
            PdfDictionary font,
            PdfDocument doc,
            Action<string> reportUnembedded,
            string fallbackLabel = null,
            Action<string> reportDiagnostic = null)
        {
            if (font == null || doc == null || reportUnembedded == null) return;

            WalkDescriptors(font, doc, (leaf, subtype, fd) =>
            {
                bool embedded = fd != null
                    && (IsProgram(doc, fd[PdfName.FontFile])
                     || IsProgram(doc, fd[PdfName.FontFile2])
                     || IsProgram(doc, fd[PdfName.FontFile3]));

                if (!embedded) reportUnembedded(Label(doc, leaf, fallbackLabel));
            }, reportDiagnostic, new HashSet<PdfDictionary>(), 0);
        }

        /// <summary>
        /// The traversal half of <see cref="ReportUnembedded"/>, for a caller that needs the
        /// <b>descriptor itself</b> — not just an embedded/not-embedded verdict — without
        /// re-deriving the Type0/Type3/cycle-guard/depth-cap handling. Invokes
        /// <paramref name="onLeaf"/> once per terminal font in <paramref name="font"/>'s tree with
        /// its resolved <c>/FontDescriptor</c> — <c>null</c> when the descriptor could not be
        /// determined (an unresolved <c>/DescendantFonts</c> array, or the depth cap was hit).
        /// Callers must treat a <c>null</c> descriptor as "cannot confirm anything about this font",
        /// never as "conforms" — <see cref="ReportUnembedded"/>'s own wrapper above does exactly
        /// that: <c>fd == null</c> makes <c>embedded</c> false, so the depth-cap and
        /// unresolved-descendants cases are reported as not embedded.
        /// A Type 3 font is skipped outright (§9.6.5 — its glyphs are content streams in
        /// <c>/CharProcs</c>; there is no font program to have a descriptor for), so
        /// <paramref name="onLeaf"/> is never called for one.
        /// </summary>
        internal static void WalkDescriptors(
            PdfDictionary font, PdfDocument doc,
            Action<PdfDictionary, string, PdfDictionary> onLeaf,
            Action<string> diagnostic,
            HashSet<PdfDictionary> visited, int depth)
        {
            if (font == null || doc == null || onLeaf == null || visited == null) return;
            if (!visited.Add(font)) return;

            string subtype = (Resolve(doc, font[PdfName.Subtype]) as PdfName)?.Value;

            if (subtype == "Type0")
            {
                if (depth >= MaxDescendantFontDepth)
                {
                    diagnostic?.Invoke(
                        $"the /DescendantFonts chain is deeper than {MaxDescendantFontDepth} " +
                        "levels; the font is reported as not embedded");
                    onLeaf(font, subtype, null);
                    return;
                }

                var descendants = Resolve(doc, font[PdfName.DescendantFonts]) as PdfArray;
                if (descendants == null)
                {
                    // Reported, not skipped. A composite font whose descendants cannot be reached
                    // is one whose embedding cannot be confirmed, and "cannot confirm" must not
                    // read as "conforms".
                    onLeaf(font, subtype, null);
                    return;
                }

                for (int i = 0; i < descendants.Count; i++)
                {
                    if (Resolve(doc, descendants[i]) is PdfDictionary desc)
                        WalkDescriptors(desc, doc, onLeaf, diagnostic, visited, depth + 1);
                }
                return;
            }

            // §9.6.5: a Type 3 font's glyphs are content streams in /CharProcs. There is no font
            // program, so the embedding requirement does not apply — this is "not applicable",
            // never "not embedded".
            if (subtype == "Type3") return;

            // No /Subtype is NOT a reason to stop. A malformed font dictionary is exactly where an
            // unembedded program hides, and abandoning the check here is how one validator passed
            // a document the other failed.
            var fd = Resolve(doc, font[PdfName.FontDescriptor]) as PdfDictionary;
            onLeaf(font, subtype, fd);
        }

        /// <summary>
        /// <c>/BaseFont</c>, else <c>/Name</c>, else the caller's fallback, else a constant.
        /// </summary>
        private static string Label(PdfDocument doc, PdfDictionary font, string fallbackLabel)
            => (Resolve(doc, font[PdfName.BaseFont]) as PdfName)?.Value
            ?? (Resolve(doc, font[PdfName.Name]) as PdfName)?.Value
            ?? fallbackLabel
            ?? "(unnamed font)";

        /// <summary>
        /// Whether a font-file entry resolves to a stream: one naming an object the file does not
        /// hold, or one that will not parse, embeds nothing — the font is reported, which is the
        /// direction a conformance check must fail in when it cannot confirm the program.
        /// </summary>
        private static bool IsProgram(PdfDocument doc, PdfObject entry) => entry != null && Resolve(doc, entry) is PdfStream;

        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            try { return doc.ResolveObject(obj) ?? obj; }
            catch { return obj; }
        }
    }
}
