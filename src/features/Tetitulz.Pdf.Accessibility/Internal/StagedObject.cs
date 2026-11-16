// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Staging an existing indirect object for rewriting: a shallow copy that keeps the original's
    /// object number, so an incremental update supersedes it rather than adding a second one.
    ///
    /// <para><b>Two writers had byte-identical private copies of this</b> —
    /// <c>PdfAutoTagger.CloneDictionary</c> and <c>PdfUaXmpWriter.CloneDictionary</c>, differing
    /// only in a capacity hint — and both open-coded the copy loop instead of calling
    /// <see cref="PdfDictionary.CloneShallow"/>, which the rest of the tree was consolidated onto.
    /// Two other <c>CloneDictionary</c> helpers in the same assembly already forwarded to it, so the
    /// assembly held both halves of the same lesson at once.</para>
    ///
    /// <para><b>Going through <c>CloneShallow</c> is not only tidier — it is the one that handles a
    /// stream.</b> Both private copies constructed a plain <see cref="PdfDictionary"/> whatever the
    /// source was, so cloning a <see cref="PdfStream"/> through them would have produced a
    /// dictionary with the stream's keys and none of its data. Neither caller stages a stream today;
    /// that is the kind of "true when written" that this project has been bitten by before.</para>
    /// </summary>
    internal static class StagedObject
    {
        /// <summary>
        /// A shallow copy of <paramref name="source"/> carrying <paramref name="objectNumber"/> and
        /// <paramref name="generation"/>. An object number of 0 marks a direct object — the callers
        /// use it when they are minting a dictionary rather than superseding one.
        /// </summary>
        internal static PdfDictionary CloneWithIdentity(
            PdfDictionary source, int objectNumber, int generation)
        {
            PdfDictionary clone = PdfDictionary.CloneShallow(source);
            clone.ObjectNumber = objectNumber;
            clone.GenerationNumber = generation;
            return clone;
        }

        /// <summary>
        /// The object number <paramref name="dict"/> is stored under, or −1 for an object the file
        /// does not hold indirectly: <see cref="Base.Operations.PdfObjectIndex.GetObjectNumber"/>'s
        /// answer, which already takes the number the parser stamped on it when the cross-reference
        /// table knows that number. The number a staged copy must carry to supersede it; the
        /// auto-tagger and the page-content rewriter each had a copy of this.
        /// </summary>
        /// <remarks>
        /// <b>It does not repeat the index's own fallback</b> after the index has answered −1: the
        /// repetition could only ever answer −1 too, and no test could tell a branch handing out an
        /// unknown number from it.
        /// </remarks>
        internal static int ObjectNumberOf(Base.Operations.PdfObjectIndex index, PdfDictionary dict)
            => index.GetObjectNumber(dict);
    }
}
