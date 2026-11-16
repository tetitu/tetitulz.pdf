// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Repairs the page geometry X05 asks for: every page carries a <c>/TrimBox</c> or an
    /// <c>/ArtBox</c>, and not both (ISO 15930-1 §6.2.4).
    /// </summary>
    /// <remarks>
    /// <para><b>The two halves are not equally safe, and are treated differently.</b> Adding a
    /// <c>/TrimBox</c> to a page that declares no box at all supplies a missing statement; deleting
    /// one of two boxes discards a statement the producer made. So synthesis is what
    /// <see cref="PdfXConverter"/> does by default and deletion is opt-in — the split
    /// <see cref="PdfXValidator"/> already anticipates by reporting the two cases as separate X05
    /// violations "because the remediation differs".</para>
    ///
    /// <para><b>The synthesised box asserts that the page has no bleed.</b> <c>/TrimBox</c> is the
    /// finished size after trimming; taking it from the <c>/CropBox</c> says the visible page *is*
    /// the finished page. That is right for the ordinary document with no bleed and wrong for
    /// artwork designed to run off the edge, and nothing in the file distinguishes them — a page
    /// with bleed and a page without look identical once the box is missing. The assumption is
    /// stated rather than hidden, and reported per page through
    /// <see cref="PdfXConversionResult.PagesGivenTrimBox"/>.</para>
    /// </remarks>
    internal static class PageBoxRepair
    {
        /// <summary>What <see cref="Repair"/> changed, and what it declined to.</summary>
        internal sealed class Repaired
        {
            /// <summary>Pages that had neither box and were given a <c>/TrimBox</c>.</summary>
            internal int TrimBoxesAdded;

            /// <summary>Pages that carried both boxes and had the <c>/ArtBox</c> removed.</summary>
            internal int ArtBoxesRemoved;

            /// <summary>
            /// Pages left as they were, each with the reason. A skipped page keeps its X05
            /// violation, so this never turns into a silent pass.
            /// </summary>
            internal readonly List<string> Skipped = new List<string>();
        }

        /// <param name="addTrimBoxWhenNeitherPresent">
        /// Give a page declaring no box at all a <c>/TrimBox</c> from its visible extent.
        /// </param>
        /// <param name="removeArtBoxWhenBothPresent">
        /// Delete the <c>/ArtBox</c> from a page carrying both boxes. <c>/TrimBox</c> is the one
        /// kept: it states the finished page size, which is what a print workflow imposes on, while
        /// <c>/ArtBox</c> describes the extent of meaningful content.
        /// </param>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the original bytes plus the appended revision.</param>
        internal static Repaired Repair(Stream input, Stream output,
            bool addTrimBoxWhenNeitherPresent, bool removeArtBoxWhenBothPresent)
        {
            var result = new Repaired();

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            var objects = new List<IndirectObject>();

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var page = doc.Pages[i];
                var pd = page?.Dictionary;
                if (pd == null) continue;

                // Presence is asked of the page *dictionary*, never of PdfPage.TrimBox /
                // PdfPage.ArtBox: those properties default to /CropBox per §14.11.2 and are
                // therefore never null, so a page with no box at all would read as having one and
                // this method would do nothing on precisely the documents it exists for.
                // PdfXValidator.CheckPageBoxes asks the same question of the same dictionary —
                // checker and fixer must agree on scope or a "repaired" page still fails.
                bool hasTrim = pd[PdfName.TrimBox] != null;
                bool hasArt  = pd[PdfName.ArtBox]  != null;

                if (hasTrim ^ hasArt) continue;          // exactly one — already conforming

                // A page dictionary is reached through /Kids and is therefore an indirect object;
                // the parser stamps its number on. Without one there is nothing to revise, and
                // inventing an object number would corrupt the file.
                int objNum = pd.ObjectNumber;
                if (objNum <= 0)
                {
                    result.Skipped.Add($"page {i + 1}: its object number could not be determined, " +
                                       "so the page dictionary could not be revised");
                    continue;
                }

                if (hasTrim && hasArt)
                {
                    if (!removeArtBoxWhenBothPresent)
                    {
                        result.Skipped.Add(
                            $"page {i + 1}: carries both /TrimBox and /ArtBox. Removing one " +
                            "discards a statement the producer made, so it is opt-in — set " +
                            nameof(PdfXConversionOptions) + "." +
                            nameof(PdfXConversionOptions.RemoveArtBoxWhenBothPresent) + ".");
                        continue;
                    }

                    var trimmed = Clone(pd);
                    trimmed.Remove(PdfName.ArtBox);
                    objects.Add(Revision(objNum, pd.GenerationNumber, trimmed));
                    result.ArtBoxesRemoved++;
                    continue;
                }

                // Neither box.
                if (!addTrimBoxWhenNeitherPresent)
                {
                    result.Skipped.Add($"page {i + 1}: has neither /TrimBox nor /ArtBox, and " +
                                       nameof(PdfXConversionOptions) + "." +
                                       nameof(PdfXConversionOptions.RepairPageBoxes) +
                                       " is off, so no box was derived for it.");
                    continue;
                }

                // PdfPage.CropBox already resolves an indirect value, falls back to the inherited
                // entry, and then to /MediaBox — all three of which a page may rely on, and none
                // of which a direct dictionary read would find.
                var box = CopyBox(page.CropBox);
                if (box == null)
                {
                    result.Skipped.Add($"page {i + 1}: has neither /TrimBox nor /ArtBox, and no " +
                                       "usable /CropBox or /MediaBox to derive one from");
                    continue;
                }

                var withTrim = Clone(pd);
                withTrim.Set(PdfName.TrimBox, box);
                objects.Add(Revision(objNum, pd.GenerationNumber, withTrim));
                result.TrimBoxesAdded++;
            }

            // Nothing to do means byte-identical output, not an empty revision appended to every
            // already-conforming document.
            if (objects.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return result;
            }

            int newSize = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, newSize);

            return result;
        }

        private static IndirectObject Revision(int objectNumber, int generation, PdfDictionary d)
        {
            d.ObjectNumber = objectNumber;
            d.GenerationNumber = generation;
            return new IndirectObject(objectNumber, generation, d);
        }

        /// <summary>
        /// A fresh array of literal numbers rather than the resolved source array: the source may
        /// itself be an indirect object, and storing that instance would either emit a reference
        /// this revision does not define or share a mutable instance with the box it was copied
        /// from. Returns <c>null</c> for anything that is not four numbers — a malformed box is
        /// refused rather than partly copied.
        /// </summary>
        private static PdfArray CopyBox(PdfArray src)
        {
            if (src == null || src.Count < 4) return null;

            var copy = new PdfArray();
            for (int i = 0; i < 4; i++)
            {
                if (!(src[i] is PdfNumber n)) return null;
                copy.Add(new PdfNumber(n.DoubleValue));
            }
            return copy;
        }

        /// <summary>
        /// Shallow-copies a page dictionary so the revision can add or drop a box without
        /// mutating the parsed original.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="PdfDictionary.CloneShallow"/>, the canonical implementation. A
        /// hand-written copy would differ from it in two ways that only bite later: a
        /// <c>NullReferenceException</c> rather than the documented <c>ArgumentNullException</c> on a
        /// null source, and a <c>PdfStream</c> (a stream *is* a dictionary here) rebuilt as a plain
        /// <c>PdfDictionary</c>, its data silently dropped. Kept as a named method because the two
        /// call sites read better with it.
        /// </remarks>
        private static PdfDictionary Clone(PdfDictionary src) => PdfDictionary.CloneShallow(src);
    }
}
