// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>What kind of dictionary an <see cref="ActionOwner"/> is.</summary>
    internal enum ActionOwnerKind
    {
        /// <summary>The document catalog.</summary>
        Catalog,

        /// <summary>A page dictionary.</summary>
        Page,

        /// <summary>An annotation on a page's <c>/Annots</c> whose <c>/Subtype</c> is not <c>/Widget</c>.</summary>
        Annotation,

        /// <summary>A widget annotation on a page's <c>/Annots</c>.</summary>
        Widget,

        /// <summary>A node of the AcroForm field tree.</summary>
        Field,
    }

    /// <summary>One dictionary that may carry an <c>/A</c> or an <c>/AA</c> entry, and where it is.</summary>
    internal readonly struct ActionOwner
    {
        internal ActionOwner(ActionOwnerKind kind, PdfDictionary dictionary, int pageIndex)
        {
            Kind = kind;
            Dictionary = dictionary;
            PageIndex = pageIndex;
        }

        internal ActionOwnerKind Kind { get; }
        internal PdfDictionary Dictionary { get; }

        /// <summary>The zero-based page for a page, an annotation or a widget; <c>-1</c> otherwise.</summary>
        internal int PageIndex { get; }
    }

    /// <summary>The outcome of one <see cref="ActionOwnerWalk.Walk"/>.</summary>
    internal sealed class ActionOwnerWalkResult
    {
        internal ActionOwnerWalkResult(List<ActionOwner> owners, AcroFormFieldWalkResult fields)
        {
            Owners = owners;
            FieldWalk = fields;
        }

        /// <summary>The catalog, then each page followed by its annotations, then every field node.</summary>
        internal IReadOnlyList<ActionOwner> Owners { get; }

        /// <summary>
        /// The field walk the <see cref="ActionOwnerKind.Field"/> owners came from. When its budget
        /// or depth cap stopped it, fields past that point are missing, and the caller reports it in
        /// its own words.
        /// </summary>
        internal AcroFormFieldWalkResult FieldWalk { get; }
    }

    /// <summary>
    /// Every dictionary that the conformance rules on <c>/A</c> and <c>/AA</c> name: the catalog,
    /// each page, each annotation (a widget told apart from the rest), and each field node.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is not <c>PdfActionDispatcher</c>.</b> The dispatcher lists actions, and these
    /// rules are about keys. ISO 19005 (the profile tests <c>containsAA == false</c>, <c>containsA ==
    /// false</c>) and ISO 15930 forbid the entry itself, so an <c>/AA</c> that holds no action, or
    /// holds something that is not an action dictionary, breaks the rule and adds nothing to the
    /// dispatcher's list. The action <i>types</i> are the dispatcher's question, and the validators
    /// ask it there.</para>
    /// <para><b>One walk for both validators.</b> <see cref="PdfXValidator"/>'s X07 and
    /// <see cref="PdfAValidator"/>'s A04 and A23 each walked these owners themselves, and differently:
    /// PDF/A looked at the catalog and the pages only, and PDF/X at annotations and fields too.</para>
    /// <para>An annotation listed twice, on one page or two, is one owner, at its first page. A merged
    /// field and widget (§12.7.3.1) is one dictionary reached twice, as a
    /// <see cref="ActionOwnerKind.Widget"/> and as a <see cref="ActionOwnerKind.Field"/>, because the
    /// rules name the two roles separately. A caller that reports one defect per dictionary dedupes
    /// by <see cref="ActionOwner.Dictionary"/>.</para>
    /// <para>The field nodes are <see cref="AcroFormFieldWalk"/>'s, the one field walk in this
    /// assembly, and the caller's resolver goes to it so a failure lands in that caller's
    /// diagnostic slot.</para>
    /// </remarks>
    internal static class ActionOwnerWalk
    {
        internal static ActionOwnerWalkResult Walk(PdfDocument doc, AcroFormResolver resolve)
        {
            var owners = new List<ActionOwner>();
            var catalog = doc.Catalog?.Dictionary;
            if (catalog != null)
                owners.Add(new ActionOwner(ActionOwnerKind.Catalog, catalog, -1));

            var seen = new HashSet<PdfObject>(PdfReferenceEquality.Instance);
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var page = doc.Pages[p].Dictionary;
                owners.Add(new ActionOwner(ActionOwnerKind.Page, page, p));

                if (!(resolve(page[PdfName.Annots]) is PdfArray annots) || !ScanReuse.AdmitAnnotations(annots)) continue;
                for (int i = 0; i < annots.Count; i++)
                {
                    if (!(resolve(annots[i]) is PdfDictionary annot) || !seen.Add(annot)) continue;
                    bool widget = (resolve(annot[PdfName.Subtype]) as PdfName)?.Value == PdfAnnotationSubtypes.Widget;
                    owners.Add(new ActionOwner(widget ? ActionOwnerKind.Widget : ActionOwnerKind.Annotation, annot, p));
                }
            }

            var fields = AcroFormFieldWalk.Walk(catalog, resolve);
            foreach (var field in fields.Fields)
                if (field.Dictionary != null)
                    owners.Add(new ActionOwner(ActionOwnerKind.Field, field.Dictionary, -1));

            return new ActionOwnerWalkResult(owners, fields);
        }
    }
}
