// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>What a structure element's <c>/K</c> child dictionary is (§14.7.2, Tables 323–325).</summary>
    public enum StructKidKind
    {
        /// <summary>Another structure element — recurse into it.</summary>
        Element,

        /// <summary>A marked-content reference: names an MCID, optionally in another stream.</summary>
        MarkedContentReference,

        /// <summary>An object reference: names an annotation or XObject, never text.</summary>
        ObjectReference,

        /// <summary>
        /// None of the three: a dictionary carrying no <c>/S</c>, no <c>/MCID</c>, no <c>/Obj</c>
        /// and no <c>/Type</c> that names a kind. <b>Not a kid at all</b> — a caller must skip it.
        /// </summary>
        /// <remarks>
        /// Appended rather than made value 0 so no existing ordinal shifts. Answering
        /// <see cref="Element"/> for this case — which every other reader calls not-an-element — would make a bare
        /// <c>&lt;&lt; &gt;&gt;</c> in a <c>/K</c> array a phantom child of the structure tree, reported by
        /// <c>PdfStructureTreeReader</c> as an element of type <c>"OBJR"</c> (the fallback when the
        /// deduced kind is not MCR) and recursed into by the tagged-text extractor.
        /// </remarks>
        Unknown,
    }

    /// <summary>
    /// Decides what a <c>/K</c> child dictionary is.
    /// </summary>
    /// <remarks>
    /// <para><b>Classifying on <c>/Type</c> alone loses text.</b> Table 324 makes <c>/Type</c>
    /// <i>Optional</i> on a marked-content reference and <c>/MCID</c> <i>Required</i>, so
    /// <c>/K &lt;&lt; /MCID 0 &gt;&gt;</c> is a perfectly legal MCR. Both Base readers treated a
    /// typeless dictionary as a nested structure element:
    /// <c>PdfTaggedTextExtractor</c> recursed into it, found no <c>/K</c> of its own, emitted no
    /// block — and <b><c>ExtractText</c> returned the empty string</b> for a document whose content
    /// really is marked. <c>PdfStructureTreeReader</c> reported a phantom child whose
    /// <c>ElementType</c> was <c>"StructElem"</c> and whose <c>PageIndex</c> was the parent's.</para>
    ///
    /// <para><b>And <c>/Type</c> must be resolved.</b> It may be written indirectly (§7.3.10);
    /// <c>PdfDictionary.GetName</c> does not follow a reference, so one reader saw such an
    /// <c>/MCR</c> as typeless while the other resolved it — the two disagreeing about one tree.</para>
    ///
    /// <para><b>Order matters, and <c>/S</c> comes before <c>/MCID</c>.</b> A structure element may
    /// legitimately carry both (an element with an <c>/S</c> whose own <c>/K</c> is an integer is
    /// still an element), so testing <c>/MCID</c> first would reclassify real elements as leaves and
    /// silently prune their subtrees.</para>
    ///
    /// <para>A leaf by construction — no static fields, so no type initializer.</para>
    /// </remarks>
    public static class StructKidClassifier
    {
        /// <summary>
        /// Classifies <paramref name="kid"/>. <paramref name="doc"/> may be null, in which case an
        /// indirectly-written <c>/Type</c> cannot be followed and the content test decides.
        /// </summary>
        public static StructKidKind Classify(PdfDictionary kid, PdfDocument doc)
        {
            if (kid == null) return StructKidKind.Unknown;

            // /Type names the kind outright for the two leaf kinds — and resolved, because it may
            // be indirect.
            string type = ((doc?.ResolveObject(kid[PdfName.Type]) ?? kid[PdfName.Type]) as PdfName)?.Value;
            if (type == "MCR")  return StructKidKind.MarkedContentReference;
            if (type == "OBJR") return StructKidKind.ObjectReference;

            // Then CONTENT, and only then a bare /Type /StructElem. Table 323 makes /S *Required*
            // on a structure element, so what the dictionary carries outranks what it calls itself:
            // << /Type /StructElem /Obj 12 0 R >> is an object reference whose /Type is wrong, not
            // an element with no /S. Taking /Type /StructElem first — which this did — made that
            // dictionary an Element here and an ObjectReference in the Conformance plugin's twin,
            // on the same file.
            //
            // /S before /MCID: an element may legitimately hold both, and it is still an element.
            if (kid[PdfName.S] != null) return StructKidKind.Element;
            if (kid[PdfName.MCID] != null) return StructKidKind.MarkedContentReference;
            if (kid[PdfName.Obj] != null) return StructKidKind.ObjectReference;

            // A dictionary declaring itself an element and carrying nothing is taken at its word.
            // Anything else is not a kid: see StructKidKind.Unknown for what answering Element here
            // cost. This ladder was PdfUaChecker.ClassifyStructKid's, step for step — the copy that
            // had thought hardest about it — and that method now delegates here, so there is one.
            return type == "StructElem" ? StructKidKind.Element : StructKidKind.Unknown;
        }
    }
}
