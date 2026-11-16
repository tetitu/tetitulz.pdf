// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The annotation repairs <see cref="PdfAConverter"/> makes before it validates: what PDF/A's
    /// annotation rules (A38, A39) ask for that can be supplied without changing what a page shows on
    /// screen. An annotation with no <c>/F</c>, or with one that neither prints nor hides it, gets the
    /// Print flag; an <c>/AP</c> holding a rollover or down appearance keeps only <c>/N</c>.
    /// </summary>
    /// <remarks>
    /// What it leaves is still the validator's to report and the converter's to block on: an
    /// annotation flagged Hidden, Invisible, NoView or ToggleNoView (making it print would show what
    /// its author hid), a missing appearance (drawing one is a rendering decision), and a type the
    /// part does not permit.
    /// </remarks>
    internal static class PdfAAnnotationRepair
    {
        /// <summary>What one run changed.</summary>
        internal sealed class Outcome
        {
            internal int PrintFlagsSet;
            internal int AppearancesTrimmed;
            internal int Unrevisable;

            /// <summary>/Annots arrays read, each once: the evidence the shared-array skip is live, the answer being the same without it.</summary>
            internal int ArraysRead;
        }

        /// <summary>Copies <paramref name="input"/> to <paramref name="output"/>, with the repairs appended as one revision.</summary>
        internal static Outcome Repair(byte[] input, Stream output)
        {
            var outcome = new Outcome();
            using var doc = PdfDocument.Open(new MemoryStream(input));
            if (doc.IsEncrypted || doc.Pages == null)
            {
                output.Write(input, 0, input.Length);
                return outcome;
            }

            var updates = new Dictionary<int, IndirectObject>();
            // An array pages share, and an annotation listed twice, are revised once: read again on
            // every page they were pages × entries, and each repair counted again.
            var seenArrays = new HashSet<object>(PdfReferenceEquality.Instance);
            var seenAnnotations = new HashSet<object>(PdfReferenceEquality.Instance);
            foreach (var page in doc.Pages)
            {
                var pageDict = page.Dictionary;
                if (pageDict == null) continue;
                var annotsRaw = pageDict[PdfName.Annots];
                if (!(doc.ResolveObject(annotsRaw) is PdfArray annots) || !seenArrays.Add(annots)) continue;
                outcome.ArraysRead++;

                PdfArray revisedArray = null;
                for (int i = 0; i < annots.Count; i++)
                {
                    if (!(doc.ResolveObject(annots[i]) is PdfDictionary annot) || !seenAnnotations.Add(annot)) continue;
                    var revised = Revise(doc, annot, outcome);
                    if (revised == null) continue;

                    if (annot.ObjectNumber > 0)
                    {
                        updates[annot.ObjectNumber] = new IndirectObject(annot.ObjectNumber, annot.GenerationNumber, revised);
                        continue;
                    }

                    // A direct annotation lives inside the /Annots array: the array, or the page
                    // holding it, is what is superseded.
                    if (revisedArray == null)
                    {
                        revisedArray = new PdfArray();
                        for (int k = 0; k < annots.Count; k++) revisedArray.Add(annots[k]);
                    }
                    revisedArray[i] = revised;
                }

                if (revisedArray == null) continue;
                if (annots.ObjectNumber > 0)
                {
                    updates[annots.ObjectNumber] = new IndirectObject(annots.ObjectNumber, annots.GenerationNumber, revisedArray);
                }
                else if (pageDict.ObjectNumber > 0)
                {
                    var revisedPage = PdfDictionary.CloneShallow(pageDict);
                    revisedPage[PdfName.Annots] = revisedArray;
                    updates[pageDict.ObjectNumber] = new IndirectObject(pageDict.ObjectNumber, pageDict.GenerationNumber, revisedPage);
                }
                else
                {
                    outcome.Unrevisable++;
                }
            }

            RepairRevision.Write(input, output, doc, updates);
            return outcome;
        }

        /// <summary>The repaired annotation dictionary, or <see langword="null"/> when nothing changes.</summary>
        private static PdfDictionary Revise(PdfDocument doc, PdfDictionary annot, Outcome outcome)
        {
            PdfDictionary revised = null;

            var flags = PdfAnnotationFlags.FromNumber(doc.ResolveObject(annot[PdfName.F]));
            const int hiding = PdfAnnotationFlags.Invisible | PdfAnnotationFlags.Hidden
                               | PdfAnnotationFlags.NoView | PdfAnnotationFlags.ToggleNoView;
            if (flags == null || ((flags.Value & PdfAnnotationFlags.Print) == 0 && (flags.Value & hiding) == 0))
            {
                revised = PdfDictionary.CloneShallow(annot);
                revised[PdfName.F] = new PdfNumber((flags ?? 0) | PdfAnnotationFlags.Print);
                outcome.PrintFlagsSet++;
            }

            if (doc.ResolveObject(annot[PdfName.AP]) is PdfDictionary ap && ap[PdfName.N] != null && ap.Count > 1)
            {
                revised ??= PdfDictionary.CloneShallow(annot);
                var normalOnly = new PdfDictionary();
                normalOnly[PdfName.N] = ap[PdfName.N];
                revised[PdfName.AP] = normalOnly;
                outcome.AppearancesTrimmed++;
            }
            return revised;
        }
    }
}
